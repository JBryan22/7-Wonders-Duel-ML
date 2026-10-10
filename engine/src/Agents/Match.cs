using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using SevenWondersDuel.Engine;

namespace SevenWondersDuel.Agents;

/// <summary>
/// What a Match plays (spec A5, Matches and Elo): Agent A against Agent B over <see cref="Pairs"/> seed pairs from
/// <see cref="FirstSeed"/>, under the base rules plus <see cref="Promos"/>. Headline Elo comes from base-only Matches; a
/// smaller Match with both promos is reported next to it. <see cref="EloA"/> / <see cref="EloB"/> are an Agent's known
/// rating on the shared scale (Random is always 0); they only affect the report, not the games.
/// </summary>
public sealed record MatchSettings(string AgentA, string AgentB, int Pairs = MatchSettings.DefaultPairs, ulong FirstSeed = 0,
    Promos Promos = Promos.None, double? EloA = null, double? EloB = null)
{
    /// <summary>A Match is 400 games: 200 seed pairs.</summary>
    public const int DefaultPairs = 200;

    public Ruleset Rules => new(Promos: Promos);

    /// <summary>The first line of a Match file: everything that decides the games. Resuming requires the same header.</summary>
    internal MatchHeader Header => new(Match.FormatVersion, AgentA, AgentB, Pairs, FirstSeed, Rules.ToString());
}

internal sealed record MatchHeader(string Format, string A, string B, int Pairs, ulong Seed, string Rules);

/// <summary>Who won one game of a Match: Agent A, Agent B, or nobody.</summary>
public enum MatchWinner { A, B, Draw }

/// <summary>
/// One finished game: the seat Agent A sat in (0 = P1), the winner and how, both final VP totals, how many Moves each
/// Agent made and how long it spent choosing them (wall-clock, logged next to the result but never used to compare
/// Agents), and the whole game's wall-clock.
/// </summary>
public sealed record MatchGame(int SeatA, MatchWinner Winner, VictoryType Victory,
    int VpA, int VpB, int MovesA, int MovesB, double SecondsA, double SecondsB, double Seconds)
{
    /// <summary>Agent A's score for this game: 1 for a win, ½ for a draw, 0 for a loss.</summary>
    [JsonIgnore] public double ScoreA => Winner switch { MatchWinner.A => 1, MatchWinner.Draw => 0.5, _ => 0 };
}

/// <summary>One seed pair: the same seed played twice with the seats swapped (A is P1 in <see cref="First"/>).</summary>
public sealed record MatchPair(int Pair, MatchGame First, MatchGame Second)
{
    [JsonIgnore] public IEnumerable<MatchGame> Games => [First, Second];
}

/// <summary>
/// Plays Matches (spec A5): seed pairs with seats swapped inside each pair so both sides get the same luck, on every
/// core. Pair <c>i</c> uses seed <c>FirstSeed + i</c>; from it come one Chance Source seed (shared by both games of the
/// pair, so the first-player draw, the Progress tokens and the first Wonder offer are identical and later Reveals stay
/// as alike as the diverging games allow) and one seed per Agent (each Agent keeps its seed across the seat swap).
/// Results depend only on the settings, never on thread count or on stopping and resuming.
///
/// With a directory, every finished pair is appended to <c>match.jsonl</c> at once (the header line first), so a stopped
/// Match resumes from its last finished seed pair: pairs already in the file are not replayed. A pair in progress when
/// the Match is stopped is dropped and replayed on resume. <c>summary.json</c> is written at the end of every session.
/// </summary>
public static class Match
{
    public const string FormatVersion = "7wd-match-1";
    public const string FileName = "match.jsonl";
    public const string SummaryName = "summary.json";

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    static readonly JsonSerializerOptions JsonIndented = new(Json) { WriteIndented = true };

