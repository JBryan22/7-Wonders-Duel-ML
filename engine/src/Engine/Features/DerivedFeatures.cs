using SevenWondersDuel.Engine.Data;

namespace SevenWondersDuel.Engine;

/// <summary>
/// Board signals computed from a Game State, never stored in it (spec A4, Derived features): production and trading
/// price per resource, science symbols, VP so far, the Conflict Pawn's distance to each capital, and a few similar
/// counts. One module, shared by the Heuristic, the Encoding's derived block and the Advisor's reason tags, so each
/// computation exists once. Everything here is allocation-free and reads the state directly, so search can call it on
/// states it reuses with <see cref="GameState.CopyFrom"/>.
/// </summary>
public static class DerivedFeatures
{
    /// <summary>Wonders that produce one resource of a choice each turn once built, one bit per Wonder index.</summary>
    static readonly ushort ChoiceWonders = (ushort)GameData.Wonders
        .Where(w => w.ProduceChoice.Length > 0).Aggregate(0, (m, w) => m | 1 << w.Index);

    /// <summary>Per card: the cards it makes free (its chain targets), as a set.</summary>
    static readonly CardSet[] ChainsInto = GameData.Cards
        .Select(c => CardSet.Of(GameData.Cards.Where(t => t.FreeIf == c.Index).Select(t => t.Index)))
        .ToArray();

    /// <summary>Resources a player produces every turn from brown and grey cards (choice production not included).</summary>
    public static int Production(GameState s, int player, Resource r) => s.Production(player, r);

    /// <summary>A player's choice producers: Forum, Caravansery, the Great Lighthouse, Piraeus (each covers one unit a turn).</summary>
    public static int ChoiceProducers(GameState s, int player) =>
        (s.City(player) & CardSets.ChoiceProducers).Count
        + System.Numerics.BitOperations.PopCount((uint)(s.BuiltWonders(player) & ChoiceWonders));

    /// <summary>What one unit of <paramref name="r"/> costs <paramref name="player"/> to buy: 1 with a fixed-price card, else 2 + the opponent's production.</summary>
    public static int TradingPrice(GameState s, int player, Resource r) => Payment.TradingPrice(s, player, r);

    /// <summary>Different scientific symbols a player shows (Law included): 6 is science supremacy.</summary>
    public static int ScienceSymbols(GameState s, int player) => s.DistinctSymbols(player);

    /// <summary>Scientific symbols a player shows at least twice: each such pair earned a Progress token.</summary>
    public static int SciencePairs(GameState s, int player)
    {
        var n = 0;
        for (var sym = 0; sym < CardSets.OfSymbol.Length; sym++)
            if (s.SymbolCount(player, (ScienceSymbol)sym) >= 2) n++;
        return n;
    }

    /// <summary>True if building a card with this symbol would give the player a new symbol.</summary>
    public static bool IsNewSymbol(GameState s, int player, ScienceSymbol symbol) => s.SymbolCount(player, symbol) == 0;

    /// <summary>A player's VP if the game ended now by civilian count (every category, coins and military included).</summary>
    public static int Vp(GameState s, int player) => Scoring.Total(s, player);

    /// <summary>
    /// Spaces between the Conflict Pawn and <paramref name="player"/>'s capital: 9 at the start, 0 means the opponent
    /// won by military supremacy.
    /// </summary>
    public static int DistanceToCapital(GameState s, int player) =>
        Math.Max(0, GameState.CapitalDistance + s.MilitaryLead(player));

    /// <summary>
    /// Cards a player would get free through a chain that are still in play: not built by anyone, not discarded and
    /// not tucked under a Wonder (they may still be in the unseen pools or the layout).
    /// </summary>
    public static int OpenChains(GameState s, int player)
    {
        var gone = s.City(0) | s.City(1) | s.DiscardPile | s.CardsUnderWonders(0) | s.CardsUnderWonders(1);
        var targets = CardSet.Empty;
        foreach (var c in s.City(player).Cards())
            if (c < ChainsInto.Length) targets |= ChainsInto[c]; // Pantheon's Grand Temples chain into nothing
        return (targets & ~gone).Count;
    }

    // ---------------------------------------------------------------- the derived block as one vector

    /// <summary>Numbers per player in <see cref="Write"/>.</summary>
    public const int PerPlayer = 2 * Resources.Count + 6;
    /// <summary>Length of the vector <see cref="Write"/> fills: me, then the opponent, then the two capital distances.</summary>
    public const int Count = 2 * PerPlayer + 2;

    /// <summary>A name for every index of <see cref="Write"/> (the Encoding's manifest uses them).</summary>
    public static readonly string[] Names = BuildNames();

    static string[] BuildNames()
    {
        var names = new List<string>();
        foreach (var who in new[] { "me", "opp" })
        {
            foreach (var r in Enum.GetValues<Resource>()) names.Add($"{who}.production.{r.ToString().ToLowerInvariant()}");
            names.Add($"{who}.choice-producers");
            foreach (var r in Enum.GetValues<Resource>()) names.Add($"{who}.trading-price.{r.ToString().ToLowerInvariant()}");
            names.Add($"{who}.science-symbols");
            names.Add($"{who}.science-pairs");
            names.Add($"{who}.vp");
            names.Add($"{who}.wonders-built");
            names.Add($"{who}.open-chains");
        }
        names.Add("me.capital-distance");
        names.Add("opp.capital-distance");
        return [.. names];
    }

    /// <summary>
    /// Writes the derived features from <paramref name="me"/>'s point of view (spec A6: relative to the player to
    /// decide) as raw integers, in the order of <see cref="Names"/>. Scaling is the Encoding's job.
    /// </summary>
    public static void Write(GameState s, int me, Span<int> values)
    {
        if (values.Length < Count) throw new ArgumentException($"Need {Count} values", nameof(values));
        var i = 0;
        foreach (var p in (ReadOnlySpan<int>)[me, 1 - me])
        {
            for (var r = 0; r < Resources.Count; r++) values[i++] = Production(s, p, (Resource)r);
            values[i++] = ChoiceProducers(s, p);
            for (var r = 0; r < Resources.Count; r++) values[i++] = TradingPrice(s, p, (Resource)r);
            values[i++] = ScienceSymbols(s, p);
            values[i++] = SciencePairs(s, p);
            values[i++] = Vp(s, p);
            values[i++] = s.WondersBuilt(p);
            values[i++] = OpenChains(s, p);
        }
        values[i++] = DistanceToCapital(s, me);
        values[i] = DistanceToCapital(s, 1 - me);
    }
}
