namespace SevenWondersDuel.Engine;

public enum MoveKind : byte { Build, Discard }

/// <summary>
/// One decision that resolves the front Pending Decision. Payment is never part of a Move:
/// the Engine always pays the cheapest way (spec A4).
/// </summary>
public readonly record struct Move(MoveKind Kind, int Card)
{
    public static Move Build(int card) => new(MoveKind.Build, card);
    public static Move Discard(int card) => new(MoveKind.Discard, card);

    public override string ToString() => MoveLog.FormatMove(this);
}

public enum DecisionKind : byte { TakeCard }

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
/// Layout cards becoming known: the face-up cards of a new Age (<see cref="Opening"/>, "reveal age1 0.0 a 0.1 b ...")
/// or a face-down card flipped once it becomes accessible ("reveal 3.1 baths").
/// </summary>
public sealed record SlotReveal(int Age, bool Opening, (int Slot, int Card)[] Cards) : Reveal
{
    public override string ToString() => MoveLog.Format(this);
}
