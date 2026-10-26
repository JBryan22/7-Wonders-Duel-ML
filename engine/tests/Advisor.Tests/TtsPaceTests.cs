using SevenWondersDuel.Advisor.Tts;

namespace SevenWondersDuel.Advisor.Tests;

/// <summary>
/// How often the Adapter reads the table (owner's request, 2026-10-26: each reading makes TTS pause, which is annoying
/// while moving pieces): every interval ("live"), every 10 seconds once the table is settled, or only on "Read the
/// table now". A changing table is read every interval until it settles, whatever the pace.
/// </summary>
public sealed class TtsPaceTests
{
    const string Table = """{"v":1,"age":"None","players":[{"color":"White","host":true}],"pieces":[]}""";
    static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(20);

    sealed class CountingSource : ITableSource
    {
        int _reads;
        public int Reads => Volatile.Read(ref _reads);
        public string Text { get; set; } = Table;
        public Task<string> ReadAsync(CancellationToken cancel)
        {
            Interlocked.Increment(ref _reads);
            return Task.FromResult(Text);
        }
        public void Dispose() { }
    }

    static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition(), "timed out");
    }

    [Fact]
    public async Task Live_reads_every_interval()
    {
        var source = new CountingSource();
        using var tts = new TtsAdapter(() => source, _ => { }, Interval);
        tts.Start();
        await WaitFor(() => source.Reads >= 6);
    }

    [Fact]
    public async Task On_click_reads_until_settled_then_waits_for_the_next_click()
    {
        var source = new CountingSource();
        var settled = 0;
        using var tts = new TtsAdapter(() => source, _ => Interlocked.Increment(ref settled), Interval) { Pace = TtsPace.OnClick };
        tts.Start();
        // Following starts with one burst: two identical readings settle the table, then nothing more is read.
        await WaitFor(() => Volatile.Read(ref settled) == 1);
        var after = source.Reads;
        Assert.Equal(2, after);
        await Task.Delay(Interval * 10);
        Assert.Equal(after, source.Reads);

        tts.ReadNow();
        await WaitFor(() => Volatile.Read(ref settled) == 2);
        await Task.Delay(Interval * 10);
        Assert.Equal(after + 1, source.Reads); // the last reading still counts: one more identical reading settles it
    }

    [Fact]
    public async Task On_click_keeps_reading_a_changing_table_for_a_few_readings_at_most()
    {
        var source = new CountingSource();
        using var tts = new TtsAdapter(() => source, _ => { }, Interval) { Pace = TtsPace.OnClick };
        // A table that never settles (a piece held throughout).
        source.Text = Table.Replace("\"pieces\":[]", "\"pieces\":[{\"kind\":\"coin\",\"x\":0,\"y\":0,\"z\":0,\"held\":true,\"value\":1}]");
        tts.Start();
        await WaitFor(() => source.Reads >= TtsAdapter.ClickReads);
        await Task.Delay(Interval * 10);
        Assert.Equal(TtsAdapter.ClickReads, source.Reads);
    }

    [Fact]
    public async Task Every_10_seconds_waits_after_a_settled_table_and_a_click_reads_at_once()
    {
        var source = new CountingSource();
        var settled = 0;
        using var tts = new TtsAdapter(() => source, _ => Interlocked.Increment(ref settled), Interval) { Pace = TtsPace.Every10 };
        tts.Start();
        await WaitFor(() => Volatile.Read(ref settled) == 1);
        var after = source.Reads;
        await Task.Delay(Interval * 10);
        Assert.Equal(after, source.Reads); // the next reading is 10 seconds away

        tts.ReadNow();
        await WaitFor(() => source.Reads == after + 1);
    }

    [Fact]
    public void Prefs_keep_the_pace_and_fall_back_to_live()
    {
        var folder = Path.Combine(Path.GetTempPath(), "7wd-advisor-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new AdvisorStore(folder);
            Assert.Equal(TtsPace.Live, store.LoadTtsPrefs().TtsPace);
            store.SaveTtsPrefs(new TtsPrefs(Follow: true, Pace: "click"));
            Assert.Equal(TtsPace.OnClick, store.LoadTtsPrefs().TtsPace);
            File.WriteAllText(Path.Combine(folder, "tts.json"), """{"follow":true,"color":"Auto","pace":"hourly"}""");
            Assert.Equal(TtsPrefs.Live, store.LoadTtsPrefs().Pace);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }
}
