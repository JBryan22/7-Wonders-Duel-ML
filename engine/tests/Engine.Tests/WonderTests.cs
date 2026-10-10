using System.Numerics;
using SevenWondersDuel.Engine.Data;

namespace SevenWondersDuel.Engine.Tests;

/// <summary>
/// The Wonder draft (A-B-B-A, rules-reference §1 and mistake 19), building Wonders with an Age card (§2, §7),
/// play-again turns (mistake 8), the 7-Wonder limit (mistake 7) and the Wonders that ask for no choice.
/// </summary>
public class WonderTests
{
    const string Tokens = "reveal tokens agriculture economy law strategy urbanism";
    const string Opening =
        "reveal age1 0.0 palisade 0.1 tavern 2.0 guard-tower 2.1 altar 2.2 lumber-yard 2.3 pharmacist "
        + "4.0 apothecary 4.1 baths 4.2 logging-camp 4.3 quarry 4.4 glassworks 4.5 stone-reserve";

    static int C(string slug) => GameData.CardIndex(slug);
    static int W(string slug) => GameData.WonderIndex(slug);

    static string[] DraftFor(params string[] p1) => Setup.DraftFor(p1);

    /// <summary>A game with P1 first, the given draft and the Age I opening, then <paramref name="lines"/>. Both players get 50 coins.</summary>
    static Game Start(string[] draft, params string[] lines)
    {
        var game = MoveLog.Parse(string.Join('\n',
            ["7wd-log v1", "rules base", "reveal first P1", Tokens, .. draft, Opening]));
        game.State.SetCoins(0, 50);
        game.State.SetCoins(1, 50);
        Do(game, lines);
        return game;
    }

    static void Do(Game game, params string[] lines)
    {
        foreach (var line in lines) game.Apply(MoveLog.ParseEntry(line, game.State));
    }

    static Game Drafting(int first)
    {
        var game = new Game(Ruleset.Default);
        game.Apply(new FirstPlayerReveal(first));
        Do(game, Tokens);
        return game;
    }

    static int WonderVp(GameState s, int player) => Scoring.Score(s, player)[ScoreCategory.Wonders];

    // ---------------------------------------------------------------- the draft

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Draft_picks_go_a_b_b_a_with_the_second_player_opening_round_two(int first)
    {
        var game = Drafting(first);
        var second = 1 - first;
        int[] expected = [first, second, second, first, second, first, first, second];
        Assert.Equal(expected, Enumerable.Range(0, 8).Select(game.State.DraftPicker));

        var pickers = new List<int>();
        foreach (var line in Setup.Draft(first))
        {
            var entry = MoveLog.ParseEntry(line, game.State);
            if (entry is MoveEntry m)
            {
                Assert.Equal(DecisionKind.DraftWonder, game.State.Pending(0).Kind);
                Assert.Equal(m.Player, game.State.PlayerToDecide);
                pickers.Add(m.Player);
            }
            else Assert.True(game.State.AwaitingDraftOffer);
            game.Apply(entry);
        }
        Assert.Equal(expected, pickers);

        var s = game.State;
        Assert.Equal(1, s.Age);
        Assert.Equal(0, s.DraftOffer);
        Assert.Equal(8, s.DraftPicks);
        string[] firsts = ["pyramids", "great-lighthouse", "temple-of-artemis", "statue-of-zeus"];
        Assert.Equal(firsts.Select(W).Order(), GameState.WondersIn(s.Wonders(first)));
        Assert.Equal(4, BitOperations.PopCount(s.Wonders(second)));
        Assert.Equal(0, s.Wonders(0) & s.Wonders(1));
        // The draft's first player starts Age I.
        game.Apply(MoveLog.ParseEntry(Opening, s));
        Assert.Equal(first, s.PlayerToDecide);
        Assert.Equal(DecisionKind.TakeCard, s.Pending(0).Kind);
    }