    /// <summary>
    /// Plays the pairs not yet finished (all of them without a <paramref name="directory"/>) and reports on every
    /// finished pair. Stopping through <paramref name="cancel"/> returns a report with <see cref="MatchReport.Stopped"/>
    /// set instead of throwing. <paramref name="createAgent"/> replaces <see cref="AgentSpecs.Create"/> (tests).
    /// </summary>
    public static MatchReport Run(MatchSettings settings, string? directory = null, CancellationToken cancel = default,
        int? threads = null, Func<string, ulong, IAgent>? createAgent = null, Action<MatchPair>? onPair = null)
    {
        if (settings.Pairs <= 0) throw new ArgumentOutOfRangeException(nameof(settings), "A Match needs at least one seed pair");
        createAgent ??= AgentSpecs.Create;
        createAgent(settings.AgentA, 0);
        createAgent(settings.AgentB, 0);

        var clock = Stopwatch.StartNew();
        var done = new Dictionary<int, MatchPair>();
        StreamWriter? file = null;
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, FileName);
            foreach (var pair in Load(path, settings)) done[pair.Pair] = pair;
            Save(path, settings, done.Values.OrderBy(p => p.Pair));
            file = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
        }

        var todo = Enumerable.Range(0, settings.Pairs).Where(i => !done.ContainsKey(i)).ToArray();
        var stopped = false;
        try
        {
            var options = new ParallelOptions
            {
                MaxDegreeOfParallelism = threads ?? Environment.ProcessorCount,
                CancellationToken = cancel,
            };
            Parallel.ForEach(todo, options, i =>
            {
                var pair = PlayPair(settings, i, createAgent, cancel);
                lock (done)
                {
                    done[i] = pair;
                    file?.WriteLine(JsonSerializer.Serialize(pair, Json));
                }
                onPair?.Invoke(pair);
            });
        }
        catch (Exception e) when (cancel.IsCancellationRequested && IsCancellation(e))
        {
            stopped = true;
        }
        finally
        {
            file?.Dispose();
        }

        var report = new MatchReport(settings, done.Values.OrderBy(p => p.Pair).ToList(), clock.Elapsed.TotalSeconds, stopped);
        if (directory is not null)
            File.WriteAllText(Path.Combine(directory, SummaryName), JsonSerializer.Serialize(report.Summary(), JsonIndented));
        return report;
    }

    static bool IsCancellation(Exception e) =>
        e is OperationCanceledException || e is AggregateException a && a.Flatten().InnerExceptions.All(i => i is OperationCanceledException);

    /// <summary>Reads the finished pairs of a Match file (none if there is no file). A torn last line is ignored.</summary>
    public static IReadOnlyList<MatchPair> Load(string path, MatchSettings settings)
    {
        if (!File.Exists(path)) return [];
        var lines = File.ReadAllLines(path);
        if (lines.Length == 0) return [];
        var expected = JsonSerializer.Serialize(settings.Header, Json);
        if (lines[0] != expected)
            throw new FormatException($"{path} holds a different Match:\n  {lines[0]}\nnot\n  {expected}\nUse another directory to start a new Match.");
        var pairs = new List<MatchPair>();
        for (var i = 1; i < lines.Length; i++)
        {
            MatchPair? pair;
            try
            {
                pair = JsonSerializer.Deserialize<MatchPair>(lines[i], Json);
            }
            catch (JsonException) when (i == lines.Length - 1)
            {
                break; // the process stopped in the middle of writing this line
            }
            if (pair is null || pair.Pair < 0 || pair.Pair >= settings.Pairs || pair.First is null || pair.Second is null)
                throw new FormatException($"{path} line {i + 1} is not a seed pair of this Match");
            pairs.Add(pair);
        }
        return pairs;
    }

    /// <summary>Rewrites the file as the header plus the given pairs (dropping a torn last line), through a temporary file.</summary>
    static void Save(string path, MatchSettings settings, IEnumerable<MatchPair> pairs)
    {
        var temp = path + ".tmp";
        File.WriteAllLines(temp, [JsonSerializer.Serialize(settings.Header, Json), .. pairs.Select(p => JsonSerializer.Serialize(p, Json))]);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Plays seed pair <paramref name="pair"/>: A as P1, then the same seeds with A as P2.</summary>
    public static MatchPair PlayPair(MatchSettings settings, int pair, Func<string, ulong, IAgent>? createAgent = null,
        CancellationToken cancel = default)
    {
        createAgent ??= AgentSpecs.Create;
        var seeds = new Rng(settings.FirstSeed + (ulong)pair);
        var (chanceSeed, seedA, seedB) = (seeds.NextUInt64(), seeds.NextUInt64(), seeds.NextUInt64());
        MatchGame Play(int seatA) => PlayGame(settings.Rules, new RandomChanceSource(chanceSeed),
            createAgent(settings.AgentA, seedA), createAgent(settings.AgentB, seedB), seatA, cancel);
        return new MatchPair(pair, Play(0), Play(1));
    }

    static MatchGame PlayGame(Ruleset rules, IChanceSource chance, IAgent a, IAgent b, int seatA, CancellationToken cancel)
    {
        var clock = Stopwatch.StartNew();
        var s = GameState.New(rules);
        Span<long> ticks = stackalloc long[2];
        Span<int> moves = stackalloc int[2];
        while (s.Step != StepKind.GameOver)
        {
            if (s.Step == StepKind.Reveal)
            {
                s.Apply(chance.Next(s));
                continue;
            }
            cancel.ThrowIfCancellationRequested();
            var player = s.PlayerToDecide;
            var side = player == seatA ? 0 : 1;
            var start = Stopwatch.GetTimestamp();
            var move = (side == 0 ? a : b).ChooseMove(s.Observe(player));
            ticks[side] += Stopwatch.GetTimestamp() - start;
            moves[side]++;
            s.Apply(move);
        }

        var result = s.Result!;
        var winner = result.IsDraw ? MatchWinner.Draw : result.Winner == seatA ? MatchWinner.A : MatchWinner.B;
        var (sheetA, sheetB) = seatA == 0 ? (result.P1, result.P2) : (result.P2, result.P1);
        static double Seconds(long t) => Math.Round((double)t / Stopwatch.Frequency, 6);
        return new MatchGame(seatA, winner, result.Victory, sheetA.Total, sheetB.Total, moves[0], moves[1],
            Seconds(ticks[0]), Seconds(ticks[1]), Math.Round(clock.Elapsed.TotalSeconds, 6));
    }
}
