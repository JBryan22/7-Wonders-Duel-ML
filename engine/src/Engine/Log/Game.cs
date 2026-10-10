namespace SevenWondersDuel.Engine;

/// <summary>A Game State together with the Move Log that produced it. Every entry is checked before it is logged.</summary>
public sealed class Game
{
    readonly List<LogEntry> _log = [];

    public Game(Ruleset rules) => State = GameState.New(rules);

    public GameState State { get; }
    public Ruleset Rules => State.Rules;
    public IReadOnlyList<LogEntry> Log => _log;

    public void Apply(LogEntry entry)
    {
        switch (entry)
        {
            case MoveEntry m:
                if (State.Step == StepKind.Decision && m.Player != State.PlayerToDecide)
                    throw new InvalidOperationException($"It is P{State.PlayerToDecide + 1}'s decision, not P{m.Player + 1}'s");
                State.Apply(m.Move);
                break;
            case Reveal r:
                State.Apply(r);
                break;
            default:
                throw new ArgumentException($"Unknown log entry {entry.GetType().Name}", nameof(entry));
        }
        _log.Add(entry);
    }

    /// <summary>Applies a Move for the player to decide.</summary>
    public void Apply(Move move) => Apply(new MoveEntry(State.PlayerToDecide, move));

    /// <summary>The Move Log as text.</summary>
    public string WriteLog() => MoveLog.Write(Rules, _log);
}
