using System.Numerics;
using SevenWondersDuel.Engine.Data;

namespace SevenWondersDuel.Engine.Tests;

/// <summary>
/// Pantheon ticket 02 (spec-pantheon P-A2, P-A3, P-A4): the bigger pools, the Age I Mythology tokens and the private
/// Divinity draws they cause, the Mythology decks, the Age II setup (Gate, Pantheon, Offering and Enki Reveals),
/// Age III's Grand Temples, the 3 new Progress tokens, and the Move Log for all of it.
/// </summary>
public class PantheonSetupTests
{
    const string Rules = "rules base expansion=pantheon";
    const string Tokens = "reveal tokens agriculture economy law strategy urbanism";
    /// <summary>Age I with every face-up card named (unseen: clay-pool clay-pit stone-pit pharmacist stable garrison workshop ...).</summary>
    const string Opening =
        "reveal age1 0.0 palisade 0.1 tavern 2.0 guard-tower 2.1 altar 2.2 lumber-yard 2.3 press "
        + "4.0 apothecary 4.1 baths 4.2 logging-camp 4.3 quarry 4.4 glassworks 4.5 stone-reserve";
    /// <summary>R1 slots 2, 4, 9, 11, 13 = 1.0, 1.2, 3.0, 3.2, 3.4.</summary>
    const string MythTokens = "reveal myth-tokens 1.0 greek 1.2 roman 3.0 egyptian 3.2 phoenician 3.4 greek";

    static int D(string slug) => PantheonData.DivinityIndex(slug);
    static int Temple(string mythology) => PantheonData.TempleOf(PantheonData.MythologyOf(mythology)).Card;
    static readonly int Mysticism = PantheonData.TokenIndex("mysticism");
    static readonly int Poliorcetics = PantheonData.TokenIndex("poliorcetics");
    static readonly int Engineering = PantheonData.TokenIndex("engineering");

    /// <summary>A Pantheon game in Age I after the Mythology tokens, P1 first; both players get 50 coins.</summary>
    static Game Start(params string[] lines)
    {
        var game = MoveLog.Parse(string.Join('\n',
            ["7wd-log v1", Rules, "reveal first P1", Tokens, .. Setup.Draft(0), Opening, MythTokens, .. lines]));
        game.State.SetCoins(0, 50);
        game.State.SetCoins(1, 50);
        return game;
    }

    static void Do(Game game, params string[] lines)
    {
        foreach (var line in lines) game.Apply(MoveLog.ParseEntry(line, game.State));
    }

    /// <summary>Plays random Pantheon games (random Reveals and Moves) until a Decision satisfies <paramref name="stop"/>.</summary>
    static Game PlayUntil(Func<GameState, bool> stop, string rules = Rules, int seeds = 400)
    {
        for (ulong seed = 0; seed < (ulong)seeds; seed++)
        {
            var game = new Game(Ruleset.Parse(rules));
            var chance = new RandomChanceSource(seed);
            var rng = new Rng(seed + 1000);
            while (!game.State.IsOver)
            {
                if (game.State.Step == StepKind.Reveal) game.Apply(chance.Next(game.State));
                else if (stop(game.State)) return game;
                else
                {
                    var moves = game.State.LegalMoves();
                    game.Apply(moves[rng.Next(moves.Count)]);
                }
            }
        }
        throw new InvalidOperationException("No random game reached the position");
    }

    /// <summary>Plays random Reveals and Moves until the state waits for a Reveal that <paramref name="stop"/> accepts.</summary>
    static Game PlayUntilReveal(Func<GameState, bool> stop, int seeds = 400)
    {
        for (ulong seed = 0; seed < (ulong)seeds; seed++)
        {
            var game = new Game(Ruleset.Parse(Rules));
            var chance = new RandomChanceSource(seed);
            var rng = new Rng(seed + 1000);
            while (!game.State.IsOver)
            {
                if (game.State.Step == StepKind.Reveal)
                {
                    if (stop(game.State)) return game;
                    game.Apply(chance.Next(game.State));
                }
                else
                {
                    var moves = game.State.LegalMoves();
                    game.Apply(moves[rng.Next(moves.Count)]);
                }
            }
        }
        throw new InvalidOperationException("No random game reached the position");
    }

    // ---------------------------------------------------------------- pools

