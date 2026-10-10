using System.Text;
using SevenWondersDuel.Engine;

namespace SevenWondersDuel.Agents;

/// <summary>A win rate (draws count ½) with its 95% confidence interval, on a 0..1 scale.</summary>
public readonly record struct WinRate(double Score, double Low, double High)
{
    /// <summary>
    /// The Wilson score interval at 95% for <paramref name="points"/> out of <paramref name="games"/>. Wilson stays
    /// inside 0..1 and is sensible at 0% and 100%, unlike the plain ±1.96·SE interval. It treats the games as
    /// independent, which is slightly conservative for seat-swapped pairs (the swap cancels the seat advantage).
    /// </summary>
    public static WinRate Wilson(double points, int games)
    {
        if (games == 0) return new(0.5, 0, 1);
        const double z = 1.959964;
        var p = points / games;
        var z2n = z * z / games;
        var centre = (p + z2n / 2) / (1 + z2n);
        var half = z * Math.Sqrt(p * (1 - p) / games + z2n / (4 * games)) / (1 + z2n);
        return new(p, Math.Max(0, centre - half), Math.Min(1, centre + half));
    }

    public bool Contains(double rate) => Low <= rate && rate <= High;
}

/// <summary>
/// The result of a Match so far (spec A5): A's win rate ± 95% CI, the Elo difference, A's and B's Elo on the shared
/// scale with Random = 0 where one side's rating is known, the victory-type breakdown, and wall-clock. Everything is
/// computed from the finished seed pairs, so a resumed Match reports as if it had never stopped (except
/// <see cref="SessionSeconds"/>, this session's wall-clock).
/// </summary>
public sealed record MatchReport(MatchSettings Settings, IReadOnlyList<MatchPair> Pairs, double SessionSeconds, bool Stopped)
{
    /// <summary>Elo points per factor of 10 in the odds: 200 points ≈ 76% expected score.</summary>
    public const double EloScale = 400;

    public IEnumerable<MatchGame> Games => Pairs.SelectMany(p => p.Games);
    public int GameCount => Pairs.Count * 2;
    public bool Complete => Pairs.Count == Settings.Pairs;

    public int WinsA => Games.Count(g => g.Winner == MatchWinner.A);
    public int WinsB => Games.Count(g => g.Winner == MatchWinner.B);
    public int Draws => Games.Count(g => g.Winner == MatchWinner.Draw);

    /// <summary>A's score rate with its 95% CI. The pass rule: <c>WinRate.Low &gt; 0.5</c>.</summary>
    public WinRate WinRate => WinRate.Wilson(Games.Sum(g => g.ScoreA), GameCount);

    /// <summary>A passes B (the Agent Ladder's pass rule): the lower 95% bound of A's win rate is above 50%.</summary>
    public bool APasses => WinRate.Low > 0.5;

    /// <summary>
    /// Elo(A) − Elo(B) with its 95% CI, from the win rate and its bounds. A rate of 0% or 100% has no finite Elo, so
    /// rates are kept at least half a game away from either end.
    /// </summary>
    public (double Diff, double Low, double High) EloDifference
    {
        get
        {
            var r = WinRate;
            return (Elo(r.Score, GameCount), Elo(r.Low, GameCount), Elo(r.High, GameCount));
        }
    }

    public static double Elo(double rate, int games)
    {
        var edge = 0.5 / Math.Max(1, games);
        rate = Math.Clamp(rate, edge, 1 - edge);
        return EloScale * Math.Log10(rate / (1 - rate));
    }

    /// <summary>An Agent's known rating: given in the settings, or 0 for Random (the scale's zero).</summary>
    static double? Known(string spec, double? given) => given ?? (spec == "random" ? 0 : null);

    /// <summary>A's Elo on the shared scale (Random = 0), when B's rating is known; else null.</summary>
    public double? EloA => Known(Settings.AgentA, Settings.EloA) ??
        (Known(Settings.AgentB, Settings.EloB) is { } b ? b + EloDifference.Diff : null);

    /// <summary>B's Elo on the shared scale (Random = 0), when A's rating is known; else null.</summary>
    public double? EloB => Known(Settings.AgentB, Settings.EloB) ??
        (Known(Settings.AgentA, Settings.EloA) is { } a ? a - EloDifference.Diff : null);

    /// <summary>How many games each side won by each victory type.</summary>
    public int Wins(MatchWinner side, VictoryType victory) => Games.Count(g => g.Winner == side && g.Victory == victory);

