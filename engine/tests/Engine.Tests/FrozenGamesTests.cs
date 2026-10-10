namespace SevenWondersDuel.Engine.Tests;

/// <summary>
/// Scripted games (spec A11 item 2): full random games whose result was worked out independently of our Engine, by
/// indjev99's C++ engine during the cross-check (ticket 11, `7wd crosscheck --freeze`). Each Move Log carries the C++
/// verdict as a comment, "# expect P1 civilian 72 65" (winner or "draw", victory type, P1 and P2 VP). Replaying the log
/// must reach that result; a rules change that breaks one is either a bug or must be checked against the rules and the
/// cross-check rerun.
/// </summary>
public class FrozenGamesTests
{
    static string Dir => Path.Combine(AppContext.BaseDirectory, "FrozenGames");

    public static TheoryData<string> Files()
    {
        var data = new TheoryData<string>();
        foreach (var f in Directory.GetFiles(Dir, "*.log").Order()) data.Add(Path.GetFileName(f));
        return data;
    }

    static (int Winner, VictoryType Victory, int P1, int P2) Expected(string text)
    {
        var line = text.Split('\n').Single(l => l.StartsWith("# expect ", StringComparison.Ordinal));
        var t = line.Trim().Split(' ');
        var winner = t[2] switch { "P1" => 0, "P2" => 1, "draw" => -1, _ => throw new FormatException(line) };
        return (winner, Enum.Parse<VictoryType>(t[3], ignoreCase: true), int.Parse(t[4]), int.Parse(t[5]));
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void Frozen_game_replays_to_the_cpp_engines_result(string file)
    {
        var text = File.ReadAllText(Path.Combine(Dir, file));
        var expected = Expected(text);
        var result = MoveLog.Parse(text).State.Result;
        Assert.NotNull(result);
        Assert.Equal(expected.Winner, result.Winner);
        Assert.Equal(expected.Victory, result.Victory);
        Assert.Equal((expected.P1, expected.P2), (result.P1.Total, result.P2.Total));
    }

    [Fact]
    public void Frozen_games_cover_military_science_and_civilian_wins_and_a_draw()
    {
        var results = Directory.GetFiles(Dir, "*.log").Select(f => Expected(File.ReadAllText(f))).ToList();
        Assert.InRange(results.Count, 8, 20);
        Assert.Contains(results, r => r.Winner >= 0 && r.Victory == VictoryType.Military);
        Assert.Contains(results, r => r.Winner >= 0 && r.Victory == VictoryType.Science);
        Assert.Contains(results, r => r.Winner >= 0 && r.Victory == VictoryType.Civilian);
        Assert.Contains(results, r => r.Winner < 0);
    }
}
