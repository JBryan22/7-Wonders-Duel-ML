using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using SevenWondersDuel.Advisor.Tts;
using SevenWondersDuel.Engine;
using SevenWondersDuel.Engine.Data;

namespace SevenWondersDuel.Advisor.Tests;

/// <summary>
/// Ticket 34, the TTS game life cycle: the owner's colour from the host's seat (overridable, asked when the host isn't
/// seated at White or Red), a fresh setup on the table offered as a new game (never started by itself), the unticked
/// promo in a draft with its one-click fix, and catching up after a restart or after TTS loads a game.
/// </summary>
public sealed class TtsLifecycleTests : IDisposable
{
    readonly string _folder = Path.Combine(Path.GetTempPath(), "7wd-advisor-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>A table that answers whatever the test last laid out, and can say TTS loaded a game.</summary>
    sealed class FakeSource : ITableSource
    {
        public string? Text { get; set; }
        public DateTime? LoadedAt { get; set; }
        public Task<string> ReadAsync(CancellationToken cancel) =>
            Text is null ? throw new TtsException("Could not reach Tabletop Simulator (fake)") : Task.FromResult(Text);
        public void Dispose() { }
    }

    /// <summary>An Advisor server on a free port whose polling the test drives.</summary>
    sealed class Harness : IAsyncDisposable
    {
        readonly WebApplication _app;
        public readonly HttpClient Http;
        public readonly AdvisorServer Server;

        Harness(WebApplication app)
        {
            _app = app;
            Http = new HttpClient { BaseAddress = new Uri(app.Urls.Single().TrimEnd('/') + "/") };
            Server = app.Services.GetRequiredService<AdvisorServer>();
        }

        public static async Task<Harness> Start(string folder, FakeSource table)
        {
            var app = AdvisorServer.Build(new AdvisorOptions(folder, Port: 0, TableSource: () => table, TtsPolling: false));
            await app.StartAsync();
            return new Harness(app);
        }

        public async Task Settle()
        {
            await Server.Tts.PollOnceAsync();
            await Server.Tts.PollOnceAsync();
        }

        public async Task<JsonElement> State() => await ServerTests.Json(await Http.GetAsync("api/state"));
        public async Task<JsonElement> Post(string path, object? body = null) =>
            await ServerTests.Json(await Http.PostAsJsonAsync(path, body ?? new { }));

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    static string[] Log(JsonElement state) => state.GetProperty("game").ValueKind == JsonValueKind.Null
        ? []
        : state.GetProperty("game").GetProperty("log").EnumerateArray().Select(l => l.GetProperty("text").GetString()!).ToArray();

    static string Status(JsonElement state) => state.GetProperty("tts").GetProperty("status").GetString()!;

    static AdvisorSession Setup(Ruleset rules, int seat, string first, string draft)
    {
        var s = AdvisorSession.New(rules, seat);
        s.Enter($"reveal first {first}");
        s.Enter("reveal tokens agriculture economy law strategy urbanism");
        s.Enter($"reveal draft {draft}");
        return s;
    }

    // ------------------------------------------------------------------ seat

    [Fact]
    public void The_owner_sits_at_the_hosts_colour_unless_overridden()
    {
        var s = AdvisorSession.New(Ruleset.Default, 0).State;
        TableSnapshot Hosted(params (string, bool)[] players) => TablePainter.Snapshot([], s, players: players);

        Assert.Equal(Side.White, TableLifecycle.OwnerSide("Auto", Hosted(("White", true))));
        Assert.Equal(Side.Red, TableLifecycle.OwnerSide("Auto", Hosted(("White", false), ("Red", true))));
        Assert.Null(TableLifecycle.OwnerSide("Auto", Hosted(("Grey", true))));     // a spectator
        Assert.Null(TableLifecycle.OwnerSide("Auto", Hosted(("Blue", true))));     // a seat this game doesn't use
        Assert.Null(TableLifecycle.OwnerSide("Auto", Hosted(("White", false))));   // the host isn't seated
        Assert.Null(TableLifecycle.OwnerSide("Auto", null));
        Assert.Equal(Side.Red, TableLifecycle.OwnerSide("Red", Hosted(("White", true)))); // the override wins
        Assert.Equal(Side.White, TableLifecycle.OwnerSide("White", Hosted(("Grey", true))));
    }

    [Fact]
    public async Task The_server_asks_for_the_seat_and_follows_the_hosts_colour()
    {
        var table = new FakeSource();
        await using var h = await Harness.Start(_folder, table);
        await h.Post("api/new", new { messe = false, liberty = false, rule2015 = false, seat = 0 });
        var on = await h.Post("api/tts", new { follow = true });
        Assert.Equal("Auto", on.GetProperty("tts").GetProperty("color").GetString());

        // The owner (P1) plays Red this time; the table after P1's first pick.
        var truth = Setup(Ruleset.Default, 0, "P1", "pyramids great-lighthouse circus-maximus piraeus");
        truth.Enter("P1 draft pyramids");
        var objects = TablePainter.Paint(truth.State, SeatSides.ForOwner(0, Side.Red));

        // The host watches as Grey: the Advisor asks instead of guessing, and enters nothing.
        table.Text = TablePainter.Snapshot(objects, truth.State, players: [("Grey", true)]).Text;
        await h.Settle();
        var state = await h.State();
        Assert.Equal("seat", Status(state));
        Assert.Empty(Log(state));

        // The host sits down at Red: followed from Red's side.
        table.Text = TablePainter.Snapshot(objects, truth.State, players: [("Red", true), ("White", false)]).Text;
        await h.Settle();
        state = await h.State();
        Assert.Equal("matches", Status(state));
        Assert.Equal("P1 draft pyramids", Log(state)[^1]);
        Assert.Equal("Red", state.GetProperty("tts").GetProperty("side").GetString());
        Assert.True(state.GetProperty("tts").GetProperty("sideDetected").GetBoolean());

        // Overridden (wrongly) to White: the table no longer agrees with the log, and it says so.
        state = await h.Post("api/tts", new { color = "White" });
        Assert.Equal("White", state.GetProperty("tts").GetProperty("side").GetString());
        Assert.False(state.GetProperty("tts").GetProperty("sideDetected").GetBoolean());
        Assert.Equal("desync", Status(state));
        Assert.Contains("\"color\": \"White\"", File.ReadAllText(Path.Combine(_folder, "tts.json")));
        state = await h.Post("api/tts", new { color = "Auto" });
        Assert.Equal("matches", Status(state));
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await h.Http.PostAsJsonAsync("api/tts", new { color = "Blue" })).StatusCode);
    }

