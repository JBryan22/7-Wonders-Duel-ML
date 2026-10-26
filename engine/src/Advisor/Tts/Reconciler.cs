using SevenWondersDuel.Engine;
using SevenWondersDuel.Engine.Data;

namespace SevenWondersDuel.Advisor.Tts;

/// <summary>What a settled Table Snapshot says about the Move Log (spec A9, ADR 0004).</summary>
public enum SyncStatus
{
    /// <summary>The table shows exactly what the log says: nothing to do.</summary>
    Matches,
    /// <summary>Exactly one short chain of legal Moves and Reveals explains the table: it is appended.</summary>
    OneChain,
    /// <summary>Several chains explain it (who plays first, who starts the Age): the owner picks one with a click.</summary>
    SeveralChains,
    /// <summary>The log waits for a Reveal the table can't show (the owner's Great Library draw, a card nobody flipped).</summary>
    Waiting,
    /// <summary>Nothing explains the table: flagged with the list of mismatches, nothing appended.</summary>
    Desync,
}

/// <summary>A chain of log entries that turns the Game State into what the table shows.</summary>
public sealed record TableChain(IReadOnlyList<LogEntry> Entries)
{
    /// <summary>
    /// The lines to enter, in order. The opponent's redacted private Reveals (their Great Library draw; with Pantheon,
    /// their Divinity draw and Divine Theater) are left out: the Advisor session logs them by itself when they are due.
    /// </summary>
    public IReadOnlyList<string> Lines { get; } = Entries
        .Where(e => e is not (LibraryReveal { Redacted: true } or DivinityDrawReveal { Redacted: true } or TheaterReveal { Redacted: true }))
        .Select(MoveLog.Format)
        .ToList();

    public string Text => string.Join("; ", Lines);
}

/// <summary>
/// The outcome of one reconciliation. <see cref="NotOnTable"/> and <see cref="NotInLog"/> are the mismatches between the
/// log's expected table and the snapshot (for a Desync, or what is still to come while <see cref="SyncStatus.Waiting"/>);
/// <see cref="Notes"/> are the soft checks for the state the log reaches (after the chain, if one is appended).
/// </summary>
public sealed record Reconciliation(
    SyncStatus Status,
    IReadOnlyList<TableChain> Chains,
    IReadOnlyList<string> NotOnTable,
    IReadOnlyList<string> NotInLog,
    string? Waiting,
    IReadOnlyList<string> Notes);

/// <summary>Limits of the chain search.</summary>
/// <param name="MaxMoves">Moves in a chain (spec: "up to about 4 legal Moves and Reveals"; the Reveals between them are free, since the table fixes them).</param>
/// <param name="MaxEntries">Moves and Reveals together.</param>
/// <param name="MaxNodes">States visited before the search gives up (a guard; real tables need a few dozen).</param>
/// <param name="MaxReveals">Readings of one Reveal tried (cards flipped and taken between two readings can be named several ways).</param>
public sealed record ReconcileOptions(int MaxMoves = 4, int MaxEntries = 10, int MaxNodes = 20_000, int MaxReveals = 24)
{
    /// <summary>
    /// Catch-up (ticket 34): the first reading after the Advisor restarts, starts following, or TTS loads a game may be
    /// many Moves ahead of the saved log (the game went on while the Advisor was closed), so the search goes deeper.
    /// </summary>
    public static readonly ReconcileOptions CatchUp = new(MaxMoves: 8, MaxEntries: 24, MaxNodes: 60_000, MaxReveals: 120);
}

