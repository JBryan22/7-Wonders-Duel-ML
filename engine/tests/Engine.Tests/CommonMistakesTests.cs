using SevenWondersDuel.Engine.Data;

namespace SevenWondersDuel.Engine.Tests;

/// <summary>
/// One test per "edge case implementers often get wrong" in docs/research/rules-reference.md §12 (spec A11: the 20
/// common mistakes), numbered as there. Several repeat a check from the topic's own test class; here each mistake
/// has one place to look.
/// </summary>
public class CommonMistakesTests
{
    const string Tokens = "reveal tokens agriculture economy law strategy urbanism";
    /// <summary>Age I: row 4 is accessible; slot 3.k is uncovered once 4.k and 4.(k+1) are both taken.</summary>
    const string Opening =
        "reveal age1 0.0 palisade 0.1 tavern 2.0 guard-tower 2.1 altar 2.2 lumber-yard 2.3 pharmacist "
        + "4.0 apothecary 4.1 baths 4.2 logging-camp 4.3 quarry 4.4 glassworks 4.5 stone-reserve";

    static int C(string slug) => GameData.CardIndex(slug);
    static int W(string slug) => GameData.WonderIndex(slug);

    /// <summary>A game with P1 first, the given draft and the Age I opening; both players get 50 coins.</summary>
    static Game Start(string[] draft)
    {
        var game = MoveLog.Parse(string.Join('\n', ["7wd-log v1", "rules base", "reveal first P1", Tokens, .. draft, Opening]));
        game.State.SetCoins(0, 50);
        game.State.SetCoins(1, 50);
        return game;
    }

    static void Do(Game game, params string[] lines)
    {
        foreach (var line in lines) game.Apply(MoveLog.ParseEntry(line, game.State));
    }

    static void Give(GameState s, int player, params string[] cards)
    {
        foreach (var card in cards) s.AddToCity(player, C(card));
    }

    static GameState Position(string[] p1, string[] p2)
    {
        var s = GameState.New(Ruleset.Default);
        Give(s, 0, p1);
        Give(s, 1, p2);
        return s;
    }

    [Fact]
    public void Mistake_01_trading_prices_count_only_the_opponents_brown_and_grey()
    {
        // Lumber Yard 1 + Sawmill 2 (a double card counts twice); the yellow Caravansery and Wood Reserve do not count.
        var s = Position([], ["lumber-yard", "sawmill", "caravansery", "wood-reserve"]);
        Assert.Equal(2 + 3, Payment.TradingPrice(s, 0, Resource.Wood));
        Assert.Equal(2, Payment.TradingPrice(s, 0, Resource.Clay));
        Assert.Equal(2, Payment.TradingPrice(s, 0, Resource.Stone));

        // Nor does Wonder production: P1's Great Lighthouse leaves P2's prices alone.
        var game = Start(Setup.Draft(0));
        Do(game, "P1 wonder quarry great-lighthouse");
        foreach (var r in new[] { Resource.Wood, Resource.Clay, Resource.Stone })
            Assert.Equal(2, Payment.TradingPrice(game.State, 1, r));
    }

    [Fact]
    public void Mistake_02_a_destroyed_card_lowers_prices_and_reaches_the_discard_pile()
    {
        var game = Start(Setup.DraftFor("statue-of-zeus", "mausoleum"));
        var s = game.State;
        Give(s, 1, "clay-pit");
        Assert.Equal(3, Payment.TradingPrice(s, 0, Resource.Clay));

        Do(game, "P1 wonder quarry statue-of-zeus", "P1 destroy clay-pit");
        Assert.Equal(2, Payment.TradingPrice(s, 0, Resource.Clay));
        Assert.True(s.DiscardPile.Contains(C("clay-pit")));

        // A later Mausoleum can build it.
        Do(game, "P2 discard stone-reserve", "P1 wonder apothecary mausoleum");
        Assert.Contains(Move.Mausoleum(C("clay-pit")), s.LegalMoves());
        Do(game, "P1 mausoleum clay-pit");
        Assert.True(s.City(0).Contains(C("clay-pit")));
        Assert.Equal(1, s.Production(0, Resource.Clay));
    }

