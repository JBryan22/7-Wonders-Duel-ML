namespace SevenWondersDuel.Engine;

public enum MoveKind : byte { Build, Discard, StartMe, StartOpponent, Token, Draft, Wonder }

/// <summary>
/// One decision that resolves the front Pending Decision. Payment is never part of a Move:
/// the Engine always pays the cheapest way (spec A4). <see cref="Card"/> is -1 for Moves that name no card;
/// for a <see cref="MoveKind.Token"/> Move it holds the Progress token index instead (see <see cref="Token"/>).
/// <see cref="Wonder"/> is the Wonder a <see cref="MoveKind.Draft"/> Move drafts or a <see cref="MoveKind.Wonder"/>
/// Move builds (the tucked Age card is then in <see cref="Card"/>); -1 for every other Move.
/// </summary>
public readonly record struct Move(MoveKind Kind, int Card, int Wonder = -1)
{
    public static Move Build(int card) => new(MoveKind.Build, card);
    public static Move Discard(int card) => new(MoveKind.Discard, card);
    /// <summary>"wonder baths sphinx": take the Age card and build the Wonder with it, the card tucked under it.</summary>
    public static Move BuildWonder(int card, int wonder) => new(MoveKind.Wonder, card, wonder);
    /// <summary>"draft sphinx": take a Wonder from the draft offer.</summary>
    public static Move Draft(int wonder) => new(MoveKind.Draft, -1, wonder);
    /// <summary>"start me": the deciding player starts the new Age.</summary>
    public static readonly Move StartMe = new(MoveKind.StartMe, -1);
    /// <summary>"start opponent": the deciding player's opponent starts the new Age.</summary>
    public static readonly Move StartOpponent = new(MoveKind.StartOpponent, -1);
    /// <summary>"token law": take a Progress token from the board after a science pair.</summary>
    public static Move PickToken(int token) => new(MoveKind.Token, token);

    /// <summary>The Progress token a <see cref="MoveKind.Token"/> Move takes, or -1.</summary>
    public int Token => Kind == MoveKind.Token ? Card : -1;

    /// <summary>True for the Moves that answer a take-card decision: build, discard, or build a Wonder.</summary>
    public bool TakesCard => Kind is MoveKind.Build or MoveKind.Discard or MoveKind.Wonder;

    public override string ToString() => MoveLog.FormatMove(this);
}

public enum DecisionKind : byte
{
    /// <summary>Take an accessible card: build it, discard it, or build a Wonder with it.</summary>
    TakeCard,
    /// <summary>Choose who starts the new Age: the weaker military, or on a tie per <see cref="Ruleset.StartChoice"/>.</summary>
    ChooseStarter,
    /// <summary>Take a Progress token from the board after a science pair: compulsory while any remain (spec A3).</summary>
    ChooseToken,
    /// <summary>Take a Wonder from the draft offer (picks A-B-B-A, rules-reference §1). The last Wonder is still a Move.</summary>
    DraftWonder,
}

/// <summary>A choice the Game State is waiting on, owned by the player who must make it.</summary>
public readonly record struct PendingDecision(DecisionKind Kind, int Player);

/// <summary>What the Game State needs next.</summary>
public enum StepKind : byte { Reveal, Decision, GameOver }

/// <summary>One line of the Move Log: a Move or a Reveal.</summary>
public abstract record LogEntry
{
    public override string ToString() => MoveLog.Format(this);
}

/// <summary>A Move made by a player ("P1 build lumber-yard").</summary>
public sealed record MoveEntry(int Player, Move Move) : LogEntry
{
    public override string ToString() => MoveLog.Format(this);
}

/// <summary>A chance outcome at the moment it becomes known (ADR 0002). <see cref="Audience"/> is -1 for both players.</summary>
public abstract record Reveal : LogEntry
{
    public virtual int Audience => -1;
}

/// <summary>Who plays first ("reveal first P1").</summary>
public sealed record FirstPlayerReveal(int Player) : Reveal
{
    public override string ToString() => MoveLog.Format(this);
}

/// <summary>
/// The 5 Progress tokens set out face up on the board at setup ("reveal tokens agriculture law ..."). Comes right
/// after the first player; the other 5 stay in the box unnamed (the Great Library draws from them).
/// </summary>
public sealed record TokenReveal(int[] Tokens) : Reveal
{
    public override string ToString() => MoveLog.Format(this);
}

/// <summary>
/// The 4 Wonders offered in one draft round ("reveal draft pyramids colossus sphinx appian-way"), drawn from the
/// Wonders of the pool nobody has seen yet. Round 1 comes after the Progress tokens, round 2 after the 4th pick.
/// </summary>
public sealed record DraftReveal(int[] Wonders) : Reveal
{
    public override string ToString() => MoveLog.Format(this);
}

/// <summary>
/// Which Age III slots hold a Guild ("reveal guilds 0.1 3.0 5.2"). Guild backs are purple, so both players
/// see where the 3 Guilds lie before any card is named; the opening Reveal then names the face-up cards.
/// </summary>
public sealed record GuildPatternReveal(int[] Slots) : Reveal
{
    public override string ToString() => MoveLog.Format(this);
}

/// <summary>
/// Layout cards becoming known: the face-up cards of a new Age (<see cref="Opening"/>, "reveal age1 0.0 a 0.1 b ...")
/// or a face-down card flipped once it becomes accessible ("reveal 3.1 baths").
/// </summary>
public sealed record SlotReveal(int Age, bool Opening, (int Slot, int Card)[] Cards) : Reveal
{
    public override string ToString() => MoveLog.Format(this);
}
