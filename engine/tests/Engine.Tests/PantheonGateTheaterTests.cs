using SevenWondersDuel.Engine.Data;

namespace SevenWondersDuel.Engine.Tests;

/// <summary>
/// Pantheon ticket 05 (spec-pantheon P-A4, R3, R5): the Gate (a public Reveal of the deck tops, one activated for
/// free), the Divine Theater (a private Reveal of a whole deck, one activated for free, the rest restacked by a private
/// Move), Minerva (her pawn stops the Conflict Pawn) and Neptune (discard a Military token, apply another), and the
/// unchosen-Enki FAQ.
/// </summary>
public class PantheonGateTheaterTests
{
    const string Rules = "rules base expansion=pantheon";

    static int D(string slug) => PantheonData.DivinityIndex(slug);
    static readonly int Theater = PantheonData.WonderIndex("divine-theater");
    static readonly int Enki = D("enki");

    static Game AgeTwo(int player, string divinity, Func<GameState, bool>? where = null) =>
        PantheonActivationTests.AgeTwoWhere(player, where ?? (_ => true), divinity);

    /// <summary>The player to decide activates <paramref name="divinity"/> (or the Gate), with exactly the coins it costs.</summary>
    static void Activate(Game game, int divinity)
    {
        var s = game.State;
        var player = s.PlayerToDecide;
        s.Pantheon!.HeldOfferings[player] = 0;
        s.SetCoins(player, PantheonModule.ActivationCost(s, player, divinity));
        game.Apply(Move.Activate(divinity));
    }

    static void Reveals(Game game, ulong seed = 7)
    {
        var chance = new RandomChanceSource(seed);
        while (game.State.Step == StepKind.Reveal) game.Apply(chance.Next(game.State));
    }

    static readonly int Poliorcetics = PantheonData.TokenIndex("poliorcetics");
    static bool AllMilitaryTokens(GameState s) => s.MilitaryTokensLeft == GameState.MilitaryTokenCount;

    static int[] Accessible(GameState s) => Enumerable.Range(0, GameState.SlotCount).Where(s.IsAccessible).ToArray();

    /// <summary>Gives the player to decide the Divine Theater and builds it (coins enough), then reveals the flips.</summary>
    static void BuildTheater(Game game)
    {
        var s = game.State;
        var player = s.PlayerToDecide;
        s.GiveWonder(player, Theater);
        s.SetCoins(player, 99);
        game.Apply(Move.BuildWonder(s.SlotCard(Accessible(s)[0]), Theater));
        Reveals(game);
    }

    /// <summary>An Age II take-card decision where the Divine Theater is still in the box and a deck holds all 3 cards, none seen.</summary>
    static (Game Game, Mythology Deck) AgeTwoWithFullDeck(int player, Func<GameState, Mythology, bool>? where = null)
    {
        Mythology? Full(GameState s) => Enum.GetValues<Mythology>().Cast<Mythology?>().FirstOrDefault(m =>
            s.Pantheon!.DeckSize(m!.Value) == PantheonData.DeckSize && s.Pantheon.DeckCard(m.Value, 0) == PantheonState.Unknown
            && (where?.Invoke(s, m.Value) ?? true));
        var game = PantheonActivationTests.AgeTwoWhere(player, s =>
            ((s.Wonders(0) | s.Wonders(1)) & 1 << Theater) == 0 && s.TotalWondersBuilt < 6 && Full(s) is not null);
        return (game, Full(game.State)!.Value);
    }

    // ---------------------------------------------------------------- the Gate