/// <summary>
/// Reconciliation (spec A9, ADR 0004): compares a settled Table Snapshot with the expected table of the current Game
/// State and searches for chains of legal Moves and Reveals that explain the difference.
/// <para>
/// The search only follows what the table shows. A Move is tried only when the table has evidence of it (a built card
/// lies in that player's city, a discarded one on the discard pile, the Wonder has an Age card tucked under it, the
/// drafted Wonder lies in that player's column, the token in their area, ...). A Reveal comes from the table: the face-up
/// card now on a slot, the 4 offered Wonders, the 5 tokens on the board, the Guild backs, the Statue of Liberty's cards.
/// A card flipped and taken between two readings is named from where it went (a city or the discard pile). Who plays
/// first and who starts an Age can't be seen until a card is played, so both are tried. A chain ends as soon as its
/// expected table matches; the opponent's Great Library draw is redacted along the way exactly as the Advisor session
/// logs it. Chains that reach the same Game State count as one.
/// </para>
/// <para>
/// A chain that stops at a Reveal the table can never show (the owner's Great Library draw, a card nobody flipped, a
/// flipped card tucked under a Wonder before it was read, the Statue of Liberty's cards left face down) makes the
/// result <see cref="SyncStatus.Waiting"/> rather than a Desync, with what to enter by hand.
/// </para>
/// </summary>
public static class Reconciler
{
    public static Reconciliation Reconcile(GameState current, int ownerSeat, SeatSides sides, TableSnapshot snapshot,
        ReconcileOptions? options = null)
    {
        var search = new Search(ownerSeat, sides, snapshot, options ?? new ReconcileOptions(), current.Pantheon is not null);
        var start = current.Clone();
        AutoReveals(start, ownerSeat);
        var (missing, extra) = search.Diff(start);
        if (missing.Count == 0 && extra.Count == 0 && !search.ShowsPendingAnswer(start))
            return new Reconciliation(SyncStatus.Matches, [], [], [], search.WhyBlocked(start), search.Notes(start));

        search.Run(start, [], 0);
        // Chains reaching the same Game State differ only in history nobody can see any more (two flipped cards taken
        // before the table was read, in either slot): one of them stands for all.
        var found = search.Explained.Count > 0 ? search.Explained : search.Deferred;
        var explained = Distinct(found.GroupBy(e => e.End).Select(g => g.First().Chain).ToList());
        if (explained.Count == 0 && missing.Count == 0 && extra.Count == 0)
            return new Reconciliation(SyncStatus.Matches, [], [], [], search.WhyBlocked(start), search.Notes(start));
        if (explained.Count == 1)
        {
            var end = Apply(start, explained[0].Entries, ownerSeat);
            return new Reconciliation(SyncStatus.OneChain, explained, [], [], null, search.Notes(end));
        }
        if (explained.Count > 1)
        {
            // Coins are never input, but they make a good order: the readings the coins agree with come first.
            var ranked = explained.OrderBy(c => search.Notes(Apply(start, c.Entries, ownerSeat)).Count).ToList();
            return new Reconciliation(SyncStatus.SeveralChains, ranked, [], [], null, []);
        }
        if (search.Blocked.Count > 0)
        {
            // The prefixes that leave the fewest differences with the table come first (a long catch-up may find many).
            var ranked = search.Blocked.OrderBy(b => b.Mismatches).ThenBy(b => b.Chain.Count).ToList();
            var reason = ranked[0].Reason;
            var prefixes = Distinct(ranked.Where(b => b.Chain.Count > 0).Select(b => new TableChain(b.Chain)).ToList());
            return new Reconciliation(SyncStatus.Waiting, prefixes.Take(5).ToList(), missing, extra, reason, []);
        }
        return new Reconciliation(SyncStatus.Desync, [], missing, extra, null, []);
    }

    /// <summary>Applies a chain to a copy of the state (for the soft checks of the state it reaches).</summary>
    static GameState Apply(GameState state, IReadOnlyList<LogEntry> entries, int ownerSeat)
    {
        var s = state.Clone();
        foreach (var e in entries) Step(s, e, ownerSeat);
        return s;
    }

    static void Step(GameState s, LogEntry e, int ownerSeat)
    {
        switch (e)
        {
            case MoveEntry m: s.Apply(m.Move); break;
            case Reveal r: s.Apply(r); break;
        }
        AutoReveals(s, ownerSeat);
    }

    /// <summary>
    /// The Reveals nobody at the table can name, logged redacted as AdvisorSession does: the opponent's Library draw, and
    /// with Pantheon their Divinity draw and Divine Theater.
    /// </summary>
    static void AutoReveals(GameState s, int ownerSeat)
    {
        while (s.Step == StepKind.Reveal && s.SlotsToReveal == 0 && AdvisorSession.HiddenRevealDue(s, ownerSeat, againstAi: false) is Reveal hidden)
            s.Apply(hidden);
    }

    static List<TableChain> Distinct(List<TableChain> chains) =>
        chains.GroupBy(c => c.Text, StringComparer.Ordinal).Select(g => g.First()).ToList();

    sealed class Search
    {
        readonly int _owner;
        readonly SeatSides _sides;
        readonly TableSnapshot _snapshot;
        readonly ReconcileOptions _options;
        readonly bool _pantheon;
        readonly Dictionary<int, TableView> _views = [];
        int _nodes;
        // States already searched (with the Moves and entries used to get there): orders of Moves that reach the same
        // state lead to the same chains, so each is searched once. Matters for the deep catch-up search.
        readonly HashSet<string> _visited = new(StringComparer.Ordinal);

        public Search(int owner, SeatSides sides, TableSnapshot snapshot, ReconcileOptions options, bool pantheon)
        {
            _owner = owner;
            _sides = sides;
            _snapshot = snapshot;
            _options = options;
            _pantheon = pantheon;
        }

        /// <summary>Explaining chains with the state each reaches.</summary>
        public List<(TableChain Chain, string End)> Explained { get; } = [];
        /// <summary>Chains that explain the table but stop at a decision whose answer the table already shows (Neptune).</summary>
        public List<(TableChain Chain, string End)> Deferred { get; } = [];

        /// <summary>
        /// Pantheon: the table already shows what the state waits for, though no fact differs: the Gate's deck tops (in
        /// the box area), the owner's own draw or Divine Theater deck (in their hand), or one of Neptune's decisions (a
        /// looting token gone: those are soft checks). The search goes on from there.
        /// </summary>
        public bool ShowsPendingAnswer(GameState s)
        {
            if (s.Pantheon is not { } p) return false;
            if (s.Step == StepKind.Reveal)
            {
                if (s.SlotsToReveal != 0 || !(p.AwaitingGateReveal || p.AwaitingTheaterReveal
                    || s.PendingCount > 0 && s.Pending(0).Kind == DecisionKind.PlaceDivinity && !p.HasDraw)) return false;
                var reveals = Reveals(s, out var blocked);
                return blocked is null && reveals.Count > 0;
            }
            if (s.Step != StepKind.Decision || s.Pending(0).Kind is not (DecisionKind.NeptuneDiscard or DecisionKind.NeptuneApply)) return false;
            var player = s.PlayerToDecide;
            return s.LegalMoves().Any(m => Plausible(s, player, m));
        }
        public List<(List<LogEntry> Chain, string Reason, int Mismatches)> Blocked { get; } = [];

