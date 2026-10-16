using SevenWondersDuel.Engine.Data;

namespace SevenWondersDuel.Engine.Tests;

/// <summary>
/// Pantheon ticket 04 (spec-pantheon P-A4, R2): the Divinities whose effect is a decision. Enki (keep a token), Nisaba
/// (the Snake), Baal (steal a brown or grey card), Hades (build from the discard pile), Zeus (discard any structure
/// card by slot, face down included), Anubis (unbuild a Wonder), Isis (build a Wonder with a discarded card) and Ra
/// (take an unbuilt Wonder).
/// </summary>
public class PantheonDivinityTests
{
    const string Rules = "rules base expansion=pantheon";

    static int D(string slug) => PantheonData.DivinityIndex(slug);
    static readonly CardSet Green = CardSets.OfColor[(int)CardColor.Green];
    static readonly CardSet BrownAndGrey = CardSets.OfColor[(int)CardColor.Brown] | CardSets.OfColor[(int)CardColor.Grey];

    /// <summary>An Age II take-card decision of <paramref name="player"/> with <paramref name="divinity"/> alone in the Pantheon (space 1).</summary>
    static Game AgeTwo(int player, string divinity, Func<GameState, bool>? where = null) =>
        PantheonActivationTests.AgeTwoWhere(player, where ?? (_ => true), divinity);

    /// <summary>The player to decide activates <paramref name="divinity"/>, with exactly the coins it costs.</summary>
    static void Activate(Game game, string divinity)
    {
        var s = game.State;
        var player = s.PlayerToDecide;
        s.Pantheon!.HeldOfferings[player] = 0;
        s.SetCoins(player, PantheonModule.ActivationCost(s, player, D(divinity)));
        game.Apply(Move.Activate(D(divinity)));
    }

    static void Reveals(Game game, ulong seed = 7)
    {
        var chance = new RandomChanceSource(seed);
        while (game.State.Step == StepKind.Reveal) game.Apply(chance.Next(game.State));
    }

    static void ClearDiscard(GameState s)
    {
        foreach (var c in s.DiscardPile.Cards().ToArray()) s.TakeFromDiscard(c);
    }

    static int[] Accessible(GameState s) => Enumerable.Range(0, GameState.SlotCount).Where(s.IsAccessible).ToArray();

    // ---------------------------------------------------------------- Enki

    [Fact]
    public void Enki_keeps_one_of_its_two_tokens_and_the_other_goes_back_to_the_box()
    {
        var game = AgeTwo(0, "enki");
        var s = game.State;
        var p = s.Pantheon!;
        var box = ProgressTokens.In(s.TokensSetAside).Where(t => t != ProgressTokens.Law).ToArray();
        p.EnkiTokens = (ushort)(1 << box[0] | 1 << box[1]);
        Assert.Equal(0, s.TokensSetAside & p.EnkiTokens);
        Activate(game, "enki");
        Assert.Equal(new PendingDecision(DecisionKind.Enki, 0), s.Pending(0));
        Assert.Equal([Move.Enki(box[0]), Move.Enki(box[1])], s.LegalMoves());
        Assert.Contains("not on Enki", s.WhyIllegal(Move.Enki(box[2])));
        Assert.Contains("Enki's Progress tokens", s.WhyIllegal(Move.Discard(0)));

        game.Apply(Move.Enki(box[1]));
        Assert.True(s.HasToken(0, box[1]));
        Assert.Equal(0, p.Enki);
        Assert.NotEqual(0, s.TokensSetAside & 1 << box[0]);
        Assert.Equal(0, s.TokensSetAside & 1 << box[1]);
        Assert.Equal($"P1 enki {GameData.Token(box[1]).Id}", game.Log[^1].ToString());
        Assert.Equal(new PendingDecision(DecisionKind.TakeCard, 1), s.Pending(0));
    }

    // ---------------------------------------------------------------- Nisaba

