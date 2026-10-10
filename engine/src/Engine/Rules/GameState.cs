using System.Runtime.CompilerServices;
using SevenWondersDuel.Engine.Data;

[assembly: InternalsVisibleTo("Engine.Tests")]

namespace SevenWondersDuel.Engine;

/// <summary>
/// The complete, canonical game at one moment (spec A4). Mutable: <see cref="Apply(Move)"/> and
/// <see cref="Apply(Reveal)"/> change it in place, and search copies it first with <see cref="Clone"/> or
/// <see cref="CopyFrom"/>. Hidden identities are never stored: a face-down slot is just <see cref="Hidden"/>
/// until a Reveal names it (ADR 0002).
/// </summary>
public sealed class GameState
{
    /// <summary>Slot value of a taken (empty) slot.</summary>
    public const sbyte Empty = -1;
    /// <summary>Slot value of a card whose identity is not known yet (face down, or face up but not yet revealed).</summary>
    public const sbyte Hidden = -2;

    /// <summary>The last Age the Engine plays. Ticket 04 adds Ages II and III.</summary>
    public const int LastAge = 1;
    public const int StartingCoins = 7;
    public const int SlotCount = 20;
    /// <summary>Upper bound on legal Moves in any position, for caller-supplied buffers.</summary>
    public const int MaxMoves = 128;
    const int QueueCapacity = 8;
    const uint AllSlots = (1u << SlotCount) - 1;

    /// <summary>Per Age (index Age - 1), per slot: the slots that cover it, as a bitmask.</summary>
    static readonly uint[][] CoveredByMask = GameData.Layouts
        .Select(l => l.Slots.Select(s => s.CoveredBy.Aggregate(0u, (m, c) => m | 1u << c)).ToArray())
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

    GameState(Ruleset rules) => Rules = rules;

    /// <summary>A new game before any Reveal: the first Reveal names the first player.</summary>
    public static GameState New(Ruleset rules)
    {
        var s = new GameState(rules) { FirstPlayer = -1 };
        s._coins[0] = s._coins[1] = StartingCoins;
        Array.Fill(s._slots, Empty);
        return s;
    }

    public Ruleset Rules { get; private set; }
    /// <summary>The player who plays first, or -1 until revealed.</summary>
    public int FirstPlayer { get; private set; }
    /// <summary>The current Age, or 0 before Age I starts.</summary>
    public int Age { get; private set; }
    /// <summary>The player whose turn it is.</summary>
    public int CurrentPlayer { get; private set; }
    public bool IsOver { get; private set; }

    public AgeLayout Layout => GameData.Layout(Age);
    public int Coins(int player) => _coins[player];
    public CardSet City(int player) => _city[player];
    public CardSet DiscardPile => _discard;
    /// <summary>Every card whose identity has been revealed so far.</summary>
    public CardSet Seen => _seen;
    public int Production(int player, Resource r) => _production[player * Resources.Count + (int)r];
    public int YellowCards(int player) => (_city[player] & CardSets.OfColor[(int)CardColor.Yellow]).Count;

    /// <summary>The card in a slot, <see cref="Empty"/> or <see cref="Hidden"/>.</summary>
    public int SlotCard(int slot) => _slots[slot];
    public bool IsOccupied(int slot) => (_occupied & 1u << slot) != 0;
    public bool IsAccessible(int slot) => IsOccupied(slot) && (_occupied & CoveredByMask[Age - 1][slot]) == 0;
    public int CardsLeftInAge => System.Numerics.BitOperations.PopCount(_occupied);
    /// <summary>True until the first card of the current Age is taken.</summary>
    public bool AgeUntouched => Age > 0 && _occupied == AllSlots;

    /// <summary>Cards of an Age that could still be revealed (never named so far).</summary>
    public CardSet UnseenPool(int age) => CardSets.OfAge[age] & ~_seen;

    /// <summary>Slots that should show their card (face up, or accessible) but whose identity is not yet known.</summary>
    public uint SlotsToReveal
    {
        get
        {
            if (Age == 0) return 0;
            uint mask = 0;
            for (var s = 0; s < SlotCount; s++)
                if (_slots[s] == Hidden && ((FaceUpMask[Age - 1] & 1u << s) != 0 || IsAccessible(s)))
                    mask |= 1u << s;
            return mask;
        }
    }

    /// <summary>What the game needs next: a Reveal from the Chance Source, a Move, or nothing.</summary>
    public StepKind Step => IsOver ? StepKind.GameOver
        : FirstPlayer < 0 || SlotsToReveal != 0 ? StepKind.Reveal
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
        for (var s = 0; s < SlotCount; s++)
        {
            if (!IsAccessible(s)) continue;
            var card = _slots[s];
            if (CanAffordBuild(player, card)) buffer[n++] = Move.Build(card);
            buffer[n++] = Move.Discard(card);
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
        if (move.Card < 0 || move.Card >= GameData.Cards.Length) return "unknown card";
        var slot = SlotOf(move.Card);
        if (slot < 0) return $"{GameData.Cards[move.Card].Id} is not in the layout";
        if (!IsAccessible(slot)) return $"{GameData.Cards[move.Card].Id} is covered";
        return move.Kind switch
        {
            MoveKind.Discard => null,
            MoveKind.Build => CanAffordBuild(_queue[0].Player, move.Card) ? null
                : $"{GameData.Cards[move.Card].Id} costs {Payment.Quote(this, _queue[0].Player, move.Card).Total} coins, player has {_coins[_queue[0].Player]}",
            _ => "unknown move kind",
        };
    }

