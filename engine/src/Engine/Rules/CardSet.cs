using System.Numerics;
using SevenWondersDuel.Engine.Data;

namespace SevenWondersDuel.Engine;

/// <summary>A set of cards as a 128-bit mask over card indices (73 cards fit). A plain value: copying it is free.</summary>
public readonly record struct CardSet(UInt128 Bits)
{
    public static readonly CardSet Empty = default;

    public static CardSet Of(int card) => new(UInt128.One << card);
    public static CardSet Of(IEnumerable<int> cards) => cards.Aggregate(Empty, (s, c) => s.With(c));

    public bool Contains(int card) => ((Bits >> card) & UInt128.One) != UInt128.Zero;
    public CardSet With(int card) => new(Bits | (UInt128.One << card));
    public CardSet Without(int card) => new(Bits & ~(UInt128.One << card));
    public int Count => (int)UInt128.PopCount(Bits);
    public bool IsEmpty => Bits == UInt128.Zero;

    public static CardSet operator &(CardSet a, CardSet b) => new(a.Bits & b.Bits);
    public static CardSet operator |(CardSet a, CardSet b) => new(a.Bits | b.Bits);
    public static CardSet operator ~(CardSet a) => new(~a.Bits);

    /// <summary>The n-th card (0-based, in index order) of the set.</summary>
    public int ElementAt(int n)
    {
        var lo = (ulong)Bits;
        var loCount = BitOperations.PopCount(lo);
        if (n >= loCount) return 64 + NthBit((ulong)(Bits >> 64), n - loCount);
        return NthBit(lo, n);
    }

    static int NthBit(ulong bits, int n)
    {
        for (var i = 0; i < n; i++) bits &= bits - 1;
        if (bits == 0) throw new ArgumentOutOfRangeException(nameof(n));
        return BitOperations.TrailingZeroCount(bits);
    }

    /// <summary>The cards in index order (allocation-free in a <c>foreach</c>).</summary>
    public Bits Cards() => new(Bits);

    public override string ToString() => string.Join(' ', Cards().Select(c => GameData.Cards[c].Id));
}

/// <summary>Fixed card sets derived once from the card data.</summary>
public static class CardSets
{
    /// <summary>The non-Guild cards of each Age, indexed by Age (index 0 unused).</summary>
    public static readonly CardSet[] OfAge;
    public static readonly CardSet Guilds;
    /// <summary>Indexed by <see cref="CardColor"/>.</summary>
    public static readonly CardSet[] OfColor;
    /// <summary>Cards that produce one resource of a choice each turn (Forum, Caravansery).</summary>
    public static readonly CardSet ChoiceProducers;
    /// <summary>Cards that fix a resource's trading price at 1 coin, indexed by <see cref="Resource"/>.</summary>
    public static readonly CardSet[] FixedPrice;
    /// <summary>Green cards showing each scientific symbol, indexed by <see cref="ScienceSymbol"/> (Law's set is empty).</summary>
    public static readonly CardSet[] OfSymbol;

    static CardSets()
    {
        var cards = GameData.Cards;
        CardSet Where(Func<CardDef, bool> p) => CardSet.Of(cards.Where(p).Select(c => c.Index));
        OfAge = [CardSet.Empty, .. Enumerable.Range(1, 3).Select(a => Where(c => c.Age == a && !c.IsGuild))];
        Guilds = Where(c => c.IsGuild);
        OfColor = Enum.GetValues<CardColor>().Select(col => Where(c => c.Color == col)).ToArray();
        ChoiceProducers = Where(c => c.ProduceChoice.Length > 0);
        FixedPrice = Enum.GetValues<Resource>().Select(r => Where(c => c.FixedPrice.Contains(r))).ToArray();
        OfSymbol = Enum.GetValues<ScienceSymbol>().Select(sym => Where(c => c.Science == sym)).ToArray();
    }
}
