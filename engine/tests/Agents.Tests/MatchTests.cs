using SevenWondersDuel.Engine;

namespace SevenWondersDuel.Agents.Tests;

/// <summary>The Match runner (spec A5, Matches and Elo; P4): seat-swapped seed pairs, statistics, resume.</summary>
public class MatchTests
{
    /// <summary>A deliberately weak Agent: discards whenever it may, else plays randomly.</summary>
    sealed class Discarder(ulong seed) : IAgent
    {
        readonly RandomAgent _random = new(seed);

        public Move ChooseMove(Observation observation)
        {
            Span<Move> moves = stackalloc Move[GameState.MaxMoves];
            var n = observation.LegalMoves(moves);
            for (var i = 0; i < n; i++)
                if (moves[i].Kind == MoveKind.Discard) return moves[i];
            return _random.ChooseMove(observation);
        }
    }

    static IAgent WithDiscarder(string spec, ulong seed) => spec == "discarder" ? new Discarder(seed) : AgentSpecs.Create(spec, seed);

    /// <summary>What a game decided, without its wall-clock (which differs from run to run).</summary>
    static object Outcome(MatchPair p) => (p.Pair,
        p.First with { SecondsA = 0, SecondsB = 0, Seconds = 0 },
        p.Second with { SecondsA = 0, SecondsB = 0, Seconds = 0 });

