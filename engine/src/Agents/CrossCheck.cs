using System.Diagnostics;
using System.Text;
using SevenWondersDuel.Engine;
using SevenWondersDuel.Engine.Data;

namespace SevenWondersDuel.Agents;

/// <summary>
/// The cross-check against indjev99's C++ engine (spec A11 item 5, ticket 11): seeded random games are played in our
/// Engine, then the same Moves and Reveals are fed to the C++ engine through its driver
/// (<c>tools/crosscheck/driver.cpp</c>). Before every Move both engines must offer the same legal Moves, agree on both
/// players' coins and the Conflict Pawn, and in the end on the winner, the victory type and both scores. Base game,
/// <c>start-choice=new</c> only: that is the C++ engine's start-of-Age rule, and it has no promos. Opt-in and out of
/// CI: it needs the driver built by <c>tools/crosscheck/build.sh</c>.
/// </summary>
public static class CrossCheck
{
    /// <summary>The C++ engine's start-of-Age rule (the weaker military chooses; on a tie, whoever took the last card).</summary>
    public static readonly Ruleset Rules = Ruleset.Default;

    /// <summary>Victory types and draws the frozen games must cover, and how many of each to freeze (about 10 in all).</summary>
    static readonly (string Kind, int Count)[] FreezePlan = [("military", 3), ("science", 3), ("civilian", 3), ("draw", 1)];

    /// <summary>Cross-checks <paramref name="games"/> random games from <paramref name="firstSeed"/>, one driver process per thread.</summary>
    public static CrossCheckReport Run(string driverPath, int games, ulong firstSeed = 0, int? threads = null)
    {
        var clock = Stopwatch.StartNew();
        var outcomes = new GameCheck[games];
        var workers = Math.Max(1, Math.Min(threads ?? Environment.ProcessorCount, games));
        var tasks = Enumerable.Range(0, workers).Select(w => Task.Run(() =>
        {
            using var driver = new CppDriver(driverPath);
            for (var i = w; i < games; i += workers)
                outcomes[i] = CheckGame(driver, firstSeed + (ulong)i);
        })).ToArray();
        Task.WaitAll(tasks);

        var frozen = new List<FrozenGame>();
        foreach (var (kind, count) in FreezePlan)
            foreach (var o in outcomes.Where(o => o.Disagreement is null && o.Kind == kind).Take(count))
                frozen.Add(new FrozenGame(o.Seed, GameRunner.PlayRandom(o.Seed, Rules).WriteLog(), o.Cpp!));

        int Count(string kind) => outcomes.Count(o => o.Disagreement is null && o.Kind == kind);
        return new CrossCheckReport(games, Count("civilian"), Count("military"), Count("science"), Count("draw"),
            outcomes.Where(o => o.Disagreement is not null).Select(o => o.Disagreement!).ToList(), frozen,
            clock.Elapsed.TotalSeconds);
    }

    /// <summary>How one game went: the C++ engine's result if both engines agreed throughout, otherwise the first disagreement.</summary>
    public sealed record GameCheck(ulong Seed, CppResult? Cpp, string? Disagreement)
    {
        /// <summary>"civilian", "military", "science" or "draw" (null after a disagreement).</summary>
        public string? Kind => Cpp is null ? null : Cpp.Winner < 0 ? "draw" : Cpp.Victory.ToString().ToLowerInvariant();
    }

    /// <summary>Plays seed <paramref name="seed"/> in our Engine and replays it through the C++ engine.</summary>
    public static GameCheck CheckGame(CppDriver driver, ulong seed)
    {
        var game = GameRunner.PlayRandom(seed, Rules);
        try
        {
            var (cpp, why) = Compare(driver, game);
            return new GameCheck(seed, why is null ? cpp : null, why is null ? null : $"seed {seed}: {why}");
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or FormatException)
        {
            return new GameCheck(seed, null, $"seed {seed}: driver failure: {e.Message}");
        }
    }

    /// <summary>Our state just before one of our Moves.</summary>
    sealed record Checkpoint(int LogIndex, MoveEntry Entry, HashSet<string> Legal, int Coins0, int Coins1, int Pawn);

