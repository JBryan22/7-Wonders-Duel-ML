using System.Text;
using SevenWondersDuel.Engine.Data;

namespace SevenWondersDuel.Engine;

/// <summary>
/// The Move Log text format (spec A4). One entry per line after a version line and a rules header:
/// <code>
/// 7wd-log v1
/// rules base start-choice=new
/// reveal first P1
/// reveal tokens agriculture economy law strategy urbanism
/// reveal draft pyramids great-lighthouse circus-maximus piraeus
/// P1 draft pyramids
/// ...
/// reveal age1 0.0 theater 0.1 altar 2.0 ...
/// P1 build lumber-yard
/// reveal 3.1 baths
/// P2 wonder baths piraeus
/// ...
/// P2 token law
/// P1 wonder temple great-library
/// reveal P1 library architecture masonry philosophy   (private to P1)
/// P1 library masonry
/// P2 wonder senate statue-of-zeus
/// P2 destroy quarry
/// P1 wonder arsenal mausoleum
/// P1 mausoleum quarry
/// P2 wonder baths messe                       (rules ... promo=messe)
/// P2 messe palisade
/// P1 wonder altar statue-of-liberty           (rules ... promo=liberty)
/// reveal returned age1 stable tavern workshop
/// P1 liberty-keep tavern
/// P1 liberty-give workshop                    (the stable goes to the discard pile)
/// P2 start opponent
/// reveal guilds 0.1 3.0 5.2
/// reveal age3 0.0 palace 0.1 senate ...
/// </code>
/// Slots are "row.position", both counted from 0 at the top row. Blank lines and lines starting with '#' are ignored.
/// <para>
/// Two more forms are accepted but never written by the base game (spec A4, A10):
/// a <b>slot-named Move</b>, where a take-card or Messe Move names the slot instead of the card ("P1 build 4.2",
/// "P2 wonder 3.1 sphinx", "P1 messe 0.1"; resolved to the face-up card in that slot, and logged by card name from then on), and a
/// <b>redacted private Reveal</b>, "reveal P2 library ? ? ?", for a Great Library draw the log's keeper did not see.
/// </para>
/// The rules header follows <see cref="Ruleset"/>; a log with Agora is rejected with a
/// <see cref="NotSupportedException"/>. Pantheon's lines are listed in <see cref="PantheonLog"/>.
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
        TokenReveal t => "reveal tokens " + string.Join(' ', t.Tokens.Select(TokenSlug)),
        DraftReveal d => "reveal draft " + string.Join(' ', d.Wonders.Select(WonderSlug)),
        SlotReveal r => FormatSlotReveal(r),
        GuildPatternReveal g => (g.Temples ? "reveal temples " : "reveal guilds ") + string.Join(' ', g.Slots.Select(s => GameData.Layout(GameState.AgeCount).SlotName(s))),
        LibraryReveal { Redacted: true } l => $"reveal {PlayerName(l.Player)} library ? ? ?",
        LibraryReveal l => $"reveal {PlayerName(l.Player)} library " + string.Join(' ', l.Tokens.Select(TokenSlug)),
        ReturnedReveal r => $"reveal returned age{r.Age} " + string.Join(' ', r.Cards.Select(CardSlug)),
        _ => PantheonLog.Format(entry) ?? throw new ArgumentException($"Unknown log entry {entry.GetType().Name}", nameof(entry)),
    };

    public static string FormatMove(Move move) => move.Kind switch
    {
        MoveKind.Build => $"build {CardSlug(move.Card)}",
        MoveKind.Discard => $"discard {CardSlug(move.Card)}",
        MoveKind.StartMe => "start me",
        MoveKind.StartOpponent => "start opponent",
        MoveKind.Token => $"token {TokenSlug(move.Token)}",
        MoveKind.Draft => $"draft {WonderSlug(move.Wonder)}",
        MoveKind.Wonder => $"wonder {CardSlug(move.Card)} {WonderSlug(move.Wonder)}",
        MoveKind.Destroy => $"destroy {CardSlug(move.Card)}",
        MoveKind.Mausoleum => $"mausoleum {CardSlug(move.Card)}",
        MoveKind.Library => $"library {TokenSlug(move.Token)}",
        MoveKind.Messe => $"messe {CardSlug(move.Card)}",
        MoveKind.LibertyKeep => $"liberty-keep {CardSlug(move.Card)}",
        MoveKind.LibertyGive => $"liberty-give {CardSlug(move.Card)}",
        _ => PantheonLog.FormatMove(move) ?? throw new ArgumentException($"Unknown move kind {move.Kind}", nameof(move)),
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
    static string CardSlug(int card) => GameData.IsCard(card) ? GameData.Card(card).Id : $"#{card}";
    static string TokenSlug(int token) => GameData.IsToken(token) ? GameData.Token(token).Id : $"#{token}";
    static string WonderSlug(int wonder) => GameData.IsWonder(wonder) ? GameData.Wonder(wonder).Id : $"#{wonder}";

    /// <summary>
    /// <paramref name="entry"/> as a log kept by <paramref name="viewer"/> records it: every private entry of the
    /// opponent's redacted ("reveal P2 library ? ? ?", and Pantheon's, see <see cref="PantheonLog"/>), the rest unchanged.
    /// Replaying a log redacted this way gives a state whose Observation for <paramref name="viewer"/> is the true one's.
    /// </summary>
    public static LogEntry AsSeenBy(LogEntry entry, int viewer) => entry switch
    {
        LibraryReveal { Redacted: false } l when l.Player != viewer => LibraryReveal.Hidden(l.Player),
        _ => PantheonLog.AsSeenBy(entry, viewer),
    };

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
            catch (NotSupportedException e)
            {
                throw new NotSupportedException($"Move Log line {i + 1} ('{line}'): {e.Message}", e);
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
        if (PantheonLog.TryParse(t, state) is { } pantheon) return pantheon;
        if (t[0] == "reveal")
        {
            if (t.Length == 3 && t[1] == "first") return new FirstPlayerReveal(ParsePlayer(t[2]));
            if (t.Length > 1 && t[1] == "tokens") return new TokenReveal(t.Skip(2).Select(GameData.AnyTokenIndex).ToArray());
            if (t.Length > 1 && t[1] == "draft") return new DraftReveal(t.Skip(2).Select(GameData.AnyWonderIndex).ToArray());
            // "reveal 1.0 library" names the green card Library in a slot; the Great Library's draw names a player.
            if (t.Length > 2 && t[1] is "P1" or "P2" && t[2] == "library")
            {
                var drawn = t.Skip(3).ToArray();
                if (drawn.Length == GameState.LibraryDrawSize && drawn.All(x => x == "?")) return LibraryReveal.Hidden(ParsePlayer(t[1]));
                if (drawn.Contains("?")) throw new FormatException("A redacted Great Library draw hides all 3 tokens: '? ? ?'");
                return new LibraryReveal(ParsePlayer(t[1]), drawn.Select(GameData.AnyTokenIndex).ToArray());
            }
            if (t.Length > 1 && t[1] == "returned")
            {
                if (t.Length != 3 + GameState.ReturnedCardCount || !t[2].StartsWith("age", StringComparison.Ordinal)
                    || !int.TryParse(t[2].AsSpan(3), out var returnedAge))
                    throw new FormatException("Expected 'reveal returned age<N> <card> <card> <card>'");
                return new ReturnedReveal(returnedAge, t.Skip(3).Select(GameData.AnyCardIndex).ToArray());
            }
            if (t.Length > 1 && t[1] is "guilds" or "temples")
            {
                var guildLayout = GameData.Layout(GameState.AgeCount);
                return new GuildPatternReveal(t.Skip(2).Select(x => ParseSlot(guildLayout, x)).ToArray(), t[1] == "temples");
            }

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
                cards[k] = (ParseSlot(layout, t[i + 2 * k]), GameData.AnyCardIndex(t[i + 2 * k + 1]));
            return new SlotReveal(age, opening, cards);
        }

        if (t.Length >= 2 && t[1] == "wonder")
        {
            if (t.Length != 4) throw new FormatException("Expected 'P<n> wonder <card> <wonder>'");
            return new MoveEntry(ParsePlayer(t[0]), Move.BuildWonder(ParseTakenCard(t[2], state),GameData.AnyWonderIndex(t[3])));
        }
        if (t.Length != 3) throw new FormatException("Expected 'P<n> <move> <argument>'");
        var player = ParsePlayer(t[0]);
        var move = t[1] switch
        {
            "draft" => Move.Draft(GameData.AnyWonderIndex(t[2])),
            "build" => Move.Build(ParseTakenCard(t[2], state)),
            "discard" => Move.Discard(ParseTakenCard(t[2], state)),
            "start" => t[2] switch
            {
                "me" => Move.StartMe,
                "opponent" => Move.StartOpponent,
                _ => throw new FormatException($"Expected 'start me' or 'start opponent', got 'start {t[2]}'"),
            },
            "token" => Move.PickToken(GameData.AnyTokenIndex(t[2])),
            "destroy" => Move.Destroy(GameData.AnyCardIndex(t[2])),
            "mausoleum" => Move.Mausoleum(GameData.AnyCardIndex(t[2])),
            "library" => Move.Library(GameData.AnyTokenIndex(t[2])),
            // A top-row card is always face up, so the Messe may name its slot too ("P1 messe 0.1").
            "messe" => Move.Messe(ParseTakenCard(t[2], state)),
            "liberty-keep" => Move.LibertyKeep(GameData.AnyCardIndex(t[2])),
            "liberty-give" => Move.LibertyGive(GameData.AnyCardIndex(t[2])),
            _ => throw new FormatException($"Unknown move '{t[1]}'"),
        };
        return new MoveEntry(player, move);
    }

    /// <summary>
    /// The card a take-card Move names: a card slug, or a slot of the current Age ("4.2", a slot-named Move) holding
    /// a face-up card. A face-down or empty slot is an error: the base game has no Move on an unknown card.
    /// </summary>
    static int ParseTakenCard(string text, GameState state)
    {
        if (text.Length == 0 || !char.IsAsciiDigit(text[0])) return GameData.AnyCardIndex(text);
        if (state.Age < 1) throw new FormatException($"Slot '{text}' named before any Age has started");
        var slot = ParseSlot(state.Layout, text);
        return state.SlotCard(slot) switch
        {
            GameState.Empty => throw new FormatException($"Slot {text} of Age {state.Age} is empty"),
            < 0 => throw new FormatException($"Slot {text} of Age {state.Age} holds a face-down card; name a face-up card"),
            var card => card,
        };
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