    // ------------------------------------------------------------------ new game

    [Fact]
    public void A_fresh_setup_is_a_table_where_nothing_was_taken()
    {
        var setup = Setup(Ruleset.Default, 0, "P1", "pyramids great-lighthouse circus-maximus piraeus");
        var sides = SeatSides.ForOwner(0, Side.White);
        Assert.True(TableLifecycle.LooksFresh(TablePainter.Snapshot(AdvisorSession.New(Ruleset.Default, 0).State, sides))); // an empty table
        Assert.True(TableLifecycle.LooksFresh(TablePainter.Snapshot(setup.State, sides))); // tokens and the offer out
        setup.Enter("P1 draft pyramids");
        Assert.False(TableLifecycle.LooksFresh(TablePainter.Snapshot(setup.State, sides))); // a Wonder drafted
        Assert.False(TableLifecycle.LooksFresh(TablePainter.Snapshot([], setup.State, modAge: "Age1"))); // an Age dealt
        var practice = TableSnapshot.Parse(File.ReadAllText(TtsSnapshotTests.FixturePath));
        Assert.False(TableLifecycle.LooksFresh(practice));
    }

    [Fact]
    public async Task A_fresh_setup_on_the_table_is_offered_as_a_new_game_never_started_by_itself()
    {
        new AdvisorStore(_folder).SaveSettings(new NewGameSettings(Seat: 1));
        var table = new FakeSource();
        await using var h = await Harness.Start(_folder, table);
        await h.Post("api/tts", new { follow = true });
        var sides = SeatSides.ForOwner(1, Side.White);

        // No game, and a game already under way on the table: no cold join.
        var underway = Setup(Ruleset.Default, 1, "P1", "pyramids great-lighthouse circus-maximus piraeus");
        underway.Enter("P1 draft pyramids");
        table.Text = TablePainter.Snapshot(underway.State, sides).Text;
        await h.Settle();
        Assert.Equal("no-game", Status(await h.State()));

        // A fresh setup: offered, with the settings the click would use, and not started.
        var truth = Setup(Ruleset.Default, 1, "P2", "pyramids great-lighthouse circus-maximus piraeus");
        table.Text = TablePainter.Snapshot(truth.State, sides).Text;
        await h.Settle();
        var state = await h.State();
        Assert.Equal("new-game", Status(state));
        Assert.Contains("you are P2", state.GetProperty("tts").GetProperty("newGame").GetString());
        Assert.Equal(JsonValueKind.Null, state.GetProperty("game").ValueKind);
        Assert.Null(h.Server.Session);

        // One click starts it; the table's setup is then read (who plays first can't be seen yet: two choices).
        state = await h.Post("api/tts/newgame");
        Assert.Equal(1, state.GetProperty("game").GetProperty("seat").GetInt32());
        Assert.Equal("choices", Status(state));
        truth.Enter("P2 draft piraeus");
        table.Text = TablePainter.Snapshot(truth.State, sides).Text;
        await h.Settle();
        state = await h.State();
        Assert.Equal("P2 draft piraeus", Log(state)[^1]);
        var played = Log(state);

        // The table is set up again (a new game) while the log is a game in progress: offered, the log untouched.
        var next = Setup(Ruleset.Default, 1, "P1", "colossus sphinx temple-of-artemis appian-way");
        table.Text = TablePainter.Snapshot(next.State, sides).Text;
        await h.Settle();
        state = await h.State();
        Assert.Equal("new-game", Status(state));
        Assert.Contains("game in progress", state.GetProperty("tts").GetProperty("message").GetString());
        Assert.Equal(played, Log(state));

        state = await h.Post("api/tts/newgame");
        Assert.Contains("saved to", state.GetProperty("notice").GetString());
        Assert.Single(Directory.GetFiles(Path.Combine(_folder, "games")));
        Assert.Equal("choices", Status(state));
        Assert.Empty(Log(state));
    }