    /// <summary>The 2 green cards showing a symbol, neither in a city, the discard pile or the structure.</summary>
    static int[]? FreeGreenPair(GameState s, ScienceSymbol symbol)
    {
        var cards = (CardSets.OfSymbol[(int)symbol] & Green).Cards().ToArray();
        return cards.Length == 2 && cards.All(c => !s.City(0).Contains(c) && !s.City(1).Contains(c) && !s.DiscardPile.Contains(c)
            && !Enumerable.Range(0, GameState.SlotCount).Any(slot => s.SlotCard(slot) == c)) ? cards : null;
    }

    static readonly ScienceSymbol[] GreenSymbols = Enum.GetValues<ScienceSymbol>().Where(x => x != ScienceSymbol.Law).ToArray();

    [Fact]
    public void Nisaba_copies_the_symbol_under_the_snake_which_completes_a_pair()
    {
        var game = AgeTwo(0, "nisaba", s => s.CardsOfColor(0, CardColor.Green) == 0 && s.CardsOfColor(1, CardColor.Green) == 0
            && GreenSymbols.Any(x => FreeGreenPair(s, x) is not null));
        var s = game.State;
        var symbol = GreenSymbols.First(x => FreeGreenPair(s, x) is not null);
        var pair = FreeGreenPair(s, symbol)!;
        s.AddToCity(0, pair[0]);
        s.AddToCity(1, pair[1]);
        var free = Enumerable.Range(0, ProgressTokens.Count).First(t => !s.HasToken(0, t) && !s.HasToken(1, t));
        s.SetBoardTokens((ushort)(1 << free));
        Activate(game, "nisaba");
        Assert.Equal(new PendingDecision(DecisionKind.Snake, 0), s.Pending(0));
        Assert.Equal([Move.Snake(pair[1])], s.LegalMoves());
        Assert.Contains("not a green card in P2's city", s.WhyIllegal(Move.Snake(pair[0])));

        game.Apply(Move.Snake(pair[1]));
        Assert.Equal(pair[1], s.Pantheon!.Snake);
        Assert.Equal(2, s.SymbolCount(0, symbol));
        Assert.Equal(1, s.SymbolCount(1, symbol));
        Assert.True(s.City(1).Contains(pair[1]));
        Assert.Equal(new PendingDecision(DecisionKind.ChooseToken, 0), s.Pending(0));
        Assert.Equal($"P1 snake {GameData.Card(pair[1]).Id}", game.Log[^1].ToString());
        Assert.Contains($"Nisaba's Snake: {GameData.Card(pair[1]).Id}", s.ToString());
    }

    [Fact]
    public void Nisaba_counts_for_science_supremacy()
    {
        var game = AgeTwo(1, "nisaba", s => s.CardsOfColor(0, CardColor.Green) == 0 && s.CardsOfColor(1, CardColor.Green) == 0
            && !s.HasToken(1, ProgressTokens.Law));
        var s = game.State;
        // P2 shows 5 different symbols; P1 holds the 6th, which the Snake copies.
        static int CardOf(ScienceSymbol x) => (CardSets.OfSymbol[(int)x] & Green).Cards().First();
        foreach (var symbol in GreenSymbols[..5]) s.AddToCity(1, CardOf(symbol));
        var sixth = CardOf(GreenSymbols[5]);
        s.AddToCity(0, sixth);
        Assert.Equal(5, s.DistinctSymbols(1));
        Activate(game, "nisaba");
        game.Apply(Move.Snake(sixth));
        Assert.True(s.IsOver);
        Assert.Equal(1, s.Result!.Winner);
        Assert.Equal(VictoryType.Science, s.Result.Victory);
        Assert.Equal(0, s.PendingCount);
    }

    // ---------------------------------------------------------------- Baal

