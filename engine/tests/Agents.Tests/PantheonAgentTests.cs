using SevenWondersDuel.Engine;
using SevenWondersDuel.Engine.Data;
using Xunit.Abstractions;

namespace SevenWondersDuel.Agents.Tests;

/// <summary>
/// The Agent Ladder in Pantheon games (spec-pantheon P-A6): the Heuristic's Pantheon values, PIMC and MCTS through the
/// Engine and the Determinizer, the Pantheon Elo scale and <c>--expansion pantheon</c> Matches.
/// </summary>
public class PantheonAgentTests(ITestOutputHelper output)
{
    static readonly Ruleset Pantheon = new(Expansions: Expansions.Pantheon);

    /// <summary>What a Heuristic did over a number of Pantheon games against an opponent.</summary>
    sealed record ActivationStats(int Games, int GamesWithActivation, int Activations, int[] PerDivinity)
    {
        public double Share => (double)GamesWithActivation / Games;
        public override string ToString() =>
            $"{GamesWithActivation}/{Games} games with an activation ({Share:P0}), {(double)Activations / Games:0.00} per game; "
            + string.Join(" ", PantheonData.Divinities.Select(d => $"{d.Id} {PerDivinity[d.Index]}"));
    }

    /// <summary>Plays <paramref name="games"/> Pantheon games of <paramref name="spec"/> against an opponent (seats alternate) and counts its activations.</summary>
    static ActivationStats Activations(string spec, string opponent, int games, Promos promos = Promos.None)
    {
        var rng = new Rng(808);
        var per = new int[PantheonData.Divinities.Length];
        var (withActivation, total) = (0, 0);
        for (var g = 0; g < games; g++)
        {
            var chance = new RandomChanceSource(rng.NextUInt64());
            var seat = g % 2;
            IAgent[] agents = seat == 0
                ? [AgentSpecs.Create(spec, rng.NextUInt64()), AgentSpecs.Create(opponent, rng.NextUInt64())]
                : [AgentSpecs.Create(opponent, rng.NextUInt64()), AgentSpecs.Create(spec, rng.NextUInt64())];
            var s = GameState.New(new Ruleset(Promos: promos, Expansions: Expansions.Pantheon));
            while (!s.IsOver)
                if (s.Step == StepKind.Reveal) s.Apply(chance.Next(s));
                else s.Apply(agents[s.PlayerToDecide].ChooseMove(s.Observe(s.PlayerToDecide)));
            var activated = new Bits(s.Pantheon!.Activated(seat)).ToArray();
            if (activated.Length > 0) withActivation++;
            total += activated.Length;
            foreach (var d in activated) per[d]++;
        }
        return new(games, withActivation, total, per);
    }

    [Fact]
    public void The_heuristic_activates_divinities_in_most_pantheon_games()
    {
        var stats = Activations("heuristic", "heuristic", 200);
        output.WriteLine($"heuristic vs heuristic: {stats}");
        Assert.True(stats.Share > 0.6, stats.ToString());
        Assert.True(stats.PerDivinity.Count(n => n > 0) >= 10, stats.ToString());
    }

    [Theory]
    [InlineData(Promos.None)]
    [InlineData(Promos.Messe | Promos.Liberty)]
    public void Adaptive_balanced_passes_random_in_pantheon_games_on_the_pantheon_scale(Promos promos)
    {
        var report = Match.Run(new MatchSettings("heuristic:balanced", "random", Pairs: 100, Promos: promos,
            Expansions: Expansions.Pantheon));
        Assert.True(report.WinRate.Low > 0.9, report.ToString());
        Assert.Contains("expansion=pantheon", report.ToString());
        Assert.Equal(Ladder.Elo("heuristic", Expansions.Pantheon), report.EloA);
    }

    [Fact]
    public void The_pantheon_values_beat_a_heuristic_blind_to_them()
    {
        var report = Match.Run(new MatchSettings("heuristic", "heuristic:balanced,pantheon=0", Pairs: 200, FirstSeed: 100,
            Expansions: Expansions.Pantheon));
        Assert.True(report.APasses, report.ToString());
    }

