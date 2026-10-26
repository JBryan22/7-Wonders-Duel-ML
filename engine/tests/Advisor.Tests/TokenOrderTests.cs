using SevenWondersDuel.Advisor.Tts;
using SevenWondersDuel.Agents;
using SevenWondersDuel.Engine;

namespace SevenWondersDuel.Advisor.Tests;

/// <summary>
/// Owner ruling 2026-10-26 (spec-pantheon R11, Pantheon ticket 17): the player who takes a Mythology token draws and
/// places its Divinity before the cards their turn uncovered are flipped. The Advisor asks in that order (by hand and
/// against the AI), refuses a flip entered too early with a plain reason, and still loads a game saved in the old order
/// (flips first), the AI's secrets included.
/// </summary>
public sealed class TokenOrderTests : IDisposable
{
    readonly string _folder = Path.Combine(Path.GetTempPath(), "7wd-advisor-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    static readonly Ruleset Pantheon = new(Expansions: Expansions.Pantheon);

    /// <summary>
    /// A random Pantheon game entered by hand from <paramref name="seat"/>, up to the first token draw of
    /// <paramref name="placer"/> that holds flips (before the draw is applied): the true game, the owner's session, and the
    /// true game's chance source and Move generator to go on with.
    /// </summary>
    static (Game Truth, AdvisorSession Session, RandomChanceSource Chance, Rng Rng) ToHeldDraw(int seat, int placer)
    {
        for (ulong seed = 0; seed < 50; seed++)
        {
            var truth = new Game(Pantheon);
            var session = AdvisorSession.New(Pantheon, seat);
            var chance = new RandomChanceSource(seed);
            var rng = new Rng(seed + 99);
            while (!truth.State.IsOver && truth.State.Age <= 1)
            {
                var s = truth.State;
                if (s.Step == StepKind.Reveal && s.FlipsHeld && s.PlayerToDecide == placer) return (truth, session, chance, rng);
                Next(truth, session, seat, chance, rng);
            }
        }
        throw new InvalidOperationException("No random game reached a held placement");
    }

    /// <summary>One more entry of the true game, entered in the owner's session as they see it (unless it logged it itself).</summary>
    static LogEntry Next(Game truth, AdvisorSession session, int seat, RandomChanceSource chance, Rng rng)
    {
        var s = truth.State;
        LogEntry entry;
        if (s.Step == StepKind.Reveal) entry = chance.Next(s);
        else
        {
            var moves = s.LegalMoves();
            entry = new MoveEntry(s.PlayerToDecide, moves[rng.Next(moves.Count)]);
        }
        var index = truth.Log.Count;
        truth.Apply(entry);
        if (session.Log.Count <= index) session.Enter(MoveLog.Format(MoveLog.AsSeenBy(entry, seat)));
        return entry;
    }

    /// <summary>The flip the true game will make once its held decisions are made (from a copy).</summary>
    static string FlipToCome(Game truth)
    {
        var copy = MoveLog.Parse(truth.WriteLog());
        var c = new RandomChanceSource(99);
        while (copy.State.FlipsHeld)
        {
            var s = copy.State;
            if (s.Step == StepKind.Reveal) copy.Apply(c.Next(s));
            else copy.Apply(new MoveEntry(s.PlayerToDecide, s.LegalMoves()[0]));
        }
        return MoveLog.Format(c.Next(copy.State));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    public void A_token_s_divinity_is_drawn_and_placed_before_the_flip_entered_by_hand(int seat, int placer)
    {
        var (truth, session, chance, rng) = ToHeldDraw(seat, placer);
        var flip = FlipToCome(truth);
        var p = $"P{placer + 1}";
        if (placer == seat)
        {
            // The owner names their own draw first (from their hand): the picker asks for it, not for the flip.
            Assert.Equal(StepKind.Reveal, session.State.Step);
            Assert.StartsWith($"reveal {p} divinities", BoardView.Build(session, "").Reveal!.LinePrefix);
            Assert.Throws<EntryException>(() => session.Enter(flip));
            Next(truth, session, seat, chance, rng);
        }
        else
        {
            // The opponent's draw is logged redacted by itself.
            Assert.IsType<DivinityDrawReveal>(session.Log[^1]);
            Next(truth, session, seat, chance, rng);
        }
        Assert.Equal(StepKind.Decision, session.State.Step);
        Assert.True(session.State.FlipsHeld);
        var view = BoardView.Build(session, "");
        Assert.Equal("divinity", view.Decision!.Kind);
        Assert.Contains("before flipping the uncovered cards", view.Decision.Title);
        var early = Assert.Throws<EntryException>(() => session.Enter(flip));
        Assert.Contains(placer == seat ? "Place your Divinity first" : $"{p} places their Divinity first", early.Message);

        // The placement (and any second token's), then the flip.
        while (truth.State.FlipsHeld) Next(truth, session, seat, chance, rng);
        Assert.Equal(StepKind.Reveal, session.State.Step);
        Assert.Equal("slots", BoardView.Build(session, "").Reveal!.Kind);
        Assert.IsType<SlotReveal>(Next(truth, session, seat, chance, rng));
        Assert.Equal(truth.State.Observe(seat).State.ToBytes(), session.State.Observe(seat).State.ToBytes());
    }


    /// <summary>
    /// Following the TTS table: the token's Divinity placed before the flip reads as one chain (the placement, then the
    /// flip). A table where the uncovered card was flipped first, with no Divinity placed yet, makes the Advisor wait for
    /// the placement instead of calling a Desync.
    /// </summary>
    [Theory]
    [InlineData(0, 1)]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    public void Following_the_table_reads_the_placement_before_the_flip_and_waits_if_the_card_was_flipped_first(int seat, int placer)
    {
        var (truth, session, chance, rng) = ToHeldDraw(seat, placer);
        Next(truth, session, seat, chance, rng); // the draw
        var held = truth.State.Clone();
        var sides = SeatSides.ForOwner(seat, Side.White);
        var shadow = AdvisorSession.Load(session.Write());
        while (truth.State.FlipsHeld) Next(truth, shadow, seat, chance, rng);
        var flip = Assert.IsType<SlotReveal>(Next(truth, shadow, seat, chance, rng));

        // The table: the held position with the uncovered cards already face up.
        var objects = TablePainter.Paint(held, sides);
        foreach (var (slot, card) in flip.Cards)
        {
            var at = TtsData.Places.SlotCentre(held.Layout, slot);
            var i = objects.FindIndex(o => o.Type == "Card" && o.FaceDown && Math.Abs(o.X - at.X) < 0.01 && Math.Abs(o.Z - at.Z) < 0.01);
            objects[i] = objects[i] with { Name = TablePainter.CardName(card), FaceDown = false };
        }
        var early = Reconciler.Reconcile(session.State, seat, sides, TablePainter.Snapshot(objects, held));
        Assert.Equal(SyncStatus.Waiting, early.Status);
        Assert.Contains("before the uncovered cards are flipped", early.Waiting);

        // Once the Divinity is placed on the table: the placement, then the flip. (The owner's own face-down card can
        // be read either way once both drawn cards have left their hand: one click picks it.)
        var r = Reconciler.Reconcile(session.State, seat, sides, TablePainter.Snapshot(truth.State, sides));
        Assert.Equal(placer == seat ? SyncStatus.SeveralChains : SyncStatus.OneChain, r.Status);
        Assert.All(r.Chains, c => Assert.True(c.Text.IndexOf(" divinity ", StringComparison.Ordinal) is var at && at >= 0
            && at < c.Text.IndexOf(MoveLog.Format(flip), StringComparison.Ordinal), c.Text));
        var right = r.Chains.Single(c => c.Lines.SequenceEqual(truth.Log.Skip(session.Log.Count)
            .Where(e => e is not DivinityDrawReveal { Redacted: true }).Select(e => MoveLog.Format(MoveLog.AsSeenBy(e, seat)))));
        session.Enter(right.Lines);
        Assert.Equal(truth.State.Observe(seat).State.ToBytes(), session.State.Observe(seat).State.ToBytes());
    }
    /// <summary>
    /// A saved game turned back into the order logs had before the ruling: each "reveal 3.2 x" that follows a token's
    /// draw and placement moved before them, with the AI's secrets moved along with their lines.
    /// </summary>
    static string OldOrder(string saved)
    {
        var lines = saved.Split('\n').ToList();
        var first = lines.FindIndex(2, l => !l.StartsWith('#'));
        var header = lines.Take(first).Where(l => !l.StartsWith(AdvisorSession.SecretPrefix)).ToList();
        var secrets = lines.Where(l => l.StartsWith(AdvisorSession.SecretPrefix))
            .Select(l => l[AdvisorSession.SecretPrefix.Length..].Split(' '))
            .ToDictionary(p => int.Parse(p[0]), p => p[1]);
        var entries = lines.Skip(first).Where(l => l.Length > 0).ToList();
        var moved = 0;
        for (var k = 0; k + 2 < entries.Count; k++)
        {
            if (!(entries[k].StartsWith("reveal P") && entries[k].Contains(" divinities ") && entries[k + 1].Contains(" divinity ")
                && System.Text.RegularExpressions.Regex.IsMatch(entries[k + 2], @"^reveal \d\.\d "))) continue;
            (entries[k], entries[k + 1], entries[k + 2]) = (entries[k + 2], entries[k], entries[k + 1]);
            var at = secrets.Where(x => x.Key == k || x.Key == k + 1).OrderByDescending(x => x.Key).ToList();
            foreach (var (index, _) in at) secrets.Remove(index);
            foreach (var (index, value) in at) secrets[index + 1] = value;
            moved++;
            k += 2;
        }
        Assert.True(moved > 0, "no token draw was followed by a flip");
        return string.Join('\n', [.. header, .. secrets.OrderBy(x => x.Key).Select(x => $"{AdvisorSession.SecretPrefix}{x.Key} {x.Value}"),
            .. entries]) + "\n";
    }

    [Fact]
    public void A_game_against_the_AI_saved_in_the_old_order_resumes_with_its_secrets()
    {
        var session = AdvisorSession.New(Pantheon, 0, new AiOpponent("random", "AI", 10, 11));
        var rng = new Rng(11);
        var aiPlacedBeforeFlips = 0;
        while (session.State.Age <= 1 && !session.State.IsOver)
        {
            if (session.AiToMove)
            {
                // The AI places its Divinity while the uncovered cards are still face down, in the true game too.
                if (session.State.Pending(0).Kind == DecisionKind.PlaceDivinity && session.State.FlipsHeld)
                {
                    Assert.True(session.TrueGame().State.FlipsHeld);
                    Assert.True(session.AiObservation().State.FlipsHeld);
                    aiPlacedBeforeFlips++;
                }
                session.EnterAiMove(AiPlayer.Choose(session.AiObservation(), session.Ai!, session.AiSeed));
            }
            else
            {
                var moves = session.State.LegalMoves();
                session.EnterOwn([MoveLog.Format(new MoveEntry(0, moves[rng.Next(moves.Count)]))]);
            }
        }
        Assert.True(aiPlacedBeforeFlips > 0);
        var saved = session.Write();
        Assert.Contains(AdvisorSession.SecretPrefix, saved);

        var old = OldOrder(saved);
        Assert.NotEqual(saved, old);
        Directory.CreateDirectory(_folder);
        var store = new AdvisorStore(_folder);
        File.WriteAllText(store.CurrentPath, old);
        var resumed = store.LoadCurrent()!;
        Assert.Equal(saved, resumed.Write());
        Assert.Equal(session.TrueGame().State.ToBytes(), resumed.TrueGame().State.ToBytes());
        Assert.Equal(session.State.ToBytes(), resumed.State.ToBytes());
        // Loading does not rewrite the file: the new order is saved with the next entry.
        Assert.Equal(old, File.ReadAllText(store.CurrentPath));
    }
}
