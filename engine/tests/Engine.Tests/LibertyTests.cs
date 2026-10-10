using System.Numerics;
using SevenWondersDuel.Engine.Data;

namespace SevenWondersDuel.Engine.Tests;

/// <summary>
/// The Statue of Liberty (promo-wonders §3, spec A4): build it → "reveal returned age&lt;N&gt; a b c" → liberty-keep →
/// liberty-give (the third card to the discard pile) → the owner's card resolves fully, then the opponent's. The
/// opponent picks the token from a science pair the given card completes, and can win on the owner's turn.
/// </summary>
public class LibertyTests
{
    const string Rules = "rules base promo=liberty";
    const string Tokens = "reveal tokens agriculture economy law strategy urbanism";
    /// <summary>
    /// Age I with every face-up card named. Unseen (8 face-down slots plus the 3 removed cards): clay-pool clay-pit
    /// stone-pit pharmacist stable garrison workshop scriptorium clay-reserve wood-reserve theater.
    /// </summary>
    const string Opening =
        "reveal age1 0.0 palisade 0.1 tavern 2.0 guard-tower 2.1 altar 2.2 lumber-yard 2.3 press "
        + "4.0 apothecary 4.1 baths 4.2 logging-camp 4.3 quarry 4.4 glassworks 4.5 stone-reserve";

    static int C(string slug) => GameData.CardIndex(slug);
    static int W(string slug) => GameData.WonderIndex(slug);
    static readonly int Liberty = GameData.WonderIndex("statue-of-liberty");

    /// <summary>A promo=liberty game, P1 first and owning the Statue, in Age I; both players get 50 coins.</summary>
    static Game Start(string rules = Rules)
    {
        var game = MoveLog.Parse(string.Join('\n',
            ["7wd-log v1", rules, "reveal first P1", Tokens, .. Setup.DraftFor("statue-of-liberty"), Opening]));
        game.State.SetCoins(0, 50);
        game.State.SetCoins(1, 50);
        return game;
    }

    static void Do(Game game, params string[] lines)
    {
        foreach (var line in lines) game.Apply(MoveLog.ParseEntry(line, game.State));
    }

    /// <summary>P1 builds the Statue with the Quarry (uncovering nothing) and reveals the returned cards.</summary>
    static Game Built(string returned)
    {
        var game = Start();
        Do(game, "P1 wonder quarry statue-of-liberty", $"reveal returned age1 {returned}");
        return game;
    }

    // ---------------------------------------------------------------- the promo switch

    [Fact]
    public void The_engine_accepts_the_statue_of_liberty_and_drafts_from_14_wonders_with_both_promos()
    {
        var both = GameState.New(Ruleset.Parse("rules base promo=messe,liberty"));
        Assert.Equal(14, BitOperations.PopCount(both.WonderPool));
        Assert.Equal(13, BitOperations.PopCount(GameState.New(Ruleset.Parse(Rules)).WonderPool));
        var e = Assert.Throws<FormatException>(() => MoveLog.Parse(string.Join('\n',
            ["7wd-log v1", "rules base promo=messe", "reveal first P1", Tokens, "reveal draft statue-of-liberty pyramids colossus sphinx"])));
        Assert.Contains("not in this game's Wonder pool", e.Message);
    }

    // ---------------------------------------------------------------- the whole sequence

