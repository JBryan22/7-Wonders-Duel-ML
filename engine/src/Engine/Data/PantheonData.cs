using System.Text.Json.Nodes;

namespace SevenWondersDuel.Engine.Data;

/// <summary>One Pantheon Mythology: its deck of 3 Divinities, its 2 tokens and its Grand Temple.</summary>
public sealed class MythologyDef
{
    public required Mythology Mythology { get; init; }
    public required string Id { get; init; }
    public required string Name { get; init; }
    /// <summary>Mythology tokens of this kind in the box (2); 5 of the 10 are used each game.</summary>
    public required int Tokens { get; init; }
    /// <summary>The Divinity indices of this Mythology's deck, in data order.</summary>
    public required int[] Divinities { get; init; }

    public override string ToString() => Id;
}

/// <summary>A Divinity, or the Gate (<see cref="IsGate"/>, no Mythology). <see cref="Index"/> is its small-integer id.</summary>
public sealed class DivinityDef
{
    public required int Index { get; init; }
    public required string Id { get; init; }
    public required string Name { get; init; }
    /// <summary>The Mythology deck it belongs to; null for the Gate.</summary>
    public required Mythology? Mythology { get; init; }
    public required int VictoryPoints { get; init; }
    public required int Coins { get; init; }
    /// <summary>Astarte's coins: spendable, safe from coin loss, 1 VP each at the end.</summary>
    public required int ProtectedCoins { get; init; }
    public required int Shields { get; init; }
    public required ScienceSymbol? Science { get; init; }

    public bool IsGate => Mythology is null;
    public override string ToString() => Id;
}

/// <summary>A Grand Temple: an Age III card with its own back (R6), free while holding its Mythology's token (R9).</summary>
public sealed class TempleDef
{
    /// <summary>0..4 in data order. Not a card index: see <see cref="Card"/>.</summary>
    public required int Index { get; init; }
    /// <summary>Its card index, continuing the base cards (73..77): read with <see cref="GameData.Card"/>.</summary>
    public required int Card { get; init; }
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required Mythology Mythology { get; init; }
    public required Cost Cost { get; init; }

    public override string ToString() => Id;
}

/// <summary>An Offering token: a single-use discount of <see cref="Value"/> coins on an activation.</summary>
public sealed class OfferingDef
{
    public required int Index { get; init; }
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required int Value { get; init; }

    public override string ToString() => Id;
}

/// <summary>
/// One Pantheon board space. <see cref="Index"/> 0..5 is space 1..6, counted from the end nearest P1's capital (the
/// Conflict Pawn's negative end), so P1's cost rises 3..8 along the board and P2's falls 8..3.
/// </summary>
public sealed class PantheonSpaceDef
{
    public required int Index { get; init; }
    /// <summary>Activation cost per player (index 0 = P1).</summary>
    public required int[] Costs { get; init; }

    /// <summary>The space's number as written in the Move Log (1..6).</summary>
    public int Number => Index + 1;
    public int Cost(int player) => Costs[player];
}

/// <summary>
/// The Pantheon expansion's data (spec-pantheon P-A3), loaded once from the embedded JSON in data/pantheon/, and only
/// when first used: base games never touch it. Indices are stable small integers in file order; slugs are the stable
/// ids used in the Move Log. The 2 Wonders and 3 Progress tokens carry indices that continue the base ones
/// (<see cref="GameData.Wonders"/> 14, 15; <see cref="GameData.Tokens"/> 10..12), ready for the 16-bit masks, but they are
/// not in the base arrays.
/// </summary>
public static class PantheonData
{
    public const int SpaceCount = 6;
    public const int DeckSize = 3;
    /// <summary>Counts of the identities that continue the base indices (checked against the data at load).</summary>
    public const int TempleCount = 5, WonderCount = 2, TokenCount = 3;

