using System.Runtime.CompilerServices;
using SevenWondersDuel.Engine.Data;

[assembly: InternalsVisibleTo("Engine.Tests")]

namespace SevenWondersDuel.Engine;

/// <summary>
/// The complete, canonical game at one moment (spec A4). Mutable: <see cref="Apply(Move)"/> and
/// <see cref="Apply(Reveal)"/> change it in place, and search copies it first with <see cref="Clone"/> or
/// <see cref="CopyFrom"/>. Hidden identities are never stored: a face-down slot holds only its back,
/// <see cref="Hidden"/> or <see cref="HiddenGuild"/>, until a Reveal names it (ADR 0002).
/// </summary>
public sealed class GameState
{
    /// <summary>Slot value of a taken (empty) slot.</summary>
    public const sbyte Empty = -1;
    /// <summary>Slot value of an unknown card of the current Age (face down, or face up but not yet revealed).</summary>
    public const sbyte Hidden = -2;
    /// <summary>Slot value of an unknown Guild: an Age III slot showing the purple Guild back.</summary>
    public const sbyte HiddenGuild = -3;

    /// <summary>The number of Ages; the game ends after the last one.</summary>
    public const int AgeCount = 3;
    /// <summary>Guilds shuffled into Age III (rules-reference §1).</summary>
    public const int GuildsPerGame = 3;
    public const int StartingCoins = 7;
    public const int SlotCount = 20;
    /// <summary>Upper bound on legal Moves in any position, for caller-supplied buffers.</summary>
    public const int MaxMoves = 128;
    /// <summary>Spaces from the centre to a capital: the pawn reaching it is military supremacy (rules-reference §8).</summary>
    public const int CapitalDistance = 9;
    /// <summary>Military tokens on the board at the start: a 2-coin and a 5-coin token on each side.</summary>
    public const int MilitaryTokenCount = 4;
    const int QueueCapacity = 8;
    const uint AllSlots = (1u << SlotCount) - 1;

    /// <summary>Per Age (index Age - 1), per slot: the slots that cover it, as a bitmask.</summary>
    static readonly uint[][] CoveredByMask = GameData.Layouts
        .Select(l => l.Slots.Select(s => s.CoveredBy.Aggregate(0u, (m, c) => m | 1u << c)).ToArray())
        .ToArray();
    /// <summary>Per Age: the top row (row 0, farthest from the players, under every other row), as a bitmask.</summary>
    static readonly uint[] TopRowMask = GameData.Layouts
        .Select(l => l.Slots.Where(s => s.Row == 0).Aggregate(0u, (m, s) => m | 1u << s.Index))
        .ToArray();
    /// <summary>Per Age: the slots laid face up, as a bitmask.</summary>
    static readonly uint[] FaceUpMask = GameData.Layouts
        .Select(l => l.Slots.Where(s => s.FaceUp).Aggregate(0u, (m, s) => m | 1u << s.Index))
        .ToArray();

    readonly sbyte[] _slots = new sbyte[SlotCount];
    readonly int[] _coins = new int[2];
    readonly CardSet[] _city = new CardSet[2];
    /// <summary>Cached production per player and resource (index player * 5 + resource); derived from the city.</summary>
    readonly int[] _production = new int[2 * Resources.Count];
    readonly PendingDecision[] _queue = new PendingDecision[QueueCapacity];
    int _queueCount;
    uint _occupied;
    CardSet _seen;
    CardSet _discard;
    bool _awaitingGuildPattern;
    /// <summary>Military tokens still on the board, one bit per <see cref="MilitaryToken"/> index.</summary>
    byte _militaryTokens;
    /// <summary>Progress tokens set out at setup (0 until their Reveal), still on the board, and owned per player: one bit per token.</summary>
    ushort _tokensSetOut;
    ushort _boardTokens;
    readonly ushort[] _tokens = new ushort[2];
    /// <summary>Wonder masks (one bit per Wonder index): the current draft offer, every Wonder offered so far, removed Wonders.</summary>
    ushort _draftOffer;
    ushort _wondersSeen;
    ushort _wondersRemoved;
    /// <summary>Wonders drafted so far (0..8): which round and whose pick comes next.</summary>
    byte _draftPicks;
    /// <summary>Per player: Wonders owned (built or not, removed included) and Wonders built.</summary>
    readonly ushort[] _wonders = new ushort[2];
    readonly ushort[] _wondersBuilt = new ushort[2];
    /// <summary>Per Wonder: the Age card tucked under it, or -1 while unbuilt.</summary>
    readonly sbyte[] _wonderCard = new sbyte[GameData.Wonders.Length];
    /// <summary>The player whose turn it is gets another turn once the current one ends (a Wonder's "play again").</summary>
    bool _playAgain;
    /// <summary>The Great Library's private draw while its pick is pending (one bit per token), and whether this copy only knows that it was drawn.</summary>
    ushort _libraryDraw;
    bool _libraryDrawHidden;
    /// <summary>
    /// The Statue of Liberty: the returned cards still undecided (3 after their Reveal, 2 after liberty-keep), the
    /// card kept by its owner until liberty-give, and the card given to the opponent until its build (after the
    /// owner's card has resolved fully). Empty / -1 at every other time.
    /// </summary>
    CardSet _libertyOffer;
    sbyte _libertyKept;
    sbyte _libertyGiven;

    GameState(Ruleset rules)
    {
        Rules = rules;
        _libertyKept = _libertyGiven = -1;
    }

    /// <summary>A new game before any Reveal: the first Reveal names the first player.</summary>
    public static GameState New(Ruleset rules)
    {
        var s = new GameState(rules) { FirstPlayer = -1 };
        s._coins[0] = s._coins[1] = StartingCoins;
        s._militaryTokens = (1 << MilitaryTokenCount) - 1;
        Array.Fill(s._slots, Empty);
        Array.Fill(s._wonderCard, (sbyte)-1);
        return s;
    }

    public Ruleset Rules { get; private set; }
    /// <summary>The player who plays first, or -1 until revealed.</summary>
    public int FirstPlayer { get; private set; }
    /// <summary>The current Age, or 0 before Age I starts.</summary>
    public int Age { get; private set; }
    /// <summary>The player whose turn it is (during the start-of-Age choice: the player who took the last card).</summary>
    public int CurrentPlayer { get; private set; }
    public bool IsOver => Result is not null;
    /// <summary>How the game ended, or null while it is running.</summary>
    public Result? Result { get; private set; }

    /// <summary>
    /// The Conflict Pawn: spaces from the centre toward P2's capital (positive) or P1's (negative), -9..9.
    /// Shields move it (<see cref="AddShields"/>).
    /// </summary>
    public int ConflictPawn { get; private set; }
    /// <summary>How many spaces the pawn stands toward this player's opponent's capital (negative if toward their own).</summary>
    public int MilitaryLead(int player) => player == 0 ? ConflictPawn : -ConflictPawn;

    /// <summary>
    /// Index (0..3) of a military token: the one on <paramref name="victim"/>'s side of the track, small (2 coins,
    /// zone 3-5) or large (5 coins, zone 6-8). The victim loses coins when the opponent first pushes the pawn into its zone.
    /// </summary>
    public static int MilitaryToken(int victim, bool large) => victim * 2 + (large ? 1 : 0);
    /// <summary>Coins a military token takes from its victim.</summary>
    public static int MilitaryTokenCoins(int token) => (token & 1) != 0 ? 5 : 2;
    /// <summary>The attacker's lead at which the pawn enters a token's zone.</summary>
    public static int MilitaryTokenZone(int token) => (token & 1) != 0 ? 6 : 3;
    public bool MilitaryTokenLeft(int token) => (_militaryTokens & 1 << token) != 0;
    public int MilitaryTokensLeft => System.Numerics.BitOperations.PopCount((uint)_militaryTokens);