    [Fact]
    public void Baal_steals_a_brown_or_grey_card_with_its_production()
    {
        var game = AgeTwo(0, "baal", s => !(s.City(1) & BrownAndGrey).IsEmpty);
        var s = game.State;
        var targets = (s.City(1) & BrownAndGrey).Cards().ToArray();
        Activate(game, "baal");
        Assert.Equal(targets.Select(Move.Steal), s.LegalMoves());
        var card = targets[^1];
        var produce = GameData.Card(card).Produce;
        var before = Enumerable.Range(0, Resources.Count).Select(r => (s.Production(0, (Resource)r), s.Production(1, (Resource)r))).ToArray();
        game.Apply(Move.Steal(card));
        Assert.True(s.City(0).Contains(card));
        Assert.False(s.City(1).Contains(card));
        for (var r = 0; r < Resources.Count; r++)
        {
            Assert.Equal(before[r].Item1 + produce[r], s.Production(0, (Resource)r));
            Assert.Equal(before[r].Item2 - produce[r], s.Production(1, (Resource)r));
        }
        Assert.Equal($"P1 steal {GameData.Card(card).Id}", game.Log[^1].ToString());
        Assert.Equal(new PendingDecision(DecisionKind.TakeCard, 1), s.Pending(0));
    }

    // ---------------------------------------------------------------- Hades

    static bool Plain(int card) => GameData.Card(card) is { Coins: 0, CoinsPer: CoinsPerKind.None, Shields: 0, Science: null, IsGuild: false };

    [Fact]
    public void Hades_builds_a_discarded_card_for_free()
    {
        var game = AgeTwo(1, "hades", s => s.DiscardPile.Cards().Any(Plain));
        var s = game.State;
        var pile = s.DiscardPile.Cards().ToArray();
        var card = pile.First(Plain);
        Activate(game, "hades");
        Assert.Equal(pile.Select(Move.Hades), s.LegalMoves());
        game.Apply(Move.Hades(card));
        Assert.True(s.City(1).Contains(card));
        Assert.False(s.DiscardPile.Contains(card));
        Assert.Equal(0, s.Coins(1));
        Assert.Equal($"P2 hades {GameData.Card(card).Id}", game.Log[^1].ToString());
        Assert.Equal(new PendingDecision(DecisionKind.TakeCard, 0), s.Pending(0));
    }

    [Theory]
    [InlineData("hades")]
    [InlineData("isis")]
    public void Hades_and_isis_do_nothing_with_an_empty_discard_pile(string divinity)
    {
        var game = AgeTwo(0, divinity);
        var s = game.State;
        ClearDiscard(s);
        var cities = (s.City(0), s.City(1), s.BuiltWonders(0));
        Activate(game, divinity);
        Assert.Equal(cities, (s.City(0), s.City(1), s.BuiltWonders(0)));
        Assert.Equal(1 << D(divinity), s.Pantheon!.Activated(0));
        Assert.Equal(new PendingDecision(DecisionKind.TakeCard, 1), s.Pending(0));
        Assert.Equal(1, s.PendingCount);
    }

    // ---------------------------------------------------------------- Zeus

    /// <summary>An accessible slot whose card covers a face-down card that nothing else covers.</summary>
    static int UncoveringSlot(GameState s) => Accessible(s).FirstOrDefault(x =>
        GameData.Layout(s.Age).Slots[x].Covers.Any(t => s.IsOccupied(t) && s.SlotCard(t) < 0
            && GameData.Layout(s.Age).Slots[t].CoveredBy.All(c => c == x || !s.IsOccupied(c))), -1);

    [Fact]
    public void Zeus_discards_any_structure_card_by_slot_and_uncovered_cards_are_revealed()
    {
        var game = AgeTwo(0, "zeus", s => UncoveringSlot(s) >= 0);
        var s = game.State;
        var slot = UncoveringSlot(s);
        var card = s.SlotCard(slot);
        var occupied = Enumerable.Range(0, GameState.SlotCount).Where(s.IsOccupied).ToArray();
        Activate(game, "zeus");
        Assert.Equal(new PendingDecision(DecisionKind.Zeus, 0), s.Pending(0));
        // Every card of the structure, covered or face down, is a target.
        Assert.Equal(occupied.Select(x => Move.Zeus(x, 2)), s.LegalMoves());
        Assert.Contains("Age 2's", s.WhyIllegal(Move.Zeus(slot, 1)));

        game.Apply(Move.Zeus(slot, 2));
        Assert.Equal($"P1 zeus {GameData.Layout(2).SlotName(slot)}", game.Log[^1].ToString());
        Assert.True(s.DiscardPile.Contains(card));
        Assert.False(s.IsOccupied(slot));
        // The uncovered face-down card is flipped, then the opponent takes a card.
        Assert.Equal(StepKind.Reveal, s.Step);
        Assert.NotEqual(0u, s.SlotsToReveal);
        Assert.IsType<SlotReveal>(new RandomChanceSource(3).Next(s));
        Reveals(game);
        Assert.Equal(new PendingDecision(DecisionKind.TakeCard, 1), s.Pending(0));
    }