    public static readonly MythologyDef[] Mythologies;
    /// <summary>The 15 Divinities grouped by Mythology, then the Gate (<see cref="Gate"/>).</summary>
    public static readonly DivinityDef[] Divinities;
    public static readonly int Gate;
    public static readonly TempleDef[] Temples;
    /// <summary>The Grand Temples as cards (indices 73..77): Age III, colour <see cref="CardColor.Temple"/>, no chain.</summary>
    public static readonly CardDef[] TempleCards;
    /// <summary>The 5 Grand Temple cards: what a <c>temple</c> back can hide.</summary>
    public static readonly CardSet TempleSet;
    /// <summary>End-of-game VP for 1, 2 and 3 Grand Temples built (index = count - 1).</summary>
    public static readonly int[] TemplePoints;
    public static readonly WonderDef[] Wonders;
    public static readonly TokenDef[] Tokens;
    public static readonly PantheonSpaceDef[] Spaces;
    public static readonly OfferingDef[] Offerings;
    /// <summary>Age I structure slots that get a Mythology token at setup (R1).</summary>
    public static readonly int[] MythologySlots;
    /// <summary>Age II structure slots that get an Offering token at setup (R1).</summary>
    public static readonly int[] OfferingSlots;

    static readonly Dictionary<string, int> DivinityBySlug;
    static readonly Dictionary<string, int> TempleBySlug;
    static readonly Dictionary<string, int> WonderBySlug;
    static readonly Dictionary<string, int> TokenBySlug;
    static readonly Dictionary<string, int> OfferingBySlug;
    static readonly Dictionary<string, Mythology> MythologyBySlug;

    static PantheonData()
    {
        var gods = GameData.Load("pantheon", "divinities.json");
        var mythologyIds = gods["mythologies"]!.AsArray().Select(m => (string)m!["id"]!).ToArray();
        if (!mythologyIds.SequenceEqual(Enum.GetNames<Mythology>().Select(n => n.ToLowerInvariant())))
            throw new InvalidOperationException("data/pantheon/divinities.json must list the Mythologies in enum order");
        MythologyBySlug = mythologyIds.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => (Mythology)x.i);

        Divinities = gods["divinities"]!.AsArray().Select((d, i) => ParseDivinity(d!, i)).ToArray();
        Gate = Array.FindIndex(Divinities, d => d.IsGate);
        Mythologies = gods["mythologies"]!.AsArray().Select((m, i) => new MythologyDef
        {
            Mythology = (Mythology)i,
            Id = (string)m!["id"]!,
            Name = (string)m["name"]!,
            Tokens = (int)m["tokens"]!,
            Divinities = Divinities.Where(d => d.Mythology == (Mythology)i).Select(d => d.Index).ToArray(),
        }).ToArray();

        var temples = GameData.Load("pantheon", "temples.json");
        TemplePoints = temples["points"]!.AsArray().Select(p => (int)p!).ToArray();
        Temples = temples["temples"]!.AsArray().Select((t, i) => new TempleDef
        {
            Index = i,
            Card = GameData.Cards.Length + i,
            Id = (string)t!["id"]!,
            Name = (string)t["name"]!,
            Mythology = MythologyOf((string)t["mythology"]!),
            Cost = Cost.Parse((string?)t["cost"]),
        }).ToArray();
        TempleCards = Temples.Select(t => new CardDef
        {
            Index = t.Card,
            Id = t.Id,
            Name = t.Name,
            Aliases = [],
            Age = 3,
            Color = CardColor.Temple,
            Cost = t.Cost,
            Produce = new int[Resources.Count],
            ProduceChoice = [],
            FixedPrice = [],
            Shields = 0,
            Science = null,
            VictoryPoints = 0,
            Coins = 0,
            CoinsPer = CoinsPerKind.None,
            CoinsPerAmount = 0,
            FreeIf = -1,
            Guild = GuildKind.None,
        }).ToArray();
        TempleSet = CardSet.Of(TempleCards.Select(c => c.Index));

        Wonders = GameData.Load("pantheon", "wonders.json")["wonders"]!.AsArray()
            .Select((w, i) => GameData.ParseWonder(w!, GameData.Wonders.Length + i)).ToArray();
        Tokens = GameData.Load("pantheon", "tokens.json")["tokens"]!.AsArray()
            .Select((t, i) => GameData.ParseToken(t!, GameData.Tokens.Length + i)).ToArray();

