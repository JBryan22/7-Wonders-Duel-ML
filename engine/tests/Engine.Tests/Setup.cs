namespace SevenWondersDuel.Engine.Tests;

/// <summary>Shared Move Log fragments for tests that start a game from text.</summary>
internal static class Setup
{
    /// <summary>
    /// A complete Wonder draft (A-B-B-A, round 2 started by the second player) in which the first player ends with
    /// the rulebook's first-game P1 Wonders (pyramids, great-lighthouse, temple-of-artemis, statue-of-zeus) and the
    /// second player with circus-maximus, piraeus, appian-way and colossus.
    /// </summary>
    internal static string[] Draft(int firstPlayer = 0) => Draft(firstPlayer,
        ["pyramids", "great-lighthouse", "temple-of-artemis", "statue-of-zeus"],
        ["circus-maximus", "piraeus", "appian-way", "colossus"]);

    /// <summary>
    /// A complete draft giving the first player <paramref name="first"/> and the second player <paramref name="second"/>
    /// (4 each). Round 1 offers the first two of each and is picked A-B-B-A; round 2 offers the rest, B first.
    /// </summary>
    internal static string[] Draft(int firstPlayer, string[] first, string[] second)
    {
        var a = $"P{firstPlayer + 1}";
        var b = $"P{2 - firstPlayer}";
        return
        [
            $"reveal draft {first[0]} {first[1]} {second[0]} {second[1]}",
            $"{a} draft {first[0]}",
            $"{b} draft {second[0]}",
            $"{b} draft {second[1]}",
            $"{a} draft {first[1]}",
            $"reveal draft {first[2]} {first[3]} {second[2]} {second[3]}",
            $"{b} draft {second[2]}",
            $"{a} draft {first[2]}",
            $"{a} draft {first[3]}",
            $"{b} draft {second[3]}",
        ];
    }

    static readonly string[] BaseWonders = Data.GameData.Wonders.Where(w => !w.IsPromo).Select(w => w.Id).ToArray();

    /// <summary>A draft (P1 first) giving P1 <paramref name="p1"/> plus fillers and P2 the next base Wonders, so 8 differ.</summary>
    internal static string[] DraftFor(params string[] p1)
    {
        var rest = BaseWonders.Except(p1).ToArray();
        var mine = p1.Concat(rest).Take(4).ToArray();
        var theirs = rest.Except(mine).Take(4).ToArray();
        return Draft(0, mine, theirs);
    }

    /// <summary>Applies <see cref="Draft"/> to a game waiting for its first draft offer.</summary>
    internal static void ApplyDraft(Game game)
    {
        foreach (var line in Draft(game.State.FirstPlayer)) game.Apply(MoveLog.ParseEntry(line, game.State));
    }
}
