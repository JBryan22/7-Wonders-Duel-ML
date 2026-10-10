using SevenWondersDuel.Engine.Data;

namespace SevenWondersDuel.Engine.Tests;

/// <summary>
/// The Wonders whose power asks for a choice (rules-reference §7, §12): the Statue of Zeus and Circus Maximus
/// destroy, the Mausoleum's free build from the discard pile, and the Great Library's private draw.
/// </summary>
public class ChoiceWonderTests
{
    /// <summary>Set out on the board; the box (the Great Library's pool) holds architecture masonry mathematics philosophy theology.</summary>
    const string Tokens = "reveal tokens agriculture economy law strategy urbanism";
    /// <summary>Age I: row 4 (0-based) is accessible; slot 3.k is uncovered once 4.k and 4.(k+1) are both taken.</summary>
    const string Opening =
        "reveal age1 0.0 palisade 0.1 tavern 2.0 guard-tower 2.1 altar 2.2 lumber-yard 2.3 pharmacist "
        + "4.0 apothecary 4.1 baths 4.2 logging-camp 4.3 quarry 4.4 glassworks 4.5 stone-reserve";

    static int C(string slug) => GameData.CardIndex(slug);
    static int T(string slug) => GameData.TokenIndex(slug);

    /// <summary>A game with P1 first, the given draft and the Age I opening; both players get 50 coins.</summary>
    static Game Start(string[] draft, string tokens = Tokens)
    {
        var game = MoveLog.Parse(string.Join('\n', ["7wd-log v1", "rules base", "reveal first P1", tokens, .. draft, Opening]));
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

    // ---------------------------------------------------------------- Statue of Zeus and Circus Maximus

    [Fact]
    public void The_statue_of_zeus_sends_a_brown_card_of_the_opponents_choice_to_the_discard_pile()
    {
        var game = Start(Setup.DraftFor("statue-of-zeus"));
        var s = game.State;
        Give(s, 1, "clay-pit", "stone-pit", "press");
        Give(s, 0, "clay-pool");
        Assert.Equal(3, Payment.TradingPrice(s, 0, Resource.Clay));

        Do(game, "P1 wonder quarry statue-of-zeus");
        Assert.Equal(StepKind.Decision, s.Step);
        Assert.Equal(new PendingDecision(DecisionKind.Destroy, 0, GameData.WonderIndex("statue-of-zeus")), s.Pending(0));
        Assert.Equal(CardColor.Brown, s.DestroyColor);
        Assert.Equal(1, s.ConflictPawn); // the shield came first
        // Only the opponent's brown cards: not its grey Press, not P1's own Clay Pool.
        Assert.Equal(new[] { Move.Destroy(C("clay-pit")), Move.Destroy(C("stone-pit")) }, s.LegalMoves());

        Do(game, "P1 destroy clay-pit");
        Assert.False(s.City(1).Contains(C("clay-pit")));
        Assert.True(s.DiscardPile.Contains(C("clay-pit")));
        // The cached production drops, and with it P1's trading price (rules-reference §12 mistake 2).
        Assert.Equal(0, s.Production(1, Resource.Clay));
        Assert.Equal(1, s.Production(1, Resource.Stone));
        Assert.Equal(2, Payment.TradingPrice(s, 0, Resource.Clay));
        Assert.Equal(new PendingDecision(DecisionKind.TakeCard, 1), s.Pending(0));
        Assert.Null(s.DestroyColor);
        Assert.Contains("P1 destroy clay-pit", game.WriteLog());
    }

    [Fact]
    public void Circus_maximus_sends_a_grey_card_to_the_discard_pile()
    {
        var game = Start(Setup.Draft(0)); // P2 owns Circus Maximus
        var s = game.State;
        Give(s, 0, "press", "glass-blower", "clay-pool");
        Do(game, "P1 discard apothecary", "P2 wonder quarry circus-maximus");
        Assert.Equal(1, s.PlayerToDecide);
        Assert.Equal(CardColor.Grey, s.DestroyColor);
        Assert.Equal(new[] { Move.Destroy(C("press")), Move.Destroy(C("glass-blower")) }, s.LegalMoves());

        Do(game, "P2 destroy glass-blower");
        Assert.Equal(0, s.Production(0, Resource.Glass));
        Assert.Equal(1, s.Production(0, Resource.Papyrus));
        Assert.Equal(1, s.Production(0, Resource.Clay));
        Assert.Equal(-1, s.ConflictPawn);
        Assert.True(s.DiscardPile.Contains(C("glass-blower")));
        Assert.Equal(0, s.PlayerToDecide);
    }

    [Fact]
    public void A_destroy_names_an_opponents_card_of_the_right_colour()
    {
        var game = Start(Setup.DraftFor("statue-of-zeus"));
        var s = game.State;
        Give(s, 1, "clay-pit", "press");
        Give(s, 0, "clay-pool");
        Do(game, "P1 wonder quarry statue-of-zeus");
        Assert.Contains("not a brown card in P2's city", s.WhyIllegal(Move.Destroy(C("press"))));
        Assert.Contains("not a brown card in P2's city", s.WhyIllegal(Move.Destroy(C("clay-pool"))));
        Assert.Contains("which brown card to destroy", s.WhyIllegal(Move.Discard(C("baths"))));
        Assert.Throws<InvalidOperationException>(() => s.Apply(Move.Destroy(C("press"))));
    }

    [Theory]
    [InlineData("statue-of-zeus")]
    [InlineData("circus-maximus")]
    [InlineData("mausoleum")]
    public void A_power_with_nothing_to_choose_is_skipped_silently(string wonder)
    {
        // The opponent has no brown or grey card (only a yellow one) and the discard pile is empty.
        var game = Start(Setup.DraftFor(wonder));
        var s = game.State;
        Give(s, 1, "stone-reserve");
        Do(game, $"P1 wonder quarry {wonder}");
        Assert.Equal(1, s.PendingCount);
        Assert.Equal(new PendingDecision(DecisionKind.TakeCard, 1), s.Pending(0));
        Assert.Equal(1, s.City(1).Count);
        Assert.Equal($"P1 wonder quarry {wonder}", game.Log[^1].ToString()); // nothing logged after the Wonder
    }

    [Fact]
    public void A_destroy_comes_after_the_flips_and_before_a_theology_play_again()
    {
        var game = Start(Setup.DraftFor("statue-of-zeus"));
        var s = game.State;
        s.TakeToken(0, ProgressTokens.Theology);
        Give(s, 1, "clay-pit");
        Do(game, "P1 discard apothecary", "P2 discard quarry", "P1 wonder baths statue-of-zeus");
        Assert.Equal(StepKind.Reveal, s.Step); // the Baths uncovered 3.0
        Assert.Equal(DecisionKind.Destroy, s.Pending(0).Kind);
        Assert.True(s.PlayAgain);

        Do(game, "reveal 3.0 workshop");
        Assert.Equal(0, s.PlayerToDecide);
        Do(game, "P1 destroy clay-pit");
        Assert.Equal(new PendingDecision(DecisionKind.TakeCard, 0), s.Pending(0));
        Assert.False(s.PlayAgain);
    }

    // ---------------------------------------------------------------- the Mausoleum

    [Fact]
    public void The_mausoleum_builds_any_discarded_or_destroyed_card_for_free_but_never_a_card_under_a_wonder()
    {
        var game = Start(Setup.DraftFor("statue-of-zeus", "mausoleum"));
        var s = game.State;
        Give(s, 1, "clay-pit");
        Do(game,
            "P1 discard apothecary",
            "P2 discard logging-camp",
            "P1 wonder glassworks statue-of-zeus",
            "P1 destroy clay-pit",
            "P2 build stone-reserve",
            "reveal 3.4 press",
            "P1 wonder press mausoleum");

        // Discarded and destroyed cards are in the pile; the Age cards under Wonders and built cards are not
        // (rules-reference §12 mistake 6), nor are the cards removed at setup.
        Assert.Equal(CardSet.Of([C("apothecary"), C("logging-camp"), C("clay-pit")]), s.DiscardPile);
        Assert.Equal(new PendingDecision(DecisionKind.Mausoleum, 0, GameData.WonderIndex("mausoleum")), s.Pending(0));
        Assert.Equal(new[] { Move.Mausoleum(C("logging-camp")), Move.Mausoleum(C("clay-pit")), Move.Mausoleum(C("apothecary")) }.OrderBy(m => m.Card),
            s.LegalMoves());
        Assert.Contains("not in the discard pile", s.WhyIllegal(Move.Mausoleum(C("glassworks"))));
        Assert.Contains("not in the discard pile", s.WhyIllegal(Move.Mausoleum(C("stone-reserve"))));

        var coins = s.Coins(0);
        Do(game, "P1 mausoleum clay-pit"); // costs $1 normally
        Assert.Equal(coins, s.Coins(0));
        Assert.True(s.City(0).Contains(C("clay-pit")));
        Assert.Equal(1, s.Production(0, Resource.Clay));
        Assert.Equal(CardSet.Of([C("apothecary"), C("logging-camp")]), s.DiscardPile);
        Assert.Equal(new PendingDecision(DecisionKind.TakeCard, 1), s.Pending(0));
        Assert.Contains("P1 mausoleum clay-pit", game.WriteLog());
    }

    [Fact]
    public void A_mausoleum_build_applies_the_cards_effects_including_a_science_pair()
    {
        var game = Start(Setup.DraftFor("mausoleum"));
        var s = game.State;
        Give(s, 0, "school"); // a wheel
        Do(game, "P1 discard glassworks", "P2 discard apothecary", "P1 wonder logging-camp mausoleum");
        var coins = s.Coins(0);
        Do(game, "P1 mausoleum apothecary"); // costs G normally
        Assert.Equal(coins, s.Coins(0));
        Assert.Equal(2, s.SymbolCount(0, ScienceSymbol.Wheel));
        Assert.Equal(new PendingDecision(DecisionKind.ChooseToken, 0), s.Pending(0));
        Do(game, "P1 token agriculture");
        Assert.Equal(coins + 6, s.Coins(0));
        Assert.Equal(1, s.PlayerToDecide);
    }

    [Fact]
    public void A_mausoleum_build_can_win_by_science()
    {
        var game = Start(Setup.DraftFor("mausoleum"));
        var s = game.State;
        Give(s, 0, "workshop", "scriptorium", "dispensary", "academy", "university"); // 5 different symbols
        Do(game, "P1 discard glassworks", "P2 discard apothecary", "P1 wonder logging-camp mausoleum", "P1 mausoleum apothecary");
        Assert.True(s.IsOver);
        Assert.Equal(VictoryType.Science, s.Result!.Victory);
        Assert.Equal(0, s.Result.Winner);
        Assert.Equal(0, s.PendingCount);
    }

    [Fact]
    public void A_mausoleum_build_pays_the_cards_coins_but_never_urbanism_even_for_a_chained_card()
    {
        var game = Start(Setup.DraftFor("mausoleum"));
        var s = game.State;
        s.TakeToken(0, ProgressTokens.Urbanism);
        Give(s, 0, "brewery");
        s.AddToDiscard(C("arena")); // chained from the Brewery; 2 coins per Wonder built
        Do(game, "P1 wonder quarry mausoleum");
        var coins = s.Coins(0);
        Do(game, "P1 mausoleum arena");
        // Urbanism's +4 is for chain builds only, not Mausoleum builds (rules-reference §12 mistake 4).
        Assert.Equal(coins + 2, s.Coins(0));
        Assert.True(s.City(0).Contains(C("arena")));
    }

    // ---------------------------------------------------------------- the Great Library

    [Fact]
    public void The_great_library_draws_three_box_tokens_privately_and_keeps_one()
    {
        var game = Start(Setup.DraftFor("great-library"));
        var s = game.State;
        Do(game, "P1 wonder quarry great-library");
        Assert.Equal(StepKind.Reveal, s.Step);
        Assert.True(s.AwaitingLibraryDraw);
        Assert.Equal(new PendingDecision(DecisionKind.Library, 0, GameData.WonderIndex("great-library")), s.Pending(0));
        Assert.Empty(s.LegalMoves());

        var draw = Assert.IsType<LibraryReveal>(new RandomChanceSource(5).Next(s));
        Assert.Equal(0, draw.Player);
        Assert.Equal(0, draw.Audience);
        Assert.All(draw.Tokens, t => Assert.True((s.TokensSetAside & 1 << t) != 0));

        Do(game, "reveal P1 library mathematics philosophy theology");
        Assert.False(s.AwaitingLibraryDraw);
        Assert.Equal((ushort)(1 << T("mathematics") | 1 << T("philosophy") | 1 << T("theology")), s.LibraryDraw);
        Assert.Equal(new[] { Move.Library(T("mathematics")), Move.Library(T("philosophy")), Move.Library(T("theology")) }.OrderBy(m => m.Card), s.LegalMoves());
        Assert.Contains("not in the Great Library draw", s.WhyIllegal(Move.Library(T("masonry"))));
        Assert.Contains("which Great Library token to keep", s.WhyIllegal(Move.PickToken(T("law"))));

        var board = s.BoardTokens;
        Do(game, "P1 library theology");
        Assert.True(s.HasToken(0, ProgressTokens.Theology));
        Assert.Equal(0, s.LibraryDraw);
        Assert.Equal(board, s.BoardTokens);
        // Theology kept from the Library does not make the Library itself play again.
        Assert.Equal(new PendingDecision(DecisionKind.TakeCard, 1), s.Pending(0));
        Assert.Equal(4, Scoring.Score(s, 0)[ScoreCategory.Wonders]);

        var log = game.WriteLog();
        Assert.Contains("reveal P1 library mathematics philosophy theology\nP1 library theology", log);
        Assert.Equal(new MoveEntry(0, Move.Library(T("theology"))), MoveLog.ParseEntry("P1 library theology", s));
    }

    [Theory]
    [InlineData("reveal P2 library mathematics philosophy theology", "P1's, not P2's")]
    [InlineData("reveal P1 library law mathematics philosophy", "law was set out on the board")]
    [InlineData("reveal P1 library mathematics philosophy", "draws 3 different tokens")]
    [InlineData("reveal P1 library mathematics mathematics philosophy", "draws 3 different tokens")]
    public void A_great_library_draw_names_three_box_tokens_for_its_owner(string reveal, string reason)
    {
        var game = Start(Setup.DraftFor("great-library"));
        Do(game, "P1 wonder quarry great-library");
        var e = Assert.Throws<InvalidOperationException>(() => Do(game, reveal));
        Assert.Contains(reason, e.Message);
    }

    [Fact]
    public void A_great_library_draw_is_only_accepted_when_the_library_was_just_built()
    {
        var game = Start(Setup.DraftFor("great-library"));
        var e = Assert.Throws<InvalidOperationException>(() => Do(game, "reveal P1 library mathematics philosophy theology"));
        Assert.Contains("not waiting for a Great Library draw", e.Message);
    }

    [Fact]
    public void The_great_library_draw_comes_after_the_flips_and_its_pick_before_a_theology_play_again()
    {
        var game = Start(Setup.DraftFor("great-library"));
        var s = game.State;
        s.TakeToken(0, ProgressTokens.Agriculture);
        Do(game, "P1 discard apothecary", "P2 discard quarry", "P1 wonder baths great-library");
        Assert.Equal(1u << 9, s.SlotsToReveal);
        Assert.True(s.AwaitingLibraryDraw);
        var e = Assert.Throws<InvalidOperationException>(() => Do(game, "reveal P1 library mathematics philosophy theology"));
        Assert.Contains("revealed before the Great Library draw", e.Message);

        var chance = new RandomChanceSource(1);
        Assert.IsType<SlotReveal>(chance.Next(s));
        Do(game, "reveal 3.0 workshop");
        Assert.IsType<LibraryReveal>(chance.Next(s));
        Do(game, "reveal P1 library architecture masonry theology", "P1 library architecture");
        Assert.Equal(1, s.PlayerToDecide);
    }

    [Fact]
    public void The_great_library_with_theology_plays_again_after_the_pick()
    {
        var game = Start(Setup.DraftFor("great-library"));
        var s = game.State;
        s.TakeToken(0, ProgressTokens.Theology);
        Do(game, "P1 wonder quarry great-library", "reveal P1 library architecture masonry mathematics");
        Assert.True(s.PlayAgain);
        Do(game, "P1 library masonry");
        Assert.Equal(new PendingDecision(DecisionKind.TakeCard, 0), s.Pending(0));
        Assert.False(s.PlayAgain);
    }

    [Fact]
    public void Law_kept_from_the_great_library_can_win_by_science()
    {
        var game = Start(Setup.DraftFor("great-library"), "reveal tokens agriculture economy strategy theology urbanism");
        var s = game.State;
        Give(s, 0, "workshop", "scriptorium", "dispensary", "academy", "university");
        Do(game, "P1 wonder quarry great-library", "reveal P1 library law masonry philosophy", "P1 library law");
        Assert.True(s.IsOver);
        Assert.Equal(VictoryType.Science, s.Result!.Victory);
        Assert.Equal(0, s.Result.Winner);
        Assert.Equal(0, s.PendingCount);
    }
}