    [Fact]
    public void The_draft_comes_after_the_progress_tokens_and_age_one_after_the_draft()
    {
        var game = new Game(Ruleset.Default);
        game.Apply(new FirstPlayerReveal(0));
        Assert.Throws<InvalidOperationException>(() => game.State.Apply(new DraftReveal([0, 1, 2, 3])));
        Do(game, Tokens);
        Assert.True(game.State.AwaitingDraftOffer);
        Assert.Equal(StepKind.Reveal, game.State.Step);
        Assert.Equal(0, game.State.Age);
        Assert.Equal(0u, game.State.SlotsToReveal);
    }

    [Fact]
    public void Each_pick_offers_what_is_left_and_the_last_wonder_of_a_round_is_a_one_option_move()
    {
        var game = Drafting(0);
        var lines = Setup.Draft(0);
        var options = new List<int>();
        foreach (var line in lines)
        {
            var entry = MoveLog.ParseEntry(line, game.State);
            if (entry is MoveEntry)
            {
                var moves = game.State.LegalMoves();
                Assert.All(moves, m => Assert.Equal(MoveKind.Draft, m.Kind));
                options.Add(moves.Count);
            }
            game.Apply(entry);
        }
        Assert.Equal(new[] { 4, 3, 2, 1, 4, 3, 2, 1 }, options);
    }

    [Theory]
    [InlineData("P2 draft pyramids", "P1's decision")]
    [InlineData("P1 draft sphinx", "not in the draft offer")]
    [InlineData("P1 build quarry", "which Wonder to draft")]
    public void Bad_draft_moves_are_rejected(string move, string reason)
    {
        var game = Drafting(0);
        Do(game, "reveal draft circus-maximus great-lighthouse piraeus pyramids");
        var e = Assert.ThrowsAny<Exception>(() => Do(game, move));
        Assert.Contains(reason, e.Message);
    }

    [Theory]
    [InlineData("reveal draft sphinx colossus appian-way pyramids", "already offered")]
    [InlineData("reveal draft sphinx colossus appian-way messe", "not in this game's Wonder pool")]
    [InlineData("reveal draft sphinx colossus appian-way", "4 different")]
    [InlineData("reveal draft sphinx colossus appian-way sphinx", "4 different")]
    public void Bad_draft_offers_are_rejected(string reveal, string reason)
    {
        var game = Drafting(0);
        Do(game, Setup.Draft(0).Take(5).ToArray());
        Assert.True(game.State.AwaitingDraftOffer);
        var e = Assert.Throws<InvalidOperationException>(() => Do(game, reveal));
        Assert.Contains(reason, e.Message);
    }

    [Fact]
    public void The_chance_source_offers_unseen_base_wonders_and_leaves_four_unused()
    {
        for (ulong seed = 0; seed < 50; seed++)
        {
            var game = Drafting((int)(seed % 2));
            var chance = new RandomChanceSource(seed);
            var offered = new List<int>();
            while (game.State.Age == 0)
            {
                if (game.State.Step == StepKind.Reveal)
                {
                    var reveal = Assert.IsType<DraftReveal>(chance.Next(game.State));
                    Assert.All(reveal.Wonders, w => Assert.False(GameData.Wonders[w].IsPromo));
                    offered.AddRange(reveal.Wonders);
                    game.Apply(reveal);
                }
                else game.Apply(game.State.LegalMoves()[0]);
            }
            Assert.Equal(8, offered.Distinct().Count());
            Assert.Equal(4, BitOperations.PopCount(game.State.UnseenWonders));
            Assert.Equal(game.State.Wonders(0) | game.State.Wonders(1), offered.Aggregate(0, (m, w) => m | 1 << w));
        }
    }

    // ---------------------------------------------------------------- building a Wonder