        /// <summary>The snapshot placed with an Age's pyramid grid (0: no grid, during the draft).</summary>
        TableView View(int age)
        {
            if (!_views.TryGetValue(age, out var view)) _views[age] = view = TableView.From(_snapshot, age, _pantheon);
            return view;
        }

        public (List<string> Missing, List<string> Extra) Diff(GameState s)
        {
            var expected = ExpectedTable.Facts(s, _sides);
            var actual = ExpectedTable.Facts(View(s.Age), s, _sides);
            var missing = new List<string>();
            var extra = new List<string>();
            foreach (var d in TableView.Compare(expected, actual))
                if (d.StartsWith("missing: ", StringComparison.Ordinal)) missing.Add(d["missing: ".Length..]);
                else extra.Add(d["extra:   ".Length..]);
            return (missing, extra);
        }

        public IReadOnlyList<string> Notes(GameState s) => ExpectedTable.SoftChecks(s, View(s.Age), _sides);

        public void Run(GameState s, List<LogEntry> chain, int moves)
        {
            if (++_nodes > _options.MaxNodes) return;
            if (chain.Count > 0 && !_visited.Add($"{moves}:{chain.Count}:{Convert.ToBase64String(s.ToBytes())}")) return;
            if (chain.Count > 0)
            {
                var (missing, extra) = Diff(s);
                if (missing.Count == 0 && extra.Count == 0)
                {
                    if (!ShowsPendingAnswer(s))
                    {
                        Explained.Add((new TableChain([.. chain]), Convert.ToBase64String(s.ToBytes())));
                        return;
                    }
                    // Pantheon's Neptune: the table already shows the answer (looting tokens are never facts): go on, and
                    // fall back on this chain if nothing longer explains the table.
                    Deferred.Add((new TableChain([.. chain]), Convert.ToBase64String(s.ToBytes())));
                }
            }
            if (s.Step == StepKind.GameOver || chain.Count >= _options.MaxEntries) return;

            if (s.Step == StepKind.Reveal)
            {
                var reveals = Reveals(s, out var blocked);
                if (blocked is not null)
                {
                    var (missing, extra) = Diff(s);
                    Blocked.Add(([.. chain], blocked, missing.Count + extra.Count));
                    return;
                }
                foreach (var r in reveals) Try(s, chain, r, moves);
                return;
            }
            if (moves >= _options.MaxMoves) return;
            var player = s.PlayerToDecide;
            var tried = false;
            foreach (var move in s.LegalMoves())
                if (Plausible(s, player, move))
                {
                    tried = true;
                    Try(s, chain, new MoveEntry(player, move), moves + 1);
                }
            if (!tried && FlippedBeforePlacement(s) is { } why)
            {
                var (missing, extra) = Diff(s);
                Blocked.Add(([.. chain], why, missing.Count + extra.Count));
            }
        }

        /// <summary>
        /// Pantheon, owner ruling 2026-10-26: a Mythology token's Divinity is placed before the cards the turn uncovered
        /// are flipped. When the table already shows such a card face up but no Divinity placed yet, the Advisor waits
        /// for the placement (it then reads both) rather than calling it a Desync. Null otherwise.
        /// </summary>
        string? FlippedBeforePlacement(GameState s)
        {
            if (!s.FlipsHeld || s.Pending(0).Kind != DecisionKind.PlaceDivinity) return null;
            var view = View(s.Age);
            var flipped = Enumerable.Range(0, GameState.SlotCount).Any(slot => s.IsOccupied(slot) && s.IsAccessible(slot)
                && s.SlotCard(slot) < 0 && view.OnSlot(slot).Any(x => CardIndex(x.Slug) >= 0));
            if (!flipped) return null;
            var placer = s.PlayerToDecide;
            return placer == _owner
                ? "Place your Divinity on the Pantheon board: the Mythology token's Divinity is placed before the uncovered cards are flipped. The Advisor reads both once it is placed."
                : $"Waiting for P{placer + 1} to place their Divinity on the Pantheon board (it comes before the uncovered cards are flipped). The Advisor reads both once it is placed.";
        }

        void Try(GameState s, List<LogEntry> chain, LogEntry entry, int moves)
        {
            var next = s.Clone();
            try
            {
                Step(next, entry, _owner);
            }
            catch (Exception e) when (e is InvalidOperationException or ArgumentException)
            {
                return;
            }
            chain.Add(entry);
            Run(next, chain, moves);
            chain.RemoveAt(chain.Count - 1);
        }

        /// <summary>Why the state waits for a Reveal the table can't give, or null.</summary>
        public string? WhyBlocked(GameState s)
        {
            if (s.Step != StepKind.Reveal) return null;
            Reveals(s, out var blocked);
            return blocked;
        }

