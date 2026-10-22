using System.Text.Json;
using SevenWondersDuel.Agents;
using SevenWondersDuel.Engine;

namespace SevenWondersDuel.Advisor;

/// <summary>
/// The opponent of a "Play against the AI" game (ticket 36): an Agent spec (<c>neural:&lt;onnx&gt;,mode=classic</c>,
/// <c>mcts</c>, <c>heuristic</c>, <c>random</c>, ...), the name shown for it, the search budget (simulations, for the
/// search Agents), the game's seed (every Reveal and every AI search seed derives from it and the log position, so a
/// saved game resumes exactly) and how many Moves the owner took back. Saved in the Move Log as one comment line.
/// </summary>
public sealed record AiOpponent(string Spec, string Label, int Simulations, ulong Seed, int Takebacks = 0)
{
    public const string Prefix = "# advisor ai ";
    public const int DefaultSimulations = 800;

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string Write() => JsonSerializer.Serialize(this, Json);

    public static AiOpponent Parse(string json) =>
        Deserialize(json) ?? throw new FormatException("Empty advisor ai line");

    static AiOpponent? Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<AiOpponent>(json, Json);
        }
        catch (JsonException e)
        {
            throw new FormatException($"Bad advisor ai line: {e.Message}");
        }
    }

    /// <summary>A seed for one use (<paramref name="purpose"/>) at one log position: SplitMix-style mixing.</summary>
    public static ulong Mix(ulong seed, ulong position, ulong purpose)
    {
        var x = seed ^ (position * 0x9E3779B97F4A7C15UL) ^ (purpose * 0xD1B54A32D192ED03UL);
        x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
        x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
        return x ^ (x >> 31);
    }

    /// <summary>True for the Agents that search, where <see cref="Simulations"/> sets the budget.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Searches => AdviceSettings.Split(Spec).Name is "mcts" or "neural";

    /// <summary>
    /// Throws a <see cref="FormatException"/> (or the network's load error) unless the spec builds an Agent, and a
    /// <see cref="NotSupportedException"/> when it can't play games of <paramref name="rules"/> (a base network in a
    /// Pantheon game).
    /// </summary>
    public void Validate(Ruleset? rules = null)
    {
        if (Simulations < 1) throw new FormatException("The AI needs at least 1 simulation");
        var agent = AiPlayer.Create(this, 0);
        if (rules is not null) (agent as NeuralAgent)?.EncodingFor(rules);
    }
}

/// <summary>Builds the AI opponent's Agent and asks it for a Move (it sees only its own Observation).</summary>
public static class AiPlayer
{
    public static IAgent Create(AiOpponent ai, ulong seed) =>
        ai.Searches ? AdviceSearch.CreateAgent(ai.Spec, ai.Simulations, seed) : AgentSpecs.Create(ai.Spec, seed);

    /// <summary>The AI's Move for the decision at the front of <paramref name="observation"/>; a search can be cancelled.</summary>
    public static Move Choose(Observation observation, AiOpponent ai, ulong seed, CancellationToken cancel = default)
    {
        var moves = observation.LegalMoves();
        if (moves.Count == 0) throw new InvalidOperationException("The AI has no legal Move");
        if (moves.Count == 1) return moves[0];
        return Create(ai, seed) switch
        {
            MctsAgent mcts => mcts.Search(observation, cancel).Best,
            NeuralAgent neural => neural.Search(observation, cancel).Best,
            var agent => agent.ChooseMove(observation),
        };
    }

    /// <summary>"AI played: Build Baths": the AI's Move in the owner's words.</summary>
    public static string Describe(GameState before, Move move) => move.Kind switch
    {
        MoveKind.StartMe => "AI played: the AI starts this Age",
        MoveKind.StartOpponent => "AI played: you start this Age",
        MoveKind.Library => $"AI played: kept {Engine.Data.GameData.Token(move.Token).Name} from its Great Library draw",
        _ => $"AI played: {AdviceTable.Label(before, move)}",
    };
}

/// <summary>One choice in the new-game screen's opponent list.</summary>
public sealed record OpponentOption(string Group, string Text, string Label, string Spec);