    [Fact]
    public void Zeus_on_a_face_down_card_holding_an_offering_token_reveals_it_and_takes_the_token()
    {
        static int TokenSlot(GameState s) => Enumerable.Range(0, GameState.SlotCount).FirstOrDefault(x =>
            s.IsOccupied(x) && s.SlotCard(x) < 0 && s.Pantheon!.SlotToken(x) >= PantheonState.OfferingToken, -1);
        var game = AgeTwo(1, "zeus", s => TokenSlot(s) >= 0);
        var s = game.State;
        var p = s.Pantheon!;
        var slot = TokenSlot(s);
        var offering = p.SlotToken(slot) - PantheonState.OfferingToken;
        Activate(game, "zeus");
        game.Apply(Move.Zeus(slot, 2));
        // The face-down card is named by a public Reveal before anything else.
        Assert.True(p.AwaitingZeusDiscard);
        Assert.Equal(StepKind.Reveal, s.Step);
        Assert.Equal(0, s.PendingCount);
        Assert.Empty(s.LegalMoves());
        Assert.True(s.IsOccupied(slot));
        var seen = s.Seen.Cards().First(c => GameData.Card(c).Age == 2);
        Assert.Throws<InvalidOperationException>(() => game.Apply(new ZeusDiscardReveal(seen)));
        var reveal = new RandomChanceSource(5).Next(s);
        var named = Assert.IsType<ZeusDiscardReveal>(reveal);
        Assert.Equal(-1, reveal.Audience);
        game.Apply(reveal);
        Assert.Equal($"reveal discarded {GameData.Card(named.Card).Id}", game.Log[^1].ToString());
        Assert.True(s.DiscardPile.Contains(named.Card));
        Assert.True(s.Seen.Contains(named.Card));
        Assert.False(s.IsOccupied(slot));
        Assert.False(p.AwaitingZeusDiscard);
        // R2: the player who used Zeus takes the token.
        Assert.Equal(PantheonState.NoToken, p.SlotToken(slot));
        Assert.NotEqual(0, p.OfferingTokens(1) & 1 << offering);
        Assert.Equal(0, p.OfferingTokens(0) & 1 << offering);
        Reveals(game);
        Assert.Equal(new PendingDecision(DecisionKind.TakeCard, 0), s.Pending(0));
    }

    // ---------------------------------------------------------------- Anubis

    [Fact]
    public void Anubis_unbuilds_a_wonder_which_its_owner_can_rebuild()
    {
        var game = AgeTwo(0, "anubis", s => s.BuiltWonders(0) != 0 && s.TotalWondersBuilt < 7 && s.CardsLeftInAge >= 4);
        var s = game.State;
        var wonder = GameState.WondersIn(s.BuiltWonders(0)).First();
        var card = s.WonderCard(wonder);
        var coins = s.Coins(1);
        Activate(game, "anubis");
        // Either player's built Wonders.
        Assert.Equal(GameState.WondersIn((ushort)(s.BuiltWonders(0) | s.BuiltWonders(1))).Select(Move.Anubis), s.LegalMoves());
        Assert.Contains("is not built", s.WhyIllegal(Move.Anubis(GameState.WondersIn(s.BuildableWonders(0)).FirstOrDefault(-1))));
        game.Apply(Move.Anubis(wonder));
        Assert.Equal($"P1 anubis {GameData.Wonder(wonder).Id}", game.Log[^1].ToString());
        Assert.False(s.IsWonderBuilt(wonder));
        Assert.Equal(-1, s.WonderCard(wonder));
        Assert.True(s.DiscardPile.Contains(card));
        Assert.NotEqual(0, s.BuildableWonders(0) & 1 << wonder);
        // What the Wonder gave stays given.
        Assert.Equal(0, s.Coins(0));
        Assert.Equal(coins, s.Coins(1));

        PantheonActivationTests.OpponentDiscards(game);
        s.SetCoins(0, 99);
        var tuck = s.SlotCard(Accessible(s)[0]);
        game.Apply(Move.BuildWonder(tuck, wonder));
        Assert.True(s.IsWonderBuilt(wonder));
        Assert.Equal(tuck, s.WonderCard(wonder));
    }

