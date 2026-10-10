namespace SevenWondersDuel.Agents.Tests;

/// <summary>
/// The soak checker and the bench, at CI size. The full 100k-game soak (<c>7wd soak</c>) and the timed bench
/// (<c>7wd bench</c>) are run by hand (spec A11: long soaks stay out of CI).
/// </summary>
public class SoakAndBenchTests
{
    [Fact]
    public void Every_invariant_holds_over_3k_soaked_games_under_both_start_rules()
    {
        var report = Soak.Run(3000);
        Assert.True(report.Passed, string.Join('\n', report.Failures.Take(10)));
        Assert.Equal(3000, report.Games);
        Assert.Equal(report.Games, report.Civilian + report.Military + report.Science);
        Assert.True(report.Military > 0 && report.Science > 0, report.ToString());
    }

    [Fact]
    public void A_soaked_game_is_the_same_game_on_any_thread_count()
    {
        var one = Soak.Run(200, firstSeed: 500, threads: 1);
        var many = Soak.Run(200, firstSeed: 500, threads: 4);
        Assert.Equal((one.Civilian, one.Military, one.Science, one.Draws), (many.Civilian, many.Military, many.Science, many.Draws));
    }

    [Fact]
    public void Soak_seeds_alternate_between_the_start_rules()
    {
        Assert.Equal(Engine.StartChoice.New, Soak.RulesFor(0).StartChoice);
        Assert.Equal(Engine.StartChoice.Rule2015, Soak.RulesFor(1).StartChoice);
    }

    [Fact]
    public void Bench_measures_every_variant()
    {
        var results = Bench.Run(TimeSpan.FromMilliseconds(100));
        Assert.Equal(3, results.Count);
        Assert.All(results, r =>
        {
            Assert.True(r.Games > 0 && r.Decisions > r.Games, r.ToString());
            Assert.True(r.GamesPerSecond > 0);
        });
    }
}
