using SevenWondersDuel.Advisor.Tts;
using SevenWondersDuel.Engine;
using SevenWondersDuel.Engine.Data;

namespace SevenWondersDuel.Advisor.Tests;

/// <summary>
/// Ticket 33: the expected table, settled-table detection, the chain search (one chain appended, several offered,
/// otherwise a Desync) and the soft checks. Tables are Game States laid out by <see cref="TablePainter"/> and read
/// through the real snapshot.lua, so these are the snapshots TTS would send.
/// </summary>
public sealed class TtsReconcileTests
{
    static readonly Ruleset[] AllRules =
    [
        Ruleset.Default,
        new(Promos: Promos.Messe | Promos.Liberty),
        new(StartChoice.Rule2015, Promos.Liberty),
    ];

    /// <summary>Plays a game forward in the "truth" session by <paramref name="moves"/> Moves and every Reveal after them.</summary>
    internal static void Advance(AdvisorSession truth, RandomChanceSource chance, Rng rng, int moves)
    {
        while (!truth.State.IsOver)
        {
            var s = truth.State;
            if (s.Step == StepKind.Reveal)
            {
                truth.Enter(MoveLog.Format(chance.Next(s)));
                continue;
            }
            if (moves-- == 0) return;
            var legal = s.LegalMoves();
            truth.Enter($"P{s.PlayerToDecide + 1} {legal[rng.Next(legal.Count)]}");
        }
    }

    internal sealed record Counts(int Appended, int Choices, int Waits, int ByHand, int HandMoves = 0);