    /// <summary>Feeds a finished game to the C++ engine; returns its result and the first disagreement (null if none).</summary>
    public static (CppResult? Cpp, string? Disagreement) Compare(CppDriver driver, Game game)
    {
        var map = driver.Map;

        // Pass 1: replay our log, noting the legal Moves before each Move and every Reveal's content.
        var replay = GameState.New(game.Rules);
        var checkpoints = new List<Checkpoint>();
        var first = -1;
        var tokens = new Queue<int>();
        var wonders = new Queue<int>();
        var guilds = new Queue<int>();
        var library = new Queue<int>();
        var slotCards = new Dictionary<(int Age, int Slot), int>();
        for (var i = 0; i < game.Log.Count; i++)
        {
            switch (game.Log[i])
            {
                case MoveEntry m:
                    checkpoints.Add(new Checkpoint(i, m, replay.LegalMoves().Select(MoveLog.FormatMove).ToHashSet(),
                        replay.Coins(0), replay.Coins(1), replay.ConflictPawn));
                    replay.Apply(m.Move);
                    continue;
                case FirstPlayerReveal f: first = f.Player; break;
                case TokenReveal t: foreach (var x in t.Tokens) tokens.Enqueue(x); break;
                case DraftReveal d: foreach (var x in d.Wonders) wonders.Enqueue(x); break;
                case GuildPatternReveal g: foreach (var x in g.Slots) guilds.Enqueue(x); break;
                case LibraryReveal l: foreach (var x in l.Tokens) library.Enqueue(x); break;
                case SlotReveal r: foreach (var (slot, card) in r.Cards) slotCards[(r.Age, slot)] = card; break;
            }
            replay.Apply((Reveal)game.Log[i]);
        }
        var ours = game.State.Result ?? throw new InvalidOperationException("our game did not finish");

        // Pass 2: drive the C++ engine. It asks for Reveals in its own order (e.g. a Wonder's choice before the flips
        // its Age card caused), so Reveals are served from what pass 1 collected, Moves in log order.
        var status = CppStatus.Parse(driver.Send("new"));
        var next = 0;
        for (var steps = 0; !status.End; steps++)
        {
            if (steps > 2000) return (null, "the C++ game does not end");
            CppAction action;
            if (status.Expected.Type < 0)
            {
                var e = status.Expected;
                int? arg = e.Type switch
                {
                    CppAction.RevealFirstPlayer => first,
                    CppAction.RevealGameToken => tokens.TryDequeue(out var t) ? map.Token(t) : null,
                    CppAction.RevealWonder => wonders.TryDequeue(out var w) ? map.Wonder(w) : null,
                    CppAction.RevealGuild => guilds.TryDequeue(out var g) ? g : null,
                    CppAction.RevealBoxToken => library.TryDequeue(out var b) ? map.Token(b) : null,
                    CppAction.RevealPyramidCard => slotCards.TryGetValue((status.Age + 1, e.Arg2), out var c) ? map.Card(c) : null,
                    _ => null,
                };
                if (arg is null)
                    return (null, $"the C++ engine wants a Reveal our game never made ({e}, C++ age {status.Age}) "
                        + $"after our log entry #{(next > 0 ? checkpoints[next - 1].LogIndex : 0)}");
                action = e with { Arg1 = arg.Value };
            }
            else
            {
                if (next == checkpoints.Count)
                    return (null, $"the C++ engine wants a Move ({status.Expected}) after our game ended ({ours})");
                var cp = checkpoints[next++];
                var where = $"before log entry #{cp.LogIndex} '{cp.Entry}'";
                if (status.Actor != cp.Entry.Player)
                    return (null, $"{where}: the C++ engine has P{status.Actor + 1} to move");
                var cppMoves = new Dictionary<string, CppAction>();
                foreach (var a in status.Legal) cppMoves[map.Describe(a, status.Actor)] = a;
                if (!cppMoves.Keys.ToHashSet().SetEquals(cp.Legal))
                    return (null, $"{where}: legal Moves differ; only ours: [{string.Join(", ", cp.Legal.Except(cppMoves.Keys).Order())}]"
                        + $"; only C++: [{string.Join(", ", cppMoves.Keys.Except(cp.Legal).Order())}]");
                if (status.Coins0 != cp.Coins0 || status.Coins1 != cp.Coins1)
                    return (null, $"{where}: coins differ; ours {cp.Coins0}/{cp.Coins1}, C++ {status.Coins0}/{status.Coins1}");
                if (status.Military0 - status.Military1 != cp.Pawn)
                    return (null, $"{where}: Conflict Pawn differs; ours {cp.Pawn}, C++ {status.Military0 - status.Military1}");
                action = cppMoves[MoveLog.FormatMove(cp.Entry.Move)];
            }
            var reply = driver.Send($"do {action.Type} {action.Arg1} {action.Arg2}");
            if (reply.StartsWith("err", StringComparison.Ordinal))
                return (null, $"the C++ engine rejected {action}: {reply[3..].Trim()}");
            status = CppStatus.Parse(reply);
        }

        if (next != checkpoints.Count)
            return (null, $"the C++ game ended ({status.Result}) before our log entry #{checkpoints[next].LogIndex} '{checkpoints[next].Entry}'");
        var cpp = status.Result!;
        var mismatch = new List<string>();
        if (cpp.Winner != ours.Winner || (ours.Winner >= 0 && cpp.Victory != ours.Victory)) mismatch.Add("result");
        if (cpp.Score0 != ours.P1.Total || cpp.Score1 != ours.P2.Total) mismatch.Add("scores");
        if (cpp.Blue0 != ours.P1[ScoreCategory.Blue] || cpp.Blue1 != ours.P2[ScoreCategory.Blue]) mismatch.Add("blue points");
        if (cpp.Coins0 != game.State.Coins(0) || cpp.Coins1 != game.State.Coins(1)) mismatch.Add("coins");
        if (Math.Clamp(cpp.Military0 - cpp.Military1, -GameState.CapitalDistance, GameState.CapitalDistance) != game.State.ConflictPawn)
            mismatch.Add("Conflict Pawn");
        return mismatch.Count == 0 ? (cpp, null)
            : (cpp, $"final {string.Join(", ", mismatch)} differ; ours: {ours} (coins {game.State.Coins(0)}/{game.State.Coins(1)}, "
                + $"pawn {game.State.ConflictPawn}); C++: {cpp}");
    }
}