    /// <summary>Cards built showing at least this many different scientific symbols win at once (rules-reference §6).</summary>
    public const int ScienceSupremacySymbols = 6;

    /// <summary>True after the first player is known and before the Progress tokens on the board are revealed.</summary>
    public bool AwaitingTokens => FirstPlayer >= 0 && _tokensSetOut == 0;
    /// <summary>Progress tokens still on the board, one bit per token index.</summary>
    public ushort BoardTokens => _boardTokens;
    public bool TokenOnBoard(int token) => (_boardTokens & 1 << token) != 0;
    /// <summary>The 5 tokens set out at setup (0 until revealed).</summary>
    public ushort TokensSetOut => _tokensSetOut;
    /// <summary>The tokens left in the box at setup, never named: what the Great Library draws from (once set out).</summary>
    public ushort TokensSetAside => (ushort)(ProgressTokens.All & ~_tokensSetOut);
    /// <summary>The Progress tokens a player owns, one bit per token index.</summary>
    public ushort Tokens(int player) => _tokens[player];
    public bool HasToken(int player, int token) => (_tokens[player] & 1 << token) != 0;
    public int TokenCount(int player) => System.Numerics.BitOperations.PopCount((uint)_tokens[player]);

    /// <summary>How many times a player shows a scientific symbol: green cards in the city plus tokens (Law).</summary>
    public int SymbolCount(int player, ScienceSymbol symbol)
    {
        var n = (_city[player] & CardSets.OfSymbol[(int)symbol]).Count;
        if (_tokens[player] != 0)
            foreach (var t in ProgressTokens.In(_tokens[player]))
                if (GameData.Tokens[t].Science == symbol) n++;
        return n;
    }

    /// <summary>Different scientific symbols a player shows, Law included (rules-reference §6).</summary>
    public int DistinctSymbols(int player)
    {
        var n = 0;
        for (var sym = 0; sym < CardSets.OfSymbol.Length; sym++)
            if (SymbolCount(player, (ScienceSymbol)sym) > 0) n++;
        return n;
    }

    /// <summary>
    /// True if this Wonder gives its owner another turn: printed "play again", or any Wonder once Theology is owned
    /// (never twice). Checked when a Wonder is built.
    /// </summary>
    public bool WonderPlaysAgain(int player, int wonder) =>
        GameData.Wonders[wonder].PlayAgain || HasToken(player, ProgressTokens.Theology);

    public AgeLayout Layout => GameData.Layout(Age);
    public int Coins(int player) => _coins[player];
    public CardSet City(int player) => _city[player];
    public CardSet DiscardPile => _discard;
    /// <summary>Every card whose identity has been revealed so far.</summary>
    public CardSet Seen => _seen;
    public int Production(int player, Resource r) => _production[player * Resources.Count + (int)r];
    public int CardsOfColor(int player, CardColor color) => (_city[player] & CardSets.OfColor[(int)color]).Count;
    public int YellowCards(int player) => CardsOfColor(player, CardColor.Yellow);
    // ---------------------------------------------------------------- Wonders

    /// <summary>Wonders drafted in total: 8, in two rounds of 4 (rules-reference §1).</summary>
    public const int DraftSize = 8;
    /// <summary>Wonders offered per draft round.</summary>
    public const int DraftOfferSize = 4;
    /// <summary>Only 7 Wonders can be built in a game: once the 7th is built the last unbuilt one leaves the game.</summary>
    public const int MaxWondersBuilt = 7;

    /// <summary>
    /// The Wonders this game drafts from (spec A4): the 12 base Wonders plus each promo Wonder switched on in
    /// <see cref="Ruleset.Promos"/>, so 12, 13 or 14. The draft itself is the same for every pool.
    /// </summary>
    public ushort WonderPool => PoolFor(Rules.Promos);

    /// <summary>The Wonder pool for a set of promo switches.</summary>
    public static ushort PoolFor(Promos promos) => PoolByPromos[(int)promos];
    static readonly ushort[] PoolByPromos = Enumerable.Range(0, (int)(Promos.Messe | Promos.Liberty) + 1)
        .Select(p => (ushort)GameData.Wonders
            .Where(w => !w.IsPromo || ((Promos)p & PromoOf(w)) != 0)
            .Aggregate(0, (m, w) => m | 1 << w.Index))
        .ToArray();
    static Promos PromoOf(WonderDef w) => w.Effect switch
    {
        WonderEffect.Messe => Promos.Messe,
        WonderEffect.Liberty => Promos.Liberty,
        _ => throw new InvalidOperationException($"Promo Wonder '{w.Id}' has no Ruleset switch"),
    };

    /// <summary>True during setup when the next draft round's 4 Wonders must be revealed.</summary>
    public bool AwaitingDraftOffer => _tokensSetOut != 0 && Age == 0 && _draftOffer == 0 && _draftPicks < DraftSize;
    /// <summary>The Wonders on offer in the current draft round, one bit per Wonder index (0 outside the draft).</summary>
    public ushort DraftOffer => _draftOffer;
    /// <summary>Wonders drafted so far (0..8).</summary>
    public int DraftPicks => _draftPicks;
    /// <summary>Wonders of the pool never offered so far: what a draft Reveal draws from.</summary>
    public ushort UnseenWonders => (ushort)(WonderPool & ~_wondersSeen);

    /// <summary>
    /// Who makes draft pick <paramref name="pick"/> (0..7): A-B-B-A per round, where A is the first player in
    /// round 1 and the second player in round 2 (rules-reference §1, mistake 19).
    /// </summary>
    public int DraftPicker(int pick)
    {
        var a = pick < DraftOfferSize ? FirstPlayer : 1 - FirstPlayer;
        var i = pick % DraftOfferSize;
        return i is 0 or 3 ? a : 1 - a;
    }

    /// <summary>The Wonders a player drafted, built or not (a removed Wonder stays listed), one bit per Wonder index.</summary>
    public ushort Wonders(int player) => _wonders[player];
    /// <summary>The Wonders a player has built.</summary>
    public ushort BuiltWonders(int player) => _wondersBuilt[player];
    /// <summary>Wonders that left the game unbuilt (the 8th once 7 are built).</summary>
    public ushort RemovedWonders => _wondersRemoved;
    public bool OwnsWonder(int player, int wonder) => (_wonders[player] & 1 << wonder) != 0;
    public bool IsWonderBuilt(int wonder) => ((_wondersBuilt[0] | _wondersBuilt[1]) & 1 << wonder) != 0;
    public bool IsWonderRemoved(int wonder) => (_wondersRemoved & 1 << wonder) != 0;
    /// <summary>The player's Wonders that can still be built: owned, unbuilt and not removed.</summary>
    public ushort BuildableWonders(int player) => (ushort)(_wonders[player] & ~_wondersBuilt[player] & ~_wondersRemoved);
    /// <summary>The Age card tucked under a built Wonder, or -1.</summary>
    public int WonderCard(int wonder) => _wonderCard[wonder];
    /// <summary>The Age cards tucked under a player's built Wonders.</summary>
    public CardSet CardsUnderWonders(int player)
    {
        var set = CardSet.Empty;
        foreach (var w in WondersIn(_wondersBuilt[player])) set = set.With(_wonderCard[w]);
        return set;
    }
    /// <summary>Wonders this player has built (the Arena and the Builders Guild count them).</summary>
    public int WondersBuilt(int player) => System.Numerics.BitOperations.PopCount((uint)_wondersBuilt[player]);
    public int TotalWondersBuilt => WondersBuilt(0) + WondersBuilt(1);
    /// <summary>
    /// True while the current turn has earned a play-again that has not started yet: only between a play-again Wonder
    /// and the end of its turn (for example while a decision it caused is still queued). When the turn ends the flag
    /// is spent: the same player gets the next take-card decision, queued behind any Reveals.
    /// </summary>
    public bool PlayAgain => _playAgain;