    [Fact]
    public void Mistake_03_choices_and_discounts_go_on_the_dearest_resources()
    {
        // Rostrum (S W) with a Caravansery (W/C/S): Stone costs 4 to buy and Wood 2, so the choice takes the Stone.
        var s = Position(["caravansery"], ["quarry", "stone-pit"]);
        Assert.Equal(2, Payment.Quote(s, 0, C("rostrum")).Total);

        // Palace (C S W G G) with Masonry: the 2-resource discount takes Stone (4) and one Glass (3): 2 + 2 + 3 = 7.
        s = Position([], ["quarry", "stone-pit", "glassworks"]);
        s.TakeToken(0, ProgressTokens.Masonry);
        Assert.Equal(7, Payment.Quote(s, 0, C("palace")).Total);
    }

    [Fact]
    public void Mistake_04_a_chain_makes_the_card_free_and_urbanism_pays_only_for_chains()
    {
        // Barracks prints $3; owning the Garrison makes it free, coins included.
        var s = Position(["garrison"], []);
        Assert.Equal(0, Payment.Quote(s, 0, C("barracks")).Total);
        Assert.True(Payment.IsChained(s, 0, C("barracks")));

        // A chain build with Urbanism earns +4.
        var (chainGame, card) = ScienceTests.PlayUntilAccessible(c => c.FreeIf >= 0);
        var cs = chainGame.State;
        var me = cs.PlayerToDecide;
        cs.AddToCity(me, GameData.Cards[card].FreeIf);
        cs.TakeToken(me, ProgressTokens.Urbanism);
        var before = cs.Coins(me);
        cs.Apply(Move.Build(card));
        Assert.Equal(before + ProgressTokens.UrbanismChainCoins, cs.Coins(me));

        // A free card without a chain, a Mausoleum build of a chained card and a Wonder earn nothing.
        var game = Start(Setup.DraftFor("mausoleum"));
        s = game.State;
        s.TakeToken(0, ProgressTokens.Urbanism);
        Give(s, 0, "garrison");
        s.AddToDiscard(C("barracks"));
        before = s.Coins(0);
        Do(game, "P1 build quarry", "P2 discard stone-reserve");
        Assert.Equal(before, s.Coins(0));
        before = s.Coins(0) - Payment.QuoteWonder(s, 0, W("mausoleum")).Total;
        Do(game, "P1 wonder apothecary mausoleum");
        Assert.Equal(before, s.Coins(0));
        Do(game, "P1 mausoleum barracks");
        Assert.Equal(before, s.Coins(0));
    }

    [Fact]
    public void Mistake_05_discard_value_counts_only_yellow_cards_already_in_the_city()
    {
        var game = Start(Setup.Draft(0));
        Give(game.State, 0, "wood-reserve", "clay-reserve");
        Do(game, "P1 discard stone-reserve"); // yellow itself, but not counted
        Assert.Equal(50 + 2 + 2, game.State.Coins(0));
    }

    [Fact]
    public void Mistake_06_only_discards_and_destroys_fill_the_discard_pile()
    {
        var game = Start(Setup.DraftFor("pyramids"));
        var s = game.State;
        Do(game, "P1 wonder quarry pyramids");
        Assert.True(s.DiscardPile.IsEmpty); // not the tucked Age card, nor the 3 Age I cards removed at setup
        Do(game, "P2 discard baths");
        Assert.Equal(CardSet.Of(C("baths")), s.DiscardPile);
    }

    [Fact]
    public void Mistake_07_once_seven_wonders_are_built_the_eighth_is_never_offered_again()
    {
        var game = Start(Setup.Draft(0));
        var s = game.State;
        var chance = new RandomChanceSource(11);
        var checkedAfterSeventh = 0;
        while (!s.IsOver)
        {
            if (s.Step == StepKind.Reveal) { game.Apply(chance.Next(s)); continue; }
            var moves = s.LegalMoves();
            if (s.Pending(0).Kind != DecisionKind.TakeCard) { game.Apply(moves[0]); continue; }
            if (s.TotalWondersBuilt == GameState.MaxWondersBuilt)
            {
                Assert.Equal(1, System.Numerics.BitOperations.PopCount((uint)s.RemovedWonders));
                Assert.DoesNotContain(moves, m => m.Kind == MoveKind.Wonder);
                checkedAfterSeventh++;
            }
            else Assert.Equal(0, s.RemovedWonders);
            s.SetCoins(s.PlayerToDecide, 100);
            moves = s.LegalMoves();
            game.Apply(moves.FirstOrDefault(m => m.Kind == MoveKind.Wonder, moves.Last(m => m.Kind == MoveKind.Discard)));
        }
        Assert.Equal(GameState.MaxWondersBuilt, s.TotalWondersBuilt);
        Assert.True(checkedAfterSeventh > 0);
        Assert.All(GameState.WondersIn(s.RemovedWonders), w => Assert.False(s.IsWonderBuilt(w)));
    }