/// <summary>What a cross-check run found, and the games it froze.</summary>
public sealed record CrossCheckReport(int Games, int Civilian, int Military, int Science, int Draws,
    IReadOnlyList<string> Disagreements, IReadOnlyList<FrozenGame> Frozen, double Seconds)
{
    public bool Passed => Disagreements.Count == 0;

    public override string ToString() =>
        $"{Games:N0} games in {Seconds:N1} s: {Civilian:N0} civilian, {Military:N0} military, {Science:N0} science, {Draws:N0} draws; "
        + (Passed ? "zero disagreements" : $"{Disagreements.Count} DISAGREE");
}

/// <summary>
/// A cross-checked game kept as a scripted test (spec A11 item 2): its Move Log and the C++ engine's verdict, written as
/// <c># expect</c> comment lines the Move Log parser skips.
/// </summary>
public sealed record FrozenGame(ulong Seed, string Log, CppResult Expected)
{
    public string Kind => Expected.Winner < 0 ? "draw" : Expected.Victory.ToString().ToLowerInvariant();
    public string FileName => $"{Kind}-seed{Seed}.log";

    public string ToText() =>
        $"# Frozen by `7wd crosscheck` (ticket 11): `7wd random --seed {Seed}`, cross-checked against indjev99's C++ engine\n"
        + "# (MIT, @01ed7a5f): every legal-Move set, coins and pawn agreed. The expect line is the C++ engine's result.\n"
        + $"# expect {(Expected.Winner < 0 ? "draw" : $"P{Expected.Winner + 1}")} {Expected.Victory.ToString().ToLowerInvariant()}"
        + $" {Expected.Score0} {Expected.Score1}\n"
        + Log;
}

