using SevenWondersDuel.Advisor;
using SevenWondersDuel.Engine;
using SevenWondersDuel.Engine.Data;

namespace SevenWondersDuel.Advisor.Tests;

/// <summary>
/// The Advisor's Move Log handling (spec A8, ticket 29): entry line by line, Undo and rewind, the redacted opponent
/// Library draw and the friendly refusals. Games are entered the way the page enters them: one Move Log line per click.
/// </summary>
public class SessionTests
{
    static readonly Ruleset[] AllRules =
    [
        Ruleset.Default,
        new(Promos: Promos.Messe | Promos.Liberty),
        new(StartChoice.Rule2015, Promos.Liberty),
    ];

    /// <summary>The next line a table would produce: a Reveal from the seeded Chance Source or a random legal Move.</summary>
    static List<string> NextLines(AdvisorSession session, RandomChanceSource chance, Rng rng, bool splitFlips)
    {
        var s = session.State;
        if (s.Step == StepKind.Reveal)
        {
            var reveal = chance.Next(s);
            if (splitFlips && reveal is SlotReveal flips)
                return flips.Cards.Select(c => MoveLog.Format(new SlotReveal(flips.Age, flips.Opening, [c]))).ToList();
            return [MoveLog.Format(reveal)];
        }
        var moves = s.LegalMoves();
        return [$"P{s.PlayerToDecide + 1} {moves[rng.Next(moves.Count)]}"];
    }

    static AdvisorSession PlayThrough(ulong seed, Ruleset rules, int seat, bool splitFlips = false)
    {
        var session = AdvisorSession.New(rules, seat);
        var chance = new RandomChanceSource(seed);
        var rng = new Rng(seed * 7919 + 1);
        while (!session.State.IsOver)
            foreach (var line in NextLines(session, chance, rng, splitFlips))
                session.Enter(line);
        return session;
    }

    /// <summary>The Game State the Engine gets by replaying the log text from scratch, as `7wd replay` would.</summary>
    static byte[] Replayed(AdvisorSession session) => MoveLog.Parse(session.Write()).State.ToBytes();

    [Fact]
    public void Full_games_entered_line_by_line_end_and_replay_to_the_same_state()
    {
        for (ulong seed = 0; seed < 30; seed++)
        foreach (var rules in AllRules)
        {
            var session = PlayThrough(seed, rules, seat: (int)(seed % 2));
            Assert.True(session.State.IsOver);
            Assert.Equal(Replayed(session), session.State.ToBytes());
            var loaded = AdvisorSession.Load(session.Write());
            Assert.Equal(session.Seat, loaded.Seat);
            Assert.Equal(session.State.ToBytes(), loaded.State.ToBytes());
            Assert.Equal(session.Write(), loaded.Write());
        }
    }

    [Fact]
    public void Flips_named_one_card_at_a_time_merge_into_the_Engine_s_reveal_lines()
    {
        for (ulong seed = 0; seed < 10; seed++)
        {
            var whole = PlayThrough(seed, Ruleset.Default, 0);
            var split = PlayThrough(seed, Ruleset.Default, 0, splitFlips: true);
            Assert.Equal(whole.Write(), split.Write());
            Assert.Contains(whole.Log, e => e is SlotReveal { Opening: true, Cards.Length: > 1 });
        }
    }

