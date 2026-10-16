using SevenWondersDuel.Engine;

namespace SevenWondersDuel.Agents.Tests;

/// <summary>
/// No-peeking with Pantheon (spec-pantheon P-A5): two Game States with the same Observation but different hidden
/// Divinities and deck tops (the opponent's face-down Divinities, the cards they put back, what their Divine Theater
/// showed them) give the same search result under the same seed, for PIMC and for the chance-node MCTS.
/// </summary>
public class PantheonNoPeekingTests
{
    /// <summary>
    /// Positions from random Pantheon games where the player to decide cannot see some of the opponent's Pantheon
    /// knowledge, each with two true states that differ in it: both sampled from that player's Observation, so both are
    /// legal states that look the same to them.
    /// </summary>
    static IEnumerable<(GameState A, GameState B)> HiddenPositions(int count)
    {
        var rng = new Rng(31);
        var found = 0;
        for (var game = 0; game < 400 && found < count; game++)
        {
            var chance = new RandomChanceSource(rng.NextUInt64());
            var s = GameState.New(new Ruleset(Expansions: Expansions.Pantheon));
            while (!s.IsOver && found < count)
            {
                if (s.Step == StepKind.Reveal)
                {
                    s.Apply(chance.Next(s));
                    continue;
                }
                var observation = s.Observe(s.PlayerToDecide);
                if (!observation.State.SameAs(s) && rng.Next(3) == 0)
                {
                    var a = new Determinizer(rng.NextUInt64()).Sample(observation);
                    for (var tries = 0; tries < 20; tries++)
                    {
                        var b = new Determinizer(rng.NextUInt64()).Sample(observation);
                        if (a.SameAs(b)) continue;
                        found++;
                        yield return (a, b);
                        break;
                    }
                }
                var moves = s.LegalMoves();
                s.Apply(moves[rng.Next(moves.Count)]);
            }
        }
        Assert.True(found >= count, $"only {found} positions with hidden Pantheon knowledge");
    }

    [Fact]
    public void Pimc_gives_the_same_result_whatever_the_opponents_hidden_divinities()
    {
        var seeds = new Rng(5);
        foreach (var (a, b) in HiddenPositions(6))
        {
            var player = a.PlayerToDecide;
            var observationA = a.Observe(player);
            var observationB = b.Observe(player);
            Assert.True(observationA.SameAs(observationB));
            var before = observationA.State.Clone();
            var seed = seeds.NextUInt64();
            var resultA = new PimcAgent(new PimcSettings(Samples: 8), seed).Search(observationA);
            var resultB = new PimcAgent(new PimcSettings(Samples: 8), seed).Search(observationB);
            Assert.True(observationA.State.SameAs(before), "PIMC changed the Observation");
            Assert.Equal(resultA.Best, resultB.Best);
            Assert.Equal(resultA.Moves, resultB.Moves);
        }
    }

    [Fact]
    public void Mcts_gives_the_same_result_whatever_the_opponents_hidden_divinities()
    {
        var seeds = new Rng(6);
        foreach (var (a, b) in HiddenPositions(6))
        {
            var player = a.PlayerToDecide;
            var observationA = a.Observe(player);
            var observationB = b.Observe(player);
            Assert.True(observationA.SameAs(observationB));
            var before = observationA.State.Clone();
            var seed = seeds.NextUInt64();
            var resultA = new MctsAgent(new MctsSettings(Simulations: 60), seed).Search(observationA);
            var resultB = new MctsAgent(new MctsSettings(Simulations: 60), seed).Search(observationB);
            Assert.True(observationA.State.SameAs(before), "MCTS changed the Observation");
            Assert.Equal(resultA.Best, resultB.Best);
            Assert.Equal(resultA.Moves, resultB.Moves);
            Assert.Equal(resultA.Line, resultB.Line);
        }
    }
}