        /// <summary>
        /// The Reveals the table shows for the Reveal the state waits for. None, with <paramref name="blocked"/> set, when it
        /// is one the table can never show (the owner names it by hand); none without it when the table should show it but
        /// doesn't (the tokens, the draft offer, Age III's Guild backs): a dead end.
        /// </summary>
        List<Reveal> Reveals(GameState s, out string? blocked)
        {
            blocked = null;
            var view = View(s.Age);
            if (s.FirstPlayer < 0) return [new FirstPlayerReveal(0), new FirstPlayerReveal(1)];
            if (s.AwaitingTokens)
            {
                var tokens = view.At(Place.ProgressBoard).Select(p => TokenIndex(p.Slug)).Where(t => t >= 0).Distinct().Order().ToArray();
                if (tokens.Length == ProgressTokens.OnBoard) return [new TokenReveal(tokens)];
                return [];
            }
            if (s.AwaitingDraftOffer)
            {
                var wonders = view.Pieces
                    .Where(p => p.Place is Place.WonderOffer or Place.Wonder && p.Piece.Back == "wonder")
                    .Select(p => WonderIndex(p.Slug))
                    .Where(w => w >= 0 && (s.UnseenWonders & 1 << w) != 0)
                    .Distinct().Order().ToArray();
                if (wonders.Length == GameState.DraftOfferSize) return [new DraftReveal(wonders)];
                return [];
            }
            if (s.AwaitingGuildPattern)
            {
                // With Pantheon the special backs are Grand Temples' (R6).
                var back = s.TempleBacks ? "temple" : "guild";
                var guilds = view.At(Place.Pyramid).Where(p => p.Piece.Back == back).Select(p => p.Slot).Distinct().Order().ToList();
                if (guilds.Count == GameState.GuildsPerGame) return [new GuildPatternReveal([.. guilds], s.TempleBacks)];
                // A Guild already taken (before the table was read) left an empty slot: try each.
                var missing = GameState.GuildsPerGame - guilds.Count;
                var takenGuilds = view.Pieces.Count(p => p.Place is Place.City or Place.Discard
                    && CardIndex(p.Slug) is var c and >= 0 && (s.TempleBacks ? PantheonData.TempleSet.Contains(c) : GameData.Card(c).IsGuild)
                    && !s.Seen.Contains(c));
                if (guilds.Count > GameState.GuildsPerGame || takenGuilds < missing) return [];
                var empty = Enumerable.Range(0, GameState.SlotCount).Where(i => !view.OnSlot(i).Any()).ToList();
                var patterns = new List<Reveal>();
                void Pick(int from, List<int> chosen)
                {
                    if (patterns.Count >= _options.MaxReveals) return;
                    if (chosen.Count == GameState.GuildsPerGame)
                    {
                        patterns.Add(new GuildPatternReveal([.. chosen.Order()], s.TempleBacks));
                        return;
                    }
                    for (var i = from; i < empty.Count; i++)
                    {
                        chosen.Add(empty[i]);
                        Pick(i + 1, chosen);
                        chosen.RemoveAt(chosen.Count - 1);
                    }
                }
                Pick(0, guilds);
                return patterns;
            }
            if (s.SlotsToReveal != 0)
            {
                // A card face up on its slot names itself. Any other slot may have been flipped and taken between two
                // readings (its spot may even hold the next Age's card by now): its card is then one the log hasn't seen
                // that lies in a city or on the discard pile. Every possibility is tried, the Moves that follow decide;
                // a slot may also stay unrevealed (still face down, or its card went under a Wonder unseen).
                var fixedCards = new List<(int Slot, int Card)>();
                var open = new List<int>();
                var taken = new List<string>();
                var unflipped = new List<string>();
                var named = CardSet.Empty;
                for (var slot = 0; slot < GameState.SlotCount; slot++)
                {
                    if ((s.SlotsToReveal & 1u << slot) == 0) continue;
                    var onSlot = view.OnSlot(slot).ToList();
                    var card = onSlot.Where(p => !p.Piece.FaceDown).Select(p => CardIndex(p.Slug)).FirstOrDefault(-1);
                    if (card >= 0 && (s.UnseenPoolFor(slot) & ~named).Contains(card))
                    {
                        named = named.With(card);
                        fixedCards.Add((slot, card));
                        continue;
                    }
                    open.Add(slot);
                    (onSlot.Count == 0 ? taken : unflipped).Add(s.Layout.SlotName(slot));
                }
                var visible = view.Pieces
                    .Where(p => p.Place is Place.City or Place.Discard && p.Piece.Back != "wonder")
                    .Select(p => CardIndex(p.Slug))
                    .Where(c => c >= 0 && !s.Seen.Contains(c) && !named.Contains(c))
                    .Distinct().ToList();
                var reveals = new List<Reveal>();
                void Assign(int i, CardSet used, List<(int Slot, int Card)> chosen)
                {
                    if (reveals.Count >= _options.MaxReveals) return;
                    if (i == open.Count)
                    {
                        if (chosen.Count > 0) reveals.Add(new SlotReveal(s.Age, s.AgeUntouched, [.. chosen.OrderBy(x => x.Slot)]));
                        return;
                    }
                    foreach (var c in visible)
                    {
                        if (used.Contains(c) || !s.UnseenPoolFor(open[i]).Contains(c)) continue;
                        chosen.Add((open[i], c));
                        Assign(i + 1, used.With(c), chosen);
                        chosen.RemoveAt(chosen.Count - 1);
                    }
                    Assign(i + 1, used, chosen);
                }
                Assign(0, named, fixedCards);
                if (reveals.Count > 0) return reveals;
                var where = string.Join(", ", unflipped.Concat(taken));
                blocked = taken.Count > 0
                    ? $"Name the card that was at {where} in the Advisor Window (it left the table before it could be read)."
                    : unflipped.Count == 1
                        ? $"Flip the card at {where} on the table, or name it in the Advisor Window."
                        : $"Flip the cards at {where} on the table, or name them in the Advisor Window.";
                return [];
            }
            if (s.AwaitingLibraryDraw)
            {
                blocked = "Name the 3 Progress tokens of your Great Library draw in the Advisor Window (the table never shows them).";
                return [];
            }
            if (s.AwaitingReturnedCards)
            {
                var pool = s.UnseenPool(s.Age);
                var cards = view.Pieces
                    .Where(p => p.Place is Place.City or Place.Discard or Place.Loose && p.Piece.Kind is PieceKind.Card or PieceKind.Deck && p.Piece.Back != "wonder")
                    .Select(p => CardIndex(p.Slug))
                    .Where(c => c >= 0 && pool.Contains(c))
                    .Distinct().Order().ToArray();
                if (cards.Length == GameState.ReturnedCardCount) return [new ReturnedReveal(s.Age, cards)];
                blocked = "Turn the Statue of Liberty's 3 cards face up on the table, or name them in the Advisor Window.";
                return [];
            }
            if (s.Pantheon is { } pantheon) return PantheonReveals(s, pantheon, view, out blocked);
            return [];
        }