    /// <summary>
    /// Reconciles the follower with the table until it matches, the way the Advisor server does: a single chain is
    /// entered; with several chains, or a wait, the owner's answer is taken from the truth log; the owner's own Great
    /// Library draw (never on the table) is entered by hand. With <paramref name="handMoves"/> (catch-up), when the table can't
    /// tell the owner's next Move apart (which card went under a Wonder, unseen) the owner enters it by hand.
    /// </summary>
    internal static Counts CatchUp(AdvisorSession follower, AdvisorSession truth, SeatSides sides, TableSnapshot table, Counts counts,
        ReconcileOptions? options = null, bool handMoves = false)
    {
        // The states the real game went through. A chain is what really happened when it leads to one of them: the
        // order of Moves the table can't tell apart (who discarded which card within one reading) may differ.
        var real = new List<string>();
        var replay = new Game(truth.Rules);
        real.Add(Convert.ToBase64String(replay.State.ToBytes()));
        foreach (var e in truth.Log)
        {
            replay.Apply(e);
            real.Add(Convert.ToBase64String(replay.State.ToBytes()));
        }
        // Where the follower is in the real game: the real log continues with truth.Log[Here()].
        int Here() => real.LastIndexOf(Convert.ToBase64String(follower.State.ToBytes()));
        bool IsNext(TableChain chain)
        {
            var copy = AdvisorSession.Load(follower.Write());
            copy.Enter(chain.Lines);
            return real.Contains(Convert.ToBase64String(copy.State.ToBytes()));
        }
        // Reveals the table can't show, entered by hand as the owner would: their own Great Library draw, and a card
        // flipped and tucked under a Wonder before the table was read (with the Move that tucked it).
        bool EnterByHand(Reconciliation r)
        {
            var here = Here();
            if (r.Waiting is null || here < 0 || here >= truth.Log.Count) return false;
            var next = truth.Log.Skip(here).ToList();
            if (next[0] is LibraryReveal { Redacted: false } || (next[0] is SlotReveal && r.Waiting.StartsWith("Name the card", StringComparison.Ordinal)))
            {
                follower.Enter(MoveLog.Format(next[0]));
                return true;
            }
            if (!r.Waiting.StartsWith("Name the card that was at", StringComparison.Ordinal)) return false;
            var k = next.FindIndex(e => e is SlotReveal);
            follower.Enter(next.Take(k + 1).Select(MoveLog.Format).ToList());
            return true;
        }
        // The owner enters the game's next Move by hand (with any Reveals before it).
        void EnterNextMove()
        {
            var here = Here();
            Assert.True(here >= 0 && here < truth.Log.Count, $"off the game:\n{follower.Write()}\n{truth.Write()}");
            var next = truth.Log.Skip(here).ToList();
            var k = next.FindIndex(e => e is MoveEntry);
            follower.Enter(next.Take(k < 0 ? next.Count : k + 1).Select(MoveLog.Format).ToList());
        }

        for (var guard = 0; guard < 16; guard++)
        {
            var r = Reconciler.Reconcile(follower.State, follower.Seat, sides, table, options);
            switch (r.Status)
            {
                case SyncStatus.Matches when Here() < truth.Log.Count && EnterByHand(r):
                    counts = counts with { ByHand = counts.ByHand + 1 };
                    break;
                case SyncStatus.Matches:
                    return counts;
                case SyncStatus.OneChain:
                    Assert.True(IsNext(r.Chains[0]), $"appended '{r.Chains[0].Text}', but the game went\n{truth.Write()}\nThe follower:\n{follower.Write()}");
                    follower.Enter(r.Chains[0].Lines);
                    counts = counts with { Appended = counts.Appended + 1 };
                    break;
                case SyncStatus.SeveralChains:
                    follower.Enter(Assert.Single(r.Chains, IsNext).Lines);
                    counts = counts with { Choices = counts.Choices + 1 };
                    break;
                case SyncStatus.Waiting:
                    var pick = r.Chains.Where(IsNext).ToList();
                    if (pick.Count == 1)
                    {
                        follower.Enter(pick[0].Lines);
                        counts = counts with { Waits = counts.Waits + 1 };
                    }
                    else
                    {
                        if (handMoves && !r.Chains.Any(IsNext) && r.Waiting is not null && !r.Waiting.StartsWith("Name the card", StringComparison.Ordinal))
                        {
                            EnterNextMove();
                            counts = counts with { HandMoves = counts.HandMoves + 1 };
                            break;
                        }
                        Assert.True(EnterByHand(r), $"Waiting: {r.Waiting} / Chains: {string.Join(" | ", r.Chains.Select(c => c.Text))} / "
                            + $"Not on table: {string.Join("; ", r.NotOnTable)} / Not in log: {string.Join("; ", r.NotInLog)} / "
                            + $"Follower: {follower.Write()} / Truth: {truth.Write()}");
                        counts = counts with { ByHand = counts.ByHand + 1 };
                    }
                    break;
                default:
                    if (handMoves)
                    {
                        EnterNextMove();
                        counts = counts with { HandMoves = counts.HandMoves + 1 };
                        break;
                    }
                    Assert.Fail($"Desync.\nNot on the table: {string.Join("; ", r.NotOnTable)}\nNot in the log: {string.Join("; ", r.NotInLog)}\n"
                        + $"Follower:\n{follower.Write()}\nTruth:\n{truth.Write()}");
                    break;
            }
        }
        Assert.Fail("reconciliation did not settle");
        return counts;
    }

