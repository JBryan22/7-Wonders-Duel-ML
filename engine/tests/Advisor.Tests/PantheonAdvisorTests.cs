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
/// The Advisor Window with Pantheon (spec-pantheon P-A9, ticket 12): whole Pantheon games entered by hand from one seat
/// (the opponent's private draws, placements and Divine Theater logged redacted, as the owner sees them), entered only
/// through what the view offers; Undo and rewind; advice for every Pantheon decision; the network chosen by the Ruleset;
/// and games against the AI, where the owner's view never holds the AI's private cards.
/// </summary>
public sealed class PantheonAdvisorTests : IDisposable
{
    readonly string _folder = Path.Combine(Path.GetTempPath(), "7wd-advisor-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    static readonly Ruleset[] PantheonRules =
    [
        new(Expansions: Expansions.Pantheon),
        new(Promos: Promos.Messe | Promos.Liberty, Expansions: Expansions.Pantheon),
        new(StartChoice.Rule2015, Promos.Liberty, Expansions.Pantheon),
        new(Promos: Promos.Messe, Expansions: Expansions.Pantheon),
    ];

    static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    static readonly DecisionKind[] PantheonDecisions =
    [
        DecisionKind.PlaceDivinity, DecisionKind.Offer, DecisionKind.Enki, DecisionKind.Snake, DecisionKind.Steal, DecisionKind.Hades,
        DecisionKind.Zeus, DecisionKind.Anubis, DecisionKind.Isis, DecisionKind.Ra, DecisionKind.Gate, DecisionKind.TheaterDeck,
        DecisionKind.Theater, DecisionKind.TheaterOrder, DecisionKind.Minerva, DecisionKind.NeptuneDiscard, DecisionKind.NeptuneApply,
    ];

    /// <summary>A true Pantheon game, dealt by a seeded Chance Source and played by random legal Moves (activations favoured).</summary>
    sealed class Table(Ruleset rules, ulong seed)
    {
        public readonly Game Game = new(rules);
        readonly RandomChanceSource _chance = new(seed);
        readonly Rng _rng = new(seed * 7919 + 3);

        public LogEntry Next()
        {
            var s = Game.State;
            if (s.Step == StepKind.Reveal) return _chance.Next(s);
            var moves = s.LegalMoves();
            // Activations are a small share of the take-card Moves: pick one often, so every Divinity's decision turns up.
            var activations = moves.Where(m => m.Kind == MoveKind.Activate).ToList();
            var move = activations.Count > 0 && _rng.Next(3) == 0 ? activations[_rng.Next(activations.Count)] : moves[_rng.Next(moves.Count)];
            return new MoveEntry(s.PlayerToDecide, move);
        }
    }

    /// <summary>
    /// Plays one true game and enters it in a session for <paramref name="seat"/>, each line as the owner sees it. The
    /// session logs the opponent's private Reveals by itself; every other line must be offered by the view. Calls
    /// <paramref name="atStep"/> before each entry.
    /// </summary>
    static (AdvisorSession Session, Game Truth) EnterByHand(Ruleset rules, ulong seed, int seat, Action<AdvisorSession, LogEntry>? atStep = null)
    {
        var table = new Table(rules, seed);
        var session = AdvisorSession.New(rules, seat);
        while (!table.Game.State.IsOver)
        {
            var entry = table.Next();
            var seen = MoveLog.AsSeenBy(entry, seat);
            var index = table.Game.Log.Count;
            atStep?.Invoke(session, seen);
            table.Game.Apply(entry);
            if (session.Log.Count > index)
            {
                // Logged by the session itself: the opponent's private draw or Divine Theater, redacted.
                Assert.Equal(MoveLog.Format(seen), MoveLog.Format(session.Log[index]));
                Assert.NotSame(entry, seen);
            }
            else
            {
                var line = MoveLog.Format(seen);
                AssertOffered(session, line);
                session.Enter(line);
            }
            // The session may be ahead by the opponent's private Reveal it logged by itself; compare once they agree.
            Assert.All(session.Log.Skip(table.Game.Log.Count), e => Assert.True(e is LibraryReveal { Redacted: true } or DivinityDrawReveal { Redacted: true } or TheaterReveal { Redacted: true }));
            if (session.Log.Count > table.Game.Log.Count) continue;
            Assert.Equal(table.Game.State.Observe(seat).State.ToBytes(), session.State.Observe(seat).State.ToBytes());
            Assert.True(session.State.Observe(seat).State.SameAs(session.State), "the owner's state holds something their Observation hides");
        }
        return (session, table.Game);
    }

    /// <summary>The line is something the page offers: a Reveal picker's choices, a tile, a card action, or an Activate button.</summary>
    static void AssertOffered(AdvisorSession session, string line)
    {
        var view = BoardView.Build(session, "x");
        JsonSerializer.Serialize(view, Web); // the page's JSON can always be written
        if (view.Reveal is { } r)
        {
            Assert.StartsWith(r.LinePrefix, line);
            var rest = line[r.LinePrefix.Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            switch (r.Kind)
            {
                case "fields":
                    var at = 0;
                    foreach (var f in r.Fields!)
                    {
                        if (f.Key is not null) Assert.Equal(f.Key, rest[at++]);
                        var value = rest[at++];
                        Assert.Contains(f.Options, o => o.Id == value && o.Enabled);
                    }
                    Assert.Equal(rest.Length, at);
                    return;
                case "guilds":
                    Assert.Equal(r.Count, rest.Length);
                    Assert.All(rest, slot => Assert.Contains(r.Options, o => o.Id == slot));
                    return;
                case "slots":
                    // Flips: each named on its pulsing slot from the candidates.
                    for (var i = 0; i + 1 < rest.Length; i += 2)
                    {
                        if (rest[i].StartsWith("age")) i++;
                        var slot = view.Pyramid!.Slots.Single(x => x.Name == rest[i]);
                        Assert.True(slot.Pulse);
                        Assert.Contains(slot.Candidates!, c => c.Id == rest[i + 1] && c.Enabled);
                    }
                    return;
                default:
                    Assert.Equal(r.Count, rest.Length);
                    Assert.All(rest, id => Assert.Contains(r.Options, o => o.Id == id && o.Enabled));
                    return;
            }
        }
        var lines = new HashSet<string>();
        foreach (var t in view.Decision?.Tiles ?? []) lines.Add(t.Line);
        foreach (var slot in view.Pyramid?.Slots ?? [])
            foreach (var a in slot.Actions.Where(a => a.Enabled)) lines.Add(a.Line);
        foreach (var space in view.Pantheon?.Spaces ?? [])
            if (space.Activate is { Enabled: true } a) lines.Add(a.Line);
        // A slot-named entry is accepted too, but the view offers card names: compare in the session's own form.
        var logged = MoveLog.Format(MoveLog.ParseEntry(line, session.State));
        Assert.Contains(logged, lines);
    }

    [Fact]
    public void Pantheon_games_entered_by_hand_from_either_seat_replay_to_the_same_state()
    {
        var redacted = 0;
        for (ulong seed = 0; seed < 24; seed++)
        {
            var rules = PantheonRules[seed % (ulong)PantheonRules.Length];
            var seat = (int)(seed / 4 % 2);
            var (session, truth) = EnterByHand(rules, seed, seat);
            Assert.True(session.State.IsOver);
            Assert.Equal(truth.State.Result!.P1.Total, session.State.Result!.P1.Total);
            Assert.Equal(truth.State.Result!.P2.Total, session.State.Result!.P2.Total);
            Assert.Equal(truth.State.Result!.Winner, session.State.Result!.Winner);
            Assert.Equal(MoveLog.Parse(session.Write()).State.ToBytes(), session.State.ToBytes());
            var loaded = AdvisorSession.Load(session.Write());
            Assert.Equal(session.Write(), loaded.Write());
            Assert.Equal(session.State.ToBytes(), loaded.State.ToBytes());
            redacted += session.Log.Count(e => e is DivinityDrawReveal { Redacted: true } or MoveEntry { Move.Kind: MoveKind.Divinity, Move.Divinity: PantheonState.Hidden });
            Assert.DoesNotContain(session.Log, e => e is DivinityDrawReveal { Redacted: false } d && d.Player != seat);
            Assert.DoesNotContain(session.Log, e => e is TheaterReveal { Redacted: false } t && t.Player != seat);
        }
        Assert.True(redacted > 20, $"only {redacted} redacted lines");
    }

    [Fact]
    public void Undo_and_rewind_in_Pantheon_games_equal_replaying_the_truncated_log()
    {
        var checks = 0;
        for (ulong seed = 100; seed < 112; seed++)
        {
            var rules = PantheonRules[seed % (ulong)PantheonRules.Length];
            var (session, _) = EnterByHand(rules, seed, (int)(seed % 2));
            var text = session.Write();
            var rng = new Rng(seed);
            for (var i = 0; i < 12; i++)
            {
                var copy = AdvisorSession.Load(text);
                var before = copy.Log.ToList();
                int kept;
                if (i % 3 == 0)
                {
                    // Undo several times from a random point.
                    copy.RewindTo(rng.Next(before.Count + 1));
                    before = copy.Log.ToList();
                    Assert.True(copy.Undo() || before.Count == 0);
                    var lastMove = before.FindLastIndex(e => e is MoveEntry);
                    kept = lastMove >= 0 ? lastMove : Math.Max(0, before.Count - 1);
                }
                else
                {
                    kept = rng.Next(before.Count + 1);
                    copy.RewindTo(kept);
                }
                Assert.Equal(before.Take(kept).Select(MoveLog.Format), copy.Log.Take(kept).Select(MoveLog.Format));
                // Anything after the cut is the opponent's private Reveal the session logs by itself.
                Assert.All(copy.Log.Skip(kept), e => Assert.True(e is LibraryReveal { Redacted: true } or DivinityDrawReveal { Redacted: true } or TheaterReveal { Redacted: true }));
                var replay = new Game(rules);
                foreach (var e in copy.Log) replay.Apply(e);
                Assert.Equal(replay.State.ToBytes(), copy.State.ToBytes());
                Assert.Equal(MoveLog.Parse(copy.Write()).State.ToBytes(), copy.State.ToBytes());
                BoardView.Build(copy, "x");
                checks++;
            }
        }
        Assert.True(checks >= 100);
    }

    [Fact]
    public void The_opponent_s_private_lines_are_refused_named_and_the_owner_s_refused_redacted()
    {
        // Find a game where P2 (the opponent of an owner at P1) draws Divinities, and one where P1 does.
        for (ulong seed = 0; seed < 40; seed++)
        {
            var refusedOpponent = false;
            var refusedOwn = false;
            EnterByHand(PantheonRules[0], seed, 0, (session, seen) =>
            {
                var s = session.State;
                if (s.Step == StepKind.Decision && s.Pending(0).Kind == DecisionKind.PlaceDivinity && s.Pantheon!.DrawHidden
                    && s.Pending(0).Player == 1 && !refusedOpponent)
                {
                    var m = (Mythology)s.Pending(0).Wonder;
                    var named = PantheonData.MythologyDef(m).Divinities.First();
                    var e = Assert.Throws<EntryException>(() => session.Enter($"P2 divinity {PantheonData.Divinities[named].Id} 1"));
                    Assert.Contains("face down", e.Message);
                    refusedOpponent = true;
                }
                if (seen is DivinityDrawReveal { Player: 0 } own && !refusedOwn)
                {
                    var e = Assert.Throws<EntryException>(() => session.Enter(
                        MoveLog.Format(DivinityDrawReveal.Hidden(0, own.Mythology, own.Divinities.Length))));
                    Assert.Contains("Name the Divinities", e.Message);
                    refusedOwn = true;
                }
            });
            if (refusedOpponent && refusedOwn) return;
        }
        Assert.Fail("no game had both players draw Divinities");
    }

    /// <summary>The ticket's advice check: MCTS (no Pantheon network yet) answers every Pantheon decision, every row enterable.</summary>
    [Fact]
    public void Advice_works_for_every_Pantheon_decision()
    {
        var seen = new HashSet<DecisionKind>();
        var activations = 0;
        for (ulong seed = 0; seed < 400 && (seen.Count < PantheonDecisions.Length || activations < 3); seed++)
        {
            var rules = PantheonRules[seed % (ulong)PantheonRules.Length];
            var seat = (int)(seed % 2);
            EnterByHand(rules, seed, seat, (session, _) =>
            {
                var s = session.State;
                if (s.Step != StepKind.Decision || s.PlayerToDecide != seat) return;
                var kind = s.Pending(0).Kind;
                var activate = kind == DecisionKind.TakeCard && s.LegalMoves().Any(m => m.Kind == MoveKind.Activate);
                if (!(PantheonDecisions.Contains(kind) && seen.Add(kind)) && !(activate && activations < 3)) return;
                Assert.Null(AdviceService.WhyNoAdvice(session));
                var outcome = AdviceSearch.Run(s.Observe(seat), "mcts", 40, seed);
                var rows = AdviceTable.Rank(s, outcome);
                Assert.NotEmpty(rows);
                foreach (var row in rows)
                {
                    Assert.NotEmpty(row.Reasons);
                    Assert.False(string.IsNullOrWhiteSpace(row.Label));
                    var entry = (MoveEntry)MoveLog.ParseEntry(row.Line, s);
                    Assert.Null(s.WhyIllegal(entry.Move));
                }
                if (activate)
                {
                    activations++;
                    Assert.Contains(rows, r => r.Move.Kind == MoveKind.Activate);
                    Assert.Contains(rows, r => r.Move.Kind == MoveKind.Activate && r.Label.StartsWith("Activate "));
                }
            });
        }
        Assert.Equal(PantheonDecisions.Order(), seen.Order());
        Assert.True(activations >= 3);
    }

    [Fact]
    public async Task The_advice_network_is_chosen_by_the_game_s_rules()
    {
        var tiny = Path.Combine(AppContext.BaseDirectory, "Models", "tiny.onnx");      // Encoding v1
        var v2 = Path.Combine(AppContext.BaseDirectory, "Models", "surgery-child.onnx"); // Encoding v2
        var pantheon = new Ruleset(Expansions: Expansions.Pantheon);

        // A base network advises base games; Pantheon games fall back to the MCTS Anchor.
        var settings = new AdviceSettings($"neural:{tiny}");
        Assert.Equal($"neural:{tiny}", settings.AgentFor(Ruleset.Default));
        Assert.Equal("mcts", settings.AgentFor(pantheon));
        // A Pantheon network given for Pantheon games; a v2 network as the only agent advises both.
        settings = new AdviceSettings($"neural:{tiny}", PantheonAgent: $"neural:{v2}");
        settings.Validate();
        Assert.Equal($"neural:{v2}", settings.AgentFor(pantheon));
        Assert.Equal($"neural:{tiny}", settings.AgentFor(Ruleset.Default));
        Assert.Equal($"neural:{v2}", new AdviceSettings($"neural:{v2}").AgentFor(pantheon));
        // A base network can't be the Pantheon one.
        Assert.Throws<NotSupportedException>(() => new AdviceSettings("mcts", PantheonAgent: $"neural:{tiny}").Validate());

        // The service searches a Pantheon position with the Pantheon network (outcome bars included).
        using var service = new AdviceService(new AdviceSettings($"neural:{tiny}", 24, $"neural:{v2}"));
        AdvisorSession? position = null;
        EnterByHand(pantheon, 3, 0, (session, _) =>
        {
            if (position is null && session.State.Age == 2 && session.State.Step == StepKind.Decision && session.State.PlayerToDecide == 0)
                position = AdvisorSession.Load(session.Write());
        });
        Assert.NotNull(position);
        service.Request(position!, longer: false);
        await service.Running;
        var view = service.View(position);
        Assert.Equal("ready", view.Status);
        Assert.Equal($"neural:{v2}", view.Agent);
        Assert.True(view.HasOutcome);
        Assert.NotEmpty(view.Rows);
    }

    /// <summary>
    /// Pantheon games against the AI in both seats: the program deals from the true game, the AI plays legally from its
    /// own Observation, the owner's state is exactly their Observation of the true game (the AI's draws, face-down
    /// Divinities, put-back cards and Divine Theater stay '?'), the banner never names a private card, and the saved
    /// log (with the AI's secrets) resumes to the same game.
    /// </summary>
    [Fact]
    public void Pantheon_games_against_the_AI_complete_and_the_owner_never_sees_the_AI_s_private_cards()
    {
        var secrets = 0;
        for (ulong seed = 0; seed < 16; seed++)
        {
            var rules = PantheonRules[seed % (ulong)PantheonRules.Length];
            var seat = (int)(seed / 2 % 2);
            var ai = new AiOpponent(seed % 4 == 3 ? "heuristic" : "random", "AI", 50, seed * 31 + 7);
            var session = AdvisorSession.New(rules, seat, ai);
            var rng = new Rng(seed + 1000);
            while (!session.State.IsOver)
            {
                Assert.NotEqual(StepKind.Reveal, session.State.Step);
                var truth = session.TrueGame().State;
                Assert.Equal(truth.Observe(seat).State.ToBytes(), session.State.Observe(seat).State.ToBytes());
                Assert.True(session.State.Observe(seat).State.SameAs(session.State), "the owner's state holds something their Observation hides");
                var json = JsonSerializer.Serialize(BoardView.Build(session, "x"), Web);
                foreach (var line in Secrets(session)) Assert.DoesNotContain(line, json);
                foreach (var m in MoveText.SinceOwnersMove(session))
                    Assert.False(m.Text.Contains("placed") && !m.Text.Contains("face down"), m.Text);

                if (session.AiToMove)
                {
                    var observation = session.AiObservation();
                    // (The AI's own Great Library draw is drawn for its eyes only: then its Observation holds the draw.)
                    if (!session.State.LibraryDrawHidden)
                        Assert.Equal(truth.Observe(session.AiSeat).State.ToBytes(), observation.State.ToBytes());
                    var move = AiPlayer.Choose(observation, session.Ai!, session.AiSeed);
                    Assert.Contains(move, observation.LegalMoves());
                    Assert.Contains(move, truth.LegalMoves());
                    var described = AiPlayer.Describe(session.State, ((MoveEntry)MoveLog.AsSeenBy(new MoveEntry(session.AiSeat, move), seat)).Move);
                    if (move.Kind == MoveKind.Divinity) Assert.DoesNotContain(PantheonData.Divinities[move.Divinity].Name, described);
                    session.EnterAiMove(move);
                }
                else
                {
                    var moves = session.State.Observe(seat).LegalMoves();
                    var activations = moves.Where(m => m.Kind == MoveKind.Activate).ToList();
                    var move = activations.Count > 0 && rng.Next(2) == 0 ? activations[rng.Next(activations.Count)] : moves[rng.Next(moves.Count)];
                    var line = MoveLog.Format(new MoveEntry(seat, move));
                    AssertOffered(session, line);
                    session.EnterOwn([line]);
                }
                if (rng.Next(25) == 0)
                {
                    // A restart mid-game: the saved log (with the AI's secrets) resumes to the same true game.
                    var resumed = AdvisorSession.Load(session.Write());
                    Assert.Equal(session.Write(), resumed.Write());
                    Assert.Equal(session.TrueGame().State.ToBytes(), resumed.TrueGame().State.ToBytes());
                }
            }
            secrets += Secrets(session).Count;
            Assert.Equal(MoveLog.Parse(session.Write()).State.ToBytes(), session.State.ToBytes());
            Assert.Equal(session.State.Result!.P1.Total, session.TrueGame().State.Result!.P1.Total);
            var loaded = AdvisorSession.Load(session.Write());
            Assert.Equal(session.Write(), loaded.Write());
            Assert.Equal(session.TrueGame().State.ToBytes(), loaded.TrueGame().State.ToBytes());
        }
        Assert.True(secrets > 10, $"only {secrets} AI secrets were kept");
    }

    [Fact]
    public void Taking_back_a_Move_against_the_AI_drops_the_AI_s_later_secrets()
    {
        var session = AdvisorSession.New(PantheonRules[0], 0, new AiOpponent("random", "AI", 10, 5));
        var rng = new Rng(5);
        // Play until the AI has kept a secret, then take back the owner's Moves until it is gone.
        while (Secrets(session).Count == 0 && !session.State.IsOver)
        {
            if (session.AiToMove) session.EnterAiMove(AiPlayer.Choose(session.AiObservation(), session.Ai!, session.AiSeed));
            else
            {
                var moves = session.State.LegalMoves();
                session.EnterOwn([MoveLog.Format(new MoveEntry(0, moves[rng.Next(moves.Count)]))]);
            }
        }
        Assert.NotEmpty(Secrets(session));
        while (Secrets(session).Count > 0) session.TakeBack();
        Assert.DoesNotContain(AdvisorSession.SecretPrefix, session.Write());
        Assert.Equal(session.State.ToBytes(), session.TrueGame().State.ToBytes());
    }

    /// <summary>
    /// Through the HTTP API as the page uses it: the Pantheon box starts a Pantheon game (by hand and against the AI);
    /// the opponent list for Pantheon holds only Pantheon Runs; a base network is refused as a Pantheon opponent; a
    /// Pantheon game against Random is played to the end with the "AI played" line never naming the AI's private cards.
    /// </summary>
    [Fact]
    public async Task A_Pantheon_game_over_HTTP_against_Random()
    {
        var runs = Path.Combine(_folder, "runs");
        var tiny = Path.Combine(AppContext.BaseDirectory, "Models", "tiny.onnx");
        var v2 = Path.Combine(AppContext.BaseDirectory, "Models", "surgery-child.onnx");
        foreach (var (name, model, version) in new[] { ("base-run", tiny, 0), ("pantheon-run", v2, 2) })
        {
            Directory.CreateDirectory(Path.Combine(runs, name, "net"));
            File.Copy(model, Path.Combine(runs, name, "net", "gen-0001.onnx"));
            File.WriteAllText(Path.Combine(runs, name, "best.json"), """{"generation": 1, "model": "net/gen-0001.onnx"}""");
            if (version > 0) File.WriteAllText(Path.Combine(runs, name, "run.json"), $$"""{"format": "7wd-run-1", "encoding_version": {{version}}}""");
        }
        var app = AdvisorServer.Build(new AdvisorOptions(Path.Combine(_folder, "data"), Port: 0, RunsFolder: runs, AiPause: TimeSpan.Zero,
            TableSource: () => new NoTable(), TtsPolling: false));
        await app.StartAsync();
        var http = new HttpClient { BaseAddress = new Uri(app.Urls.Single().TrimEnd('/') + "/") };
        var server = app.Services.GetRequiredService<AdvisorServer>();
        try
        {
            var list = await ServerTests.Json(await http.GetAsync("api/play/opponents"));
            var all = list.GetProperty("opponents").EnumerateArray().Select(o => o.GetProperty("group").GetString()).ToList();
            var pan = list.GetProperty("pantheonOpponents").EnumerateArray().Select(o => o.GetProperty("group").GetString()).ToList();
            Assert.Contains("base-run", all);
            Assert.Contains("pantheon-run", all);
            Assert.DoesNotContain("base-run", pan);
            Assert.Contains("pantheon-run", pan);

            // By hand: the rules header and the Pantheon panel.
            var hand = await ServerTests.Json(await http.PostAsJsonAsync("api/new", new { messe = false, liberty = false, rule2015 = false, seat = 0, pantheon = true }));
            Assert.Contains("expansion=pantheon", hand.GetProperty("game").GetProperty("rules").GetString());
            Assert.True(hand.GetProperty("game").GetProperty("pantheonGame").GetBoolean());
            Assert.Equal(6, hand.GetProperty("game").GetProperty("pantheon").GetProperty("spaces").GetArrayLength());
            Assert.True(new AdvisorStore(Path.Combine(_folder, "data")).LoadSettings().Pantheon);
            // Following the TTS table works in a Pantheon game as in a base one (ticket 13): here there is no table to read.
            var tts = await ServerTests.Json(await http.PostAsJsonAsync("api/tts", new { follow = true }));
            Assert.Equal("reading", tts.GetProperty("tts").GetProperty("status").GetString());
            await http.PostAsJsonAsync("api/tts", new { follow = false });

            // A base network can't play Pantheon: refused before the game starts.
            var refused = await http.PostAsJsonAsync("api/new", new
            {
                messe = false, liberty = false, rule2015 = false, seat = 0, pantheon = true, vsAi = true,
                opponent = Opponents.NetworkSpec(Path.Combine(runs, "base-run", "net", "gen-0001.onnx")), opponentLabel = "base",
            });
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Contains("Encoding", (await ServerTests.Json(refused)).GetProperty("error").GetString());

            var created = await http.PostAsJsonAsync("api/new", new
            {
                messe = true, liberty = false, rule2015 = false, seat = 1, pantheon = true, vsAi = true, opponent = "random", opponentLabel = "Random",
            });
            Assert.Equal(HttpStatusCode.OK, created.StatusCode);
            var rng = new Rng(9);
            while (true)
            {
                await server.AiSettled();
                var state = await ServerTests.Json(await http.GetAsync("api/state"));
                var text = state.GetRawText();
                var session = server.Session!;
                foreach (var secret in Secrets(session))
                {
                    Assert.DoesNotContain(secret, text);
                    var entry = MoveLog.ParseEntry(secret, session.TrueGame().State);
                    if (entry is MoveEntry { Move.Kind: MoveKind.Divinity } m && state.GetProperty("play").GetProperty("lastAi").GetString() is { } last)
                        Assert.DoesNotContain($"{PantheonData.Divinities[m.Move.Divinity].Name} on", last);
                }
                if (session.State.IsOver) break;
                var moves = session.State.LegalMoves();
                var activations = moves.Where(x => x.Kind == MoveKind.Activate).ToList();
                var move = activations.Count > 0 && rng.Next(2) == 0 ? activations[rng.Next(activations.Count)] : moves[rng.Next(moves.Count)];
                var answer = await http.PostAsJsonAsync("api/entry", new { line = MoveLog.Format(new MoveEntry(session.Seat, move)) });
                Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
            }
            Assert.NotEmpty(Secrets(server.Session!));
            Assert.Single(new AdvisorStore(Path.Combine(_folder, "data")).LoadAiResults());
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public void The_catalog_describes_every_Pantheon_piece()
    {
        var pan = CardText.Catalog().Pantheon!;
        Assert.Equal(PantheonData.TempleCount, pan.Cards.Count);
        Assert.Equal(PantheonData.WonderCount, pan.Wonders.Count);
        Assert.Equal(PantheonData.TokenCount, pan.Tokens.Count);
        Assert.Equal(PantheonData.Divinities.Length, pan.Divinities.Count);
        foreach (var info in pan.Cards.Values.Concat(pan.Wonders.Values).Concat(pan.Tokens.Values).Concat(pan.Divinities.Values))
        {
            Assert.False(string.IsNullOrWhiteSpace(info.EffectIcons), info.Id);
            Assert.NotEmpty(info.Effect);
        }
        Assert.Equal("Grand Temple, Age III", pan.Cards["greek-grand-temple"].Type);
        Assert.Contains("2 coins less", string.Join(' ', pan.Wonders["sanctuary"].Effect));
        // Pantheon's legend icons are distinct from each other and from the base ones.
        var icons = CardText.Catalog().Legend.Select(l => l.Icon).Concat(pan.Legend.Select(l => l.Icon)).ToList();
        Assert.Equal(icons.Count, icons.Distinct().Count());
    }

    sealed class NoTable : SevenWondersDuel.Advisor.Tts.ITableSource
    {
        public Task<string> ReadAsync(CancellationToken cancel) => throw new SevenWondersDuel.Advisor.Tts.TtsException("No table (test)");
        public void Dispose() { }
    }

    /// <summary>The AI's true private lines as kept in the saved game.</summary>
    static List<string> Secrets(AdvisorSession session) =>
        session.Write().Split('\n').Where(l => l.StartsWith(AdvisorSession.SecretPrefix))
            .Select(l => System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(l.Split(' ')[^1]))).ToList();
}
