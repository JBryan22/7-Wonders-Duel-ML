using System.Text;
using SevenWondersDuel.Engine.Data;

namespace SevenWondersDuel.Engine;

/// <summary>
/// The Move Log text format (spec A4). One entry per line after a version line and a rules header:
/// <code>
/// 7wd-log v1
/// rules base start-choice=new
/// reveal first P1
/// reveal age1 0.0 theater 0.1 altar 2.0 ...
/// P1 build lumber-yard
/// reveal 3.1 baths
/// </code>
/// Slots are "row.position", both counted from 0 at the top row. Blank lines and lines starting with '#' are ignored.
/// Ticket 09 finishes the grammar (slot-named Moves, redacted private Reveals).
/// </summary>
public static class MoveLog
{
    public const string VersionLine = "7wd-log v1";

    public static string Write(Ruleset rules, IEnumerable<LogEntry> entries)
    {
        var sb = new StringBuilder();
        sb.Append(VersionLine).Append('\n');
        sb.Append(rules).Append('\n');
        foreach (var e in entries) sb.Append(Format(e)).Append('\n');
        return sb.ToString();
    }

    public static string Format(LogEntry entry) => entry switch
    {
        MoveEntry m => $"{PlayerName(m.Player)} {FormatMove(m.Move)}",
        FirstPlayerReveal f => $"reveal first {PlayerName(f.Player)}",
        SlotReveal r => FormatSlotReveal(r),
        _ => throw new ArgumentException($"Unknown log entry {entry.GetType().Name}", nameof(entry)),
    };

    public static string FormatMove(Move move) => move.Kind switch
    {
        MoveKind.Build => $"build {CardSlug(move.Card)}",
        MoveKind.Discard => $"discard {CardSlug(move.Card)}",
        _ => throw new ArgumentException($"Unknown move kind {move.Kind}", nameof(move)),
    };

    static string FormatSlotReveal(SlotReveal r)
    {
        var sb = new StringBuilder("reveal");
        if (r.Opening) sb.Append(" age").Append(r.Age);
        var layout = GameData.Layout(r.Age);
        foreach (var (slot, card) in r.Cards) sb.Append(' ').Append(layout.SlotName(slot)).Append(' ').Append(CardSlug(card));
        return sb.ToString();
    }

    static string PlayerName(int player) => $"P{player + 1}";
    static string CardSlug(int card) => card >= 0 && card < GameData.Cards.Length ? GameData.Cards[card].Id : $"#{card}";

    /// <summary>Parses a Move Log and replays it, checking every entry. Errors name the offending line.</summary>
    public static Game Parse(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        Game? game = null;
        var sawVersion = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            try
            {
                if (!sawVersion)
                {
                    if (line != VersionLine) throw new FormatException($"Expected '{VersionLine}'");
                    sawVersion = true;
                }
                else if (game is null) game = new Game(Ruleset.Parse(line));
                else game.Apply(ParseEntry(line, game.State));
            }
            catch (Exception e) when (e is FormatException or InvalidOperationException or KeyNotFoundException or ArgumentException)
            {
                throw new FormatException($"Move Log line {i + 1} ('{line}'): {e.Message}", e);
            }
        }
        return game ?? throw new FormatException("Move Log has no rules header");
    }

    /// <summary>Parses one entry line. The state supplies context (the current Age for slot names).</summary>
    public static LogEntry ParseEntry(string line, GameState state)
    {
        var t = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (t[0] == "reveal")
        {
            if (t.Length == 3 && t[1] == "first") return new FirstPlayerReveal(ParsePlayer(t[2]));

            var i = 1;
            var opening = false;
            var age = state.Age;
            if (t.Length > 1 && t[1].StartsWith("age", StringComparison.Ordinal))
            {
                if (!int.TryParse(t[1].AsSpan(3), out age) || age < 1 || age > GameData.Layouts.Length)
                    throw new FormatException($"Unknown age '{t[1]}'");
                opening = true;
                i = 2;
            }
            if (age < 1) throw new FormatException("No Age has started yet");
            if ((t.Length - i) % 2 != 0 || t.Length == i) throw new FormatException("Expected 'reveal [ageN] row.pos card ...'");
            var layout = GameData.Layout(age);
            var cards = new (int, int)[(t.Length - i) / 2];
            for (var k = 0; k < cards.Length; k++)
                cards[k] = (ParseSlot(layout, t[i + 2 * k]), GameData.CardIndex(t[i + 2 * k + 1]));
            return new SlotReveal(age, opening, cards);
        }

        if (t.Length != 3) throw new FormatException("Expected 'P<n> <move> <card>'");
        var player = ParsePlayer(t[0]);
        var card = GameData.CardIndex(t[2]);
        var move = t[1] switch
        {
            "build" => Move.Build(card),
            "discard" => Move.Discard(card),
            _ => throw new FormatException($"Unknown move '{t[1]}'"),
        };
        return new MoveEntry(player, move);
    }

    static int ParsePlayer(string text) => text switch
    {
        "P1" => 0,
        "P2" => 1,
        _ => throw new FormatException($"Expected P1 or P2, got '{text}'"),
    };

    static int ParseSlot(AgeLayout layout, string text)
    {
        var dot = text.IndexOf('.');
        if (dot < 0 || !int.TryParse(text.AsSpan(0, dot), out var row) || !int.TryParse(text.AsSpan(dot + 1), out var pos)
            || row < 0 || row >= layout.RowCount || pos < 0 || pos >= layout.Slots.Count(s => s.Row == row))
            throw new FormatException($"'{text}' is not a slot of the Age {layout.Age} layout");
        return layout.SlotAt(row, pos);
    }
}