    [Fact]
    public void Building_a_wonder_pays_its_cost_tucks_the_card_under_it_and_scores_its_vp()
    {
        var game = Start(Setup.Draft(0));
        var s = game.State;
        var cost = Payment.QuoteWonder(s, 0, W("pyramids")).Total;
        Assert.Equal(8, cost); // P S S S, all bought for 2
        Do(game, "P1 wonder apothecary pyramids");

        Assert.Equal(50 - cost, s.Coins(0));
        Assert.True(s.IsWonderBuilt(W("pyramids")));
        Assert.Equal(C("apothecary"), s.WonderCard(W("pyramids")));
        // The Age card is not in the city and never reaches the discard pile (rules-reference mistake 6).
        Assert.False(s.City(0).Contains(C("apothecary")));
        Assert.False(s.DiscardPile.Contains(C("apothecary")));
        Assert.Equal(1, s.WondersBuilt(0));
        Assert.Equal(0, s.WondersBuilt(1));
        Assert.Equal(9, WonderVp(s, 0));
        Assert.Equal(9 + 42 / 3, Scoring.Score(s, 0).Total);
        // The Pyramids do not play again.
        Assert.Equal(1, s.PlayerToDecide);
        Assert.False(s.PlayAgain);
        Assert.Contains("P1 wonder apothecary pyramids", game.WriteLog());
    }

    [Fact]
    public void Only_the_players_own_unbuilt_affordable_wonders_can_be_built()
    {
        var game = Start(Setup.Draft(0));
        var s = game.State;
        Assert.True(s.IsLegal(Move.BuildWonder(C("quarry"), W("pyramids"))));
        Assert.Contains("does not own", s.WhyIllegal(Move.BuildWonder(C("quarry"), W("colossus"))));
        Assert.Contains("does not own", s.WhyIllegal(Move.BuildWonder(C("quarry"), W("sphinx"))));
        Assert.Contains("covered", s.WhyIllegal(Move.BuildWonder(C("palisade"), W("pyramids"))));
        // 6 accessible cards: discard, build (all affordable with 50 coins) and P1's 4 Wonders each.
        Assert.Equal(6 * (2 + 4), s.LegalMoves().Count);

        Do(game, "P1 wonder quarry pyramids", "P2 discard baths");
        Assert.Contains("already built", s.WhyIllegal(Move.BuildWonder(C("apothecary"), W("pyramids"))));
        s.SetCoins(0, 7);
        Assert.Contains("costs", s.WhyIllegal(Move.BuildWonder(C("apothecary"), W("statue-of-zeus"))));
        Assert.DoesNotContain(s.LegalMoves(), m => m.Kind == MoveKind.Wonder && m.Wonder == W("statue-of-zeus"));
    }

    /// <summary>Each Wonder without a choice: VP, coins, the opponent's loss, shields and play again (rules-reference §7).</summary>
    [Theory]
    [InlineData("appian-way", 3, 3, 3, 0, true)]
    [InlineData("circus-maximus", 3, 0, 0, 1, false)]
    [InlineData("colossus", 3, 0, 0, 2, false)]
    [InlineData("great-lighthouse", 4, 0, 0, 0, false)]
    [InlineData("hanging-gardens", 3, 6, 0, 0, true)]
    [InlineData("piraeus", 2, 0, 0, 0, true)]
    [InlineData("pyramids", 9, 0, 0, 0, false)]
    [InlineData("sphinx", 6, 0, 0, 0, true)]
    [InlineData("statue-of-zeus", 3, 0, 0, 1, false)]
    [InlineData("temple-of-artemis", 0, 12, 0, 0, true)]
    public void Each_simple_wonder_has_its_printed_effect(string wonder, int vp, int coins, int opponentLoses, int shields, bool playAgain)
    {
        var game = Start(DraftFor(wonder));
        var s = game.State;
        var cost = Payment.QuoteWonder(s, 0, W(wonder)).Total;
        Do(game, $"P1 wonder quarry {wonder}");

        Assert.Equal(50 - cost + coins, s.Coins(0));
        Assert.Equal(50 - opponentLoses, s.Coins(1));
        Assert.Equal(shields, s.ConflictPawn);
        Assert.Equal(vp, WonderVp(s, 0));
        Assert.Equal(0, WonderVp(s, 1));
        Assert.Equal(playAgain ? 0 : 1, s.PlayerToDecide);
        Assert.Equal(DecisionKind.TakeCard, s.Pending(0).Kind);
    }

