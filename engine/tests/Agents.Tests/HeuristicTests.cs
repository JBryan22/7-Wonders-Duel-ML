using SevenWondersDuel.Engine;
using SevenWondersDuel.Engine.Data;

namespace SevenWondersDuel.Agents.Tests;

/// <summary>The Heuristic rung of the Agent Ladder (spec A5; P4): presets, adaptive balanced, promo Wonders.</summary>
public class HeuristicTests
{
    const Promos BothPromos = Promos.Messe | Promos.Liberty;

    [Fact]
    public void Specs_name_a_preset_with_optional_weight_overrides()
    {
        Assert.Equal(HeuristicWeights.Balanced, ((HeuristicAgent)AgentSpecs.Create("heuristic", 1)).Weights);
        foreach (var preset in HeuristicWeights.Presets)
            Assert.Equal(preset, ((HeuristicAgent)AgentSpecs.Create($"heuristic:{preset.Name}", 1)).Weights);
        var tuned = ((HeuristicAgent)AgentSpecs.Create("heuristic:military,science=2.5,adaptive=1", 1)).Weights;
        Assert.Equal(HeuristicWeights.MilitaryStyle with { Science = 2.5, Adaptive = true }, tuned);
        Assert.Throws<FormatException>(() => AgentSpecs.Create("heuristic:greedy", 1));
        Assert.Throws<FormatException>(() => AgentSpecs.Create("heuristic:balanced,luck=2", 1));
        Assert.Throws<FormatException>(() => AgentSpecs.Create("heuristic:balanced,science=lots", 1));
    }

    [Theory]
    [InlineData(Promos.None)]
    [InlineData(BothPromos)]
    public void Adaptive_balanced_passes_random_by_a_clear_margin(Promos promos)
    {
        var report = Match.Run(new MatchSettings("heuristic:balanced", "random", Promos: promos));
        Assert.True(report.APasses, report.ToString());
        Assert.True(report.WinRate.Low > 0.9, report.ToString());
        Assert.True(report.EloA > 400, report.ToString());
    }

    [Fact]
    public void Each_style_presets_victory_type_mix_skews_as_intended()
    {
        var share = new Dictionary<string, Dictionary<VictoryType, double>>();
        foreach (var preset in new[] { "science", "military", "civilian", "balanced" })
        {
            var report = Match.Run(new MatchSettings($"heuristic:{preset}", "random", Pairs: 150, FirstSeed: 500));
            Assert.True(report.APasses, report.ToString());
            share[preset] = Enum.GetValues<VictoryType>()
                .ToDictionary(v => v, v => (double)report.Wins(MatchWinner.A, v) / report.WinsA);
        }
        string Table() => string.Join("; ", share.Select(p => $"{p.Key}: " + string.Join(" ", p.Value.Select(v => $"{v.Key} {v.Value:P0}"))));

        foreach (var other in new[] { "military", "civilian", "balanced" })
            Assert.True(share["science"][VictoryType.Science] > share[other][VictoryType.Science] + 0.05, Table());
        foreach (var other in new[] { "science", "civilian", "balanced" })
            Assert.True(share["military"][VictoryType.Military] > share[other][VictoryType.Military] + 0.2, Table());
        foreach (var other in new[] { "science", "military" })
            Assert.True(share["civilian"][VictoryType.Civilian] > share[other][VictoryType.Civilian], Table());
    }

    /// <summary>
    /// Plays random games with both promos and, at every decision, asks a Heuristic what it would do: it must not change
    /// the Observation it was given, and when some Move wins on the spot (after any token pick of its own) it must pick
    /// a winning Move.
    /// </summary>
    [Fact]
    public void It_takes_an_immediate_win_and_leaves_the_Observation_alone()
    {
        var rng = new Rng(42);
        var wins = 0;
        for (var game = 0; game < 150; game++)
        {
            var chance = new RandomChanceSource(rng.NextUInt64());
            var heuristic = new HeuristicAgent(HeuristicWeights.Balanced, rng.NextUInt64());
            var s = GameState.New(new Ruleset(Promos: BothPromos));
            while (!s.IsOver)
            {
                if (s.Step == StepKind.Reveal)
                {
                    s.Apply(chance.Next(s));
                    continue;
                }
                var me = s.PlayerToDecide;
                var observation = s.Observe(me);
                var before = observation.State.Clone();
                var choice = heuristic.ChooseMove(observation);
                Assert.True(observation.State.SameAs(before));
                Assert.True(s.IsLegal(choice), $"{choice} is not legal");

                var moves = s.LegalMoves();
                bool Wins(Move m) => WinsOnTheSpot(s, me, m);
                if (moves.Any(Wins))
                {
                    wins++;
                    Assert.True(Wins(choice), $"{choice} does not win but one of {string.Join(", ", moves)} does\n{s}");
                }
                s.Apply(moves[rng.Next(moves.Count)]);
            }
        }
        Assert.True(wins > 20, $"only {wins} positions with a winning Move");
    }

    /// <summary>
    /// True if the Move wins at once, or through some choice of the same player's that follows it without a Reveal
    /// (a token pick, a Mausoleum or Messe card, a Statue keep and give).
    /// </summary>
    static bool WinsOnTheSpot(GameState s, int me, Move m)
    {
        var next = s.Clone();
        next.Apply(m);
        if (next.Result is { } r) return r.Winner == me;
        return next.Step == StepKind.Decision && next.PlayerToDecide == me && next.Pending(0).Kind != DecisionKind.TakeCard
            && next.LegalMoves().Any(n => WinsOnTheSpot(next, me, n));
    }

    [Fact]
    public void Its_choices_depend_only_on_its_seed_and_the_Observation()
    {
        static string Log(ulong seed) => GameRunner.Play(new Ruleset(Promos: BothPromos), new RandomChanceSource(3),
            new HeuristicAgent(HeuristicWeights.Balanced, seed), new HeuristicAgent(HeuristicWeights.ScienceStyle, seed + 1)).WriteLog();
        Assert.Equal(Log(7), Log(7));
    }

    [Fact]
    public void It_drafts_builds_and_resolves_the_promo_Wonders()
    {
        var built = new Dictionary<WonderEffect, int> { [WonderEffect.Messe] = 0, [WonderEffect.Liberty] = 0 };
        var rng = new Rng(9);
        for (var game = 0; game < 100; game++)
        {
            var g = GameRunner.Play(new Ruleset(Promos: BothPromos), new RandomChanceSource(rng.NextUInt64()),
                new HeuristicAgent(HeuristicWeights.Balanced, rng.NextUInt64()),
                new HeuristicAgent(HeuristicWeights.Balanced, rng.NextUInt64()));
            foreach (var p in new[] { 0, 1 })
                foreach (var w in GameState.WondersIn(g.State.BuiltWonders(p)))
                    if (built.ContainsKey(GameData.Wonders[w].Effect)) built[GameData.Wonders[w].Effect]++;
        }
        Assert.All(built.Values, n => Assert.True(n >= 10, string.Join(", ", built)));
    }
}