    /// <summary>
    /// Plays a Pantheon game: random Moves in Age I (no Wonders), then from Age II <paramref name="take"/> answers each
    /// take-card decision (random answers to other decisions), until <paramref name="stop"/> holds at a lone take-card decision.
    /// </summary>
    static Game? Play(ulong seed, Func<GameState, Move?> take, Func<GameState, bool> stop)
    {
        var game = new Game(Ruleset.Parse(Rules));
        var chance = new RandomChanceSource(seed);
        var rng = new Rng(seed + 77);
        var s = game.State;
        while (!s.IsOver)
        {
            if (s.Step == StepKind.Reveal) game.Apply(chance.Next(s));
            else if (s.PendingCount == 1 && s.Pending(0).Kind == DecisionKind.TakeCard && stop(s)) return game;
            else
            {
                var moves = s.LegalMoves().Where(m => m.Kind is not (MoveKind.Wonder or MoveKind.Activate)).ToList();
                var chosen = s.Age >= 2 && s.Pending(0).Kind == DecisionKind.TakeCard ? take(s) : null;
                game.Apply(chosen ?? moves[rng.Next(moves.Count)]);
            }
        }
        return null;
    }

    /// <summary>Builds a Wonder (first buildable, first accessible card) if <paramref name="allowed"/>, with coins enough; otherwise discards.</summary>
    static Move BuildWonderOrDiscard(GameState s, bool allowed)
    {
        var player = s.PlayerToDecide;
        var card = s.SlotCard(Accessible(s)[0]);
        if (!allowed || s.BuildableWonders(player) == 0) return Move.Discard(card);
        s.SetCoins(player, 99);
        return Move.BuildWonder(card, GameState.WondersIn(s.BuildableWonders(player)).First());
    }

    static void OnlyInPantheon(GameState s, string divinity)
    {
        var p = s.Pantheon!;
        for (var i = 0; i < PantheonData.SpaceCount; i++) p.Spaces[i] = (sbyte)PantheonState.EmptySpace;
        p.FaceDown = 0;
        p.Spaces[0] = (sbyte)D(divinity);
        p.HeldOfferings[0] = p.HeldOfferings[1] = 0;
    }

    [Fact]
    public void The_eighth_wonder_stays_removed_after_anubis()
    {
        Game? game = null;
        for (ulong seed = 0; game is null && seed < 200; seed++)
            game = Play(seed, s => BuildWonderOrDiscard(s, true), s => s.TotalWondersBuilt == 7 && s.CardsLeftInAge >= 4);
        Assert.NotNull(game);
        var s = game!.State;
        var player = s.PlayerToDecide;
        var removed = s.RemovedWonders;
        Assert.Equal(1, System.Numerics.BitOperations.PopCount(removed));
        OnlyInPantheon(s, "anubis");
        Activate(game, "anubis");
        var wonder = GameState.WondersIn(s.BuiltWonders(player)).First();
        game.Apply(Move.Anubis(wonder));
        Assert.Equal(6, s.TotalWondersBuilt);
        Assert.Equal(removed, s.RemovedWonders);
        // Only the unbuilt Wonder can be built again, never the 8th, even by its owner.
        Assert.Equal(1 << wonder, s.BuildableWonders(player));
        Assert.Equal(0, s.BuildableWonders(1 - player));

        PantheonActivationTests.OpponentDiscards(game);
        s.SetCoins(player, 99);
        game.Apply(Move.BuildWonder(s.SlotCard(Accessible(s)[0]), wonder));
        Assert.Equal(7, s.TotalWondersBuilt);
        Assert.Equal(removed, s.RemovedWonders);
    }