    /// <summary>The Pantheon terms never reach a base game: switching them off changes no choice there.</summary>
    [Fact]
    public void The_pantheon_values_change_nothing_in_base_games()
    {
        var blind = HeuristicWeights.Balanced with { Pantheon = 0 };
        Assert.Equal(blind, HeuristicWeights.Parse("balanced,pantheon=0"));
        for (ulong seed = 0; seed < 20; seed++)
        {
            string Log(HeuristicWeights w) => GameRunner.Play(new Ruleset(Promos: (Promos)(seed % 4)), new RandomChanceSource(seed),
                new HeuristicAgent(w, seed), new HeuristicAgent(HeuristicWeights.ScienceStyle with { Pantheon = w.Pantheon }, seed + 1)).WriteLog();
            Assert.Equal(Log(HeuristicWeights.Balanced), Log(blind));
        }
    }

    /// <summary>
    /// Random Pantheon games (both promos): at every decision, including every Pantheon one, the Heuristic picks a legal
    /// Move and leaves its Observation alone (its follow-up Reveals are sampled on copies).
    /// </summary>
    [Fact]
    public void The_heuristic_answers_every_pantheon_decision_and_leaves_the_Observation_alone()
    {
        var rng = new Rng(43);
        var kinds = new HashSet<DecisionKind>();
        for (var game = 0; game < 120; game++)
        {
            var chance = new RandomChanceSource(rng.NextUInt64());
            var heuristic = new HeuristicAgent(HeuristicWeights.Balanced, rng.NextUInt64());
            var s = GameState.New(new Ruleset(Promos: Promos.Messe | Promos.Liberty, Expansions: Expansions.Pantheon));
            while (!s.IsOver)
            {
                if (s.Step == StepKind.Reveal)
                {
                    s.Apply(chance.Next(s));
                    continue;
                }
                var observation = s.Observe(s.PlayerToDecide);
                var before = observation.State.Clone();
                var choice = heuristic.ChooseMove(observation);
                Assert.True(observation.State.SameAs(before));
                Assert.True(s.IsLegal(choice), $"{choice} is not legal");
                kinds.Add(s.Pending(0).Kind);
                var moves = s.LegalMoves();
                s.Apply(rng.Next(3) == 0 ? choice : moves[rng.Next(moves.Count)]);
            }
        }
        foreach (var kind in new[] { DecisionKind.PlaceDivinity, DecisionKind.Offer, DecisionKind.Enki, DecisionKind.Gate, DecisionKind.Minerva })
            Assert.Contains(kind, kinds);
    }

    [Fact]
    public void Each_preset_keeps_its_skew_in_pantheon_games()
    {
        double Share(string preset, VictoryType v)
        {
            var report = Match.Run(new MatchSettings($"heuristic:{preset}", "random", Pairs: 100, FirstSeed: 500,
                Expansions: Expansions.Pantheon));
            Assert.True(report.APasses, report.ToString());
            return (double)report.Wins(MatchWinner.A, v) / report.WinsA;
        }
        Assert.True(Share("science", VictoryType.Science) > Share("balanced", VictoryType.Science) + 0.05);
        Assert.True(Share("military", VictoryType.Military) > Share("balanced", VictoryType.Military) + 0.2);
        Assert.True(Share("civilian", VictoryType.Civilian) > Share("military", VictoryType.Civilian));
    }

    /// <summary>PIMC and MCTS need no Pantheon code: small searches play whole Pantheon games with both promos.</summary>
    [Fact]
    public void Pimc_and_mcts_play_pantheon_games_through_the_engine_and_the_determinizer()
    {
        var report = Match.Run(new MatchSettings("mcts:sims=60", "pimc:samples=6", Pairs: 6,
            Promos: Promos.Messe | Promos.Liberty, Expansions: Expansions.Pantheon));
        Assert.True(report.Complete, report.ToString());
        Assert.Equal(12, report.GameCount);
    }

    [Fact]
    public void Pantheon_and_base_elo_are_separate_scales()
    {
        Assert.Equal(Ladder.AnchorElo, Ladder.Elo(Ladder.Anchor, Expansions.None));
        Assert.Equal(Ladder.PantheonAnchorElo, Ladder.Elo(Ladder.PantheonAnchor, Expansions.Pantheon));
        Assert.Equal(0, Ladder.Elo("random", Expansions.Pantheon));
        Assert.Null(Ladder.Elo(Ladder.Reference, Expansions.Pantheon));
        Assert.True(Ladder.Elo(Ladder.PantheonPimc, Expansions.Pantheon) > Ladder.Elo("heuristic", Expansions.Pantheon));
        Assert.True(Ladder.PantheonAnchorElo > Ladder.Elo(Ladder.PantheonPimc, Expansions.Pantheon));
        // A base Match's header and rules are unchanged by the new setting.
        Assert.Equal("rules base start-choice=new", new MatchSettings("a", "b").Rules.ToString());
    }
}
