using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using SevenWondersDuel.Advisor.Tts;
using SevenWondersDuel.Engine;

namespace SevenWondersDuel.Advisor.Tests;

/// <summary>
/// Ticket 33 in the Advisor server: following a (fake) TTS table feeds the same session as the clicks. Settled snapshots
/// are reconciled, a single chain is appended and saved, a Desync appends nothing and clears when the table is fixed,
/// a manual Undo is not undone by the table, offered chains are entered with one click, and soft checks are notes.
/// </summary>
public sealed class TtsServerTests : IDisposable
{
    readonly string _folder = Path.Combine(Path.GetTempPath(), "7wd-advisor-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>A table that answers whatever the test last laid out.</summary>
    sealed class FakeSource : ITableSource
    {
        public string? Text { get; set; }
        public int Reads { get; private set; }
        public Task<string> ReadAsync(CancellationToken cancel)
        {
            Reads++;
            return Text is null ? throw new TtsException("Could not reach Tabletop Simulator (fake)") : Task.FromResult(Text);
        }
        public void Dispose() { }
    }

    [Fact]
    public async Task Following_the_table_appends_flags_desyncs_and_respects_manual_fixes()
    {
        var table = new FakeSource();
        var app = AdvisorServer.Build(new AdvisorOptions(_folder, Port: 0, TableSource: () => table, TtsPolling: false));
        await app.StartAsync();
        var http = new HttpClient { BaseAddress = new Uri(app.Urls.Single().TrimEnd('/') + "/") };
        var server = app.Services.GetRequiredService<AdvisorServer>();
        async Task Settle()
        {
            await server.Tts.PollOnceAsync();
            await server.Tts.PollOnceAsync();
        }
        async Task<JsonElement> State() => await ServerTests.Json(await http.GetAsync("api/state"));
        static string[] Log(JsonElement state) =>
            state.GetProperty("game").GetProperty("log").EnumerateArray().Select(l => l.GetProperty("text").GetString()!).ToArray();
        try
        {
            await http.PostAsJsonAsync("api/new", new { messe = false, liberty = false, rule2015 = false, seat = 0 });
            var off = (await State()).GetProperty("tts");
            Assert.Equal("off", off.GetProperty("status").GetString());
            await Settle();
            Assert.Equal(0, table.Reads); // nothing is read while not following

            var on = await ServerTests.Json(await http.PostAsJsonAsync("api/tts", new { follow = true }));
            Assert.True(on.GetProperty("tts").GetProperty("following").GetBoolean());
            Assert.Equal("White", on.GetProperty("tts").GetProperty("color").GetString());
            Assert.Contains("\"follow\": true", File.ReadAllText(Path.Combine(_folder, "tts.json")));

            // TTS not running: the problem is shown, nothing else happens.
            await Settle();
            var tts = (await State()).GetProperty("tts");
            Assert.False(tts.GetProperty("connected").GetBoolean());
            Assert.Contains("Could not reach", tts.GetProperty("problem").GetString());

            // The owner (P1) sits White. The table: setup, the draft dealt and P1's first pick.
            var sides = SeatSides.ForOwner(0, Side.White);
            var truth = AdvisorSession.New(Ruleset.Default, 0);
            foreach (var line in new[] { "reveal first P1", "reveal tokens agriculture economy law strategy urbanism",
                         "reveal draft pyramids great-lighthouse circus-maximus piraeus", "P1 draft pyramids" })
                truth.Enter(line);
            table.Text = TablePainter.Snapshot(truth.State, sides).Text;
            await server.Tts.PollOnceAsync();
            Assert.Empty(Log(await State())); // one reading is not settled yet
            Assert.Equal("reading", (await State()).GetProperty("tts").GetProperty("status").GetString());
            await server.Tts.PollOnceAsync();
            var state = await State();
            Assert.Equal(4, Log(state).Length);
            Assert.Equal("P1 draft pyramids", Log(state)[^1]);
            Assert.Equal("matches", state.GetProperty("tts").GetProperty("status").GetString());
            Assert.Contains("P1 draft pyramids", state.GetProperty("tts").GetProperty("lastAppended").GetString());
            Assert.Contains("P1 draft pyramids", File.ReadAllText(Path.Combine(_folder, "current.log"))); // saved like a click

            // P2 drafts the Lighthouse but it lands in P1's (White) column: a Desync, nothing appended.
            truth.Enter("P2 draft great-lighthouse");
            var objects = TablePainter.Paint(truth.State, sides);
            var lighthouse = objects.FindIndex(o => o.Name == TablePainter.WonderName(Engine.Data.GameData.WonderIndex("great-lighthouse")));
            objects[lighthouse] = objects[lighthouse] with { X = TtsData.Places.WonderX[Side.White], Z = TtsData.Places.WonderRows[1] };
            table.Text = TablePainter.Snapshot(objects, truth.State).Text;
            await Settle();
            state = await State();
            Assert.Equal(4, Log(state).Length);
            tts = state.GetProperty("tts");
            Assert.Equal("desync", tts.GetProperty("status").GetString());
            Assert.Contains("White (P1)'s Wonder: The Great Lighthouse", tts.GetProperty("notInLog").EnumerateArray().Select(e => e.GetString()));
            Assert.Contains("Wonder draft offer: The Great Lighthouse", tts.GetProperty("notOnTable").EnumerateArray().Select(e => e.GetString()));

            // Fixed on the table: the Desync clears and the pick is appended.
            table.Text = TablePainter.Snapshot(truth.State, sides).Text;
            await Settle();
            state = await State();
            Assert.Equal("P2 draft great-lighthouse", Log(state)[^1]);
            Assert.Equal("matches", state.GetProperty("tts").GetProperty("status").GetString());

            // The owner undoes it by hand: the table still shows it, but it is offered, not put straight back.
            state = await ServerTests.Json(await http.PostAsync("api/undo", null));
            Assert.Equal(4, Log(state).Length);
            Assert.Equal("held", state.GetProperty("tts").GetProperty("status").GetString());
            await Settle();
            state = await State();
            Assert.Equal(4, Log(state).Length);
            var choices = state.GetProperty("tts").GetProperty("choices");
            Assert.Equal("P2 draft great-lighthouse", Assert.Single(choices.EnumerateArray()).GetProperty("text").GetString());
            Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsync("api/tts/choose", new { index = 3 })).StatusCode);
            state = await ServerTests.Json(await http.PostAsJsonAsync("api/tts/choose", new { index = 0 }));
            Assert.Equal("P2 draft great-lighthouse", Log(state)[^1]);
            Assert.Equal("matches", state.GetProperty("tts").GetProperty("status").GetString());

            // Coins off on the table: a note, never a Desync.
            truth.Enter("P2 draft circus-maximus");
            objects = TablePainter.Paint(truth.State, sides);
            TablePainter.AddCoins(objects, 3, -30, -38);
            table.Text = TablePainter.Snapshot(objects, truth.State).Text;
            await Settle();
            tts = (await State()).GetProperty("tts");
            Assert.Equal("matches", tts.GetProperty("status").GetString());
            Assert.Equal(["Coins: TTS shows 10 for Red (P2), expected 7"], tts.GetProperty("notes").EnumerateArray().Select(e => e.GetString()));

            // The page polls this while following; the change counter tells it when to fetch the state again.
            var polled = await ServerTests.Json(await http.GetAsync("api/tts"));
            Assert.True(polled.GetProperty("changes").GetInt64() > 0);

            // Stop following: nothing more is read.
            var stopped = await ServerTests.Json(await http.PostAsJsonAsync("api/tts", new { follow = false }));
            Assert.Equal("off", stopped.GetProperty("tts").GetProperty("status").GetString());
            var reads = table.Reads;
            await Settle();
            Assert.Equal(reads, table.Reads);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    /// <summary>The background poller: following with polling on reads the table about once a second by itself.</summary>
    [Fact]
    public async Task The_background_poller_appends_by_itself()
    {
        var table = new FakeSource();
        var sides = SeatSides.ForOwner(1, Side.White);
        var truth = AdvisorSession.New(Ruleset.Default, 1);
        foreach (var line in new[] { "reveal first P2", "reveal tokens agriculture economy law strategy urbanism",
                     "reveal draft pyramids great-lighthouse circus-maximus piraeus", "P2 draft piraeus" })
            truth.Enter(line);
        table.Text = TablePainter.Snapshot(truth.State, sides).Text;

        var store = new AdvisorStore(_folder);
        store.SaveTtsPrefs(new TtsPrefs(Follow: true));
        store.SaveCurrent(AdvisorSession.New(Ruleset.Default, 1));
        using var server = new AdvisorServer(store, tableSource: () => table);
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (server.Session!.Log.Count < 4 && DateTime.UtcNow < deadline) await Task.Delay(100);
        Assert.Equal("P2 draft piraeus", MoveLog.Format(server.Session!.Log[^1]));
    }
}
