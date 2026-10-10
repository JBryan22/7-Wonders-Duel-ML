using SevenWondersDuel.Engine.Data;

namespace SevenWondersDuel.Engine.Tests;

/// <summary>
/// Science (rules-reference §6, §12 mistakes 13 and 14): the Progress tokens set out at setup, symbol pairs and the
/// compulsory token pick (spec A3), Law as the 7th symbol, and science supremacy on 6 different symbols.
/// </summary>
public class ScienceTests
{
    internal const string Tokens = "reveal tokens agriculture economy law strategy urbanism";

    static int C(string slug) => GameData.CardIndex(slug);
    static int T(string slug) => GameData.TokenIndex(slug);

    /// <summary>A card of Age II or III for each symbol: never in the Age I layout, so tests can put it in a city.</summary>
    static int LaterCard(ScienceSymbol symbol) =>
        CardSets.OfSymbol[(int)symbol].Cards().First(c => GameData.Cards[c].Age >= 2);

    static readonly ScienceSymbol[] CardSymbols = Enum.GetValues<ScienceSymbol>().Where(s => s != ScienceSymbol.Law).ToArray();

    /// <summary>
    /// A real game (P1 first, <paramref name="tokens"/> on the board), discarding until the player to decide has an
    /// accessible card matching <paramref name="pick"/> at a take-card decision. That player gets 50 coins.
    /// </summary>
    internal static (Game Game, int Card) PlayUntilAccessible(Func<CardDef, bool> pick, string tokens = Tokens, ulong seed = 1)
    {
        var game = new Game(Ruleset.Default);
        game.Apply(new FirstPlayerReveal(0));
        game.Apply(MoveLog.ParseEntry(tokens, game.State));
        var chance = new RandomChanceSource(seed);
        while (true)
        {
            var s = game.State;
            if (s.IsOver) throw new InvalidOperationException("No matching card became accessible");
            if (s.Step == StepKind.Reveal) { game.Apply(chance.Next(s)); continue; }
            if (s.Pending(0).Kind == DecisionKind.ChooseStarter) { game.Apply(Move.StartMe); continue; }
            var card = Enumerable.Range(0, GameState.SlotCount).Where(s.IsAccessible).Select(s.SlotCard)
                .Where(c => c >= 0 && pick(GameData.Cards[c])).DefaultIfEmpty(-1).First();
            if (card >= 0)
            {
                s.SetCoins(s.PlayerToDecide, 50);
                return (game, card);
            }
            game.Apply(s.LegalMoves().Last(m => m.Kind == MoveKind.Discard));
        }
    }

    static (Game Game, int Card) AgeOneGreen(string tokens = Tokens) =>
        PlayUntilAccessible(c => c.Age == 1 && c.Science is not null, tokens);

    /// <summary>Supplies Reveals until the next decision.</summary>
    static void RevealAll(Game game)
    {
        var chance = new RandomChanceSource(9);
        while (game.State.Step == StepKind.Reveal) game.Apply(chance.Next(game.State));
    }

    // ---------------------------------------------------------------- setup

    [Fact]
    public void Five_tokens_are_set_out_after_the_first_player_and_the_rest_stay_in_the_box()
    {
        var game = new Game(Ruleset.Default);
        game.Apply(new FirstPlayerReveal(0));
        Assert.True(game.State.AwaitingTokens);
        var reveal = new RandomChanceSource(4).Next(game.State);
        var tokens = Assert.IsType<TokenReveal>(reveal).Tokens;
        Assert.Equal(ProgressTokens.OnBoard, tokens.Distinct().Count());
        game.Apply(reveal);

        var s = game.State;
        Assert.Equal(ProgressTokens.OnBoard, System.Numerics.BitOperations.PopCount(s.BoardTokens));
        Assert.Equal(s.BoardTokens, s.TokensSetOut);
        Assert.Equal(ProgressTokens.All, (ushort)(s.TokensSetOut | s.TokensSetAside));
        Assert.Equal(0, s.TokensSetOut & s.TokensSetAside);
        Assert.All(tokens, t => Assert.True(s.TokenOnBoard(t)));
        Assert.Equal(1, s.Age);
        Assert.Equal(0, s.Tokens(0) | s.Tokens(1));
    }

