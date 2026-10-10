using SevenWondersDuel.Engine.Data;

namespace SevenWondersDuel.Engine;

/// <summary>What building a card costs: coins printed on the card plus coins paid for trading.</summary>
public readonly record struct PaymentQuote(int CardCoins, int TradeCoins)
{
    public int Total => CardCoins + TradeCoins;
}

/// <summary>
/// The cheapest automatic payment (spec A4: payment is never part of a Move). Rules: rules-reference §3 and
/// common mistakes 1 and 3. Trading coins are kept apart from printed coins because Economy redirects only the
/// trading part. Masonry (blue cards) and Architecture (Wonders) waive 2 resources of the payer's choice, which
/// the search places on the most expensive missing resources.
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
        if (IsChained(s, player, card)) return default;
        var discount = def.Color == CardColor.Blue && s.HasToken(player, ProgressTokens.Masonry) ? ProgressTokens.Discount : 0;
        return QuoteCost(s, player, def.Cost, discount);
    }

    /// <summary>
    /// What building a Wonder costs, with the Architecture discount. Ticket 07 adds the Wonders' own choice
    /// production (Piraeus, the Great Lighthouse) to <see cref="QuoteCost"/>'s units.
    /// </summary>
    public static PaymentQuote QuoteWonder(GameState s, int player, int wonder)
    {
        var discount = s.HasToken(player, ProgressTokens.Architecture) ? ProgressTokens.Discount : 0;
        return QuoteCost(s, player, GameData.Wonders[wonder].Cost, discount);
    }

    /// <summary>True if <paramref name="player"/> owns the card that chains into <paramref name="card"/> (it is built free).</summary>
    public static bool IsChained(GameState s, int player, int card)
    {
        var freeIf = GameData.Cards[card].FreeIf;
        return freeIf >= 0 && s.City(player).Contains(freeIf);
    }

    /// <summary>A cost paid from the player's production, choice production, <paramref name="discount"/> free resources and trading.</summary>
    static PaymentQuote QuoteCost(GameState s, int player, Cost cost, int discount)
    {
        Span<int> need = stackalloc int[Resources.Count];
        Span<int> price = stackalloc int[Resources.Count];
        var anyNeed = false;
        for (var r = 0; r < Resources.Count; r++)
        {
            need[r] = Math.Max(0, cost.Resources[r] - s.Production(player, (Resource)r));
            price[r] = TradingPrice(s, player, (Resource)r);
            anyNeed |= need[r] > 0;
        }
        if (!anyNeed) return new PaymentQuote(cost.Coins, 0);

        // Each choice producer covers one unit of one of its resources. Wonder producers join in ticket 07.
        Span<byte> units = stackalloc byte[8];
        var unitCount = 0;
        var producers = s.City(player) & CardSets.ChoiceProducers;
        if (!producers.IsEmpty)
            foreach (var c in producers.Cards())
                units[unitCount++] = ChoiceMask[c];

        return new PaymentQuote(cost.Coins, Cheapest(need, price, units[..unitCount], discount));
    }

    /// <summary>
    /// The least trading cost for <paramref name="need"/>, trying every way to place each choice unit, then
    /// waiving <paramref name="free"/> units of whatever is still missing, most expensive first. There are at most
    /// a handful of choice units, so exhaustive search is cheap and, unlike a fixed priority order, always finds the
    /// optimum (rules-reference mistake 3). Once the choice units are placed, a free unit can cover any resource, so
    /// taking the dearest remaining one is optimal.
    /// </summary>
    internal static int Cheapest(Span<int> need, ReadOnlySpan<int> price, ReadOnlySpan<byte> units, int free = 0)
    {
        if (units.IsEmpty)
        {
            var total = 0;
            for (var r = 0; r < need.Length; r++) total += need[r] * price[r];
            if (free == 0) return total;
            Span<int> left = stackalloc int[need.Length];
            need.CopyTo(left);
            for (var k = 0; k < free; k++)
            {
                var dearest = -1;
                for (var r = 0; r < left.Length; r++)
                    if (left[r] > 0 && (dearest < 0 || price[r] > price[dearest])) dearest = r;
                if (dearest < 0) break;
                total -= price[dearest];
                left[dearest]--;
            }
            return total;
        }

        var best = int.MaxValue;
        var used = false;
        for (var r = 0; r < need.Length; r++)
        {
            if ((units[0] & 1 << r) == 0 || need[r] == 0) continue;
            used = true;
            need[r]--;
            best = Math.Min(best, Cheapest(need, price, units[1..], free));
            need[r]++;
        }
        return used ? best : Cheapest(need, price, units[1..], free);
    }
}
