using SevenWondersDuel.Engine.Data;

namespace SevenWondersDuel.Engine.Tests;

/// <summary>
/// Pantheon ticket 07 (spec-pantheon P-A5, R3): the Observation hides the opponent's draw, the Divinities they placed
/// face down, the cards they put back and everything their Divine Theater showed them but the activated Divinity; the
/// Advisor's redacted log lines give the same view; the Determinizer fills the unknowns uniformly from each
/// Mythology's unseen cards.
/// </summary>
public class PantheonObservationTests
{
    const string Rules = "rules base expansion=pantheon";
    const string Opening =
        "reveal age1 0.0 palisade 0.1 tavern 2.0 guard-tower 2.1 altar 2.2 lumber-yard 2.3 press "
        + "4.0 apothecary 4.1 baths 4.2 logging-camp 4.3 quarry 4.4 glassworks 4.5 stone-reserve";
    const string MythTokens = "reveal myth-tokens 1.0 greek 1.2 roman 3.0 egyptian 3.2 phoenician 3.4 greek";
    static readonly int Theater = PantheonData.WonderIndex("divine-theater");

    static int D(string slug) => PantheonData.DivinityIndex(slug);
    static ushort Egyptian => (ushort)(1 << D("anubis") | 1 << D("isis") | 1 << D("ra"));

    /// <summary>
    /// P2 takes 4.1, flipping 3.0 with the Egyptian token, draws <paramref name="draw"/> and (if
    /// <paramref name="place"/>) places it face down on space 4; the other card goes back on top, known to P2 only.
    /// </summary>
    static Game EgyptianDraw(string draw, string? place)
    {
        string[] lines = ["7wd-log v1", Rules, "reveal first P1", "reveal tokens agriculture economy law strategy urbanism",
            .. Setup.Draft(0), Opening, MythTokens, "P1 discard apothecary", "P2 discard baths", "reveal 3.0 stable",
            $"reveal P2 divinities egyptian {draw}", .. place is null ? Array.Empty<string>() : [$"P2 divinity {place} 4"]];
        return MoveLog.Parse(string.Join('\n', lines));
    }

    /// <summary>The game's log as <paramref name="viewer"/> keeps it (the opponent's private lines redacted), replayed.</summary>
    static Game Redacted(Game game, int viewer) =>
        MoveLog.Parse(MoveLog.Write(game.Rules, game.Log.Select(e => MoveLog.AsSeenBy(e, viewer))));

    // ---------------------------------------------------------------- the Observation

    [Fact]
    public void The_opponent_sees_neither_the_draw_nor_the_placed_divinity_nor_the_card_put_back()
    {
        // Two different hidden draws and placements (and so different deck tops).
        var a = EgyptianDraw("anubis isis", "isis");
        var b = EgyptianDraw("ra anubis", "anubis");
        Assert.False(a.State.SameAs(b.State));
        var mineA = a.State.Observe(0);
        Assert.True(mineA.SameAs(b.State.Observe(0)));
        Assert.False(a.State.Observe(1).SameAs(b.State.Observe(1)));

        var p = mineA.State.Pantheon!;
        Assert.True(p.IsFaceDown(3) && p.IsHidden(3));
        Assert.Equal(Mythology.Egyptian, p.SpaceMythology(3));
        Assert.Equal(1, p.Placer(3));
        Assert.Equal(PantheonState.HiddenSpace(Mythology.Egyptian), p.Space(3));
        Assert.Equal(2, p.DeckSize(Mythology.Egyptian));
        Assert.Equal((PantheonState.Unknown, 2), (p.DeckCard(Mythology.Egyptian, 0), p.DeckKnownBy(Mythology.Egyptian, 0)));
        Assert.Equal((PantheonState.Unknown, 0), (p.DeckCard(Mythology.Egyptian, 1), p.DeckKnownBy(Mythology.Egyptian, 1)));
        Assert.Equal(0, p.SeenDivinities & Egyptian);
        Assert.Contains("?egyptian(face down, P2)", mineA.ToString());

        // P2 sees all of it.
        var theirs = a.State.Observe(1).State.Pantheon!;
        Assert.Equal(D("isis"), theirs.Space(3));
        Assert.Equal(D("anubis"), theirs.DeckCard(Mythology.Egyptian, 0));
        // Observing never changes the game.
        Assert.Equal(D("isis"), a.State.Pantheon!.Space(3));
    }