    /// <summary>
    /// The heart of ticket 33: whole random games (all rule sets, both seats, both colours) played on a table that
    /// moves 1 to 3 Moves at a time. The Advisor follows from the table alone (plus the owner's own Library draw): a single
    /// explaining chain is appended every time it exists, and the followed game ends identical to the real one.
    /// </summary>
    [Fact]
    public void A_single_explaining_chain_is_appended()
    {
        var total = new Counts(0, 0, 0, 0);
        for (ulong seed = 0; seed < 20; seed++)
        {
            var rules = AllRules[seed % 3];
            var seat = (int)(seed % 2);
            var sides = SeatSides.ForOwner(seat, seed % 4 < 2 ? Side.White : Side.Red);
            var truth = AdvisorSession.New(rules, seat);
            var follower = AdvisorSession.New(rules, seat);
            var chance = new RandomChanceSource(seed + 100);
            var rng = new Rng(seed * 31 + 7);

            // The empty table at setup matches the empty log.
            Assert.Equal(SyncStatus.Matches, Reconciler.Reconcile(follower.State, seat, sides, TablePainter.Snapshot(truth.State, sides)).Status);
            while (!truth.State.IsOver)
            {
                Advance(truth, chance, rng, 1 + rng.Next(3));
                total = CatchUp(follower, truth, sides, TablePainter.Snapshot(truth.State, sides), total);
                // The follower is where the game is, or one choice behind that the table can't show yet: who starts the
                // new Age (until its first card is taken), or the Statue of Liberty's keep (until the give).
                if (!follower.State.ToBytes().AsSpan().SequenceEqual(truth.State.ToBytes()))
                {
                    var before = new Game(truth.Rules);
                    foreach (var e in truth.Log.Take(truth.Log.Count - 1)) before.Apply(e);
                    Assert.True(truth.Log[^1] is MoveEntry { Move.Kind: MoveKind.StartMe or MoveKind.StartOpponent or MoveKind.LibertyKeep }
                        && before.State.ToBytes().AsSpan().SequenceEqual(follower.State.ToBytes()),
                        $"behind the game:\n{follower.Write()}\n{truth.Write()}");
                }
            }
            Assert.True(follower.State.IsOver);
            Assert.Equal(truth.State.ToBytes(), follower.State.ToBytes());
        }
        Assert.True(total.Appended > 400, $"{total}");
        Assert.True(total.Choices >= 20, $"{total}"); // at least who plays first, in every game
    }

    /// <summary>Who plays first can't be seen until the first draft pick: both readings are offered, then one is certain.</summary>
    [Fact]
    public void Several_chains_are_offered_and_one_becomes_certain()
    {
        var sides = SeatSides.ForOwner(0, Side.White);
        var truth = AdvisorSession.New(Ruleset.Default, 0);
        truth.Enter("reveal first P2");
        truth.Enter("reveal tokens agriculture economy law strategy urbanism");
        truth.Enter("reveal draft pyramids great-lighthouse circus-maximus piraeus");
        var follower = AdvisorSession.New(Ruleset.Default, 0);

        var r = Reconciler.Reconcile(follower.State, 0, sides, TablePainter.Snapshot(truth.State, sides));
        Assert.Equal(SyncStatus.SeveralChains, r.Status);
        Assert.Equal(["reveal first P1", "reveal first P2"], r.Chains.Select(c => c.Lines[0]).Order());
        Assert.All(r.Chains, c => Assert.Equal(3, c.Lines.Count));

        truth.Enter("P2 draft piraeus");
        r = Reconciler.Reconcile(follower.State, 0, sides, TablePainter.Snapshot(truth.State, sides));
        Assert.Equal(SyncStatus.OneChain, r.Status);
        Assert.Equal("reveal first P2; reveal tokens agriculture economy law strategy urbanism; "
            + "reveal draft circus-maximus great-lighthouse piraeus pyramids; P2 draft piraeus", r.Chains[0].Text);
    }

    static (AdvisorSession Session, SeatSides Sides) AgeOne()
    {
        var session = AdvisorSession.New(Ruleset.Default, 0);
        var chance = new RandomChanceSource(5);
        var rng = new Rng(5);
        while (session.State.Age == 0 || session.State.Step == StepKind.Reveal) Advance(session, chance, rng, 1);
        return (session, SeatSides.ForOwner(0, Side.White));
    }

