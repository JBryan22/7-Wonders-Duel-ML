using SevenWondersDuel.Engine;

namespace SevenWondersDuel.Agents.Tests;

/// <summary>
/// Owner ruling 2026-10-26 (spec-pantheon R11, Pantheon ticket 17): a Mythology token's Divinity is placed before the
/// cards the turn uncovered are flipped. Every Agent decides such a placement from an Observation where those cards are
/// still face down (they exist only as an unseen pool), returns a legal placement and leaves the Observation unchanged.
/// </summary>
public class PantheonTokenOrderTests
{
    static string Model(string name) => Path.Combine(AppContext.BaseDirectory, "Models", name);

    /// <summary>Placements decided while the flips are held, from random Pantheon games.</summary>
    static List<Observation> HeldPlacements(int count)
    {
        var rng = new Rng(17);
        var found = new List<Observation>();
        for (var game = 0; game < 100 && found.Count < count; game++)
        {
            var chance = new RandomChanceSource(rng.NextUInt64());
            var s = GameState.New(new Ruleset(Expansions: Expansions.Pantheon));
            while (!s.IsOver && s.Age <= 1 && found.Count < count)
            {
                if (s.Step == StepKind.Reveal)
                {
                    s.Apply(chance.Next(s));
                    continue;
                }
                if (s.Pending(0).Kind == DecisionKind.PlaceDivinity)
                {
                    Assert.Equal(0u, s.SlotsToReveal);
                    // Every token lies on a face-down card, so the flips are held (unless Zeus discarded the card).
                    if (s.FlipsHeld) found.Add(s.Observe(s.PlayerToDecide));
                }
                var moves = s.LegalMoves();
                s.Apply(moves[rng.Next(moves.Count)]);
            }
        }
        Assert.True(found.Count >= count, $"only {found.Count} held placements");
        return found;
    }

    [Theory]
    [InlineData("heuristic")]
    [InlineData("mcts:sims=64")]
    [InlineData("pimc:samples=4,playouts=2")]
    [InlineData("neural")]
    public void Every_agent_places_a_divinity_before_the_flips_without_seeing_them(string spec)
    {
        if (spec == "neural") spec = $"neural:{Model("surgery-child.onnx")},sims=16";
        var seed = 0ul;
        foreach (var observation in HeldPlacements(4))
        {
            var before = observation.State.Clone();
            var move = AgentSpecs.Create(spec, seed++).ChooseMove(observation);
            Assert.Equal(MoveKind.Divinity, move.Kind);
            Assert.True(observation.State.IsLegal(move), $"{spec}: {move}");
            Assert.True(observation.State.SameAs(before), $"{spec} changed the Observation");
            // Played on, the uncovered cards are flipped only after the placement.
            var world = new Determinizer(seed).Sample(observation);
            world.Apply(move);
            if (world.PendingCount == 0 || world.Pending(0).Kind != DecisionKind.PlaceDivinity)
                Assert.NotEqual(0u, world.SlotsToReveal);
        }
    }
}