    /// <summary>The ticket's check: Undo and rewind at random points always give the state of replaying the truncated log.</summary>
    [Fact]
    public void Undo_and_rewind_always_equal_replaying_the_truncated_log()
    {
        var checks = 0;
        for (ulong seed = 0; seed < 40; seed++)
        {
            var rules = AllRules[seed % (ulong)AllRules.Length];
            var session = AdvisorSession.New(rules, (int)(seed % 2));
            var chance = new RandomChanceSource(seed);
            var rng = new Rng(seed + 1000);
            var corrections = 0;
            while (!session.State.IsOver)
            {
                foreach (var line in NextLines(session, chance, rng, splitFlips: rng.Next(2) == 0)) session.Enter(line);
                if (corrections >= 12 || rng.Next(8) != 0) continue;
                corrections++;

                var before = session.Log.ToList();
                int kept;
                if (rng.Next(2) == 0)
                {
                    Assert.True(session.Undo());
                    var lastMove = before.FindLastIndex(e => e is MoveEntry);
                    kept = lastMove >= 0 ? lastMove : before.Count - 1;
                }
                else
                {
                    kept = rng.Next(before.Count + 1);
                    session.RewindTo(kept);
                }

                // The log is the old one cut at that point (plus the opponent's redacted Library draw when it is due).
                Assert.True(session.Log.Count >= kept);
                Assert.Equal(before.Take(kept).Select(MoveLog.Format), session.Log.Take(kept).Select(MoveLog.Format));
                Assert.All(session.Log.Skip(kept), e => Assert.True(e is LibraryReveal { Redacted: true }));

                var truncated = new Game(rules);
                foreach (var e in session.Log) truncated.Apply(e);
                Assert.Equal(truncated.State.ToBytes(), session.State.ToBytes());
                Assert.Equal(Replayed(session), session.State.ToBytes());
                checks++;
            }
            Assert.Equal(Replayed(session), session.State.ToBytes());
        }
        Assert.True(checks > 100, $"only {checks} corrections were checked");
    }

    [Fact]
    public void Undo_removes_the_last_Move_and_the_Reveals_after_it()
    {
        var session = AdvisorSession.New(Ruleset.Default, 0);
        Assert.False(session.Undo());
        session.Enter("reveal first P1");
        session.Enter("reveal tokens agriculture economy law strategy urbanism");
        Assert.True(session.Undo()); // before any Move: the last Reveal
        Assert.Single(session.Log);
        session.Enter("reveal tokens agriculture economy law strategy urbanism");
        session.Enter("reveal draft pyramids great-lighthouse circus-maximus piraeus");
        session.Enter("P1 draft pyramids");
        session.Enter("P2 draft piraeus");
        session.Enter("P2 draft circus-maximus");
        session.Enter("P1 draft great-lighthouse");
        session.Enter("reveal draft sphinx colossus appian-way temple-of-artemis");
        Assert.Equal(8, session.Log.Count);
        session.Undo();
        Assert.Equal(6, session.Log.Count);
        Assert.Equal("P2 draft circus-maximus", MoveLog.Format(session.Log[^1]));
        Assert.Equal(StepKind.Decision, session.State.Step);
        Assert.Equal(0, session.State.PlayerToDecide);
    }

    [Fact]
    public void The_opponent_s_Great_Library_draw_is_logged_redacted_and_the_owner_s_is_named()
    {
        var opponentDraws = 0;
        var ownDraws = 0;
        for (ulong seed = 0; seed < 400 && (opponentDraws < 3 || ownDraws < 3); seed++)
        {
            var session = PlayThrough(seed, Ruleset.Default, seat: 0);
            foreach (var reveal in session.Log.OfType<LibraryReveal>())
            {
                if (reveal.Player == 0) { Assert.False(reveal.Redacted); ownDraws++; }
                else { Assert.True(reveal.Redacted); opponentDraws++; }
            }
        }
        Assert.True(opponentDraws >= 3 && ownDraws >= 3, $"{opponentDraws} opponent and {ownDraws} own Library draws");
    }

    [Fact]
    public void Naming_the_opponent_s_Library_draw_is_refused_and_so_is_hiding_your_own()
    {
        var session = AdvisorSession.New(Ruleset.Default, 0);
        var e = Assert.Throws<EntryException>(() => session.Enter("reveal P2 library law strategy theology"));
        Assert.Contains("private", e.Message);
        e = Assert.Throws<EntryException>(() => session.Enter("reveal P1 library ? ? ?"));
        Assert.Contains("Name the 3", e.Message);
    }