    [Fact]
    public void The_appian_way_takes_no_more_coins_than_the_opponent_has()
    {
        var game = Start(DraftFor("appian-way"));
        game.State.SetCoins(1, 2);
        Do(game, "P1 wonder quarry appian-way");
        Assert.Equal(0, game.State.Coins(1));
    }

    [Fact]
    public void The_great_lighthouse_produces_one_of_wood_clay_or_stone_without_changing_trading_prices()
    {
        var game = Start(DraftFor("great-lighthouse", "pyramids"), "P1 wonder apothecary great-lighthouse", "P2 discard logging-camp");
        var s = game.State;
        Assert.Equal(new PaymentQuote(0, 0), Payment.Quote(s, 0, C("baths"))); // S
        // One unit per turn: the Pyramids (P S S S) still buy the Papyrus and 2 Stone.
        Assert.Equal(new PaymentQuote(0, 3 * 2), Payment.QuoteWonder(s, 0, W("pyramids")));
        // Wonder production never raises the opponent's prices (rules-reference mistake 1).
        Assert.Equal(2, Payment.TradingPrice(s, 1, Resource.Stone));
        Assert.Equal(0, s.Production(0, Resource.Stone));
    }

    [Fact]
    public void Piraeus_produces_one_of_glass_or_papyrus()
    {
        var game = Start(DraftFor("piraeus"), "P1 wonder baths piraeus");
        var s = game.State;
        Assert.Equal(0, s.PlayerToDecide); // play again
        Assert.Equal(new PaymentQuote(0, 0), Payment.Quote(s, 0, C("apothecary"))); // G
        var coins = s.Coins(0);
        Do(game, "P1 build apothecary");
        Assert.Equal(coins, s.Coins(0));
        Assert.Equal(2, Payment.TradingPrice(s, 1, Resource.Glass));
    }

    // ---------------------------------------------------------------- tokens and Wonders

    [Fact]
    public void Architecture_lowers_the_price_of_a_built_wonder_and_economy_gets_the_trading_coins()
    {
        var game = Start(Setup.Draft(0));
        var s = game.State;
        s.TakeToken(0, ProgressTokens.Architecture);
        s.TakeToken(1, ProgressTokens.Economy);
        var p1 = s.Coins(0);
        var p2 = s.Coins(1);
        Do(game, "P1 wonder quarry pyramids");
        // P S S S with 2 resources waived: 2 bought for 2 each, paid to P2.
        Assert.Equal(p1 - 4, s.Coins(0));
        Assert.Equal(p2 + 4, s.Coins(1));
    }

    [Fact]
    public void Theology_gives_any_wonder_play_again()
    {
        var game = Start(Setup.Draft(0));
        game.State.TakeToken(0, ProgressTokens.Theology);
        Do(game, "P1 wonder quarry pyramids");
        Assert.Equal(0, game.State.PlayerToDecide);
    }

    [Fact]
    public void Strategy_adds_nothing_to_wonder_shields_and_wonder_shields_loot()
    {
        var game = Start(DraftFor("colossus"));
        var s = game.State;
        s.TakeToken(0, ProgressTokens.Strategy);
        s.SetConflictPawn(1);
        Do(game, "P1 wonder quarry colossus");
        Assert.Equal(3, s.ConflictPawn);
        Assert.Equal(48, s.Coins(1)); // the 2-coin token on P2's side
        Assert.False(s.MilitaryTokenLeft(GameState.MilitaryToken(1, false)));
    }