    [Fact]
    public void Pantheon_drafts_from_14_to_16_wonders_and_sets_out_tokens_from_13()
    {
        Assert.Equal(14, BitOperations.PopCount(GameState.New(Ruleset.Parse(Rules)).WonderPool));
        Assert.Equal(16, BitOperations.PopCount(GameState.New(Ruleset.Parse("rules base promo=messe,liberty expansion=pantheon")).WonderPool));
        Assert.Equal(13, BitOperations.PopCount(GameState.New(Ruleset.Parse(Rules)).TokenPool));
        Assert.Equal(10, BitOperations.PopCount(GameState.New(Ruleset.Default).TokenPool));
        Assert.Equal(12, BitOperations.PopCount(GameState.New(Ruleset.Default).WonderPool));

        var game = MoveLog.Parse(string.Join('\n', ["7wd-log v1", Rules, "reveal first P2",
            "reveal tokens mysticism poliorcetics engineering law economy", "reveal draft sanctuary divine-theater pyramids sphinx"]));
        Assert.True(game.State.TokenOnBoard(Mysticism));
        Assert.Equal(DecisionKind.DraftWonder, game.State.Pending(0).Kind);

        // A base game knows the slugs but not the identities.
        var e = Assert.Throws<FormatException>(() => MoveLog.Parse(string.Join('\n',
            ["7wd-log v1", "rules base", "reveal first P1", "reveal tokens mysticism economy law strategy urbanism"])));
        Assert.Contains("mysticism", e.Message);
        e = Assert.Throws<FormatException>(() => MoveLog.Parse(string.Join('\n',
            ["7wd-log v1", "rules base", "reveal first P1", Tokens, "reveal draft sanctuary pyramids colossus sphinx"])));
        Assert.Contains("not in this game's Wonder pool", e.Message);
    }

    [Fact]
    public void Random_pantheon_games_use_the_new_wonders_and_tokens()
    {
        var wonders = 0;
        var tokens = 0;
        for (ulong seed = 0; seed < 200; seed++)
        {
            var s = GameState.New(Ruleset.Parse(Rules));
            var chance = new RandomChanceSource(seed);
            s.Apply(chance.Next(s));
            s.Apply(chance.Next(s));
            tokens |= s.TokensSetOut;
            s.Apply(chance.Next(s));
            wonders |= s.DraftOffer;
        }
        Assert.Equal(0x1FFF, tokens);
        Assert.Equal(PantheonData.Wonders.Aggregate(0, (m, w) => m | 1 << w.Index), wonders & 0xC000);
    }

    // ---------------------------------------------------------------- Age I tokens

    [Fact]
    public void The_mythology_tokens_are_revealed_after_the_age_one_cards_on_the_rulebook_slots()
    {
        var game = MoveLog.Parse(string.Join('\n', ["7wd-log v1", Rules, "reveal first P1", Tokens, .. Setup.Draft(0)]));
        var s = game.State;
        var early = Assert.Throws<InvalidOperationException>(() => s.Apply((Reveal)MoveLog.ParseEntry(MythTokens, s)));
        Assert.Contains("revealed first", early.Message);
        Do(game, Opening);
        Assert.Equal(StepKind.Reveal, s.Step);
        Assert.True(s.Pantheon!.AwaitingMythologyTokens);
        Assert.Empty(s.LegalMoves());

        Assert.Throws<InvalidOperationException>(() => s.Apply((Reveal)MoveLog.ParseEntry(
            "reveal myth-tokens 1.0 greek 1.1 roman 3.0 egyptian 3.2 phoenician 3.4 greek", s)));
        Assert.Throws<InvalidOperationException>(() => s.Apply((Reveal)MoveLog.ParseEntry(
            "reveal myth-tokens 1.0 greek 1.2 greek 3.0 greek 3.2 phoenician 3.4 roman", s)));
        Do(game, MythTokens);
        Assert.Equal(StepKind.Decision, s.Step);
        Assert.Equal((int)Mythology.Greek, s.Pantheon.SlotToken(2));
        Assert.Equal((int)Mythology.Roman, s.Pantheon.SlotToken(4));
        Assert.Equal((int)Mythology.Egyptian, s.Pantheon.SlotToken(9));
        Assert.Equal((int)Mythology.Greek, s.Pantheon.SlotToken(13));
        Assert.Equal(PantheonState.NoToken, s.Pantheon.SlotToken(10));
        Assert.Throws<InvalidOperationException>(() => s.Apply((Reveal)MoveLog.ParseEntry(MythTokens, s)));
    }