    [Fact]
    public void The_statue_reveals_the_returned_cards_then_keep_and_give_place_all_three()
    {
        var game = Start();
        var s = game.State;
        Do(game, "P1 wonder quarry statue-of-liberty");
        Assert.True(s.IsWonderBuilt(Liberty));
        Assert.Equal(new PendingDecision(DecisionKind.LibertyKeep, 0, Liberty), s.Pending(0));
        Assert.Equal(StepKind.Reveal, s.Step);
        Assert.True(s.AwaitingReturnedCards);

        Do(game, "reveal returned age1 workshop theater stable");
        Assert.False(s.AwaitingReturnedCards);
        Assert.Equal(StepKind.Decision, s.Step);
        Assert.Equal(CardSet.Of([C("workshop"), C("theater"), C("stable")]), s.LibertyOffer);
        Assert.True((s.Seen & s.LibertyOffer) == s.LibertyOffer);
        Assert.Equal(new[] { "liberty-keep stable", "liberty-keep workshop", "liberty-keep theater" }.Order(),
            s.LegalMoves().Select(m => m.ToString()).Order());

        var coins = (s.Coins(0), s.Coins(1));
        Do(game, "P1 liberty-keep theater");
        Assert.Equal(new PendingDecision(DecisionKind.LibertyGive, 0, Liberty), s.Pending(0));
        Assert.Equal(C("theater"), s.LibertyKept);
        Assert.Equal(2, s.LegalMoves().Count);
        Assert.Contains("keeps theater, gives P2 one of", StatePrinter.Describe(s));
        Assert.False(s.City(0).Contains(C("theater"))); // nothing resolves before the give

        Do(game, "P1 liberty-give stable");
        Assert.True(s.City(0).Contains(C("theater")));
        Assert.True(s.City(1).Contains(C("stable")));
        Assert.True(s.DiscardPile.Contains(C("workshop")));
        Assert.Equal(coins, (s.Coins(0), s.Coins(1))); // both builds are free
        Assert.Equal(-1, s.ConflictPawn); // P2's Stable pushes toward P1
        Assert.True(s.LibertyOffer.IsEmpty);
        Assert.Equal(-1, s.LibertyKept);
        Assert.Equal(new PendingDecision(DecisionKind.TakeCard, 1), s.Pending(0));
        Assert.Equal(["P1 wonder quarry statue-of-liberty", "reveal returned age1 workshop theater stable",
            "P1 liberty-keep theater", "P1 liberty-give stable"], game.Log.TakeLast(4).Select(e => e.ToString()));
    }

    [Fact]
    public void The_given_card_takes_effect_in_the_opponents_city_with_the_opponents_strategy()
    {
        var game = Built("garrison theater stable");
        var s = game.State;
        s.TakeToken(1, ProgressTokens.Strategy);
        Do(game, "P1 liberty-keep garrison", "P1 liberty-give stable");
        Assert.Equal(1 - 2, s.ConflictPawn); // P1's Garrison +1, P2's Stable 1 + Strategy's 1
        Assert.Equal(1, s.CardsOfColor(1, CardColor.Red));
    }

    [Fact]
    public void The_returned_cards_come_after_the_flips_the_statues_card_caused()
    {
        var game = Start();
        var s = game.State;
        Do(game, "P1 discard apothecary", "P2 discard logging-camp", "P1 wonder baths statue-of-liberty");
        Assert.NotEqual(0u, s.SlotsToReveal); // 3.0 and 3.1 are uncovered
        var e = Assert.Throws<InvalidOperationException>(() => s.Apply(new ReturnedReveal(1, [C("workshop"), C("theater"), C("stable")])));
        Assert.Contains("Newly accessible cards are revealed before", e.Message);

        var chance = new RandomChanceSource(3);
        Assert.IsType<SlotReveal>(chance.Next(s));
        game.Apply(chance.Next(s));
        var returned = Assert.IsType<ReturnedReveal>(chance.Next(s));
        Assert.Equal(1, returned.Age);
        game.Apply(returned);
        Assert.Equal(GameState.ReturnedCardCount, s.LibertyOffer.Count);
    }

    [Fact]
    public void Only_three_different_unseen_cards_of_the_current_age_can_be_returned()
    {
        var game = Start();
        var s = game.State;
        Do(game, "P1 wonder quarry statue-of-liberty");
        void Rejects(string line, string why)
        {
            var e = Assert.ThrowsAny<Exception>(() => game.Apply(MoveLog.ParseEntry(line, s)));
            Assert.Contains(why, e.Message);
        }
        Rejects("reveal returned age1 workshop theater baths", "not an unseen Age 1 card"); // Baths is face up
        Rejects("reveal returned age1 workshop theater school", "not an unseen Age 1 card"); // an Age II card
        Rejects("reveal returned age2 school library walls", "Age 1's, not Age 2's");
        Rejects("reveal returned age1 workshop workshop theater", "3 different cards");
        Rejects("reveal returned age1 workshop theater", "Expected 'reveal returned");
        Assert.Throws<InvalidOperationException>(() => s.Apply(new ReturnedReveal(1, [C("workshop"), C("theater"), C("stable"), C("garrison")])));
        Assert.True(s.AwaitingReturnedCards);
    }

    [Fact]
    public void A_returned_reveal_is_refused_when_nothing_waits_for_it()
    {
        var game = Start();
        var e = Assert.Throws<InvalidOperationException>(() => game.State.Apply(new ReturnedReveal(1, [C("workshop"), C("theater"), C("stable")])));
        Assert.Contains("not waiting for the Statue of Liberty", e.Message);
    }