    /// <summary>Progress tokens the Great Library draws from the box (rules-reference §7).</summary>
    public const int LibraryDrawSize = 3;
    /// <summary>
    /// The Great Library's draw while its pick is pending, one bit per token: the 3 tokens its owner (the player to
    /// decide) chooses from. 0 before the draw's Reveal, after the pick, and in the opponent's Observation.
    /// </summary>
    public ushort LibraryDraw => _libraryDraw;
    /// <summary>
    /// True when the Great Library's draw happened but this state does not know it: the opponent's Observation
    /// (<see cref="Observe"/>). Its owner may then pick any token left in the box, as far as this state can tell.
    /// </summary>
    public bool LibraryDrawHidden => _libraryDrawHidden;
    /// <summary>True when the Great Library was just built and its private draw must be revealed (after any flips).</summary>
    public bool AwaitingLibraryDraw =>
        _queueCount > 0 && _queue[0].Kind == DecisionKind.Library && _libraryDraw == 0 && !_libraryDrawHidden;
    /// <summary>The Statue of Liberty's returned cards per Age (promo-wonders §3): the 3 removed unseen at setup.</summary>
    public const int ReturnedCardCount = 3;
    /// <summary>
    /// The Statue of Liberty's returned cards still undecided: all 3 while liberty-keep is pending, the other 2 while
    /// liberty-give is pending, otherwise empty. Public: both players see them (promo-wonders §3.1.5).
    /// </summary>
    public CardSet LibertyOffer => _libertyOffer;
    /// <summary>The card the Statue's owner chose to keep, while liberty-give is pending; otherwise -1.</summary>
    public int LibertyKept => _libertyKept;
    /// <summary>True when the Statue of Liberty was just built and its 3 returned cards must be revealed (after any flips).</summary>
    public bool AwaitingReturnedCards =>
        _queueCount > 0 && _queue[0].Kind == DecisionKind.LibertyKeep && _libertyOffer.IsEmpty;

    /// <summary>The colour the pending Destroy decision targets (brown for Zeus, grey for Circus Maximus), or null.</summary>
    public CardColor? DestroyColor =>
        _queueCount > 0 && _queue[0].Kind == DecisionKind.Destroy ? DestroyColorOf(_queue[0].Wonder) : null;
    static CardColor DestroyColorOf(int wonder) =>
        GameData.Wonders[wonder].Effect == WonderEffect.DestroyBrown ? CardColor.Brown : CardColor.Grey;

    /// <summary>
    /// The occupied slots of the current Age's top row: what the Messe may take, covered or not (promo-wonders §2).
    /// The top row is face up in every Age, so these cards are always known. It empties only with the whole structure.
    /// </summary>
    public uint TopRowSlots => Age == 0 ? 0 : _occupied & TopRowMask[Age - 1];

    /// <summary>The Wonders in a mask, in index order.</summary>
    public static Bits WondersIn(ushort mask) => new(mask);

    /// <summary>The card in a slot, or its back: <see cref="Empty"/>, <see cref="Hidden"/> or <see cref="HiddenGuild"/>.</summary>
    public int SlotCard(int slot) => _slots[slot];
    public bool IsOccupied(int slot) => (_occupied & 1u << slot) != 0;
    public bool IsAccessible(int slot) => IsOccupied(slot) && (_occupied & CoveredByMask[Age - 1][slot]) == 0;
    public int CardsLeftInAge => System.Numerics.BitOperations.PopCount(_occupied);
    /// <summary>True until the first card of the current Age is taken.</summary>
    public bool AgeUntouched => Age > 0 && _occupied == AllSlots;
    /// <summary>True at the start of Age III until the Guild back pattern is revealed.</summary>
    public bool AwaitingGuildPattern => _awaitingGuildPattern;

    /// <summary>Non-Guild cards of an Age that could still be revealed (never named so far).</summary>
    public CardSet UnseenPool(int age) => CardSets.OfAge[age] & ~_seen;
    /// <summary>Guilds that could still be revealed.</summary>
    public CardSet UnseenGuilds => CardSets.Guilds & ~_seen;
    /// <summary>The cards a hidden slot could turn out to be: unseen Guilds behind a Guild back, else unseen cards of the Age.</summary>
    public CardSet UnseenPoolFor(int slot) => _slots[slot] == HiddenGuild ? UnseenGuilds : UnseenPool(Age);

    /// <summary>Slots that should show their card (face up, or accessible) but whose identity is not yet known.</summary>
    public uint SlotsToReveal
    {
        get
        {
            if (Age == 0 || _awaitingGuildPattern) return 0;
            uint mask = 0;
            for (var s = 0; s < SlotCount; s++)
                if (_slots[s] <= Hidden && ((FaceUpMask[Age - 1] & 1u << s) != 0 || IsAccessible(s)))
                    mask |= 1u << s;
            return mask;
        }
    }

    /// <summary>What the game needs next: a Reveal from the Chance Source, a Move, or nothing.</summary>
    public StepKind Step => IsOver ? StepKind.GameOver
        : FirstPlayer < 0 || AwaitingTokens || AwaitingDraftOffer || _awaitingGuildPattern || SlotsToReveal != 0
            || AwaitingLibraryDraw || AwaitingReturnedCards ? StepKind.Reveal
        : StepKind.Decision;

    public int PendingCount => _queueCount;
    public PendingDecision Pending(int i) => i < _queueCount ? _queue[i] : throw new ArgumentOutOfRangeException(nameof(i));
    /// <summary>The player who must make the next Move (may differ from <see cref="CurrentPlayer"/>).</summary>
    public int PlayerToDecide => _queueCount > 0 ? _queue[0].Player : throw new InvalidOperationException("No Pending Decision");

    // ---------------------------------------------------------------- legal Moves

    /// <summary>Writes the legal Moves into <paramref name="buffer"/> (at least <see cref="MaxMoves"/> long) and returns their count.</summary>
    public int LegalMoves(Span<Move> buffer)
    {
        if (Step != StepKind.Decision) return 0;
        var n = 0;
        var player = _queue[0].Player;
        switch (_queue[0].Kind)
        {
            case DecisionKind.ChooseStarter:
                buffer[n++] = Move.StartMe;
                buffer[n++] = Move.StartOpponent;
                break;
            case DecisionKind.ChooseToken:
                foreach (var t in ProgressTokens.In(_boardTokens)) buffer[n++] = Move.PickToken(t);
                break;
            case DecisionKind.DraftWonder:
                foreach (var w in WondersIn(_draftOffer)) buffer[n++] = Move.Draft(w);
                break;
            case DecisionKind.Destroy:
                foreach (var c in DestroyTargets(player, _queue[0].Wonder).Cards()) buffer[n++] = Move.Destroy(c);
                break;
            case DecisionKind.Mausoleum:
                foreach (var c in _discard.Cards()) buffer[n++] = Move.Mausoleum(c);
                break;
            case DecisionKind.Library:
                foreach (var t in ProgressTokens.In(LibraryOptions)) buffer[n++] = Move.Library(t);
                break;
            case DecisionKind.Messe:
                for (var s = 0; s < SlotCount; s++)
                    if ((TopRowSlots & 1u << s) != 0) buffer[n++] = Move.Messe(_slots[s]);
                break;
            case DecisionKind.LibertyKeep:
                foreach (var c in _libertyOffer.Cards()) buffer[n++] = Move.LibertyKeep(c);
                break;
            case DecisionKind.LibertyGive:
                foreach (var c in _libertyOffer.Cards()) buffer[n++] = Move.LibertyGive(c);
                break;
            default:
                // Which Wonders the player can afford does not depend on the card taken.
                Span<int> wonders = stackalloc int[DraftOfferSize];
                var wonderCount = 0;
                foreach (var w in WondersIn(BuildableWonders(player)))
                    if (CanAffordWonder(player, w)) wonders[wonderCount++] = w;
                for (var s = 0; s < SlotCount; s++)
                {
                    if (!IsAccessible(s)) continue;
                    var card = _slots[s];
                    if (CanAffordBuild(player, card)) buffer[n++] = Move.Build(card);
                    buffer[n++] = Move.Discard(card);
                    for (var i = 0; i < wonderCount; i++) buffer[n++] = Move.BuildWonder(card, wonders[i]);
                }
                break;
        }
        return n;
    }

