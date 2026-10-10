using SevenWondersDuel.Engine.Data;

namespace SevenWondersDuel.Engine.Tests;

/// <summary>Taking cards in Age I: accessibility, Reveals of flipped cards, build and discard (rules-reference §2).</summary>
public class AgeOneTests
{
    const string Opening =
        "reveal age1 0.0 palisade 0.1 tavern 2.0 guard-tower 2.1 altar 2.2 lumber-yard 2.3 pharmacist "
        + "4.0 apothecary 4.1 baths 4.2 logging-camp 4.3 quarry 4.4 glassworks 4.5 stone-reserve";

    static int C(string slug) => GameData.CardIndex(slug);

    static Game Play(params string[] lines) =>
        MoveLog.Parse(string.Join('\n', ["7wd-log v1", "rules base start-choice=new", "reveal first P1", Opening, .. lines]));

    [Fact]
    public void A_new_game_waits_for_the_first_player_then_the_face_up_cards()
    {
        var game = new Game(Ruleset.Default);
        Assert.Equal(StepKind.Reveal, game.State.Step);
        game.Apply(new FirstPlayerReveal(1));
        Assert.Equal(StepKind.Reveal, game.State.Step);
        Assert.Equal(12, System.Numerics.BitOperations.PopCount(game.State.SlotsToReveal));
        Assert.Empty(game.State.LegalMoves());
    }

    [Fact]
    public void The_first_player_starts_age_one_with_the_six_bottom_cards_accessible()
    {
        var s = Play().State;
        Assert.Equal(StepKind.Decision, s.Step);
        Assert.Equal(0, s.PlayerToDecide);
        Assert.Equal(new[] { 14, 15, 16, 17, 18, 19 }, Enumerable.Range(0, 20).Where(s.IsAccessible));
        Assert.Equal(7, s.Coins(0));
        Assert.Equal(7, s.Coins(1));
        // Every accessible card can be discarded, and all six are affordable with 7 coins.
        Assert.Equal(12, s.LegalMoves().Count);
    }

    [Fact]
    public void A_card_flips_only_once_both_cards_covering_it_are_gone_and_before_the_next_move()
    {
        var game = Play("P1 build apothecary");
        Assert.Equal(StepKind.Decision, game.State.Step); // 3.0 is still covered by 4.1

        game.Apply(Move.Discard(C("baths")));
        Assert.Equal(StepKind.Reveal, game.State.Step);
        Assert.Equal(1u << 9, game.State.SlotsToReveal);
        Assert.Throws<InvalidOperationException>(() => game.Apply(Move.Discard(C("quarry"))));

        game.Apply(MoveLog.ParseEntry("reveal 3.0 workshop", game.State));
        Assert.Equal(StepKind.Decision, game.State.Step);
        Assert.Equal(0, game.State.PlayerToDecide);
        Assert.True(game.State.IsAccessible(9));
    }

    [Fact]
    public void Covered_cards_cannot_be_taken()
    {
        var s = Play().State;
        Assert.False(s.IsLegal(Move.Discard(C("altar"))));
        Assert.Throws<InvalidOperationException>(() => s.Apply(Move.Build(C("palisade"))));
    }

    [Fact]
    public void Building_pays_the_cost_and_needs_enough_coins()
    {
        var s = Play("P1 build baths").State; // 1 Stone bought for 2
        Assert.Equal(5, s.Coins(0));
        Assert.True(s.City(0).Contains(C("baths")));

        s.SetCoins(1, 0);
        Assert.False(s.IsLegal(Move.Build(C("logging-camp")))); // $1
        Assert.False(s.IsLegal(Move.Build(C("apothecary"))));   // 1 Glass
        Assert.True(s.IsLegal(Move.Build(C("quarry"))));        // free
        Assert.True(s.IsLegal(Move.Discard(C("logging-camp"))));
        s.SetCoins(1, 1);
        Assert.True(s.IsLegal(Move.Build(C("logging-camp"))));
    }

    [Fact]
    public void Trading_price_follows_the_opponents_production()
    {
        // P1 builds the Quarry, so Stone costs P2 3 coins for the Baths.
        var s = Play("P1 build quarry", "P2 build baths").State;
        Assert.Equal(4, s.Coins(1));
    }

    [Fact]
    public void Discard_gives_two_plus_yellow_cards_already_in_the_city()
    {
        var s = Play("P1 discard stone-reserve").State; // the discarded yellow card itself does not count
        Assert.Equal(9, s.Coins(0));
        Assert.True(s.DiscardPile.Contains(C("stone-reserve")));

        s = Play("P1 build stone-reserve", "P2 discard quarry", "P1 discard glassworks").State;
        Assert.Equal(7 - 3 + 3, s.Coins(0));
    }

    [Fact]
    public void Tavern_gives_four_coins()
    {
        var game = MoveLog.Parse("7wd-log v1\nrules base\nreveal first P2\n"
            + "reveal age1 0.0 palisade 0.1 altar 2.0 guard-tower 2.1 baths 2.2 lumber-yard 2.3 pharmacist "
            + "4.0 apothecary 4.1 tavern 4.2 logging-camp 4.3 quarry 4.4 glassworks 4.5 stone-reserve\n"
            + "P2 build tavern\n");
        Assert.Equal(11, game.State.Coins(1));
        Assert.Equal(0, game.State.PlayerToDecide);
    }

    [Theory]
    [InlineData("reveal 3.0 baths", "not an unseen")]           // already on the table
    [InlineData("reveal 3.0 sawmill", "not an unseen")]         // an Age II card
    [InlineData("reveal 3.1 workshop", "not waiting")]          // still covered
    [InlineData("reveal age1 3.0 workshop", "before any card")] // only the first Reveal of an Age is its opening
    public void Bad_reveals_are_rejected(string reveal, string reason)
    {
        var e = Assert.Throws<FormatException>(() => Play("P1 build apothecary", "P2 discard baths", reveal));
        Assert.Contains(reason, e.Message);
        Assert.Contains("line 7", e.Message);
    }

    [Fact]
    public void A_move_by_the_wrong_player_is_rejected()
    {
        var e = Assert.Throws<FormatException>(() => Play("P2 build quarry"));
        Assert.Contains("P1's decision", e.Message);
    }

    [Fact]
    public void Turns_alternate_and_age_two_starts_when_age_one_is_empty()
    {
        var game = Play();
        var chance = new RandomChanceSource(1);
        var turns = 0;
        while (game.State.Age == 1)
        {
            if (game.State.Step == StepKind.Reveal)
            {
                game.Apply(chance.Next(game.State));
                continue;
            }
            Assert.Equal(turns % 2, game.State.PlayerToDecide);
            game.Apply(game.State.LegalMoves().Last()); // always a discard
            turns++;
        }
        Assert.Equal(20, turns);
        Assert.Equal(2, game.State.Age);
        Assert.Equal(20, game.State.CardsLeftInAge);
        Assert.Equal(20, game.State.DiscardPile.Count);
    }
}
