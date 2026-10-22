using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using SevenWondersDuel.Engine;

namespace SevenWondersDuel.Agents;

/// <summary>
/// What a Self-Play session plays (spec A7): <see cref="Games"/> games of the neural search against itself with
/// <see cref="Agent"/>'s network and settings (spec: 64 Gumbel simulations), game <c>i</c> from seed
/// <see cref="FirstSeed"/> + i. Each game includes both promo Wonders with probability <see cref="PromoRate"/> (spec
/// default 100%; drawn from the game's seed) and is otherwise base-only. With <see cref="Expansions"/> (Pantheon),
/// every game is played with that expansion (spec-pantheon P-A8); the seeds are drawn exactly as in base Self-Play.
/// </summary>
public sealed record SelfPlaySettings(NeuralSettings Agent, int Games, ulong FirstSeed = 0, double PromoRate = 1,
    Expansions Expansions = Expansions.None)
{
    /// <summary>Simulations per searched decision in training (spec A5), the default for <c>7wd selfplay</c>.</summary>
    public const int DefaultSimulations = 64;

    /// <summary>
    /// The search settings that decide the games, as <c>neural:</c> spec parts (no model, device or threads): on the
    /// CPU, where outputs don't depend on batching, the device and thread count never change a game.
    /// </summary>
    public string SearchSpec
    {
        get
        {
            var a = Agent;
            static string F(double v) => v.ToString("R", CultureInfo.InvariantCulture);
            return a.Mode == NeuralMode.Gumbel
                ? $"sims={a.Simulations},mode=gumbel,m={a.Considered},cvisit={F(a.CVisit)},cscale={F(a.CScale)},gumbel={F(a.GumbelScale)}"
                : $"sims={a.Simulations},mode=classic,c={F(a.Cpuct)},noise={F(a.Noise)},alpha={F(a.Alpha)},temp={F(a.Temperature)}";
        }
    }
}

/// <summary>
/// The first line of <c>games.jsonl</c>: everything that decides the games. Resuming requires the same header (the model
/// path may differ, its contents may not). <see cref="Expansion"/> is the rules header's expansion list (e.g.
/// <c>pantheon</c>); it is left out of the line for base Self-Play, so base headers are unchanged.
/// </summary>
public sealed record SelfPlayHeader(string Format, string Model, string ModelSha256, string Search, int Games, ulong Seed,
    double PromoRate, int EncodingVersion,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Expansion = null);

/// <summary>
/// One Self-Play session's outcome: whether every game is finished (and the shard written), how many games are
/// recorded in all, and this session's throughput: games and searched positions finished, network evaluations
/// and calls (when an ONNX model ran), wall-clock seconds.
/// </summary>
public sealed record SelfPlayReport(bool Complete, int GamesDone, int GamesTotal, int GamesPlayed, long PositionsPlayed,
    long Evaluations, long Batches, double Seconds, int? ShardPositions)
{
    public double GamesPerSecond => Seconds > 0 ? GamesPlayed / Seconds : 0;
    public double PositionsPerSecond => Seconds > 0 ? PositionsPlayed / Seconds : 0;
    public double MeanBatch => Batches > 0 ? (double)Evaluations / Batches : 0;

    public override string ToString() =>
        $"{GamesDone}/{GamesTotal} games recorded{(Complete ? "" : " (stopped: rerun the same command to continue)")}; "
        + $"this session {GamesPlayed:N0} games, {PositionsPlayed:N0} positions in {Seconds:0.0} s: "
        + $"{GamesPerSecond:0.00} games/s, {PositionsPerSecond:0.0} positions/s"
        + (Batches > 0 ? $"; network {Evaluations:N0} evaluations in {Batches:N0} calls (mean batch {MeanBatch:0.0}, {Evaluations / Math.Max(Seconds, 1e-9):N0}/s)" : "")
        + (ShardPositions is { } p ? $"; shard: {p:N0} positions" : "");
}