    // ------------------------------------------------------------------ promo

    [Fact]
    public void Only_promo_Wonders_in_the_draft_or_a_column_count_not_the_box()
    {
        var truth = Setup(new Ruleset(Promos: Promos.Messe), 0, "P1", "messe pyramids great-lighthouse piraeus");
        var sides = SeatSides.ForOwner(0, Side.White);
        Assert.Equal(Promos.Messe, TableLifecycle.MissingPromos(TablePainter.Snapshot(truth.State, sides), Ruleset.Default));
        Assert.Equal(Promos.None, TableLifecycle.MissingPromos(TablePainter.Snapshot(truth.State, sides), new Ruleset(Promos: Promos.Messe)));
        truth.Enter("P1 draft messe"); // drafted before the table was read: in P1's column
        Assert.Equal(Promos.Messe, TableLifecycle.MissingPromos(TablePainter.Snapshot(truth.State, sides), Ruleset.Default));

        // Unused promos lying in the box area are not in the game.
        var boxed = new List<FakeObject>
        {
            new("Card", "w1", "The Messe", -76.7, 1.6, 10, Tags: ["wonder"]),
            new("Card", "w2", "Statue Of Liberty", -76.7, 1.6, 14, Tags: ["wonder"]),
        };
        Assert.Equal(Promos.None, TableLifecycle.MissingPromos(TablePainter.Snapshot(boxed, truth.State), Ruleset.Default));
    }