/// <summary>
/// The opponents offered on the new-game screen: every Run under the runs folder (its best Generation first, then each
/// Generation's network, all with the classic evaluation search, which played better than Gumbel in ticket 26's
/// Matches), then the MCTS Anchor, the Heuristic and Random. The runs folder is only read.
/// </summary>
public static class Opponents
{
    /// <summary><c>SEVENWD_RUNS</c>, else <c>C:\7wd-runs</c> on Windows and <c>~/7wd-runs</c> elsewhere.</summary>
    public static string DefaultRunsFolder =>
        Environment.GetEnvironmentVariable("SEVENWD_RUNS") is { Length: > 0 } env ? env
        : OperatingSystem.IsWindows() ? @"C:\7wd-runs"
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "7wd-runs");

    public static string NetworkSpec(string onnx) => $"neural:{Path.GetFullPath(onnx)},mode=classic";

    /// <param name="expansions">Pantheon: only Runs trained for it (their <c>run.json</c> says Encoding v2) are listed.</param>
    public static List<OpponentOption> List(string? runsFolder, Expansions expansions = Expansions.None)
    {
        var list = new List<OpponentOption>();
        if (runsFolder is not null && Directory.Exists(runsFolder))
        {
            // The default choice comes first: the main Run's best network; until it has one, the Run it continues from
            // (its warm start, run.json init_from); then the Run whose best network changed last.
            var dirs = Directory.GetDirectories(runsFolder);
            var preferred = PreferredRun(runsFolder);
            foreach (var run in dirs
                         .Where(r => expansions == Expansions.None || EncodingVersionOf(r) >= 2)
                         .OrderByDescending(r => preferred is not null && SamePath(r, preferred))
                         .ThenByDescending(r => File.Exists(Path.Combine(r, "best.json")) ? File.GetLastWriteTimeUtc(Path.Combine(r, "best.json")) : DateTime.MinValue)
                         .ThenBy(r => r, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(run);
                var net = Path.Combine(run, "net");
                var gens = Directory.Exists(net)
                    ? Directory.GetFiles(net, "gen-*.onnx")
                        .Select(f => (File: f, Gen: int.TryParse(Path.GetFileNameWithoutExtension(f)[4..], out var g) ? g : -1))
                        .Where(x => x.Gen >= 0).OrderByDescending(x => x.Gen).ToList()
                    : [];
                var best = BestOf(run);
                if (best is { } b && File.Exists(b.File))
                    list.Add(new OpponentOption(name, $"{name}: best network (Gen {b.Gen})", $"{name} Gen {b.Gen}", NetworkSpec(b.File)));
                foreach (var (file, gen) in gens)
                    list.Add(new OpponentOption(name, $"{name}: Gen {gen}", $"{name} Gen {gen}", NetworkSpec(file)));
            }
        }
        list.Add(new OpponentOption("Built-in", "MCTS Anchor (no network)", "MCTS Anchor", "mcts"));
        list.Add(new OpponentOption("Built-in", "Heuristic (weak)", "Heuristic", "heuristic"));
        list.Add(new OpponentOption("Built-in", "Random (very weak)", "Random", "random"));
        return list;
    }

    /// <summary>A Run's Encoding version from its <c>run.json</c> (1 when it doesn't say, as for the first Runs).</summary>
    static int EncodingVersionOf(string run)
    {
        try
        {
            var path = Path.Combine(run, "run.json");
            if (!File.Exists(path)) return 1;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.TryGetProperty("encoding_version", out var v) && v.TryGetInt32(out var n) ? n : 1;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return 1;
        }
    }

    /// <summary>The Run the main training Run is called by (ticket 28: <c>C:\7wd-runs\main</c>).</summary>
    public const string MainRun = "main";

    /// <summary>
    /// The Run offered first: <see cref="MainRun"/> once it has a best network; before that, the Run it warm-starts
    /// from (<c>init_from.run</c> in its <c>run.json</c>, e.g. pilot-1-small); null when there is no main Run.
    /// </summary>
    static string? PreferredRun(string runsFolder)
    {
        var main = Path.Combine(runsFolder, MainRun);
        if (!Directory.Exists(main)) return null;
        if (BestOf(main) is { } b && File.Exists(b.File)) return main;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(main, "run.json")));
            if (doc.RootElement.TryGetProperty("init_from", out var from) && from.TryGetProperty("run", out var parent)
                && parent.GetString() is { } p && Directory.Exists(p))
                return p;
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidOperationException or UnauthorizedAccessException)
        {
        }
        return main;
    }

    static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>A Run's best Generation from its <c>best.json</c> (<c>{"generation": 3, "model": "net/gen-0003.onnx"}</c>), or null.</summary>
    static (int Gen, string File)? BestOf(string run)
    {
        try
        {
            var path = Path.Combine(run, "best.json");
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            var gen = root.GetProperty("generation").GetInt32();
            var model = root.GetProperty("model").GetString();
            return model is null ? null : (gen, Path.GetFullPath(Path.Combine(run, model)));
        }
        catch (Exception e) when (e is IOException or JsonException or KeyNotFoundException or InvalidOperationException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>One finished game against the AI, as kept in <c>vs-ai.json</c>.</summary>
public sealed record AiGameRecord(DateTime Finished, string Opponent, string Spec, int Simulations, int Seat, string Winner,
    string Victory, int YourVp, int AiVp, int Takebacks, string Seed);

/// <summary>The tally against one opponent.</summary>
public sealed record TallyView(string Opponent, int You, int Ai, int Draws, string Text);
