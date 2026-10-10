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
    };

    /// <summary>The recorded Elo of an Agent spec, or null if it is not a rung at its default settings.</summary>
    public static double? Elo(string spec) => Ratings.TryGetValue(spec, out var elo) ? elo : null;
}