    static string TempDirectory() => Path.Combine(Path.GetTempPath(), "7wd-match-test-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(Promos.None)]
    [InlineData(Promos.Messe | Promos.Liberty)]
    public void Random_against_random_lands_within_its_CI_of_50_percent(Promos promos)
    {
        var report = Match.Run(new MatchSettings("random", "random", Promos: promos));
        Assert.True(report.Complete);
        Assert.Equal(400, report.GameCount);
        Assert.Equal(400, report.WinsA + report.WinsB + report.Draws);
        Assert.True(report.WinRate.Contains(0.5), report.ToString());
        Assert.False(report.APasses);
        Assert.Equal((0.0, 0.0), (report.EloA!.Value, report.EloB!.Value));
        Assert.Equal(report.WinsA, Enum.GetValues<VictoryType>().Sum(v => report.Wins(MatchWinner.A, v)));
        Assert.Equal(report.WinsA, report.WinsAAsSeat(0) + report.WinsAAsSeat(1));
    }

    [Fact]
    public void Over_many_pairs_random_against_random_converges_to_50_percent()
    {
        var report = Match.Run(new MatchSettings("random", "random", Pairs: 10_000, FirstSeed: 1_000_000));
        Assert.InRange(report.WinRate.Score, 0.49, 0.51);
        Assert.InRange(report.WinRate.High - report.WinRate.Low, 0.01, 0.02);
        Assert.True(report.Wins(MatchWinner.A, VictoryType.Military) > 0 && report.Wins(MatchWinner.B, VictoryType.Science) > 0);
    }

    [Fact]
    public void Each_seed_pair_swaps_the_seats()
    {
        var report = Match.Run(new MatchSettings("random", "random", Pairs: 20));
        Assert.Equal(Enumerable.Range(0, 20), report.Pairs.Select(p => p.Pair));
        Assert.All(report.Pairs, p => Assert.Equal((0, 1), (p.First.SeatA, p.Second.SeatA)));
        Assert.All(report.Games, g => Assert.True(g.MovesA > 0 && g.MovesB > 0 && g.Seconds > 0));
    }

    [Fact]
    public void A_stronger_agent_passes_and_gets_a_positive_elo_on_the_random_scale()
    {
        var report = Match.Run(new MatchSettings("random", "discarder", Pairs: 100), createAgent: WithDiscarder);
        Assert.True(report.APasses, report.ToString());
        Assert.True(report.EloDifference.Low > 200, report.ToString());
        Assert.Equal(0, report.EloA);
        Assert.Equal(-report.EloDifference.Diff, report.EloB!.Value, 9);

        // With B's rating known, A's comes from it.
        var rated = Match.Run(new MatchSettings("random", "discarder", Pairs: 100, EloB: -500), createAgent: WithDiscarder);
        Assert.Equal(0, rated.EloA);
        Assert.Equal(-500, rated.EloB);
        var unrated = Match.Run(new MatchSettings("discarder", "discarder", Pairs: 5), createAgent: WithDiscarder);
        Assert.Null(unrated.EloA);
        Assert.Null(unrated.EloB);
    }

    [Fact]
    public void Results_do_not_depend_on_the_thread_count()
    {
        var settings = new MatchSettings("random", "random", Pairs: 50, FirstSeed: 99);
        var one = Match.Run(settings, threads: 1);
        var many = Match.Run(settings, threads: 4);
        Assert.Equal(one.Pairs.Select(Outcome), many.Pairs.Select(Outcome));
    }

    [Fact]
    public void A_stopped_match_resumes_from_its_last_finished_seed_pair()
    {
        var settings = new MatchSettings("random", "random", Pairs: 200, FirstSeed: 5, Promos: Promos.Messe);
        var uninterrupted = Match.Run(settings);
        var directory = TempDirectory();
        try
        {
            using var stop = new CancellationTokenSource();
            var played = 0;
            var stopped = Match.Run(settings, directory, stop.Token, threads: 4, onPair: _ =>
            {
                if (Interlocked.Increment(ref played) == 30) stop.Cancel();
            });
            Assert.True(stopped.Stopped && !stopped.Complete);
            Assert.InRange(stopped.Pairs.Count, 30, 199);
            var saved = Match.Load(Path.Combine(directory, Match.FileName), settings);
            Assert.Equal(stopped.Pairs.Count, saved.Count);
            Assert.Contains("STOPPED", stopped.ToString());

            var replayed = 0;
            var resumed = Match.Run(settings, directory, onPair: _ => Interlocked.Increment(ref replayed));
            Assert.True(resumed.Complete && !resumed.Stopped);
            Assert.Equal(200 - stopped.Pairs.Count, replayed);
            Assert.Equal(uninterrupted.Pairs.Select(Outcome), resumed.Pairs.Select(Outcome));
            Assert.Equal(uninterrupted.WinRate, resumed.WinRate);
            Assert.True(File.Exists(Path.Combine(directory, Match.SummaryName)));

            // A finished Match only reports again.
            replayed = 0;
            var again = Match.Run(settings, directory, onPair: _ => replayed++);
            Assert.Equal(0, replayed);
            Assert.Equal(resumed.WinRate, again.WinRate);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void A_torn_last_line_is_replayed_and_another_matchs_file_is_refused()
    {
        var settings = new MatchSettings("random", "random", Pairs: 10);
        var directory = TempDirectory();
        try
        {
            var path = Path.Combine(directory, Match.FileName);
            Match.Run(settings, directory);
            var lines = File.ReadAllLines(path);
            File.WriteAllLines(path, [.. lines[..6]]);
            File.AppendAllText(path, lines[6][..20]); // the process died while writing the sixth pair's line
            Assert.Equal(5, Match.Load(path, settings).Count);

            var replayed = 0;
            var resumed = Match.Run(settings, directory, onPair: _ => replayed++);
            Assert.True(resumed.Complete);
            Assert.Equal(5, replayed);
            Assert.Equal(11, File.ReadAllLines(path).Length);

            Assert.Throws<FormatException>(() => Match.Run(settings with { FirstSeed = 1 }, directory));
            Assert.Throws<FormatException>(() => Match.Run(settings with { Promos = Promos.Liberty }, directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Wilson_interval_and_elo_match_known_values()
    {
        var half = WinRate.Wilson(5, 10);
        Assert.Equal(0.2366, half.Low, 4);
        Assert.Equal(0.7634, half.High, 4);
        var none = WinRate.Wilson(0, 10);
        Assert.Equal(0, none.Low);
        Assert.Equal(0.2775, none.High, 4);
        Assert.Equal(1, WinRate.Wilson(10, 10).High, 9);

        Assert.Equal(0, MatchReport.Elo(0.5, 400), 9);
        Assert.InRange(MatchReport.Elo(0.76, 400), 199, 201); // spec: 200 points ≈ 76% expected wins
        Assert.Equal(-MatchReport.Elo(0.7, 400), MatchReport.Elo(0.3, 400), 9);
        Assert.True(double.IsFinite(MatchReport.Elo(1, 400)) && MatchReport.Elo(1, 400) > 1000);
    }

    [Fact]
    public void Agent_specs_name_known_agents_only()
    {
        Assert.IsType<RandomAgent>(AgentSpecs.Create("random", 1));
        Assert.Throws<FormatException>(() => AgentSpecs.Create("nobody", 1));
        Assert.Throws<FormatException>(() => AgentSpecs.Create("random:3", 1));
        Assert.Throws<FormatException>(() => Match.Run(new MatchSettings("random", "nobody", Pairs: 1)));
    }
}