    // ---------------------------------------------------------------- Ra

    [Fact]
    public void Ra_takes_an_unbuilt_wonder_and_the_thief_can_build_5_of_7()
    {
        for (ulong seed = 0; seed < 200; seed++)
        {
            var game = Play(seed, _ => null, s => s.Age == 2 && s.PlayerToDecide == 0 && s.TotalWondersBuilt == 0);
            if (game is null) continue;
            var s = game.State;
            OnlyInPantheon(s, "ra");
            Activate(game, "ra");
            var targets = GameState.WondersIn(s.BuildableWonders(1)).ToArray();
            Assert.Equal(targets.Select(Move.Ra), s.LegalMoves());
            var stolen = targets[0];
            Assert.Contains("not an unbuilt Wonder of P2's", s.WhyIllegal(Move.Ra(GameState.WondersIn(s.Wonders(0)).First())));
            game.Apply(Move.Ra(stolen));
            Assert.Equal($"P1 ra {GameData.Wonder(stolen).Id}", game.Log[^1].ToString());
            Assert.True(s.OwnsWonder(0, stolen));
            Assert.False(s.OwnsWonder(1, stolen));
            Assert.Equal(5, System.Numerics.BitOperations.PopCount(s.Wonders(0)));
            Assert.Equal(3, System.Numerics.BitOperations.PopCount(s.Wonders(1)));

            // P1 builds all 5, P2 builds 2: the 7th Wonder removes P2's last one.
            var rng = new Rng(seed);
            var chance = new RandomChanceSource(seed + 9);
            while (!s.IsOver && s.TotalWondersBuilt < 7)
            {
                if (s.Step == StepKind.Reveal) { game.Apply(chance.Next(s)); continue; }
                var moves = s.LegalMoves().Where(m => m.Kind is not (MoveKind.Wonder or MoveKind.Activate)).ToList();
                game.Apply(s.Pending(0).Kind != DecisionKind.TakeCard ? moves[rng.Next(moves.Count)]
                    : BuildWonderOrDiscard(s, s.PlayerToDecide == 0 || s.WondersBuilt(1) < 2));
            }
            if (s.IsOver && s.TotalWondersBuilt < 7) continue;
            Assert.Equal(5, s.WondersBuilt(0));
            Assert.Equal(2, s.WondersBuilt(1));
            Assert.Equal(s.Wonders(1) & ~s.BuiltWonders(1), s.RemovedWonders);
            Assert.Equal(1, System.Numerics.BitOperations.PopCount(s.RemovedWonders));
            return;
        }
        Assert.Fail("No game reached 7 Wonders");
    }

    // ---------------------------------------------------------------- Isis

    [Fact]
    public void Isis_builds_an_own_wonder_for_free_with_a_discarded_card()
    {
        var game = AgeTwo(0, "isis", s => !s.DiscardPile.IsEmpty && s.BuildableWonders(0) != 0 && s.TotalWondersBuilt < 6);
        var s = game.State;
        var pile = s.DiscardPile.Cards().ToArray();
        var wonders = GameState.WondersIn(s.BuildableWonders(0)).ToArray();
        Activate(game, "isis");
        Assert.Equal(pile.SelectMany(c => wonders.Select(w => Move.Isis(c, w))), s.LegalMoves());
        var (card, wonder) = (pile[^1], wonders[^1]);
        Assert.Contains("not an unbuilt Wonder of P1's", s.WhyIllegal(Move.Isis(card, GameState.WondersIn(s.Wonders(1)).First())));
        game.Apply(Move.Isis(card, wonder));
        Assert.Equal($"P1 isis {GameData.Card(card).Id} {GameData.Wonder(wonder).Id}", game.Log[^1].ToString());
        Assert.True(s.IsWonderBuilt(wonder));
        Assert.Equal(card, s.WonderCard(wonder));
        Assert.False(s.DiscardPile.Contains(card));
        // Free: P1 paid nothing and gained the Wonder's coins.
        Assert.Equal(GameData.Wonder(wonder).Coins, s.Coins(0));
    }

