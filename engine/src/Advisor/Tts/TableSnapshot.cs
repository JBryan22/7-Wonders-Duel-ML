using System.Text.Json;

namespace SevenWondersDuel.Advisor.Tts;

/// <summary>What <c>tts/snapshot.lua</c> reports for one object on the table.</summary>
public enum PieceKind
{
    Card, Deck, Token, Pawn, Military, Coin, Bag,
    /// <summary>Pantheon: a Mythology or Offering token (named by its face image: "greek", "offering-3").</summary>
    PToken,
    /// <summary>Pantheon: Nisaba's Snake.</summary>
    Snake,
    /// <summary>Pantheon: the Minerva pawn.</summary>
    Minerva,
}

/// <summary>
/// One piece of a <see cref="TableSnapshot"/>, exactly as the Lua reported it. <see cref="Name"/> and
/// <see cref="Names"/> are present only for public pieces (the Lua filters hidden ones, see snapshot.lua).
/// </summary>
/// <param name="Back">Cards: "age", "guild" or "wonder", and with Pantheon a Mythology ("greek"), "gate" or "temple" (visible
/// on either side). Pantheon tokens: "mythology" or "offering".</param>
/// <param name="Count">Decks and the Progress token bag: how many objects are inside.</param>
/// <param name="Value">Coins (1, 3, 6) and looting tokens (2, 5).</param>
/// <param name="Names">Decks in the discard pile only: every card's name.</param>
/// <param name="InHand">The piece is in a player's hand (never named, except <paramref name="Own"/>).</param>
/// <param name="Own">Pantheon: a Divinity in the host's own hand (their private draw), named for them alone.</param>
public sealed record TablePiece(
    PieceKind Kind, double X, double Y, double Z,
    bool FaceDown = false, bool Held = false, bool Moving = false, bool InHand = false,
    string? Name = null, string? Back = null, int Count = 0, int Value = 0, IReadOnlyList<string>? Names = null, bool Own = false);

/// <summary>A player seated at the TTS table.</summary>
public sealed record TablePlayer(string Color, bool Host);

/// <summary>
/// A Table Snapshot (GLOSSARY): every game piece on the TTS table with its raw position, hidden names already
/// filtered out inside TTS. <see cref="TableView"/> turns it into places. <see cref="Text"/> is the JSON exactly as
/// TTS sent it; the Lua sorts the pieces, so two snapshots of an unchanged table have the same text.
/// </summary>
public sealed class TableSnapshot
{
    TableSnapshot(string text, string modAge, IReadOnlyList<TablePlayer> players, IReadOnlyList<TablePiece> pieces)
    {
        Text = text;
        ModAge = modAge;
        Players = players;
        Pieces = pieces;
    }

    public string Text { get; }
    /// <summary>The mod's own record of the Age ("Age1".."Age3", "None" before Age I is dealt).</summary>
    public string ModAge { get; }
    public IReadOnlyList<TablePlayer> Players { get; }
    public IReadOnlyList<TablePiece> Pieces { get; }

    /// <summary>1..3 from <see cref="ModAge"/>, or null before Age I is dealt.</summary>
    public int? Age => ModAge is ['A', 'g', 'e', var d] && d is >= '1' and <= '3' ? d - '0' : null;

    /// <summary>
    /// Nobody holds anything (one of the conditions for a settled table, ADR 0004). TTS's "not resting" flag is not
    /// used: on the live table cards in the mod's auto-sorting layout zones stay "not resting" for good while lying
    /// perfectly still (2026-10-26), and two identical snapshots in a row already show nothing moved.
    /// </summary>
    public bool StillNow => !Pieces.Any(p => p.Held);

    /// <summary>The host's seat colour, if the host is seated.</summary>
    public string? HostColor => Players.FirstOrDefault(p => p.Host)?.Color;

    /// <summary>Pantheon pieces are on the table (a Divinity, the Gate, a Grand Temple, a Pantheon token, the Snake, Minerva).</summary>
    public bool HasPantheonPieces => Pieces.Any(p => p.Kind is PieceKind.PToken or PieceKind.Snake or PieceKind.Minerva
        || p.Kind == PieceKind.Card && p.Back is not ("age" or "guild" or "wonder"));

    /// <summary>Parses the JSON string snapshot.lua sends. Throws <see cref="FormatException"/> on anything else.</summary>
    public static TableSnapshot Parse(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (!root.TryGetProperty("v", out var v) || v.GetInt32() != 1)
                throw new FormatException("Not a version-1 table snapshot");
            var age = root.TryGetProperty("age", out var a) ? a.GetString() ?? "None" : "None";
            var players = root.GetProperty("players").EnumerateArray()
                .Select(p => new TablePlayer(p.GetProperty("color").GetString() ?? "", p.TryGetProperty("host", out var h) && h.GetBoolean()))
                .ToList();
            var pieces = root.GetProperty("pieces").EnumerateArray().Select(ParsePiece).ToList();
            return new TableSnapshot(text, age, players, pieces);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new FormatException($"Bad table snapshot: {e.Message}", e);
        }
    }

    static TablePiece ParsePiece(JsonElement p)
    {
        var kind = p.GetProperty("kind").GetString() switch
        {
            "card" => PieceKind.Card,
            "deck" => PieceKind.Deck,
            "token" => PieceKind.Token,
            "pawn" => PieceKind.Pawn,
            "military" => PieceKind.Military,
            "coin" => PieceKind.Coin,
            "bag" => PieceKind.Bag,
            "ptoken" => PieceKind.PToken,
            "snake" => PieceKind.Snake,
            "minerva" => PieceKind.Minerva,
            var other => throw new FormatException($"Unknown piece kind '{other}'"),
        };
        return new TablePiece(
            kind,
            Hundredths(p, "x"), Hundredths(p, "y"), Hundredths(p, "z"),
            FaceDown: Flag(p, "down"), Held: Flag(p, "held"), Moving: Flag(p, "moving"), InHand: Flag(p, "hand"),
            Name: p.TryGetProperty("name", out var n) ? n.GetString() : null,
            Back: p.TryGetProperty("back", out var b) ? b.GetString() : null,
            Count: p.TryGetProperty("count", out var c) ? c.GetInt32() : 0,
            Value: p.TryGetProperty("value", out var val) ? val.GetInt32() : 0,
            Names: p.TryGetProperty("names", out var ns) ? ns.EnumerateArray().Select(e => e.GetString() ?? "").ToList() : null,
            Own: Flag(p, "own"));
    }

    static double Hundredths(JsonElement p, string name) => p.GetProperty(name).GetInt64() / 100.0;
    static bool Flag(JsonElement p, string name) => p.TryGetProperty(name, out var f) && f.GetBoolean();
}
