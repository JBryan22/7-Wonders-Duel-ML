using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using SevenWondersDuel.Agents;
using SevenWondersDuel.Engine;
using SevenWondersDuel.Engine.Data;

namespace SevenWondersDuel.Advisor.Tests;

/// <summary>
/// "Play against the AI" (ticket 36): the program deals, the AI plays its seat from its own Observation, the owner's
/// view never holds what their seat may not see, takebacks, saving and resuming, and results recorded per opponent.
/// </summary>
public sealed class PlayAiTests : IDisposable
{
    static readonly Ruleset[] AllRules =
    [
        Ruleset.Default,
        new(Promos: Promos.Messe | Promos.Liberty),
        new(StartChoice.Rule2015, Promos.Liberty),
    ];

    static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    readonly string _folder = Path.Combine(Path.GetTempPath(), "7wd-advisor-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    static AiOpponent Opponent(string spec, ulong seed, int sims = 50) => new(spec, spec, sims, seed);

    static string OwnerLine(AdvisorSession session, Rng rng)
    {
        var moves = session.State.Observe(session.Seat).LegalMoves();
        return MoveLog.Format(new MoveEntry(session.Seat, moves[rng.Next(moves.Count)]));
    }

    /// <summary>
    /// Whole games against the AI (random, and the Heuristic) in every rule set and both seats: every Reveal is dealt at
    /// once, every AI Move is legal in its Observation and in the game, the owner's state never holds anything their
    /// Observation would strip, the AI's Great Library draw is never in the log or the view (only the token it keeps,
    /// once it's in its city), and the saved log replays and resumes to the same game.
    /// </summary>
    [Fact]
    public void The_program_deals_the_AI_plays_legally_and_the_owner_never_sees_hidden_information()
    {
        var aiDraws = 0;
        for (ulong seed = 0; seed < 40; seed++)
        {
            var rules = AllRules[seed % 3];
            var seat = (int)(seed / 3 % 2);
            var session = AdvisorSession.New(rules, seat, Opponent(seed % 4 == 3 ? "heuristic" : "random", seed * 31 + 7));
            var rng = new Rng(seed + 1000);
            var secret = new HashSet<string>(); // names of tokens the AI drew and didn't keep
            while (!session.State.IsOver)
            {
                Assert.NotEqual(StepKind.Reveal, session.State.Step);
                Assert.True(session.State.Observe(seat).State.SameAs(session.State), "the owner's state holds something their Observation hides");
                var json = JsonSerializer.Serialize(BoardView.Build(session, "x"), Web);
                foreach (var name in secret) Assert.DoesNotContain(name, json);
                Assert.DoesNotContain(session.Log, e => e is LibraryReveal { Redacted: false } l && l.Player == session.AiSeat);

                if (session.AiToMove)
                {
                    var observation = session.AiObservation();
                    Assert.Equal(session.AiSeat, observation.Player);
                    var move = AiPlayer.Choose(observation, session.Ai!, session.AiSeed);
                    Assert.Contains(move, observation.LegalMoves());
                    Assert.Contains(move, session.State.LegalMoves());
                    if (observation.State.LibraryDraw != 0)
                    {
                        aiDraws++;
                        Assert.Equal(0, session.State.LibraryDraw); // the owner's copy holds no draw
                        Assert.Equal(observation.State.LibraryDraw, session.AiObservation().State.LibraryDraw); // asking again: the same draw
                        foreach (var t in ProgressTokens.In(observation.State.LibraryDraw))
                            if (t != move.Token)
                                secret.Add(GameData.Tokens[t].Name);
                        // The view offers every token in the box for the AI's keep (as with a hidden draw at the table),
                        // so it doesn't single out the 3 drawn.
                        var tiles = BoardView.Build(session, "x").Decision!.Tiles.Select(t => t.Label).Order().ToList();
                        Assert.Equal(ProgressTokens.In(session.State.TokensSetAside).Select(t => GameData.Tokens[t].Name).Order().ToList(), tiles);
                        Assert.True(tiles.Count > GameState.LibraryDrawSize);
                    }
                    session.EnterAiMove(move);
                }
                else
                {
                    session.EnterOwn([OwnerLine(session, rng)]);
                }
            }
            Assert.Equal(MoveLog.Parse(session.Write()).State.ToBytes(), session.State.ToBytes());
            var loaded = AdvisorSession.Load(session.Write());
            Assert.Equal(session.Ai, loaded.Ai);
            Assert.Equal(session.Write(), loaded.Write());
        }
        Assert.True(aiDraws > 0, "no game had the AI build the Great Library: the hidden-draw checks never ran");
    }

    [Fact]
    public void Dealing_and_the_AI_depend_only_on_the_seed_and_the_log_so_a_saved_game_resumes_exactly()
    {
        for (ulong seed = 0; seed < 8; seed++)
        {
            var a = AdvisorSession.New(Ruleset.Default, 0, Opponent("mcts", seed, sims: 30));
            var rng = new Rng(seed);
            for (var step = 0; step < 30 && !a.State.IsOver; step++)
            {
                if (a.AiToMove)
                {
                    // A restart mid-game: the saved log resumes, and the AI chooses the same Move from it.
                    var resumed = AdvisorSession.Load(a.Write());
                    var move = AiPlayer.Choose(a.AiObservation(), a.Ai!, a.AiSeed);
                    Assert.Equal(move, AiPlayer.Choose(resumed.AiObservation(), resumed.Ai!, resumed.AiSeed));
                    a.EnterAiMove(move);
                }
                else a.EnterOwn([OwnerLine(a, rng)]);
            }
            var b = AdvisorSession.New(Ruleset.Default, 0, Opponent("mcts", seed, sims: 30));
            foreach (var e in a.Log.OfType<MoveEntry>())
            {
                if (b.AiToMove) b.EnterAiMove(e.Move);
                else b.EnterOwn([MoveLog.Format(e)]);
            }
            Assert.Equal(a.Write(), b.Write()); // the same Moves deal the same cards
        }
    }

    [Fact]
    public void The_owner_enters_only_their_own_Moves_and_takes_back_their_last_Move_with_the_AIs_reply()
    {
        var session = AdvisorSession.New(Ruleset.Default, 0, Opponent("random", 5));
        var rng = new Rng(9);
        // Play until the owner has moved twice.
        var ownMoves = 0;
        while (ownMoves < 2)
        {
            if (session.AiToMove) session.EnterAiMove(AiPlayer.Choose(session.AiObservation(), session.Ai!, session.AiSeed));
            else
            {
                Assert.Throws<EntryException>(() => session.EnterOwn(["reveal first P1"]));
                var aiMove = session.State.LegalMoves()[0];
                Assert.Throws<EntryException>(() => session.EnterOwn([MoveLog.Format(new MoveEntry(1, aiMove))]));
                session.EnterOwn([OwnerLine(session, rng)]);
                ownMoves++;
            }
        }
        while (session.AiToMove) session.EnterAiMove(AiPlayer.Choose(session.AiObservation(), session.Ai!, session.AiSeed));

        var lastOwn = session.Log.ToList().FindLastIndex(e => e is MoveEntry m && m.Player == 0);
        var expected = session.Log.Take(lastOwn).Select(MoveLog.Format).ToList();
        Assert.True(session.CanTakeBack);
        session.TakeBack();
        Assert.Equal(expected, session.Log.Select(MoveLog.Format).ToList());
        Assert.Equal(1, session.Ai!.Takebacks);
        Assert.False(session.AiToMove);
        Assert.Contains("\"takebacks\":1", session.Write());

        var fresh = AdvisorSession.New(Ruleset.Default, 1, Opponent("random", 6));
        while (fresh.AiToMove) fresh.EnterAiMove(AiPlayer.Choose(fresh.AiObservation(), fresh.Ai!, fresh.AiSeed));
        Assert.False(fresh.CanTakeBack);
        Assert.Throws<EntryException>(fresh.TakeBack);
    }

    [Fact]
    public void The_opponent_list_offers_each_Runs_best_network_and_Generations_then_the_built_in_Agents()
    {
        var runs = MakeRuns();
        var list = Opponents.List(runs);
        var best = list[0];
        Assert.Equal("pilot-x", best.Group);
        Assert.Equal("pilot-x Gen 2", best.Label);
        Assert.Contains("(Gen 2)", best.Text);
        Assert.StartsWith("neural:", best.Spec);
        Assert.EndsWith(Path.Combine("pilot-x", "net", "gen-0002.onnx") + ",mode=classic", best.Spec);
        Assert.Equal(new[] { "pilot-x Gen 2", "pilot-x Gen 2", "pilot-x Gen 1", "MCTS Anchor", "Heuristic", "Random" }, list.Select(o => o.Label));
        Assert.Equal(new[] { "mcts", "heuristic", "random" }, list.TakeLast(3).Select(o => o.Spec));
        Assert.Equal(3, Opponents.List(Path.Combine(_folder, "missing")).Count);
    }

    /// <summary>A runs folder like C:\7wd-runs with one Run, two Generations (the tiny test network) and a best.json.</summary>
    string MakeRuns()
    {
        var runs = Path.Combine(_folder, "runs");
        var net = Path.Combine(runs, "pilot-x", "net");
        Directory.CreateDirectory(net);
        var tiny = Path.Combine(AppContext.BaseDirectory, "Models", "tiny.onnx");
        File.Copy(tiny, Path.Combine(net, "gen-0001.onnx"));
        File.Copy(tiny, Path.Combine(net, "gen-0002.onnx"));
        File.WriteAllText(Path.Combine(runs, "pilot-x", "best.json"), """{"generation": 2, "model": "net/gen-0002.onnx"}""");
        Directory.CreateDirectory(Path.Combine(runs, "empty-run"));
        return runs;
    }

    async Task<(WebApplication App, HttpClient Http, AdvisorServer Server)> Start(string? runs = null)
    {
        var app = AdvisorServer.Build(new AdvisorOptions(Path.Combine(_folder, "data"), Port: 0, RunsFolder: runs, AiPause: TimeSpan.Zero));
        await app.StartAsync();
        return (app, new HttpClient { BaseAddress = new Uri(app.Urls.Single().TrimEnd('/') + "/") }, app.Services.GetRequiredService<AdvisorServer>());
    }

    /// <summary>
    /// Through the HTTP API as the page uses it: a game against Random, the owner clicking random legal Moves, the AI
    /// answering by itself, to the end. Advice is off by default and can be turned on; rewind is refused, Undo takes
    /// back; the finished game is recorded once in vs-ai.json and the tally shows it; a second game adds to the tally.
    /// </summary>
    [Fact]
    public async Task A_full_game_against_Random_over_HTTP_ends_and_records_the_result()
    {
        var (app, http, server) = await Start();
        try
        {
            for (var game = 1; game <= 2; game++)
            {
                var created = await http.PostAsJsonAsync("api/new", new
                {
                    messe = true, liberty = true, rule2015 = false, seat = 0, vsAi = true, opponent = "random",
                    opponentLabel = "Random", aiSimulations = 800, randomSeat = true,
                });
                Assert.Equal(HttpStatusCode.OK, created.StatusCode);
                var state = await ServerTests.Json(created);
                Assert.Equal("Random", state.GetProperty("play").GetProperty("opponent").GetString());
                Assert.Equal("off", state.GetProperty("advice").GetProperty("status").GetString());
                Assert.Equal(JsonValueKind.Null, state.GetProperty("game").GetProperty("reveal").ValueKind); // the setup is dealt

                var rng = new Rng((ulong)game);
                var moves = 0;
                while (true)
                {
                    await server.AiSettled();
                    var session = server.Session!;
                    if (session.State.IsOver) break;
                    Assert.False(session.AiToMove);
                    if (moves == 3 && game == 1)
                    {
                        var on = await ServerTests.Json(await http.PostAsJsonAsync("api/play/advice", new { on = true }));
                        Assert.NotEqual("off", on.GetProperty("advice").GetProperty("status").GetString());
                        var off = await ServerTests.Json(await http.PostAsJsonAsync("api/play/advice", new { on = false }));
                        Assert.Equal("off", off.GetProperty("advice").GetProperty("status").GetString());
                        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsync("api/rewind", new { count = 3 })).StatusCode);
                        var undo = await ServerTests.Json(await http.PostAsync("api/undo", null));
                        Assert.Equal(1, undo.GetProperty("play").GetProperty("takebacks").GetInt32());
                        await server.AiSettled();
                        session = server.Session!;
                    }
                    var line = OwnerLine(session, rng);
                    var answer = await http.PostAsJsonAsync("api/entry", new { line });
                    Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
                    moves++;
                }
                var end = await ServerTests.Json(await http.GetAsync("api/state"));
                Assert.NotEqual(JsonValueKind.Null, end.GetProperty("game").GetProperty("result").ValueKind);
                Assert.False(end.GetProperty("game").GetProperty("canUndo").GetBoolean());
                var tally = end.GetProperty("play").GetProperty("tally");
                Assert.Equal(game, tally.GetProperty("you").GetInt32() + tally.GetProperty("ai").GetInt32() + tally.GetProperty("draws").GetInt32());
                Assert.Matches(@"^You \d+ – \d+ AI \(Random\)", tally.GetProperty("text").GetString());

                var store = new AdvisorStore(Path.Combine(_folder, "data"));
                var records = store.LoadAiResults();
                Assert.Equal(game, records.Count);
                var r = records[^1];
                Assert.Equal("random", r.Spec);
                Assert.Equal(game == 1 ? 1 : 0, r.Takebacks);
                var result = server.Session!.State.Result!;
                Assert.Equal(result.IsDraw ? "draw" : result.Winner == server.Session.Seat ? "you" : "ai", r.Winner);
                Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsync("api/undo", null)).StatusCode); // the result stands
            }
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    /// <summary>
    /// A Run's network as the opponent (the tiny test network, as a Run would list it): the new-game screen lists it,
    /// the game starts, the AI answers with a network search, and a restart resumes the game against the same network.
    /// A spec that can't be built is refused before the game starts.
    /// </summary>
    [Fact]
    public async Task A_Runs_network_plays_and_a_restart_resumes_the_game()
    {
        var runs = MakeRuns();
        var (app, http, server) = await Start(runs);
        string log;
        try
        {
            var opponents = await ServerTests.Json(await http.GetAsync("api/play/opponents"));
            var best = opponents.GetProperty("opponents")[0];
            Assert.Equal("pilot-x Gen 2", best.GetProperty("label").GetString());

            var bad = await http.PostAsJsonAsync("api/new", new { messe = false, liberty = false, rule2015 = false, seat = 0, vsAi = true, opponent = "neural:missing.onnx" });
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
            Assert.Contains("Can't play against", (await ServerTests.Json(bad)).GetProperty("error").GetString());

            var created = await http.PostAsJsonAsync("api/new", new
            {
                messe = false, liberty = false, rule2015 = false, seat = 1, vsAi = true,
                opponent = best.GetProperty("spec").GetString(), opponentLabel = "pilot-x Gen 2", aiSimulations = 16,
            });
            Assert.Equal(HttpStatusCode.OK, created.StatusCode);
            var rng = new Rng(3);
            for (var i = 0; i < 6; i++)
            {
                await server.AiSettled();
                Assert.Equal(JsonValueKind.Null, (await ServerTests.Json(await http.GetAsync("api/state"))).GetProperty("play").GetProperty("error").ValueKind);
                Assert.Equal(HttpStatusCode.OK, (await http.PostAsJsonAsync("api/entry", new { line = OwnerLine(server.Session!, rng) })).StatusCode);
            }
            await server.AiSettled();
            var state = await ServerTests.Json(await http.GetAsync("api/state"));
            Assert.StartsWith("AI played: ", state.GetProperty("play").GetProperty("lastAi").GetString());
            Assert.Contains(server.Session!.Log, e => e is MoveEntry { Player: 0 });
            log = server.Session!.Write();
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }

        (app, http, server) = await Start(runs);
        try
        {
            var state = await ServerTests.Json(await http.GetAsync("api/state"));
            Assert.Equal("pilot-x Gen 2", state.GetProperty("play").GetProperty("opponent").GetString());
            Assert.Equal(16, state.GetProperty("play").GetProperty("simulations").GetInt32());
            Assert.Equal(log, server.Session!.Write());
            Assert.Equal("P1 (AI)", state.GetProperty("game").GetProperty("players")[0].GetProperty("label").GetString());
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
