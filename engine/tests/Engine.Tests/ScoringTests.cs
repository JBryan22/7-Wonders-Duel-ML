using SevenWondersDuel.Engine.Data;

namespace SevenWondersDuel.Engine.Tests;

/// <summary>Hand-traced civilian scores (rules-reference §10) and the blue-VP tiebreak (mistake 20).</summary>
public class ScoringTests
{
    static GameState Position(string[] p1, int coins1, string[] p2, int coins2, int pawn = 0)
    {
        var s = GameState.New(Ruleset.Default);
        foreach (var c in p1) s.AddToCity(0, GameData.CardIndex(c));
        foreach (var c in p2) s.AddToCity(1, GameData.CardIndex(c));
        s.SetCoins(0, coins1);
        s.SetCoins(1, coins2);
        s.SetConflictPawn(pawn);
        return s;
    }

    static readonly string[] P1City =
        ["theater", "palace", "workshop", "scriptorium", "lighthouse", "magistrates-guild", "moneylenders-guild"];
    static readonly string[] P2City =
        ["altar", "temple", "pantheon", "aqueduct", "academy", "scientists-guild", "lumber-yard"];

    [Fact]
    public void A_hand_traced_civilian_game()
    {
        var r = Scoring.Civilian(Position(P1City, 17, P2City, 8));

        // P1: blue 3 + 7; green Workshop 1 (Scriptorium has no VP); Lighthouse 3;
        // Magistrates 1 per blue in the city with most blue (P2: 4) + Moneylenders 1 per 3 coins of the richest (17 -> 5);
        // coins 17 / 3 = 5.
        Assert.Equal(10, r.P1[ScoreCategory.Blue]);
        Assert.Equal(1, r.P1[ScoreCategory.Green]);
        Assert.Equal(3, r.P1[ScoreCategory.Yellow]);
        Assert.Equal(9, r.P1[ScoreCategory.Guilds]);
        Assert.Equal(5, r.P1[ScoreCategory.Coins]);
        Assert.Equal(0, r.P1[ScoreCategory.Military]);
        Assert.Equal(28, r.P1.Total);

        // P2: blue 3 + 4 + 6 + 5; green Academy 3; Scientists 1 per green in the city with most green (2, P1); coins 8 / 3 = 2.
        Assert.Equal(18, r.P2[ScoreCategory.Blue]);
        Assert.Equal(3, r.P2[ScoreCategory.Green]);
        Assert.Equal(2, r.P2[ScoreCategory.Guilds]);
        Assert.Equal(2, r.P2[ScoreCategory.Coins]);
        Assert.Equal(25, r.P2.Total);

        Assert.Equal(0, r.Winner);
        Assert.Equal(VictoryType.Civilian, r.Victory);
        Assert.False(r.IsDraw);
    }

    [Fact]
    public void Military_points_turn_the_same_game_around()
    {
        // Pawn 4 spaces toward P1's capital: P2 scores 5 military VP, P1 none. 28 vs 30.
        var r = Scoring.Civilian(Position(P1City, 17, P2City, 8, pawn: -4));
        Assert.Equal(0, r.P1[ScoreCategory.Military]);
        Assert.Equal(5, r.P2[ScoreCategory.Military]);
        Assert.Equal(30, r.P2.Total);
        Assert.Equal(1, r.Winner);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 2)]
    [InlineData(2, 2)]
    [InlineData(3, 5)]
    [InlineData(5, 5)]
    [InlineData(6, 10)]
    [InlineData(8, 10)]
    [InlineData(-3, 0)]
    public void Military_points_by_zone(int lead, int points) => Assert.Equal(points, Scoring.MilitaryPoints(lead));

    [Fact]
    public void Shipowners_and_tacticians_count_at_game_end()
    {
        // Shipowners: brown + grey in one city (P1: 1 + 2 = 3; P2: 2 + 0). Tacticians: red, P2's 2.
        var r = Scoring.Civilian(Position(
            ["shipowners-guild", "tacticians-guild", "lumber-yard", "glassworks", "press", "garrison"], 0,
            ["quarry", "clay-pool", "stable", "palisade"], 0));
        Assert.Equal(3 + 2, r.P1[ScoreCategory.Guilds]);
    }

    [Fact]
    public void Builders_guild_scores_nothing_without_wonders()
    {
        // Two Wonder VP per Wonder in the city with the most; Wonders arrive in ticket 07.
        var r = Scoring.Civilian(Position(["builders-guild"], 0, [], 0));
        Assert.Equal(0, r.P1[ScoreCategory.Guilds]);
    }

    [Fact]
    public void Equal_vp_goes_to_the_most_blue_vp()
    {
        // P1: Theater 3 blue, 0 coins = 3. P2: Workshop 1 + Apothecary 1 green, 3 coins = 1 -> 3.
        var r = Scoring.Civilian(Position(["theater"], 0, ["workshop", "apothecary"], 3));
        Assert.Equal(r.P1.Total, r.P2.Total);
        Assert.Equal(0, r.Winner);
        Assert.Equal(VictoryType.Civilian, r.Victory);
    }

    [Fact]
    public void Equal_vp_and_equal_blue_vp_is_a_draw()
    {
        // P1: Theater 3 + 7 coins (2) = 5. P2: Altar 3 + Workshop 1 + 3 coins (1) = 5. Blue 3 each.
        var r = Scoring.Civilian(Position(["theater"], 7, ["altar", "workshop"], 3));
        Assert.Equal(5, r.P1.Total);
        Assert.Equal(5, r.P2.Total);
        Assert.True(r.IsDraw);
        Assert.Equal(-1, r.Winner);
        Assert.StartsWith("Draw", r.ToString());
    }

    [Fact]
    public void Equal_blue_vp_with_unequal_totals_is_not_a_draw()
    {
        var r = Scoring.Civilian(Position(["theater"], 9, ["altar"], 0));
        Assert.Equal(0, r.Winner);
    }
}