/// <summary>One C++ action: a type and two arguments (indjev99's <c>src/game/action.h</c>).</summary>
public readonly record struct CppAction(int Type, int Arg1, int Arg2)
{
    public const int PlayPyramidCard = 0, BuildGameToken = 1, BuildBoxToken = 2, BuildDiscarded = 3, DestroyObject = 4,
        SelectWonder = 5, ChooseStartPlayer = 6;
    public const int RevealGuild = -1, RevealPyramidCard = -2, RevealGameToken = -3, RevealBoxToken = -4,
        RevealWonder = -5, RevealFirstPlayer = -6;
    public const int Build = -1, Discard = -2;

    public static CppAction Parse(string text)
    {
        var p = text.Split(':');
        return new CppAction(int.Parse(p[0]), int.Parse(p[1]), int.Parse(p[2]));
    }

    public override string ToString() => $"C++ action {Type}:{Arg1}:{Arg2}";
}

/// <summary>The C++ engine's verdict: its result code from P1's side, both scores, blue points, coins and shields.</summary>
public sealed record CppResult(int Code, int Score0, int Score1, int Blue0, int Blue1, int Coins0, int Coins1, int Military0, int Military1)
{
    /// <summary>0 or 1, or -1 for a draw.</summary>
    public int Winner => Code > 0 ? 0 : Code < 0 ? 1 : -1;

    /// <summary>Codes 1 (civilian) and 4 (civilian, decided on blue points) are both a civilian victory.</summary>
    public VictoryType Victory => Math.Abs(Code) switch
    {
        2 => VictoryType.Science,
        3 => VictoryType.Military,
        _ => VictoryType.Civilian,
    };

    public override string ToString() =>
        (Winner < 0 ? "draw" : $"P{Winner + 1} wins ({Victory.ToString().ToLowerInvariant()}{(Math.Abs(Code) == 4 ? ", on blue points" : "")})")
        + $", {Score0}-{Score1} VP, blue {Blue0}-{Blue1}, coins {Coins0}/{Coins1}, shields {Military0}/{Military1}";
}

/// <summary>One status line of the driver: the next expected action (with the legal actions before a Move) or the end.</summary>
public sealed record CppStatus(bool End, CppAction Expected, int Actor, int Age, int Coins0, int Coins1, int Military0, int Military1,
    IReadOnlyList<CppAction> Legal, CppResult? Result)
{
    public static CppStatus Parse(string line)
    {
        var t = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int I(int i) => int.Parse(t[i]);
        if (t.Length >= 10 && t[0] == "end")
            return new CppStatus(true, default, -1, -1, I(6), I(7), I(8), I(9), [],
                new CppResult(I(1), I(2), I(3), I(4), I(5), I(6), I(7), I(8), I(9)));
        if (t.Length >= 10 && t[0] == "next")
            return new CppStatus(false, new CppAction(I(1), I(2), I(3)), I(4), I(5), I(6), I(7), I(8), I(9),
                t.Skip(10).Select(CppAction.Parse).ToList(), null);
        throw new FormatException($"Unexpected driver reply '{line}'");
    }
}

/// <summary>
/// The C++ engine's object ids against our card, Wonder and token indices, built from the driver's names
/// (its display names; slugs are matched by our name table, with the one spelling it does not know).
/// </summary>
public sealed class CppObjectMap
{
    readonly Dictionary<int, int> _cardToCpp = [], _wonderToCpp = [], _tokenToCpp = [];
    readonly Dictionary<int, (char Kind, int Index)> _fromCpp = [];

    /// <summary>Parses the driver's <c>names</c> reply; throws unless every base card, Wonder and token is matched.</summary>
    public CppObjectMap(string namesReply)
    {
        if (!namesReply.StartsWith("names ", StringComparison.Ordinal)) throw new FormatException($"Unexpected driver reply '{namesReply}'");
        foreach (var item in namesReply[6..].Split(';'))
        {
            var eq = item.IndexOf('=');
            var id = int.Parse(item[..eq]);
            var name = item[(eq + 1)..].Replace('_', ' ');
            if (name.StartsWith("Looting", StringComparison.Ordinal)) continue; // its military tokens, never in an action
            var slug = Slug(name);
            if (GameData.TryCardIndex(slug, out var card)) { _cardToCpp[card] = id; _fromCpp[id] = ('c', card); }
            else if (GameData.TryWonderIndex(slug, out var wonder)) { _wonderToCpp[wonder] = id; _fromCpp[id] = ('w', wonder); }
            else if (GameData.TryTokenIndex(slug, out var token)) { _tokenToCpp[token] = id; _fromCpp[id] = ('t', token); }
            else throw new FormatException($"C++ object {id} '{name}' matches no card, Wonder or token");
        }
        var baseWonders = GameData.Wonders.Count(w => !w.IsPromo);
        if (_cardToCpp.Count != GameData.Cards.Length || _wonderToCpp.Count != baseWonders || _tokenToCpp.Count != GameData.Tokens.Length)
            throw new FormatException($"C++ objects matched {_cardToCpp.Count} cards, {_wonderToCpp.Count} Wonders, {_tokenToCpp.Count} tokens; "
                + $"expected {GameData.Cards.Length}, {baseWonders}, {GameData.Tokens.Length}");
    }

