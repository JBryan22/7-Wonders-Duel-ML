using SevenWondersDuel.Engine.Data;

namespace SevenWondersDuel.Engine;

/// <summary>How a game was won. Growable (spec A4: victory types come from data later).</summary>
public enum VictoryType : byte { Civilian, Military, Science }

/// <summary>The civilian score categories (rules-reference §10), in display order. Growable.</summary>
public enum ScoreCategory : byte { Blue, Green, Yellow, Guilds, Wonders, Progress, Coins, Military }

/// <summary>One player's victory points by category.</summary>
public sealed class ScoreSheet : IEquatable<ScoreSheet>
{
    public static readonly int CategoryCount = Enum.GetValues<ScoreCategory>().Length;

    readonly int[] _points;

    public ScoreSheet(int[] points)
    {
        if (points.Length != CategoryCount) throw new ArgumentException($"Expected {CategoryCount} categories", nameof(points));
        _points = (int[])points.Clone();
    }

    public int this[ScoreCategory c] => _points[(int)c];
    public int Total => _points.Sum();

    public bool Equals(ScoreSheet? other) => other is not null && _points.AsSpan().SequenceEqual(other._points);
    public override bool Equals(object? obj) => Equals(obj as ScoreSheet);
    public override int GetHashCode() => _points.Aggregate(17, (h, p) => h * 31 + p);

    public override string ToString() =>
        $"{Total} VP (" + string.Join(", ", Enum.GetValues<ScoreCategory>()
            .Where(c => this[c] != 0).Select(c => $"{c.ToString().ToLowerInvariant()} {this[c]}")) + ")";
}

/// <summary>
/// The end of a game (spec A4): the winner (or -1 for a draw), the victory type and both players' scores.
/// Scores are filled for every victory type, so a supremacy win still shows the civilian position it ended.
/// </summary>
public sealed record Result(int Winner, VictoryType Victory, ScoreSheet P1, ScoreSheet P2)
{
    public bool IsDraw => Winner < 0;
    public ScoreSheet Score(int player) => player == 0 ? P1 : P2;

    public override string ToString() =>
        (IsDraw ? "Draw" : $"P{Winner + 1} wins ({Victory.ToString().ToLowerInvariant()})")
        + $" | P1 {P1} | P2 {P2}";
}

/// <summary>Civilian scoring at the end of Age III (rules-reference §10) and the Guilds' coin and VP counts (§5).</summary>
public static class Scoring
{
    /// <summary>
    /// The civilian Result: highest total wins; on equal totals the most blue-card VP wins; equal on both is a draw
    /// (rules-reference §10, mistake 20).
    /// </summary>
    public static Result Civilian(GameState s)
    {
        var p1 = Score(s, 0);
        var p2 = Score(s, 1);
        var winner = p1.Total != p2.Total ? (p1.Total > p2.Total ? 0 : 1)
            : p1[ScoreCategory.Blue] != p2[ScoreCategory.Blue] ? (p1[ScoreCategory.Blue] > p2[ScoreCategory.Blue] ? 0 : 1)
            : -1;
        return new Result(winner, VictoryType.Civilian, p1, p2);
    }

    /// <summary>
    /// An immediate win by military or science supremacy (rules-reference §10). Both score sheets show the position
    /// as it stands, for reporting only.
    /// </summary>
    public static Result Supremacy(GameState s, int winner, VictoryType victory) =>
        new(winner, victory, Score(s, 0), Score(s, 1));

    /// <summary>One player's victory points by category, as if the game ended now.</summary>
    public static ScoreSheet Score(GameState s, int player)
    {
        var points = new int[ScoreSheet.CategoryCount];
        foreach (var card in s.City(player).Cards())
        {
            var def = GameData.Cards[card];
            switch (def.Color)
            {
                case CardColor.Blue: points[(int)ScoreCategory.Blue] += def.VictoryPoints; break;
                case CardColor.Green: points[(int)ScoreCategory.Green] += def.VictoryPoints; break;
                case CardColor.Yellow: points[(int)ScoreCategory.Yellow] += def.VictoryPoints; break;
                case CardColor.Purple: points[(int)ScoreCategory.Guilds] += GuildPoints(s, def.Guild); break;
            }
        }
        foreach (var w in GameState.WondersIn(s.BuiltWonders(player)))
            points[(int)ScoreCategory.Wonders] += GameData.Wonders[w].VictoryPoints;
        points[(int)ScoreCategory.Progress] = ProgressPoints(s, player);
        points[(int)ScoreCategory.Coins] = s.Coins(player) / 3;
        points[(int)ScoreCategory.Military] = MilitaryPoints(s.MilitaryLead(player));
        return new ScoreSheet(points);
    }

    /// <summary>
    /// Progress token VP (rules-reference §6): printed VP (Agriculture 4, Philosophy 7) plus Mathematics' 3 VP per
    /// token owned, itself included.
    /// </summary>
    public static int ProgressPoints(GameState s, int player)
    {
        var points = 0;
        foreach (var t in ProgressTokens.In(s.Tokens(player))) points += GameData.Tokens[t].VictoryPoints;
        if (s.HasToken(player, ProgressTokens.Mathematics))
            points += ProgressTokens.MathematicsPointsPerToken * s.TokenCount(player);
        return points;
    }

    /// <summary>Military VP for a pawn this many spaces toward the opponent's capital (rules-reference §8).</summary>
    public static int MilitaryPoints(int lead) => lead switch
    {
        <= 0 => 0,
        <= 2 => 2,
        <= 5 => 5,
        _ => 10,
    };

    /// <summary>Coins a Guild pays its builder once, when built (rules-reference §5, mistake 15).</summary>
    public static int GuildCoins(GameState s, GuildKind guild) => guild switch
    {
        GuildKind.Merchants or GuildKind.Shipowners or GuildKind.Magistrates
            or GuildKind.Scientists or GuildKind.Tacticians => MostCards(s, guild),
        _ => 0, // Builders and Moneylenders pay no coins
    };

    /// <summary>A Guild's end-of-game VP. "Most" compares both cities, independently of who owns the Guild.</summary>
    public static int GuildPoints(GameState s, GuildKind guild) => guild switch
    {
        GuildKind.Builders => 2 * Math.Max(s.WondersBuilt(0), s.WondersBuilt(1)),
        GuildKind.Moneylenders => Math.Max(s.Coins(0), s.Coins(1)) / 3,
        GuildKind.None => 0,
        _ => MostCards(s, guild),
    };

    /// <summary>The card count a Guild counts in the city that has the most of it.</summary>
    static int MostCards(GameState s, GuildKind guild)
    {
        var colors = guild switch
        {
            GuildKind.Merchants => CardSets.OfColor[(int)CardColor.Yellow],
            // One city for brown and grey together.
            GuildKind.Shipowners => CardSets.OfColor[(int)CardColor.Brown] | CardSets.OfColor[(int)CardColor.Grey],
            GuildKind.Magistrates => CardSets.OfColor[(int)CardColor.Blue],
            GuildKind.Scientists => CardSets.OfColor[(int)CardColor.Green],
            GuildKind.Tacticians => CardSets.OfColor[(int)CardColor.Red],
            _ => throw new ArgumentOutOfRangeException(nameof(guild), guild, "Guild does not count cards"),
        };
        return Math.Max((s.City(0) & colors).Count, (s.City(1) & colors).Count);
    }
}