    public List<Move> LegalMoves()
    {
        Span<Move> buffer = stackalloc Move[MaxMoves];
        return buffer[..LegalMoves(buffer)].ToArray().ToList();
    }

    public bool IsLegal(Move move) => WhyIllegal(move) is null;

    /// <summary>Null if the Move is legal now, otherwise the reason.</summary>
    public string? WhyIllegal(Move move)
    {
        if (Step != StepKind.Decision) return $"the game is waiting for {(IsOver ? "nothing (it is over)" : "a Reveal")}";
        var decision = _queue[0];
        if (decision.Kind == DecisionKind.ChooseStarter)
            return move.Kind is MoveKind.StartMe or MoveKind.StartOpponent ? null : "the decision is who starts the Age";
        if (decision.Kind == DecisionKind.ChooseToken)
            return move.Kind != MoveKind.Token ? "the decision is which Progress token to take (it cannot be declined)"
                : move.Token < 0 || move.Token >= ProgressTokens.Count ? "unknown Progress token"
                : !TokenOnBoard(move.Token) ? $"{GameData.Tokens[move.Token].Id} is not on the board"
                : null;
        if (decision.Kind == DecisionKind.DraftWonder)
            return move.Kind != MoveKind.Draft ? "the decision is which Wonder to draft"
                : move.Wonder < 0 || move.Wonder >= GameData.Wonders.Length ? "unknown Wonder"
                : (_draftOffer & 1 << move.Wonder) == 0 ? $"{GameData.Wonders[move.Wonder].Id} is not in the draft offer"
                : null;
        if (decision.Kind == DecisionKind.Destroy)
        {
            var color = DestroyColorOf(decision.Wonder);
            return move.Kind != MoveKind.Destroy ? $"the decision is which {color.ToString().ToLowerInvariant()} card to destroy"
                : move.Card < 0 || move.Card >= GameData.Cards.Length ? "unknown card"
                : !DestroyTargets(decision.Player, decision.Wonder).Contains(move.Card)
                    ? $"{GameData.Cards[move.Card].Id} is not a {color.ToString().ToLowerInvariant()} card in P{2 - decision.Player}'s city"
                : null;
        }
        if (decision.Kind == DecisionKind.Mausoleum)
            return move.Kind != MoveKind.Mausoleum ? "the decision is which discarded card the Mausoleum builds"
                : move.Card < 0 || move.Card >= GameData.Cards.Length ? "unknown card"
                : !_discard.Contains(move.Card) ? $"{GameData.Cards[move.Card].Id} is not in the discard pile"
                : null;
        if (decision.Kind == DecisionKind.Library)
            return move.Kind != MoveKind.Library ? "the decision is which Great Library token to keep"
                : move.Token < 0 || move.Token >= ProgressTokens.Count ? "unknown Progress token"
                : (LibraryOptions & 1 << move.Token) == 0 ? $"{GameData.Tokens[move.Token].Id} is not in the Great Library draw"
                : null;
        if (decision.Kind == DecisionKind.Messe)
        {
            if (move.Kind != MoveKind.Messe) return "the decision is which top-row card the Messe builds";
            if (move.Card < 0 || move.Card >= GameData.Cards.Length) return "unknown card";
            var top = SlotOf(move.Card);
            return top >= 0 && (TopRowSlots & 1u << top) != 0 ? null
                : $"{GameData.Cards[move.Card].Id} is not in the top row of the Age {Age} structure";
        }
        if (decision.Kind is DecisionKind.LibertyKeep or DecisionKind.LibertyGive)
        {
            var keep = decision.Kind == DecisionKind.LibertyKeep;
            return move.Kind != (keep ? MoveKind.LibertyKeep : MoveKind.LibertyGive)
                    ? keep ? "the decision is which returned card the Statue of Liberty's owner builds"
                        : "the decision is which returned card the opponent builds (the other is discarded)"
                : move.Card < 0 || move.Card >= GameData.Cards.Length ? "unknown card"
                : !_libertyOffer.Contains(move.Card) ? $"{GameData.Cards[move.Card].Id} is not among the Statue of Liberty's cards on offer"
                : null;
        }
        if (!move.TakesCard) return "the decision is which card to take";

        if (move.Card < 0 || move.Card >= GameData.Cards.Length) return "unknown card";
        var slot = SlotOf(move.Card);
        if (slot < 0) return $"{GameData.Cards[move.Card].Id} is not in the layout";
        if (!IsAccessible(slot)) return $"{GameData.Cards[move.Card].Id} is covered";
        return move.Kind switch
        {
            MoveKind.Discard => null,
            MoveKind.Wonder => WhyWonderIllegal(decision.Player, move.Wonder),
            _ => CanAffordBuild(decision.Player, move.Card) ? null
                : $"{GameData.Cards[move.Card].Id} costs {Payment.Quote(this, decision.Player, move.Card).Total} coins, player has {_coins[decision.Player]}",
        };
    }

    string? WhyWonderIllegal(int player, int wonder)
    {
        if (wonder < 0 || wonder >= GameData.Wonders.Length) return "unknown Wonder";
        var id = GameData.Wonders[wonder].Id;
        return IsWonderRemoved(wonder) ? $"{id} left the game: 7 Wonders are built"
            : !OwnsWonder(player, wonder) ? $"P{player + 1} does not own {id}"
            : IsWonderBuilt(wonder) ? $"{id} is already built"
            : !CanAffordWonder(player, wonder)
                ? $"{id} costs {Payment.QuoteWonder(this, player, wonder).Total} coins, player has {_coins[player]}"
            : null;
    }

    bool CanAffordBuild(int player, int card) => Payment.Quote(this, player, card).Total <= _coins[player];
    bool CanAffordWonder(int player, int wonder) => Payment.QuoteWonder(this, player, wonder).Total <= _coins[player];

    /// <summary>The opponent's cards a destroying Wonder can send to the discard pile.</summary>
    CardSet DestroyTargets(int player, int wonder) => _city[1 - player] & CardSets.OfColor[(int)DestroyColorOf(wonder)];

    /// <summary>
    /// The tokens the pending Great Library pick may keep: the draw, or (when this state does not know the draw) any
    /// token left in the box that nobody owns.
    /// </summary>
    ushort LibraryOptions => _libraryDrawHidden
        ? (ushort)(TokensSetAside & ~_tokens[0] & ~_tokens[1])
        : _libraryDraw;

    int SlotOf(int card)
    {
        for (var s = 0; s < SlotCount; s++)
            if (_slots[s] == card) return s;
        return -1;
    }

    // ---------------------------------------------------------------- applying