    [Fact]
    public void Military_supremacy_from_a_wonder_ends_the_game_before_play_again()
    {
        var game = Start(DraftFor("colossus"));
        var s = game.State;
        s.TakeToken(0, ProgressTokens.Theology);
        s.SetConflictPawn(7);
        Do(game, "P1 wonder quarry colossus");
        Assert.True(s.IsOver);
        Assert.Equal(VictoryType.Military, s.Result!.Victory);
        Assert.Equal(0, s.Result.Winner);
        Assert.Equal(0, s.PendingCount);
        Assert.False(s.PlayAgain);
        Assert.Equal(StepKind.GameOver, s.Step);
    }

    // ---------------------------------------------------------------- play again

    [Fact]
    public void Play_again_comes_after_the_newly_accessible_cards_are_revealed()
    {
        var game = Start(DraftFor("sphinx"), "P1 discard apothecary", "P2 discard quarry", "P1 wonder baths sphinx");
        var s = game.State;
        // Taking the Baths uncovers 3.0: its Reveal comes before P1's extra turn.
        Assert.Equal(StepKind.Reveal, s.Step);
        Assert.Equal(1u << 9, s.SlotsToReveal);
        Assert.Equal(DecisionKind.TakeCard, s.Pending(0).Kind); // P1's extra turn is queued behind the Reveal
        Assert.Equal(0, s.Pending(0).Player);
        Assert.Empty(s.LegalMoves());

        Do(game, "reveal 3.0 workshop");
        Assert.Equal(StepKind.Decision, s.Step);
        Assert.Equal(0, s.PlayerToDecide);
        Assert.False(s.PlayAgain);
        Assert.True(s.IsLegal(Move.Build(C("workshop"))));

        // Only one extra turn.
        Do(game, "P1 discard workshop");
        Assert.Equal(1, s.PlayerToDecide);
    }

    [Fact]
    public void Theology_does_not_give_a_play_again_wonder_a_second_extra_turn()
    {
        var game = Start(DraftFor("sphinx"));
        var s = game.State;
        s.TakeToken(0, ProgressTokens.Theology); // irrelevant: the Sphinx plays again anyway, and never twice
        Do(game, "P1 wonder quarry sphinx");
        Assert.Equal(0, s.PlayerToDecide);
        Do(game, "P1 discard glassworks");
        Assert.Equal(1, s.PlayerToDecide);
    }

    [Fact]
    public void Play_again_is_lost_at_the_end_of_an_age()
    {
        // P1 has the Sphinx, P2 the Hanging Gardens; with only discards, P2 takes the 20th card.
        var game = Start(Setup.Draft(0, ["sphinx", "pyramids", "colossus", "great-library"],
            ["hanging-gardens", "appian-way", "piraeus", "mausoleum"]));
        var s = game.State;
        var chance = new RandomChanceSource(3);
        while (s.CardsLeftInAge > 1 || s.Step == StepKind.Reveal)
            if (s.Step == StepKind.Reveal) game.Apply(chance.Next(s));
            else game.Apply(s.LegalMoves().Last(m => m.Kind == MoveKind.Discard));
        Assert.Equal(1, s.PlayerToDecide);
        var last = Enumerable.Range(0, GameState.SlotCount).Single(s.IsAccessible);
        s.SetCoins(1, 50);
        s.SetConflictPawn(1); // P2 is the weaker military and chooses who starts Age II
        game.Apply(Move.BuildWonder(s.SlotCard(last), W("hanging-gardens")));

        Assert.Equal(2, s.Age);
        Assert.False(s.PlayAgain);
        while (s.Step == StepKind.Reveal) game.Apply(chance.Next(s));
        Assert.Equal(DecisionKind.ChooseStarter, s.Pending(0).Kind);
        Assert.Equal(1, s.PlayerToDecide);
        Do(game, "P2 start opponent");
        Assert.Equal(0, s.PlayerToDecide);
        game.Apply(s.LegalMoves().Last(m => m.Kind == MoveKind.Discard));
        Assert.Equal(1, s.PlayerToDecide);
    }

    // ---------------------------------------------------------------- 7 Wonders, not one more

