using SevenWondersDuel.Engine.Data;

namespace SevenWondersDuel.Engine;

/// <summary>What building a card costs: coins printed on the card plus coins paid for trading.</summary>
public readonly record struct PaymentQuote(int CardCoins, int TradeCoins)
{
    public int Total => CardCoins + TradeCoins;
}

/// <summary>
/// The cheapest automatic payment (spec A4: payment is never part of a Move). Rules: rules-reference §3 and
/// common mistakes 1 and 3. Trading coins are kept apart from printed coins because Economy (ticket 06)
/// redirects only the trading part.
/// </summary>
public static class Payment
{
    /// <summary>Per card: the resources its choice production can give, as a bitmask over <see cref="Resource"/>.</summary>
    static readonly byte[] ChoiceMask = GameData.Cards
        .Select(c => (byte)c.ProduceChoice.Aggregate(0, (m, r) => m | 1 << (int)r))
        .ToArray();

    /// <summary>
    /// The price of buying one unit of <paramref name="r"/> for <paramref name="player"/>: 1 with a fixed-price
    /// card, otherwise 2 + what the opponent's brown and grey cards produce of it.
    /// </summary>
    public static int TradingPrice(GameState s, int player, Resource r) =>
        !(s.City(player) & CardSets.FixedPrice[(int)r]).IsEmpty ? 1 : 2 + s.Production(1 - player, r);

    public static PaymentQuote Quote(GameState s, int player, int card)
    {
        var def = GameData.Cards[card];
        // A chain makes the card free, printed coins included.
        if (def.FreeIf >= 0 && s.City(player).Contains(def.FreeIf)) return default;

        Span<int> need = stackalloc int[Resources.Count];
        Span<int> price = stackalloc int[Resources.Count];
        var anyNeed = false;
        for (var r = 0; r < Resources.Count; r++)
        {
            need[r] = Math.Max(0, def.Cost.Resources[r] - s.Production(player, (Resource)r));
            price[r] = TradingPrice(s, player, (Resource)r);
            anyNeed |= need[r] > 0;
        }
        if (!anyNeed) return new PaymentQuote(def.Cost.Coins, 0);

        // Each choice producer covers one unit of one of its resources. Wonder producers join in ticket 07,
        // and the Architecture/Masonry discounts become extra units in ticket 06.
        Span<byte> units = stackalloc byte[8];
        var unitCount = 0;
        var producers = s.City(player) & CardSets.ChoiceProducers;
        if (!producers.IsEmpty)
            foreach (var c in producers.Cards())
                units[unitCount++] = ChoiceMask[c];

        return new PaymentQuote(def.Cost.Coins, Cheapest(need, price, units[..unitCount]));
    }

    /// <summary>
    /// The least trading cost for <paramref name="need"/>, trying every way to place each choice unit.
    /// There are at most a handful of units, so exhaustive search is cheap and, unlike a fixed priority order,
    /// always finds the optimum (rules-reference mistake 3).
    /// </summary>
    internal static int Cheapest(Span<int> need, ReadOnlySpan<int> price, ReadOnlySpan<byte> units)
    {
        if (units.IsEmpty)
        {
            var total = 0;
            for (var r = 0; r < need.Length; r++) total += need[r] * price[r];
            return total;
        }

        var best = int.MaxValue;
        var used = false;
        for (var r = 0; r < need.Length; r++)
        {
            if ((units[0] & 1 << r) == 0 || need[r] == 0) continue;
            used = true;
            need[r]--;
            best = Math.Min(best, Cheapest(need, price, units[1..]));
            need[r]++;
        }
        return used ? best : Cheapest(need, price, units[1..]);
    }
}