    [Fact]
    public void While_the_opponent_places_their_draw_it_is_hidden_and_only_the_space_can_be_told()
    {
        var a = EgyptianDraw("anubis isis", null);
        var b = EgyptianDraw("ra isis", null);
        var observation = a.State.Observe(0);
        Assert.True(observation.SameAs(b.State.Observe(0)));
        var p = observation.State.Pantheon!;
        Assert.True(p.HasDraw && p.DrawHidden);
        Assert.Equal([PantheonState.Hidden, PantheonState.Hidden], new[] { p.Drawn(0), p.Drawn(1) });
        Assert.Equal(StepKind.Decision, observation.State.Step);
        Assert.Equal(Enumerable.Range(0, 6).Select(space => Move.PlaceDivinity(PantheonState.Hidden, space)), observation.LegalMoves());
        Assert.Equal("divinity ? 1", observation.LegalMoves()[0].ToString());
        // The drawer sees their own draw.
        Assert.True(a.State.Observe(1).State.SameAs(a.State));
    }

    // ---------------------------------------------------------------- the Advisor's redacted log

    [Fact]
    public void The_redacted_log_lines_replay_to_the_same_view()
    {
        var game = EgyptianDraw("anubis isis", "isis");
        var text = MoveLog.Write(game.Rules, game.Log.Select(e => MoveLog.AsSeenBy(e, 0)));
        Assert.Contains("reveal P2 divinities egyptian ? ?\n", text);
        Assert.Contains("P2 divinity ? 4\n", text);
        Assert.DoesNotContain("isis", text);
        var replayed = MoveLog.Parse(text);
        Assert.Equal(text, replayed.WriteLog());
        Assert.True(replayed.State.Observe(0).SameAs(game.State.Observe(0)));
        // P2's own log is the whole log.
        Assert.Equal(game.WriteLog(), MoveLog.Write(game.Rules, game.Log.Select(e => MoveLog.AsSeenBy(e, 1))));

        // A redacted draw hides every card, and a hidden draw is placed as '?'.
        var s = EgyptianDraw("anubis isis", null).State;
        var e = Assert.Throws<FormatException>(() => MoveLog.ParseEntry("reveal P2 divinities egyptian ? isis", s));
        Assert.Contains("one '?' per card", e.Message);
        var hidden = MoveLog.Parse(string.Join('\n', Redacted(EgyptianDraw("anubis isis", null), 0).WriteLog())).State;
        Assert.True(hidden.Pantheon!.DrawHidden);
        Assert.Contains("'?'", hidden.WhyIllegal(Move.PlaceDivinity(D("isis"), 3)));
        Assert.Null(hidden.WhyIllegal(Move.PlaceDivinity(PantheonState.Hidden, 3)));
        Assert.NotNull(s.WhyIllegal(Move.PlaceDivinity(PantheonState.Hidden, 3)));
    }

    [Fact]
    public void Age_two_names_the_opponents_face_down_divinity_in_the_redacted_log_too()
    {
        // Random games to Age II, kept by P1 with P2's private lines redacted (and the other way round).
        for (ulong seed = 0; seed < 30; seed++)
        {
            var game = new Game(Ruleset.Parse(Rules));
            var chance = new RandomChanceSource(seed);
            var rng = new Rng(seed + 1);
            while (game.State.Age < 2 || game.State.Pantheon!.AwaitingPantheon || game.State.Step == StepKind.Reveal)
            {
                if (game.State.Step == StepKind.Reveal) game.Apply(chance.Next(game.State));
                else
                {
                    var moves = game.State.LegalMoves();
                    game.Apply(moves[rng.Next(moves.Count)]);
                }
            }
            for (var viewer = 0; viewer < 2; viewer++)
            {
                var view = Redacted(game, viewer).State;
                Assert.True(view.Observe(viewer).SameAs(game.State.Observe(viewer)));
                // After "reveal pantheon" nothing on the Pantheon is hidden any more.
                Assert.All(Enumerable.Range(0, 6), space => Assert.False(view.Pantheon!.IsHidden(space)));
            }
        }
    }

    // ---------------------------------------------------------------- the Divine Theater (R3)