        var board = GameData.Load("pantheon", "board.json");
        Spaces = board["spaces"]!.AsArray().Select((s, i) => new PantheonSpaceDef
        {
            Index = (int)s!["space"]! - 1 == i ? i : throw new InvalidOperationException("Pantheon spaces must be listed 1 to 6"),
            Costs = s["cost"]!.AsArray().Select(c => (int)c!).ToArray(),
        }).ToArray();
        MythologySlots = board["mythologySlots"]!.AsArray().Select(s => (int)s!).ToArray();
        OfferingSlots = board["offeringSlots"]!.AsArray().Select(s => (int)s!).ToArray();
        Offerings = board["offerings"]!.AsArray().Select((o, i) => new OfferingDef
        {
            Index = i,
            Id = (string)o!["id"]!,
            Name = (string)o["name"]!,
            Value = (int)o["value"]!,
        }).ToArray();

        if (Temples.Length != TempleCount || Wonders.Length != WonderCount || Tokens.Length != TokenCount)
            throw new InvalidOperationException("data/pantheon/ must hold 5 Grand Temples, 2 Wonders and 3 Progress tokens");

        DivinityBySlug = Divinities.ToDictionary(d => d.Id, d => d.Index);
        TempleBySlug = Temples.ToDictionary(t => t.Id, t => t.Index);
        WonderBySlug = Wonders.ToDictionary(w => w.Id, w => w.Index);
        TokenBySlug = Tokens.ToDictionary(t => t.Id, t => t.Index);
        OfferingBySlug = Offerings.ToDictionary(o => o.Id, o => o.Index);
    }

    public static MythologyDef MythologyDef(Mythology m) => Mythologies[(int)m];
    public static TempleDef TempleOf(Mythology m) => Temples.Single(t => t.Mythology == m);

    public static int DivinityIndex(string slug) =>
        DivinityBySlug.TryGetValue(slug, out var i) ? i : throw new KeyNotFoundException($"Unknown Divinity '{slug}'");
    public static int TempleIndex(string slug) =>
        TempleBySlug.TryGetValue(slug, out var i) ? i : throw new KeyNotFoundException($"Unknown Grand Temple '{slug}'");
    /// <summary>The Wonder's index, continuing the base Wonders (14 or 15).</summary>
    public static int WonderIndex(string slug) =>
        WonderBySlug.TryGetValue(slug, out var i) ? i : throw new KeyNotFoundException($"Unknown Pantheon Wonder '{slug}'");
    /// <summary>The Progress token's index, continuing the base tokens (10..12).</summary>
    public static int TokenIndex(string slug) =>
        TokenBySlug.TryGetValue(slug, out var i) ? i : throw new KeyNotFoundException($"Unknown Pantheon Progress token '{slug}'");
    public static int OfferingIndex(string slug) =>
        OfferingBySlug.TryGetValue(slug, out var i) ? i : throw new KeyNotFoundException($"Unknown Offering token '{slug}'");
    public static Mythology MythologyOf(string slug) =>
        MythologyBySlug.TryGetValue(slug, out var m) ? m : throw new KeyNotFoundException($"Unknown Mythology '{slug}'");

    public static bool TryDivinityIndex(string slug, out int index) => DivinityBySlug.TryGetValue(slug, out index);
    public static bool TryTempleIndex(string slug, out int index) => TempleBySlug.TryGetValue(slug, out index);
    public static bool TryWonderIndex(string slug, out int index) => WonderBySlug.TryGetValue(slug, out index);
    public static bool TryTokenIndex(string slug, out int index) => TokenBySlug.TryGetValue(slug, out index);
    public static bool TryOfferingIndex(string slug, out int index) => OfferingBySlug.TryGetValue(slug, out index);
    public static bool TryMythology(string slug, out Mythology mythology) => MythologyBySlug.TryGetValue(slug, out mythology);

    static DivinityDef ParseDivinity(JsonNode d, int index) => new()
    {
        Index = index,
        Id = (string)d["id"]!,
        Name = (string)d["name"]!,
        Mythology = d["mythology"] is { } m ? MythologyOf((string)m!) : null,
        VictoryPoints = (int?)d["vp"] ?? 0,
        Coins = (int?)d["coins"] ?? 0,
        ProtectedCoins = (int?)d["protectedCoins"] ?? 0,
        Shields = (int?)d["shields"] ?? 0,
        Science = d["science"] is { } s ? Enum.Parse<ScienceSymbol>((string)s!, ignoreCase: true) : null,
    };
}