        // ---------------------------------------------------------------- Pantheon (spec-pantheon P-A9)

        /// <summary>
        /// The Divinities in the owner's own hand: snapshot.lua names only the host's hand ("own"), and only when the host
        /// sits at the owner's colour is that the owner's.
        /// </summary>
        List<int> OwnHand(TableView view)
        {
            if (_snapshot.HostColor != _sides.Of(_owner).ToString()) return [];
            return view.At(Place.Hand).Where(p => p.Piece.Own).Select(p => DivinityIndex(p.Slug)).Where(d => d >= 0).Distinct().ToList();
        }

        static List<int> OfMythology(IEnumerable<int> divinities, Mythology m) =>
            divinities.Where(d => PantheonData.Divinities[d].Mythology == m).Distinct().ToList();

        /// <summary>
        /// A deck's top <paramref name="count"/> cards from the cards a hand (or the table) shows, in deck order: cards the
        /// state knows stay at their positions, the others fill the unknown positions (in index order: which unknown card
        /// was above which is never seen, and makes no difference once the decision is made). Null if they don't fit.
        /// </summary>
        static int[]? DeckOrder(PantheonState p, Mythology m, IReadOnlyCollection<int> cards, int count)
        {
            if (cards.Count != count) return null;
            var order = new int[count];
            var rest = new SortedSet<int>(cards);
            for (var i = 0; i < count; i++)
            {
                var known = p.DeckCard(m, i);
                order[i] = known;
                if (known >= 0 && !rest.Remove(known)) return null;
            }
            for (var i = 0; i < count; i++)
                if (order[i] < 0)
                {
                    order[i] = rest.Min;
                    rest.Remove(order[i]);
                }
            return order;
        }

