using System.Diagnostics;

namespace SevenWondersDuel.Advisor.Tts;

/// <summary>
/// <c>7wd-advisor tts-snapshot</c>: reads the TTS table once (or a few times) and prints it place by place. With
/// <c>--compare</c> it checks the table against a recorded snapshot, e.g. the committed practice save's
/// (tts/fixtures/ml-practice.snapshot.json): the owner's live check for ticket 32.
/// </summary>
public static class TtsCommand
{
    public const string Usage = """
        usage: 7wd-advisor tts-snapshot [--compare FILE] [--save FILE] [--count N] [--age N] [--timeout S]
          Reads the Tabletop Simulator table through the External Editor API and prints it place by place.
          Nothing on the table is changed. Close any TTS code-editor plugin (VS Code, Atom) first.
          --compare FILE  compare with a recorded snapshot (e.g. tts/fixtures/ml-practice.snapshot.json)
          --save FILE     also write the snapshot as TTS sent it
          --count N       read N times, one second apart, and report each round trip (default 1)
          --age N         use the Age N pyramid grid (default: the mod's own Age)
          --timeout S     seconds to wait for TTS (default 5)
        """;

    public static async Task<int> Run(string[] args, TextWriter output)
    {
        string? compare = null, save = null;
        int count = 1, ttsPort = TtsLink.DefaultTtsPort, listenPort = TtsLink.DefaultListenPort;
        int? age = null;
        var timeout = TimeSpan.FromSeconds(5);
        for (var i = 0; i < args.Length; i++)
        {
            int n;
            switch (args[i])
            {
                case "--compare" when i + 1 < args.Length: compare = args[++i]; break;
                case "--save" when i + 1 < args.Length: save = args[++i]; break;
                case "--count" when i + 1 < args.Length && int.TryParse(args[i + 1], out n) && n > 0: count = n; i++; break;
                case "--age" when i + 1 < args.Length && int.TryParse(args[i + 1], out n) && n is >= 1 and <= 3: age = n; i++; break;
                case "--timeout" when i + 1 < args.Length && int.TryParse(args[i + 1], out n) && n > 0: timeout = TimeSpan.FromSeconds(n); i++; break;
                case "--tts-port" when i + 1 < args.Length && int.TryParse(args[i + 1], out n): ttsPort = n; i++; break;
                case "--listen-port" when i + 1 < args.Length && int.TryParse(args[i + 1], out n): listenPort = n; i++; break;
                default:
                    output.WriteLine(Usage);
                    return 1;
            }
        }

        TableSnapshot? expected = null;
        if (compare is not null)
        {
            try
            {
                expected = TableSnapshot.Parse(File.ReadAllText(compare));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException)
            {
                output.WriteLine($"Can't read {compare}: {e.Message}");
                return 1;
            }
        }

        try
        {
            using var link = new TtsLink(ttsPort, listenPort);
            TableSnapshot? last = null;
            string? lastText = null;
            for (var i = 0; i < count; i++)
            {
                if (i > 0) await Task.Delay(TimeSpan.FromSeconds(1));
                var clock = Stopwatch.StartNew();
                var text = await link.RequestSnapshotTextAsync(timeout);
                var ms = clock.ElapsedMilliseconds;
                last = TableSnapshot.Parse(text);
                var note = lastText is null ? "" : text == lastText ? ", same as the last one" : ", changed since the last one";
                output.WriteLine($"Snapshot {i + 1}: {ms} ms round trip, {text.Length:N0} characters, {last.Pieces.Count} pieces{(last.StillNow ? "" : ", something is held or moving")}{note}");
                lastText = text;
            }
            if (save is not null) File.WriteAllText(save, lastText);

            // A table with Pantheon pieces on it is read with them (a base table has none, so it reads as before).
            var pantheon = last!.HasPantheonPieces;
            var view = TableView.From(last!, age, pantheon);
            output.WriteLine();
            output.WriteLine($"Mod Age: {last!.ModAge}; pyramid grid: {(view.Age is { } a ? $"Age {a}" : "none")}");
            output.WriteLine($"Seated: {(last.Players.Count == 0 ? "nobody" : string.Join(", ", last.Players.Select(p => p.Color + (p.Host ? " (host)" : ""))))}");
            foreach (var fact in view.Facts()) output.WriteLine($"  {fact}");

            if (expected is null) return 0;
            var diffs = TableView.Compare(TableView.From(expected, age, pantheon).Facts(), view.Facts());
            output.WriteLine();
            if (diffs.Count == 0)
            {
                output.WriteLine($"MATCH: the table matches {Path.GetFileName(compare)} place by place.");
                return 0;
            }
            output.WriteLine($"DIFFERENT from {Path.GetFileName(compare)} ({diffs.Count} differences):");
            foreach (var d in diffs) output.WriteLine($"  {d}");
            return 2;
        }
        catch (TtsException e)
        {
            output.WriteLine(e.Message);
            return 1;
        }
        catch (FormatException e)
        {
            output.WriteLine($"TTS sent something unexpected: {e.Message}");
            return 1;
        }
    }
}