    [Fact]
    public void Keep_and_give_take_only_cards_on_offer()
    {
        var game = Built("workshop theater stable");
        var s = game.State;
        Assert.Contains("which returned card the Statue of Liberty's owner builds", s.WhyIllegal(Move.LibertyGive(C("stable"))));
        Assert.Contains("not among the Statue of Liberty's cards", s.WhyIllegal(Move.LibertyKeep(C("garrison"))));
        Assert.Contains("which returned card the Statue of Liberty's owner builds", s.WhyIllegal(Move.Build(C("baths"))));
        Assert.Contains("keeps one of", StatePrinter.Describe(s));
        Do(game, "P1 liberty-keep theater");
        Assert.Contains("which returned card the opponent builds", s.WhyIllegal(Move.LibertyKeep(C("stable"))));
        Assert.Contains("not among the Statue of Liberty's cards", s.WhyIllegal(Move.LibertyGive(C("theater"))));
        Assert.Throws<InvalidOperationException>(() => s.Apply(Move.LibertyGive(C("theater"))));
        Assert.Equal(new MoveEntry(0, Move.LibertyGive(C("stable"))), MoveLog.ParseEntry("P1 liberty-give stable", s));
    }

    [Fact]
    public void Both_players_see_the_returned_cards()
    {
        var game = Built("workshop theater stable");
        var s = game.State;
        for (var p = 0; p < 2; p++)
        {
            Assert.True(s.Observe(p).State.SameAs(s));
            Assert.Equal(s.LibertyOffer, s.Observe(p).State.LibertyOffer);
        }
        Do(game, "P1 liberty-keep theater");
        Assert.True(s.Observe(1).State.SameAs(s));
    }

    // ---------------------------------------------------------------- each Age