        /// <summary>
        /// Pantheon's Reveals, in the Engine's order (Zeus's discard, the Gate, the Divine Theater, the setup Reveals, a
        /// Divinity draw). The opponent's private ones never come here: they are logged redacted (<see cref="AutoReveals"/>).
        /// </summary>
        List<Reveal> PantheonReveals(GameState s, PantheonState p, TableView view, out string? blocked)
        {
            blocked = null;
            if (p.AwaitingZeusDiscard)
            {
                var pool = s.UnseenPoolFor(p.ZeusTarget);
                return view.At(Place.Discard).Select(x => CardIndex(x.Slug)).Where(c => c >= 0 && pool.Contains(c)).Distinct()
                    .Select(c => (Reveal)new ZeusDiscardReveal(c)).ToList();
            }
            if (p.AwaitingGateReveal)
            {
                // The deck tops lie face up somewhere off the Pantheon (on their decks in the box area, or on the table); the
                // one the Gate's owner then chose may already lie on their Divinity board.
                var activated = p.Activated(0) | p.Activated(1);
                var shown = view.Pieces
                    .Where(x => x.Place is not (Place.PantheonSpace or Place.Hand) && !x.Piece.FaceDown && x.Piece.Kind == PieceKind.Card)
                    .Select(x => DivinityIndex(x.Slug))
                    .Where(d => d >= 0 && d != PantheonData.Gate && (activated & 1 << d) == 0).Distinct().ToList();
                var tops = new List<List<int>>();
                foreach (var m in PantheonData.Mythologies.Where(m => p.DeckSize(m.Mythology) > 0))
                {
                    var known = p.DeckCard(m.Mythology, 0);
                    var candidates = OfMythology(shown, m.Mythology).Where(d => known < 0 || d == known).ToList();
                    if (candidates.Count == 0)
                    {
                        blocked = "Lay the top card of each Mythology deck face up (the Gate), or name them in the Advisor Window.";
                        return [];
                    }
                    tops.Add(candidates);
                }
                return Combinations(tops).Select(t => (Reveal)new GateReveal(t)).ToList();
            }
            if (p.AwaitingTheaterReveal)
            {
                var m = p.TheaterDeck;
                var player = s.Pending(0).Player;
                // The chosen card may already lie on the Theater's owner's Divinity board, the rest in their hand.
                var activated = p.Activated(0) | p.Activated(1);
                var chosen = view.At(Place.Activated, _sides.Of(player)).Select(x => DivinityIndex(x.Slug))
                    .Where(d => d >= 0 && (activated & 1 << d) == 0);
                var cards = OfMythology(OwnHand(view).Concat(chosen), m);
                if (DeckOrder(p, m, cards, p.DeckSize(m)) is { } order) return [new TheaterReveal(player, m, order)];
                blocked = $"Take the {PantheonData.MythologyDef(m).Name} deck into your hand in TTS to look through it, or name its cards in the Advisor Window.";
                return [];
            }
            if (p.AwaitingMythologyTokens) return SlotTokenReveals(s, p, view, PantheonData.MythologySlots, offerings: false);
            if (p.AwaitingPantheon)
            {
                // Each face-down card, now face up on its space; or, activated before the table was read, on a Divinity
                // board (any newly activated card of its Mythology).
                var activated = p.Activated(0) | p.Activated(1);
                var boards = view.At(Place.Activated).Select(x => DivinityIndex(x.Slug)).Where(d => d >= 0 && (activated & 1 << d) == 0).ToList();
                var spaces = new List<int>();
                var options = new List<List<int>>();
                for (var space = 0; space < PantheonData.SpaceCount; space++)
                {
                    if (!p.IsFaceDown(space)) continue;
                    var physical = ExpectedTable.EngineSpace(space, _sides);
                    var d = view.At(Place.PantheonSpace).Where(x => x.Slot == physical && !x.Piece.FaceDown)
                        .Select(x => DivinityIndex(x.Slug)).FirstOrDefault(-1);
                    var known = p.IsHidden(space) ? -1 : p.Space(space);
                    var candidates = d >= 0 ? [d]
                        : OfMythology(boards, p.SpaceMythology(space)!.Value).Where(c => known < 0 || c == known).ToList();
                    if (candidates.Count == 0)
                    {
                        blocked = "Turn the Pantheon's cards face up (the mod's Age II Setup button does it), or name them in the Advisor Window.";
                        return [];
                    }
                    spaces.Add(space);
                    options.Add(candidates);
                }
                return Combinations(options).Where(c => c.Distinct().Count() == c.Length)
                    .Select(c => (Reveal)new PantheonReveal(spaces.Select((space, i) => (space, c[i])).ToArray())).ToList();
            }
            if (p.AwaitingOfferings) return SlotTokenReveals(s, p, view, PantheonData.OfferingSlots, offerings: true);
            if (p.AwaitingEnki)
            {
                var box = s.TokensSetAside;
                var tokens = view.At(Place.EnkiToken).Select(x => TokenIndex(x.Slug)).Where(t => t >= 0 && (box & 1 << t) != 0)
                    .Distinct().Order().ToArray();
                if (tokens.Length == 2) return [new EnkiReveal(tokens)];
                blocked = "Put Enki's 2 Progress tokens face up on Enki (the mod deals them when Enki is turned face up), or name them in the Advisor Window.";
                return [];
            }
            if (s.PendingCount > 0 && s.Pending(0).Kind == DecisionKind.PlaceDivinity && !p.HasDraw)
            {
                var decision = s.Pending(0);
                var m = (Mythology)decision.Wonder;
                var count = Math.Min(2, p.DeckSize(m));
                var hand = OfMythology(OwnHand(view), m);
                if (DeckOrder(p, m, hand, count) is { } order) return [new DivinityDrawReveal(decision.Player, m, order)];
                // One card already placed face down (a card of that Mythology lies on a space the state has empty), the other
                // still in the hand: the placed one is any card it could be.
                var placed = Enumerable.Range(0, PantheonData.SpaceCount).Any(space => p.Space(space) == PantheonState.EmptySpace
                    && view.At(Place.PantheonSpace).Any(x => x.Slot == ExpectedTable.EngineSpace(space, _sides) && x.Piece.Back == PantheonData.MythologyDef(m).Id));
                if (count == 2 && hand.Count == 1 && placed)
                {
                    var unseen = PantheonData.MythologyDef(m).Divinities.Where(d => (p.SeenDivinities & 1 << d) == 0 && d != hand[0]);
                    var reveals = unseen.Select(d => DeckOrder(p, m, [hand[0], d], count)).OfType<int[]>()
                        .Select(o => (Reveal)new DivinityDrawReveal(decision.Player, m, o)).ToList();
                    if (reveals.Count > 0) return reveals;
                }
                blocked = $"Draw your {(count == 2 ? "2 " : "")}{PantheonData.MythologyDef(m).Name} Divinit{(count == 2 ? "ies" : "y")} into your hand in TTS, or name {(count == 2 ? "them" : "it")} in the Advisor Window.";
                return [];
            }
            return [];
        }

