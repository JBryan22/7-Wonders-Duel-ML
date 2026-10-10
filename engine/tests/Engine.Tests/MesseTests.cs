using System.Numerics;
using SevenWondersDuel.Engine.Data;

namespace SevenWondersDuel.Engine.Tests;

/// <summary>
/// The promo= switch and the Wonder pool (spec A4), and The Messe (promo-wonders §2): build one card of the current
/// Age's top row for free, even a covered one; skipped when the top row is empty.
/// </summary>
public class MesseTests
{
    const string Rules = "rules base promo=messe";
    const string Tokens = "reveal tokens agriculture economy law strategy urbanism";
    /// <summary>Age I: the top row (0.0 palisade, 0.1 tavern) is covered by row 1 until nearly the end of the Age.</summary>
    const string Opening =
        "reveal age1 0.0 palisade 0.1 tavern 2.0 guard-tower 2.1 altar 2.2 lumber-yard 2.3 pharmacist "
        + "4.0 apothecary 4.1 baths 4.2 logging-camp 4.3 quarry 4.4 glassworks 4.5 stone-reserve";

    static int C(string slug) => GameData.CardIndex(slug);
    static int W(string slug) => GameData.WonderIndex(slug);
    static readonly string[] Base = GameData.Wonders.Where(w => !w.IsPromo).Select(w => w.Id).ToArray();

    /// <summary>A promo=messe game with P1 first and the given draft; both players get 50 coins.</summary>
    static Game Start(string[] draft, string opening = Opening)
    {
        var game = MoveLog.Parse(string.Join('\n', ["7wd-log v1", Rules, "reveal first P1", Tokens, .. draft, opening]));
        game.State.SetCoins(0, 50);
        game.State.SetCoins(1, 50);
        return game;
    }

    /// <summary>P1 owns the Messe.</summary>
    static Game StartP1(string opening = Opening) => Start(Setup.DraftFor("messe"), opening);

    static void Do(Game game, params string[] lines)
    {
        foreach (var line in lines) game.Apply(MoveLog.ParseEntry(line, game.State));
    }

    /// <summary>
    /// Discards cards of Age I, highest slot first (so the top row goes last), with random flips, until
    /// <paramref name="left"/> cards remain.
    /// </summary>
    static void DiscardUntil(Game game, int left)
    {
        var s = game.State;
        var chance = new RandomChanceSource(5);
        while (s.CardsLeftInAge > left || s.Step == StepKind.Reveal)
        {
            if (s.Step == StepKind.Reveal) game.Apply(chance.Next(s));
            else game.Apply(new MoveEntry(s.PlayerToDecide, s.LegalMoves().Last(m => m.Kind == MoveKind.Discard)));
        }
    }

    // ---------------------------------------------------------------- the promo switch and the Wonder pool

    [Fact]
    public void The_wonder_pool_is_the_12_base_wonders_plus_each_promo_switched_on()
    {
        Assert.Equal(12, BitOperations.PopCount(GameState.PoolFor(Promos.None)));
        Assert.Equal(13, BitOperations.PopCount(GameState.PoolFor(Promos.Messe)));
        Assert.Equal(13, BitOperations.PopCount(GameState.PoolFor(Promos.Liberty)));
        Assert.Equal(14, BitOperations.PopCount(GameState.PoolFor(Promos.Messe | Promos.Liberty)));
        Assert.Equal(GameState.PoolFor(Promos.None) | 1 << W("messe"), GameState.PoolFor(Promos.Messe));
        Assert.Equal(GameState.PoolFor(Promos.None) | 1 << W("statue-of-liberty"), GameState.PoolFor(Promos.Liberty));

        var messe = GameState.New(Ruleset.Parse(Rules));
        Assert.Equal(GameState.PoolFor(Promos.Messe), messe.WonderPool);
        Assert.Equal(GameState.PoolFor(Promos.None), GameState.New(Ruleset.Default).WonderPool);
    }