/// <summary>
/// Self-Play (spec A7, <c>7wd selfplay</c>): the network plays itself, both seats searching with the same settings,
/// many games at once so the shared inference threads get big batches.
/// <para>
/// Every game runs on its own thread (<c>parallel</c> of them at a time) and blocks in the batcher while its position
/// waits for the network; a few inference threads (<c>workers</c>) take turns emptying the queue into network calls.
/// Played Moves come straight from
/// the search (Gumbel: its selected Move; the noise is the exploration). Each searched decision's policy target is
/// kept in the Game Record.
/// </para>
/// <para>
/// Output, in one directory: <c>games.jsonl</c> (a header line, then one Game Record per line, appended and flushed
/// the moment its game finishes: per-game saving, so a stopped session loses only the games in progress, and
/// rerunning the same command resumes); once every game is recorded, the encoded shard in <c>shard/</c>
/// (<see cref="ShardWriter"/>), and <c>summary.json</c> (the last session's report).
/// </para>
/// <para>
/// Game <c>i</c> depends only on the settings, the network and its seed, never on which thread or which batch ran it
/// (on the CPU, where a position's outputs don't depend on its batch), so a Game Record replays exactly from its seeds
/// (<see cref="Replay"/>).
/// </para>
/// </summary>
public static class SelfPlay
{
    public const string FormatVersion = "7wd-selfplay-1";
    public const string FileName = "games.jsonl";
    public const string ShardName = "shard";
    public const string SummaryName = "summary.json";

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    static readonly JsonSerializerOptions JsonIndented = new(Json) { WriteIndented = true };

    /// <summary>
    /// Plays the games of <paramref name="settings"/> not yet in <paramref name="directory"/>, <paramref name="parallel"/>
    /// at a time (default <see cref="DefaultParallel"/>) with <paramref name="workers"/> inference threads (default
    /// <see cref="DefaultWorkers"/>), then writes the shard. Stopping through <paramref name="cancel"/> keeps the
    /// finished games and returns an incomplete report.
    /// </summary>
    public static SelfPlayReport Run(SelfPlaySettings settings, string directory, int? parallel = null,
        CancellationToken cancel = default, Action<GameRecord>? onGame = null, int? workers = null)
    {
        if (settings.Games <= 0) throw new ArgumentOutOfRangeException(nameof(settings), "Self-Play needs at least one game");
        if (settings.PromoRate is < 0 or > 1 || double.IsNaN(settings.PromoRate))
            throw new ArgumentOutOfRangeException(nameof(settings), "The promo rate is a probability, 0..1");
        var clock = Stopwatch.StartNew();
        // The network decides the Encoding (its declared version); a network that can't encode the games is refused
        // before any game (a Pantheon session with an Encoding v1 network).
        using var agents = AgentSource.Open(settings.Agent, maxBatch: Math.Max(1, Math.Min(settings.Games, parallel ?? DefaultParallel)),
            workers ?? DefaultWorkers);
        var encoding = agents.EncodingFor(SessionRules(settings));
        var header = Header(settings, encoding);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, FileName);
        var done = Load(path, header).ToDictionary(r => r.Game);
        Save(path, header, done.Values.OrderBy(r => r.Game));

        var todo = Enumerable.Range(0, settings.Games).Where(i => !done.ContainsKey(i)).ToArray();
        var threads = Math.Min(todo.Length, parallel ?? DefaultParallel);
        var played = 0;
        long positions = 0;
        if (todo.Length > 0)
        {
            using var file = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            Exception? failure = null;
            var next = -1;
            var gameThreads = Enumerable.Range(0, threads).Select(t => new Thread(() =>
            {
                try
                {
                    int k;
                    while (!stop.IsCancellationRequested && (k = Interlocked.Increment(ref next)) < todo.Length)
                    {
                        var record = PlayGame(settings, todo[k], agents.Create, stop.Token);
                        lock (done)
                        {
                            done[record.Game] = record;
                            file.WriteLine(JsonSerializer.Serialize(record, Json));
                            played++;
                            positions += record.Searched.Count;
                        }
                        onGame?.Invoke(record);
                    }
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested)
                {
                }
                catch (Exception e)
                {
                    Interlocked.CompareExchange(ref failure, e, null);
                    stop.Cancel();
                }
            }) { IsBackground = true, Name = $"7wd selfplay {t}" }).ToList();
            gameThreads.ForEach(w => w.Start());
            gameThreads.ForEach(w => w.Join());
            if (failure is not null) throw new InvalidOperationException($"Self-Play failed: {failure.Message}", failure);
        }

