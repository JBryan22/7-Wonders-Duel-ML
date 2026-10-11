using SevenWondersDuel.Engine;

namespace SevenWondersDuel.Agents.Tests;

/// <summary>The chance-node MCTS rung of the Agent Ladder, the Anchor (spec A5; P5): passes PIMC, deterministic, Observation only.</summary>
public class MctsTests
{
    const Promos BothPromos = Promos.Messe | Promos.Liberty;

    [Fact]
    public void Specs_set_the_budget_exploration_and_margin_weight()
    {
        Assert.Equal(new MctsSettings(1000, 0.7, 0.02), ((MctsAgent)AgentSpecs.Create("mcts", 1)).Settings);
        Assert.Equal(new MctsSettings(4000, 1.5, 0), ((MctsAgent)AgentSpecs.Create("mcts:sims=4000,c=1.5,margin=0", 1)).Settings);
        Assert.Equal(200, ((MctsAgent)AgentSpecs.Create("mcts:simulations=200", 1)).Settings.Simulations);
        Assert.Throws<FormatException>(() => AgentSpecs.Create("mcts:sims=0", 1));
        Assert.Throws<FormatException>(() => AgentSpecs.Create("mcts:sims=2.5", 1));
        Assert.Throws<FormatException>(() => AgentSpecs.Create("mcts:depth=3", 1));
        Assert.Throws<FormatException>(() => AgentSpecs.Create("mcts:c", 1));
    }

    /// <summary>
    /// The pass rule against PIMC at default settings. 100 seed pairs keep the test quick (the recorded 400-game Match
    /// scored 80.7 %); the lower bound still clears 50 % by a wide margin.
    /// </summary>
    [Fact]
    public void It_passes_PIMC()
    {
        var report = Match.Run(new MatchSettings("mcts", "pimc", Pairs: 100));
        Assert.True(report.APasses, report.ToString());
        Assert.Equal(Ladder.AnchorElo, report.EloA);
        Assert.Equal(Ladder.Elo("pimc"), report.EloB);
    }

    /// <summary>After a search the Advisor reads each Move's line and the tree below it (ticket 31).</summary>
    [Fact]
    public void Lines_and_children_can_be_read_for_every_root_Move()
    {
        var chance = new RandomChanceSource(31);
        var rng = new Rng(31);
        var s = GameState.New(Ruleset.Default);
        var searched = 0;
        while (!s.IsOver && searched < 6)
        {
            if (s.Step == StepKind.Reveal) { s.Apply(chance.Next(s)); continue; }
            var moves = s.LegalMoves();
            if (moves.Count > 2 && s.Age > 0)
            {
                searched++;
                var agent = new MctsAgent(MctsSettings.Default with { Simulations = 600 }, 7);
                var r = agent.Search(s.Observe(s.PlayerToDecide));
                Assert.Equal(r.Line, agent.LineOf([r.Best]));
                Assert.Empty(agent.LineOf([]));
                Assert.Equal(r.Moves.Where(m => m.Visits > 0).Select(m => (m.Move, m.Visits)).OrderBy(x => x.Move.ToString()),
                    agent.ChildrenOf([]).Select(c => (c.Move, c.Visits)).OrderBy(x => x.Move.ToString()));
                foreach (var m in r.Moves)
                {
                    var line = agent.LineOf([m.Move]);
                    if (m.Visits < MctsAgent.MinLineVisits) Assert.Empty(line);
                    else Assert.Equal(m.Move, line[0].Move);
                    Assert.All(line, step => Assert.True(step.Visits >= MctsAgent.MinLineVisits));
                    var children = agent.ChildrenOf([m.Move]);
                    Assert.True(children.Sum(c => c.Visits) <= m.Visits);
                    if (children.Count > 0 && line.Count > 1)
                    {
                        Assert.Equal(line.Skip(1), agent.LineOf([m.Move, line[1].Move]).Skip(1));
                        Assert.Equal(children.Max(c => c.Visits), children.Single(c => c.Move == line[1].Move).Visits);
                    }
                }
            }
            s.Apply(moves[rng.Next(moves.Count)]);
        }
        Assert.Equal(6, searched);
    }

    [Fact]
    public void The_Anchor_is_the_recorded_top_of_the_classic_Ladder()
    {
        Assert.Equal(0, Ladder.Elo("random"));
        Assert.True(Ladder.Elo("heuristic:balanced") < Ladder.Elo("pimc"));
        Assert.True(Ladder.Elo("pimc") < Ladder.Elo(Ladder.Anchor));
        Assert.IsType<MctsAgent>(AgentSpecs.Create(Ladder.Anchor, 1));
        Assert.Null(Ladder.Elo("mcts:sims=10"));
    }