    [Fact]
    public void Flipping_a_token_card_gives_the_token_to_the_player_whose_turn_flipped_it_then_a_private_draw_and_placement()
    {
        // P1 takes 4.0; P2 takes 4.1, which flips 3.0 with the Egyptian token: P2's token, P2's draw.
        var game = Start("P1 discard apothecary", "P2 discard baths");
        var s = game.State;
        var p = s.Pantheon!;
        Assert.Equal(1, p.MythologyTokens(1, Mythology.Egyptian));
        Assert.Equal(PantheonState.NoToken, p.SlotToken(9));
        Assert.Equal(new PendingDecision(DecisionKind.PlaceDivinity, 1, (int)Mythology.Egyptian), s.Pending(0));
        Assert.Equal(new PendingDecision(DecisionKind.TakeCard, 0), s.Pending(1));

        // Owner ruling 2026-10-26: the private draw and the placement come before the uncovered card is flipped.
        Assert.Equal(StepKind.Reveal, s.Step);
        Assert.True(s.FlipsHeld);
        Assert.Equal(0u, s.SlotsToReveal);
        Assert.True(s.IsAccessible(9));
        Assert.Equal(GameState.Hidden, s.SlotCard(9));
        var early = Assert.Throws<InvalidOperationException>(() => s.Apply((Reveal)MoveLog.ParseEntry("reveal 3.0 stable", s)));
        Assert.Contains("before the uncovered cards are revealed", early.Message);
        Assert.IsType<DivinityDrawReveal>(new RandomChanceSource(3).Next(s));
        var draw = MoveLog.ParseEntry("reveal P2 divinities egyptian anubis isis", s);
        Assert.Throws<InvalidOperationException>(() => s.Apply((Reveal)MoveLog.ParseEntry("reveal P1 divinities egyptian anubis isis", s)));
        Assert.Throws<InvalidOperationException>(() => s.Apply((Reveal)MoveLog.ParseEntry("reveal P2 divinities greek zeus hades", s)));
        Assert.Throws<InvalidOperationException>(() => s.Apply((Reveal)MoveLog.ParseEntry("reveal P2 divinities egyptian anubis mars", s)));
        game.Apply(draw);
        Assert.Equal(1, ((Reveal)draw).Audience);
        Assert.Equal(StepKind.Decision, s.Step);
        Assert.Equal(1, s.PlayerToDecide);
        Assert.Equal(12, s.LegalMoves().Count);
        Assert.All(s.LegalMoves(), m => Assert.Equal(MoveKind.Divinity, m.Kind));
        Assert.Equal(1, p.DeckSize(Mythology.Egyptian));

        Assert.False(s.IsLegal(Move.PlaceDivinity(D("ra"), 3)));
        Assert.False(s.IsLegal(Move.Discard(GameData.CardIndex("logging-camp"))));
        Do(game, "P2 divinity isis 4");
        Assert.Equal(D("isis"), p.Space(3));
        Assert.True(p.IsFaceDown(3));
        Assert.Equal(1, p.Placer(3));
        // Anubis goes back on top, known to P2 only.
        Assert.Equal(2, p.DeckSize(Mythology.Egyptian));
        Assert.Equal(D("anubis"), p.DeckCard(Mythology.Egyptian, 0));
        Assert.Equal(2, p.DeckKnownBy(Mythology.Egyptian, 0));
        Assert.Equal(PantheonState.Unknown, p.DeckCard(Mythology.Egyptian, 1));
        Assert.Equal(new PendingDecision(DecisionKind.TakeCard, 0), s.Pending(0));
        Assert.False(s.IsLegal(Move.PlaceDivinity(D("anubis"), 0)));
        Assert.Equal("P2 divinity isis 4", game.Log[^1].ToString());

        // Only now is the uncovered card flipped, before P1's turn.
        Assert.False(s.FlipsHeld);
        Assert.Equal(StepKind.Reveal, s.Step);
        Assert.Equal(1u << 9, s.SlotsToReveal);
        Do(game, "reveal 3.0 stable");
        Assert.Equal(StepKind.Decision, s.Step);
        Assert.Equal(0, s.PlayerToDecide);
    }

    [Theory]
    [InlineData("reveal P2 divinities egyptian anubis isis", "P2 divinity isis 4")]
    [InlineData("reveal P2 divinities egyptian ? ?", "P2 divinity ? 4")]
    public void An_old_order_log_loads_with_the_flip_moved_after_the_placement(string draw, string place)
    {
        // Logs written before the owner ruling of 2026-10-26 flipped first ("reveal 3.0 stable"), then drew and placed.
        string[] head = ["7wd-log v1", Rules, "reveal first P1", Tokens, .. Setup.Draft(0), Opening, MythTokens,
            "P1 discard apothecary", "P2 discard baths"];
        var n = head.Length - 2; // entries before the flip
        var old = MoveLog.Parse(string.Join('\n', [.. head, "reveal 3.0 stable", draw, place, "# a comment", "P1 discard stable"]), out var order);
        var now = MoveLog.Parse(string.Join('\n', [.. head, draw, place, "reveal 3.0 stable", "P1 discard stable"]), out var same);
        Assert.Equal(now.WriteLog(), old.WriteLog());
        Assert.True(old.State.SameAs(now.State));
        Assert.Equal([n + 1, n + 2, n, n + 3], order.Skip(n).ToArray());
        Assert.Equal(Enumerable.Range(0, n + 4), same);

        // A log that ends between the old flip and the placement drops the flip: it is due again after the placement.
        var cut = MoveLog.Parse(string.Join('\n', [.. head, "reveal 3.0 stable", draw]), out var cutOrder);
        Assert.Equal(n + 1, cut.Log.Count);
        Assert.Equal([n + 1], cutOrder.Skip(n).ToArray());
        Assert.Equal(StepKind.Decision, cut.State.Step);
        Assert.True(cut.State.FlipsHeld);

        // A wrong flip still names its own line.
        var e = Assert.Throws<FormatException>(() => MoveLog.Parse(string.Join('\n', [.. head, "reveal 3.0 apothecary", draw, place])));
        Assert.Contains($"line {head.Length + 1} ", e.Message);
    }