    /// <summary>
    /// Age II: P2 builds the Divine Theater on a full deck nobody has seen, activates its middle card and restacks
    /// the other two with <paramref name="topFirst"/> deciding which goes on top. Returns the game and the deck.
    /// </summary>
    static (Game Game, Mythology Deck, (GameState State, int LogCount) Before) TheaterGame(bool topFirst, bool restack = true)
    {
        Mythology? Full(GameState s) => Enum.GetValues<Mythology>().Cast<Mythology?>().FirstOrDefault(m =>
            s.Pantheon!.DeckSize(m!.Value) == PantheonData.DeckSize && s.Pantheon.DeckCard(m.Value, 0) == PantheonState.Unknown);
        var game = PantheonActivationTests.AgeTwoWhere(1, s =>
            ((s.Wonders(0) | s.Wonders(1)) & 1 << Theater) == 0 && s.TotalWondersBuilt < 6 && Full(s) is not null);
        var s = game.State;
        var deck = Full(s)!.Value;
        s.GiveWonder(1, Theater);
        s.SetCoins(1, 99);
        var before = (s.Clone(), game.Log.Count);
        game.Apply(Move.BuildWonder(s.SlotCard(Enumerable.Range(0, GameState.SlotCount).First(s.IsAccessible)), Theater));
        var chance = new RandomChanceSource(3);
        while (s.Step == StepKind.Reveal) game.Apply(chance.Next(s));
        game.Apply(Move.TheaterDeck(deck));
        game.Apply(new TheaterReveal(1, deck, PantheonData.MythologyDef(deck).Divinities.ToArray()));
        var cards = PantheonData.MythologyDef(deck).Divinities;
        game.Apply(Move.Theater(cards[1]));
        if (restack) game.Apply(Move.TheaterOrder(topFirst ? cards[0] : cards[2]));
        return (game, deck, before);
    }

    [Fact]
    public void The_opponents_theater_shows_only_the_activated_divinity()
    {
        var (a, deck, _) = TheaterGame(topFirst: true);
        var (b, _, _) = TheaterGame(topFirst: false);
        var cards = PantheonData.MythologyDef(deck).Divinities;
        Assert.False(a.State.SameAs(b.State));
        Assert.True(a.State.Observe(0).SameAs(b.State.Observe(0)));
        Assert.False(a.State.Observe(1).SameAs(b.State.Observe(1)));

        var p = a.State.Observe(0).State.Pantheon!;
        // The activated Divinity is public; the unused cards and their order are P2's alone.
        Assert.NotEqual(0, p.Activated(1) & 1 << cards[1]);
        Assert.NotEqual(0, p.SeenDivinities & 1 << cards[1]);
        Assert.Equal(0, p.SeenDivinities & (1 << cards[0] | 1 << cards[2]));
        Assert.Equal(2, p.DeckSize(deck));
        for (var pos = 0; pos < 2; pos++) Assert.Equal((PantheonState.Unknown, 2), (p.DeckCard(deck, pos), p.DeckKnownBy(deck, pos)));
        Assert.Contains("?(P2 knows)", a.State.Observe(0).ToString());
    }

    [Fact]
    public void Before_the_restack_the_opponent_can_only_log_theater_order_hidden()
    {
        var (game, deck, before) = TheaterGame(topFirst: true, restack: false);
        var observation = game.State.Observe(0);
        Assert.Equal(DecisionKind.TheaterOrder, observation.State.Pending(0).Kind);
        Assert.Equal([Move.TheaterOrder(PantheonState.Hidden)], observation.LegalMoves());
        Assert.Equal("theater-order ?", observation.LegalMoves()[0].ToString());
        // The owner names the order.
        Assert.Equal(2, game.State.LegalMoves().Count);

        // The Advisor's view: P1's state before the Theater, then P2's lines as P1 logs them.
        var view = before.State.Observe(0).State;
        var lines = game.Log.Skip(before.LogCount).Select(e => MoveLog.AsSeenBy(e, 0)).ToList();
        Assert.Contains(lines, e => e.ToString() == $"reveal P2 theater {PantheonData.MythologyDef(deck).Id} ? ? ?");
        foreach (var e in lines)
            if (e is MoveEntry m) view.Apply(m.Move);
            else view.Apply((Reveal)e);
        Assert.True(view.Observe(0).SameAs(observation));
        Assert.Equal("P2 theater-order ?", MoveLog.AsSeenBy(new MoveEntry(1, Move.TheaterOrder(D("zeus"))), 0).ToString());
        var restacked = MoveLog.ParseEntry("P2 theater-order ?", view);
        view.Apply(((MoveEntry)restacked).Move);
        game.Apply(game.State.LegalMoves()[1]);
        Assert.True(view.Observe(0).SameAs(game.State.Observe(0)));
    }

    // ---------------------------------------------------------------- the Determinizer

