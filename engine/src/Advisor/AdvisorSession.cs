using System.Text;
using SevenWondersDuel.Engine;
using SevenWondersDuel.Engine.Data;

namespace SevenWondersDuel.Advisor;

/// <summary>A Move or Reveal the Advisor refused, with a message meant for the owner.</summary>
public sealed class EntryException(string message) : Exception(message);

/// <summary>
/// One game in the Advisor Window (spec A8): the Move Log is the source of truth and the Game State is always the
/// replay of it. Entering a line, <see cref="Undo"/> and <see cref="RewindTo"/> all build a new log and replay it
/// from the start, so they can never drift from what <c>7wd replay</c> would print for the same log.
/// <para>
/// <see cref="Seat"/> is the owner's player. The opponent's Great Library draw is private to them, so it is logged
/// redacted ("reveal P2 library ? ? ?") as soon as it is due, and a named draw for the opponent is refused.
/// Flips revealed one card at a time are merged into one Reveal line while no Move comes between them, so a log
/// entered by hand reads like one written by the Engine ("reveal age1 0.0 a 0.1 b ...", "reveal 3.1 x 3.2 y").
/// </para>
/// </summary>
public sealed class AdvisorSession
{
    /// <summary>The comment line that records the owner's seat in a saved Move Log (comments are ignored by the parser).</summary>
    public const string SeatPrefix = "# advisor seat=";

    AdvisorSession(Ruleset rules, int seat, Game game)
    {
        Rules = rules;
        Seat = seat;
        Game = game;
    }

    public Ruleset Rules { get; }
    /// <summary>The owner's player: 0 for P1, 1 for P2.</summary>
    public int Seat { get; }
    public Game Game { get; private set; }
    public GameState State => Game.State;
    public IReadOnlyList<LogEntry> Log => Game.Log;

    public static AdvisorSession New(Ruleset rules, int seat)
    {
        if (seat is not (0 or 1)) throw new ArgumentOutOfRangeException(nameof(seat), "The seat is P1 (0) or P2 (1)");
        return new AdvisorSession(rules, seat, new Game(rules));
    }

