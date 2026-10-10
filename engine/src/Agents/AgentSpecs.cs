namespace SevenWondersDuel.Agents;

/// <summary>
/// Turns an Agent spec from the command line (<c>random</c>, later <c>heuristic:balanced</c>, <c>mcts</c>,
/// <c>neural:&lt;onnx&gt;</c>) into a fresh Agent with its own seed. A spec is a name, optionally followed by
/// <c>:argument</c>. Each game gets new Agents, so an Agent never carries state from one game into the next and games can
/// run on any thread. Later rungs of the Agent Ladder add themselves to <see cref="Factories"/>.
/// </summary>
public static class AgentSpecs
{
    /// <summary>Builds an Agent from the spec's argument (null when there is none) and a seed.</summary>
    public delegate IAgent Factory(string? argument, ulong seed);

    static readonly Dictionary<string, Factory> Factories = new()
    {
        ["random"] = (argument, seed) => argument is null
            ? new RandomAgent(seed)
            : throw new FormatException("The random Agent takes no argument"),
    };

    public static IEnumerable<string> Names => Factories.Keys.Order();

    public static IAgent Create(string spec, ulong seed)
    {
        var colon = spec.IndexOf(':');
        var (name, argument) = colon < 0 ? (spec, null) : (spec[..colon], spec[(colon + 1)..]);
        if (!Factories.TryGetValue(name, out var factory))
            throw new FormatException($"Unknown Agent '{spec}' (known: {string.Join(", ", Names)})");
        return factory(argument, seed);
    }

    /// <summary>Throws a <see cref="FormatException"/> unless the spec names an Agent that can be built.</summary>
    public static void Validate(string spec) => Create(spec, 0);
}