    // ---------------------------------------------------------------- log, random games

    [Fact]
    public void The_log_writes_and_reads_the_divinity_moves()
    {
        var s = AgeTwo(0, "zeus").State;
        string[] lines =
        [
            "P1 enki law", "P2 snake library", "P1 steal quarry", "P2 hades baths", "P1 zeus 1.3", "P2 zeus 4.1",
            "P1 anubis pyramids", "P2 isis baths pyramids", "P1 ra colossus", "reveal discarded caravansery",
        ];
        foreach (var line in lines) Assert.Equal(line, MoveLog.ParseEntry(line, s).ToString());
        Assert.Equal(new MoveEntry(0, Move.Zeus(GameData.Layout(2).SlotAt(1, 3), 2)), MoveLog.ParseEntry("P1 zeus 1.3", s));
        Assert.Equal(new ZeusDiscardReveal(GameData.CardIndex("caravansery")), MoveLog.ParseEntry("reveal discarded caravansery", s));
        Assert.Throws<FormatException>(() => MoveLog.ParseEntry("P1 zeus 5.0", s));
        Assert.Throws<FormatException>(() => MoveLog.ParseEntry("P1 zeus baths", s));
        Assert.Throws<FormatException>(() => MoveLog.ParseEntry("P1 isis baths", s));
        Assert.Throws<FormatException>(() => MoveLog.ParseEntry("P1 ra", s));
        Assert.Throws<FormatException>(() => MoveLog.ParseEntry("P1 zeus 0.0", GameState.New(Ruleset.Parse(Rules))));
        // Outside their decision, the Divinities' Moves are illegal.
        Assert.Contains("no zeus decision", s.WhyIllegal(Move.Zeus(0, 2)));
    }

    [Fact]
    public void Random_pantheon_games_use_every_divinity_decision_and_replay()
    {
        var kinds = new Dictionary<DecisionKind, int>();
        var faceDownZeus = 0;
        for (ulong seed = 0; seed < 300; seed++)
        {
            var game = new Game(Ruleset.Parse(Rules));
            var chance = new RandomChanceSource(seed);
            var rng = new Rng(seed + 11);
            var s = game.State;
            while (!s.IsOver)
            {
                if (s.Step == StepKind.Reveal)
                {
                    var reveal = chance.Next(s);
                    if (reveal is ZeusDiscardReveal) faceDownZeus++;
                    game.Apply(reveal);
                    continue;
                }
                var moves = s.LegalMoves();
                Assert.True(moves.Count > 0);
                Assert.All(moves, m => Assert.Null(s.WhyIllegal(m)));
                var kind = s.Pending(0).Kind;
                if (kind > DecisionKind.Offer) kinds[kind] = kinds.GetValueOrDefault(kind) + 1;
                // Favour activations so every Divinity gets used.
                var activate = moves.Where(m => m.Kind == MoveKind.Activate).ToList();
                game.Apply(activate.Count > 0 && rng.Next(2) == 0 ? activate[rng.Next(activate.Count)] : moves[rng.Next(moves.Count)]);
                Assert.True(s.Coins(0) >= 0 && s.Coins(1) >= 0);
                Assert.True(s.TotalWondersBuilt <= GameState.MaxWondersBuilt);
            }
            var text = game.WriteLog();
            var replayed = MoveLog.Parse(text);
            Assert.True(replayed.State.SameAs(s));
            Assert.Equal(s.ToBytes(), replayed.State.ToBytes());
            Assert.Equal(text, replayed.WriteLog());
            Assert.Equal(s.ToBytes(), s.Clone().ToBytes());
        }
        foreach (var kind in new[] { DecisionKind.Enki, DecisionKind.Snake, DecisionKind.Steal, DecisionKind.Hades,
                     DecisionKind.Zeus, DecisionKind.Anubis, DecisionKind.Isis, DecisionKind.Ra })
            Assert.True(kinds.GetValueOrDefault(kind) > 0, $"no {kind} decision in {string.Join(", ", kinds)}");
        Assert.True(faceDownZeus > 0, "Zeus never discarded a face-down card");
    }
}