    [Fact]
    public void Random_games_place_every_token_divinity_before_the_flips_of_its_turn()
    {
        // Owner ruling 2026-10-26: in every random game, no flip comes between a token's draw and its placement, and a
        // turn's flips never come before its draws.
        var draws = 0;
        for (ulong seed = 0; seed < 40; seed++)
        {
            var game = new Game(Ruleset.Parse(Rules));
            var chance = new RandomChanceSource(seed);
            var rng = new Rng(seed + 1000);
            while (!game.State.IsOver)
            {
                if (game.State.Step == StepKind.Reveal) game.Apply(chance.Next(game.State));
                else
                {
                    var moves = game.State.LegalMoves();
                    game.Apply(moves[rng.Next(moves.Count)]);
                }
            }
            var log = game.Log;
            for (var i = 1; i < log.Count; i++)
            {
                if (log[i] is not DivinityDrawReveal) continue;
                draws++;
                Assert.IsNotType<SlotReveal>(log[i - 1]);
                Assert.IsType<MoveEntry>(log[i + 1]);
                Assert.Equal(MoveKind.Divinity, ((MoveEntry)log[i + 1]).Move.Kind);
            }
        }
        Assert.True(draws > 100);
    }

    [Fact]
    public void A_token_is_taken_before_a_play_again_turn()
    {
        // P1 builds the Temple of Artemis (play again) with 4.1, flipping 3.0: the token's decision comes first.
        var game = Start("P1 discard apothecary", "P2 discard logging-camp", "P1 wonder baths temple-of-artemis");
        var s = game.State;
        Assert.Equal(new PendingDecision(DecisionKind.PlaceDivinity, 0, (int)Mythology.Egyptian), s.Pending(0));
        Assert.Equal(new PendingDecision(DecisionKind.TakeCard, 0), s.Pending(1));
        Do(game, "reveal P1 divinities egyptian ra anubis", "P1 divinity ra 1", "reveal 3.0 stable 3.1 garrison");
        Assert.Equal(0, s.PlayerToDecide);
        Assert.Equal(DecisionKind.TakeCard, s.Pending(0).Kind);
        Assert.Equal(1, s.Pantheon!.MythologyTokens(0, Mythology.Egyptian));
    }

    [Fact]
    public void Two_tokens_from_one_flip_queue_two_decisions_and_the_returned_card_is_drawn_first_by_the_next_token()
    {
        // Put a second Greek token on 3.1 (next to 3.0): the baths cover both.
        var game = Start();
        var p = game.State.Pantheon!;
        p.SlotTokens[9] = (sbyte)Mythology.Greek;
        p.SlotTokens[10] = (sbyte)Mythology.Greek;
        p.SlotTokens[13] = PantheonState.NoToken;
        Do(game, "P1 discard apothecary", "P2 discard logging-camp", "P1 discard baths");
        var s = game.State;
        Assert.Equal(2, p.MythologyTokens(0, Mythology.Greek));
        Assert.Equal(new PendingDecision(DecisionKind.PlaceDivinity, 0, (int)Mythology.Greek), s.Pending(0));
        Assert.Equal(new PendingDecision(DecisionKind.PlaceDivinity, 0, (int)Mythology.Greek), s.Pending(1));
        Assert.Equal(new PendingDecision(DecisionKind.TakeCard, 1), s.Pending(2));

        // Both draws and placements come before the flips (owner ruling 2026-10-26).
        Do(game, "reveal P1 divinities greek zeus hades", "P1 divinity zeus 2");
        Assert.Equal(StepKind.Reveal, s.Step);
        Assert.True(s.FlipsHeld);
        Assert.Equal(D("hades"), p.DeckCard(Mythology.Greek, 0));
        // The second draw takes Hades (the known top) first, then the last unseen card.
        var wrong = Assert.Throws<InvalidOperationException>(() => s.Apply((Reveal)MoveLog.ParseEntry("reveal P1 divinities greek aphrodite hades", s)));
        Assert.Contains("hades", wrong.Message);
        Assert.Throws<InvalidOperationException>(() => s.Apply((Reveal)MoveLog.ParseEntry("reveal P1 divinities greek hades zeus", s)));
        var random = (DivinityDrawReveal)new RandomChanceSource(7).Next(s);
        Assert.Equal([D("hades"), D("aphrodite")], random.Divinities);
        Do(game, "reveal P1 divinities greek hades aphrodite");
        Assert.Equal(0, p.DeckSize(Mythology.Greek));
        Assert.False(s.IsLegal(Move.PlaceDivinity(D("aphrodite"), 1)));
        Do(game, "P1 divinity aphrodite 6");
        Assert.Equal(1, p.DeckSize(Mythology.Greek));
        Assert.Equal(D("hades"), p.DeckCard(Mythology.Greek, 0));
        Assert.False(s.FlipsHeld);
        Do(game, "reveal 3.0 stable 3.1 garrison");
        Assert.Equal(1, s.PlayerToDecide);
        Assert.Equal(DecisionKind.TakeCard, s.Pending(0).Kind);
    }

