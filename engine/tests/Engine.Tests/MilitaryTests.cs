using SevenWondersDuel.Engine.Data;

namespace SevenWondersDuel.Engine.Tests;

/// <summary>
/// Military (rules-reference §8, §10, §12 mistakes 11, 12 and 14): shields move the Conflict Pawn, military tokens
/// loot coins once (never below zero), and reaching a capital ends the game at once with a military victory.
/// </summary>
public class MilitaryTests
{
    static readonly int P1Small = GameState.MilitaryToken(0, false);
    static readonly int P1Large = GameState.MilitaryToken(0, true);
    static readonly int P2Small = GameState.MilitaryToken(1, false);
    static readonly int P2Large = GameState.MilitaryToken(1, true);

    static GameState Fresh(int pawn = 0)
    {
        var s = GameState.New(Ruleset.Default);
        s.SetConflictPawn(pawn);
        return s;
    }

    /// <summary>
    /// A real game, discarding until the player to decide can build a red card; returns that game and card.
    /// P1 plays first.
    /// </summary>
    static (Game Game, int Card) RedCardBuildable(ulong seed = 1)
    {
        var game = new Game(Ruleset.Default);
        game.Apply(new FirstPlayerReveal(0));
        var chance = new RandomChanceSource(seed);
        while (true)
        {
            var s = game.State;
            if (s.Step == StepKind.Reveal) { game.Apply(chance.Next(s)); continue; }
            if (s.Pending(0).Kind == DecisionKind.ChooseStarter) { game.Apply(Move.StartMe); continue; }
            if (s.Pending(0).Kind == DecisionKind.DraftWonder) { game.Apply(s.LegalMoves()[0]); continue; }
            var red = s.LegalMoves().Where(m => m.Kind == MoveKind.Build && GameData.Cards[m.Card].Color == CardColor.Red).ToList();
            if (red.Count > 0) return (game, red[0].Card);
            game.Apply(s.LegalMoves().Last(m => m.Kind == MoveKind.Discard));
        }
    }

    // ---------------------------------------------------------------- the pawn

    [Fact]
    public void The_game_starts_with_the_pawn_at_the_centre_and_all_four_tokens()
    {
        var s = GameState.New(Ruleset.Default);
        Assert.Equal(0, s.ConflictPawn);
        Assert.Equal(4, s.MilitaryTokensLeft);
        Assert.All(Enumerable.Range(0, 4), t => Assert.True(s.MilitaryTokenLeft(t)));
        Assert.Equal(new[] { 2, 5, 2, 5 }, Enumerable.Range(0, 4).Select(GameState.MilitaryTokenCoins));
    }

    [Fact]
    public void Each_shield_moves_the_pawn_one_space_toward_the_opponents_capital()
    {
        var s = Fresh();
        s.AddShields(0, 2);
        Assert.Equal(2, s.ConflictPawn);
        Assert.Equal(2, s.MilitaryLead(0));
        Assert.Equal(-2, s.MilitaryLead(1));
        s.AddShields(1, 3);
        Assert.Equal(-1, s.ConflictPawn);
        Assert.Equal(1, s.MilitaryLead(1));
    }

    [Fact]
    public void Building_a_red_card_moves_the_pawn_by_its_shields()
    {
        var (game, card) = RedCardBuildable();
        var player = game.State.PlayerToDecide;
        var shields = GameData.Cards[card].Shields;
        Assert.True(shields > 0);
        game.Apply(Move.Build(card));
        Assert.Equal(shields, game.State.MilitaryLead(player));
        Assert.False(game.State.IsOver);
    }

    // ---------------------------------------------------------------- military tokens

    [Fact]
    public void Entering_the_three_space_zone_takes_two_coins_from_the_opponent_once()
    {
        var s = Fresh(2);
        s.AddShields(0, 1);
        Assert.Equal(3, s.ConflictPawn);
        Assert.Equal(5, s.Coins(1));
        Assert.Equal(7, s.Coins(0));
        Assert.False(s.MilitaryTokenLeft(P2Small));
        Assert.Equal(3, s.MilitaryTokensLeft);

        // Retreat and advance again: the token is gone, so nothing more is lost.
        s.AddShields(1, 2);
        s.AddShields(0, 2);
        Assert.Equal(3, s.ConflictPawn);
        Assert.Equal(5, s.Coins(1));
    }

    [Fact]
    public void Entering_the_six_space_zone_takes_five_coins()
    {
        var s = Fresh(5);
        s.AddShields(0, 1);
        Assert.Equal(2, s.Coins(1));
        Assert.False(s.MilitaryTokenLeft(P2Large));
        Assert.True(s.MilitaryTokenLeft(P2Small)); // set up past it without looting
    }

    [Fact]
    public void P2_attacking_loots_the_tokens_on_p1s_side()
    {
        var s = Fresh();
        s.AddShields(1, 6);
        Assert.Equal(-6, s.ConflictPawn);
        Assert.Equal(0, s.Coins(0)); // 7 - 2 - 5
        Assert.Equal(7, s.Coins(1));
        Assert.False(s.MilitaryTokenLeft(P1Small));
        Assert.False(s.MilitaryTokenLeft(P1Large));
        Assert.True(s.MilitaryTokenLeft(P2Small));
        Assert.True(s.MilitaryTokenLeft(P2Large));
    }