    public void Apply(Move move)
    {
        if (WhyIllegal(move) is { } why) throw new InvalidOperationException($"Illegal move '{move}': {why}");
        var player = _queue[0].Player;
        var wonder = _queue[0].Wonder;
        PopDecision();

        switch (move.Kind)
        {
            case MoveKind.LibertyKeep:
                _libertyOffer = _libertyOffer.Without(move.Card);
                _libertyKept = (sbyte)move.Card;
                PushDecision(new PendingDecision(DecisionKind.LibertyGive, player, wonder));
                return;
            case MoveKind.LibertyGive:
                // The third card goes to the discard pile (promo-wonders §3.4: [7WD] loses it). Then the owner's card
                // resolves fully, token pick or win included, before the opponent's card (FinishDecision).
                _discard |= _libertyOffer.Without(move.Card);
                _libertyOffer = CardSet.Empty;
                var kept = _libertyKept;
                _libertyKept = -1;
                _libertyGiven = (sbyte)move.Card;
                Build(player, kept, free: true);
                FinishDecision();
                return;
            case MoveKind.Token:
                TakeToken(player, move.Token);
                FinishDecision();
                return;
            case MoveKind.Library:
                // The other 2 tokens go back to the box unseen by the opponent.
                _libraryDraw = 0;
                _libraryDrawHidden = false;
                TakeToken(player, move.Token);
                FinishDecision();
                return;
            case MoveKind.Destroy:
                RemoveFromCity(1 - player, move.Card);
                _discard = _discard.With(move.Card);
                FinishDecision();
                return;
            case MoveKind.Mausoleum:
                _discard = _discard.Without(move.Card);
                Build(player, move.Card, free: true);
                FinishDecision();
                return;
            case MoveKind.Messe:
                // No card lies under a top-row card, so taking one uncovers nothing and no Reveal follows.
                var top = SlotOf(move.Card);
                _slots[top] = Empty;
                _occupied &= ~(1u << top);
                Build(player, move.Card, free: true);
                FinishDecision();
                return;
        }

        if (move.Kind == MoveKind.Draft)
        {
            DraftWonder(player, move.Wonder);
            return;
        }

        if (!move.TakesCard)
        {
            CurrentPlayer = move.Kind == MoveKind.StartMe ? player : 1 - player;
            PushDecision(new PendingDecision(DecisionKind.TakeCard, CurrentPlayer));
            return;
        }

        var slot = SlotOf(move.Card);
        _slots[slot] = Empty;
        _occupied &= ~(1u << slot);

        if (move.Kind is MoveKind.Build or MoveKind.Wonder)
        {
            if (move.Kind == MoveKind.Build) Build(player, move.Card);
            else BuildWonder(player, move.Card, move.Wonder);
            if (IsOver)
            {
                // Supremacy ends the game in the middle of a move (rules-reference §12 mistake 14).
                _queueCount = 0;
                return;
            }
        }
        else
        {
            // Discard value counts yellow cards already in the city (rules-reference §12 mistake 5).
            _coins[player] += 2 + YellowCards(player);
            _discard = _discard.With(move.Card);
        }

        if (_queueCount == 0) EndTurn();
    }

    /// <summary>
    /// After a decision a Wonder or a science pair asked for: a win clears the queue, an empty queue ends the turn.
    /// The Statue of Liberty's given card is built here, once its owner's card has resolved (its token pick
    /// included): it goes to the opponent of the player whose turn it is, who may then owe a token pick, or win.
    /// </summary>
    void FinishDecision()
    {
        if (!IsOver && _queueCount == 0 && _libertyGiven >= 0)
        {
            var given = _libertyGiven;
            _libertyGiven = -1;
            Build(1 - CurrentPlayer, given, free: true);
        }
        if (IsOver)
        {
            _queueCount = 0;
            _libertyGiven = -1;
        }
        else if (_queueCount == 0) EndTurn();
    }

    /// <summary>
    /// Puts a card into a player's city with all its effects. A <paramref name="free"/> build (the Mausoleum, the Messe, the Statue of Liberty) pays
    /// nothing and earns no Urbanism coins (rules-reference §12 mistake 4); everything else applies as usual,
    /// including a science pair or win.
    /// </summary>
    void Build(int player, int card, bool free = false)
    {
        var def = GameData.Cards[card];
        var opponent = 1 - player;
        if (!free)
        {
            var chained = Payment.IsChained(this, player, card);
            var quote = Payment.Quote(this, player, card);
            _coins[player] -= quote.Total;
            // Economy: the opponent's trading coins (after discounts, not printed costs) go to its owner.
            if (HasToken(opponent, ProgressTokens.Economy)) _coins[opponent] += quote.TradeCoins;
            AddToCity(player, card);
            if (chained && HasToken(player, ProgressTokens.Urbanism)) _coins[player] += ProgressTokens.UrbanismChainCoins;
        }
        else AddToCity(player, card);
        // One-time coins, counted with the new card already in the city (the Lighthouse counts itself).
        _coins[player] += def.Coins + def.CoinsPerAmount * def.CoinsPer switch
        {
            CoinsPerKind.Brown => CardsOfColor(player, CardColor.Brown),
            CoinsPerKind.Grey => CardsOfColor(player, CardColor.Grey),
            CoinsPerKind.Red => CardsOfColor(player, CardColor.Red),
            CoinsPerKind.Yellow => CardsOfColor(player, CardColor.Yellow),
            CoinsPerKind.Wonder => WondersBuilt(player),
            _ => 0,
        };
        if (def.IsGuild) _coins[player] += Scoring.GuildCoins(this, def.Guild);
        // Strategy adds a shield to red cards built after it is taken, never to Wonders.
        if (def.Shields > 0)
            AddShields(player, def.Shields + (def.Color == CardColor.Red && HasToken(player, ProgressTokens.Strategy) ? 1 : 0));
        if (def.Science is { } symbol && !IsOver) GainSymbol(player, symbol);
    }

    /// <summary>
    /// Builds a Wonder with an Age card (rules-reference §2, §7): pays the cheapest way (Architecture applies, the
    /// trading coins go to an opponent who owns Economy), tucks the card under it (never to the discard pile), then
    /// applies its coins, the opponent's coin loss and its shields. Once the 7th Wonder of the game is built, the
    /// last unbuilt one leaves the game. "Play again" (printed, or from Theology) is granted only if the game goes on;
    /// it takes effect when the turn ends, after the Reveals of newly accessible cards.
    /// The choice powers (Zeus, Circus Maximus, Mausoleum, Great Library, the Messe, the Statue of Liberty) then queue their decision.
    /// </summary>
    void BuildWonder(int player, int card, int wonder)
    {
        var def = GameData.Wonders[wonder];
        var opponent = 1 - player;
        var quote = Payment.QuoteWonder(this, player, wonder);
        _coins[player] -= quote.Total;
        if (HasToken(opponent, ProgressTokens.Economy)) _coins[opponent] += quote.TradeCoins;
        _wondersBuilt[player] |= (ushort)(1 << wonder);
        _wonderCard[wonder] = (sbyte)card;

        // "7 Wonders, not one more!" (rules-reference §7): the one Wonder still unbuilt leaves the game.
        if (TotalWondersBuilt == MaxWondersBuilt)
            _wondersRemoved = (ushort)((_wonders[0] | _wonders[1]) & ~(_wondersBuilt[0] | _wondersBuilt[1]));

        _coins[player] += def.Coins;
        _coins[opponent] = Math.Max(0, _coins[opponent] - def.OpponentLoses);
        // Wonder shields never get Strategy's bonus (rules-reference §12 mistake 9).
        if (def.Shields > 0) AddShields(player, def.Shields);
        if (IsOver) return;
        if (WonderPlaysAgain(player, wonder)) _playAgain = true;

        // The choice powers: queued before the turn ends, so they come after the flips and before any play-again.
        // A power with nothing to choose from does nothing (rules-reference §13), without a decision.
        switch (def.Effect)
        {
            case WonderEffect.DestroyBrown or WonderEffect.DestroyGrey:
                if (!DestroyTargets(player, wonder).IsEmpty)
                    PushDecision(new PendingDecision(DecisionKind.Destroy, player, wonder));
                break;
            case WonderEffect.Mausoleum:
                if (!_discard.IsEmpty) PushDecision(new PendingDecision(DecisionKind.Mausoleum, player, wonder));
                break;
            case WonderEffect.Library:
                // The draw itself is a private Reveal (AwaitingLibraryDraw); the box always holds 5 tokens.
                if (System.Numerics.BitOperations.PopCount((uint)TokensSetAside) >= LibraryDrawSize)
                    PushDecision(new PendingDecision(DecisionKind.Library, player, wonder));
                break;
            case WonderEffect.Messe:
                // Empty only when the Messe's own Age card was the Age's last (then the effect does nothing).
                if (TopRowSlots != 0) PushDecision(new PendingDecision(DecisionKind.Messe, player, wonder));
                break;
            case WonderEffect.Liberty:
                // Never skipped: an Age card was just taken, so this Age's 3 removed cards exist (promo-wonders
                // §3.1.10). Their Reveal (AwaitingReturnedCards) comes after the flips, then liberty-keep and -give.
                PushDecision(new PendingDecision(DecisionKind.LibertyKeep, player, wonder));
                break;
        }
    }