        /// <summary>
        /// The Mythology tokens (Age I) or Offering tokens (Age II) setup puts on the structure, read from the slots; a
        /// token already taken (its card uncovered before the table was read) lies in a player's area: each assignment of
        /// those to the empty slots is tried.
        /// </summary>
        List<Reveal> SlotTokenReveals(GameState s, PantheonState p, TableView view, int[] slots, bool offerings)
        {
            bool Kind(int code) => code >= 0 && code >= PantheonState.OfferingToken == offerings;
            var onSlots = new Dictionary<int, int>();
            foreach (var x in view.At(Place.SlotToken))
                if (slots.Contains(x.Slot) && x.Slug is { } slug && ExpectedTable.TokenCode(slug) is var code && Kind(code)) onSlots.TryAdd(x.Slot, code);
            var empty = slots.Where(slot => !onSlots.ContainsKey(slot)).ToList();
            // Held tokens on the table beyond those the state already gives the players.
            var extra = view.At(Place.HeldToken).Select(x => x.Slug is { } slug ? ExpectedTable.TokenCode(slug) : -1).Where(Kind).ToList();
            for (var player = 0; player < 2; player++)
                foreach (var m in PantheonData.Mythologies)
                    for (var k = 0; k < p.MythologyTokens(player, m.Mythology); k++) extra.Remove((int)m.Mythology);
            for (var player = 0; player < 2; player++)
                foreach (var o in PantheonData.Offerings)
                    if ((p.OfferingTokens(player) & 1 << o.Index) != 0) extra.Remove(PantheonState.OfferingToken + o.Index);
            var reveals = new List<Reveal>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            void Fill(int i, List<int> left)
            {
                if (reveals.Count >= _options.MaxReveals) return;
                if (i == empty.Count)
                {
                    var tokens = slots.Select(slot => (slot, onSlots[slot])).ToArray();
                    if (!seen.Add(string.Join(',', tokens))) return;
                    reveals.Add(offerings
                        ? new OfferingsReveal(tokens.Select(t => (t.slot, t.Item2 - PantheonState.OfferingToken)).ToArray())
                        : new MythologyTokensReveal(tokens.Select(t => (t.slot, (Mythology)t.Item2)).ToArray()));
                    return;
                }
                for (var k = 0; k < left.Count; k++)
                {
                    onSlots[empty[i]] = left[k];
                    var rest = left.Where((_, j) => j != k).ToList();
                    Fill(i + 1, rest);
                    onSlots.Remove(empty[i]);
                }
            }
            Fill(0, extra);
            return reveals;
        }

        /// <summary>Every way of picking one value from each list (capped at the Reveal limit).</summary>
        IEnumerable<int[]> Combinations(List<List<int>> lists)
        {
            var result = new List<int[]> { Array.Empty<int>() };
            foreach (var list in lists)
                result = result.SelectMany(r => list.Select(v => r.Append(v).ToArray())).Take(_options.MaxReveals).ToList();
            return result;
        }

        /// <summary>Evidence for Pantheon's Moves; null for the Moves this doesn't cover.</summary>
        bool? PantheonPlausible(GameState s, int player, Move move, TableView view)
        {
            if (s.Pantheon is not { } p) return null;
            var side = _sides.Of(player);
            var other = SeatSides.Other(side);
            bool OnBoard(Side where, int divinity) => view.At(Place.Activated, where).Any(x => DivinityIndex(x.Slug) == divinity);
            switch (move.Kind)
            {
                case MoveKind.Divinity:
                {
                    // A card of the drawn Mythology now lies on that space (face down); the owner's other card is still in hand.
                    var m = PantheonData.MythologyDef((Mythology)s.Pending(0).Wonder).Id;
                    var physical = ExpectedTable.EngineSpace(move.Space, _sides);
                    if (!view.At(Place.PantheonSpace).Any(x => x.Slot == physical && x.Piece.Back == m)) return false;
                    return move.Divinity < 0 || !OwnHand(view).Contains(move.Divinity);
                }
                case MoveKind.Activate:
                case MoveKind.Gate:
                case MoveKind.Theater:
                    return OnBoard(side, move.Divinity);
                case MoveKind.TheaterDeck:
                {
                    var activated = p.Activated(0) | p.Activated(1);
                    var m = (Mythology)move.Card;
                    return view.At(Place.Activated, side).Select(x => DivinityIndex(x.Slug))
                            .Any(d => d >= 0 && (activated & 1 << d) == 0 && PantheonData.Divinities[d].Mythology == m)
                        || OfMythology(OwnHand(view), m).Count > 0;
                }
                case MoveKind.Enki:
                    return view.At(Place.ProgressArea, side).Any(x => TokenIndex(x.Slug) == move.Token);
                case MoveKind.Snake:
                {
                    var snake = view.At(Place.Snake, other).FirstOrDefault();
                    if (snake is null) return false;
                    // The card under the Snake, when one lies close enough to tell.
                    var under = view.At(Place.City, other).Where(x => CardIndex(x.Slug) >= 0)
                        .Select(x => (Card: CardIndex(x.Slug), Distance: Math.Abs(x.Piece.X - snake.Piece.X) + Math.Abs(x.Piece.Z - snake.Piece.Z)))
                        .Where(x => x.Distance <= 1.0).OrderBy(x => x.Distance).Select(x => x.Card).FirstOrDefault(-1);
                    return under < 0 || under == move.Card || GameData.Card(under).Color != CardColor.Green;
                }
                case MoveKind.Steal:
                    return view.At(Place.City, side).Any(x => x.Piece.Back != "wonder" && CardIndex(x.Slug) == move.Card);
                case MoveKind.Hades:
                    // On the discard pile too: a later Move in the chain may send it back there.
                    return view.Pieces.Any(x => (x.Place == Place.City && x.Side == side || x.Place == Place.Discard)
                        && x.Piece.Back != "wonder" && CardIndex(x.Slug) == move.Card);
                case MoveKind.Zeus:
                    return move.Wonder == s.Age && !view.OnSlot(move.Slot).Any();
                case MoveKind.Anubis:
                    // The Wonder shows unbuilt, or the card it was built with lies on the discard pile (a later Move in
                    // the chain built the Wonder again, with a card the table hides under it).
                    return view.At(Place.Wonder).Any(x => WonderIndex(x.Slug) == move.Wonder && !view.WonderBuilt(x.Side!.Value, x.Row))
                        || view.At(Place.Discard).Any(x => CardIndex(x.Slug) == s.WonderCard(move.Wonder));
                case MoveKind.Isis:
                    return view.At(Place.Wonder, side).Any(x => WonderIndex(x.Slug) == move.Wonder && view.WonderBuilt(side, x.Row))
                        && !view.At(Place.Discard).Any(x => CardIndex(x.Slug) == move.Card);
                case MoveKind.Ra:
                    return view.At(Place.Wonder, side).Any(x => WonderIndex(x.Slug) == move.Wonder);
                case MoveKind.Minerva:
                {
                    // Read like the Conflict Pawn (approximate): the one or two spaces she stands nearest.
                    if (view.MinervaSpace is not { } shown) return false;
                    var expected = _sides.P1 == Side.Red ? move.MilitarySpace : -move.MilitarySpace;
                    return Math.Abs(shown - expected) < 1.0;
                }
                case MoveKind.NeptuneDiscard:
                case MoveKind.NeptuneApply:
                {
                    var t = move.MilitaryToken;
                    var token = view.Looting.FirstOrDefault(x => x.Side == _sides.Of(t / 2) && x.Value == GameState.MilitaryTokenCoins(t));
                    return token is null || !token.OnBoard;
                }
                default:
                    return null;
            }
        }

