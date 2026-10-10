using System.Globalization;
using SevenWondersDuel.Agents;
using SevenWondersDuel.Engine;

const string Usage = """
    usage: 7wd <command>
      replay <log-file>                replay a Move Log and print the final Game State
      random [--seed N] [--promo P]    play one random game and print its Move Log (P: messe, liberty or messe,liberty)
      bench [--seconds S]              random games/s on one thread (default 5 s per variant)
      soak [--games N] [--seed S] [--promo P]
                                       check the random-play invariants over N games (default 100000) from seed S,
                                       with the promo Wonders P switched on (e.g. messe,liberty)
      crosscheck [--games N] [--seed S] [--driver PATH] [--freeze DIR]
                                       compare N random games (default 10000) with indjev99's C++ engine; the driver
                                       comes from tools/crosscheck/build.sh (Linux). --freeze writes ~10 agreed games
      match <agent-a> <agent-b> [--pairs N] [--seed S] [--promo P] [--out DIR] [--threads T] [--elo-a X] [--elo-b X]
                                       play N seed pairs (default 200 = 400 games) with seats swapped; report the win
                                       rate with its 95% CI, Elo (Random = 0) and the victory types. With --out, each
                                       finished pair is saved and rerunning the same command resumes. Ctrl+C stops.
                                       Agents: random, heuristic[:science|military|civilian|balanced] (default balanced),
                                       pimc[:samples=50,playouts=1,margin=0.02], mcts[:sims=1000,c=0.7,margin=0.02]
                                       (the Anchor). The Ladder's recorded Elo is used unless --elo-a/--elo-b is given
      manifest [--out PATH]            print the Encoding manifest (spec A6), or write it to PATH; the committed copy is
                                       data/encoding/v1.json (regenerate it with --out after an Encoding change)
    """;

try
{
    return args switch
    {
        ["replay", var path] => Replay(path),
        ["random", .. var options] => Random(options),
        ["bench"] => RunBench(5),
        ["bench", "--seconds", var secs] when double.TryParse(secs, out var d) && d > 0 => RunBench(d),
        ["soak", .. var options] => RunSoak(options),
        ["crosscheck", .. var options] => RunCrossCheck(options),
        ["manifest"] => Manifest(null),
        ["manifest", "--out", var path] => Manifest(path),
        ["match", var a, var b, .. var options] when !a.StartsWith("--") && !b.StartsWith("--") => RunMatch(a, b, options),
        _ => Fail(Usage),
    };
}
catch (Exception e) when (e is FormatException or NotSupportedException or IOException or UnauthorizedAccessException)
{
    return Fail(e.Message);
}

static int Manifest(string? path)
{
    var text = EncodingManifest.Generate();
    if (path is null) Console.Out.Write(text);
    else
    {
        if (Path.GetDirectoryName(Path.GetFullPath(path)) is { } dir) Directory.CreateDirectory(dir);
        File.WriteAllText(path, text);
        Console.WriteLine($"wrote Encoding v{NetworkEncoding.Version} manifest to {path}: {NetworkEncoding.InputSize} inputs, {NetworkEncoding.PolicySize} policy logits");
    }
    return 0;
}

static int Replay(string path)
{
    var game = MoveLog.Parse(File.ReadAllText(path));
    Console.Write(StatePrinter.Describe(game.State));
    return 0;
}

static int Random(string[] options)
{
    ulong seed = 1;
    var promos = Promos.None;
    for (var i = 0; i < options.Length; i += 2)
    {
        var value = i + 1 < options.Length ? options[i + 1] : null;
        switch (options[i])
        {
            case "--seed" when ulong.TryParse(value, out var s): seed = s; break;
            case "--promo" when value is not null: promos = ParsePromos(value); break;
            default: return Fail(Usage);
        }
    }
    Console.Write(GameRunner.PlayRandom(seed, new Ruleset(Promos: promos)).WriteLog());
    return 0;
}

/// <summary>"messe" or "messe,liberty", as the rules header's promo= list.</summary>
static Promos ParsePromos(string value) => Ruleset.Parse($"rules base promo={value}").Promos;

static int RunBench(double seconds)
{
    Console.WriteLine($"7wd bench: random games on one thread, {seconds:0.#} s per variant ({Environment.OSVersion}, .NET {Environment.Version})");
    foreach (var result in Bench.Run(TimeSpan.FromSeconds(seconds)))
        Console.WriteLine(result);
    return 0;
}