    [Fact]
    public void The_messe_is_offered_only_when_switched_on()
    {
        var head = new[] { "7wd-log v1", "rules base", "reveal first P1", Tokens };
        var e = Assert.Throws<FormatException>(() => MoveLog.Parse(string.Join('\n', [.. head, "reveal draft messe pyramids colossus sphinx"])));
        Assert.Contains("not in this game's Wonder pool", e.Message);

        var game = MoveLog.Parse(string.Join('\n', ["7wd-log v1", Rules, "reveal first P1", Tokens, "reveal draft messe pyramids colossus sphinx"]));
        Assert.Equal(DecisionKind.DraftWonder, game.State.Pending(0).Kind);
        Assert.Equal(13 - 4, BitOperations.PopCount(game.State.UnseenWonders));
    }

    [Fact]
    public void A_random_draft_reaches_the_messe_only_with_the_switch_on()
    {
        int offered = 0;
        for (ulong seed = 0; seed < 200; seed++)
            foreach (var promos in new[] { Promos.None, Promos.Messe })
            {
                var s = GameState.New(new Ruleset(Promos: promos));
                var chance = new RandomChanceSource(seed);
                while (s.Step == StepKind.Reveal) s.Apply(chance.Next(s));
                // Both draft rounds' Wonders come from the pool: draft the first Wonder offered each time.
                while (s.Age == 0)
                    if (s.Step == StepKind.Reveal) s.Apply(chance.Next(s));
                    else s.Apply(s.LegalMoves()[0]);
                var drafted = (ushort)(s.Wonders(0) | s.Wonders(1));
                Assert.Equal(drafted, drafted & s.WonderPool);
                if ((drafted & 1 << W("messe")) != 0)
                {
                    Assert.Equal(Promos.Messe, promos);
                    offered++;
                }
            }
        // 8 of 13 Wonders are drafted, so the Messe is in about 62% of promo games.
        Assert.InRange(offered, 90, 160);
    }

    [Fact]
    public void The_statue_of_liberty_is_still_refused_until_ticket_13()
    {
        var e = Assert.Throws<NotSupportedException>(() => GameState.New(Ruleset.Parse("rules base promo=messe,liberty")));
        Assert.Contains("Statue of Liberty", e.Message);
    }

    // ---------------------------------------------------------------- The Messe

    [Fact]
    public void The_messe_builds_a_covered_top_row_card_for_free()
    {
        var game = StartP1();
        var s = game.State;
        Assert.False(s.IsAccessible(0) || s.IsAccessible(1)); // both top-row cards are covered

        Do(game, "P1 wonder quarry messe");
        Assert.True(s.IsWonderBuilt(W("messe")));
        Assert.Equal(new PendingDecision(DecisionKind.Messe, 0, W("messe")), s.Pending(0));
        Assert.Equal(new[] { Move.Messe(C("palisade")), Move.Messe(C("tavern")) }, s.LegalMoves());

        var coins = s.Coins(0);
        Do(game, "P1 messe tavern");
        Assert.True(s.City(0).Contains(C("tavern")));
        Assert.Equal(coins + 4, s.Coins(0)); // free, plus the Tavern's own 4 coins
        Assert.Equal(GameState.Empty, s.SlotCard(1));
        Assert.Equal(18, s.CardsLeftInAge);
        // Nothing lay under the Tavern: nothing flips, and the opponent's turn follows at once.
        Assert.Equal(StepKind.Decision, s.Step);
        Assert.Equal(new PendingDecision(DecisionKind.TakeCard, 1), s.Pending(0));
        Assert.False(s.IsAccessible(0));
        Assert.Equal("P1 messe tavern", game.Log[^1].ToString());
    }

    [Fact]
    public void A_messe_build_pays_nothing_and_applies_the_cards_effects_with_strategy()
    {
        var game = StartP1();
        var s = game.State;
        s.TakeToken(0, ProgressTokens.Strategy);
        Do(game, "P1 wonder quarry messe");
        var coins = s.Coins(0);
        Do(game, "P1 messe palisade"); // printed cost 2 coins
        Assert.Equal(coins, s.Coins(0));
        Assert.Equal(2, s.ConflictPawn); // 1 shield + Strategy's 1 (the Messe itself has none)
        Assert.Equal(1, s.CardsOfColor(0, CardColor.Red));
    }