    static AdvisorSession AgeOneStarted()
    {
        var session = AdvisorSession.New(Ruleset.Default, 0);
        foreach (var line in new[]
        {
            "reveal first P1",
            "reveal tokens agriculture economy law strategy urbanism",
            "reveal draft pyramids great-lighthouse circus-maximus piraeus",
            "P1 draft pyramids", "P2 draft piraeus", "P2 draft circus-maximus", "P1 draft great-lighthouse",
            "reveal draft sphinx colossus appian-way temple-of-artemis",
            "P2 draft sphinx", "P1 draft colossus", "P1 draft appian-way", "P2 draft temple-of-artemis",
        })
            session.Enter(line);
        return session;
    }

    [Fact]
    public void Revealing_a_card_already_on_the_table_is_refused_with_where_it_is()
    {
        var session = AgeOneStarted();
        session.Enter("reveal age1 0.0 theater");
        var e = Assert.Throws<EntryException>(() => session.Enter("reveal age1 0.1 theater"));
        Assert.Contains("already been revealed", e.Message);
        e = Assert.Throws<EntryException>(() => session.Enter("reveal age1 0.0 altar"));
        Assert.Contains("already shows Theater", e.Message);
        e = Assert.Throws<EntryException>(() => session.Enter("reveal age1 0.1 baths-not-a-card"));
        Assert.Contains("Unknown card", e.Message);
        e = Assert.Throws<EntryException>(() => session.Enter("reveal age1 0.1 senate"));
        Assert.Contains("is an Age 3 card, not Age 1", e.Message);
        Assert.Single(session.Log.OfType<SlotReveal>()); // nothing was added by the refusals

        var view = BoardView.Build(session, "x");
        var slot = view.Pyramid!.Slots.Single(s => s.Name == "0.1");
        Assert.True(slot.Pulse);
        var theater = slot.Candidates!.Single(c => c.Id == "theater");
        Assert.False(theater.Enabled);
        Assert.Equal("already in slot 0.0", theater.Reason);
    }

    [Fact]
    public void Nothing_but_the_pending_Reveal_can_be_entered()
    {
        var session = AgeOneStarted();
        var e = Assert.Throws<EntryException>(() => session.Enter("P1 build theater"));
        Assert.Contains("Reveal", e.Message);
    }

    [Fact]
    public void Illegal_moves_and_typos_get_a_readable_reason()
    {
        var session = AgeOneStarted();
        var layout = GameData.Layout(1);
        var faceUp = layout.Slots.Where(s => s.FaceUp).Select(s => layout.SlotName(s.Index)).ToArray();
        var cards = GameData.Cards.Where(c => c.Age == 1).Select(c => c.Id).Take(faceUp.Length).ToArray();
        session.Enter("reveal age1 " + string.Join(' ', faceUp.Zip(cards, (s, c) => $"{s} {c}")));
        Assert.Equal(StepKind.Decision, session.State.Step);

        var e = Assert.Throws<EntryException>(() => session.Enter("P2 discard " + cards[0]));
        Assert.Contains("P1's decision", e.Message);
        e = Assert.Throws<EntryException>(() => session.Enter("P1 dance " + cards[0]));
        Assert.Contains("Unknown move", e.Message);
        e = Assert.Throws<EntryException>(() => session.Enter("P1 discard " + cards[0])); // top row: covered
        Assert.StartsWith("Can't discard", e.Message);
        Assert.Contains("covered", e.Message);
    }

