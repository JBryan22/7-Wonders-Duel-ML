using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using SevenWondersDuel.Advisor;
using SevenWondersDuel.Engine;

namespace SevenWondersDuel.Advisor.Tests;

/// <summary>Persistence and the local web server: the page and its API, as the browser uses them.</summary>
public sealed class ServerTests : IDisposable
{
    readonly string _folder = Path.Combine(Path.GetTempPath(), "7wd-advisor-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void The_store_saves_resumes_archives_and_remembers_the_new_game_choices()
    {
        var store = new AdvisorStore(_folder);
        Assert.Null(store.LoadCurrent());
        Assert.Equal(new NewGameSettings(), store.LoadSettings());

        var settings = new NewGameSettings(Messe: true, Liberty: false, Rule2015: true, Seat: 1);
        store.SaveSettings(settings);
        Assert.Equal(settings, new AdvisorStore(_folder).LoadSettings());
        Assert.Equal("rules base start-choice=2015 promo=messe", settings.ToRuleset().ToString());

        var session = AdvisorSession.New(settings.ToRuleset(), settings.Seat);
        session.Enter("reveal first P2");
        store.SaveCurrent(session);
        var text = File.ReadAllText(store.CurrentPath);
        Assert.Contains("# advisor seat=P2", text);
        Assert.Equal(session.State.ToBytes(), MoveLog.Parse(text).State.ToBytes()); // still a plain Move Log for 7wd replay

        var resumed = new AdvisorStore(_folder).LoadCurrent()!;
        Assert.Equal(1, resumed.Seat);
        Assert.Equal(session.Write(), resumed.Write());

        var archived = store.ArchiveCurrent()!;
        Assert.True(File.Exists(archived));
        Assert.Null(store.LoadCurrent());
    }

    static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    static async Task<(WebApplication App, HttpClient Http)> Start(string folder)
    {
        var port = FreePort();
        var app = AdvisorServer.Build(new AdvisorOptions(folder, port));
        await app.StartAsync();
        return (app, new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") });
    }

    static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    [Fact]
    public async Task The_page_and_API_enter_undo_rewind_and_resume_after_a_restart()
    {
        var (app, http) = await Start(_folder);
        try
        {
            var page = await http.GetStringAsync("");
            Assert.Contains("<script type=\"module\" src=\"app.js\">", page);
            Assert.Contains("function render", await http.GetStringAsync("app.js"));
            Assert.Contains(".pyramid", await http.GetStringAsync("style.css"));

            var state = await Json(await http.GetAsync("api/state"));
            Assert.Equal(JsonValueKind.Null, state.GetProperty("game").ValueKind);
            Assert.False(state.GetProperty("settings").GetProperty("messe").GetBoolean());

            var created = await http.PostAsJsonAsync("api/new", new { messe = true, liberty = true, rule2015 = false, seat = 1 });
            Assert.Equal(HttpStatusCode.OK, created.StatusCode);
            var game = (await Json(created)).GetProperty("game");
            Assert.Equal("rules base start-choice=new promo=messe,liberty", game.GetProperty("rules").GetString());
            Assert.Equal("first", game.GetProperty("reveal").GetProperty("kind").GetString());
            Assert.Equal("P2 (you)", game.GetProperty("players")[1].GetProperty("label").GetString());

            foreach (var line in new[] { "reveal first P1", "reveal tokens agriculture economy law strategy urbanism" })
                Assert.Equal(HttpStatusCode.OK, (await http.PostAsJsonAsync("api/entry", new { line })).StatusCode);

            var refused = await http.PostAsJsonAsync("api/entry", new { line = "reveal draft pyramids pyramids sphinx colossus" });
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            var body = await Json(refused);
            Assert.Contains("4 different Wonders", body.GetProperty("error").GetString());
            Assert.Equal(2, body.GetProperty("game").GetProperty("log").GetArrayLength()); // the view still comes back

            var draft = (await Json(await http.PostAsJsonAsync("api/entry", new { line = "reveal draft pyramids messe sphinx colossus" }))).GetProperty("game");
            Assert.Equal("draft", draft.GetProperty("decision").GetProperty("kind").GetString());
            Assert.Equal(4, draft.GetProperty("decision").GetProperty("tiles").GetArrayLength());
            await http.PostAsJsonAsync("api/entry", new { line = "P1 draft messe" });

            var undone = (await Json(await http.PostAsync("api/undo", null))).GetProperty("game");
            Assert.Equal(3, undone.GetProperty("log").GetArrayLength());
            var rewound = (await Json(await http.PostAsJsonAsync("api/rewind", new { count = 1 }))).GetProperty("game");
            Assert.Equal(1, rewound.GetProperty("log").GetArrayLength());
            Assert.Equal("tokens", rewound.GetProperty("reveal").GetProperty("kind").GetString());
            await http.PostAsJsonAsync("api/entry", new { line = "reveal tokens agriculture economy law strategy urbanism" });
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }

        // A restart resumes the saved game.
        (app, http) = await Start(_folder);
        try
        {
            var state = await Json(await http.GetAsync("api/state"));
            Assert.Contains("Resumed", state.GetProperty("notice").GetString());
            var game = state.GetProperty("game");
            Assert.Equal(2, game.GetProperty("log").GetArrayLength());
            Assert.Equal(1, game.GetProperty("seat").GetInt32());

            // Abandoning archives it; the new-game screen remembers the promo boxes.
            var after = await Json(await http.PostAsync("api/abandon", null));
            Assert.Equal(JsonValueKind.Null, after.GetProperty("game").ValueKind);
            Assert.True(after.GetProperty("settings").GetProperty("liberty").GetBoolean());
            Assert.Single(Directory.GetFiles(Path.Combine(_folder, "games")));
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_broken_saved_game_is_moved_aside_instead_of_stopping_the_Advisor()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "current.log"), "7wd-log v1\nrules base start-choice=new\nreveal first P7\n");
        var (app, http) = await Start(_folder);
        try
        {
            var state = await Json(await http.GetAsync("api/state"));
            Assert.Equal(JsonValueKind.Null, state.GetProperty("game").ValueKind);
            Assert.Contains("could not be read", state.GetProperty("notice").GetString());
            Assert.Single(Directory.GetFiles(Path.Combine(_folder, "games")));
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