    /// <summary>Takes a Wonder from the offer; the next pick, the next round's Reveal, or Age I follows.</summary>
    void DraftWonder(int player, int wonder)
    {
        _draftOffer &= (ushort)~(1 << wonder);
        _wonders[player] |= (ushort)(1 << wonder);
        _draftPicks++;
        if (_draftPicks == DraftSize)
        {
            // The draft's first player starts Age I (rules-reference §1).
            CurrentPlayer = FirstPlayer;
            StartAge(1);
        }
        else if (_draftOffer != 0)
            PushDecision(new PendingDecision(DecisionKind.DraftWonder, DraftPicker(_draftPicks)));
        // else: round 2's offer is revealed next (AwaitingDraftOffer).
    }

    /// <summary>
    /// A player has just gained a scientific symbol (a green card entering the city by any route, or Law). Six
    /// different symbols win at once; otherwise a completed pair asks for a Progress token while any remain on the
    /// board (compulsory, spec A3). Each symbol exists twice, so a pair forms at most once (rules-reference §6).
    /// </summary>
    void GainSymbol(int player, ScienceSymbol symbol)
    {
        if (DistinctSymbols(player) >= ScienceSupremacySymbols)
        {
            Result = Scoring.Supremacy(this, player, VictoryType.Science);
            return;
        }
        if (SymbolCount(player, symbol) == 2 && _boardTokens != 0)
            PushDecision(new PendingDecision(DecisionKind.ChooseToken, player));
    }

    /// <summary>
    /// Gives a player a Progress token: from the board after a pair, or the Great Library's pick from the box. Applies the immediate effects: coins (Agriculture, Urbanism) and Law's symbol, which can win by science.
    /// </summary>
    internal void TakeToken(int player, int token)
    {
        var def = GameData.Tokens[token];
        _boardTokens &= (ushort)~(1 << token);
        _tokens[player] |= (ushort)(1 << token);
        _coins[player] += def.Coins;
        if (def.Science is { } symbol) GainSymbol(player, symbol);
    }

    /// <summary>
    /// Moves the Conflict Pawn <paramref name="shields"/> spaces toward the opponent's capital, one space at a time
    /// (rules-reference §8). Entering a zone that still holds a military token loots it: the opponent loses that many
    /// coins (all they have if fewer) to the bank, and the token leaves the game. Reaching the capital ends the game
    /// at once with a military victory. Retreating never loots, so each token triggers at most once.
    /// Red cards (with Strategy's extra shield already added by the caller) and Wonders feed it.
    /// </summary>
    internal void AddShields(int player, int shields)
    {
        var opponent = 1 - player;
        var step = player == 0 ? 1 : -1;
        for (var i = 0; i < shields; i++)
        {
            ConflictPawn += step;
            var lead = MilitaryLead(player);
            if (lead >= CapitalDistance)
            {
                Result = Scoring.Supremacy(this, player, VictoryType.Military);
                return;
            }
            for (var t = MilitaryToken(opponent, false); t <= MilitaryToken(opponent, true); t++)
            {
                if (lead != MilitaryTokenZone(t) || !MilitaryTokenLeft(t)) continue;
                _coins[opponent] = Math.Max(0, _coins[opponent] - MilitaryTokenCoins(t));
                _militaryTokens &= (byte)~(1 << t);
            }
        }
    }

    public void Apply(Reveal reveal)
    {
        if (IsOver) throw new InvalidOperationException("The game is over");
        switch (reveal)
        {
            case FirstPlayerReveal f:
                if (FirstPlayer >= 0) throw new InvalidOperationException("The first player is already known");
                if (f.Player is not (0 or 1)) throw new InvalidOperationException("The first player must be P1 or P2");
                FirstPlayer = CurrentPlayer = f.Player;
                break;

            case TokenReveal t:
                ApplyTokenReveal(t);
                break;

            case DraftReveal d:
                ApplyDraftReveal(d);
                break;

            case GuildPatternReveal g:
                ApplyGuildPattern(g);
                break;

            case SlotReveal r:
                ApplySlotReveal(r);
                break;

            case LibraryReveal l:
                ApplyLibraryReveal(l);
                break;

            case ReturnedReveal r:
                ApplyReturnedReveal(r);
                break;

            default:
                throw new InvalidOperationException($"Unsupported Reveal {reveal.GetType().Name}");
        }
    }

    /// <summary>
    /// The Great Library's private draw: 3 different tokens of the 5 left in the box. It comes after the Reveals of
    /// cards the Wonder's Age card uncovered, so every log orders them the same way.
    /// </summary>
    void ApplyLibraryReveal(LibraryReveal l)
    {
        if (!AwaitingLibraryDraw) throw new InvalidOperationException("The game is not waiting for a Great Library draw");
        if (SlotsToReveal != 0) throw new InvalidOperationException("Newly accessible cards are revealed before the Great Library draw");
        if (l.Player != _queue[0].Player)
            throw new InvalidOperationException($"The Great Library draw is P{_queue[0].Player + 1}'s, not P{l.Player + 1}'s");
        if (l.Redacted)
        {
            // Logged by someone outside the audience (spec A4): the draw happened and stays unknown forever.
            _libraryDrawHidden = true;
            return;
        }
        if (l.Tokens.Length != LibraryDrawSize || l.Tokens.Distinct().Count() != LibraryDrawSize)
            throw new InvalidOperationException($"The Great Library draws {LibraryDrawSize} different tokens");
        ushort mask = 0;
        foreach (var token in l.Tokens)
        {
            if (token < 0 || token >= ProgressTokens.Count) throw new InvalidOperationException($"Unknown Progress token {token}");
            if ((TokensSetAside & 1 << token) == 0)
                throw new InvalidOperationException($"{GameData.Tokens[token].Id} was set out on the board, not left in the box");
            mask |= (ushort)(1 << token);
        }
        _libraryDraw = mask;
    }

    /// <summary>
    /// The Statue of Liberty's 3 returned cards: unseen non-Guild cards of the current Age (the 3 removed at setup
    /// are exchangeable with the face-down slots, ADR 0002). Public, and after the flips its Age card caused.
    /// </summary>
    void ApplyReturnedReveal(ReturnedReveal r)
    {
        if (!AwaitingReturnedCards) throw new InvalidOperationException("The game is not waiting for the Statue of Liberty's returned cards");
        if (SlotsToReveal != 0) throw new InvalidOperationException("Newly accessible cards are revealed before the Statue of Liberty's cards");
        if (r.Age != Age) throw new InvalidOperationException($"The returned cards are Age {Age}'s, not Age {r.Age}'s");
        if (r.Cards.Length != ReturnedCardCount || r.Cards.Distinct().Count() != ReturnedCardCount)
            throw new InvalidOperationException($"The Statue of Liberty reveals {ReturnedCardCount} different cards");
        var offer = CardSet.Empty;
        foreach (var card in r.Cards)
        {
            if (card < 0 || card >= GameData.Cards.Length) throw new InvalidOperationException($"Unknown card {card}");
            if (!UnseenPool(Age).Contains(card))
                throw new InvalidOperationException($"{GameData.Cards[card].Id} cannot be a returned card: not an unseen Age {Age} card");
            offer = offer.With(card);
        }
        _libertyOffer = offer;
        _seen |= offer;
    }

