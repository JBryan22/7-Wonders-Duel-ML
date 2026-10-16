using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SevenWondersDuel.Engine.Data;

/// <summary>
/// The card, Wonder, token and layout data, loaded once from the embedded JSON in data/base/.
/// Indices are stable small integers in file order; slugs are the stable ids used in the Move Log.
/// </summary>
public static class GameData
{
    public static readonly CardDef[] Cards;
    public static readonly WonderDef[] Wonders;
    public static readonly TokenDef[] Tokens;
    /// <summary>Indexed by Age - 1.</summary>
    public static readonly AgeLayout[] Layouts;

    static readonly Dictionary<string, int> CardBySlug;
    static readonly Dictionary<string, int> WonderBySlug;
    static readonly Dictionary<string, int> TokenBySlug;
    static readonly Dictionary<string, string> SlugByName;

    static GameData()
    {
        var cards = Load("base", "cards.json")["cards"]!.AsArray();
        var slugs = cards.Select(c => (string)c!["id"]!).ToList();
        Cards = cards.Select((c, i) => ParseCard(c!, i, slugs)).ToArray();
        Wonders = Load("base", "wonders.json")["wonders"]!.AsArray().Select((w, i) => ParseWonder(w!, i)).ToArray();
        Tokens = Load("base", "tokens.json")["tokens"]!.AsArray().Select((t, i) => ParseToken(t!, i)).ToArray();
        Layouts = Load("base", "layouts.json")["ages"]!.AsArray().Select(a => ParseLayout(a!)).ToArray();

        CardBySlug = Cards.ToDictionary(c => c.Id, c => c.Index);
        WonderBySlug = Wonders.ToDictionary(w => w.Id, w => w.Index);
        TokenBySlug = Tokens.ToDictionary(t => t.Id, t => t.Index);

        // Primary names win over aliases, so an alias can never shadow another card's real name.
        SlugByName = new(StringComparer.OrdinalIgnoreCase);
        var named = Cards.Select(c => (c.Id, c.Name, c.Aliases))
            .Concat(Wonders.Select(w => (w.Id, w.Name, w.Aliases)))
            .Concat(Tokens.Select(t => (t.Id, t.Name, Aliases: Array.Empty<string>())))
            .ToList();
        foreach (var (id, name, _) in named) SlugByName[name] = id;
        foreach (var (id, _, aliases) in named)
            foreach (var alias in aliases) SlugByName.TryAdd(alias, id);
    }

    public static int CardIndex(string slug) =>
        CardBySlug.TryGetValue(slug, out var i) ? i : throw new KeyNotFoundException($"Unknown card '{slug}'");

    public static int WonderIndex(string slug) =>
        WonderBySlug.TryGetValue(slug, out var i) ? i : throw new KeyNotFoundException($"Unknown Wonder '{slug}'");

    public static int TokenIndex(string slug) =>
        TokenBySlug.TryGetValue(slug, out var i) ? i : throw new KeyNotFoundException($"Unknown Progress token '{slug}'");

    public static bool TryCardIndex(string slug, out int index) => CardBySlug.TryGetValue(slug, out index);
    public static bool TryWonderIndex(string slug, out int index) => WonderBySlug.TryGetValue(slug, out index);
    public static bool TryTokenIndex(string slug, out int index) => TokenBySlug.TryGetValue(slug, out index);

    /// <summary>Resolves a display name or alias ("Tribunal", "Stonehenge", "The Great Library") to its slug.</summary>
    public static string? SlugForName(string name) => SlugByName.GetValueOrDefault(name.Trim());

    public static AgeLayout Layout(int age) => Layouts[age - 1];

    // ---------------------------------------------------------------- every index, base and expansion

    /// <summary>
    /// A card by index, the Pantheon Grand Temples included (<see cref="PantheonData.TempleCards"/>, indices 73..77
    /// after the base cards). Base indices read <see cref="Cards"/> directly, so a base game never loads Pantheon data.
    /// </summary>
    public static CardDef Card(int index) => index < Cards.Length ? Cards[index] : PantheonData.TempleCards[index - Cards.Length];
    /// <summary>A Wonder by index, Pantheon's 2 included (14, 15).</summary>
    public static WonderDef Wonder(int index) => index < Wonders.Length ? Wonders[index] : PantheonData.Wonders[index - Wonders.Length];
    /// <summary>A Progress token by index, Pantheon's 3 included (10..12).</summary>
    public static TokenDef Token(int index) => index < Tokens.Length ? Tokens[index] : PantheonData.Tokens[index - Tokens.Length];

    /// <summary>True for any card index, base or expansion (it may still be absent from the game being played).</summary>
    public static bool IsCard(int index) => index >= 0 && index < Cards.Length + PantheonData.TempleCount;
    public static bool IsWonder(int index) => index >= 0 && index < Wonders.Length + PantheonData.WonderCount;
    public static bool IsToken(int index) => index >= 0 && index < Tokens.Length + PantheonData.TokenCount;