    bool CanAffordBuild(int player, int card) => Payment.Quote(this, player, card).Total <= _coins[player];

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
        var slot = SlotOf(move.Card);
        _slots[slot] = Empty;
        _occupied &= ~(1u << slot);

        if (move.Kind == MoveKind.Build)
        {
            _coins[player] -= Payment.Quote(this, player, move.Card).Total;
            AddToCity(player, move.Card);
            _coins[player] += GameData.Cards[move.Card].Coins;
        }
        else
        {
            // Discard value counts yellow cards already in the city (rules-reference §11 mistake 5).
            _coins[player] += 2 + YellowCards(player);
            _discard = _discard.With(move.Card);
        }

        PopDecision();
        if (_queueCount == 0) EndTurn();
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
                StartAge(1);
                break;

            case SlotReveal r:
                ApplySlotReveal(r);
                break;

            default:
                throw new InvalidOperationException($"Unsupported Reveal {reveal.GetType().Name}");
        }
    }

    void ApplySlotReveal(SlotReveal r)
    {
        if (r.Age != Age) throw new InvalidOperationException($"Reveal is for Age {r.Age}, the game is in Age {Age}");
        if (r.Opening != AgeUntouched)
            throw new InvalidOperationException(r.Opening
                ? "An Age's opening Reveal must come before any card is taken"
                : "The Age's opening Reveal must name its age");
        if (r.Cards.Length == 0) throw new InvalidOperationException("Reveal names no card");

        var needed = SlotsToReveal;
        var pool = UnseenPool(Age);
        foreach (var (slot, card) in r.Cards)
        {
            if (slot < 0 || slot >= SlotCount || (needed & 1u << slot) == 0)
                throw new InvalidOperationException($"Slot {SlotName(slot)} is not waiting for a Reveal");
            if (card < 0 || card >= GameData.Cards.Length || !pool.Contains(card))
                throw new InvalidOperationException(
                    $"{(card >= 0 && card < GameData.Cards.Length ? GameData.Cards[card].Id : "?")} cannot be revealed: not an unseen Age {Age} card");
            needed &= ~(1u << slot);
            pool = pool.Without(card);
        }
        foreach (var (slot, card) in r.Cards)
        {
            _slots[slot] = (sbyte)card;
            _seen = _seen.With(card);
        }
    }

    string SlotName(int slot) => slot >= 0 && slot < SlotCount && Age > 0 ? Layout.SlotName(slot) : slot.ToString();

    void StartAge(int age)
    {
        Age = age;
        Array.Fill(_slots, Hidden);
        _occupied = AllSlots;
        PushDecision(new PendingDecision(DecisionKind.TakeCard, CurrentPlayer));
    }

    void EndTurn()
    {
        if (_occupied == 0)
        {
            EndAge();
            return;
        }
        CurrentPlayer ^= 1;
        PushDecision(new PendingDecision(DecisionKind.TakeCard, CurrentPlayer));
    }

    void EndAge()
    {
        if (Age == LastAge)
        {
            IsOver = true;
            return;
        }
        throw new NotImplementedException("Ages II and III arrive in ticket 04");
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

    internal void SetCoins(int player, int coins) => _coins[player] = coins;

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
        IsOver = other.IsOver;
        other._slots.CopyTo(_slots, 0);
        other._coins.CopyTo(_coins, 0);
        other._city.CopyTo(_city, 0);
        other._production.CopyTo(_production, 0);
        other._queue.CopyTo(_queue, 0);
        _queueCount = other._queueCount;
        _occupied = other._occupied;
        _seen = other._seen;
        _discard = other._discard;
    }

    /// <summary>True if both states are the same game position in every field.</summary>
    public bool SameAs(GameState o) =>
        Rules == o.Rules && FirstPlayer == o.FirstPlayer && Age == o.Age && CurrentPlayer == o.CurrentPlayer
        && IsOver == o.IsOver && _occupied == o._occupied && _seen == o._seen && _discard == o._discard
        && _queueCount == o._queueCount
        && _slots.AsSpan().SequenceEqual(o._slots) && _coins.AsSpan().SequenceEqual(o._coins)
        && _city.AsSpan().SequenceEqual(o._city) && _production.AsSpan().SequenceEqual(o._production)
        && _queue.AsSpan(0, _queueCount).SequenceEqual(o._queue.AsSpan(0, o._queueCount));

    public override string ToString() => StatePrinter.Describe(this);
}
