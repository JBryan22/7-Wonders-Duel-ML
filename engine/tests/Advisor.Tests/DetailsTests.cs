using System.Net;
using System.Net.Http.Json;
using SevenWondersDuel.Advisor;
using SevenWondersDuel.Agents;
using SevenWondersDuel.Engine;
using SevenWondersDuel.Engine.Data;
using Xunit.Abstractions;

namespace SevenWondersDuel.Advisor.Tests;

/// <summary>
/// The advice details (spec A8, ticket 31): reason tags, the expected line ("no line" when the search is too shallow),
/// the outcome bar from a network's outcome head, and the Statue of Liberty as keep/give pairs.
/// </summary>
public sealed class DetailsTests(ITestOutputHelper output) : IDisposable
{
    readonly string _folder = Path.Combine(Path.GetTempPath(), "7wd-advisor-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    static readonly Ruleset Liberty = new(Promos: Promos.Liberty);
    static readonly Ruleset BothPromos = new(Promos: Promos.Messe | Promos.Liberty);
    static string TinyModel => Path.Combine(AppContext.BaseDirectory, "Models", "tiny.onnx");

    static (Advice Advice, SearchOutcome Outcome) Search(AdvisorSession session, int simulations = 800, string agent = "mcts", ulong seed = 12345)
    {
        var outcome = AdviceSearch.Run(session.State.Observe(session.Seat), agent, simulations, seed);
        var advice = new Advice("", agent, outcome.Simulations, 0, AdviceTable.Rank(session.State, outcome), outcome.Line)
        {
            HasOutcome = outcome.HasOutcome,
        };
        return (advice, outcome);
    }

    /// <summary>The first position (over seeds) where the owner keeps a card of the Statue of Liberty.</summary>
    static AdvisorSession StatueKeep()
    {
        for (ulong seed = 1; seed < 400; seed++)
            foreach (var p in AdviceTests.OwnerDecisions(seed, (int)(seed % 2), rules: Liberty))
                if (p.State.Pending(0).Kind == DecisionKind.LibertyKeep) return p;
        throw new InvalidOperationException("No Statue of Liberty keep in 400 random games");
    }

    [Fact]
    public void Every_option_gets_reason_tags_and_they_describe_the_board_change()
    {
        int rows = 0, pairs = 0, shields = 0, uncovers = 0, faceDown = 0;
        foreach (var (seed, rules) in new[] { (21UL, Ruleset.Default), (22UL, BothPromos), (23UL, Ruleset.Default) })
        foreach (var session in AdviceTests.OwnerDecisions(seed, (int)(seed % 2), rules: rules))
        {
            var s = session.State;
            var me = s.PlayerToDecide;
            foreach (var move in s.LegalMoves())
            {
                var tags = Reasons.For(s, move);
                rows++;
                Assert.NotEmpty(tags);
                Assert.All(tags, t => Assert.False(string.IsNullOrWhiteSpace(t)));
                if (move.Kind != MoveKind.Build) continue;
                var card = GameData.Cards[move.Card];
                var after = s.Clone();
                after.Apply(move);
                if (after.IsOver) continue;
                if (card.Science is { } sym && s.SymbolCount(me, sym) == 1)
                {
                    Assert.Contains(tags, t => t.StartsWith($"completes your {sym.ToString().ToLowerInvariant()} pair: you pick a Progress token"));
                    pairs++;
                }
                if (card.Shields > 0 && DerivedFeatures.DistanceToCapital(after, 1 - me) < DerivedFeatures.DistanceToCapital(s, 1 - me))
                {
                    Assert.Contains(tags, t => t.Contains(" shield") && t.Contains($"pawn {DerivedFeatures.DistanceToCapital(after, 1 - me)} from their capital"));
                    shields++;
                }
                if (tags.Any(t => t.StartsWith("uncovers ") && t.EndsWith(" for them"))) uncovers++;
                if (tags.Any(t => t.Contains("face-down card"))) faceDown++;
            }
        }
        output.WriteLine($"{rows} options tagged; pairs {pairs}, shields {shields}, uncovers {uncovers}, face-down {faceDown}");
        Assert.True(pairs > 0 && shields > 0 && uncovers > 0 && faceDown > 0);
    }

    [Fact]
    public void Reason_tags_never_name_a_face_down_card()
    {
        // Tags come from the owner's Observation: a card the Move uncovers face down is "a face-down card".
        foreach (var session in AdviceTests.OwnerDecisions(24, 0))
        {
            var s = session.State;
            var observed = s.Observe(session.Seat).State;
            foreach (var move in s.LegalMoves().Where(m => m.TakesCard))
            {
                var tags = Reasons.For(observed, move);
                var full = s.Clone();
                full.Apply(move);
                for (var slot = 0; slot < GameState.SlotCount; slot++)
                    if (s.Age == full.Age && full.IsOccupied(slot) && full.IsAccessible(slot) && !s.IsAccessible(slot)
                        && observed.SlotCard(slot) < 0 && full.SlotCard(slot) >= 0)
                        Assert.DoesNotContain(tags, t => t.Contains(GameData.Cards[full.SlotCard(slot)].Name));
            }
        }
    }

    [Fact]
    public void A_draft_pick_and_a_token_say_what_they_bring()
    {
        var session = AdvisorSession.New(Ruleset.Default, 0);
        foreach (var line in new[] { "reveal first P1", "reveal tokens agriculture economy law strategy urbanism", "reveal draft pyramids sphinx colossus appian-way" })
            session.Enter(line);
        var tags = Reasons.For(session.State, Move.Draft(GameData.WonderIndex("colossus")));
        Assert.Contains(tags, t => t.StartsWith("The Colossus: ") && t.Contains("2 shields"));

        // Every Progress token pick says what the token does (now, through the board change, or later).
        var picks = 0;
        for (ulong seed = 30; seed < 60 && picks < 20; seed++)
            foreach (var p in AdviceTests.OwnerDecisions(seed, 0).Where(p => p.State.Pending(0).Kind == DecisionKind.ChooseToken))
                foreach (var move in p.State.LegalMoves())
                {
                    picks++;
                    var token = GameData.Tokens[move.Token];
                    var tokenTags = Reasons.For(p.State, move);
                    if (Reasons.TokenEffect(token.Id) is { } effect) Assert.Contains(effect, tokenTags);
                    if (token.Coins > 0) Assert.Contains(tokenTags, t => t.StartsWith($"+{token.Coins} coins") || t.Contains("wins"));
                }
        Assert.True(picks > 0);
    }

    [Fact]
    public void The_expected_line_starts_with_the_row_and_says_no_line_when_the_search_is_too_shallow()
    {
        int withLine = 0, chance = 0, markers = 0, positions = 0;
        foreach (var session in AdviceTests.OwnerDecisions(25, 1, minMoves: 3).Where((_, i) => i % 3 == 0))
        {
            positions++;
            var (advice, outcome) = Search(session);
            var best = advice.Best;
            foreach (var row in advice.Rows.Take(AdviceTable.MaxRows))
            {
                if (row.Expected.Count == 0) { Assert.False(row.HasLine); continue; }
                var first = row.Expected[0];
                Assert.True(first.Own);
                Assert.True(first.You);
                Assert.Equal(row.Label, first.Label);
                Assert.Equal(row.Slot, first.Slot);
                Assert.Equal(row.HasLine, row.Expected.Count > 1);
                foreach (var step in row.Expected)
                {
                    Assert.InRange(step.Visits, MctsAgent.MinLineVisits, 800);
                    if (step.AfterReveal) chance++;
                    if (step.Slot is { } slot)
                    {
                        markers++;
                        Assert.True(session.State.SlotCard(slot) >= 0, "Markers only go on cards face up now");
                    }
                    else if (step.Label.Contains("newly revealed")) Assert.True(step.AfterReveal || !step.Own);
                }
                if (row.HasLine) withLine++;
            }

            // The best row's line is the search's principal line.
            Assert.Equal(outcome.Line.Select(l => (l.Player, l.Visits)), best.Expected.Select(e => (e.Player, e.Visits)));

            // Ten simulations are far too few for a line: every row says "no line".
            var (shallow, _) = Search(session, simulations: 10);
            Assert.All(shallow.Rows, r => Assert.False(r.HasLine));
        }
        output.WriteLine($"{positions} positions: {withLine} rows with a line, {chance} steps after a chance step, {markers} board markers");
        Assert.True(withLine > 0 && chance > 0 && markers > 0);
    }

    [Fact]
    public void MCTS_has_no_outcome_bar_and_a_network_has_one_per_row()
    {
        var session = AdviceTests.OwnerDecisions(26, 0, minMoves: 3)[3];
        var (mcts, _) = Search(session);
        Assert.False(mcts.HasOutcome);
        Assert.All(mcts.Rows, r => Assert.Null(r.Outcome));

        foreach (var spec in new[] { $"neural:{TinyModel}", $"neural:{TinyModel},mode=classic" })
        {
            var (advice, _) = Search(session, 200, spec);
            Assert.True(advice.HasOutcome);
            foreach (var row in advice.Rows.Where(r => r.Visits > 0))
            {
                Assert.NotNull(row.Outcome);
                Assert.Equal(NetworkEncoding.OutcomeClasses.Length, row.Outcome!.Length);
                Assert.Equal(1.0, row.Outcome.Sum(), 6);
                Assert.All(row.Outcome, p => Assert.InRange(p, 0, 1));
            }
        }
        var (uniform, _) = Search(session, 200, "neural:uniform");
        Assert.False(uniform.HasOutcome);
        Assert.All(uniform.Rows, r => Assert.Null(r.Outcome));
    }

    [Fact]
    public void The_Statue_of_Liberty_is_advised_as_six_keep_give_pairs()
    {
        var session = StatueKeep();
        var s = session.State;
        var (advice, outcome) = Search(session);
        Assert.Equal(6, advice.Rows.Count);
        Assert.Equal(1.0, advice.Rows.Sum(r => r.Share), 6);
        Assert.True(advice.Best.Best);
        Assert.Equal(outcome.Best, advice.Best.Move);
        Assert.Equal(outcome.BestThen, advice.Best.Then);
        Assert.Equal(6, advice.Rows.Select(r => r.Key).Distinct().Count());

        // A pair's share is the keep's share times the give's share under it.
        var keeps = new MctsAgent(MctsSettings.Default with { Simulations = 800 }, 12345).Search(s.Observe(session.Seat));
        foreach (var keep in keeps.Moves)
            Assert.Equal(keep.VisitShare, advice.Rows.Where(r => r.Move == keep.Move).Sum(r => r.Share), 9);

        foreach (var row in advice.Rows)
        {
            Assert.StartsWith($"Keep {GameData.Cards[row.Move.Card].Name}, give ", row.Label);
            Assert.Equal(MoveKind.LibertyGive, row.Then!.Value.Kind);
            Assert.NotEmpty(row.Reasons);
            var copy = AdvisorSession.Load(session.Write());
            copy.Enter([row.Line, row.ThenLine!]);
            Assert.Equal(session.Log.Count(e => e is MoveEntry) + 2, copy.Log.Count(e => e is MoveEntry));
        }

        // The tray offers the same 6 pairs as tiles; the service's share map knows each of them.
        var tiles = BoardView.Build(session, "x").Decision!.Tiles;
        Assert.Equal(advice.Rows.Select(r => r.Key).Order(), tiles.Select(t => $"{t.Line}\n{t.Then}").Order());
    }

    [Fact]
    public void A_pair_is_entered_whole_or_not_at_all()
    {
        var session = StatueKeep();
        var before = session.Write();
        var keep = session.State.LegalMoves()[0];
        var p = $"P{session.Seat + 1}";
        Assert.Throws<EntryException>(() => session.Enter([$"{p} {keep}", $"{p} liberty-give no-such-card"]));
        Assert.Equal(before, session.Write());
    }

    [Fact]
    public async Task The_server_sends_the_details_and_enters_a_pair()
    {
        var session = StatueKeep();
        new AdvisorStore(_folder).SaveCurrent(session);
        var (app, http) = await ServerTests.Start(_folder);
        try
        {
            var advice = await ServerTests.Json(await http.PostAsJsonAsync("api/advice", new { longer = false }));
            for (var i = 0; i < 200 && advice.GetProperty("status").GetString() == "thinking"; i++)
            {
                await Task.Delay(50);
                advice = await ServerTests.Json(await http.GetAsync("api/advice"));
            }
            Assert.Equal("ready", advice.GetProperty("status").GetString());
            Assert.False(advice.GetProperty("hasOutcome").GetBoolean());
            Assert.Equal(7, advice.GetProperty("outcomeClasses").GetArrayLength());
            var rows = advice.GetProperty("rows");
            Assert.Equal(5, rows.GetArrayLength());
            var best = rows[0];
            Assert.NotEmpty(best.GetProperty("reasons").EnumerateArray());
            Assert.True(best.GetProperty("expected").GetArrayLength() >= 2);
            Assert.Contains("\n", best.GetProperty("key").GetString());
            Assert.True(advice.GetProperty("shares").TryGetProperty(best.GetProperty("key").GetString()!, out _));

            var entered = await http.PostAsJsonAsync("api/entry", new { line = best.GetProperty("line").GetString(), then = best.GetProperty("then").GetString() });
            Assert.Equal(HttpStatusCode.OK, entered.StatusCode);
            var log = (await ServerTests.Json(entered)).GetProperty("game").GetProperty("log");
            Assert.Equal(session.Log.Count + 2, log.GetArrayLength());
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
