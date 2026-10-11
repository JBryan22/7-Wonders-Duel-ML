using System.Diagnostics;
using SevenWondersDuel.Advisor;
using SevenWondersDuel.Engine;
using Xunit.Abstractions;

namespace SevenWondersDuel.Advisor.Tests;

/// <summary>
/// Advice on request (spec A8, ticket 30): the compare table's ranking and its "≈ same" marker, the 800-simulation
/// budget's speed, and the background service (cancel on a position change, cache, Think longer, auto, the slow rule).
/// </summary>
public sealed class AdviceTests(ITestOutputHelper output) : IDisposable
{
    readonly string _folder = Path.Combine(Path.GetTempPath(), "7wd-advisor-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Plays a seeded random game through the session and returns a copy of it at each of the owner's decisions with
    /// at least <paramref name="minMoves"/> legal Moves (the positions where advice matters).
    /// </summary>
    internal static List<AdvisorSession> OwnerDecisions(ulong seed, int seat, int minMoves = 2, Ruleset? rules = null)
    {
        var session = AdvisorSession.New(rules ?? Ruleset.Default, seat);
        var chance = new RandomChanceSource(seed);
        var rng = new Rng(seed * 7919 + 1);
        var found = new List<AdvisorSession>();
        while (!session.State.IsOver)
        {
            var s = session.State;
            if (s.Step == StepKind.Reveal)
            {
                session.Enter(MoveLog.Format(chance.Next(s)));
                continue;
            }
            var moves = s.LegalMoves();
            if (s.PlayerToDecide == seat && moves.Count >= minMoves) found.Add(AdvisorSession.Load(session.Write()));
            session.Enter($"P{s.PlayerToDecide + 1} {moves[rng.Next(moves.Count)]}");
        }
        return found;
    }

    static Advice Search(AdvisorSession session, int simulations = 800, string agent = "mcts")
    {
        var outcome = AdviceSearch.Run(session.State.Observe(session.Seat), agent, simulations, 12345);
        return new Advice("", agent, outcome.Simulations, 0, AdviceTable.Rank(session.State, outcome), outcome.Line);
    }

    /// <summary>Every row's numbers, tags, line and outcome as text (rows hold lists, which records compare by reference).</summary>
    internal static string Describe(Advice advice) => string.Join("\n", advice.Rows.Select(r =>
        $"{r.Key}|{r.Share:R}|{r.Win:R}|{r.StdErr:R}|{r.Same}|{string.Join(";", r.Reasons)}|{string.Join(";", r.Expected)}|" +
        (r.Outcome is null ? "-" : string.Join(",", r.Outcome.Select(x => x.ToString("R"))))));

    [Fact]
    public void The_noise_rule_is_two_combined_standard_errors()
    {
        Assert.Equal(2 * 0.05, AdviceTable.Noise(0.03, 0.04), 12);
        Assert.True(AdviceTable.WithinNoise(0.55, 0.03, 0.46, 0.04));   // gap 0.09 ≤ 0.10
        Assert.False(AdviceTable.WithinNoise(0.57, 0.03, 0.46, 0.04));  // gap 0.11 > 0.10
        Assert.True(AdviceTable.WithinNoise(0.46, 0.04, 0.55, 0.03));   // symmetric
        Assert.True(AdviceTable.WithinNoise(0.5, 0, 0.5, 0));
        Assert.False(AdviceTable.WithinNoise(0.51, 0, 0.5, 0));
    }

    [Fact]
    public void Rows_put_the_search_choice_first_and_mark_about_the_same_exactly_within_the_noise()
    {
        int same = 0, apart = 0, positions = 0;
        foreach (var seed in new ulong[] { 1, 2, 3 })
        foreach (var session in OwnerDecisions(seed, seat: (int)(seed % 2), minMoves: 3).Where((_, i) => i % 4 == 0))
        {
            positions++;
            var advice = Search(session);
            var rows = advice.Rows;
            var legal = session.State.LegalMoves();
            Assert.Equal(legal.Count, rows.Count);
            Assert.Equal(legal.OrderBy(m => m.ToString()), rows.Select(r => r.Move).OrderBy(m => m.ToString()));
            Assert.Equal(1.0, rows.Sum(r => r.Share), 6);

            var best = rows[0];
            Assert.True(best.Best);
            Assert.True(best.Same);
            Assert.Equal(0, best.Gap);
            Assert.Equal(best.Visits, rows.Max(r => r.Visits)); // MCTS plays its most visited Move
            for (var i = 1; i < rows.Count; i++)
            {
                var r = rows[i];
                Assert.False(r.Best);
                Assert.True(r.Share <= rows[i - 1].Share || i == 1);
                Assert.Equal(best.Win - r.Win, r.Gap, 12);
                Assert.Equal(2 * Math.Sqrt(best.StdErr * best.StdErr + r.StdErr * r.StdErr), r.Noise, 12);
                Assert.Equal(AdviceTable.WithinNoise(best.Win, best.StdErr, r.Win, r.StdErr), r.Same);
                if (i < AdviceTable.MaxRows) { if (r.Same) same++; else apart++; }
            }

            // Every row's line is the Move as the page would enter it.
            foreach (var r in rows.Take(AdviceTable.MaxRows))
            {
                var copy = AdvisorSession.Load(session.Write());
                copy.Enter(r.Line);
                Assert.Equal(r.Slot is not null, r.Move.TakesCard);
            }
        }
        output.WriteLine($"{positions} positions: {same} table rows '≈ same', {apart} clearly worse");
        Assert.True(same > 0, "Some Moves within the noise of the best should be marked '≈ same'");
        Assert.True(apart > 0, "Some Moves should be clearly worse than the best");
    }

    [Fact]
    public void The_same_position_always_gets_the_same_advice()
    {
        var session = OwnerDecisions(4, 0, minMoves: 3)[3];
        var a = Search(session);
        var b = Search(AdvisorSession.Load(session.Write()));
        Assert.Equal(Describe(a), Describe(b));
    }

    [Fact]
    public void Advice_at_800_simulations_takes_well_under_3_seconds()
    {
        var positions = OwnerDecisions(5, 0, minMoves: 3);
        Search(positions[0]); // warm up the JIT
        var worst = 0.0;
        var total = 0.0;
        foreach (var session in positions)
        {
            var watch = Stopwatch.StartNew();
            Search(session, AdviceSettings.DefaultSimulations);
            var seconds = watch.Elapsed.TotalSeconds;
            worst = Math.Max(worst, seconds);
            total += seconds;
        }
        output.WriteLine($"{positions.Count} positions at 800 simulations: mean {total / positions.Count * 1000:0} ms, worst {worst * 1000:0} ms");
        Assert.True(worst < AdviceSettings.SlowSeconds, $"Worst advice took {worst:0.00} s");
    }

    [Fact]
    public void A_network_can_give_the_advice_instead_of_MCTS()
    {
        var model = Path.Combine(AppContext.BaseDirectory, "Models", "tiny.onnx");
        var settings = new AdviceSettings($"neural:{model}");
        settings.Validate();
        var session = OwnerDecisions(6, 1, minMoves: 3)[2];
        foreach (var spec in new[] { $"neural:{model}", $"neural:{model},mode=classic" })
        {
            var advice = Search(session, 200, spec);
            Assert.Equal(session.State.LegalMoves().Count, advice.Rows.Count);
            Assert.Equal(1.0, advice.Rows.Sum(r => r.Share), 5);
            Assert.True(advice.Rows.All(r => r.Win is >= 0 and <= 1));
        }
        Assert.Throws<FormatException>(() => new AdviceSettings("random").Validate());
        Assert.Throws<FormatException>(() => new AdviceSettings("heuristic:balanced").Validate());
        Assert.Throws<FileNotFoundException>(() => new AdviceSettings("neural:missing.onnx").Validate());
    }

    [Fact]
    public async Task The_service_searches_in_the_background_and_keeps_results_per_position()
    {
        var positions = OwnerDecisions(7, 0, minMoves: 3).Where(p => p.State.LegalMoves().All(m => m.TakesCard)).ToList();
        var (first, second) = (positions[1], positions[2]);
        using var service = new AdviceService(new AdviceSettings(Simulations: 300));
        Assert.Equal("idle", service.View(first).Status);
        Assert.Equal(1200, service.ThinkLongerSimulations);

        Assert.Equal("thinking", service.Request(first, longer: false).Status);
        await service.Running;
        var ready = service.View(first);
        Assert.Equal("ready", ready.Status);
        Assert.Equal(300, ready.Simulations);
        Assert.InRange(ready.Rows.Length, 1, AdviceTable.MaxRows);
        Assert.Equal(first.State.LegalMoves().Count, ready.Shares.Count);
        Assert.Single(ready.Badges, b => b.Top);
        Assert.Equal(ready.Rows[0].Slot, ready.Badges.Single(b => b.Top).Slot);

        // A different position has no advice yet; going back shows the stored one at once.
        service.Sync(second);
        Assert.Equal("idle", service.View(second).Status);
        service.Sync(first);
        Assert.Equal("ready", service.View(first).Status);

        // Think longer: 4× the budget, and the normal request no longer needs to search.
        service.Request(first, longer: true);
        await service.Running;
        var longer = service.View(first);
        Assert.Equal(1200, longer.Simulations);
        Assert.True(longer.Longer);
        Assert.Equal("ready", service.Request(first, longer: false).Status);
        Assert.Equal(1200, service.Latest(first)!.Simulations);
    }

    [Fact]
    public async Task A_search_is_cancelled_when_the_position_changes()
    {
        var positions = OwnerDecisions(8, 1, minMoves: 3);
        using var service = new AdviceService(new AdviceSettings(Simulations: 5_000_000), fixedBudget: true);
        service.Request(positions[0], longer: false);
        var running = service.Running;
        Assert.False(running.IsCompleted);
        service.Sync(positions[1]);
        var watch = Stopwatch.StartNew();
        await running; // the cancelled search ends quietly
        Assert.True(watch.Elapsed.TotalSeconds < 5);
        Assert.Equal("idle", service.View(positions[1]).Status);
        Assert.Equal("idle", service.View(positions[0]).Status);
    }

    [Fact]
    public async Task Advice_is_only_for_the_owners_decisions_and_auto_starts_on_them()
    {
        var session = OwnerDecisions(9, 0, minMoves: 3)[2];
        using var service = new AdviceService(new AdviceSettings(Simulations: 200));

        var opponent = AdvisorSession.Load(session.Write());
        while (opponent.State.Step == StepKind.Reveal || opponent.State.PlayerToDecide == 0)
        {
            var s = opponent.State;
            opponent.Enter(s.Step == StepKind.Reveal ? MoveLog.Format(new RandomChanceSource(1).Next(s)) : $"P1 {s.LegalMoves()[0]}");
        }
        var view = service.Request(opponent, longer: false);
        Assert.Equal("unavailable", view.Status);
        Assert.Contains("P2's turn", view.Message);
        Assert.Equal("unavailable", service.View(null).Status);
        Assert.Contains("Name the revealed", service.View(AdvisorSession.New(Ruleset.Default, 0)).Message);

        service.SetAuto(true, opponent);
        Assert.Equal("unavailable", service.View(opponent).Status);
        service.Sync(session);
        Assert.Equal("thinking", service.View(session).Status);
        await service.Running;
        Assert.Equal("ready", service.View(session).Status);
        Assert.True(service.View(session).Auto);
    }

    [Fact]
    public async Task A_computer_too_slow_for_800_simulations_drops_its_default_to_400_and_remembers_it()
    {
        var store = new AdvisorStore(_folder);
        var positions = OwnerDecisions(10, 0, minMoves: 3);
        using (var service = new AdviceService(new AdviceSettings(), store.LoadAdvicePrefs(), logPath: store.AdviceLogPath,
                   savePrefs: store.SaveAdvicePrefs) { SlowSeconds = 0 })
        {
            service.Request(positions[0], false);
            await service.Running;
            Assert.Equal(800, service.DefaultSimulations); // one slow search is not enough
            service.Request(positions[1], false);
            await service.Running;
            Assert.Equal(400, service.DefaultSimulations);
            Assert.Contains("now uses 400", service.View(positions[1]).Notice);
            Assert.Equal(3200, service.ThinkLongerSimulations);
        }
        Assert.Equal(new AdvicePrefs(false, 400), store.LoadAdvicePrefs());
        Assert.Equal(400, new AdviceService(new AdviceSettings(), store.LoadAdvicePrefs()).DefaultSimulations);
        Assert.Equal(800, new AdviceService(new AdviceSettings(), store.LoadAdvicePrefs(), fixedBudget: true).DefaultSimulations);
        var log = File.ReadAllLines(store.AdviceLogPath);
        Assert.Equal(2, log.Length);
        Assert.Contains("sims=800 seconds=", log[0]);
    }
}