    [Fact]
    public void A_later_token_drawn_by_the_opponent_shows_them_the_returned_card()
    {
        var game = Start();
        var p = game.State.Pantheon!;
        p.SlotTokens[2] = (sbyte)Mythology.Egyptian;
        p.SlotTokens[9] = (sbyte)Mythology.Greek;
        // P2 flips 3.0 (Greek), then P1 flips 3.4 (Greek): P1's draw starts with the card P2 put back.
        Do(game, "P1 discard apothecary", "P2 discard baths",
            "reveal P2 divinities greek aphrodite zeus", "P2 divinity zeus 5", "reveal 3.0 stable",
            "P1 discard glassworks", "P2 discard logging-camp", "reveal 3.1 garrison", "P1 discard stone-reserve");
        Assert.Equal(1, p.MythologyTokens(0, Mythology.Greek));
        Assert.Equal(1, p.MythologyTokens(1, Mythology.Greek));
        Assert.Equal(StepKind.Reveal, game.State.Step);
        Do(game, "reveal P1 divinities greek aphrodite hades", "P1 divinity hades 1", "reveal 3.4 workshop");
        Assert.Equal(D("aphrodite"), p.DeckCard(Mythology.Greek, 0));
        Assert.Equal(1, p.DeckKnownBy(Mythology.Greek, 0));
    }

    // ---------------------------------------------------------------- Age II setup

    [Fact]
    public void Age_two_puts_the_gate_in_the_empty_space_then_reveals_the_pantheon_the_offerings_and_enki()
    {
        var sawEnki = false;
        var sawNoEnki = false;
        for (ulong seed = 0; seed < 60 && !(sawEnki && sawNoEnki); seed++)
        {
            var game = new Game(Ruleset.Parse(Rules));
            var s = game.State;
            var chance = new RandomChanceSource(seed);
            var rng = new Rng(seed);
            while (s.Age < 2)
            {
                if (s.Step == StepKind.Reveal) game.Apply(chance.Next(s));
                else
                {
                    var moves = s.LegalMoves();
                    game.Apply(moves[rng.Next(moves.Count)]);
                }
                if (s.IsOver) break;
            }
            if (s.IsOver) continue;
            var p = s.Pantheon!;
            // All 5 Mythology tokens were taken in Age I, so 5 Divinities lie face down and the Gate fills the 6th space.
            Assert.Equal(5, p.MythologyTokenCount(0) + p.MythologyTokenCount(1));
            Assert.Equal(5, BitOperations.PopCount(p.FaceDown));
            var gate = Enumerable.Range(0, 6).Single(i => p.Space(i) == PantheonData.Gate);
            Assert.False(p.IsFaceDown(gate));

            var reveals = new List<string>();
            while (s.Step == StepKind.Reveal)
            {
                var r = chance.Next(s);
                reveals.Add(r.ToString()!);
                game.Apply(r);
            }
            var enki = Enumerable.Range(0, 6).Any(i => p.Space(i) == D("enki"));
            Assert.StartsWith("reveal age2 ", reveals[0]);
            Assert.StartsWith("reveal pantheon ", reveals[1]);
            Assert.StartsWith("reveal offerings ", reveals[2]);
            Assert.Equal(enki ? 4 : 3, reveals.Count);
            if (enki)
            {
                Assert.StartsWith("reveal enki ", reveals[3]);
                Assert.Equal(2, BitOperations.PopCount(p.Enki));
                Assert.Equal(0, p.Enki & s.TokensSetOut);
                Assert.Equal(0, p.Enki & s.TokensSetAside);
                sawEnki = true;
            }
            else sawNoEnki = true;
            Assert.Equal(0, p.FaceDown);
            Assert.Equal([0, 1, 2], new[] { 6, 8, 10 }.Select(slot => p.SlotToken(slot) - PantheonState.OfferingToken).Order());
            Assert.Equal(StepKind.Decision, s.Step);
        }
        Assert.True(sawEnki && sawNoEnki);
    }

