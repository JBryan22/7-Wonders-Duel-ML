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
    /// The lines to enter, in order. The opponent's redacted Great Library draw is left out: the Advisor session logs it
    /// by itself when it is due.
    /// </summary>
    public IReadOnlyList<string> Lines { get; } = Entries
        .Where(e => e is not LibraryReveal { Redacted: true })
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
public sealed record ReconcileOptions(int MaxMoves = 4, int MaxEntries = 10, int MaxNodes = 20_000);

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
        var search = new Search(ownerSeat, sides, snapshot, options ?? new ReconcileOptions());
        var start = current.Clone();
        AutoReveals(start, ownerSeat);
        var (missing, extra) = search.Diff(start);
        if (missing.Count == 0 && extra.Count == 0)
            return new Reconciliation(SyncStatus.Matches, [], [], [], search.WhyBlocked(start), search.Notes(start));

        search.Run(start, [], 0);
        // Chains reaching the same Game State differ only in history nobody can see any more (two flipped cards taken
        // before the table was read, in either slot): one of them stands for all.
        var explained = Distinct(search.Explained.GroupBy(e => e.End).Select(g => g.First().Chain).ToList());
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
            var (_, reason) = search.Blocked.OrderBy(b => b.Chain.Count).First();
            var prefixes = Distinct(search.Blocked.Where(b => b.Chain.Count > 0).Select(b => new TableChain(b.Chain)).ToList());
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

    /// <summary>The Reveal nobody at the table can name, logged redacted as AdvisorSession does: the opponent's Library draw.</summary>
    static void AutoReveals(GameState s, int ownerSeat)
    {
        if (s.Step == StepKind.Reveal && s.SlotsToReveal == 0 && s.AwaitingLibraryDraw && s.PlayerToDecide != ownerSeat)
            s.Apply(LibraryReveal.Hidden(s.PlayerToDecide));
    }

    static List<TableChain> Distinct(List<TableChain> chains) =>
        chains.GroupBy(c => c.Text, StringComparer.Ordinal).Select(g => g.First()).ToList();

    sealed class Search
    {
        readonly int _owner;
        readonly SeatSides _sides;
        readonly TableSnapshot _snapshot;
        readonly ReconcileOptions _options;
        readonly Dictionary<int, TableView> _views = [];
        int _nodes;

        public Search(int owner, SeatSides sides, TableSnapshot snapshot, ReconcileOptions options)
        {
            _owner = owner;
            _sides = sides;
            _snapshot = snapshot;
            _options = options;
        }

        /// <summary>Explaining chains with the state each reaches.</summary>
        public List<(TableChain Chain, string End)> Explained { get; } = [];
        public List<(List<LogEntry> Chain, string Reason)> Blocked { get; } = [];

        /// <summary>The snapshot placed with an Age's pyramid grid (0: no grid, during the draft).</summary>
        TableView View(int age)
        {
            if (!_views.TryGetValue(age, out var view)) _views[age] = view = TableView.From(_snapshot, age);
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
            if (chain.Count > 0)
            {
                var (missing, extra) = Diff(s);
                if (missing.Count == 0 && extra.Count == 0)
                {
                    Explained.Add((new TableChain([.. chain]), Convert.ToBase64String(s.ToBytes())));
                    return;
                }
            }
            if (s.Step == StepKind.GameOver || chain.Count >= _options.MaxEntries) return;

            if (s.Step == StepKind.Reveal)
            {
                var reveals = Reveals(s, out var blocked);
                if (blocked is not null)
                {
                    Blocked.Add(([.. chain], blocked));
                    return;
                }
                foreach (var r in reveals) Try(s, chain, r, moves);
                return;
            }
            if (moves >= _options.MaxMoves) return;
            var player = s.PlayerToDecide;
            foreach (var move in s.LegalMoves())
                if (Plausible(s, player, move))
                    Try(s, chain, new MoveEntry(player, move), moves + 1);
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
                var guilds = view.At(Place.Pyramid).Where(p => p.Piece.Back == "guild").Select(p => p.Slot).Distinct().Order().ToList();
                if (guilds.Count == GameState.GuildsPerGame) return [new GuildPatternReveal([.. guilds])];
                // A Guild already taken (before the table was read) left an empty slot: try each.
                var missing = GameState.GuildsPerGame - guilds.Count;
                var takenGuilds = view.Pieces.Count(p => p.Place is Place.City or Place.Discard
                    && CardIndex(p.Slug) is var c and >= 0 && GameData.Cards[c].IsGuild && !s.Seen.Contains(c));
                if (guilds.Count > GameState.GuildsPerGame || takenGuilds < missing) return [];
                var empty = Enumerable.Range(0, GameState.SlotCount).Where(i => !view.OnSlot(i).Any()).ToList();
                var patterns = new List<Reveal>();
                void Pick(int from, List<int> chosen)
                {
                    if (patterns.Count >= 24) return;
                    if (chosen.Count == GameState.GuildsPerGame)
                    {
                        patterns.Add(new GuildPatternReveal([.. chosen.Order()]));
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
                    if (reveals.Count >= 24) return;
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
            return [];
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

        static int CardIndex(string? slug) => slug is not null && GameData.TryCardIndex(slug, out var i) ? i : -1;
        static int WonderIndex(string? slug) => slug is not null && GameData.TryWonderIndex(slug, out var i) ? i : -1;
        static int TokenIndex(string? slug) => slug is not null && GameData.TryTokenIndex(slug, out var i) ? i : -1;
    }
}