    /// <summary>How many games A won from each seat (0 = P1): the seat swap gives both seats the same number of games.</summary>
    public int WinsAAsSeat(int seat) => Games.Count(g => g.Winner == MatchWinner.A && g.SeatA == seat);

    /// <summary>Total wall-clock spent playing the finished games (summed over games, so over all threads and sessions).</summary>
    public double GameSeconds => Games.Sum(g => g.Seconds);

    /// <summary>Average wall-clock per Move for A or B, in milliseconds.</summary>
    public double MillisecondsPerMove(MatchWinner side)
    {
        var games = Games.ToList();
        var (moves, seconds) = side == MatchWinner.A
            ? (games.Sum(g => g.MovesA), games.Sum(g => g.SecondsA))
            : (games.Sum(g => g.MovesB), games.Sum(g => g.SecondsB));
        return moves == 0 ? 0 : seconds * 1000 / moves;
    }

    /// <summary>The two sides as printed: the specs, marked A and B when both sides run the same spec.</summary>
    string A => Settings.AgentA == Settings.AgentB ? $"{Settings.AgentA} (A)" : Settings.AgentA;
    string B => Settings.AgentA == Settings.AgentB ? $"{Settings.AgentB} (B)" : Settings.AgentB;

    public override string ToString()
    {
        var r = WinRate;
        var (diff, low, high) = EloDifference;
        var sb = new StringBuilder();
        sb.AppendLine($"{A} vs {B}: {GameCount} games in {Pairs.Count} of {Settings.Pairs} seed pairs ({Settings.Rules})"
            + (Complete ? "" : Stopped ? ", STOPPED (resume with the same command)" : ", incomplete"));
        sb.AppendLine($"  {A} scores {r.Score:P1} (95% CI {r.Low:P1} to {r.High:P1}): {WinsA} wins, {WinsB} losses, {Draws} draws"
            + (APasses ? $"; passes {B}" : ""));
        sb.AppendLine($"  Elo {A} - {B}: {diff:+0;-0;0} (95% CI {low:+0;-0;0} to {high:+0;-0;0})");
        if (EloA is { } ea && EloB is { } eb)
            sb.AppendLine($"  Elo (Random = 0): {A} {ea:0}, {B} {eb:0}");
        foreach (var side in new[] { MatchWinner.A, MatchWinner.B })
        {
            var name = side == MatchWinner.A ? A : B;
            sb.AppendLine($"  {name} wins: " + string.Join(", ", Enum.GetValues<VictoryType>()
                .Select(v => $"{Wins(side, v)} {v.ToString().ToLowerInvariant()}")));
        }
        sb.AppendLine($"  {A} wins as P1 {WinsAAsSeat(0)}, as P2 {WinsAAsSeat(1)}");
        sb.Append($"  wall-clock: {SessionSeconds:N1} s this session, {GameSeconds:N1} s of games; "
            + $"{MillisecondsPerMove(MatchWinner.A):N3} ms per Move for {A}, {MillisecondsPerMove(MatchWinner.B):N3} ms for {B}");
        return sb.ToString();
    }

    /// <summary>The figures a training loop reads (written as <c>summary.json</c>).</summary>
    public object Summary()
    {
        var r = WinRate;
        var (diff, low, high) = EloDifference;
        return new
        {
            Match.FormatVersion,
            A = Settings.AgentA,
            B = Settings.AgentB,
            Rules = Settings.Rules.ToString(),
            Settings.FirstSeed,
            PairsPlanned = Settings.Pairs,
            PairsFinished = Pairs.Count,
            Complete,
            Stopped,
            Games = GameCount,
            WinsA,
            WinsB,
            Draws,
            WinRate = r.Score,
            WinRateLow = r.Low,
            WinRateHigh = r.High,
            APasses,
            EloDiff = diff,
            EloDiffLow = low,
            EloDiffHigh = high,
            EloA,
            EloB,
            VictoriesA = Enum.GetValues<VictoryType>().ToDictionary(v => v.ToString().ToLowerInvariant(), v => Wins(MatchWinner.A, v)),
            VictoriesB = Enum.GetValues<VictoryType>().ToDictionary(v => v.ToString().ToLowerInvariant(), v => Wins(MatchWinner.B, v)),
            SessionSeconds,
            GameSeconds,
            MsPerMoveA = MillisecondsPerMove(MatchWinner.A),
            MsPerMoveB = MillisecondsPerMove(MatchWinner.B),
        };
    }
}