    [Fact]
    public void The_age_two_reveals_are_checked()
    {
        var game = PlayUntilReveal(s => s.Pantheon!.AwaitingPantheon && s.SlotsToReveal == 0);
        var s = game.State;
        var p = s.Pantheon!;
        var down = Enumerable.Range(0, 6).Where(p.IsFaceDown).ToArray();
        string Named(int skip = -1, int wrong = -1) => "reveal pantheon " + string.Join(' ', down.Where(i => i != skip)
            .Select(i => $"{i + 1} {PantheonData.Divinities[i == wrong ? (p.Space(i) + 1) % 15 : p.Space(i)].Id}"));
        Assert.Throws<InvalidOperationException>(() => s.Apply((Reveal)MoveLog.ParseEntry("reveal offerings 1.0 offering-2 1.2 offering-3 1.4 offering-4", s)));
        Assert.Throws<InvalidOperationException>(() => s.Apply((Reveal)MoveLog.ParseEntry(Named(skip: down[0]), s)));
        Assert.Throws<InvalidOperationException>(() => s.Apply((Reveal)MoveLog.ParseEntry(Named(wrong: down[1]), s)));
        Do(game, Named());
        Assert.Throws<InvalidOperationException>(() => s.Apply((Reveal)MoveLog.ParseEntry("reveal offerings 1.0 offering-2 1.2 offering-2 1.4 offering-4", s)));
        Assert.Throws<InvalidOperationException>(() => s.Apply((Reveal)MoveLog.ParseEntry("reveal offerings 1.0 offering-2 1.1 offering-3 1.4 offering-4", s)));
        Do(game, "reveal offerings 1.0 offering-4 1.2 offering-2 1.4 offering-3");
        Assert.Equal(PantheonState.OfferingToken + 2, p.SlotToken(6));
    }

    [Fact]
    public void Offering_tokens_are_taken_like_mythology_tokens_without_a_decision()
    {
        var game = PlayUntil(s => s.Age == 2 && s.Pending(0).Kind == DecisionKind.TakeCard
            && Enumerable.Range(0, 20).Any(slot => s.Pantheon!.SlotToken(slot) >= PantheonState.OfferingToken
                && s.Layout.Slots[slot].CoveredBy.Count(c => s.IsOccupied(c)) == 1
                && s.Layout.Slots[slot].CoveredBy.Any(c => s.IsAccessible(c))));
        var s = game.State;
        var p = s.Pantheon!;
        var slot = Enumerable.Range(0, 20).First(i => p.SlotToken(i) >= PantheonState.OfferingToken
            && s.Layout.Slots[i].CoveredBy.Count(c => s.IsOccupied(c)) == 1
            && s.Layout.Slots[i].CoveredBy.Any(c => s.IsAccessible(c)));
        var offering = p.SlotToken(slot) - PantheonState.OfferingToken;
        var cover = s.Layout.Slots[slot].CoveredBy.Single(s.IsOccupied);
        var player = s.PlayerToDecide;
        game.Apply(Move.Discard(s.SlotCard(cover)));
        // Held before the uncovered card is flipped, as a Mythology token is.
        Assert.NotEqual(0u, s.SlotsToReveal & 1u << slot);
        Assert.Equal(1 << offering, p.OfferingTokens(player) & 1 << offering);
        Assert.Equal(PantheonState.NoToken, p.SlotToken(slot));
        Assert.NotEqual(DecisionKind.PlaceDivinity, s.Pending(0).Kind);
    }

    // ---------------------------------------------------------------- Age III and the Grand Temples

    [Fact]
    public void Age_three_shows_temple_backs_and_never_uses_guilds()
    {
        var game = PlayUntilReveal(s => s.AwaitingGuildPattern);
        var s = game.State;
        Assert.True(s.TempleBacks);
        Assert.Equal(PantheonData.TempleSet, s.UnseenGuilds);
        var e = Assert.Throws<InvalidOperationException>(() => s.Apply(new GuildPatternReveal([0, 5, 10])));
        Assert.Contains("reveal temples", e.Message);
        var reveal = new RandomChanceSource(3).Next(s);
        Assert.StartsWith("reveal temples ", reveal.ToString());
        game.Apply(reveal);
        Assert.Equal(StepKind.Reveal, s.Step);
        Assert.Throws<InvalidOperationException>(() => GameState.New(Ruleset.Default).Apply(new GuildPatternReveal([0, 1, 2], Temples: true)));

        for (ulong seed = 0; seed < 30; seed++)
        {
            var g = PlayToEnd(seed);
            Assert.True((g.State.Seen & CardSets.Guilds).IsEmpty);
            if (g.State.Result!.Victory == VictoryType.Civilian) Assert.Equal(3, (g.State.Seen & PantheonData.TempleSet).Count);
        }
    }