    /// <summary>A card's index from its slug, the Grand Temples included.</summary>
    public static int AnyCardIndex(string slug) =>
        CardBySlug.TryGetValue(slug, out var i) ? i
        : PantheonData.TryTempleIndex(slug, out var t) ? PantheonData.Temples[t].Card
        : throw new KeyNotFoundException($"Unknown card '{slug}'");
    /// <summary>A Wonder's index from its slug, Pantheon's included.</summary>
    public static int AnyWonderIndex(string slug) =>
        WonderBySlug.TryGetValue(slug, out var i) ? i
        : PantheonData.TryWonderIndex(slug, out var w) ? w
        : throw new KeyNotFoundException($"Unknown Wonder '{slug}'");
    /// <summary>A Progress token's index from its slug, Pantheon's included.</summary>
    public static int AnyTokenIndex(string slug) =>
        TokenBySlug.TryGetValue(slug, out var i) ? i
        : PantheonData.TryTokenIndex(slug, out var t) ? t
        : throw new KeyNotFoundException($"Unknown Progress token '{slug}'");

    /// <summary>An embedded data file: <paramref name="folder"/> is "base" or an expansion's folder ("pantheon").</summary>
    internal static JsonNode Load(string folder, string file)
    {
        var name = $"data.{folder}.{file}";
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded data '{name}' is missing");
        return JsonNode.Parse(stream)!;
    }

    static CardDef ParseCard(JsonNode c, int index, List<string> slugs)
    {
        var coinsPer = c["coinsPer"];
        var freeIf = (string?)c["freeIf"];
        return new CardDef
        {
            Index = index,
            Id = (string)c["id"]!,
            Name = (string)c["name"]!,
            Aliases = Strings(c["aliases"]),
            Age = (int)c["age"]!,
            Color = Enum.Parse<CardColor>((string)c["color"]!, ignoreCase: true),
            Cost = Cost.Parse((string?)c["cost"]),
            Produce = Cost.Parse((string?)c["produce"]).Resources,
            ProduceChoice = Letters(c["produceChoice"]),
            FixedPrice = Letters(c["fixedPrice"]),
            Shields = (int?)c["shields"] ?? 0,
            Science = c["science"] is { } s ? Enum.Parse<ScienceSymbol>((string)s!, ignoreCase: true) : null,
            VictoryPoints = (int?)c["vp"] ?? 0,
            Coins = (int?)c["coins"] ?? 0,
            CoinsPer = coinsPer is null ? CoinsPerKind.None : Enum.Parse<CoinsPerKind>((string)coinsPer["of"]!, ignoreCase: true),
            CoinsPerAmount = (int?)coinsPer?["amount"] ?? 0,
            FreeIf = freeIf is null ? -1 : slugs.IndexOf(freeIf) is var i and >= 0 ? i
                : throw new InvalidOperationException($"Card '{c["id"]}' chains from unknown card '{freeIf}'"),
            Guild = c["guild"] is { } g ? Enum.Parse<GuildKind>((string)g!, ignoreCase: true) : GuildKind.None,
        };
    }

    internal static WonderDef ParseWonder(JsonNode w, int index) => new()
    {
        Index = index,
        Id = (string)w["id"]!,
        Name = (string)w["name"]!,
        Aliases = Strings(w["aliases"]),
        Cost = Cost.Parse((string?)w["cost"]),
        VictoryPoints = (int?)w["vp"] ?? 0,
        Coins = (int?)w["coins"] ?? 0,
        OpponentLoses = (int?)w["opponentLoses"] ?? 0,
        Shields = (int?)w["shields"] ?? 0,
        PlayAgain = (bool?)w["playAgain"] ?? false,
        ProduceChoice = Letters(w["produceChoice"]),
        Effect = w["effect"] is { } e ? Enum.Parse<WonderEffect>(((string)e!).Replace("-", ""), ignoreCase: true) : WonderEffect.None,
        IsPromo = (bool?)w["promo"] ?? false,
    };

    internal static TokenDef ParseToken(JsonNode t, int index) => new()
    {
        Index = index,
        Id = (string)t["id"]!,
        Name = (string)t["name"]!,
        VictoryPoints = (int?)t["vp"] ?? 0,
        Coins = (int?)t["coins"] ?? 0,
        Science = t["science"] is { } s ? Enum.Parse<ScienceSymbol>((string)s!, ignoreCase: true) : null,
    };

    static AgeLayout ParseLayout(JsonNode a)
    {
        var rows = a["rows"]!.AsArray();
        var raw = new List<(int Row, int Col, bool Up)>();
        for (var r = 0; r < rows.Count; r++)
            foreach (var col in rows[r]!["cols"]!.AsArray())
                raw.Add((r, (int)col!, (string)rows[r]!["face"]! == "up"));

        int[] Find(int row, int col) => raw
            .Select((s, i) => (s, i))
            .Where(x => x.s.Row == row && Math.Abs(x.s.Col - col) == 1)
            .Select(x => x.i).ToArray();

        var slots = raw.Select((s, i) => new LayoutSlot(i, s.Row, s.Col, s.Up,
            CoveredBy: Find(s.Row + 1, s.Col),
            Covers: Find(s.Row - 1, s.Col))).ToArray();
        return new AgeLayout { Age = (int)a["age"]!, RowCount = rows.Count, Slots = slots };
    }

    internal static string[] Strings(JsonNode? n) => n?.AsArray().Select(x => (string)x!).ToArray() ?? [];

    static Resource[] Letters(JsonNode? n) => ((string?)n ?? "").Select(Data.Resources.FromLetter).ToArray();
}