    [Fact]
    public void Once_seven_wonders_are_built_the_eighth_leaves_the_game()
    {
        var game = Start(Setup.Draft(0));
        var s = game.State;
        var chance = new RandomChanceSource(5);
        while (s.TotalWondersBuilt < 6)
        {
            if (s.Step == StepKind.Reveal) { game.Apply(chance.Next(s)); continue; }
            s.SetCoins(s.PlayerToDecide, 100);
            var moves = s.LegalMoves();
            game.Apply(moves.FirstOrDefault(m => m.Kind == MoveKind.Wonder, moves.Last(m => m.Kind == MoveKind.Discard)));
        }
        Assert.Equal(1, s.Age);
        Assert.Equal(0, s.RemovedWonders);
        var unbuilt = GameState.WondersIn((ushort)((s.Wonders(0) | s.Wonders(1)) & ~(s.BuiltWonders(0) | s.BuiltWonders(1)))).ToList();
        Assert.Equal(2, unbuilt.Count);

        // Whoever owns one of the last two builds it; the other leaves the game.
        while (s.Step == StepKind.Reveal) game.Apply(chance.Next(s));
        var player = s.PlayerToDecide;
        if (s.BuildableWonders(player) == 0)
        {
            game.Apply(s.LegalMoves().Last(m => m.Kind == MoveKind.Discard));
            while (s.Step == StepKind.Reveal) game.Apply(chance.Next(s));
            player = s.PlayerToDecide;
        }
        s.SetCoins(player, 100);
        var seventh = s.LegalMoves().First(m => m.Kind == MoveKind.Wonder);
        game.Apply(seventh);
        var eighth = unbuilt.Single(w => w != seventh.Wonder);

        Assert.Equal(GameState.MaxWondersBuilt, s.TotalWondersBuilt);
        Assert.Equal(1 << eighth, s.RemovedWonders);
        Assert.True(s.IsWonderRemoved(eighth));
        Assert.False(s.IsWonderBuilt(eighth));
        var owner = s.OwnsWonder(0, eighth) ? 0 : 1;
        Assert.Equal(0, s.BuildableWonders(owner));
        Assert.Equal(3, s.WondersBuilt(owner));
        Assert.Equal(4, s.WondersBuilt(1 - owner));

        // Never offered again, to anyone.
        while (s.Step == StepKind.Reveal) game.Apply(chance.Next(s));
        s.SetCoins(0, 100);
        s.SetCoins(1, 100);
        Assert.DoesNotContain(s.LegalMoves(), m => m.Kind == MoveKind.Wonder);
        var card = Enumerable.Range(0, GameState.SlotCount).Where(s.IsAccessible).Select(s.SlotCard).First();
        Assert.Contains("left the game", s.WhyIllegal(Move.BuildWonder(card, eighth)) ?? "");
        Assert.Contains("(removed)", s.ToString());
    }

    [Fact]
    public void Wonders_built_feed_the_builders_guild()
    {
        var game = Start(Setup.Draft(0), "P1 wonder quarry pyramids", "P2 wonder baths colossus", "P1 wonder apothecary great-lighthouse");
        var s = game.State;
        Assert.Equal(2, s.WondersBuilt(0));
        Assert.Equal(1, s.WondersBuilt(1));
        Assert.Equal(2 * 2, Scoring.GuildPoints(s, GuildKind.Builders));
        Assert.Equal(9 + 4, WonderVp(s, 0));
        Assert.Equal(3, WonderVp(s, 1));
    }

    [Fact]
    public void A_copied_state_keeps_its_wonders()
    {
        var game = Start(DraftFor("sphinx", "colossus"), "P1 wonder quarry sphinx");
        var copy = game.State.Clone();
        Assert.True(copy.SameAs(game.State));
        Do(game, "P1 wonder baths colossus");
        Assert.False(copy.SameAs(game.State));
        Assert.Equal(0, copy.PlayerToDecide);
        Assert.False(copy.IsWonderBuilt(W("colossus")));
    }
}