static int RunSoak(string[] options)
{
    var games = 100_000;
    ulong firstSeed = 0;
    var promos = Promos.None;
    for (var i = 0; i < options.Length; i += 2)
    {
        var value = i + 1 < options.Length ? options[i + 1] : null;
        switch (options[i])
        {
            case "--games" when int.TryParse(value, out var g) && g > 0: games = g; break;
            case "--seed" when ulong.TryParse(value, out var s): firstSeed = s; break;
            case "--promo" when value is not null: promos = ParsePromos(value); break;
            default: return Fail(Usage);
        }
    }
    var with = promos == Promos.None ? "" : $" ({new Ruleset(Promos: promos).ToString()["rules base start-choice=new ".Length..]})";
    Console.WriteLine($"7wd soak: {games:N0} random games{with} from seed {firstSeed}, on {Environment.ProcessorCount} threads");
    var report = Soak.Run(games, firstSeed, promos: promos);
    Console.WriteLine(report);
    foreach (var failure in report.Failures.Take(20)) Console.WriteLine("  " + failure);
    return report.Passed ? 0 : 1;
}

static int RunCrossCheck(string[] options)
{
    var games = 10_000;
    ulong seed = 0;
    string? driver = null, freeze = null;
    for (var i = 0; i < options.Length; i += 2)
    {
        var value = i + 1 < options.Length ? options[i + 1] : null;
        switch (options[i])
        {
            case "--games" when int.TryParse(value, out var g) && g > 0: games = g; break;
            case "--seed" when ulong.TryParse(value, out var s): seed = s; break;
            case "--driver" when value is not null: driver = value; break;
            case "--freeze" when value is not null: freeze = value; break;
            default: return Fail(Usage);
        }
    }
    driver ??= CppDriver.DefaultPath();
    Console.WriteLine($"7wd crosscheck: {games:N0} random games from seed {seed} against {driver}");
    var report = CrossCheck.Run(driver, games, seed);
    Console.WriteLine(report);
    foreach (var d in report.Disagreements.Take(20)) Console.WriteLine("  " + d);
    if (freeze is not null && report.Passed)
    {
        Directory.CreateDirectory(freeze);
        foreach (var f in report.Frozen)
        {
            File.WriteAllText(Path.Combine(freeze, f.FileName), f.ToText());
            Console.WriteLine($"  froze {f.FileName}: {f.Expected}");
        }
    }
    return report.Passed ? 0 : 1;
}

static int RunMatch(string a, string b, string[] options)
{
    var settings = new MatchSettings(a, b);
    string? directory = null;
    int? threads = null;
    for (var i = 0; i < options.Length; i += 2)
    {
        var value = i + 1 < options.Length ? options[i + 1] : null;
        switch (options[i])
        {
            case "--pairs" when int.TryParse(value, out var n) && n > 0: settings = settings with { Pairs = n }; break;
            case "--seed" when ulong.TryParse(value, out var s): settings = settings with { FirstSeed = s }; break;
            case "--promo" when value is not null: settings = settings with { Promos = ParsePromos(value) }; break;
            case "--out" when value is not null: directory = value; break;
            case "--threads" when int.TryParse(value, out var t) && t > 0: threads = t; break;
            case "--elo-a" when double.TryParse(value, CultureInfo.InvariantCulture, out var e): settings = settings with { EloA = e }; break;
            case "--elo-b" when double.TryParse(value, CultureInfo.InvariantCulture, out var e): settings = settings with { EloB = e }; break;
            default: return Fail(Usage);
        }
    }

    using var stop = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        if (stop.IsCancellationRequested) return;
        Console.Error.WriteLine("stopping: finished pairs are kept, pairs in progress are dropped");
        stop.Cancel();
    };

    Console.WriteLine($"7wd match: {a} vs {b}, {settings.Pairs} seed pairs from seed {settings.FirstSeed} ({settings.Rules})"
        + (directory is null ? "" : $", saved in {directory}"));
    var finished = 0;
    var step = Math.Max(1, settings.Pairs / 10);
    var report = Match.Run(settings, directory, stop.Token, threads, onPair: _ =>
    {
        var n = Interlocked.Increment(ref finished);
        if (n % step == 0) Console.Error.WriteLine($"  {n} more pairs finished");
    });
    Console.WriteLine(report);
    return report.Complete ? 0 : 2;
}

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 1;
}