    [Fact]
    public void A_grand_temple_costs_its_resources_or_nothing_while_holding_its_token_and_is_not_a_chain()
    {
        var game = PlayUntil(s => s.Age == 3 && s.Pending(0).Kind == DecisionKind.TakeCard
            && Enumerable.Range(0, 20).Any(i => s.IsAccessible(i) && PantheonData.TempleSet.Contains(s.SlotCard(i))));
        var s = game.State;
        var player = s.PlayerToDecide;
        var temple = s.SlotCard(Enumerable.Range(0, 20).First(i => s.IsAccessible(i) && PantheonData.TempleSet.Contains(s.SlotCard(i))));
        var mythology = PantheonData.Temples[temple - GameData.Cards.Length].Mythology;
        var p = s.Pantheon!;
        // Without the token: 5 resources, bought at 2 each by a player with no production.
        var fresh = GameState.New(Ruleset.Parse(Rules));
        Assert.Equal(new PaymentQuote(0, 10), Payment.Quote(fresh, 0, temple));
        Assert.Equal(PantheonData.Temples[temple - GameData.Cards.Length].Cost.Resources, GameData.Card(temple).Cost.Resources);

        p.HeldMythology[player * 5 + (int)mythology] = 1;
        Assert.Equal(0, Payment.Quote(s, player, temple).Total);
        // R9: free, but not a chain, so Urbanism pays nothing; the token is kept.
        s.SetBoardTokens((ushort)(s.BoardTokens | 1 << ProgressTokens.Urbanism));
        s.TakeToken(player, ProgressTokens.Urbanism);
        s.SetCoins(player, 9);
        game.Apply(Move.Build(temple));
        Assert.Equal(9, s.Coins(player));
        Assert.True(s.City(player).Contains(temple));
        Assert.Equal(1, p.MythologyTokens(player, mythology));
        Assert.Equal("P" + (player + 1) + " build " + PantheonData.Temples[temple - GameData.Cards.Length].Id, game.Log[^1].ToString());
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 5)]
    [InlineData(2, 12)]
    [InlineData(3, 21)]
    public void Grand_temples_score_5_12_or_21(int temples, int points)
    {
        var s = GameState.New(Ruleset.Parse(Rules));
        foreach (var m in new[] { "greek", "roman", "egyptian" }.Take(temples)) s.AddToCity(0, Temple(m));
        var sheet = Scoring.Score(s, 0);
        Assert.Equal(points, sheet[ScoreCategory.Temples]);
        Assert.Equal(points, sheet.Total - Scoring.Score(GameState.New(Ruleset.Parse(Rules)), 0).Total);
        Assert.Equal(0, Scoring.Score(s, 1)[ScoreCategory.Temples]);
    }

    // ---------------------------------------------------------------- the 3 new Progress tokens

    [Fact]
    public void Mysticism_scores_2_vp_per_mythology_and_offering_token_still_held()
    {
        var s = GameState.New(Ruleset.Parse(Rules));
        var p = s.Pantheon!;
        p.HeldMythology[(int)Mythology.Greek] = 2;
        p.HeldMythology[(int)Mythology.Roman] = 1;
        p.HeldOfferings[0] = 0b101;
        p.HeldMythology[5 + (int)Mythology.Egyptian] = 1;
        Assert.Equal(0, Scoring.Score(s, 0)[ScoreCategory.Progress]);
        s.SetBoardTokens(1 << 10);
        s.TakeToken(0, Mysticism);
        Assert.Equal(2 * 5, Scoring.Score(s, 0)[ScoreCategory.Progress]);
        Assert.Equal(0, Scoring.Score(s, 1)[ScoreCategory.Progress]);
    }

    [Fact]
    public void Poliorcetics_costs_the_opponent_a_coin_per_space_the_owner_moves_the_pawn()
    {
        var s = GameState.New(Ruleset.Parse(Rules));
        s.SetBoardTokens((ushort)(1 << Poliorcetics));
        s.TakeToken(0, Poliorcetics);
        s.SetCoins(1, 10);
        s.AddShields(0, 2);
        Assert.Equal(8, s.Coins(1));
        // Into the 2-coin zone: 1 more for Poliorcetics, 2 for the military token.
        s.AddShields(0, 1);
        Assert.Equal(5, s.Coins(1));
        // The opponent's shields cost the owner nothing, and coins never go below 0.
        s.SetCoins(0, 4);
        s.AddShields(1, 2);
        Assert.Equal(4, s.Coins(0));
        s.SetCoins(1, 1);
        s.AddShields(0, 3);
        Assert.Equal(0, s.Coins(1));
    }

    [Fact]
    public void Engineering_builds_a_card_with_a_chain_symbol_for_1_coin_and_urbanism_does_not_pay()
    {
        static bool Fits(GameState s, int slot)
        {
            if (!s.IsAccessible(slot)) return false;
            var c = GameData.Card(s.SlotCard(slot));
            return c.FreeIf >= 0 && c.Coins == 0 && c.CoinsPer == CoinsPerKind.None
                && !Payment.IsChained(s, s.PlayerToDecide, c.Index) && Payment.Quote(s, s.PlayerToDecide, c.Index).Total > 1;
        }
        var game = PlayUntil(s => s.Pending(0).Kind == DecisionKind.TakeCard && Enumerable.Range(0, 20).Any(i => Fits(s, i)));
        var s = game.State;
        var player = s.PlayerToDecide;
        var card = s.SlotCard(Enumerable.Range(0, 20).First(i => Fits(s, i)));
        s.SetBoardTokens((ushort)(s.BoardTokens | 1 << Engineering | 1 << ProgressTokens.Urbanism));
        s.TakeToken(player, Engineering);
        s.TakeToken(player, ProgressTokens.Urbanism);
        Assert.Equal(new PaymentQuote(1, 0), Payment.Quote(s, player, card));
        // Not a chain build: Urbanism pays nothing.
        s.SetCoins(player, 20);
        game.Apply(Move.Build(card));
        Assert.Equal(19, s.Coins(player));
    }

    [Fact]
    public void Engineering_does_not_touch_cards_without_a_chain_symbol_or_cheaper_quotes()
    {
        var s = GameState.New(Ruleset.Parse(Rules));
        s.SetBoardTokens((ushort)(1 << Engineering));
        s.TakeToken(0, Engineering);
        var noChain = GameData.Cards.First(c => c.FreeIf < 0 && c.Cost.ResourceTotal >= 2 && !c.IsGuild).Index;
        Assert.True(Payment.Quote(s, 0, noChain).Total > 1);
        var chainable = GameData.Cards.Where(c => c.FreeIf >= 0).ToList();
        Assert.All(chainable, c => Assert.True(Payment.Quote(s, 0, c.Index).Total <= 1, c.Id));
        Assert.Contains(chainable, c => Payment.Quote(GameState.New(Ruleset.Parse(Rules)), 0, c.Index).Total > 1);
        // Already chained: free.
        var chained = chainable.First();
        s.AddToCity(0, chained.FreeIf);
        Assert.Equal(0, Payment.Quote(s, 0, chained.Index).Total);
    }

    // ---------------------------------------------------------------- whole games and the log

    static Game PlayToEnd(ulong seed, string rules = Rules)
    {
        var game = new Game(Ruleset.Parse(rules));
        var chance = new RandomChanceSource(seed);
        var rng = new Rng(seed + 7);
        while (!game.State.IsOver)
        {
            if (game.State.Step == StepKind.Reveal) game.Apply(chance.Next(game.State));
            else
            {
                var moves = game.State.LegalMoves();
                Assert.NotEmpty(moves);
                Assert.All(moves, m => Assert.Null(game.State.WhyIllegal(m)));
                game.Apply(moves[rng.Next(moves.Count)]);
            }
        }
        return game;
    }

    [Fact]
    public void Random_pantheon_games_reach_the_end_and_replay_from_their_log()
    {
        var civilianEnds = 0;
        var lines = new HashSet<string>();
        for (ulong seed = 0; seed < 200; seed++)
        {
            var rules = (seed % 4) switch
            {
                0 => Rules,
                1 => "rules base start-choice=2015 expansion=pantheon",
                2 => "rules base promo=messe expansion=pantheon",
                _ => "rules base promo=messe,liberty expansion=pantheon",
            };
            var game = PlayToEnd(seed, rules);
            var s = game.State;
            Assert.NotNull(s.Result);
            if (s.Result.Victory == VictoryType.Civilian)
            {
                civilianEnds++;
                Assert.Equal(3, s.Age);
            }
            var text = game.WriteLog();
            foreach (var line in text.Split('\n')) lines.Add(string.Join(' ', line.Split(' ').Take(line.StartsWith("reveal P") ? 3 : 2)));
            var replayed = MoveLog.Parse(text);
            Assert.True(replayed.State.SameAs(s));
            Assert.Equal(s.ToBytes(), replayed.State.ToBytes());
            Assert.Equal(text, replayed.WriteLog());
            var copy = s.Clone();
            Assert.True(copy.SameAs(s));
            Assert.Equal(s.ToBytes(), copy.ToBytes());
        }
        Assert.True(civilianEnds > 100);
        foreach (var expected in new[] { "reveal myth-tokens", "reveal P1 divinities", "reveal P2 divinities", "P1 divinity", "P2 divinity",
                     "reveal pantheon", "reveal offerings", "reveal enki", "reveal temples" })
            Assert.Contains(expected, lines);
    }

    [Fact]
    public void Pantheon_games_are_deterministic_and_differ_from_base_games_only_after_the_base_bytes_at_the_start()
    {
        Assert.Equal(PlayToEnd(11).WriteLog(), PlayToEnd(11).WriteLog());
        Assert.Equal(PlayToEnd(11).State.ToBytes(), PlayToEnd(11).State.ToBytes());
        var baseBytes = GameState.New(Ruleset.Default).ToBytes();
        var pantheonBytes = GameState.New(Ruleset.Parse(Rules)).ToBytes();
        Assert.Equal(baseBytes, pantheonBytes[..baseBytes.Length]);
    }

    [Fact]
    public void The_state_printer_shows_the_pantheon()
    {
        var game = Start("P1 discard apothecary", "P2 discard baths", "reveal 3.0 stable", "reveal P2 divinities egyptian anubis isis", "P2 divinity isis 4");
        var text = game.State.ToString();
        Assert.Contains("Pantheon: 1:-- 2:-- 3:-- 4:isis(face down, P2)", text);
        Assert.Contains("egyptian [anubis ?]", text);
        Assert.Contains("P2 holds: egyptian", text);
        Assert.Contains("Tokens on the structure: 1.0 greek 1.2 roman 3.2 phoenician 3.4 greek", text);
    }
}
