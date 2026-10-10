namespace SevenWondersDuel.Engine.Data;

/// <summary>A price: coins plus a count per resource (indexed by <see cref="Resource"/>).</summary>
public sealed record Cost(int Coins, int[] Resources)
{
    public static readonly Cost Free = new(0, new int[Data.Resources.Count]);

    public int ResourceTotal => Resources.Sum();

    /// <summary>Parses "$3 C", "W W G", "PP" or "".</summary>
    public static Cost Parse(string? text)
    {
        var coins = 0;
        var res = new int[Data.Resources.Count];
        foreach (var token in (text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token[0] == '$') coins += int.Parse(token.AsSpan(1));
            else foreach (var c in token) res[(int)Data.Resources.FromLetter(c)]++;
        }
        return new Cost(coins, res);
    }
}

/// <summary>One Age card or Guild. <see cref="Index"/> is its small-integer id.</summary>
public sealed class CardDef
{
    public required int Index { get; init; }
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string[] Aliases { get; init; }
    public required int Age { get; init; }
    public required CardColor Color { get; init; }
    public required Cost Cost { get; init; }
    /// <summary>Resources produced every turn, per resource.</summary>
    public required int[] Produce { get; init; }
    /// <summary>Resources of which this card produces one, chosen each turn (empty if none).</summary>
    public required Resource[] ProduceChoice { get; init; }
    /// <summary>Resources this card lets its owner buy for 1 coin.</summary>
    public required Resource[] FixedPrice { get; init; }
    public required int Shields { get; init; }
    public required ScienceSymbol? Science { get; init; }
    public required int VictoryPoints { get; init; }
    public required int Coins { get; init; }
    public required CoinsPerKind CoinsPer { get; init; }
    public required int CoinsPerAmount { get; init; }
    /// <summary>Index of the earlier card that makes this one free, or -1.</summary>
    public required int FreeIf { get; init; }
    public required GuildKind Guild { get; init; }

    public bool IsGuild => Color == CardColor.Purple;
    public override string ToString() => Id;
}

public sealed class WonderDef
{
    public required int Index { get; init; }
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string[] Aliases { get; init; }
    public required Cost Cost { get; init; }
    public required int VictoryPoints { get; init; }
    public required int Coins { get; init; }
    public required int OpponentLoses { get; init; }
    public required int Shields { get; init; }
    public required bool PlayAgain { get; init; }
    public required Resource[] ProduceChoice { get; init; }
    public required WonderEffect Effect { get; init; }
    public required bool IsPromo { get; init; }

    public override string ToString() => Id;
}

public sealed class TokenDef
{
    public required int Index { get; init; }
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required int VictoryPoints { get; init; }
    public required int Coins { get; init; }
    public required ScienceSymbol? Science { get; init; }

    public override string ToString() => Id;
}

/// <summary>One slot of an Age structure.</summary>
public sealed record LayoutSlot(int Index, int Row, int Col, bool FaceUp, int[] CoveredBy, int[] Covers);

/// <summary>The card structure of one Age: 20 slots, top row first.</summary>
public sealed class AgeLayout
{
    public required int Age { get; init; }
    public required int RowCount { get; init; }
    public required LayoutSlot[] Slots { get; init; }

    public int SlotCount => Slots.Length;

    /// <summary>Slot index for a (row, column-position-in-row) pair, as written in the Move Log ("row.col").</summary>
    public int SlotAt(int row, int position) =>
        Slots.Where(s => s.Row == row).OrderBy(s => s.Col).ElementAt(position).Index;

    /// <summary>The Move Log name of a slot: "row.position", both 0-based.</summary>
    public string SlotName(int slot)
    {
        var s = Slots[slot];
        var position = Slots.Count(o => o.Row == s.Row && o.Col < s.Col);
        return $"{s.Row}.{position}";
    }
}