    [Theory]
    [InlineData("reveal tokens agriculture economy law strategy", "5 different")]
    [InlineData("reveal tokens agriculture economy law strategy strategy", "5 different")]
    [InlineData("reveal tokens agriculture economy law strategy pottery", "Unknown Progress token")]
    public void Bad_token_reveals_are_rejected(string reveal, string reason)
    {
        var e = Assert.Throws<FormatException>(() => MoveLog.Parse($"7wd-log v1\nrules base\nreveal first P1\n{reveal}\n"));
        Assert.Contains(reason, e.Message);
    }

    [Fact]
    public void Tokens_cannot_be_revealed_before_the_first_player_or_twice()
    {
        var s = GameState.New(Ruleset.Default);
        Assert.Throws<InvalidOperationException>(() => s.Apply(new TokenReveal([0, 1, 2, 3, 4])));
        s.Apply(new FirstPlayerReveal(0));
        s.Apply(new TokenReveal([0, 1, 2, 3, 4]));
        Assert.Throws<InvalidOperationException>(() => s.Apply(new TokenReveal([5, 6, 7, 8, 9])));
    }

    // ---------------------------------------------------------------- symbols

    [Fact]
    public void Every_card_symbol_appears_on_exactly_two_green_cards_and_law_on_none()
    {
        Assert.All(CardSymbols, sym => Assert.Equal(2, CardSets.OfSymbol[(int)sym].Count));
        Assert.True(CardSets.OfSymbol[(int)ScienceSymbol.Law].IsEmpty);
        Assert.Equal(ScienceSymbol.Law, GameData.Tokens[ProgressTokens.Law].Science);
    }

    [Fact]
    public void Law_is_a_seventh_symbol_that_counts_toward_supremacy_but_never_pairs()
    {
        var s = GameState.New(Ruleset.Default);
        s.AddToCity(0, C("workshop"));
        s.AddToCity(0, C("laboratory"));
        Assert.Equal(2, s.SymbolCount(0, ScienceSymbol.Compass));
        Assert.Equal(1, s.DistinctSymbols(0));
        s.TakeToken(0, ProgressTokens.Law);
        Assert.Equal(1, s.SymbolCount(0, ScienceSymbol.Law));
        Assert.Equal(2, s.DistinctSymbols(0));
        Assert.Equal(0, s.DistinctSymbols(1));
        Assert.Equal(0, s.PendingCount);
    }

    // ---------------------------------------------------------------- pairs and the token pick

    [Fact]
    public void A_green_card_with_a_new_symbol_gives_no_token_and_the_turn_passes()
    {
        var (game, card) = AgeOneGreen();
        var s = game.State;
        var me = s.PlayerToDecide;
        game.Apply(Move.Build(card));
        RevealAll(game);
        Assert.Equal(1, s.SymbolCount(me, GameData.Cards[card].Science!.Value));
        Assert.Equal(DecisionKind.TakeCard, s.Pending(0).Kind);
        Assert.Equal(1 - me, s.PlayerToDecide);
    }

    [Fact]
    public void A_science_pair_makes_the_same_player_take_a_token_from_the_board_and_it_cannot_be_declined()
    {
        var (game, card) = AgeOneGreen();
        var s = game.State;
        var me = s.PlayerToDecide;
        s.AddToCity(me, LaterCard(GameData.Cards[card].Science!.Value));
        game.Apply(Move.Build(card));
        RevealAll(game);

        Assert.Equal(new PendingDecision(DecisionKind.ChooseToken, me), s.Pending(0));
        Assert.Equal(1, s.PendingCount);
        var moves = s.LegalMoves();
        Assert.Equal(ProgressTokens.In(s.BoardTokens).Select(Move.PickToken), moves);
        Assert.Equal(ProgressTokens.OnBoard, moves.Count);
        // No pass and no card move: the pick is compulsory while any token remains.
        var other = Enumerable.Range(0, GameState.SlotCount).First(s.IsAccessible);
        Assert.Contains("cannot be declined", s.WhyIllegal(Move.Discard(s.SlotCard(other))));
        Assert.Contains("not on the board", s.WhyIllegal(Move.PickToken(T("philosophy"))));

        var coins = s.Coins(me);
        game.Apply(Move.PickToken(T("agriculture")));
        Assert.True(s.HasToken(me, T("agriculture")));
        Assert.False(s.TokenOnBoard(T("agriculture")));
        Assert.Equal(coins + 6, s.Coins(me));
        Assert.Equal(new PendingDecision(DecisionKind.TakeCard, 1 - me), s.Pending(0));
        Assert.Contains($"P{me + 1} token agriculture", game.WriteLog());
        Assert.Equal(new MoveEntry(me, Move.PickToken(T("agriculture"))), MoveLog.ParseEntry($"P{me + 1} token agriculture", s));
    }

