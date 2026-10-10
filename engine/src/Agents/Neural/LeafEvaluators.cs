using SevenWondersDuel.Engine;

namespace SevenWondersDuel.Agents;

/// <summary>
/// What the neural search asks at a new leaf: a policy logit for every policy index (illegal Moves are ignored, the
/// search masks them), the outcome-class logits, and the value, "my chance to win" in −1..1 for <c>me</c>, the player to
/// decide. One evaluator belongs to one search, so it is never called from two threads at once.
/// </summary>
public interface ILeafEvaluator
{
    float Evaluate(GameState state, int me, Span<float> policy, Span<float> outcome);
}

/// <summary>The network: encodes the position (spec A6) and runs it through the shared <see cref="InferenceBatcher"/>.</summary>
public sealed class NetworkEvaluator(InferenceBatcher batcher) : ILeafEvaluator
{
    readonly InferenceBatcher.Request _request = new();

    public InferenceBatcher Batcher => batcher;

    public float Evaluate(GameState state, int me, Span<float> policy, Span<float> outcome)
    {
        NetworkEncoding.Encode(state, me, _request.Input);
        batcher.Evaluate(_request);
        _request.Policy.CopyTo(policy);
        _request.Outcome.CopyTo(outcome);
        return _request.Value;
    }
}

/// <summary>
/// The reference with no knowledge: a uniform policy (every logit 0) and value 0. Searching with it is plain tree
/// search that learns only from finished games it reaches (the spec P6 search-bug check; <c>neural:uniform</c>).
/// </summary>
public sealed class UniformEvaluator : ILeafEvaluator
{
    public float Evaluate(GameState state, int me, Span<float> policy, Span<float> outcome)
    {
        policy.Clear();
        outcome.Clear();
        return 0;
    }
}

/// <summary>
/// A uniform policy and, as the value, one uniformly random playout to the end, the estimate plain MCTS uses, so the
/// neural search with it should play like chance-node MCTS (<c>neural:playout</c>). Reveals in the playout come from
/// a Determinizer, uniform over what is unseen. Seeded, so a search with it is deterministic.
/// </summary>
public sealed class PlayoutEvaluator(ulong seed) : ILeafEvaluator
{
    readonly Rng _rng = new(seed);
    readonly Determinizer _chance = new(seed ^ 0x9E3779B97F4A7C15UL);
    readonly GameState _scratch = GameState.New(Ruleset.Default);
    readonly Move[] _moves = new Move[GameState.MaxMoves];

    public float Evaluate(GameState state, int me, Span<float> policy, Span<float> outcome)
    {
        policy.Clear();
        outcome.Clear();
        _scratch.CopyFrom(state);
        var s = _scratch;
        while (!s.IsOver)
        {
            if (s.Step == StepKind.Reveal) s.Apply(_chance.Next(s));
            else s.Apply(_moves[_rng.Next(s.LegalMoves(_moves))]);
        }
        return NetworkEncoding.ValueTarget(s.Result!, me);
    }
}
