using System.Globalization;
using SevenWondersDuel.Engine;

namespace SevenWondersDuel.Agents;

/// <summary>
/// PIMC's settings (spec A5, Agent Ladder): <see cref="Samples"/> worlds from the Determinizer, and in each world
/// <see cref="Playouts"/> random playouts per legal Move (the spec's 50 × 1 by default). A Move is ranked by its win
/// rate plus <see cref="Margin"/> times its mean final VP margin: with only 50 binary results per Move the win rate is
/// noisy, and the VP margin of the same playouts is a much steadier signal. 0 ranks by wins alone (margin only breaks
/// ties). 0.02 (one VP worth 2 % of win rate) measured best: 64 % against the adaptive balanced Heuristic, against
/// 56 % for wins alone and 55 % for the margin alone.
/// </summary>
public sealed record PimcSettings(int Samples = 50, int Playouts = 1, double Margin = 0.02)
{
    public static readonly PimcSettings Default = new();

    /// <summary>
    /// Parses the argument of a <c>pimc</c> Agent spec: empty for the default, or overrides such as
    /// <c>samples=100,playouts=2,margin=0</c>.
    /// </summary>
    public static PimcSettings Parse(string? argument)
    {
        var settings = Default;
        if (string.IsNullOrEmpty(argument)) return settings;
        foreach (var part in argument.Split(','))
        {
            var eq = part.IndexOf('=');
            if (eq < 0 || !double.TryParse(part[(eq + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || v < 0)
                throw new FormatException($"Bad PIMC setting '{part}' (expected name=number)");
            var name = part[..eq].ToLowerInvariant();
            if (name is "samples" or "playouts" && (v < 1 || v != Math.Floor(v) || v > 1_000_000))
                throw new FormatException($"Bad PIMC setting '{part}' ({name} is a positive whole number)");
            settings = name switch
            {
                "samples" => settings with { Samples = (int)v },
                "playouts" => settings with { Playouts = (int)v },
                "margin" => settings with { Margin = v },
                _ => throw new FormatException($"Unknown PIMC setting '{part[..eq]}' (samples, playouts, margin)"),
            };
        }
        return settings;
    }
}

/// <summary>
/// One Move's PIMC statistics: playouts run from it, the deciding player's score (win 1, draw ½), and the mean final
/// VP margin (mine minus the opponent's, at the end of each playout; the tie-break between equal win counts).
/// </summary>
public readonly record struct PimcMoveStat(Move Move, int Playouts, double Score, double Margin)
{
    /// <summary>The deciding player's mean result after this Move (0 = always lost, 1 = always won).</summary>
    public double Value => Playouts == 0 ? 0 : Score / Playouts;

    /// <summary>The standard error of <see cref="Value"/> (a binomial estimate, draws counted as half).</summary>
    public double StdErr => Playouts < 2 ? 0.5 : Math.Sqrt(Value * (1 - Value) / Playouts);
}

/// <summary>What one PIMC search found: per-Move statistics in legal-Move order, and the Move chosen.</summary>
public sealed record PimcResult(IReadOnlyList<PimcMoveStat> Moves, Move Best);

/// <summary>
/// The third rung of the Agent Ladder (spec A5): Perfect-Information Monte Carlo. For each of
/// <see cref="PimcSettings.Samples"/> worlds sampled from the Observation by the <see cref="Determinizer"/>, every legal
/// Move is tried and played out to the end of the game by uniformly random Moves for both players
/// (<see cref="PimcSettings.Playouts"/> times), with every Reveal drawn by the Determinizer. The Move that wins most
/// (its win rate nudged by its mean final VP margin, <see cref="PimcSettings.Margin"/>) is played. Search sees only the
/// Observation.
/// <para>
/// Every Move is tried on common luck: in each world, playout <c>j</c> reseeds the Determinizer and the playout
/// policy with the same seed for every Move, so Moves are compared on the same hidden cards as far as the play allows,
/// which removes much of the sampling noise from the comparison. Worlds are drawn from the Agent's own seed, so a
/// search is deterministic for a fixed seed and Observation. States are reused with <see cref="GameState.CopyFrom"/>.
/// </para>
/// </summary>
public sealed class PimcAgent : IAgent
{
    readonly Rng _rng;
    readonly Determinizer _determinizer = new(0);
    readonly GameState _world = GameState.New(Ruleset.Default);
    readonly GameState _playout = GameState.New(Ruleset.Default);
    readonly Move[] _moves = new Move[GameState.MaxMoves];

    public PimcAgent(PimcSettings settings, ulong seed)
    {
        Settings = settings;
        _rng = new Rng(seed);
    }

    public PimcSettings Settings { get; }

    /// <summary>The last search's result (null before the first search, and after a forced Move).</summary>
    public PimcResult? LastSearch { get; private set; }

    public Move ChooseMove(Observation observation)
    {
        var n = observation.LegalMoves(_moves);
        if (n == 0) throw new InvalidOperationException("No legal Move");
        if (n == 1)
        {
            LastSearch = null;
            return _moves[0];
        }
        return (LastSearch = Search(observation)).Best;
    }

    /// <summary>
    /// Runs the search for the decision at the front of the Observation's queue (whoever decides it: "me" is the
    /// player to decide) and returns every legal Move's statistics. A Move the observer must allow but a sampled world
    /// rules out (an opponent's Great Library pick outside the sampled draw) is not counted in that world.
    /// </summary>
    public PimcResult Search(Observation observation)
    {
        var root = observation.State;
        var n = root.LegalMoves(_moves);
        if (n == 0) throw new InvalidOperationException("No legal Move");
        var me = root.PlayerToDecide;
        var playouts = new int[n];
        var score = new double[n];
        var margin = new long[n];

        for (var sample = 0; sample < Settings.Samples; sample++)
        {
            var worldSeed = _rng.NextUInt64();
            _determinizer.Reseed(worldSeed);
            _determinizer.Sample(observation, _world);
            for (var j = 0; j < Settings.Playouts; j++)
            {
                var playoutSeed = worldSeed + 0x9E3779B97F4A7C15UL * (ulong)(j + 1);
                for (var m = 0; m < n; m++)
                {
                    if (!_world.IsLegal(_moves[m])) continue;
                    _playout.CopyFrom(_world);
                    _playout.Apply(_moves[m]);
                    _determinizer.Reseed(playoutSeed);
                    score[m] += Playout(_playout, me, new Rng(playoutSeed ^ 0xD1B54A32D192ED03UL));
                    margin[m] += Scoring.Total(_playout, me) - Scoring.Total(_playout, 1 - me);
                    playouts[m]++;
                }
            }
        }

        // The Move that wins most, nudged by its mean VP margin (PimcSettings.Margin); exact ties go to the larger margin,
        // then a seeded coin.
        var stats = new PimcMoveStat[n];
        var best = -1;
        (double Value, double Margin, int Coin) bestKey = (double.NegativeInfinity, 0, 0);
        for (var m = 0; m < n; m++)
        {
            stats[m] = new PimcMoveStat(_moves[m], playouts[m], score[m], playouts[m] == 0 ? 0 : (double)margin[m] / playouts[m]);
            if (playouts[m] == 0) continue;
            var key = (stats[m].Value + Settings.Margin * stats[m].Margin, stats[m].Margin, _rng.Next(1 << 20));
            if (best < 0 || key.CompareTo(bestKey) > 0) (best, bestKey) = (m, key);
        }
        return new PimcResult(stats, _moves[best < 0 ? 0 : best]);
    }

    /// <summary>Plays <paramref name="s"/> to the end with uniformly random Moves and the Determinizer's Reveals; returns <paramref name="me"/>'s result.</summary>
    double Playout(GameState s, int me, Rng policy)
    {
        Span<Move> moves = stackalloc Move[GameState.MaxMoves];
        while (!s.IsOver)
        {
            if (s.Step == StepKind.Reveal)
            {
                s.Apply(_determinizer.Next(s));
                continue;
            }
            var n = s.LegalMoves(moves);
            s.Apply(moves[policy.Next(n)]);
        }
        var result = s.Result!;
        return result.IsDraw ? 0.5 : result.Winner == me ? 1 : 0;
    }
}
