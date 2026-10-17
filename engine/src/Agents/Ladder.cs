using SevenWondersDuel.Engine;

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

    /// <summary>
    /// The recorded Elo of an Agent spec on the scale of games played with <paramref name="expansions"/>: the base
    /// scale above for none, the Pantheon scale for Pantheon (spec-pantheon P-A6). The two are never compared.
    /// </summary>
    public static double? Elo(string spec, Expansions expansions) => expansions switch
    {
        Expansions.None => Elo(spec),
        Expansions.Pantheon => PantheonRatings.TryGetValue(spec, out var elo) ? elo : null,
        _ => null,
    };

    /// <summary>The Pantheon Anchor's Agent spec: the same chance-node MCTS at 1,000 simulations, in Pantheon games.</summary>
    public const string PantheonAnchor = "mcts";

    /// <summary>The Pantheon Anchor's fixed Elo on the Pantheon scale (Random = 0 in Pantheon games).</summary>
    public const double PantheonAnchorElo = 1491;

    /// <summary>
    /// The Pantheon PIMC rung: default PIMC (50 samples) only ties the Heuristic in Pantheon games, so the rung takes 4x
    /// the samples (Pantheon ticket 08).
    /// </summary>
    public const string PantheonPimc = "pimc:samples=200";

    /// <summary>
    /// The Pantheon Elo scale (spec-pantheon P-A6), chained like the base one from 400-game Pantheon Matches (no promos)
    /// against the rung below, measured once on the laptop (Pantheon ticket 08) and fixed from then on.
    /// </summary>
    static readonly Dictionary<string, double> PantheonRatings = new()
    {
        ["random"] = 0,
        // 3,997-3 in 4,000 games against Random.
        ["heuristic"] = 1250,
        ["heuristic:balanced"] = 1250,
        // -7 against heuristic:balanced (49.0 %, CI 44.1-53.9 %): not a rung, but rated.
        ["pimc"] = 1243,
        // +133 against heuristic:balanced (68.2 %, CI 63.5-72.6 %).
        [PantheonPimc] = 1383,
        // +108 against pimc:samples=200 (65.0 %, CI 60.2-69.5 %).
        [PantheonAnchor] = PantheonAnchorElo,
    };
}