        var complete = done.Count == settings.Games;
        int? shardPositions = null;
        var shard = Path.Combine(directory, ShardName);
        if (complete)
        {
            shardPositions = File.Exists(Path.Combine(shard, ShardWriter.HeaderName))
                ? JsonDocument.Parse(File.ReadAllText(Path.Combine(shard, ShardWriter.HeaderName))).RootElement.GetProperty("positions").GetInt32()
                : ShardWriter.Write(shard, done.Values.OrderBy(r => r.Game).ToList(), encoding);
        }
        var report = new SelfPlayReport(complete, done.Count, settings.Games, played, positions,
            agents.Batcher?.Positions ?? 0, agents.Batcher?.Batches ?? 0, clock.Elapsed.TotalSeconds, shardPositions);
        File.WriteAllText(Path.Combine(directory, SummaryName), JsonSerializer.Serialize(new
        {
            header,
            report.Complete, report.GamesDone, report.GamesTotal, report.GamesPlayed, report.PositionsPlayed,
            report.Evaluations, report.Batches, report.Seconds, report.GamesPerSecond, report.PositionsPerSecond,
            report.MeanBatch, report.ShardPositions,
            Device = agents.Batcher?.Network.Device.ToString(), Parallel = threads, agents.Batcher?.Workers, Machine = Environment.MachineName,
            Cores = Environment.ProcessorCount,
        }, JsonIndented));
        return report;
    }

    /// <summary>
    /// Games in flight by default: 32 per core, at least 64 (512 on the 16-thread desktop), so the inference threads
    /// always find a big batch waiting. Measured (ticket 22): beyond ~256 it hardly matters.
    /// </summary>
    public static int DefaultParallel => Math.Max(64, 32 * Environment.ProcessorCount);

    /// <summary>
    /// Inference threads by default: one per 4 cores, 1 to 4 (4 on the desktop). One thread was the bottleneck: copying
    /// inputs and outputs and waking hundreds of game threads left the network idle (ticket 22 measurements).
    /// </summary>
    public static int DefaultWorkers => Math.Clamp(Environment.ProcessorCount / 4, 1, 4);

    /// <summary>The seeds of game <paramref name="game"/>: the Chance Source's, each player's, and the promo draw.</summary>
    public static (ulong Seed, ulong Chance, ulong P1, ulong P2, Ruleset Rules) Seeds(SelfPlaySettings settings, int game)
    {
        var seed = settings.FirstSeed + (ulong)game;
        var rng = new Rng(seed);
        var (chance, p1, p2) = (rng.NextUInt64(), rng.NextUInt64(), rng.NextUInt64());
        var promoDraw = (rng.NextUInt64() >> 11) * (1.0 / (1UL << 53));
        var rules = new Ruleset(Promos: promoDraw < settings.PromoRate ? Promos.Messe | Promos.Liberty : Promos.None,
            Expansions: settings.Expansions);
        return (seed, chance, p1, p2, rules);
    }

    /// <summary>The rules every game of the session shares, apart from the promo draw.</summary>
    static Ruleset SessionRules(SelfPlaySettings settings) => new(Expansions: settings.Expansions);

    /// <summary>Plays game <paramref name="game"/> of the session; <paramref name="createAgent"/> builds each player's search from its seed.</summary>
    public static GameRecord PlayGame(SelfPlaySettings settings, int game, Func<ulong, NeuralAgent> createAgent,
        CancellationToken cancel = default)
    {
        var (seed, chance, p1, p2, rules) = Seeds(settings, game);
        return Play(game, seed, rules, chance, p1, p2, createAgent, cancel);
    }

    /// <summary>
    /// Plays a recorded game again from its seeds and rules with the same network and settings. On the CPU the
    /// result equals the record (Move Log and policy targets), apart from the wall-clock.
    /// </summary>
    public static GameRecord Replay(GameRecord record, Func<ulong, NeuralAgent> createAgent) =>
        Play(record.Game, record.Seed, record.Rules, record.ChanceSeed, record.SeedP1, record.SeedP2, createAgent, default);

    static GameRecord Play(int index, ulong seed, Ruleset rules, ulong chanceSeed, ulong seedP1, ulong seedP2,
        Func<ulong, NeuralAgent> createAgent, CancellationToken cancel)
    {
        var clock = Stopwatch.StartNew();
        var chance = new RandomChanceSource(chanceSeed);
        NeuralAgent[] players = [createAgent(seedP1), createAgent(seedP2)];
        var game = new Game(rules);
        var searched = new List<SearchedDecision>();
        while (game.State.Step != StepKind.GameOver)
        {
            if (game.State.Step == StepKind.Reveal)
            {
                game.Apply(chance.Next(game.State));
                continue;
            }
            cancel.ThrowIfCancellationRequested();
            var player = game.State.PlayerToDecide;
            var agent = players[player];
            var move = agent.ChooseMove(game.State.Observe(player));
            if (agent.LastSearch is { } search)
                searched.Add(new SearchedDecision(game.Log.Count, Round(SearchValue(search)),
                    search.PolicyTarget.Select(Round).ToArray()));
            game.Apply(move);
        }
        var result = game.State.Result!;
        return new GameRecord(index, seed, chanceSeed, seedP1, seedP2, game.WriteLog(), result.Winner, result.Victory,
            result.P1.Total, result.P2.Total, searched, Math.Round(clock.Elapsed.TotalSeconds, 4));
    }

    /// <summary>
    /// Targets and values are kept to 4 decimals: it halves a Game Record (~5 KB instead of ~10 KB, most of it targets)
    /// and is far below what training can resolve. The shard uses exactly the stored numbers.
    /// </summary>
    public const int Decimals = 4;

    static float Round(double x) => (float)Math.Round(x, Decimals);

    /// <summary>The search's value of the root for the decider, −1..1: the visit-weighted mean of the root Moves' values.</summary>
    static double SearchValue(NeuralResult search)
    {
        double sum = 0, visits = 0;
        foreach (var m in search.Moves)
        {
            sum += m.Visits * (2 * m.Value - 1);
            visits += m.Visits;
        }
        return visits == 0 ? 2 * search.RootValue - 1 : sum / visits;
    }

    /// <summary>The session's header; <paramref name="encoding"/> is the network's Encoding (default: the oldest that encodes the games).</summary>
    public static SelfPlayHeader Header(SelfPlaySettings settings, EncodingVersion? encoding = null) => new(FormatVersion,
        settings.Agent.Model, ModelIdentity(settings.Agent.Model), settings.SearchSpec, settings.Games, settings.FirstSeed,
        settings.PromoRate, (encoding ?? EncodingVersion.For(SessionRules(settings))).Number,
        settings.Expansions == Expansions.None ? null : SessionRules(settings).ToString().Split(" expansion=")[1]);

    /// <summary>The SHA-256 of an ONNX model's bytes, or the name of a built-in reference evaluator.</summary>
    public static string ModelIdentity(string model)
    {
        if (model.ToLowerInvariant() is NeuralSettings.Uniform or NeuralSettings.Playout) return model.ToLowerInvariant();
        if (!File.Exists(model)) throw new FileNotFoundException($"No network at {model}", model);
        using var stream = File.OpenRead(model);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>
    /// The Game Records of a Self-Play file (none if there is no file). With <paramref name="expected"/>, the file must
    /// hold the same session (anything but the model path may not differ). A torn last line is ignored.
    /// </summary>
    public static IReadOnlyList<GameRecord> Load(string path, SelfPlayHeader? expected = null) => Read(path, expected).Records;

    /// <summary>The header and Game Records of a Self-Play file (a directory means its <c>games.jsonl</c>).</summary>
    public static (SelfPlayHeader? Header, IReadOnlyList<GameRecord> Records) Read(string path, SelfPlayHeader? expected = null)
    {
        if (Directory.Exists(path)) path = Path.Combine(path, FileName);
        if (!File.Exists(path))
            return expected is null ? throw new FileNotFoundException($"No Self-Play records at {path}", path) : (null, []);
        var lines = File.ReadAllLines(path);
        if (lines.Length == 0) return (null, []);
        var header = JsonSerializer.Deserialize<SelfPlayHeader>(lines[0], Json);
        if (header is null || header.Format != FormatVersion)
            throw new FormatException($"{path} is not a Self-Play file ({FormatVersion})");
        if (expected is not null && header with { Model = "" } != expected with { Model = "" })
            throw new FormatException($"{path} holds a different Self-Play session:\n  {lines[0]}\nnot\n  "
                + $"{JsonSerializer.Serialize(expected, Json)}\nUse another directory to start a new one.");
        var records = new List<GameRecord>();
        for (var i = 1; i < lines.Length; i++)
        {
            GameRecord? record;
            try
            {
                record = JsonSerializer.Deserialize<GameRecord>(lines[i], Json);
            }
            catch (JsonException) when (i == lines.Length - 1)
            {
                break; // the process stopped in the middle of writing this line
            }
            if (record is null || record.Game < 0 || record.Game >= header.Games || record.Log is null || record.Searched is null)
                throw new FormatException($"{path} line {i + 1} is not a Game Record of this session");
            records.Add(record);
        }
        return (header, records);
    }

    static void Save(string path, SelfPlayHeader header, IEnumerable<GameRecord> records)
    {
        var temp = path + ".tmp";
        File.WriteAllLines(temp, [JsonSerializer.Serialize(header, Json), .. records.Select(r => JsonSerializer.Serialize(r, Json))]);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>
    /// Replays the first <paramref name="games"/> Game Records of a Self-Play directory from their seeds, with the
    /// network named in its header (which must still have the same contents), and returns the games whose Move Log or
    /// policy targets came out different (spec P7: "replaying a Game Record from its seeds reproduces its Move Log").
    /// </summary>
    public static IReadOnlyList<int> Check(string directory, int games, InferenceDevice device = InferenceDevice.Cpu,
        int threads = 4, string? model = null)
    {
        var (header, records) = Read(directory);
        if (header is null) throw new FormatException($"{directory} holds no Self-Play session");
        model ??= header.Model;
        if (ModelIdentity(model) != header.ModelSha256)
            throw new FormatException($"{model} is not the network these games were played with (SHA-256 {header.ModelSha256})");
        var settings = NeuralSettings.Parse($"{model},{header.Search},device={device},threads={threads}");
        using var agents = AgentSource.Open(settings, maxBatch: 64, workers: 1);
        var different = new List<int>();
        var chosen = records.OrderBy(r => r.Game).Take(games).ToList();
        Parallel.ForEach(chosen, record =>
        {
            if (!SameGame(record, Replay(record, agents.Create)))
                lock (different) different.Add(record.Game);
        });
        different.Sort();
        return different;
    }

    /// <summary>True when two records of one game agree on everything but the wall-clock.</summary>
    public static bool SameGame(GameRecord a, GameRecord b) =>
        (a.Game, a.Seed, a.ChanceSeed, a.SeedP1, a.SeedP2, a.Log, a.Winner, a.Victory, a.VpP1, a.VpP2)
        == (b.Game, b.Seed, b.ChanceSeed, b.SeedP1, b.SeedP2, b.Log, b.Winner, b.Victory, b.VpP1, b.VpP2)
        && a.Searched.Count == b.Searched.Count
        && a.Searched.Zip(b.Searched).All(p => p.First.Entry == p.Second.Entry && p.First.Q == p.Second.Q
            && p.First.Target.AsSpan().SequenceEqual(p.Second.Target));

    /// <summary>Builds the searches of one session: an own batcher for an ONNX model (sized to the games in flight), or a built-in reference.</summary>
    sealed class AgentSource : IDisposable
    {
        readonly NeuralSettings _settings;

        AgentSource(NeuralSettings settings, InferenceBatcher? batcher) => (_settings, Batcher) = (settings, batcher);

        public InferenceBatcher? Batcher { get; }

        public static AgentSource Open(NeuralSettings settings, int maxBatch, int workers) =>
            settings.Model.ToLowerInvariant() is NeuralSettings.Uniform or NeuralSettings.Playout
                ? new AgentSource(settings, null)
                : new AgentSource(settings, new InferenceBatcher(NeuralNetwork.Load(settings.Model, settings.Device, settings.Threads), maxBatch, workers));

        /// <summary>The Encoding the session's searches use for these rules (throws when the network can't encode them).</summary>
        public EncodingVersion EncodingFor(Ruleset rules) => Create(0).EncodingFor(rules);

        public NeuralAgent Create(ulong seed) => Batcher is null
            ? NeuralAgent.Create(_settings, seed)
            : new NeuralAgent(_settings, new NetworkEvaluator(Batcher), seed);

        public void Dispose()
        {
            if (Batcher is null) return;
            Batcher.Dispose();
            Batcher.Network.Dispose();
        }
    }
}