    /// <summary>
    /// A card dropped in the wrong city is a Desync listing both sides of the mismatch; nothing is appended. Putting the
    /// card where it belongs clears it, and the Move is then appended. A stray card is a Desync until it is tidied away.
    /// </summary>
    [Fact]
    public void Desync_clears_when_the_table_is_fixed()
    {
        var (follower, sides) = AgeOne();
        var s = follower.State;
        var player = s.PlayerToDecide;
        var take = s.LegalMoves().First(m => m.Kind == MoveKind.Build);
        var after = s.Clone();
        after.Apply(take);
        var slot = Enumerable.Range(0, GameState.SlotCount).Single(i => s.IsOccupied(i) && s.SlotCard(i) == take.Card);
        var name = TablePainter.CardName(take.Card);
        var card = ExpectedTable.CardName(take.Card);

        // The card lies in the opponent's city instead.
        var objects = TablePainter.Paint(after, sides);
        var i = objects.FindIndex(o => o.Name == name);
        var wrongCity = TtsData.Places.Cities[SeatSides.Other(sides.Of(player))];
        objects[i] = objects[i] with { X = (wrongCity.X1 + wrongCity.X2) / 2, Z = wrongCity.Z1 + 1 };
        var r = Reconciler.Reconcile(s, 0, sides, TablePainter.Snapshot(objects, after));
        Assert.Equal(SyncStatus.Desync, r.Status);
        Assert.Empty(r.Chains);
        Assert.Contains($"{sides.Name(1 - player)}'s city: {card}", r.NotInLog);
        Assert.Contains($"Age I pyramid {s.Layout.SlotName(slot)}: {card}", r.NotOnTable);
        Assert.DoesNotContain(r.NotInLog, f => f.Contains("pyramid"));

        // Fixed: the card is moved to the right city; the Move (and the flips it caused) are appended.
        r = Reconciler.Reconcile(s, 0, sides, TablePainter.Snapshot(after, sides));
        Assert.Equal(SyncStatus.OneChain, r.Status);
        Assert.Equal($"P{player + 1} build {GameData.Cards[take.Card].Id}", r.Chains[0].Lines[0]);

        // A stray card on the table: Desync until it is put away.
        objects = TablePainter.Paint(s, sides);
        objects.Add(new FakeObject("Card", "stray", "Baths", 0, 1.6, -20, Tags: ["blue"]));
        r = Reconciler.Reconcile(s, 0, sides, TablePainter.Snapshot(objects, s));
        Assert.Equal(SyncStatus.Desync, r.Status);
        Assert.Equal(["Loose on the table near (0, -20): Baths"], r.NotInLog);
        objects.RemoveAt(objects.Count - 1);
        Assert.Equal(SyncStatus.Matches, Reconciler.Reconcile(s, 0, sides, TablePainter.Snapshot(objects, s)).Status);

    }

    /// <summary>
    /// A card nobody flipped holds the log at its Reveal: the table matches, with a hint to flip or name it. If play goes
    /// on regardless, it is a wait for that Reveal, not a Desync; once flipped, the Reveal and the next Move are appended.
    /// </summary>
    [Fact]
    public void An_unflipped_card_is_a_wait_not_a_desync()
    {
        var (session, sides) = AgeOne();
        var chance = new RandomChanceSource(9);
        var rng = new Rng(9);
        // Play on until a Move uncovers a face-down card.
        GameState? uncovered = null;
        while (uncovered is null)
        {
            var s = session.State;
            foreach (var m in s.LegalMoves())
            {
                var next = s.Clone();
                next.Apply(m);
                if (next.Step == StepKind.Reveal && next.SlotsToReveal != 0 && !next.AwaitingLibraryDraw)
                {
                    session.Enter($"P{s.PlayerToDecide + 1} {m}");
                    uncovered = session.State;
                    break;
                }
            }
            if (uncovered is null) Advance(session, chance, rng, 1);
        }
        var r = Reconciler.Reconcile(uncovered, 0, sides, TablePainter.Snapshot(uncovered, sides));
        Assert.Equal(SyncStatus.Matches, r.Status);
        Assert.StartsWith("Flip the card", r.Waiting);

        // The real game goes on: the cards are flipped (but the table is painted with them face down) and the next player takes a card.
        var truth = AdvisorSession.Load(session.Write());
        Advance(truth, chance, rng, 1);
        var objects = TablePainter.Paint(truth.State, sides);
        var flipped = uncovered.SlotsToReveal;
        for (var slot = 0; slot < GameState.SlotCount; slot++)
        {
            if ((flipped & 1u << slot) == 0 || !truth.State.IsOccupied(slot)) continue;
            var at = TtsData.Places.SlotCentre(truth.State.Layout, slot);
            var i = objects.FindIndex(o => Math.Abs(o.X - at.X) < 0.01 && Math.Abs(o.Z - at.Z) < 0.01 && o.Type == "Card");
            objects[i] = objects[i] with { FaceDown = true };
        }
        r = Reconciler.Reconcile(uncovered, 0, sides, TablePainter.Snapshot(objects, truth.State));
        Assert.True(r.Status is SyncStatus.Waiting or SyncStatus.Desync, r.Status.ToString());
        if (truth.State.Age == uncovered.Age && Enumerable.Range(0, GameState.SlotCount).All(i => (flipped & 1u << i) == 0 || truth.State.IsOccupied(i)))
        {
            Assert.Equal(SyncStatus.Waiting, r.Status);
            Assert.StartsWith("Flip the card", r.Waiting);
        }

        // Flipped: the Reveal and the Move are appended.
        r = Reconciler.Reconcile(uncovered, 0, sides, TablePainter.Snapshot(truth.State, sides));
        Assert.Equal(SyncStatus.OneChain, r.Status);
        Assert.Equal(truth.Log.Skip(session.Log.Count).Select(MoveLog.Format), r.Chains[0].Entries.Select(MoveLog.Format));
    }