    [Fact]
    public void Card_actions_offer_build_discard_and_only_the_player_s_unbuilt_Wonders()
    {
        for (ulong seed = 0; seed < 20; seed++)
        {
            var session = AdvisorSession.New(Ruleset.Default, 0);
            var chance = new RandomChanceSource(seed);
            var rng = new Rng(seed);
            while (!session.State.IsOver)
            {
                var s = session.State;
                if (s.Step == StepKind.Decision && s.Pending(0).Kind == DecisionKind.TakeCard)
                {
                    var view = BoardView.Build(session, "x");
                    var player = s.PlayerToDecide;
                    var legal = s.LegalMoves().Select(m => $"P{player + 1} {m}").ToHashSet();
                    var offered = view.Pyramid!.Slots.SelectMany(x => x.Actions).ToList();
                    Assert.Equal(legal.OrderBy(x => x), offered.Where(a => a.Enabled).Select(a => a.Line).OrderBy(x => x));
                    Assert.All(offered.Where(a => !a.Enabled), a => Assert.False(string.IsNullOrEmpty(a.Reason)));
                    var wonders = GameState.WondersIn(s.BuildableWonders(player)).Select(w => GameData.Wonders[w].Name).ToHashSet();
                    Assert.All(offered.Where(a => a.Kind == "wonder"), a => Assert.Contains(a.Label[8..a.Label.LastIndexOf(" (")], wonders));
                    Assert.All(view.Pyramid.Slots, x => Assert.Equal(x.Accessible && x.Card is not null, x.Actions.Length > 0));
                }
                foreach (var line in NextLines(session, chance, rng, false)) session.Enter(line);
            }
        }
    }

    [Fact]
    public void Every_Reveal_and_decision_of_a_game_has_a_view_the_page_can_enter()
    {
        // Each step is entered through what the view offers, as the page does: a tile, a card action or a picker line.
        for (ulong seed = 0; seed < 15; seed++)
        {
            var rules = AllRules[seed % (ulong)AllRules.Length];
            var session = AdvisorSession.New(rules, (int)(seed % 2));
            var chance = new RandomChanceSource(seed);
            var rng = new Rng(seed + 5);
            while (!session.State.IsOver)
            {
                var view = BoardView.Build(session, "x");
                var s = session.State;
                Assert.False(string.IsNullOrWhiteSpace(view.Prompt));
                string line;
                if (s.Step == StepKind.Reveal)
                {
                    var r = view.Reveal!;
                    var expected = MoveLog.Format(chance.Next(s));
                    if (r.Kind == "slots")
                    {
                        var flip = (SlotReveal)MoveLog.ParseEntry(expected, s);
                        var (slot, card) = flip.Cards[0];
                        var sv = view.Pyramid!.Slots[slot];
                        Assert.True(sv.Pulse);
                        Assert.Contains(sv.Candidates!, c => c.Enabled && c.Id == GameData.Cards[card].Id);
                        line = $"{sv.RevealPrefix} {GameData.Cards[card].Id}";
                    }
                    else
                    {
                        Assert.StartsWith(r.LinePrefix + " ", expected);
                        var named = expected[(r.LinePrefix.Length + 1)..].Split(' ');
                        Assert.Equal(r.Count, named.Length);
                        Assert.All(named, n => Assert.Contains(r.Options, o => o.Id == n && o.Enabled));
                        line = expected;
                    }
                }
                else
                {
                    var options = view.Decision!.Tiles.Length > 0
                        ? view.Decision.Tiles.ToList()
                        : view.Pyramid!.Slots.SelectMany(x => x.Actions).Where(a => a.Enabled).ToList();
                    if (s.Pending(0).Kind == DecisionKind.LibertyKeep)
                    {
                        // The Statue as its 6 keep/give pairs, each entered as one change.
                        Assert.Equal(s.LegalMoves().Count * 2, options.Count);
                        Assert.All(options, o => Assert.NotNull(o.Then));
                        var pair = options[rng.Next(options.Count)];
                        session.Enter([pair.Line, pair.Then!]);
                        continue;
                    }
                    Assert.Equal(s.LegalMoves().Count, options.Count);
                    line = options[rng.Next(options.Count)].Line;
                }
                session.Enter(line);
            }
            var result = BoardView.Build(session, "x").Result!;
            Assert.Equal(session.State.Result!.P1.Total, result.P1Total);
            Assert.Equal("over", BoardView.Build(session, "x").Step);
        }
    }

    [Fact]
    public void A_finished_game_refuses_more_entries()
    {
        var session = PlayThrough(3, Ruleset.Default, 0);
        var e = Assert.Throws<EntryException>(() => session.Enter("P1 build theater"));
        Assert.Contains("over", e.Message);
    }
}
