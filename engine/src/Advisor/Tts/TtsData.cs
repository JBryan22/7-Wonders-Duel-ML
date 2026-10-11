using System.Reflection;
using System.Text.Json.Nodes;
using SevenWondersDuel.Engine.Data;

namespace SevenWondersDuel.Advisor.Tts;

/// <summary>A rectangle on the table, in TTS world units (x1..x2 by z1..z2).</summary>
public readonly record struct TableRect(double X1, double X2, double Z1, double Z2)
{
    public bool Contains(double x, double z) => x >= X1 && x <= X2 && z >= Z1 && z <= Z2;

    internal static TableRect From(JsonNode n) => new((double)n["x1"]!, (double)n["x2"]!, (double)n["z1"]!, (double)n["z2"]!);
}

/// <summary>A point on the table.</summary>
public readonly record struct TablePoint(double X, double Z);

/// <summary>
/// The files in <c>tts/</c>, embedded in the Advisor: the read-only snapshot script, the TTS-name → slug table and
/// the table geometry (spec A2: "the read-only Lua snapshot script and the TTS-name → slug table").
/// </summary>
public static class TtsData
{
    /// <summary>The placeholder in snapshot.lua replaced by each request's id.</summary>
    public const string RequestPlaceholder = "@REQUEST@";

    /// <summary>tts/snapshot.lua as committed.</summary>
    public static string SnapshotScript { get; } = Text("snapshot.lua");

    /// <summary>The script for one request: the same Lua with its request id filled in.</summary>
    public static string ScriptFor(string request)
    {
        if (request.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
            throw new ArgumentException("A request id is letters, digits and '-'", nameof(request));
        return SnapshotScript.Replace(RequestPlaceholder, request, StringComparison.Ordinal);
    }

    public static TtsNames Names { get; } = new(JsonNode.Parse(Text("names.json"))!);
    public static TablePlaces Places { get; } = new(JsonNode.Parse(Text("places.json"))!);

    static string Text(string file)
    {
        var name = $"tts.{file}";
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded '{name}' is missing");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

/// <summary>
/// tts/names.json: the mod's object names to slugs, one table per kind. Names are never resolved through the
/// Engine's display names: the mod calls the Age III red card "Courthouse" (our <c>pretorium</c>) and the Age II
/// blue one "Tribunal" (our <c>courthouse</c>).
/// </summary>
public sealed class TtsNames
{
    readonly Dictionary<string, string> _cards, _wonders, _tokens;

    internal TtsNames(JsonNode json)
    {
        _cards = Table(json, "cards");
        _wonders = Table(json, "wonders");
        _tokens = Table(json, "tokens");
    }

    public IReadOnlyDictionary<string, string> Cards => _cards;
    public IReadOnlyDictionary<string, string> Wonders => _wonders;
    public IReadOnlyDictionary<string, string> Tokens => _tokens;

    public string? Card(string name) => _cards.GetValueOrDefault(name);
    public string? Wonder(string name) => _wonders.GetValueOrDefault(name);
    public string? Token(string name) => _tokens.GetValueOrDefault(name);

    static Dictionary<string, string> Table(JsonNode json, string key) =>
        json[key]!.AsObject().ToDictionary(p => p.Key, p => (string)p.Value!, StringComparer.Ordinal);
}

/// <summary>tts/places.json: where things sit on the mod's table (see the file for the measurements).</summary>
public sealed class TablePlaces
{
    internal TablePlaces(JsonNode json)
    {
        var pyramid = json["pyramid"]!;
        PyramidXStep = (double)pyramid["xStep"]!;
        PyramidZTop = (double)pyramid["zTop"]!;
        PyramidZStep = (double)pyramid["zStep"]!;
        PyramidSnap = (double)pyramid["snap"]!;
        PyramidArea = TableRect.From(pyramid["area"]!);
        Discard = TableRect.From(json["discard"]!);
        Cities = BySide(json["cities"]!, TableRect.From);
        var wonders = json["wonders"]!;
        WonderX = BySide(wonders["x"]!, n => (double)n);
        WonderTuckInwards = (double)wonders["tuckInwards"]!;
        WonderRows = wonders["rows"]!.AsArray().Select(r => (double)r!).ToArray();
        WonderSnap = (double)wonders["snap"]!;
        OfferSpots = json["offers"]!["spots"]!.AsArray().Select(s => new TablePoint((double)s!["x"]!, (double)s["z"]!)).ToArray();
        OfferSnap = (double)json["offers"]!["snap"]!;
        ProgressBoard = TableRect.From(json["progressBoard"]!);
        LibraryDraw = BySide(json["libraryDraw"]!, TableRect.From);
        Sides = BySide(json["sides"]!, TableRect.From);
        Table = TableRect.From(json["table"]!);
        TrackCentre = (double)json["track"]!["centre"]!;
        TrackStep = (double)json["track"]!["step"]!;
        Looting = json["military"]!["tokens"]!.AsArray()
            .Select(t => new LootingHome(Enum.Parse<Side>((string)t!["side"]!), (int)t["value"]!, new TablePoint((double)t["x"]!, (double)t["z"]!)))
            .ToArray();
        LootingSnap = (double)json["military"]!["snap"]!;
    }

    public double PyramidXStep { get; }
    public double PyramidZTop { get; }
    public double PyramidZStep { get; }
    public double PyramidSnap { get; }
    public TableRect PyramidArea { get; }
    public TableRect Discard { get; }
    public IReadOnlyDictionary<Side, TableRect> Cities { get; }
    public IReadOnlyDictionary<Side, double> WonderX { get; }
    public double WonderTuckInwards { get; }
    /// <summary>z of Wonder rows 1..4 (index 0 is row 1, next to the board).</summary>
    public IReadOnlyList<double> WonderRows { get; }
    public double WonderSnap { get; }
    public IReadOnlyList<TablePoint> OfferSpots { get; }
    public double OfferSnap { get; }
    public TableRect ProgressBoard { get; }
    public IReadOnlyDictionary<Side, TableRect> LibraryDraw { get; }
    public IReadOnlyDictionary<Side, TableRect> Sides { get; }
    public TableRect Table { get; }
    public double TrackCentre { get; }
    public double TrackStep { get; }
    public IReadOnlyList<LootingHome> Looting { get; }
    public double LootingSnap { get; }

    /// <summary>The x of an Age card tucked under a Wonder of this side (about 3 units inwards of the column).</summary>
    public double TuckX(Side side) => WonderX[side] + (side == Side.Red ? WonderTuckInwards : -WonderTuckInwards);

    /// <summary>The centre of a pyramid slot of the given Age layout.</summary>
    public TablePoint SlotCentre(AgeLayout layout, int slot)
    {
        var minCol = layout.Slots.Min(s => s.Col);
        var maxCol = layout.Slots.Max(s => s.Col);
        var s = layout.Slots[slot];
        return new TablePoint(PyramidXStep * (s.Col - (minCol + maxCol) / 2.0), PyramidZTop - PyramidZStep * s.Row);
    }

    static IReadOnlyDictionary<Side, T> BySide<T>(JsonNode n, Func<JsonNode, T> read) =>
        new Dictionary<Side, T> { [Side.Red] = read(n["Red"]!), [Side.White] = read(n["White"]!) };
}

/// <summary>A looting token's home spot on the board; <see cref="Side"/> is the player who loses the coins.</summary>
public sealed record LootingHome(Side Side, int Value, TablePoint At);