    [Fact]
    public void A_messe_build_can_complete_a_science_pair()
    {
        var game = StartP1("reveal age1 0.0 pharmacist 0.1 tavern 2.0 guard-tower 2.1 altar 2.2 lumber-yard 2.3 palisade "
            + "4.0 apothecary 4.1 baths 4.2 logging-camp 4.3 quarry 4.4 glassworks 4.5 stone-reserve");
        var s = game.State;
        s.AddToCity(0, C("dispensary")); // mortar, like the Pharmacist
        Do(game, "P1 wonder quarry messe", "P1 messe pharmacist");
        Assert.Equal(new PendingDecision(DecisionKind.ChooseToken, 0), s.Pending(0));
        Do(game, "P1 token law");
        Assert.Equal(new PendingDecision(DecisionKind.TakeCard, 1), s.Pending(0));
    }

    [Fact]
    public void A_theology_play_again_comes_after_the_messe_choice()
    {
        var game = StartP1();
        var s = game.State;
        s.TakeToken(0, ProgressTokens.Theology);
        Do(game, "P1 wonder quarry messe");
        Assert.True(s.PlayAgain);
        Do(game, "P1 messe palisade");
        Assert.Equal(new PendingDecision(DecisionKind.TakeCard, 0), s.Pending(0));
        Assert.False(s.PlayAgain);
    }

    [Fact]
    public void The_messe_takes_only_a_top_row_card()
    {
        var game = StartP1();
        var s = game.State;
        Do(game, "P1 wonder quarry messe");
        Assert.Contains("not in the top row", s.WhyIllegal(Move.Messe(C("baths")))); // accessible, but row 4
        Assert.Contains("not in the top row", s.WhyIllegal(Move.Messe(C("lumber-yard"))));
        Assert.Contains("not in the top row", s.WhyIllegal(Move.Messe(C("quarry")))); // under the Messe now
        Assert.Contains("top-row card the Messe builds", s.WhyIllegal(Move.Discard(C("baths"))));
        Assert.Throws<InvalidOperationException>(() => s.Apply(Move.Messe(C("baths"))));
        Assert.Contains("builds a top-row card for free (the Messe)", StatePrinter.Describe(s));
    }

    [Fact]
    public void A_messe_move_may_name_its_slot_and_the_log_round_trips()
    {
        var game = StartP1();
        Do(game, "P1 wonder quarry messe", "P1 messe 0.1");
        Assert.Equal("P1 messe tavern", game.Log[^1].ToString());
        Assert.Contains("rules base start-choice=new promo=messe\n", game.WriteLog());
        Assert.Equal(new MoveEntry(0, Move.Messe(C("palisade"))), MoveLog.ParseEntry("P1 messe palisade", game.State));
        Assert.Equal("messe palisade", Move.Messe(C("palisade")).ToString());
        // (The full write-and-replay round trip of promo games is the soak's: these test coins are not in the log.)
    }

    [Fact]
    public void The_messe_taking_the_ages_last_card_ends_the_age()
    {
        var game = StartP1();
        var s = game.State;
        DiscardUntil(game, 2);
        Assert.Equal(0b11u, s.TopRowSlots); // only the top row is left
        Assert.Equal(0, s.PlayerToDecide); // P1 takes the 19th card
        Do(game, "P1 wonder palisade messe");
        Assert.Equal(new[] { Move.Messe(C("tavern")) }, s.LegalMoves());
        Do(game, "P1 messe tavern");
        Assert.Equal(2, s.Age);
        Assert.Equal(StepKind.Reveal, s.Step); // Age II's opening
    }

    [Fact]
    public void The_messe_does_nothing_when_the_top_row_is_empty()
    {
        // The top row lies under every other row, so it empties only with the whole structure: the Messe built with
        // the Age's last card finds nothing. P2 takes the 20th card of Age I.
        var game = Start(Setup.Draft(0, Base[..4], ["messe", .. Base[4..7]]));
        var s = game.State;
        DiscardUntil(game, 1);
        Assert.Equal(1, s.PlayerToDecide);
        var last = s.SlotCard(BitOperations.TrailingZeroCount(s.TopRowSlots));
        game.Apply(new MoveEntry(1, Move.BuildWonder(last, W("messe"))));
        Assert.True(s.IsWonderBuilt(W("messe")));
        Assert.Equal(2, s.Age); // no Messe decision: Age II starts
        Assert.DoesNotContain(game.Log, e => e is MoveEntry { Move.Kind: MoveKind.Messe });
        Assert.Equal(StepKind.Reveal, s.Step);
    }
}