    [Fact]
    public async Task An_unticked_promo_in_a_draft_offers_the_one_click_fix()
    {
        var table = new FakeSource();
        await using var h = await Harness.Start(_folder, table);
        await h.Post("api/new", new { messe = false, liberty = false, rule2015 = false, seat = 0 });
        await h.Post("api/tts", new { follow = true });

        // The mod's draft deals The Messe, but the game was started with its box unticked.
        var sides = SeatSides.ForOwner(0, Side.White);
        var truth = Setup(new Ruleset(Promos: Promos.Messe), 0, "P1", "messe pyramids great-lighthouse piraeus");
        truth.Enter("P1 draft messe");
        table.Text = TablePainter.Snapshot(truth.State, sides).Text;
        await h.Settle();
        var state = await h.State();
        Assert.Equal("promo", Status(state));
        var tts = state.GetProperty("tts");
        Assert.StartsWith("The Messe is in the Wonder draft", tts.GetProperty("message").GetString());
        var promo = Assert.Single(tts.GetProperty("promos").EnumerateArray());
        Assert.Equal("messe", promo.GetProperty("id").GetString());
        Assert.Equal("The Messe", promo.GetProperty("name").GetString());
        Assert.Contains("White (P1)'s Wonder: The Messe", tts.GetProperty("notInLog").EnumerateArray().Select(e => e.GetString()));
        Assert.Empty(Log(state));

        // One click: the header gains the promo, the log is replayed, and the table is read again at once.
        state = await h.Post("api/tts/promo", new { promo = "messe" });
        Assert.Contains("promo=messe", state.GetProperty("game").GetProperty("rules").GetString());
        Assert.Contains("promo=messe", File.ReadAllText(Path.Combine(_folder, "current.log")));
        Assert.Equal("matches", Status(state));
        Assert.Equal("P1 draft messe", Log(state)[^1]);
        Assert.True(new AdvisorStore(_folder).LoadSettings().Messe); // remembered for the next new game
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await h.Http.PostAsJsonAsync("api/tts/promo", new { promo = "pantheon" })).StatusCode);
    }

    [Fact]
    public void Enabling_a_promo_replays_the_whole_log()
    {
        var session = Setup(Ruleset.Default, 1, "P1", "pyramids great-lighthouse circus-maximus piraeus");
        session.Enter("P1 draft pyramids");
        var promo = session.WithRules(new Ruleset(Promos: Promos.Liberty));
        Assert.Equal(1, promo.Seat);
        Assert.Equal(session.Log.Count, promo.Log.Count);
        Assert.Contains("promo=liberty", promo.Write());
        Assert.NotEqual(session.State.UnseenWonders, promo.State.UnseenWonders);
        Assert.True((promo.State.UnseenWonders & 1 << GameData.WonderIndex("statue-of-liberty")) != 0);
    }

    // ------------------------------------------------------------------ catch-up

    /// <summary>
    /// Restart catch-up at the reconciler: random games (all rule sets, both seats) where the table went on 6 Moves while
    /// the Advisor was closed. The deeper catch-up search brings the log back to the game, with the same help from the
    /// owner as in normal play (a choice the table can't show, a card it never showed).
    /// </summary>
    [Fact]
    public void Catch_up_follows_a_table_several_Moves_ahead()
    {
        Ruleset[] rules = [Ruleset.Default, new(Promos: Promos.Messe | Promos.Liberty), new(StartChoice.Rule2015, Promos.Liberty)];
        var beyondPlain = 0;
        var counts = new TtsReconcileTests.Counts(0, 0, 0, 0);
        for (ulong seed = 0; seed < 24; seed++)
        {
            var seat = (int)(seed % 2);
            var sides = SeatSides.ForOwner(seat, seed % 4 < 2 ? Side.White : Side.Red);
            var truth = AdvisorSession.New(rules[seed % 3], seat);
            var chance = new RandomChanceSource(seed + 300);
            var rng = new Rng(seed * 17 + 3);
            TtsReconcileTests.Advance(truth, chance, rng, (int)(seed * 7 % 50));
            if (truth.State.IsOver) continue;
            var follower = AdvisorSession.Load(truth.Write()); // the log saved when the Advisor closed
            TtsReconcileTests.Advance(truth, chance, rng, 6);
            var table = TablePainter.Snapshot(truth.State, sides);
            if (Reconciler.Reconcile(follower.State, seat, sides, table).Status == SyncStatus.Desync) beyondPlain++;
            counts = TtsReconcileTests.CatchUp(follower, truth, sides, table, counts, ReconcileOptions.CatchUp, handMoves: true);
            if (!follower.State.ToBytes().AsSpan().SequenceEqual(truth.State.ToBytes()))
            {
                var before = new Game(truth.Rules);
                foreach (var e in truth.Log.Take(truth.Log.Count - 1)) before.Apply(e);
                Assert.True(truth.Log[^1] is MoveEntry { Move.Kind: MoveKind.StartMe or MoveKind.StartOpponent or MoveKind.LibertyKeep }
                    && before.State.ToBytes().AsSpan().SequenceEqual(follower.State.ToBytes()), $"behind the game:\n{follower.Write()}\n{truth.Write()}");
            }
        }
        Assert.True(beyondPlain >= 5, $"only {beyondPlain} gaps needed the catch-up search");
        // Moves entered by hand only where the table can't tell which card went under a Wonder before the owner's Great
        // Library draw (random play builds far more Wonders than people do).
        Assert.True(counts.HandMoves <= 6, $"{counts}");
    }

    /// <summary>A table 6 Moves past the saved log that the normal 4-Move search can't explain.</summary>
    static (AdvisorSession Saved, AdvisorSession Truth, SeatSides Sides) RestartScenario()
    {
        for (ulong seed = 0; seed < 40; seed++)
        {
            var sides = SeatSides.ForOwner(0, Side.White);
            var truth = AdvisorSession.New(Ruleset.Default, 0);
            var chance = new RandomChanceSource(seed + 900);
            var rng = new Rng(seed + 11);
            TtsReconcileTests.Advance(truth, chance, rng, 12);
            var saved = AdvisorSession.Load(truth.Write());
            TtsReconcileTests.Advance(truth, chance, rng, 6);
            if (truth.State.Step == StepKind.Reveal) continue;
            var table = TablePainter.Snapshot(truth.State, sides);
            if (Reconciler.Reconcile(saved.State, 0, sides, table).Status == SyncStatus.Desync
                && Reconciler.Reconcile(saved.State, 0, sides, table, ReconcileOptions.CatchUp).Status == SyncStatus.OneChain)
                return (saved, truth, sides);
        }
        Assert.Fail("no restart scenario found");
        return default;
    }

    [Fact]
    public async Task Restarting_mid_game_catches_up_cleanly()
    {
        var (saved, truth, sides) = RestartScenario();
        var store = new AdvisorStore(_folder);
        store.SaveCurrent(saved);
        store.SaveTtsPrefs(new TtsPrefs(Follow: true));
        var table = new FakeSource { Text = TablePainter.Snapshot(truth.State, sides).Text };

        // The Advisor starts again, resumes the saved log, and reads the table that went on without it.
        await using var h = await Harness.Start(_folder, table);
        Assert.True((await h.State()).GetProperty("tts").GetProperty("catchingUp").GetBoolean());
        await h.Settle();
        var state = await h.State();
        Assert.Equal("matches", Status(state));
        Assert.False(state.GetProperty("tts").GetProperty("catchingUp").GetBoolean());
        Assert.Equal(truth.State.ToBytes(), h.Server.Session!.State.ToBytes());
        // Saved as entered (Moves the table can't order, like two discards in a row, may be logged in either order).
        Assert.Equal(truth.State.ToBytes(), AdvisorSession.Load(File.ReadAllText(Path.Combine(_folder, "current.log"))).State.ToBytes());

        // Back to normal following: the next Moves are read with the ordinary search.
        var chance = new RandomChanceSource(77);
        TtsReconcileTests.Advance(truth, chance, new Rng(77), 1);
        table.Text = TablePainter.Snapshot(truth.State, sides).Text;
        await h.Settle();
        Assert.Equal(truth.State.ToBytes(), h.Server.Session!.State.ToBytes());
    }

    [Fact]
    public async Task TTS_loading_a_game_starts_a_catch_up()
    {
        var table = new FakeSource();
        await using var h = await Harness.Start(_folder, table);
        await h.Post("api/new", new { messe = false, liberty = false, rule2015 = false, seat = 0 });
        await h.Post("api/tts", new { follow = true });
        var sides = SeatSides.ForOwner(0, Side.White);
        var truth = Setup(Ruleset.Default, 0, "P1", "pyramids great-lighthouse circus-maximus piraeus");
        truth.Enter("P1 draft pyramids");
        table.Text = TablePainter.Snapshot(truth.State, sides).Text;
        await h.Settle();
        var state = await h.State();
        Assert.Equal("matches", Status(state));
        Assert.False(state.GetProperty("tts").GetProperty("catchingUp").GetBoolean());

        // TTS says it loaded a game (the owner reloaded a save): settling starts over, and catch-up is on until it matches.
        table.LoadedAt = DateTime.UtcNow;
        await h.Server.Tts.PollOnceAsync();
        state = await h.State();
        Assert.True(state.GetProperty("tts").GetProperty("catchingUp").GetBoolean());
        Assert.False(state.GetProperty("tts").GetProperty("settled").GetBoolean());
        await h.Server.Tts.PollOnceAsync();
        state = await h.State();
        Assert.Equal("matches", Status(state));
        Assert.False(state.GetProperty("tts").GetProperty("catchingUp").GetBoolean());
    }
}