    /// <summary>One draft round's 4 Wonders, drawn from the unseen Wonders of the pool; the round's first pick follows.</summary>
    void ApplyDraftReveal(DraftReveal d)
    {
        if (!AwaitingDraftOffer)
            throw new InvalidOperationException(_tokensSetOut == 0
                ? "The draft comes after the first player and the Progress tokens"
                : "The game is not waiting for a draft offer");
        if (d.Wonders.Length != DraftOfferSize || d.Wonders.Distinct().Count() != DraftOfferSize)
            throw new InvalidOperationException($"A draft offer names {DraftOfferSize} different Wonders");
        ushort mask = 0;
        foreach (var w in d.Wonders)
        {
            if (w < 0 || w >= GameData.Wonders.Length) throw new InvalidOperationException($"Unknown Wonder {w}");
            if ((UnseenWonders & 1 << w) == 0)
                throw new InvalidOperationException((WonderPool & 1 << w) == 0
                    ? $"{GameData.Wonders[w].Id} is not in this game's Wonder pool"
                    : $"{GameData.Wonders[w].Id} was already offered");
            mask |= (ushort)(1 << w);
        }
        _draftOffer = mask;
        _wondersSeen |= mask;
        PushDecision(new PendingDecision(DecisionKind.DraftWonder, DraftPicker(_draftPicks)));
    }

    /// <summary>The Progress tokens set out at setup. The Wonder draft follows, then Age I.</summary>
    void ApplyTokenReveal(TokenReveal t)
    {
        if (!AwaitingTokens)
            throw new InvalidOperationException(FirstPlayer < 0
                ? "The first player must be revealed before the Progress tokens"
                : "The Progress tokens are already set out");
        if (t.Tokens.Length != ProgressTokens.OnBoard || t.Tokens.Distinct().Count() != ProgressTokens.OnBoard)
            throw new InvalidOperationException($"The Progress token Reveal names {ProgressTokens.OnBoard} different tokens");
        ushort mask = 0;
        foreach (var token in t.Tokens)
        {
            if (token < 0 || token >= ProgressTokens.Count) throw new InvalidOperationException($"Unknown Progress token {token}");
            mask |= (ushort)(1 << token);
        }
        _tokensSetOut = _boardTokens = mask;
    }

    void ApplyGuildPattern(GuildPatternReveal g)
    {
        if (!_awaitingGuildPattern) throw new InvalidOperationException("The game is not waiting for the Guild back pattern");
        if (g.Slots.Length != GuildsPerGame || g.Slots.Distinct().Count() != GuildsPerGame)
            throw new InvalidOperationException($"The Guild back pattern names {GuildsPerGame} different slots");
        foreach (var slot in g.Slots)
            if (slot < 0 || slot >= SlotCount) throw new InvalidOperationException($"Slot {slot} is not in the layout");
        foreach (var slot in g.Slots) _slots[slot] = HiddenGuild;
        _awaitingGuildPattern = false;
    }

    void ApplySlotReveal(SlotReveal r)
    {
        if (r.Age != Age) throw new InvalidOperationException($"Reveal is for Age {r.Age}, the game is in Age {Age}");
        if (_awaitingGuildPattern) throw new InvalidOperationException("The Guild back pattern must be revealed first");
        if (r.Opening != AgeUntouched)
            throw new InvalidOperationException(r.Opening
                ? "An Age's opening Reveal must come before any card is taken"
                : "The Age's opening Reveal must name its age");
        if (r.Cards.Length == 0) throw new InvalidOperationException("Reveal names no card");

        var needed = SlotsToReveal;
        var named = CardSet.Empty;
        foreach (var (slot, card) in r.Cards)
        {
            if (slot < 0 || slot >= SlotCount || (needed & 1u << slot) == 0)
                throw new InvalidOperationException($"Slot {SlotName(slot)} is not waiting for a Reveal");
            var guildBack = _slots[slot] == HiddenGuild;
            if (card < 0 || card >= GameData.Cards.Length || !(UnseenPoolFor(slot) & ~named).Contains(card))
                throw new InvalidOperationException(
                    $"{(card >= 0 && card < GameData.Cards.Length ? GameData.Cards[card].Id : "?")} cannot be revealed in {SlotName(slot)}: "
                    + (guildBack ? "not an unseen Guild" : $"not an unseen Age {Age} card"));
            needed &= ~(1u << slot);
            named = named.With(card);
        }
        foreach (var (slot, card) in r.Cards)
        {
            _slots[slot] = (sbyte)card;
            _seen = _seen.With(card);
        }
    }

    string SlotName(int slot) => slot >= 0 && slot < SlotCount && Age > 0 ? Layout.SlotName(slot) : slot.ToString();

    /// <summary>
    /// Lays out an Age's structure face down. Its Reveals (the Guild back pattern in Age III, then the face-up
    /// cards) come before any decision, so in Ages II and III the start-of-Age choice is made seeing the new layout.
    /// </summary>
    void StartAge(int age)
    {
        Age = age;
        Array.Fill(_slots, Hidden);
        _occupied = AllSlots;
        _awaitingGuildPattern = age == AgeCount;
        if (age == 1)
        {
            PushDecision(new PendingDecision(DecisionKind.TakeCard, CurrentPlayer));
            return;
        }

        // The weaker military chooses who starts (rules-reference §9). On a tie, CurrentPlayer (who took the
        // last card) chooses under the newer rule, or simply starts under the 2015 rule.
        if (ConflictPawn != 0)
            PushDecision(new PendingDecision(DecisionKind.ChooseStarter, ConflictPawn > 0 ? 1 : 0));
        else if (Rules.StartChoice == StartChoice.New)
            PushDecision(new PendingDecision(DecisionKind.ChooseStarter, CurrentPlayer));
        else
            PushDecision(new PendingDecision(DecisionKind.TakeCard, CurrentPlayer));
    }

    /// <summary>
    /// Ends the turn: the next Age if the layout is empty (a pending play-again is lost), otherwise the same player
    /// again after a play-again Wonder, or the opponent. The new take-card decision waits behind the Reveals of
    /// newly accessible cards (<see cref="Step"/> asks for those first), so a play-again turn sees them.
    /// </summary>
    void EndTurn()
    {
        if (_occupied == 0)
        {
            EndAge();
            return;
        }
        if (_playAgain) _playAgain = false;
        else CurrentPlayer ^= 1;
        PushDecision(new PendingDecision(DecisionKind.TakeCard, CurrentPlayer));
    }

    void EndAge()
    {
        // "Play again" disappears at the end of an Age (rules-reference §12 mistake 8).
        _playAgain = false;
        if (Age == AgeCount) Result = Scoring.Civilian(this);
        else StartAge(Age + 1);
    }

    void PushDecision(PendingDecision d)
    {
        if (_queueCount == QueueCapacity) throw new InvalidOperationException("Pending Decision queue overflow");
        _queue[_queueCount++] = d;
    }

    void PopDecision()
    {
        Array.Copy(_queue, 1, _queue, 0, _queueCount - 1);
        _queueCount--;
    }

    /// <summary>Adds a card to a city and updates the cached production. Also used by tests to set up positions.</summary>
    internal void AddToCity(int player, int card)
    {
        _city[player] = _city[player].With(card);
        var produce = GameData.Cards[card].Produce;
        for (var r = 0; r < Resources.Count; r++) _production[player * Resources.Count + r] += produce[r];
    }

    /// <summary>Takes a card out of a city (a Zeus / Circus Maximus destroy) and updates the cached production.</summary>
    void RemoveFromCity(int player, int card)
    {
        _city[player] = _city[player].Without(card);
        var produce = GameData.Cards[card].Produce;
        for (var r = 0; r < Resources.Count; r++) _production[player * Resources.Count + r] -= produce[r];
    }

