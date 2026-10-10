using System.Text;
using System.Text.Json;
using SevenWondersDuel.Engine;

namespace SevenWondersDuel.Agents.Tests;

/// <summary>
/// Self-Play (spec A7 / P7; ticket 22): parallel games, Game Records with seeds and sparse policy targets, per-game
/// saving and resuming, and the encoded .npy shard. Python reads the shard in training/tests/test_selfplay.py.
/// </summary>
public sealed class SelfPlayTests : IDisposable
{
    static readonly string Tiny = Path.Combine(AppContext.BaseDirectory, "Models", "tiny.onnx");

    readonly string _dir = Path.Combine(Path.GetTempPath(), "7wd-selfplay-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    static SelfPlaySettings Settings(int games = 4, string spec = "sims=8", double promoRate = 1) =>
        new(NeuralSettings.Parse($"{Tiny},{spec},threads=1"), games, FirstSeed: 7, PromoRate: promoRate);

    string Dir(string name) => Path.Combine(_dir, name);

    static Func<ulong, NeuralAgent> Agents(NeuralSettings settings, out InferenceBatcher batcher)
    {
        var b = batcher = LoadedNetworks.Get(settings.Model, InferenceDevice.Cpu, 1);
        return seed => new NeuralAgent(settings, new NetworkEvaluator(b), seed);
    }

    [Fact]
    public void Replaying_a_Game_Record_from_its_seeds_reproduces_its_Move_Log()
    {
        var settings = Settings(games: 5);
        var report = SelfPlay.Run(settings, Dir("a"), parallel: 3);
        Assert.True(report.Complete);
        var records = SelfPlay.Load(Path.Combine(Dir("a"), SelfPlay.FileName));
        Assert.Equal(5, records.Count);

        var create = Agents(settings.Agent, out _);
        foreach (var record in records)
        {
            var again = SelfPlay.Replay(record, create);
            Assert.Equal(record.Log, again.Log);
            Assert.True(SelfPlay.SameGame(record, again));
        }
        // The same through the directory check the CLI uses (it reloads the network named in the header).
        Assert.Empty(SelfPlay.Check(Dir("a"), games: 5, threads: 1));

        // A different seed plays a different game.
        var other = SelfPlay.Replay(records[0] with { ChanceSeed = records[0].ChanceSeed + 1 }, create);
        Assert.NotEqual(records[0].Log, other.Log);
    }

    [Fact]
    public void Games_do_not_depend_on_how_many_run_at_once()
    {
        var settings = Settings(games: 4);
        SelfPlay.Run(settings, Dir("one"), parallel: 1, workers: 1);
        SelfPlay.Run(settings, Dir("four"), parallel: 4, workers: 3);
        var one = SelfPlay.Load(Dir("one")).OrderBy(r => r.Game).ToList();
        var four = SelfPlay.Load(Dir("four")).OrderBy(r => r.Game).ToList();
        Assert.Equal(4, one.Count);
        Assert.All(one.Zip(four), p => Assert.True(SelfPlay.SameGame(p.First, p.Second)));
        Assert.Equal(File.ReadAllBytes(Path.Combine(Dir("one"), "shard", "input.npy")),
            File.ReadAllBytes(Path.Combine(Dir("four"), "shard", "input.npy")));
    }

    [Fact]
    public void A_Game_Record_holds_seeds_result_and_a_target_for_every_searched_decision()
    {
        var settings = Settings(games: 3);
        SelfPlay.Run(settings, Dir("r"), parallel: 3);
        foreach (var record in SelfPlay.Load(Dir("r")))
        {
            var (seed, chance, p1, p2, rules) = SelfPlay.Seeds(settings, record.Game);
            Assert.Equal((seed, chance, p1, p2), (record.Seed, record.ChanceSeed, record.SeedP1, record.SeedP2));
            Assert.Equal(rules, record.Rules);
            Assert.Equal(Promos.Messe | Promos.Liberty, record.Rules.Promos); // promo rate 1

            var parsed = MoveLog.Parse(record.Log);
            var result = parsed.State.Result!;
            Assert.Equal((result.Winner, result.Victory, result.P1.Total, result.P2.Total),
                (record.Winner, record.Victory, record.VpP1, record.VpP2));

            // Every decision with a choice is searched and listed; forced ones are not.
            var game = new Game(parsed.Rules);
            var searched = record.Searched.ToDictionary(d => d.Entry);
            for (var e = 0; e < parsed.Log.Count; e++)
            {
                if (parsed.Log[e] is MoveEntry)
                {
                    var legal = game.State.LegalMoves().Count;
                    Assert.Equal(legal > 1, searched.ContainsKey(e));
                    if (legal > 1)
                    {
                        var d = searched[e];
                        Assert.Equal(legal, d.Target.Length);
                        Assert.Equal(1.0, d.Target.Sum(t => (double)t), 2);
                        Assert.All(d.Target, t => Assert.True(t >= 0));
                        Assert.InRange(d.Q, -1, 1);
                    }
                }
                else Assert.False(searched.ContainsKey(e));
                game.Apply(parsed.Log[e]);
            }
        }
    }

    [Fact]
    public void Promo_rate_decides_per_game_from_its_seed()
    {
        var none = Settings(games: 40, promoRate: 0);
        var half = Settings(games: 40, promoRate: 0.5);
        Assert.All(Enumerable.Range(0, 40), g => Assert.Equal(Promos.None, SelfPlay.Seeds(none, g).Rules.Promos));
        var withPromos = Enumerable.Range(0, 40).Count(g => SelfPlay.Seeds(half, g).Rules.Promos != Promos.None);
        Assert.InRange(withPromos, 10, 30);
    }

    [Fact]
    public void A_stopped_session_resumes_and_ends_with_the_same_games()
    {
        var settings = Settings(games: 4);
        SelfPlay.Run(settings, Dir("full"), parallel: 2);
        var full = SelfPlay.Load(Dir("full")).OrderBy(r => r.Game).ToList();

        // Simulate a stop: two games saved, the third torn mid-line, no shard yet.
        var lines = File.ReadAllLines(Path.Combine(Dir("full"), SelfPlay.FileName));
        Directory.CreateDirectory(Dir("resumed"));
        File.WriteAllText(Path.Combine(Dir("resumed"), SelfPlay.FileName),
            string.Join('\n', lines[0], lines[1], lines[2], lines[3][..(lines[3].Length / 2)]));
        var report = SelfPlay.Run(settings, Dir("resumed"), parallel: 2);
        Assert.True(report.Complete);
        Assert.Equal(2, report.GamesPlayed);
        var resumed = SelfPlay.Load(Dir("resumed")).OrderBy(r => r.Game).ToList();
        Assert.Equal(4, resumed.Count);
        Assert.All(full.Zip(resumed), p => Assert.True(SelfPlay.SameGame(p.First, p.Second)));
        Assert.Equal(File.ReadAllBytes(Path.Combine(Dir("full"), "shard", "input.npy")),
            File.ReadAllBytes(Path.Combine(Dir("resumed"), "shard", "input.npy")));

        // Rerunning a finished session plays nothing and keeps the shard.
        Assert.Equal(0, SelfPlay.Run(settings, Dir("resumed")).GamesPlayed);
    }

    [Fact]
    public void A_directory_of_another_session_is_refused()
    {
        SelfPlay.Run(Settings(games: 1), Dir("x"), parallel: 1);
        var e = Assert.Throws<FormatException>(() => SelfPlay.Run(Settings(games: 1, spec: "sims=9"), Dir("x")));
        Assert.Contains("different Self-Play session", e.Message);
        Assert.Throws<FormatException>(() => SelfPlay.Run(Settings(games: 2), Dir("x")));
    }

    [Fact]
    public void A_stop_keeps_finished_games()
    {
        using var stop = new CancellationTokenSource();
        var settings = Settings(games: 50);
        var report = SelfPlay.Run(settings, Dir("s"), parallel: 2, stop.Token, onGame: _ => stop.Cancel());
        Assert.False(report.Complete);
        Assert.Null(report.ShardPositions);
        Assert.False(Directory.Exists(Path.Combine(Dir("s"), "shard")));
        Assert.InRange(SelfPlay.Load(Dir("s")).Count, 1, 3);
    }

    [Fact]
    public void A_stop_file_cancels_once_it_appears()
    {
        Directory.CreateDirectory(_dir);
        var flag = Path.Combine(_dir, "pause.request");
        using var stop = new CancellationTokenSource();
        var told = 0;
        using (new StopFile(flag, stop, TimeSpan.FromMilliseconds(10), () => told++))
        {
            Thread.Sleep(100);
            Assert.False(stop.IsCancellationRequested);
            File.WriteAllText(flag, "pause");
            Assert.True(SpinWait.SpinUntil(() => stop.IsCancellationRequested, TimeSpan.FromSeconds(5)));
            Thread.Sleep(50);
        }
        Assert.Equal(1, told);
    }

    [Fact]
    public void A_stop_file_stops_Self_Play_keeping_finished_games()
    {
        Directory.CreateDirectory(_dir);
        var flag = Path.Combine(_dir, "pause.request");
        using var stop = new CancellationTokenSource();
        using var watcher = new StopFile(flag, stop, TimeSpan.FromMilliseconds(10));
        var report = SelfPlay.Run(Settings(games: 50), Dir("f"), parallel: 2, stop.Token,
            onGame: _ => File.WriteAllText(flag, "pause"));
        Assert.False(report.Complete);
        Assert.False(Directory.Exists(Path.Combine(Dir("f"), "shard")));
        Assert.InRange(SelfPlay.Load(Dir("f")).Count, 1, 10);
    }

    [Fact]
    public void The_shard_holds_each_searched_decision_encoded_with_its_sparse_target()
    {
        var settings = Settings(games: 3, promoRate: 0.5);
        var report = SelfPlay.Run(settings, Dir("sh"), parallel: 3);
        var records = SelfPlay.Load(Dir("sh")).OrderBy(r => r.Game).ToList();
        var shard = Path.Combine(Dir("sh"), "shard");
        var n = records.Sum(r => r.Searched.Count);
        Assert.Equal(n, report.ShardPositions);

        using var header = JsonDocument.Parse(File.ReadAllText(Path.Combine(shard, "shard.json")));
        Assert.Equal("7wd-shard", header.RootElement.GetProperty("format").GetString());
        Assert.Equal(NetworkEncoding.Version, header.RootElement.GetProperty("encoding_version").GetInt32());
        Assert.Equal(n, header.RootElement.GetProperty("positions").GetInt32());

        var input = ReadNpy<Half>(Path.Combine(shard, "input.npy"), "<f2", out var inputShape);
        Assert.Equal([n, NetworkEncoding.InputSize], inputShape);
        var offset = ReadNpy<long>(Path.Combine(shard, "policy_offset.npy"), "<i8", out _);
        var index = ReadNpy<short>(Path.Combine(shard, "policy_index.npy"), "<i2", out _);
        var prob = ReadNpy<float>(Path.Combine(shard, "policy_prob.npy"), "<f4", out _);
        var value = ReadNpy<float>(Path.Combine(shard, "value.npy"), "<f4", out _);
        var outcome = ReadNpy<sbyte>(Path.Combine(shard, "outcome.npy"), "|i1", out _);
        Assert.Equal(n + 1, offset.Length);
        Assert.Equal(index.Length, offset[^1]);

        // Rebuild every row independently from the Move Logs.
        var row = 0;
        var expected = new float[NetworkEncoding.InputSize];
        foreach (var record in records)
        {
            var parsed = MoveLog.Parse(record.Log);
            var game = new Game(parsed.Rules);
            var searched = record.Searched.ToDictionary(d => d.Entry);
            for (var e = 0; e < parsed.Log.Count; e++)
            {
                if (searched.TryGetValue(e, out var d))
                {
                    var me = ((MoveEntry)parsed.Log[e]).Player;
                    var observation = game.State.Observe(me);
                    NetworkEncoding.Encode(observation, expected);
                    for (var i = 0; i < expected.Length; i++)
                        Assert.Equal((Half)expected[i], input[row * NetworkEncoding.InputSize + i]);
                    var legal = observation.LegalMoves();
                    Assert.Equal(legal.Select(NetworkEncoding.PolicyIndex).Select(i => (short)i), index[(int)offset[row]..(int)offset[row + 1]]);
                    Assert.Equal(d.Target, prob[(int)offset[row]..(int)offset[row + 1]]);
                    Assert.Equal(NetworkEncoding.ValueTarget(parsed.State.Result!, me), value[row]);
                    Assert.Equal(NetworkEncoding.OutcomeClass(parsed.State.Result!, me), outcome[row]);
                    row++;
                }
                game.Apply(parsed.Log[e]);
            }
        }
        Assert.Equal(n, row);
    }

    [Fact]
    public void Npy_headers_follow_the_format()
    {
        var header = Npy.Header("<f2", [3, 2417]);
        Assert.Equal(0, header.Length % 64);
        Assert.Equal("\x93NUMPY", Encoding.Latin1.GetString(header, 0, 6));
        Assert.Equal([1, 0], header[6..8]);
        Assert.Equal(header.Length - 10, header[8] | header[9] << 8);
        var text = Encoding.ASCII.GetString(header, 10, header.Length - 10);
        Assert.StartsWith("{'descr': '<f2', 'fortran_order': False, 'shape': (3, 2417), }", text);
        Assert.EndsWith("\n", text);
        Assert.Contains("'shape': (5,), }", Encoding.ASCII.GetString(Npy.Header("|i1", [5])));
    }

    [Fact]
    public void Self_play_runs_with_the_built_in_reference_and_in_classic_mode()
    {
        var uniform = new SelfPlaySettings(NeuralSettings.Parse("uniform,sims=4"), 1);
        Assert.True(SelfPlay.Run(uniform, Dir("u"), parallel: 1).Complete);
        Assert.Single(SelfPlay.Load(Dir("u")));
        Assert.Equal("uniform", SelfPlay.Read(Dir("u")).Header!.ModelSha256);

        var classic = Settings(games: 1, spec: "mode=classic,sims=8,noise=0.25,temp=1");
        SelfPlay.Run(classic, Dir("c"), parallel: 1);
        var record = SelfPlay.Load(Dir("c")).Single();
        Assert.Contains("mode=classic", SelfPlay.Read(Dir("c")).Header!.Search);
        Assert.True(SelfPlay.SameGame(record, SelfPlay.Replay(record, Agents(classic.Agent, out _))));
    }

    /// <summary>A minimal .npy reader for the test: checks the dtype, returns the data and the shape.</summary>
    static T[] ReadNpy<T>(string path, string dtype, out int[] shape) where T : unmanaged
    {
        var bytes = File.ReadAllBytes(path);
        var length = bytes[8] | bytes[9] << 8;
        var text = Encoding.ASCII.GetString(bytes, 10, length);
        Assert.Contains($"'descr': '{dtype}'", text);
        Assert.Contains("'fortran_order': False", text);
        var dims = text[(text.IndexOf("'shape': (") + 10)..text.IndexOf(')')];
        shape = dims.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(int.Parse).ToArray();
        return System.Runtime.InteropServices.MemoryMarshal.Cast<byte, T>(bytes.AsSpan(10 + length)).ToArray();
    }
}