    /// <summary>Discards (random flips, first legal start choice) until it is P1's take-card turn in <paramref name="age"/>.</summary>
    static void PlayToAge(Game game, int age)
    {
        var s = game.State;
        var chance = new RandomChanceSource((ulong)(7 * age));
        while (!(s.Age == age && s.Step == StepKind.Decision && s.Pending(0) == new PendingDecision(DecisionKind.TakeCard, 0)))
        {
            if (s.Step == StepKind.Reveal) game.Apply(chance.Next(s));
            else if (s.Pending(0).Kind == DecisionKind.TakeCard)
                game.Apply(new MoveEntry(s.PlayerToDecide, s.LegalMoves().Last(m => m.Kind == MoveKind.Discard)));
            else game.Apply(new MoveEntry(s.PlayerToDecide, s.LegalMoves()[0]));
        }
        s.SetCoins(0, 50);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void The_statue_returns_three_removed_cards_of_the_current_age(int age)
    {
        var game = Start();
        var s = game.State;
        PlayToAge(game, age);
        var seenBefore = s.Seen;
        game.Apply(new MoveEntry(0, s.LegalMoves().First(m => m.Kind == MoveKind.Wonder && m.Wonder == Liberty)));
        var chance = new RandomChanceSource(11);
        while (s.Step == StepKind.Reveal) game.Apply(chance.Next(s));

        var offer = s.LibertyOffer;
        Assert.Equal(GameState.ReturnedCardCount, offer.Count);
        Assert.Equal(offer, offer & CardSets.OfAge[age]); // in Age III: removed Age III cards, never Guilds
        Assert.True((offer & CardSets.Guilds).IsEmpty);
        Assert.True((offer & seenBefore).IsEmpty);
        var log = game.Log.OfType<ReturnedReveal>().Single();
        Assert.Equal($"reveal returned age{age} ", log.ToString()[..$"reveal returned age{age} ".Length]);
        for (var slot = 0; slot < GameState.SlotCount; slot++)
            Assert.False(offer.Contains(s.SlotCard(slot)));

        var cards = offer.Cards().ToArray();
        game.Apply(new MoveEntry(0, Move.LibertyKeep(cards[0])));
        game.Apply(new MoveEntry(0, Move.LibertyGive(cards[1])));
        while (s.Step == StepKind.Decision && s.Pending(0).Kind == DecisionKind.ChooseToken)
            game.Apply(new MoveEntry(s.PlayerToDecide, s.LegalMoves()[0]));
        if (!s.IsOver)
        {
            Assert.True(s.City(0).Contains(cards[0]));
            Assert.True(s.City(1).Contains(cards[1]));
        }
        Assert.True(s.DiscardPile.Contains(cards[2]));

        // The unseen pool is now exactly the face-down cards still to come.
        var hidden = Enumerable.Range(0, GameState.SlotCount).Count(x => s.SlotCard(x) == GameState.Hidden);
        if (s.Age == age) Assert.Equal(hidden, s.UnseenPool(age).Count);
    }

    [Fact]
    public void In_age_three_a_guild_cannot_be_returned()
    {
        var game = Start();
        var s = game.State;
        PlayToAge(game, 3);
        game.Apply(new MoveEntry(0, s.LegalMoves().First(m => m.Kind == MoveKind.Wonder && m.Wonder == Liberty)));
        var chance = new RandomChanceSource(1);
        while (s.SlotsToReveal != 0) game.Apply(chance.Next(s));
        var unseen = s.UnseenPool(3).Cards().ToArray();
        var guild = s.UnseenGuilds.Cards().First();
        var e = Assert.Throws<InvalidOperationException>(() => s.Apply(new ReturnedReveal(3, [guild, unseen[0], unseen[1]])));
        Assert.Contains("not an unseen Age 3 card", e.Message);
    }

    // ---------------------------------------------------------------- order of resolution and the opponent

    [Fact]
    public void The_opponent_picks_the_token_from_the_given_cards_science_pair_on_the_owners_turn()
    {
        var game = Built("scriptorium theater stable");
        var s = game.State;
        s.AddToCity(1, C("library")); // quill, like the Scriptorium
        Do(game, "P1 liberty-keep theater", "P1 liberty-give scriptorium");
        Assert.Equal(new PendingDecision(DecisionKind.ChooseToken, 1), s.Pending(0));
        Assert.Equal(1, s.PlayerToDecide);
        Assert.Equal(0, s.CurrentPlayer);
        Do(game, "P2 token urbanism");
        Assert.True(s.HasToken(1, ProgressTokens.Urbanism));
        Assert.Equal(new PendingDecision(DecisionKind.TakeCard, 1), s.Pending(0));
    }

    [Fact]
    public void The_owners_card_resolves_fully_with_its_token_pick_before_the_opponents_card()
    {
        var game = Built("scriptorium workshop stable");
        var s = game.State;
        s.AddToCity(0, C("laboratory")); // compass, like the Workshop
        s.AddToCity(1, C("library")); // quill, like the Scriptorium
        Do(game, "P1 liberty-keep workshop", "P1 liberty-give scriptorium");
        Assert.Equal(new PendingDecision(DecisionKind.ChooseToken, 0), s.Pending(0));
        Assert.Equal(1, s.PendingCount);
        Assert.False(s.City(1).Contains(C("scriptorium"))); // not built yet
        Assert.True(s.DiscardPile.Contains(C("stable")));

        Do(game, "P1 token strategy");
        Assert.True(s.City(1).Contains(C("scriptorium")));
        Assert.Equal(new PendingDecision(DecisionKind.ChooseToken, 1), s.Pending(0));
        Do(game, "P2 token law");
        Assert.Equal(new PendingDecision(DecisionKind.TakeCard, 1), s.Pending(0));
    }

    [Fact]
    public void When_the_owner_takes_the_last_token_the_opponents_pair_earns_none()
    {
        var game = Built("scriptorium workshop stable");
        var s = game.State;
        s.SetBoardTokens((ushort)(1 << ProgressTokens.Agriculture));
        s.AddToCity(0, C("laboratory"));
        s.AddToCity(1, C("library"));
        Do(game, "P1 liberty-keep workshop", "P1 liberty-give scriptorium", "P1 token agriculture");
        Assert.True(s.City(1).Contains(C("scriptorium")));
        Assert.Equal(new PendingDecision(DecisionKind.TakeCard, 1), s.Pending(0));
    }

    [Fact]
    public void The_opponent_can_win_by_military_from_the_given_card()
    {
        var game = Built("workshop theater stable");
        var s = game.State;
        s.SetConflictPawn(-8); // one space from P1's capital
        Do(game, "P1 liberty-keep theater", "P1 liberty-give stable");
        Assert.True(s.IsOver);
        Assert.Equal((1, VictoryType.Military), (s.Result!.Winner, s.Result.Victory));
        Assert.Equal(0, s.CurrentPlayer); // on P1's turn
        Assert.Equal(0, s.PendingCount);
        Assert.Equal(StepKind.GameOver, s.Step);
    }

    [Fact]
    public void The_opponent_can_win_by_science_from_the_given_card()
    {
        var game = Built("pharmacist theater stable");
        var s = game.State;
        foreach (var card in new[] { "laboratory", "school", "library", "study", "university" }) s.AddToCity(1, C(card));
        Assert.Equal(5, s.DistinctSymbols(1));
        Do(game, "P1 liberty-keep theater", "P1 liberty-give pharmacist");
        Assert.Equal((1, VictoryType.Science), (s.Result!.Winner, s.Result.Victory));
        Assert.Equal(0, s.CurrentPlayer);
    }

    [Fact]
    public void An_owner_who_wins_with_the_kept_card_wins_before_the_given_card_is_built()
    {
        var game = Built("garrison theater stable");
        var s = game.State;
        s.SetConflictPawn(8);
        Do(game, "P1 liberty-keep stable", "P1 liberty-give garrison");
        Assert.Equal((0, VictoryType.Military), (s.Result!.Winner, s.Result.Victory));
        Assert.False(s.City(1).Contains(C("garrison")));
        Assert.True(s.DiscardPile.Contains(C("theater")));
        Assert.Equal(GameState.CapitalDistance, s.ConflictPawn);
    }

    [Fact]
    public void An_owner_whose_token_pick_wins_does_so_before_the_given_card_is_built()
    {
        // P1 shows 4 symbols and a Dispensary: the kept Pharmacist pairs mortar (5 symbols), and Law is the 6th.
        var game = Built("pharmacist theater stable");
        var s = game.State;
        foreach (var card in new[] { "laboratory", "school", "library", "study", "dispensary" }) s.AddToCity(0, C(card));
        s.SetConflictPawn(-8); // P2's Stable would win by military if it came first
        Do(game, "P1 liberty-keep pharmacist", "P1 liberty-give stable");
        Assert.Equal(new PendingDecision(DecisionKind.ChooseToken, 0), s.Pending(0));
        Do(game, "P1 token law");
        Assert.Equal((0, VictoryType.Science), (s.Result!.Winner, s.Result.Victory));
        Assert.False(s.City(1).Contains(C("stable")));
        Assert.Equal(-8, s.ConflictPawn);
        Assert.Equal(0, s.PendingCount);
    }

    [Fact]
    public void A_theology_play_again_comes_after_all_the_statues_decisions()
    {
        var game = Start();
        var s = game.State;
        s.TakeToken(0, ProgressTokens.Theology);
        Do(game, "P1 wonder quarry statue-of-liberty", "reveal returned age1 workshop theater stable");
        Assert.True(s.PlayAgain);
        Do(game, "P1 liberty-keep theater", "P1 liberty-give stable");
        Assert.Equal(new PendingDecision(DecisionKind.TakeCard, 0), s.Pending(0));
        Assert.False(s.PlayAgain);
    }

    [Fact]
    public void Free_builds_earn_no_urbanism_coins_even_for_a_chain()
    {
        var game = Built("workshop theater stable");
        var s = game.State;
        s.TakeToken(0, ProgressTokens.Urbanism);
        s.TakeToken(1, ProgressTokens.Economy);
        var coins = (s.Coins(0), s.Coins(1));
        Do(game, "P1 liberty-keep theater", "P1 liberty-give stable");
        Assert.Equal(coins, (s.Coins(0), s.Coins(1)));
    }

    [Fact]
    public void A_statue_game_writes_and_replays_its_log()
    {
        for (ulong seed = 0; seed < 300; seed++)
        {
            var game = Agents(seed);
            var text = game.WriteLog();
            Assert.True(MoveLog.Parse(text).State.SameAs(game.State));
        }

        static Game Agents(ulong seed)
        {
            var game = new Game(Ruleset.Parse("rules base promo=messe,liberty"));
            var s = game.State;
            var chance = new RandomChanceSource(seed);
            var rng = new Rng(seed + 1);
            while (!s.IsOver)
                if (s.Step == StepKind.Reveal) game.Apply(chance.Next(s));
                else
                {
                    var moves = s.LegalMoves();
                    // Prefer building the Statue whenever possible, so most games use it.
                    var statue = moves.FindIndex(m => m.Kind == MoveKind.Wonder && m.Wonder == Liberty);
                    game.Apply(new MoveEntry(s.PlayerToDecide, statue >= 0 ? moves[statue] : moves[rng.Next(moves.Count)]));
                }
            return game;
        }
    }
}