    [Fact]
    public void A_pair_with_no_tokens_left_on_the_board_gives_nothing()
    {
        var (game, card) = AgeOneGreen();
        var s = game.State;
        var me = s.PlayerToDecide;
        s.SetBoardTokens(0);
        s.AddToCity(me, LaterCard(GameData.Cards[card].Science!.Value));
        game.Apply(Move.Build(card));
        RevealAll(game);
        Assert.Equal(2, s.SymbolCount(me, GameData.Cards[card].Science!.Value));
        Assert.Equal(new PendingDecision(DecisionKind.TakeCard, 1 - me), s.Pending(0));
    }

    // ---------------------------------------------------------------- science supremacy

    [Fact]
    public void Six_different_symbols_win_at_once_by_science()
    {
        var (game, card) = AgeOneGreen();
        var s = game.State;
        var me = s.PlayerToDecide;
        var symbol = GameData.Cards[card].Science!.Value;
        foreach (var sym in CardSymbols.Where(x => x != symbol)) s.AddToCity(me, LaterCard(sym));
        Assert.Equal(5, s.DistinctSymbols(me));
        Assert.False(s.IsOver);

        game.Apply(Move.Build(card));
        Assert.True(s.IsOver);
        Assert.Equal(StepKind.GameOver, s.Step);
        Assert.Equal(me, s.Result!.Winner);
        Assert.Equal(VictoryType.Science, s.Result.Victory);
        Assert.Equal(0, s.PendingCount);
        Assert.Empty(s.LegalMoves());
        Assert.Equal(Scoring.Score(s, me), s.Result.Score(me));
    }

    [Fact]
    public void Five_different_symbols_do_not_win()
    {
        var (game, card) = AgeOneGreen();
        var s = game.State;
        var me = s.PlayerToDecide;
        var symbol = GameData.Cards[card].Science!.Value;
        foreach (var sym in CardSymbols.Where(x => x != symbol).Take(4)) s.AddToCity(me, LaterCard(sym));
        game.Apply(Move.Build(card));
        Assert.Equal(5, s.DistinctSymbols(me));
        Assert.False(s.IsOver);
    }

    [Fact]
    public void Law_taken_after_a_pair_can_complete_science_supremacy()
    {
        var (game, card) = AgeOneGreen();
        var s = game.State;
        var me = s.PlayerToDecide;
        var symbol = GameData.Cards[card].Science!.Value;
        s.AddToCity(me, LaterCard(symbol));
        foreach (var sym in CardSymbols.Where(x => x != symbol).Take(4)) s.AddToCity(me, LaterCard(sym));
        game.Apply(Move.Build(card));
        RevealAll(game);
        Assert.Equal(5, s.DistinctSymbols(me));
        Assert.Equal(DecisionKind.ChooseToken, s.Pending(0).Kind);

        game.Apply(Move.PickToken(ProgressTokens.Law));
        Assert.True(s.IsOver);
        Assert.Equal(me, s.Result!.Winner);
        Assert.Equal(VictoryType.Science, s.Result.Victory);
        Assert.Equal(0, s.PendingCount);
    }

    [Fact]
    public void Another_token_after_the_same_pair_does_not_win_with_five_symbols()
    {
        var (game, card) = AgeOneGreen();
        var s = game.State;
        var me = s.PlayerToDecide;
        var symbol = GameData.Cards[card].Science!.Value;
        s.AddToCity(me, LaterCard(symbol));
        foreach (var sym in CardSymbols.Where(x => x != symbol).Take(4)) s.AddToCity(me, LaterCard(sym));
        game.Apply(Move.Build(card));
        RevealAll(game);
        game.Apply(Move.PickToken(T("strategy")));
        Assert.False(s.IsOver);
        Assert.Equal(1 - me, s.PlayerToDecide);
    }
}
