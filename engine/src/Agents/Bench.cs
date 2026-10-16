using System.Diagnostics;
using SevenWondersDuel.Engine;

namespace SevenWondersDuel.Agents;

/// <summary>One <see cref="Bench"/> measurement: how many random games and decisions ran in how long.</summary>
public sealed record BenchResult(string Name, int Games, long Decisions, double Seconds)
{
    public double GamesPerSecond => Games / Seconds;
    public double DecisionsPerSecond => Decisions / Seconds;
    /// <summary>Average wall time per decision, in microseconds.</summary>
    public double MicrosecondsPerDecision => Seconds * 1e6 / Decisions;

    public override string ToString() =>
        $"{Name,-34} {GamesPerSecond,9:N0} games/s {DecisionsPerSecond,12:N0} decisions/s {MicrosecondsPerDecision,7:N2} us/decision ({Games:N0} games in {Seconds:N2} s)";
}

/// <summary>
/// Random-play speed on the calling thread (spec A11, speed: the target is 10k random games/s on one core). Three
/// variants separate the costs: the full <see cref="GameRunner"/> (Move Log and an Observation per decision), the
/// bare Game State with an Observation per decision (what an Agent sees), and the bare Game State choosing from its own
/// legal Moves (the rules alone). A fourth line plays Pantheon games the bare way (spec-pantheon P-A10), so a Pantheon
/// slowdown shows apart from the base numbers. Each runs seeded games back to back for about <c>duration</c>, after a short warm-up.
/// </summary>
public static class Bench
{
    public static IReadOnlyList<BenchResult> Run(TimeSpan duration, ulong seed = 1)
    {
        var variants = new (string Name, Func<ulong, long> Play)[]
        {
            ("GameRunner (log + Observe)", PlayLogged),
            ("Game State + Observe", s => PlayBare(s, observe: true)),
            ("Game State only", s => PlayBare(s, observe: false)),
            ("Game State only (Pantheon)", s => PlayBare(s, observe: false, Pantheon)),
        };
        var results = new List<BenchResult>();
        foreach (var (name, play) in variants)
        {
            // Warm-up: let the JIT reach its optimised tier before timing.
            Measure(name, play, TimeSpan.FromMilliseconds(Math.Min(500, duration.TotalMilliseconds / 2)), seed);
            results.Add(Measure(name, play, duration, seed));
        }
        return results;
    }

    static BenchResult Measure(string name, Func<ulong, long> play, TimeSpan duration, ulong seed)
    {
        var games = 0;
        var decisions = 0L;
        var clock = Stopwatch.StartNew();
        // Check the clock every 64 games, so the timer is not part of what is measured.
        while (clock.Elapsed < duration)
            for (var i = 0; i < 64; i++, games++)
                decisions += play(seed + (ulong)games);
        return new BenchResult(name, games, decisions, clock.Elapsed.TotalSeconds);
    }

    static long PlayLogged(ulong seed)
    {
        var game = GameRunner.PlayRandom(seed);
        return game.Log.Count(e => e is MoveEntry);
    }

    static readonly Ruleset Pantheon = new(Expansions: Expansions.Pantheon);

    static long PlayBare(ulong seed, bool observe, Ruleset? rules = null)
    {
        var seeds = new Rng(seed);
        var chance = new RandomChanceSource(seeds.NextUInt64());
        var rng = new[] { new Rng(seeds.NextUInt64()), new Rng(seeds.NextUInt64()) };
        Span<Move> moves = stackalloc Move[GameState.MaxMoves];
        var s = GameState.New(rules ?? Ruleset.Default);
        var decisions = 0L;
        while (true)
        {
            switch (s.Step)
            {
                case StepKind.GameOver:
                    return decisions;
                case StepKind.Reveal:
                    s.Apply(chance.Next(s));
                    break;
                default:
                    var player = s.PlayerToDecide;
                    var n = observe ? s.Observe(player).LegalMoves(moves) : s.LegalMoves(moves);
                    s.Apply(moves[rng[player].Next(n)]);
                    decisions++;
                    break;
            }
        }
    }
}