    /// <summary>Reads a saved game: a Move Log with the seat comment (default P1 if it has none).</summary>
    public static AdvisorSession Load(string text)
    {
        var seat = 0;
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith(SeatPrefix, StringComparison.Ordinal)) continue;
            seat = line[SeatPrefix.Length..] switch
            {
                "P1" => 0,
                "P2" => 1,
                var other => throw new FormatException($"Unknown advisor seat '{other}'"),
            };
        }
        var game = MoveLog.Parse(text);
        var session = new AdvisorSession(game.Rules, seat, game);
        session.Replay([.. game.Log]);
        return session;
    }

    /// <summary>The saved form: the Move Log with the seat as a comment after the rules header.</summary>
    public string Write()
    {
        var sb = new StringBuilder();
        sb.Append(MoveLog.VersionLine).Append('\n');
        sb.Append(Rules).Append('\n');
        sb.Append(SeatPrefix).Append(Seat == 0 ? "P1" : "P2").Append('\n');
        foreach (var e in Log) sb.Append(MoveLog.Format(e)).Append('\n');
        return sb.ToString();
    }

    /// <summary>
    /// Parses one Move Log line against the current state and appends it, or throws an <see cref="EntryException"/>
    /// saying why not. Slot-named Moves ("P1 build 4.2") are accepted and logged by card name.
    /// </summary>
    public void Enter(string line)
    {
        line = line.Trim();
        if (line.Length == 0) throw new EntryException("Nothing to enter");
        if (State.IsOver) throw new EntryException("The game is over. Undo or rewind to change it.");

        LogEntry entry;
        try
        {
            entry = MoveLog.ParseEntry(line, State);
        }
        catch (Exception e) when (e is FormatException or KeyNotFoundException or InvalidOperationException or ArgumentException)
        {
            throw new EntryException(e.Message);
        }

        if (WhyRefused(entry) is { } why) throw new EntryException(why);

        var entries = Log.ToList();
        if (entry is SlotReveal flip && entries.Count > 0 && entries[^1] is SlotReveal previous
            && previous.Age == flip.Age && previous.Opening == flip.Opening)
            entries[^1] = new SlotReveal(flip.Age, flip.Opening, [.. previous.Cards, .. flip.Cards]);
        else
            entries.Add(entry);
        Replay(entries);
    }

    /// <summary>Enters several lines as one change (a Statue keep/give pair): all of them, or none if one is refused.</summary>
    public void Enter(IReadOnlyList<string> lines)
    {
        var before = Game;
        try
        {
            foreach (var line in lines) Enter(line);
        }
        catch (EntryException)
        {
            Game = before;
            throw;
        }
    }

    /// <summary>
    /// Removes the last Move and every Reveal after it (spec A8). Before the first Move it removes the last Reveal.
    /// Returns false when the log is already empty.
    /// </summary>
    public bool Undo()
    {
        var entries = Log.ToList();
        if (entries.Count == 0) return false;
        var lastMove = entries.FindLastIndex(e => e is MoveEntry);
        var keep = lastMove >= 0 ? lastMove : entries.Count - 1;
        Replay(entries.Take(keep).ToList());
        return true;
    }

    /// <summary>Keeps the first <paramref name="count"/> log entries and replays them (clicking a log line).</summary>
    public void RewindTo(int count)
    {
        if (count < 0 || count > Log.Count) throw new EntryException($"There is no log line {count}");
        Replay(Log.Take(count).ToList());
    }

    /// <summary>Why the Advisor refuses an entry the Engine would accept, or a friendlier reason than the Engine's.</summary>
    string? WhyRefused(LogEntry entry)
    {
        var s = State;
        switch (entry)
        {
            case LibraryReveal { Redacted: false } l when l.Player != Seat:
                return $"P{l.Player + 1}'s Great Library draw is private: only they see it, so it stays '? ? ?'";
            case LibraryReveal { Redacted: true } l when l.Player == Seat:
                return "Name the 3 Progress tokens you drew for the Great Library";
            case SlotReveal r when r.Age == s.Age:
                var named = new HashSet<int>();
                foreach (var (slot, card) in r.Cards)
                {
                    if (slot < 0 || slot >= GameState.SlotCount || card < 0 || card >= GameData.Cards.Length) continue;
                    var name = GameData.Cards[card].Name;
                    if (s.SlotCard(slot) >= 0)
                        return $"Slot {s.Layout.SlotName(slot)} already shows {GameData.Cards[s.SlotCard(slot)].Name}; rewind the log to change it";
                    if (s.Seen.Contains(card) || !named.Add(card))
                        return $"{name} has already been revealed: every card is in the game only once";
                    if (s.SlotCard(slot) == GameState.HiddenGuild && !GameData.Cards[card].IsGuild)
                        return $"Slot {s.Layout.SlotName(slot)} shows a Guild back, but {name} is not a Guild";
                    if (s.SlotCard(slot) == GameState.Hidden && GameData.Cards[card].IsGuild)
                        return $"{name} is a Guild, but slot {s.Layout.SlotName(slot)} does not show a Guild back";
                    if (!GameData.Cards[card].IsGuild && GameData.Cards[card].Age != s.Age)
                        return $"{name} is an Age {GameData.Cards[card].Age} card, not Age {s.Age}";
                }
                return null;
            default:
                return null;
        }
    }

    /// <summary>
    /// Replays <paramref name="entries"/> into a fresh game and makes it current, then logs any Reveal nobody at this
    /// table can name (the opponent's Great Library draw, redacted). On failure the current game is left unchanged.
    /// </summary>
    void Replay(List<LogEntry> entries)
    {
        var game = new Game(Rules);
        foreach (var e in entries)
        {
            try
            {
                game.Apply(e);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or KeyNotFoundException)
            {
                throw new EntryException(Friendly(ex.Message));
            }
        }
        while (game.State.Step == StepKind.Reveal && game.State.SlotsToReveal == 0 && game.State.AwaitingLibraryDraw
               && game.State.PlayerToDecide != Seat)
            game.Apply(LibraryReveal.Hidden(game.State.PlayerToDecide));
        Game = game;
    }

    /// <summary>The Engine's messages are already specific; this only drops the "Illegal move '...': " lead-in.</summary>
    static string Friendly(string message)
    {
        const string lead = "Illegal move '";
        if (!message.StartsWith(lead, StringComparison.Ordinal)) return Capitalise(message);
        var end = message.IndexOf("': ", StringComparison.Ordinal);
        return end < 0 ? message : $"Can't {message[lead.Length..end]}: {message[(end + 3)..]}";
    }

    static string Capitalise(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