    internal void SetCoins(int player, int coins) => _coins[player] = coins;

    /// <summary>Test setup: puts a card in the discard pile.</summary>
    internal void AddToDiscard(int card) => _discard = _discard.With(card);

    /// <summary>Test setup: puts the pawn on a space without looting.</summary>
    internal void SetConflictPawn(int position) => ConflictPawn = position;

    /// <summary>Test setup: replaces the tokens on the board.</summary>
    internal void SetBoardTokens(ushort mask) => _boardTokens = mask;

    /// <summary>Test setup: gives a player a Wonder, unbuilt, outside the draft.</summary>
    internal void GiveWonder(int player, int wonder) => _wonders[player] |= (ushort)(1 << wonder);

    // ---------------------------------------------------------------- observing

    /// <summary>
    /// What <paramref name="player"/> may see (spec A4, honesty rule): a copy of this state with every Reveal whose
    /// audience excludes that player hidden. In the base game that is only the opponent's pending Great Library draw,
    /// which becomes <see cref="LibraryDrawHidden"/>. Everything else the Game State holds is public, because hidden
    /// identities are never stored (ADR 0002): unknown cards, unused tokens and unseen Wonders appear only as pools.
    /// </summary>
    public Observation Observe(int player)
    {
        if (player is not (0 or 1)) throw new ArgumentOutOfRangeException(nameof(player));
        var copy = Clone();
        if (copy._libraryDraw != 0 && copy._queue[0].Player != player)
        {
            copy._libraryDraw = 0;
            copy._libraryDrawHidden = true;
        }
        return new Observation(copy, player);
    }

    // ---------------------------------------------------------------- copying and comparing

    public GameState Clone()
    {
        var copy = new GameState(Rules);
        copy.CopyFrom(this);
        return copy;
    }

    /// <summary>Overwrites this state with <paramref name="other"/> without allocating.</summary>
    public void CopyFrom(GameState other)
    {
        Rules = other.Rules;
        FirstPlayer = other.FirstPlayer;
        Age = other.Age;
        CurrentPlayer = other.CurrentPlayer;
        Result = other.Result; // immutable, safe to share
        ConflictPawn = other.ConflictPawn;
        other._slots.CopyTo(_slots, 0);
        other._coins.CopyTo(_coins, 0);
        other._city.CopyTo(_city, 0);
        other._production.CopyTo(_production, 0);
        other._queue.CopyTo(_queue, 0);
        _queueCount = other._queueCount;
        _occupied = other._occupied;
        _seen = other._seen;
        _discard = other._discard;
        _awaitingGuildPattern = other._awaitingGuildPattern;
        _militaryTokens = other._militaryTokens;
        _tokensSetOut = other._tokensSetOut;
        _boardTokens = other._boardTokens;
        other._tokens.CopyTo(_tokens, 0);
        _draftOffer = other._draftOffer;
        _wondersSeen = other._wondersSeen;
        _wondersRemoved = other._wondersRemoved;
        _draftPicks = other._draftPicks;
        other._wonders.CopyTo(_wonders, 0);
        other._wondersBuilt.CopyTo(_wondersBuilt, 0);
        other._wonderCard.CopyTo(_wonderCard, 0);
        _playAgain = other._playAgain;
        _libraryDraw = other._libraryDraw;
        _libraryDrawHidden = other._libraryDrawHidden;
        _libertyOffer = other._libertyOffer;
        _libertyKept = other._libertyKept;
        _libertyGiven = other._libertyGiven;
    }

    /// <summary>True if both states are the same game position in every field.</summary>
    public bool SameAs(GameState o) =>
        Rules == o.Rules && FirstPlayer == o.FirstPlayer && Age == o.Age && CurrentPlayer == o.CurrentPlayer
        && Equals(Result, o.Result) && ConflictPawn == o.ConflictPawn && _awaitingGuildPattern == o._awaitingGuildPattern
        && _militaryTokens == o._militaryTokens
        && _tokensSetOut == o._tokensSetOut && _boardTokens == o._boardTokens && _tokens.AsSpan().SequenceEqual(o._tokens)
        && _occupied == o._occupied && _seen == o._seen && _discard == o._discard
        && _draftOffer == o._draftOffer && _wondersSeen == o._wondersSeen && _wondersRemoved == o._wondersRemoved
        && _draftPicks == o._draftPicks && _playAgain == o._playAgain
        && _libraryDraw == o._libraryDraw && _libraryDrawHidden == o._libraryDrawHidden
        && _libertyOffer == o._libertyOffer && _libertyKept == o._libertyKept && _libertyGiven == o._libertyGiven
        && _wonders.AsSpan().SequenceEqual(o._wonders) && _wondersBuilt.AsSpan().SequenceEqual(o._wondersBuilt)
        && _wonderCard.AsSpan().SequenceEqual(o._wonderCard)
        && _queueCount == o._queueCount
        && _slots.AsSpan().SequenceEqual(o._slots) && _coins.AsSpan().SequenceEqual(o._coins)
        && _city.AsSpan().SequenceEqual(o._city) && _production.AsSpan().SequenceEqual(o._production)
        && _queue.AsSpan(0, _queueCount).SequenceEqual(o._queue.AsSpan(0, o._queueCount));

    /// <summary>
    /// Every field <see cref="SameAs"/> compares, as canonical little-endian bytes: equal states give equal bytes on
    /// any OS and runtime. The determinism test pins a hash of these (spec A11, determinism).
    /// </summary>
    public byte[] ToBytes()
    {
        using var stream = new MemoryStream();
        using (var w = new BinaryWriter(stream))
        {
            static void Set(BinaryWriter w, CardSet c)
            {
                w.Write((ulong)c.Bits);
                w.Write((ulong)(c.Bits >> 64));
            }
            w.Write((byte)Rules.StartChoice);
            w.Write((byte)Rules.Promos);
            w.Write(FirstPlayer);
            w.Write(Age);
            w.Write(CurrentPlayer);
            w.Write(ConflictPawn);
            w.Write(Result is not null);
            if (Result is { } r)
            {
                w.Write(r.Winner);
                w.Write((byte)r.Victory);
                foreach (var sheet in new[] { r.P1, r.P2 })
                    for (var c = 0; c < ScoreSheet.CategoryCount; c++) w.Write(sheet[(ScoreCategory)c]);
            }
            foreach (var slot in _slots) w.Write(slot);
            foreach (var coins in _coins) w.Write(coins);
            foreach (var city in _city) Set(w, city);
            foreach (var p in _production) w.Write(p);
            w.Write(_queueCount);
            for (var i = 0; i < _queueCount; i++)
            {
                w.Write((byte)_queue[i].Kind);
                w.Write(_queue[i].Player);
                w.Write(_queue[i].Wonder);
            }
            w.Write(_occupied);
            Set(w, _seen);
            Set(w, _discard);
            w.Write(_awaitingGuildPattern);
            w.Write(_militaryTokens);
            w.Write(_tokensSetOut);
            w.Write(_boardTokens);
            foreach (var t in _tokens) w.Write(t);
            w.Write(_draftOffer);
            w.Write(_wondersSeen);
            w.Write(_wondersRemoved);
            w.Write(_draftPicks);
            foreach (var m in _wonders) w.Write(m);
            foreach (var m in _wondersBuilt) w.Write(m);
            foreach (var c in _wonderCard) w.Write(c);
            w.Write(_playAgain);
            w.Write(_libraryDraw);
            w.Write(_libraryDrawHidden);
            // Only Statue of Liberty games carry its fields, so the base game's pinned bytes are unchanged.
            if (Rules.HasPromo(Promos.Liberty))
            {
                Set(w, _libertyOffer);
                w.Write(_libertyKept);
                w.Write(_libertyGiven);
            }
        }
        return stream.ToArray();
    }

    public override string ToString() => StatePrinter.Describe(this);
}