    [Fact]
    public void Mistake_08_play_again_comes_after_the_flips_never_stacks_and_is_lost_at_the_end_of_an_age()
    {
        // After the flips: taking the Baths uncovers 3.0, revealed before P1's extra turn.
        var game = Start(Setup.DraftFor("sphinx"));
        var s = game.State;
        s.TakeToken(0, ProgressTokens.Theology); // does not give the Sphinx a second extra turn
        Do(game, "P1 discard apothecary", "P2 discard quarry", "P1 wonder baths sphinx");
        Assert.Equal(StepKind.Reveal, s.Step);
        Do(game, "reveal 3.0 workshop");
        Assert.Equal(0, s.PlayerToDecide);
        Do(game, "P1 discard workshop");
        Assert.Equal(1, s.PlayerToDecide);

        // Lost at the end of an Age: P2 builds the Hanging Gardens with the 20th card.
        game = Start(Setup.Draft(0, ["sphinx", "pyramids", "colossus", "great-library"],
            ["hanging-gardens", "appian-way", "piraeus", "mausoleum"]));
        s = game.State;
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
        Do(game, "P2 start opponent");
        game.Apply(s.LegalMoves().Last(m => m.Kind == MoveKind.Discard));
        Assert.Equal(1, s.PlayerToDecide);
    }

    [Fact]
    public void Mistake_09_strategy_adds_a_shield_to_later_red_cards_only_never_to_wonders()
    {
        var game = Start(Setup.DraftFor("colossus"));
        game.State.TakeToken(0, ProgressTokens.Strategy);
        Do(game, "P1 wonder quarry colossus");
        Assert.Equal(2, game.State.ConflictPawn);

        var (redGame, card) = ScienceTests.PlayUntilAccessible(c => c.Color == CardColor.Red);
        var s = redGame.State;
        var me = s.PlayerToDecide;
        s.TakeToken(me, ProgressTokens.Strategy);
        var lead = s.MilitaryLead(me);
        s.Apply(Move.Build(card));
        Assert.Equal(lead + GameData.Cards[card].Shields + 1, s.MilitaryLead(me));

        // Red cards built before the token stay as they were.
        var fresh = Position(["garrison"], []);
        fresh.TakeToken(0, ProgressTokens.Strategy);
        Assert.Equal(0, fresh.ConflictPawn);
    }

    [Fact]
    public void Mistake_10_economy_takes_only_trading_coins_actually_paid_by_the_opponent()
    {
        var game = Start(Setup.Draft(0));
        var s = game.State;
        s.TakeToken(1, ProgressTokens.Economy);
        Give(s, 0, "customs-house"); // Glass and Papyrus cost P1 1 coin

        Do(game, "P1 build logging-camp"); // $1 printed: to the bank
        Assert.Equal((49, 50), (s.Coins(0), s.Coins(1)));
        Do(game, "P2 build baths"); // P2's own trading (Stone, 2) goes to the bank
        Assert.Equal((49, 48), (s.Coins(0), s.Coins(1)));
        Do(game, "reveal 3.1 workshop", "P1 build apothecary"); // Glass bought for 1 through the Customs House
        Assert.Equal((48, 49), (s.Coins(0), s.Coins(1)));
    }

    [Fact]
    public void Mistake_11_looting_happens_once_is_capped_and_gives_the_attacker_nothing()
    {
        var s = GameState.New(Ruleset.Default);
        s.SetCoins(1, 1);
        s.AddShields(0, 3);
        Assert.Equal(0, s.Coins(1)); // capped at what P2 had
        Assert.Equal(GameState.StartingCoins, s.Coins(0));
        Assert.False(s.MilitaryTokenLeft(GameState.MilitaryToken(1, false)));

        // Retreating and advancing again does not loot again.
        s.SetCoins(1, 10);
        s.AddShields(1, 3);
        s.AddShields(0, 3);
        Assert.Equal(3, s.ConflictPawn);
        Assert.Equal(10, s.Coins(1));
    }

