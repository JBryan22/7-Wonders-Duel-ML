using SevenWondersDuel.Agents;
using SevenWondersDuel.Engine;

const string Usage = """
    usage: 7wd <command>
      replay <log-file>      replay a Move Log and print the final Game State
      random [--seed N]      play one random game and print its Move Log
    """;

try
{
    return args switch
    {
        ["replay", var path] => Replay(path),
        ["random"] => Random(1),
        ["random", "--seed", var seed] when ulong.TryParse(seed, out var s) => Random(s),
        _ => Fail(Usage),
    };
}
catch (Exception e) when (e is FormatException or IOException or UnauthorizedAccessException)
{
    return Fail(e.Message);
}

static int Replay(string path)
{
    var game = MoveLog.Parse(File.ReadAllText(path));
    Console.Write(StatePrinter.Describe(game.State));
    return 0;
}

static int Random(ulong seed)
{
    Console.Write(GameRunner.PlayRandom(seed).WriteLog());
    return 0;
}

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 1;
}