    /// <summary>Coins, the Conflict Pawn and looting tokens are soft checks: notes only, never a Desync.</summary>
    [Fact]
    public void Coin_mismatches_are_notes_only()
    {
        var (session, sides) = AgeOne();
        var s = session.State;
        var objects = TablePainter.Paint(s, sides);
        // Two coins too many on White's side, the pawn 3 spaces off, and one looting token taken too early.
        TablePainter.AddCoins(objects, 2, 30, -38);
        var pawn = objects.FindIndex(o => o.Name == "Conflict Pawn");
        objects[pawn] = objects[pawn] with { X = objects[pawn].X + 3 * TtsData.Places.TrackStep };
        var looting = objects.FindIndex(o => o.Name == "Military token");
        objects[looting] = objects[looting] with { Z = 28 };

        var r = Reconciler.Reconcile(s, 0, sides, TablePainter.Snapshot(objects, s));
        Assert.Equal(SyncStatus.Matches, r.Status);
        Assert.Empty(r.NotOnTable);
        Assert.Empty(r.NotInLog);
        Assert.Contains($"Coins: TTS shows {s.Coins(sides.PlayerAt(Side.White)) + 2} for {sides.Name(Side.White)}, expected {s.Coins(sides.PlayerAt(Side.White))}", r.Notes);
        Assert.Contains(r.Notes, n => n.StartsWith("Conflict Pawn: TTS shows it 3 towards White", StringComparison.Ordinal));
        Assert.Contains(r.Notes, n => n.StartsWith("Looting token: the ", StringComparison.Ordinal) && n.EndsWith("should be on the board", StringComparison.Ordinal));

        // A Move still goes through with the wrong coins, and the notes are about the state after it.
        var move = s.LegalMoves().First(m => m.Kind == MoveKind.Discard);
        var after = s.Clone();
        after.Apply(move);
        var later = TablePainter.Paint(after, sides);
        TablePainter.AddCoins(later, 1, -30, -38);
        r = Reconciler.Reconcile(s, 0, sides, TablePainter.Snapshot(later, after));
        Assert.Equal(SyncStatus.OneChain, r.Status);
        Assert.Equal([$"Coins: TTS shows {after.Coins(sides.PlayerAt(Side.Red)) + 1} for {sides.Name(Side.Red)}, expected {after.Coins(sides.PlayerAt(Side.Red))}"], r.Notes);

        // With everything right there are no notes; one pawn space off is within the reading's accuracy.
        Assert.Empty(Reconciler.Reconcile(s, 0, sides, TablePainter.Snapshot(s, sides)).Notes);
        objects = TablePainter.Paint(s, sides);
        pawn = objects.FindIndex(o => o.Name == "Conflict Pawn");
        objects[pawn] = objects[pawn] with { X = objects[pawn].X + TtsData.Places.TrackStep };
        Assert.Empty(Reconciler.Reconcile(s, 0, sides, TablePainter.Snapshot(objects, s)).Notes);
    }