    static string Slug(string name)
    {
        if (GameData.SlugForName(name) is { } known) return known;
        if (name == "Glassblower") return "glass-blower";
        var s = name.ToLowerInvariant();
        if (s.StartsWith("the ", StringComparison.Ordinal)) s = s[4..];
        return s.Replace(' ', '-');
    }

    public int Card(int card) => _cardToCpp[card];
    public int Wonder(int wonder) => _wonderToCpp[wonder];
    public int Token(int token) => _tokenToCpp[token];

    /// <summary>A C++ player action in our Move Log words ("build baths", "start me"), for the deciding <paramref name="actor"/>.</summary>
    public string Describe(CppAction a, int actor)
    {
        string Name(int id, char kind) => _fromCpp.TryGetValue(id, out var o) && o.Kind == kind
            ? kind switch { 'c' => GameData.Cards[o.Index].Id, 'w' => GameData.Wonders[o.Index].Id, _ => GameData.Tokens[o.Index].Id }
            : $"?{id}";
        return a.Type switch
        {
            CppAction.PlayPyramidCard => a.Arg2 switch
            {
                CppAction.Build => $"build {Name(a.Arg1, 'c')}",
                CppAction.Discard => $"discard {Name(a.Arg1, 'c')}",
                _ => $"wonder {Name(a.Arg1, 'c')} {Name(a.Arg2, 'w')}",
            },
            CppAction.BuildGameToken => $"token {Name(a.Arg1, 't')}",
            CppAction.BuildBoxToken => $"library {Name(a.Arg1, 't')}",
            CppAction.BuildDiscarded => $"mausoleum {Name(a.Arg1, 'c')}",
            CppAction.DestroyObject => $"destroy {Name(a.Arg1, 'c')}",
            CppAction.SelectWonder => $"draft {Name(a.Arg1, 'w')}",
            CppAction.ChooseStartPlayer => a.Arg1 == actor ? "start me" : "start opponent",
            _ => a.ToString(),
        };
    }
}

/// <summary>The driver process (<c>tools/crosscheck/driver.cpp</c>): one command line in, one reply line out.</summary>
public sealed class CppDriver : IDisposable
{
    readonly Process _process;

    public CppDriver(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"C++ cross-check driver not found at '{path}' (build it with tools/crosscheck/build.sh)", path);
        _process = Process.Start(new ProcessStartInfo(path)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
        }) ?? throw new IOException($"Could not start '{path}'");
        _process.StandardInput.NewLine = "\n";
        _process.StandardInput.AutoFlush = true;
        Map = new CppObjectMap(Send("names"));
    }

    public CppObjectMap Map { get; }

    public string Send(string command)
    {
        _process.StandardInput.WriteLine(command);
        return _process.StandardOutput.ReadLine() ?? throw new IOException("The C++ driver exited");
    }

    public void Dispose()
    {
        try
        {
            _process.StandardInput.Close();
            if (!_process.WaitForExit(5000)) _process.Kill();
        }
        catch (InvalidOperationException) { }
        _process.Dispose();
    }

    /// <summary>
    /// The driver named on the command line, else <c>SEVENWD_CPP_DRIVER</c>, else <c>tools/crosscheck/.cache/7wd-cpp-driver</c>
    /// in the first parent of the working directory that has one.
    /// </summary>
    public static string DefaultPath()
    {
        if (Environment.GetEnvironmentVariable("SEVENWD_CPP_DRIVER") is { Length: > 0 } env) return env;
        var relative = Path.Combine("tools", "crosscheck", ".cache", "7wd-cpp-driver");
        for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, relative))) return Path.Combine(dir.FullName, relative);
        return relative;
    }
}
