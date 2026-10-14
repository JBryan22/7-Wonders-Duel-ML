namespace SevenWondersDuel.Agents;

/// <summary>
/// The recorded Elo of each rung of the Agent Ladder on the shared scale (spec A5: Random = 0), at its default
/// settings. Each was measured once in a 400-game base-only Match against the rung below and is fixed from then on;
/// in particular <see cref="Anchor"/>'s Elo never changes, so a network's Elo against it is comparable across Runs. A
/// Match uses these when no <c>--elo-a</c>/<c>--elo-b</c> is given.
/// </summary>
public static class Ladder
{
    /// <summary>The Anchor's Agent spec: chance-node MCTS at 1,000 simulations.</summary>
    public const string Anchor = "mcts";

    /// <summary>The Anchor's fixed Elo (Random = 0).</summary>
    public const double AnchorElo = 1512;

    static readonly Dictionary<string, double> Ratings = new()
    {
        ["random"] = 0,
        // 3,995–5 in 4,000 games against Random (ticket 17; ticket 15's 400-game Match was 400–0, capped at +1161 too).
        ["heuristic"] = 1161,
        ["heuristic:balanced"] = 1161,
        // +102 against heuristic:balanced (ticket 16, 64.2 %).
        ["pimc"] = 1263,
        // +249 against pimc (ticket 17, 80.7 %).
        [Anchor] = AnchorElo,
        // The main Run's Reference (ticket 28): the Anchor at 20x the simulations. +200 against the Anchor (76.0 %,
        // CI 71.6-79.9 %, 400 games); 678 ms per Move vs the Anchor's 32 ms (16 game threads on the desktop).
        [Reference] = 1712,
    };

    /// <summary>The Reference's Agent spec: a stronger fixed opponent than the Anchor (ticket 28).</summary>
    public const string Reference = "mcts:sims=20000";

    /// <summary>The recorded Elo of an Agent spec, or null if it is not a rung at its default settings.</summary>
    public static double? Elo(string spec) => Ratings.TryGetValue(spec, out var elo) ? elo : null;
}