    /// <summary>Settled: nothing held, nothing moving, and the same snapshot twice in a row.</summary>
    [Fact]
    public void Only_a_settled_table_is_reconciled()
    {
        var (session, sides) = AgeOne();
        var objects = TablePainter.Paint(session.State, sides);
        var still = TablePainter.Snapshot(objects, session.State);
        var held = objects.ToList();
        held[0] = held[0] with { HeldBy = "White" };
        var moving = objects.ToList();
        moving[0] = moving[0] with { Resting = false };
        var moved = objects.ToList();
        moved[0] = moved[0] with { X = moved[0].X + 0.3 };

        var settle = new SettledTable();
        Assert.Null(settle.Offer(still));
        Assert.Same(still, settle.Offer(still));
        Assert.Same(still, settle.Offer(still));
        var heldSnapshot = TablePainter.Snapshot(held, session.State);
        Assert.False(heldSnapshot.StillNow);
        Assert.Null(settle.Offer(heldSnapshot));
        Assert.Null(settle.Offer(heldSnapshot));
        // A piece TTS calls "not resting" but that stays put (live table, 2026-10-26: cards in the mod's auto-sorting
        // layout zones) settles like any other: two identical readings.
        var notResting = TablePainter.Snapshot(moving, session.State);
        Assert.Null(settle.Offer(notResting));
        Assert.Same(notResting, settle.Offer(notResting));
        Assert.Null(settle.Offer(still));
        Assert.Null(settle.Offer(TablePainter.Snapshot(moved, session.State)));
        Assert.Null(settle.Offer(still));
        Assert.NotNull(settle.Offer(still));
    }

    /// <summary>
    /// The expected table is the AI-seat seam (spec A10): for every state of random games, the table laid out from it
    /// reads back as exactly the expected facts, and soft checks are clean.
    /// </summary>
    [Fact]
    public void The_expected_table_of_every_state_reads_back_from_its_own_table()
    {
        for (ulong seed = 0; seed < 4; seed++)
        {
            var truth = AdvisorSession.New(AllRules[seed % 3], (int)(seed % 2));
            var sides = SeatSides.ForOwner(truth.Seat, seed < 2 ? Side.White : Side.Red);
            var chance = new RandomChanceSource(seed);
            var rng = new Rng(seed);
            var n = 0;
            while (!truth.State.IsOver)
            {
                Advance(truth, chance, rng, 1);
                if (n++ % 3 != 0) continue;
                var s = truth.State;
                var view = TableView.From(TablePainter.Snapshot(s, sides), s.Age);
                Assert.Equal(ExpectedTable.Facts(s, sides), ExpectedTable.Facts(view, s, sides));
                Assert.Empty(ExpectedTable.SoftChecks(s, view, sides));
            }
        }
    }

    /// <summary>The committed practice snapshot (a hand-set Age II table) against a new game is a Desync with a readable list, not a crash.</summary>
    [Fact]
    public void The_practice_table_against_a_new_game_is_a_readable_desync()
    {
        var snapshot = TableSnapshot.Parse(File.ReadAllText(TtsSnapshotTests.FixturePath));
        var r = Reconciler.Reconcile(AdvisorSession.New(Ruleset.Default, 1).State, 1, SeatSides.ForOwner(1, Side.White), snapshot);
        Assert.Equal(SyncStatus.Desync, r.Status);
        Assert.Contains("White (P2)'s city: Baths", r.NotInLog);
        Assert.Contains("Red (P1)'s Wonder: Piraeus (built)", r.NotInLog);
        Assert.Contains("Discard pile: Shelf Quarry", r.NotInLog);
        Assert.Empty(r.NotOnTable);
    }
}