    [Fact]
    public void Mistake_12_military_vp_follow_where_the_pawn_ends()
    {
        var s = GameState.New(Ruleset.Default);
        s.AddShields(0, 4);
        Assert.False(s.MilitaryTokenLeft(GameState.MilitaryToken(1, false)));
        Assert.Equal(5, Scoring.Score(s, 0)[ScoreCategory.Military]); // the token is gone, the zone's VP stay
        s.AddShields(1, 2);
        Assert.Equal(2, Scoring.Score(s, 0)[ScoreCategory.Military]);
        Assert.Equal(0, Scoring.Score(s, 1)[ScoreCategory.Military]);
    }

    [Fact]
    public void Mistake_13_pairs_take_board_tokens_from_any_route_and_the_library_draws_from_the_box()
    {
        var game = Start(Setup.DraftFor("mausoleum", "great-library"));
        var s = game.State;
        Give(s, 0, "workshop");
        s.AddToDiscard(C("laboratory")); // the same symbol as the Workshop

        // A pair formed by a Mausoleum build still earns a token, from the board only.
        Do(game, "P1 wonder quarry mausoleum", "P1 mausoleum laboratory");
        Assert.Equal(new PendingDecision(DecisionKind.ChooseToken, 0), s.Pending(0));
        Assert.Equal(ProgressTokens.In(s.BoardTokens).Select(Move.PickToken), s.LegalMoves());
        Do(game, "P1 token law");

        // The Great Library draws from the 5 tokens left in the box, never the board's.
        Do(game, "P2 discard stone-reserve", "P1 wonder apothecary great-library");
        Assert.Throws<InvalidOperationException>(() => Do(game, "reveal P1 library agriculture philosophy theology"));
        Do(game, "reveal P1 library architecture philosophy theology");
        Assert.Equal(3, s.LegalMoves().Count);
    }

    [Fact]
    public void Mistake_14_supremacy_ends_the_game_in_the_middle_of_a_move()
    {
        var game = Start(Setup.DraftFor("colossus"));
        var s = game.State;
        s.TakeToken(0, ProgressTokens.Theology);
        s.SetConflictPawn(7);
        Do(game, "P1 wonder quarry colossus");
        Assert.True(s.IsOver);
        Assert.Equal((0, VictoryType.Military), (s.Result!.Winner, s.Result.Victory));
        Assert.False(s.PlayAgain);
        Assert.Equal(0, s.PendingCount);
    }

    [Fact]
    public void Mistake_15_guilds_compare_coins_at_build_and_vp_at_the_end_separately()
    {
        // Shipowners counts brown and grey together in one city: max(3, 3), not max brown 3 + max grey 2.
        var s = Position(["lumber-yard", "quarry", "clay-pool"], ["stone-pit", "glassworks", "press"]);
        Assert.Equal(3, Scoring.GuildCoins(s, GuildKind.Shipowners));
        Assert.Equal(3, Scoring.GuildPoints(s, GuildKind.Shipowners));

        // Magistrates: P2 has the most blue when the Guild is built; P1 has the most at the end.
        s = Position(["theater"], ["altar", "baths"]);
        Assert.Equal(2, Scoring.GuildCoins(s, GuildKind.Magistrates));
        Give(s, 0, "temple", "statue");
        Assert.Equal(3, Scoring.GuildPoints(s, GuildKind.Magistrates));

        Assert.Equal(0, Scoring.GuildCoins(s, GuildKind.Moneylenders));
        Assert.Equal(0, Scoring.GuildCoins(s, GuildKind.Builders));
    }

    [Fact]
    public void Mistake_16_lighthouse_counts_itself_arena_counts_wonders_built_now_and_both_pay_once()
    {
        var game = Start(Setup.DraftFor("mausoleum"));
        var s = game.State;
        Give(s, 0, "wood-reserve");
        s.AddToDiscard(C("lighthouse"));
        Do(game, "P1 wonder quarry mausoleum");
        var before = s.Coins(0);
        Do(game, "P1 mausoleum lighthouse");
        Assert.Equal(before + 2, s.Coins(0)); // Wood Reserve and the Lighthouse itself

        game = Start(Setup.DraftFor("mausoleum", "pyramids"));
        s = game.State;
        s.AddToDiscard(C("arena"));
        Do(game, "P1 wonder quarry mausoleum");
        before = s.Coins(0);
        Do(game, "P1 mausoleum arena");
        Assert.Equal(before + 2, s.Coins(0)); // 1 Wonder built so far
        Do(game, "P2 discard stone-reserve");
        before = s.Coins(0) - Payment.QuoteWonder(s, 0, W("pyramids")).Total;
        Do(game, "P1 wonder apothecary pyramids");
        Assert.Equal(before, s.Coins(0)); // a second Wonder pays nothing more
    }