    /// <summary>
    /// No-peeking test (spec P5), as for PIMC: random games are stopped at each Great Library draw (the only hidden
    /// card a Game State holds), the draw is made two ways, and MCTS searches the drawer's pick from the opponent's
    /// Observation of each. Statistics and principal line must match exactly, and the Observation must be untouched.
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
            var resultA = new MctsAgent(new MctsSettings(Simulations: 60), seed).Search(observationA);
            var resultB = new MctsAgent(new MctsSettings(Simulations: 60), seed).Search(observationB);
            Assert.True(observationA.State.SameAs(before), "MCTS changed the Observation");
            Assert.Equal(resultA.Best, resultB.Best);
            Assert.Equal(resultA.Moves, resultB.Moves);
            Assert.Equal(resultA.Line, resultB.Line);
            // All 5 options are searched; each simulation goes through one that its sampled draw holds.
            Assert.Equal(5, resultA.Moves.Count);
            Assert.Equal(60, resultA.Moves.Sum(m => m.Visits));
        }
    }

    [Fact]
    public void Its_choices_and_statistics_depend_only_on_its_seed_and_the_Observation()
    {
        static string Log(ulong seed) => GameRunner.Play(new Ruleset(Promos: BothPromos), new RandomChanceSource(3),
            new MctsAgent(new MctsSettings(Simulations: 100), seed), new HeuristicAgent(HeuristicWeights.Balanced, 4)).WriteLog();
        Assert.Equal(Log(7), Log(7));
        Assert.NotEqual(Log(7), Log(8));

        var s = GameState.New(Ruleset.Default);
        var chance = new RandomChanceSource(5);
        while (s.Step == StepKind.Reveal) s.Apply(chance.Next(s));
        var observation = s.Observe(s.PlayerToDecide);
        var first = new MctsAgent(MctsSettings.Default, 11).Search(observation);
        var second = new MctsAgent(MctsSettings.Default, 11).Search(observation);
        Assert.Equal(first.Moves, second.Moves);
        Assert.Equal(first.Line, second.Line);
    }

    /// <summary>
    /// The Agent output (spec A5): visit shares summing to 1, per-Move values with standard errors, the most visited
    /// Move played, and a principal line that starts with it, alternates sensibly and stops where the search is thin.
    /// </summary>
    [Fact]
    public void It_reports_visit_shares_values_and_the_principal_line()
    {
        var rng = new Rng(4);
        var chance = new RandomChanceSource(rng.NextUInt64());
        var mcts = new MctsAgent(new MctsSettings(Simulations: 300), rng.NextUInt64());
        var s = GameState.New(Ruleset.Default);
        var searches = 0;
        var longLines = 0;
        var reveals = 0;
        while (!s.IsOver)
        {
            if (s.Step == StepKind.Reveal)
            {
                s.Apply(chance.Next(s));
                continue;
            }
            var observation = s.Observe(s.PlayerToDecide);
            var move = mcts.ChooseMove(observation);
            Assert.True(s.IsLegal(move));
            if (mcts.LastSearch is { } search)
            {
                searches++;
                Assert.Equal(move, search.Best);
                Assert.Equal(observation.LegalMoves(), search.Moves.Select(m => m.Move));
                Assert.Equal(300, search.Moves.Sum(m => m.Visits));
                Assert.Equal(1, search.Moves.Sum(m => m.VisitShare), 9);
                Assert.All(search.Moves, m =>
                {
                    Assert.InRange(m.Value, 0, 1);
                    Assert.InRange(m.StdErr, 0, 0.5);
                });
                Assert.Equal(search.Moves.Max(m => m.Visits), search.Moves.First(m => m.Move == move).Visits);
                Assert.NotEmpty(search.Line);
                Assert.Equal(new LineStep(s.PlayerToDecide, move, search.Line[0].Visits, search.Line[0].Value, false), search.Line[0]);
                for (var i = 1; i < search.Line.Count; i++)
                    Assert.InRange(search.Line[i].Visits, MctsAgent.MinLineVisits, search.Line[i - 1].Visits);
                if (search.Line.Count >= 3) longLines++;
                reveals += search.Line.Count(l => l.AfterReveal);
            }
            else Assert.Single(observation.LegalMoves());
            s.Apply(move);
        }
        Assert.True(searches > 20);
        Assert.True(longLines > 10, $"{longLines} lines of 3+ steps");
        Assert.True(reveals > 0, "no chance step in any principal line");
    }
}