    [Fact]
    public void The_gate_costs_double_and_reveals_every_deck_top_publicly_then_activates_one_for_free()
    {
        var game = AgeTwo(0, "gate");
        var s = game.State;
        var p = s.Pantheon!;
        Assert.Equal(2 * PantheonData.Spaces[0].Cost(0), PantheonModule.ActivationCost(s, 0, PantheonData.Gate));
        Activate(game, PantheonData.Gate);
        Assert.Equal(0, s.Coins(0));
        Assert.True(p.AwaitingGateReveal);
        Assert.Equal(StepKind.Reveal, s.Step);
        var reveal = Assert.IsType<GateReveal>(new RandomChanceSource(3).Next(s));
        // R3: the Gate's Reveal is public.
        Assert.Equal(-1, reveal.Audience);
        var decks = Enum.GetValues<Mythology>().Where(m => p.DeckSize(m) > 0).ToArray();
        Assert.Equal(decks.Length, reveal.Divinities.Length);
        Assert.Throws<InvalidOperationException>(() => game.Apply(reveal with { Divinities = reveal.Divinities[1..] }));
        game.Apply(reveal);
        Assert.StartsWith("reveal gate ", game.Log[^1].ToString());
        foreach (var (m, d) in decks.Zip(reveal.Divinities))
        {
            Assert.Equal(d, p.DeckCard(m, 0));
            Assert.Equal(3, p.DeckKnownBy(m, 0));
        }
        Reveals(game); // Enki's tokens, if the Gate revealed Enki
        Assert.Equal(new PendingDecision(DecisionKind.Gate, 0), s.Pending(0));
        Assert.Equal(reveal.Divinities.Select(Move.Gate), s.LegalMoves());

        // Activate one whose effect needs no decision, if any, else the first; it leaves its deck.
        var chosen = reveal.Divinities.FirstOrDefault(d => d is var x && (x == D("tanit") || x == D("aphrodite") || x == D("astarte")), reveal.Divinities[0]);
        var deck = PantheonData.Divinities[chosen].Mythology!.Value;
        var size = p.DeckSize(deck);
        game.Apply(Move.Gate(chosen));
        Assert.Equal($"P1 gate {PantheonData.Divinities[chosen].Id}", game.Log[^1].ToString());
        Assert.Equal(size - 1, p.DeckSize(deck));
        Assert.NotEqual(0, p.Activated(0) & 1 << chosen);
        Assert.NotEqual(0, p.Activated(0) & 1 << PantheonData.Gate);
        Assert.Equal(PantheonState.EmptySpace, p.Space(0));
        // The others went back face down, still known to both.
        foreach (var (m, d) in decks.Zip(reveal.Divinities).Where(x => x.Second != chosen))
            Assert.Equal((d, 3), (p.DeckCard(m, 0), p.DeckKnownBy(m, 0)));
    }

    [Fact]
    public void An_enki_revealed_by_the_gate_shows_its_tokens_and_they_go_back_to_the_box_if_not_chosen()
    {
        var game = AgeTwo(1, "gate", s => (s.Pantheon!.SeenDivinities & 1 << Enki) == 0
            && s.Pantheon.DeckSize(Mythology.Mesopotamian) > 0 && s.Pantheon.DeckCard(Mythology.Mesopotamian, 0) == PantheonState.Unknown);
        var s = game.State;
        var p = s.Pantheon!;
        Activate(game, PantheonData.Gate);
        var random = (GateReveal)new RandomChanceSource(1).Next(s);
        var decks = Enum.GetValues<Mythology>().Where(m => p.DeckSize(m) > 0).ToArray();
        var reveal = new GateReveal(decks.Zip(random.Divinities).Select(x => x.First == Mythology.Mesopotamian ? Enki : x.Second).ToArray());
        game.Apply(reveal);
        // Enki became face up: its tokens are drawn now, publicly, before the choice.
        Assert.True(p.AwaitingEnki);
        var enki = Assert.IsType<EnkiReveal>(new RandomChanceSource(2).Next(s));
        Assert.Equal(-1, enki.Audience);
        game.Apply(enki);
        var tokens = p.Enki;
        Assert.NotEqual(0, tokens);
        Assert.Equal(0, s.TokensSetAside & tokens);

        var other = reveal.Divinities.First(d => d != Enki);
        game.Apply(Move.Gate(other));
        Assert.Equal(0, p.Enki);
        Assert.Equal(tokens, s.TokensSetAside & tokens);
        Assert.Equal(Enki, p.DeckCard(Mythology.Mesopotamian, 0));
    }

    // ---------------------------------------------------------------- the Divine Theater