    [Fact]
    public void Mistake_17_accessibility_is_per_slot_and_cards_flip_only_when_accessible()
    {
        // Age II edge cards and Age III row-3 cards (the reference counts rows from 1; slots 5-8 here, row 2 from 0)
        // are covered by one card only.
        var two = GameData.Layout(2);
        foreach (var row in Enumerable.Range(0, two.RowCount - 1))
        {
            var slots = two.Slots.Where(x => x.Row == row).OrderBy(x => x.Col).ToArray();
            Assert.Single(slots[0].CoveredBy);
            Assert.Single(slots[^1].CoveredBy);
        }
        Assert.All(GameData.Layout(3).Slots.Where(x => x.Row == 2), x => Assert.Single(x.CoveredBy));

        // Age I 3.0 is covered by 4.0 and 4.1: it stays face down until both are gone.
        var game = Start(Setup.Draft(0));
        var s = game.State;
        var slot = s.Layout.SlotAt(3, 0);
        Do(game, "P1 discard apothecary");
        Assert.False(s.IsAccessible(slot));
        Assert.Equal(0u, s.SlotsToReveal);
        Assert.Equal(GameState.Hidden, s.SlotCard(slot));
        Do(game, "P2 discard baths");
        Assert.True(s.IsAccessible(slot));
        Assert.Equal(1u << slot, s.SlotsToReveal);
    }

    [Fact]
    public void Mistake_18_face_down_guilds_show_a_guild_back()
    {
        var game = new Game(Ruleset.Default);
        game.Apply(new FirstPlayerReveal(0));
        var chance = new RandomChanceSource(4);
        var s = game.State;
        while (s.Age < 3)
        {
            if (s.Step == StepKind.Reveal) game.Apply(chance.Next(s));
            else if (s.Pending(0).Kind == DecisionKind.TakeCard) game.Apply(s.LegalMoves().Last(m => m.Kind == MoveKind.Discard));
            else game.Apply(s.LegalMoves()[0]);
        }
        var faceDown = s.Layout.Slots.Where(x => !x.FaceUp).Select(x => x.Index).ToArray();
        var guildSlots = faceDown.Take(GameState.GuildsPerGame).ToArray();
        game.Apply(new GuildPatternReveal(guildSlots));
        while (s.Step == StepKind.Reveal) game.Apply(chance.Next(s));

        var seen = s.Observe(0).State;
        foreach (var slot in faceDown)
        {
            var guild = guildSlots.Contains(slot);
            Assert.Equal(guild ? GameState.HiddenGuild : GameState.Hidden, seen.SlotCard(slot));
            Assert.Equal(guild ? CardSets.Guilds : CardSets.OfAge[3] & ~s.Seen, seen.UnseenPoolFor(slot));
        }
    }

    [Fact]
    public void Mistake_19_the_second_player_opens_draft_round_two_and_the_first_player_starts_age_one()
    {
        var game = new Game(Ruleset.Default);
        game.Apply(new FirstPlayerReveal(1));
        Do(game, Tokens);
        Assert.Equal([1, 0, 0, 1, 0, 1, 1, 0], Enumerable.Range(0, GameState.DraftSize).Select(game.State.DraftPicker));
        Do(game, [.. Setup.Draft(1), Opening]);
        Assert.Equal(1, game.State.CurrentPlayer);
        Assert.Equal(1, game.State.PlayerToDecide);
    }

    [Fact]
    public void Mistake_20_the_tiebreak_counts_blue_vp_only_and_an_exact_tie_is_a_draw()
    {
        // 8 VP each: P1 Obelisk 5 + Academy 3; P2 Palace 7 + 3 coins (1 VP). P2 has more blue VP.
        var s = Position(["obelisk", "academy"], ["palace"]);
        s.SetCoins(0, 0);
        s.SetCoins(1, 3);
        var result = Scoring.Civilian(s);
        Assert.Equal(result.P1.Total, result.P2.Total);
        Assert.Equal(1, result.Winner);

        // Same total and same blue VP: a draw.
        s = Position(["obelisk"], ["senate"]);
        Assert.True(Scoring.Civilian(s).IsDraw);
    }
}
