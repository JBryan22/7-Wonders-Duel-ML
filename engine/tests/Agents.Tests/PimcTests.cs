using SevenWondersDuel.Engine;

namespace SevenWondersDuel.Agents.Tests;

/// <summary>The PIMC rung of the Agent Ladder (spec A5; P5): passes the Heuristic, searches from the Observation only.</summary>
public class PimcTests
{
    const Promos BothPromos = Promos.Messe | Promos.Liberty;

    [Fact]
    public void Specs_set_the_budget_and_the_margin_weight()
    {
        Assert.Equal(new PimcSettings(50, 1, 0.02), ((PimcAgent)AgentSpecs.Create("pimc", 1)).Settings);
        Assert.Equal(new PimcSettings(200, 2, 0), ((PimcAgent)AgentSpecs.Create("pimc:samples=200,playouts=2,margin=0", 1)).Settings);
        Assert.Throws<FormatException>(() => AgentSpecs.Create("pimc:samples=0", 1));
        Assert.Throws<FormatException>(() => AgentSpecs.Create("pimc:samples=2.5", 1));
        Assert.Throws<FormatException>(() => AgentSpecs.Create("pimc:depth=3", 1));
        Assert.Throws<FormatException>(() => AgentSpecs.Create("pimc:samples", 1));
    }

    [Fact]
    public void It_passes_the_adaptive_balanced_Heuristic()
    {
        var report = Match.Run(new MatchSettings("pimc", "heuristic:balanced"));
        Assert.True(report.APasses, report.ToString());
    }

    [Fact]
    public void It_passes_the_adaptive_balanced_Heuristic_with_both_promos()
    {
        var report = Match.Run(new MatchSettings("pimc", "heuristic:balanced", Pairs: 100, Promos: BothPromos));
        Assert.True(report.WinRate.Score > 0.5, report.ToString());
    }

    /// <summary>
    /// No-peeking test (spec P5): two Game States with the same Observation but different hidden cards give the same
    /// search result under the same seed. The base game's only hidden card in a Game State is the Great Library's
    /// private draw (ADR 0002 keeps every other unknown out of the state), so random games are stopped at each Great
    /// Library draw, the draw is made two different ways, and PIMC searches the drawer's pick from the opponent's
    /// Observation of each. Its statistics must match exactly, and it must leave the Observation alone.
    /// </summary>
    [Fact]
    public void Same_Observation_with_different_hidden_cards_gives_the_same_result()
    {
        var rng = new Rng(21);
        var checkedDraws = 0;
        for (var game = 0; game < 400 && checkedDraws < 8; game++)
        {
            var chance = new RandomChanceSource(rng.NextUInt64());
            var s = GameState.New(new Ruleset(Promos: game % 2 == 0 ? Promos.None : BothPromos));
            while (!s.IsOver)
            {
                if (s.Step == StepKind.Reveal)
                {
                    if (s.SlotsToReveal == 0 && s.AwaitingLibraryDraw)
                    {
                        Check(s, rng.NextUInt64());
                        checkedDraws++;
                    }
                    s.Apply(chance.Next(s));
                    continue;
                }
                var moves = s.LegalMoves();
                s.Apply(moves[rng.Next(moves.Count)]);
            }
        }
        Assert.True(checkedDraws >= 8, $"only {checkedDraws} Great Library draws");

        static void Check(GameState s, ulong seed)
        {
            var drawer = s.PlayerToDecide;
            var box = ProgressTokens.In(s.TokensSetAside).ToArray();
            var a = s.Clone();
            a.Apply(new LibraryReveal(drawer, box[..3]));
            var b = s.Clone();
            b.Apply(new LibraryReveal(drawer, box[^3..]));
            Assert.False(a.SameAs(b));

            var observationA = a.Observe(1 - drawer);
            var observationB = b.Observe(1 - drawer);
            Assert.True(observationA.SameAs(observationB));
            var before = observationA.State.Clone();
            var resultA = new PimcAgent(new PimcSettings(Samples: 12), seed).Search(observationA);
            var resultB = new PimcAgent(new PimcSettings(Samples: 12), seed).Search(observationB);
            Assert.True(observationA.State.SameAs(before), "PIMC changed the Observation");
            Assert.Equal(resultA.Best, resultB.Best);
            Assert.Equal(resultA.Moves, resultB.Moves);
            // Each of the 5 options was counted in the worlds whose sampled draw holds it (3 in 5 on average).
            Assert.Equal(5, resultA.Moves.Count);
            Assert.Equal(12 * 3, resultA.Moves.Sum(m => m.Playouts));
        }
    }

    [Fact]
    public void Its_choices_depend_only_on_its_seed_and_the_Observation()
    {
        static string Log(ulong seed) => GameRunner.Play(new Ruleset(Promos: BothPromos), new RandomChanceSource(3),
            new PimcAgent(new PimcSettings(Samples: 8), seed), new HeuristicAgent(HeuristicWeights.Balanced, 4)).WriteLog();
        Assert.Equal(Log(7), Log(7));
        Assert.NotEqual(Log(7), Log(8));
    }

    /// <summary>
    /// Search statistics are sane: every legal Move gets every playout in a public position, values are win rates
    /// with a standard error, and the chosen Move has the best ranking key.
    /// </summary>
    [Fact]
    public void It_reports_per_Move_statistics_and_plays_the_best()
    {
        var rng = new Rng(4);
        var chance = new RandomChanceSource(rng.NextUInt64());
        var pimc = new PimcAgent(new PimcSettings(Samples: 10, Playouts: 2), rng.NextUInt64());
        var s = GameState.New(Ruleset.Default);
        var searches = 0;
        while (!s.IsOver)
        {
            if (s.Step == StepKind.Reveal)
            {
                s.Apply(chance.Next(s));
                continue;
            }
            var observation = s.Observe(s.PlayerToDecide);
            var move = pimc.ChooseMove(observation);
            Assert.True(s.IsLegal(move));
            if (pimc.LastSearch is { } search)
            {
                searches++;
                Assert.Equal(move, search.Best);
                Assert.Equal(observation.LegalMoves(), search.Moves.Select(m => m.Move));
                Assert.All(search.Moves, m =>
                {
                    Assert.Equal(20, m.Playouts);
                    Assert.InRange(m.Value, 0, 1);
                    Assert.InRange(m.StdErr, 0, 0.5);
                });
                var best = search.Moves.Max(m => m.Value + pimc.Settings.Margin * m.Margin);
                var chosen = search.Moves.First(m => m.Move == move);
                Assert.Equal(best, chosen.Value + pimc.Settings.Margin * chosen.Margin, 9);
            }
            else Assert.Single(observation.LegalMoves());
            s.Apply(move);
        }
        Assert.True(searches > 20);
    }
}