    [Fact]
    public void The_divine_theater_reveals_a_deck_privately_and_its_restacking_decides_the_next_draw()
    {
        var (game, deck) = AgeTwoWithFullDeck(0);
        var s = game.State;
        var p = s.Pantheon!;
        Assert.Equal(Cost.Parse("P P G W W").Resources, GameData.Wonder(Theater).Cost.Resources); // R5
        BuildTheater(game);
        Assert.Equal(new PendingDecision(DecisionKind.TheaterDeck, 0, Theater), s.Pending(0));
        Assert.Contains(Move.TheaterDeck(deck), s.LegalMoves());
        game.Apply(Move.TheaterDeck(deck));
        Assert.Equal(-1, game.Log[^1].Audience);

        // R3: the whole deck is revealed to the Theater's owner alone.
        Assert.True(p.AwaitingTheaterReveal);
        var reveal = Assert.IsType<TheaterReveal>(new RandomChanceSource(4).Next(s));
        Assert.Equal(0, reveal.Audience);
        Assert.Equal(PantheonData.DeckSize, reveal.Divinities.Length);
        game.Apply(reveal);
        Assert.Equal($"reveal P1 theater {PantheonData.MythologyDef(deck).Id} "
            + string.Join(' ', reveal.Divinities.Select(d => PantheonData.Divinities[d].Id)), game.Log[^1].ToString());
        Assert.Equal(new PendingDecision(DecisionKind.Theater, 0, (int)deck), s.Pending(0));
        Assert.Equal(reveal.Divinities.Select(Move.Theater), s.LegalMoves());

        // Only the activated Divinity becomes public: the theater Move.
        var (chosen, unused) = (reveal.Divinities[1], new[] { reveal.Divinities[0], reveal.Divinities[2] });
        game.Apply(Move.Theater(chosen));
        Assert.Equal(-1, game.Log[^1].Audience);
        Assert.Equal(new PendingDecision(DecisionKind.TheaterOrder, 0, chosen), s.Pending(0));
        Assert.Equal(unused.Select(Move.TheaterOrder), s.LegalMoves());
        Assert.Contains("not an unused card", s.WhyIllegal(Move.TheaterOrder(chosen)));

        // The restacking order is a private Move: P1 puts the card that was at the bottom on top.
        game.Apply(Move.TheaterOrder(unused[1]));
        var order = game.Log[^1];
        Assert.Equal($"P1 theater-order {PantheonData.Divinities[unused[1]].Id}", order.ToString());
        Assert.Equal(0, order.Audience);
        Assert.NotEqual(0, p.Activated(0) & 1 << chosen);
        Assert.Equal(2, p.DeckSize(deck));
        Assert.Equal((unused[1], 1), (p.DeckCard(deck, 0), p.DeckKnownBy(deck, 0)));
        Assert.Equal((unused[0], 1), (p.DeckCard(deck, 1), p.DeckKnownBy(deck, 1)));
        Reveals(game);
        while (s.Pending(0).Kind != DecisionKind.TakeCard)
        {
            game.Apply(s.LegalMoves()[0]);
            Reveals(game);
        }

        // The next draw from that deck (here the Gate's) takes the card P1 put on top.
        p.Spaces[0] = (sbyte)PantheonData.Gate;
        p.FaceDown = 0;
        Activate(game, PantheonData.Gate);
        var gate = Assert.IsType<GateReveal>(new RandomChanceSource(9).Next(s));
        var decks = Enum.GetValues<Mythology>().Where(m => p.DeckSize(m) > 0).ToList();
        Assert.Equal(unused[1], gate.Divinities[decks.IndexOf(deck)]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void With_the_theater_enkis_tokens_are_drawn_only_if_enki_is_activated(bool chooseEnki)
    {
        var (game, _) = AgeTwoWithFullDeck(0, (s, m) => m == Mythology.Mesopotamian && s.Pantheon!.Enki == 0);
        var s = game.State;
        var p = s.Pantheon!;
        BuildTheater(game);
        game.Apply(Move.TheaterDeck(Mythology.Mesopotamian));
        Reveals(game);
        Assert.Contains(Move.Theater(Enki), s.LegalMoves());
        var box = s.TokensSetAside;
        var chosen = chooseEnki ? Enki : s.LegalMoves().First(m => m.Divinity != Enki).Divinity;
        game.Apply(Move.Theater(chosen));
        game.Apply(s.LegalMoves()[0]); // theater-order
        if (!chooseEnki)
        {
            Assert.False(p.AwaitingEnki);
            Assert.Equal(0, p.Enki);
            Assert.Equal(box, s.TokensSetAside);
            Assert.DoesNotContain(game.Log, e => e is EnkiReveal);
            return;
        }
        // Enki activated: its 2 tokens are drawn now, publicly, then its owner keeps one.
        Assert.True(p.AwaitingEnki);
        Assert.Equal(new PendingDecision(DecisionKind.Enki, 0), s.Pending(0));
        Assert.Equal(StepKind.Reveal, s.Step);
        var enki = Assert.IsType<EnkiReveal>(new RandomChanceSource(5).Next(s));
        Assert.Equal(-1, enki.Audience);
        game.Apply(enki);
        var kept = s.LegalMoves()[1].Token;
        game.Apply(Move.Enki(kept));
        Assert.True(s.HasToken(0, kept));
        Assert.Equal(0, p.Enki);
    }

    [Fact]
    public void The_theater_on_a_deck_of_one_or_two_cards_needs_no_order_for_the_last()
    {
        var (game, deck) = AgeTwoWithFullDeck(1);
        var s = game.State;
        var p = s.Pantheon!;
        // Two cards of the deck already left it (say, into the Pantheon in Age I), so 1 is left after the activation.
        p.DeckCount[(int)deck] = 2;
        BuildTheater(game);
        game.Apply(Move.TheaterDeck(deck));
        var reveal = Assert.IsType<TheaterReveal>(new RandomChanceSource(4).Next(s));
        Assert.Equal(2, reveal.Divinities.Length);
        game.Apply(reveal);
        game.Apply(Move.Theater(reveal.Divinities[0]));
        Assert.NotEqual(DecisionKind.TheaterOrder, s.Pending(0).Kind);
        Assert.Equal((reveal.Divinities[1], 2), (p.DeckCard(deck, 0), p.DeckKnownBy(deck, 0)));
    }

    [Theory]
    [InlineData(Mythology.Greek)]
    [InlineData(Mythology.Phoenician)]
    [InlineData(Mythology.Mesopotamian)]
    [InlineData(Mythology.Egyptian)]
    [InlineData(Mythology.Roman)]
    public void The_theater_takes_the_chosen_card_out_of_any_deck(Mythology deck)
    {
        // Ticket 06's soak found the chosen card was taken from the wrong place in every deck but the Greek one.
        var (game, _) = AgeTwoWithFullDeck(0, (s, m) => m == deck);
        var s = game.State;
        var p = s.Pantheon!;
        BuildTheater(game);
        game.Apply(Move.TheaterDeck(deck));
        var reveal = Assert.IsType<TheaterReveal>(new RandomChanceSource(4).Next(s));
        game.Apply(reveal);
        var (top, middle, bottom) = (reveal.Divinities[0], reveal.Divinities[1], reveal.Divinities[2]);
        game.Apply(Move.Theater(top));
        Assert.Equal(2, p.DeckSize(deck));
        Assert.Equal((middle, bottom), (p.DeckCard(deck, 0), p.DeckCard(deck, 1)));
        game.Apply(Move.TheaterOrder(bottom));
        Assert.Equal((bottom, middle), (p.DeckCard(deck, 0), p.DeckCard(deck, 1)));
        Assert.NotEqual(0, p.Activated(0) & 1 << top);
    }

    [Fact]
    public void A_mythology_token_on_a_deck_of_one_card_draws_it_alone()
    {
        for (ulong seed = 0; seed < 100; seed++)
        {
            var game = new Game(Ruleset.Parse(Rules));
            var chance = new RandomChanceSource(seed);
            var rng = new Rng(seed);
            var s = game.State;
            var p = s.Pantheon!;
            while (!s.IsOver && s.Age <= 1)
            {
                if (s.SlotsToReveal == 0 && s.PendingCount > 0 && s.Pending(0).Kind == DecisionKind.PlaceDivinity && !p.HasDraw)
                {
                    var m = (Mythology)s.Pending(0).Wonder;
                    p.DeckCount[(int)m] = 1;
                    var draw = Assert.IsType<DivinityDrawReveal>(chance.Next(s));
                    Assert.Single(draw.Divinities);
                    Assert.Matches($"^reveal P[12] divinities {PantheonData.MythologyDef(m).Id} [a-z]+$", draw.ToString());
                    Assert.Equal(draw.ToString(), MoveLog.ParseEntry(draw.ToString(), s).ToString());
                    game.Apply(draw);
                    var moves = s.LegalMoves();
                    Assert.All(moves, x => Assert.Equal(draw.Divinities[0], x.Divinity));
                    game.Apply(moves[0]);
                    Assert.Equal(0, p.DeckSize(m));
                    return;
                }
                if (s.Step == StepKind.Reveal) game.Apply(chance.Next(s));
                else game.Apply(s.LegalMoves()[rng.Next(s.LegalMoves().Count)]);
            }
        }
        Assert.Fail("No Mythology token was taken");
    }

    // ---------------------------------------------------------------- Minerva

    [Fact]
    public void Minerva_stops_the_pawn_before_her_space_then_leaves()
    {
        var game = AgeTwo(0, "minerva", s => AllMilitaryTokens(s) && !s.HasToken(0, Poliorcetics) && !s.HasToken(1, Poliorcetics));
        var s = game.State;
        var p = s.Pantheon!;
        Activate(game, D("minerva"));
        Assert.Equal(new PendingDecision(DecisionKind.Minerva, 0), s.Pending(0));
        Assert.Equal(Enumerable.Range(-9, 19).Select(Move.Minerva), s.LegalMoves());
        game.Apply(Move.Minerva(3));
        Assert.Equal("P1 minerva 3", game.Log[^1].ToString());
        Assert.Equal(3, p.Minerva);
        Assert.Contains("Minerva: Military track space 3", s.ToString());

        // Moving away from her, or up to her, leaves her in place.
        s.SetCoins(1, 10);
        s.AddShields(1, 1);
        Assert.Equal((-1, 3), (s.ConflictPawn, p.Minerva));
        s.AddShields(0, 3);
        Assert.Equal((2, 3), (s.ConflictPawn, p.Minerva));
        // The pawn would enter her space (and P2's 2-coin zone): it stops, the shields left are lost, no loot.
        s.AddShields(0, 4);
        Assert.Equal(2, s.ConflictPawn);
        Assert.Equal(PantheonState.NoMinerva, p.Minerva);
        Assert.Equal(10, s.Coins(1));
        Assert.True(s.MilitaryTokenLeft(GameState.MilitaryToken(1, false)));
        // Gone: the next shield moves on.
        s.AddShields(0, 1);
        Assert.Equal(3, s.ConflictPawn);
        Assert.Equal(8, s.Coins(1));
    }

    [Fact]
    public void Minerva_on_a_capital_saves_it_once()
    {
        var game = AgeTwo(1, "minerva");
        var s = game.State;
        Activate(game, D("minerva"));
        game.Apply(Move.Minerva(-9));
        s.SetConflictPawn(-7);
        s.AddShields(1, 5);
        Assert.False(s.IsOver);
        Assert.Equal(-8, s.ConflictPawn);
        Assert.Equal(PantheonState.NoMinerva, s.Pantheon!.Minerva);
        s.AddShields(1, 1);
        Assert.True(s.IsOver);
        Assert.Equal((1, VictoryType.Military), (s.Result!.Winner, s.Result.Victory));
    }

    [Fact]
    public void Minerva_blocks_the_pawn_moving_either_way()
    {
        var game = AgeTwo(0, "minerva");
        var s = game.State;
        Activate(game, D("minerva"));
        game.Apply(Move.Minerva(-2));
        s.AddShields(1, 6);
        Assert.Equal(-1, s.ConflictPawn);
        Assert.Equal(PantheonState.NoMinerva, s.Pantheon!.Minerva);
    }

    // ---------------------------------------------------------------- Neptune

    [Fact]
    public void Neptune_discards_one_military_token_then_applies_another_from_either_side_against_the_opponent()
    {
        var game = AgeTwo(0, "neptune", AllMilitaryTokens);
        var s = game.State;
        Activate(game, D("neptune"));
        var all = Enumerable.Range(0, 4).ToArray();
        Assert.Equal(new PendingDecision(DecisionKind.NeptuneDiscard, 0), s.Pending(0));
        Assert.Equal(all.Select(Move.NeptuneDiscard), s.LegalMoves());
        // P1 discards the 5-coin token on their own side, then applies the 2-coin token on their own side: P2 loses 2.
        var ownFive = GameState.MilitaryToken(0, true);
        var ownTwo = GameState.MilitaryToken(0, false);
        game.Apply(Move.NeptuneDiscard(ownFive));
        Assert.Equal("P1 neptune-discard P1:5", game.Log[^1].ToString());
        Assert.False(s.MilitaryTokenLeft(ownFive));
        Assert.Equal(new PendingDecision(DecisionKind.NeptuneApply, 0), s.Pending(0));
        Assert.Equal(all.Where(t => t != ownFive).Select(Move.NeptuneApply), s.LegalMoves());
        Assert.Contains("not on the board", s.WhyIllegal(Move.NeptuneApply(ownFive)));
        s.SetCoins(0, 6);
        s.SetCoins(1, 6);
        game.Apply(Move.NeptuneApply(ownTwo));
        Assert.Equal("P1 neptune-apply P1:2", game.Log[^1].ToString());
        Assert.Equal((6, 4), (s.Coins(0), s.Coins(1)));
        Assert.Equal(2, s.MilitaryTokensLeft);
        Assert.Equal(new PendingDecision(DecisionKind.TakeCard, 1), s.Pending(0));
    }

    [Fact]
    public void Neptune_with_one_token_left_only_discards_it_and_never_touches_astartes_coins()
    {
        var game = AgeTwo(1, "neptune");
        var s = game.State;
        for (var t = 1; t < 4; t++) s.RemoveMilitaryToken(t);
        Activate(game, D("neptune"));
        game.Apply(Move.NeptuneDiscard(0));
        Assert.Equal(0, s.MilitaryTokensLeft);
        Assert.Equal(new PendingDecision(DecisionKind.TakeCard, 0), s.Pending(0));

        // R7: a Neptune loss hits the treasury only.
        var again = AgeTwo(0, "neptune", AllMilitaryTokens);
        var t2 = again.State;
        Activate(again, D("neptune"));
        t2.Pantheon!.Astarte[1] = 7;
        t2.SetCoins(1, 1);
        again.Apply(Move.NeptuneDiscard(0));
        again.Apply(Move.NeptuneApply(GameState.MilitaryToken(1, true)));
        Assert.Equal((0, 7), (t2.Coins(1), t2.Pantheon.AstarteCoins(1)));
    }

    [Fact]
    public void Neptune_with_no_token_left_does_nothing()
    {
        var game = AgeTwo(0, "neptune");
        var s = game.State;
        for (var t = 0; t < 4; t++) s.RemoveMilitaryToken(t);
        Activate(game, D("neptune"));
        Assert.Equal(new PendingDecision(DecisionKind.TakeCard, 1), s.Pending(0));
        Assert.Equal(1, s.PendingCount);
    }

    // ---------------------------------------------------------------- log, random games

    [Fact]
    public void The_log_writes_and_reads_the_gate_theater_minerva_and_neptune_lines()
    {
        var s = AgeTwo(0, "gate").State;
        string[] lines =
        [
            "reveal gate zeus baal enki isis mars", "P1 gate zeus", "P2 theater-deck greek", "reveal P2 theater greek zeus hades aphrodite",
            "reveal P1 theater roman mars", "P2 theater zeus", "P2 theater-order hades", "P1 minerva -3", "P2 minerva 9", "P1 minerva 0",
            "P1 neptune-discard P1:5", "P2 neptune-apply P2:2", "reveal P1 divinities greek zeus",
        ];
        foreach (var line in lines) Assert.Equal(line, MoveLog.ParseEntry(line, s).ToString());
        Assert.Equal(1, MoveLog.ParseEntry("P2 theater-order hades", s).Audience);
        Assert.Equal(1, MoveLog.ParseEntry("reveal P2 theater greek zeus hades aphrodite", s).Audience);
        Assert.Equal(-1, MoveLog.ParseEntry("P2 theater zeus", s).Audience);
        Assert.Equal(-1, MoveLog.ParseEntry("reveal gate zeus", s).Audience);
        Assert.Throws<FormatException>(() => MoveLog.ParseEntry("P1 minerva 10", s));
        Assert.Throws<FormatException>(() => MoveLog.ParseEntry("P1 neptune-apply P3:2", s));
        Assert.Throws<FormatException>(() => MoveLog.ParseEntry("P1 gate", s));
        Assert.Throws<FormatException>(() => MoveLog.ParseEntry("reveal P1 theater greek", s));
        Assert.Contains("no minerva decision", s.WhyIllegal(Move.Minerva(0)));
    }

    [Fact]
    public void Random_pantheon_games_use_the_gate_the_theater_minerva_and_neptune_and_replay()
    {
        var kinds = new Dictionary<DecisionKind, int>();
        var minervaStops = 0;
        for (ulong seed = 0; seed < 400; seed++)
        {
            var game = new Game(Ruleset.Parse(Rules));
            var chance = new RandomChanceSource(seed);
            var rng = new Rng(seed + 23);
            var s = game.State;
            while (!s.IsOver)
            {
                if (s.Step == StepKind.Reveal)
                {
                    game.Apply(chance.Next(s));
                    continue;
                }
                var moves = s.LegalMoves();
                Assert.True(moves.Count > 0);
                Assert.All(moves, m => Assert.Null(s.WhyIllegal(m)));
                var kind = s.Pending(0).Kind;
                if (kind >= DecisionKind.Gate) kinds[kind] = kinds.GetValueOrDefault(kind) + 1;
                // Favour activations and the Divine Theater so they all get used.
                var favoured = moves.Where(m => m.Kind == MoveKind.Activate || (m.Kind == MoveKind.Wonder && m.Wonder == Theater)).ToList();
                var minerva = s.Pantheon!.Minerva;
                game.Apply(favoured.Count > 0 && rng.Next(2) == 0 ? favoured[rng.Next(favoured.Count)] : moves[rng.Next(moves.Count)]);
                if (minerva != PantheonState.NoMinerva && s.Pantheon.Minerva == PantheonState.NoMinerva) minervaStops++;
                Assert.True(s.Coins(0) >= 0 && s.Coins(1) >= 0);
                Assert.All(Enum.GetValues<Mythology>(), m => Assert.InRange(s.Pantheon.DeckSize(m), 0, PantheonData.DeckSize));
            }
            var text = game.WriteLog();
            var replayed = MoveLog.Parse(text);
            Assert.True(replayed.State.SameAs(s));
            Assert.Equal(s.ToBytes(), replayed.State.ToBytes());
            Assert.Equal(text, replayed.WriteLog());
            Assert.Equal(s.ToBytes(), s.Clone().ToBytes());
        }
        foreach (var kind in new[] { DecisionKind.Gate, DecisionKind.TheaterDeck, DecisionKind.Theater, DecisionKind.TheaterOrder,
                     DecisionKind.Minerva, DecisionKind.NeptuneDiscard, DecisionKind.NeptuneApply })
            Assert.True(kinds.GetValueOrDefault(kind) > 0, $"no {kind} decision in {string.Join(", ", kinds)}");
        Assert.True(minervaStops > 0, "Minerva never stopped the pawn");
    }
}
