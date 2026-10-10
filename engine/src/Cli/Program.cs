using SevenWondersDuel.Agents;
using SevenWondersDuel.Engine;

const string Usage = """
    usage: 7wd <command>
      replay <log-file>                replay a Move Log and print the final Game State
      random [--seed N]                play one random game and print its Move Log
      bench [--seconds S]              random games/s on one thread (default 5 s per variant)
      soak [--games N] [--seed S]      check the random-play invariants over N games (default 100000) from seed S
    """;

try
{
    return args switch
    {
        ["replay", var path] => Replay(path),
        ["random"] => Random(1),
        ["random", "--seed", var seed] when ulong.TryParse(seed, out var s) => Random(s),
        ["bench"] => RunBench(5),
        ["bench", "--seconds", var secs] when double.TryParse(secs, out var d) && d > 0 => RunBench(d),
        ["soak"] => RunSoak(100_000, 0),
        ["soak", "--games", var n] when int.TryParse(n, out var g) && g > 0 => RunSoak(g, 0),
        ["soak", "--games", var n, "--seed", var seed] when int.TryParse(n, out var g) && g > 0 && ulong.TryParse(seed, out var s) => RunSoak(g, s),
        ["soak", "--seed", var seed] when ulong.TryParse(seed, out var s) => RunSoak(100_000, s),
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

static int RunBench(double seconds)
{
    Console.WriteLine($"7wd bench: random games on one thread, {seconds:0.#} s per variant ({Environment.OSVersion}, .NET {Environment.Version})");
    foreach (var result in Bench.Run(TimeSpan.FromSeconds(seconds)))
        Console.WriteLine(result);
    return 0;
}

static int RunSoak(int games, ulong firstSeed)
{
    Console.WriteLine($"7wd soak: {games:N0} random games from seed {firstSeed}, on {Environment.ProcessorCount} threads");
    var report = Soak.Run(games, firstSeed);
    Console.WriteLine(report);
    foreach (var failure in report.Failures.Take(20)) Console.WriteLine("  " + failure);
    return report.Passed ? 0 : 1;
}

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 1;
}