    [Fact]
    public void Hidden_divinities_are_filled_uniformly_from_their_mythologys_unseen_cards()
    {
        var a = EgyptianDraw("anubis isis", "isis");
        var b = EgyptianDraw("ra anubis", "anubis");
        var observation = a.State.Observe(0);
        var counts = new Dictionary<(int Space, int Top), int>();
        var target = GameState.New(Ruleset.Parse(Rules));
        for (ulong seed = 0; seed < 3000; seed++)
        {
            var world = new Determinizer(seed).Sample(observation);
            new Determinizer(seed).Sample(b.State.Observe(0), target);
            Assert.True(world.SameAs(target), "the same seed must give the same world from the same Observation");
            var p = world.Pantheon!;
            var (space, top) = (p.Space(3), p.DeckCard(Mythology.Egyptian, 0));
            Assert.True(space != top && (Egyptian & 1 << space) != 0 && (Egyptian & 1 << top) != 0);
            // The bottom card nobody has seen stays unknown: its Reveal will draw the last unseen card.
            Assert.Equal(PantheonState.Unknown, p.DeckCard(Mythology.Egyptian, 1));
            Assert.Equal(Egyptian & ~(1 << space | 1 << top), Egyptian & ~p.SeenDivinities);
            Assert.True(world.Observe(0).SameAs(observation));
            counts[(space, top)] = counts.GetValueOrDefault((space, top)) + 1;
        }
        // All 6 (space, top) pairs, each about 500 times; the true one is not favoured.
        Assert.Equal(6, counts.Count);
        Assert.All(counts.Values, n => Assert.InRange(n, 400, 600));
    }

    [Fact]
    public void The_theaters_unused_cards_are_filled_in_either_order()
    {
        var (game, deck, _) = TheaterGame(topFirst: true);
        var cards = PantheonData.MythologyDef(deck).Divinities;
        var observation = game.State.Observe(0);
        var onTop = 0;
        for (ulong seed = 0; seed < 1000; seed++)
        {
            var p = new Determinizer(seed).Sample(observation).Pantheon!;
            var top = (p.DeckCard(deck, 0), p.DeckCard(deck, 1));
            Assert.True(top == (cards[0], cards[2]) || top == (cards[2], cards[0]));
            if (top.Item1 == cards[0]) onTop++;
        }
        Assert.InRange(onTop, 430, 570);
    }

    /// <summary>
    /// From both players' Observations all through random Pantheon games (both promos), sampled worlds are legal
    /// states the observer cannot tell from the truth, and play out to the end with the Determinizer as their only
    /// Chance Source; a reseed replays the same world exactly.
    /// </summary>
    [Fact]
    public void Sampled_pantheon_worlds_are_legal_and_play_out_and_replay_from_a_seed()
    {
        var rng = new Rng(17);
        var (hidden, playouts) = (0, 0);
        for (var game = 0; game < 60; game++)
        {
            var chance = new RandomChanceSource(rng.NextUInt64());
            var s = GameState.New(Ruleset.Parse(game % 2 == 0 ? Rules : "rules base promo=messe,liberty expansion=pantheon"));
            while (!s.IsOver)
            {
                if (s.Step == StepKind.Reveal)
                {
                    s.Apply(chance.Next(s));
                    continue;
                }
                for (var viewer = 0; viewer < 2; viewer++)
                {
                    var observation = s.Observe(viewer);
                    if (observation.State.SameAs(s) && rng.Next(10) != 0) continue;
                    if (!observation.State.SameAs(s)) hidden++;
                    var world = new Determinizer(rng.NextUInt64()).Sample(observation);
                    Assert.True(world.Observe(viewer).SameAs(observation));
                    Assert.NotEmpty(world.LegalMoves());
                    if (rng.Next(4) != 0) continue;
                    var seed = rng.NextUInt64();
                    Assert.Equal(PlayOut(observation, seed), PlayOut(observation, seed));
                    playouts++;
                }
                var moves = s.LegalMoves();
                s.Apply(moves[rng.Next(moves.Count)]);
            }
        }
        Assert.True(hidden > 300 && playouts > 100, $"only {hidden} hidden positions, {playouts} playouts");
    }

    static string PlayOut(Observation observation, ulong seed)
    {
        var determinizer = new Determinizer(seed);
        var world = determinizer.Sample(observation);
        var policy = new Rng(seed + 1);
        while (!world.IsOver)
        {
            if (world.Step == StepKind.Reveal) world.Apply(determinizer.Next(world));
            else
            {
                var moves = world.LegalMoves();
                world.Apply(moves[policy.Next(moves.Count)]);
            }
        }
        return Convert.ToHexString(world.ToBytes());
    }
}
