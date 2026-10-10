using SevenWondersDuel.Engine;

namespace SevenWondersDuel.Agents;

/// <summary>The bottom rung of the Agent Ladder: a uniformly random legal Move, from its own seeded generator.</summary>
public sealed class RandomAgent(ulong seed) : IAgent
{
    readonly Rng _rng = new(seed);

    public Move ChooseMove(Observation observation)
    {
        Span<Move> moves = stackalloc Move[GameState.MaxMoves];
        var n = observation.LegalMoves(moves);
        if (n == 0) throw new InvalidOperationException("No legal Move");
        return moves[_rng.Next(n)];
    }
}