        /// <summary>Only Moves the table has evidence of are tried (keeps the search to a handful of states).</summary>
        bool Plausible(GameState s, int player, Move move)
        {
            var view = View(s.Age);
            var side = _sides.Of(player);
            var other = SeatSides.Other(side);
            bool Shows(Place place, Side? where, int card) => view.Pieces.Any(p =>
                p.Place == place && (where is null || p.Side == where) && p.Piece.Back != "wonder" && CardIndex(p.Slug) == card);
            bool TokenIn(Side where, int token) => view.At(Place.ProgressArea, where).Any(p => TokenIndex(p.Slug) == token);
            if (PantheonPlausible(s, player, move, view) is { } pantheon) return pantheon;
            switch (move.Kind)
            {
                case MoveKind.Build:
                case MoveKind.Mausoleum:
                case MoveKind.Messe:
                    // On the discard pile too: the Statue of Zeus / Circus Maximus may destroy it later in the chain.
                    return Shows(Place.City, side, move.Card) || Shows(Place.Discard, null, move.Card);
                case MoveKind.Discard:
                    return Shows(Place.Discard, null, move.Card);
                case MoveKind.Destroy:
                    return Shows(Place.Discard, null, move.Card) && !Shows(Place.City, other, move.Card);
                case MoveKind.Wonder:
                    if (!view.At(Place.Wonder, side).Any(p => WonderIndex(p.Slug) == move.Wonder && view.WonderBuilt(side, p.Row))) return false;
                    for (var slot = 0; slot < GameState.SlotCount; slot++)
                        if (s.IsOccupied(slot) && s.SlotCard(slot) == move.Card)
                            return !view.OnSlot(slot).Any(p => CardIndex(p.Slug) == move.Card);
                    return false;
                case MoveKind.Draft:
                    return view.At(Place.Wonder, side).Any(p => WonderIndex(p.Slug) == move.Wonder);
                case MoveKind.Token:
                case MoveKind.Library:
                    return TokenIn(side, move.Token);
                default:
                    // Who starts the next Age, and the Statue of Liberty's keep and give (its cards may still lie loose).
                    return true;
            }
        }

        // Pantheon's slugs (Grand Temples, its Wonders and tokens, Divinities) come only from a Pantheon game's view.
        static int CardIndex(string? slug) => slug is null ? -1 : GameData.TryCardIndex(slug, out var i) ? i
            : PantheonData.TryTempleIndex(slug, out var t) ? PantheonData.Temples[t].Card : -1;
        static int WonderIndex(string? slug) => slug is null ? -1 : GameData.TryWonderIndex(slug, out var i) ? i
            : PantheonData.TryWonderIndex(slug, out var w) ? w : -1;
        static int TokenIndex(string? slug) => slug is null ? -1 : GameData.TryTokenIndex(slug, out var i) ? i
            : PantheonData.TryTokenIndex(slug, out var t) ? t : -1;
        static int DivinityIndex(string? slug) => slug is not null && PantheonData.TryDivinityIndex(slug, out var d) ? d : -1;
    }
}