    [Fact]
    public void One_large_move_through_both_zones_triggers_both_tokens()
    {
        var s = Fresh(2);
        s.SetCoins(1, 10);
        s.AddShields(0, 4);
        Assert.Equal(6, s.ConflictPawn);
        Assert.Equal(3, s.Coins(1));
        Assert.Equal(2, s.MilitaryTokensLeft);
    }

    [Theory]
    [InlineData(2, 1, 0)]  // small token, 1 coin: loses all
    [InlineData(2, 0, 0)]  // nothing to lose
    [InlineData(2, 2, 0)]
    [InlineData(5, 4, 0)]  // large token, 4 coins
    [InlineData(5, 6, 1)]
    public void Coin_loss_never_goes_below_zero(int pawnBefore, int coins, int coinsAfter)
    {
        var s = Fresh(pawnBefore);
        s.SetCoins(1, coins);
        s.AddShields(0, 1);
        Assert.Equal(coinsAfter, s.Coins(1));
        Assert.Equal(3, s.MilitaryTokensLeft); // the token goes to the box even when little was lost
        Assert.Equal(7, s.Coins(0)); // the attacker gains nothing
    }

    [Fact]
    public void Pushing_the_pawn_back_toward_the_centre_never_loots()
    {
        var s = Fresh(7);
        s.AddShields(1, 4); // P2 pushes from 7 to 3: still on P2's side, nobody loses coins
        Assert.Equal(3, s.ConflictPawn);
        Assert.Equal(7, s.Coins(0));
        Assert.Equal(7, s.Coins(1));
        Assert.Equal(4, s.MilitaryTokensLeft);
    }

    [Fact]
    public void Military_points_count_where_the_pawn_ends_even_after_the_token_is_gone()
    {
        var s = Fresh(2);
        s.AddShields(0, 2);
        Assert.False(s.MilitaryTokenLeft(P2Small));
        Assert.Equal(5, Scoring.Score(s, 0)[ScoreCategory.Military]);
        Assert.Equal(0, Scoring.Score(s, 1)[ScoreCategory.Military]);
    }

    // ---------------------------------------------------------------- military supremacy

    [Theory]
    [InlineData(0, 9)]
    [InlineData(1, -9)]
    public void Reaching_the_capital_is_a_military_victory(int player, int pawn)
    {
        var s = Fresh(pawn - Math.Sign(pawn)); // one space short of the capital
        Assert.Equal(8, s.MilitaryLead(player));
        s.AddShields(player, 1);
        Assert.Equal(pawn, s.ConflictPawn);
        Assert.True(s.IsOver);
        Assert.Equal(player, s.Result!.Winner);
        Assert.Equal(VictoryType.Military, s.Result.Victory);
        Assert.False(s.Result.IsDraw);
    }

    [Fact]
    public void Extra_shields_beyond_the_capital_are_lost()
    {
        var s = Fresh(7);
        s.AddShields(0, 3);
        Assert.Equal(9, s.ConflictPawn);
        Assert.True(s.IsOver);
    }

    [Fact]
    public void A_military_victory_ends_the_game_in_the_middle_of_an_age()
    {
        var (game, card) = RedCardBuildable(3);
        var s = game.State;
        var player = s.PlayerToDecide;
        s.SetConflictPawn(player == 0 ? 8 : -8);
        var cardsLeft = s.CardsLeftInAge;

        game.Apply(Move.Build(card));
        Assert.Equal(StepKind.GameOver, s.Step);
        Assert.True(s.IsOver);
        Assert.Equal(player, s.Result!.Winner);
        Assert.Equal(VictoryType.Military, s.Result.Victory);
        Assert.Equal(cardsLeft - 1, s.CardsLeftInAge);
        Assert.Equal(0, s.PendingCount);
        Assert.Empty(s.LegalMoves());
        Assert.Throws<InvalidOperationException>(() => s.Apply(Move.StartMe));
        // Scores are still filled, for reporting: the winner's military shows 10.
        Assert.Equal(10, s.Result.Score(player)[ScoreCategory.Military]);
        Assert.Contains("wins (military)", s.Result.ToString());
    }

    [Fact]
    public void A_copy_carries_the_pawn_and_the_tokens()
    {
        var s = Fresh();
        s.AddShields(0, 3);
        var copy = s.Clone();
        Assert.True(copy.SameAs(s));
        copy.AddShields(0, 3);
        Assert.False(copy.SameAs(s));
        Assert.Equal(3, s.MilitaryTokensLeft);
        Assert.Equal(2, copy.MilitaryTokensLeft);
    }

    [Fact]
    public void The_state_printer_shows_the_pawn_and_the_tokens_left()
    {
        var (game, _) = RedCardBuildable();
        game.State.AddShields(1, 4);
        var text = game.State.ToString();
        Assert.Contains("Conflict Pawn: 4 toward P1", text);
        Assert.Contains("military tokens left: P1:5 P2:2 P2:5", text);
    }
}
