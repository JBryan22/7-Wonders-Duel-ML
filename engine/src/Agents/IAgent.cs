using SevenWondersDuel.Engine;

namespace SevenWondersDuel.Agents;

/// <summary>
/// Anything that chooses a Move. It is asked only when the state is waiting for a decision, for
/// <see cref="GameState.PlayerToDecide"/>, and sees only that player's <see cref="Observation"/> (honesty rule, spec A1).
/// </summary>
public interface IAgent
{
    Move ChooseMove(Observation observation);
}
