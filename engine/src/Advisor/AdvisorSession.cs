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

    /// <summary>
    /// The comment line that keeps one of the AI's private log lines in a Pantheon game against the AI (ticket 12): the
    /// log index and the true line, base64-encoded so a glance at the file doesn't spoil it. The log itself holds the
    /// line as the owner sees it ("reveal P2 divinities greek ? ?", "P2 divinity ? 4").
    /// </summary>
    public const string SecretPrefix = "# advisor ai-secret ";

    /// <summary>
    /// The AI's private log lines (Pantheon, against the AI): log index → the true line where the log holds its redacted
    /// form. Only <see cref="TrueGame"/> and the AI's Observation use them; the owner's view never does.
    /// </summary>
    readonly SortedDictionary<int, string> _secrets = [];

    AdvisorSession(Ruleset rules, int seat, Game game, AiOpponent? ai = null)
    {
        Rules = rules;
        Seat = seat;
        Game = game;
        Ai = ai;
    }

    /// <summary>
    /// The AI opponent of a "Play against the AI" game (ticket 36), or null for a game entered by hand. In such a game
    /// the program deals every Reveal itself (<see cref="Deal"/>) and the AI plays the other seat.
    /// </summary>
    public AiOpponent? Ai { get; private set; }

    /// <summary>The AI's seat in a game against the AI.</summary>
    public int AiSeat => 1 - Seat;

    public Ruleset Rules { get; }
    /// <summary>The owner's player: 0 for P1, 1 for P2.</summary>
    public int Seat { get; }
    public Game Game { get; private set; }
    public GameState State => Game.State;
    public IReadOnlyList<LogEntry> Log => Game.Log;

    public static AdvisorSession New(Ruleset rules, int seat, AiOpponent? ai = null)
    {
        if (seat is not (0 or 1)) throw new ArgumentOutOfRangeException(nameof(seat), "The seat is P1 (0) or P2 (1)");
        var session = new AdvisorSession(rules, seat, new Game(rules), ai);
        session.Deal();
        return session;
    }

    /// <summary>
    /// The same game under other rules: the rules header is rewritten and the whole log replayed (spec A9: "Enable
    /// &lt;promo&gt; for this game" when a promo Wonder turns up in a draft but its box was unticked). Throws an
    /// <see cref="EntryException"/> if the log is not legal under <paramref name="rules"/>.
    /// </summary>
    public AdvisorSession WithRules(Ruleset rules)
    {
        var session = new AdvisorSession(rules, Seat, new Game(rules), Ai);
        foreach (var (index, line) in _secrets) session._secrets[index] = line;
        session.Replay([.. Log]);
        return session;
    }

    /// <summary>Reads a saved game: a Move Log with the seat comment (default P1 if it has none).</summary>
    public static AdvisorSession Load(string text)
    {
        var seat = 0;
        AiOpponent? ai = null;
        var secrets = new SortedDictionary<int, string>();
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith(AiOpponent.Prefix, StringComparison.Ordinal)) ai = AiOpponent.Parse(line[AiOpponent.Prefix.Length..]);
            if (line.StartsWith(SecretPrefix, StringComparison.Ordinal)) ReadSecret(line[SecretPrefix.Length..], secrets);
            if (!line.StartsWith(SeatPrefix, StringComparison.Ordinal)) continue;
            seat = line[SeatPrefix.Length..] switch
            {
                "P1" => 0,
                "P2" => 1,
                var other => throw new FormatException($"Unknown advisor seat '{other}'"),
            };
        }
        var game = MoveLog.Parse(text);
        var session = new AdvisorSession(game.Rules, seat, game, ai);
        foreach (var (index, line) in secrets) session._secrets[index] = line;
        session.Replay([.. game.Log]);
        session.Deal();
        return session;
    }

    /// <summary>The saved form: the Move Log with the seat as a comment after the rules header.</summary>
    public string Write()
    {
        var sb = new StringBuilder();
        sb.Append(MoveLog.VersionLine).Append('\n');
        sb.Append(Rules).Append('\n');
        sb.Append(SeatPrefix).Append(Seat == 0 ? "P1" : "P2").Append('\n');
        if (Ai is not null) sb.Append(AiOpponent.Prefix).Append(Ai.Write()).Append('\n');
        foreach (var (index, line) in _secrets)
            sb.Append(SecretPrefix).Append(index).Append(' ').Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(line))).Append('\n');
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

    // ---------------------------------------------------------------- playing against the AI (ticket 36)

    /// <summary>True when it is the AI's decision in a game against the AI.</summary>
    public bool AiToMove => Ai is not null && State.Step == StepKind.Decision && State.PlayerToDecide == AiSeat;

    /// <summary>
    /// In a game against the AI, deals every Reveal the game is waiting for from a seeded random Chance Source (like
    /// Self-Play): the first player, the Progress tokens, the draft offers, card flips, the Guild backs, the owner's
    /// Great Library draw and the Statue of Liberty's returned cards. The AI's own Great Library draw is private, so it
    /// is logged redacted (<see cref="Replay"/> does that) and only drawn for the AI's eyes in <see cref="AiObservation"/>.
    /// Each Reveal's seed comes from the game's seed and the log position, so the same log always deals the same.
    /// </summary>
    public void Deal()
    {
        if (Ai is null) return;
        var pantheon = Rules.HasExpansion(Expansions.Pantheon);
        while (State.Step == StepKind.Reveal)
        {
            // Pantheon: deal from the true game, which knows the AI's face-down Divinities and the cards it put back
            // (the Gate, the Pantheon Reveal and later draws must agree with them); the AI's private draws and Divine
            // Theater are logged as the owner sees them and kept as secrets. A base game deals from the log's state, as before.
            var chance = new RandomChanceSource(AiOpponent.Mix(Ai.Seed, (ulong)Log.Count, 1));
            if (!pantheon)
            {
                Enter(MoveLog.Format(chance.Next(State)));
                continue;
            }
            EnterSeen(chance.Next(TrueGame().State));
        }
    }

    /// <summary>
    /// Enters an entry of the AI game as the owner sees it (<see cref="MoveLog.AsSeenBy"/>); when that hides something,
    /// the true line is kept as a secret at its log index.
    /// </summary>
    void EnterSeen(LogEntry entry)
    {
        var seen = MoveLog.AsSeenBy(entry, Seat);
        var at = Log.Count;
        Enter(MoveLog.Format(seen));
        if (!ReferenceEquals(seen, entry)) _secrets[at] = MoveLog.Format(entry);
    }

    /// <summary>
    /// The game as it really is in a game against the AI: the log with the AI's private lines put back (the owner's
    /// own lines are already named in the log). Without secrets it is the log's game itself.
    /// </summary>
    public Game TrueGame()
    {
        if (_secrets.Count == 0) return Game;
        var game = new Game(Rules);
        for (var i = 0; i < Log.Count; i++)
            game.Apply(_secrets.TryGetValue(i, out var line) ? MoveLog.ParseEntry(line, game.State) : Log[i]);
        return game;
    }

    /// <summary>
    /// What the AI sees when it is to decide: its own Observation of the real game. When its Great Library draw is due
    /// (logged "? ? ?" because the owner may not see it), the 3 tokens are drawn here from the box with a seed from the
    /// game's seed and the draw's log position, so asking again gives the same draw; the owner's view never holds it.
    /// </summary>
    public Observation AiObservation()
    {
        if (Ai is null) throw new InvalidOperationException("Not a game against the AI");
        if (!State.LibraryDrawHidden || State.PlayerToDecide != AiSeat) return TrueGame().State.Observe(AiSeat);
        var at = Log.ToList().FindLastIndex(e => e is LibraryReveal { Redacted: true } l && l.Player == AiSeat);
        var box = ProgressTokens.In(State.TokensSetAside).ToArray();
        var rng = new Rng(AiOpponent.Mix(Ai.Seed, (ulong)at, 2));
        for (var i = 0; i < GameState.LibraryDrawSize; i++)
        {
            var j = i + rng.Next(box.Length - i);
            (box[i], box[j]) = (box[j], box[i]);
        }
        var draw = box[..GameState.LibraryDrawSize];
        Array.Sort(draw);
        var game = new Game(Rules);
        for (var i = 0; i < Log.Count; i++)
            game.Apply(i == at ? new LibraryReveal(AiSeat, draw)
                : _secrets.TryGetValue(i, out var line) ? MoveLog.ParseEntry(line, game.State) : Log[i]);
        return game.State.Observe(AiSeat);
    }

    /// <summary>The AI's search seed for the current position (from the game's seed and the log position).</summary>
    public ulong AiSeed => Ai is null ? 0 : AiOpponent.Mix(Ai.Seed, (ulong)Log.Count, 3);

    /// <summary>Enters the AI's Move, then deals what it revealed.</summary>
    public void EnterAiMove(Move move)
    {
        if (!AiToMove) throw new EntryException("It is not the AI's turn");
        // A face-down placement or a Divine Theater restack is private: the log shows "P2 divinity ? 4".
        EnterSeen(new MoveEntry(AiSeat, move));
        Deal();
    }

    /// <summary>
    /// The owner's Move in a game against the AI (one line, or a Statue keep/give pair): only the owner's own Moves are
    /// accepted (the program deals the Reveals and the AI plays its seat). Then deals what it revealed.
    /// </summary>
    public void EnterOwn(IReadOnlyList<string> lines)
    {
        if (Ai is null) throw new InvalidOperationException("Not a game against the AI");
        if (State.IsOver) throw new EntryException("The game is over. Start a new game from the New game button.");
        if (State.Step != StepKind.Decision || State.PlayerToDecide != Seat)
            throw new EntryException("It is the AI's turn: wait for its Move.");
        var before = Game;
        try
        {
            foreach (var line in lines)
            {
                LogEntry entry;
                try
                {
                    entry = MoveLog.ParseEntry(line.Trim(), State);
                }
                catch (Exception e) when (e is FormatException or KeyNotFoundException or InvalidOperationException or ArgumentException)
                {
                    throw new EntryException(e.Message);
                }
                if (entry is not MoveEntry m || m.Player != Seat)
                    throw new EntryException("Against the AI you enter only your own Moves: the program deals the cards and the AI plays its seat.");
                Enter(line);
            }
            Deal();
        }
        catch (EntryException)
        {
            Game = before;
            throw;
        }
    }

    /// <summary>
    /// Takes back the owner's last Move and everything after it (the AI's reply and the cards dealt), counting a
    /// takeback. Not allowed once the game is over: the result is recorded and stands.
    /// </summary>
    public void TakeBack()
    {
        if (Ai is null) throw new InvalidOperationException("Not a game against the AI");
        if (State.IsOver) throw new EntryException("The game is over and its result is recorded: it can't be taken back.");
        var entries = Log.ToList();
        var last = entries.FindLastIndex(e => e is MoveEntry m && m.Player == Seat);
        if (last < 0) throw new EntryException("You have no Move to take back yet.");
        Replay(entries.Take(last).ToList());
        Ai = Ai with { Takebacks = Ai.Takebacks + 1 };
    }

    /// <summary>True when the owner has a Move to take back (a game against the AI that isn't over).</summary>
    public bool CanTakeBack => Ai is not null && !State.IsOver && Log.Any(e => e is MoveEntry m && m.Player == Seat);

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
            case DivinityDrawReveal { Redacted: false } d when d.Player != Seat:
                return $"P{d.Player + 1}'s Divinity draw is private: only they see it, so it stays '? ?'";
            case DivinityDrawReveal { Redacted: true } d when d.Player == Seat:
                return "Name the Divinities you drew (top card first)";
            case TheaterReveal { Redacted: false } t when t.Player != Seat:
                return $"P{t.Player + 1}'s Divine Theater is private: only they see the deck";
            case TheaterReveal { Redacted: true } t when t.Player == Seat:
                return "Name the cards of the deck your Divine Theater shows (top card first)";
            case MoveEntry { Move.Kind: MoveKind.Divinity } m when m.Player != Seat && m.Move.Divinity != PantheonState.Hidden:
                return $"P{m.Player + 1} places a Divinity face down: you see only the space ('P{m.Player + 1} divinity ? <space>')";
            case MoveEntry { Move.Kind: MoveKind.TheaterOrder } m when m.Player != Seat && m.Move.Divinity != PantheonState.Hidden:
                return $"P{m.Player + 1}'s restack is private: it stays '?'";
            case SlotReveal r when r.Age == s.Age:
                var named = new HashSet<int>();
                foreach (var (slot, card) in r.Cards)
                {
                    if (slot < 0 || slot >= GameState.SlotCount || !GameData.IsCard(card)) continue;
                    var def = GameData.Card(card);
                    var name = def.Name;
                    // Pantheon: Grand Temple backs take the Guild backs' place (and the Guilds are not used).
                    var back = s.Pantheon is null ? "Guild" : "Grand Temple";
                    var special = s.Pantheon is null ? def.IsGuild : def.Color == CardColor.Temple;
                    if (s.SlotCard(slot) >= 0)
                        return $"Slot {s.Layout.SlotName(slot)} already shows {GameData.Card(s.SlotCard(slot)).Name}; rewind the log to change it";
                    if (s.Seen.Contains(card) || !named.Add(card))
                        return $"{name} has already been revealed: every card is in the game only once";
                    if (s.SlotCard(slot) == GameState.HiddenGuild && !special)
                        return $"Slot {s.Layout.SlotName(slot)} shows a {back} back, but {name} is not a {back}";
                    if (s.SlotCard(slot) == GameState.Hidden && special)
                        return $"{name} is a {back}, but slot {s.Layout.SlotName(slot)} does not show a {back} back";
                    if (!special && !def.IsGuild && def.Age != s.Age)
                        return $"{name} is an Age {def.Age} card, not Age {s.Age}";
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
        // Secrets belong to log lines; lines cut off take theirs along (a takeback).
        foreach (var index in _secrets.Keys.Where(i => i >= entries.Count).ToList()) _secrets.Remove(index);
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
        while (game.State.Step == StepKind.Reveal && game.State.SlotsToReveal == 0 && HiddenRevealDue(game.State) is { } hidden)
            game.Apply(hidden);
        Game = game;
    }

    /// <summary>
    /// The opponent's private Reveal that is due, as this table logs it: their Great Library draw, and in a Pantheon game
    /// entered by hand their Divinity draw ("reveal P2 divinities greek ? ?") or their Divine Theater ("reveal P2 theater
    /// greek ? ? ?"). Against the AI the Pantheon ones are dealt from the true game instead (<see cref="Deal"/>).
    /// </summary>
    LogEntry? HiddenRevealDue(GameState s)
    {
        if (s.AwaitingLibraryDraw && s.PlayerToDecide != Seat) return LibraryReveal.Hidden(s.PlayerToDecide);
        if (Ai is not null || s.Pantheon is not { } p) return null;
        // Only once no other Pantheon Reveal comes first (the Engine's order: Zeus, the Gate, the Theater, setup, the draw).
        if (p.AwaitingZeusDiscard || p.AwaitingGateReveal || s.PendingCount == 0) return null;
        var d = s.Pending(0);
        if (d.Player == Seat) return null;
        if (p.AwaitingTheaterReveal) return TheaterReveal.Hidden(d.Player, p.TheaterDeck, p.DeckSize(p.TheaterDeck));
        if (p.AwaitingMythologyTokens || p.AwaitingPantheon || p.AwaitingOfferings || p.AwaitingEnki) return null;
        if (d.Kind == DecisionKind.PlaceDivinity && !p.HasDraw)
        {
            var m = (Mythology)d.Wonder;
            return DivinityDrawReveal.Hidden(d.Player, m, Math.Min(2, p.DeckSize(m)));
        }
        return null;
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

    static void ReadSecret(string text, SortedDictionary<int, string> secrets)
    {
        var space = text.IndexOf(' ');
        try
        {
            if (space > 0 && int.TryParse(text.AsSpan(0, space), out var index) && index >= 0)
            {
                secrets[index] = Encoding.UTF8.GetString(Convert.FromBase64String(text[(space + 1)..].Trim()));
                return;
            }
        }
        catch (FormatException)
        {
        }
        throw new FormatException($"Bad advisor ai-secret line: '{text}'");
    }
}
