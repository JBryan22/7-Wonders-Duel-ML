using SevenWondersDuel.Agents;
using SevenWondersDuel.Engine;
using SevenWondersDuel.Engine.Data;

namespace SevenWondersDuel.Advisor;

/// <summary>
/// Which search gives advice and how hard it thinks (spec A8): <see cref="Agent"/> is an Agent spec, <c>mcts</c> (the
/// Anchor, the default until a trained network exists) or <c>neural:&lt;onnx&gt;[,...]</c>; <see cref="Simulations"/>
/// is the budget of a normal request (800); the Think longer button runs 4× that (3,200). The budget overrides any
/// <c>sims=</c> in the spec.
/// </summary>
public sealed record AdviceSettings(string Agent = AdviceSettings.DefaultAgent, int Simulations = AdviceSettings.DefaultSimulations)
{
    public const string DefaultAgent = "mcts";
    public const int DefaultSimulations = 800;
    /// <summary>The local default when this computer needs more than <see cref="SlowSeconds"/> at 800 (spec A8).</summary>
    public const int ReducedSimulations = 400;
    public const double SlowSeconds = 3;

    /// <summary>Throws a <see cref="FormatException"/> unless <see cref="Agent"/> names a search that can advise.</summary>
    public void Validate()
    {
        if (Simulations < 1) throw new FormatException("The advice budget is at least 1 simulation");
        var (name, _) = Split(Agent);
        if (name is not ("mcts" or "neural"))
            throw new FormatException($"Advice needs a search Agent: 'mcts' or 'neural:<onnx file>', not '{Agent}'");
        AdviceSearch.CreateAgent(Agent, 1, 0);
    }

    internal static (string Name, string? Argument) Split(string spec)
    {
        var colon = spec.IndexOf(':');
        return colon < 0 ? (spec.Trim().ToLowerInvariant(), null) : (spec[..colon].Trim().ToLowerInvariant(), spec[(colon + 1)..]);
    }
}

/// <summary>
/// One option as the search saw it: its share of the search, the owner's win chance (draw ½) and its standard error.
/// <see cref="Then"/> is the second Move of a Statue of Liberty keep/give pair (spec A8: the pair's share is the
/// product of its two steps). <see cref="Line"/> is the search's expected line starting with the option's Moves
/// (shorter than the option itself when the search was too shallow); <see cref="Outcome"/> the outcome-class
/// distribution in <see cref="NetworkEncoding.OutcomeClasses"/> order (null without an outcome head, e.g. MCTS).
/// </summary>
public sealed record MoveAdvice(Move Move, int Visits, double Share, double Win, double StdErr)
{
    public Move? Then { get; init; }
    public IReadOnlyList<LineStep> Line { get; init; } = [];
    public double[]? Outcome { get; init; }
}

/// <summary>What one advice search returned, before it is ranked into the compare table. <see cref="BestThen"/> completes a Statue pair.</summary>
public sealed record SearchOutcome(Move Best, IReadOnlyList<MoveAdvice> Moves, IReadOnlyList<LineStep> Line, int Simulations)
{
    public Move? BestThen { get; init; }
    /// <summary>True when the search's network has an outcome head (the compare table then shows the outcome bar).</summary>
    public bool HasOutcome { get; init; }
}

/// <summary>
/// Runs one advice search from the owner's Observation (never the full state: advice must not peek at hidden
/// information, spec A1), with either search Agent.
/// </summary>
public static class AdviceSearch
{
    /// <summary>A search Agent with <paramref name="simulations"/> per search.</summary>
    public static IAgent CreateAgent(string spec, int simulations, ulong seed)
    {
        var (name, argument) = AdviceSettings.Split(spec);
        return name switch
        {
            "mcts" => new MctsAgent(MctsSettings.Parse(argument) with { Simulations = simulations }, seed),
            "neural" => NeuralAgent.Create(NeuralSettings.Parse(argument) with { Simulations = simulations }, seed),
            _ => throw new FormatException($"Advice needs a search Agent: 'mcts' or 'neural:<onnx file>', not '{spec}'"),
        };
    }

    /// <summary>
    /// Searches the decision at the front of <paramref name="observation"/>'s queue. Shares are the search's visit
    /// shares, or for a Gumbel network search its improved policy (which ranks the Moves better than Sequential
    /// Halving's visit counts). Each Move also gets its expected line and, from a network, its outcome distribution.
    /// A Statue of Liberty keep is advised as its keep/give pairs.
    /// </summary>
    public static SearchOutcome Run(Observation observation, string spec, int simulations, ulong seed, CancellationToken cancel = default)
    {
        MoveAdvice[] moves;
        Move best;
        IReadOnlyList<LineStep> line;
        Func<IReadOnlyList<Move>, IReadOnlyList<LineStep>> lineOf;
        Func<IReadOnlyList<Move>, IReadOnlyList<TreeChild>> childrenOf;
        Func<IReadOnlyList<Move>, double[]?> outcomeOf = _ => null;
        var hasOutcome = false;
        switch (CreateAgent(spec, simulations, seed))
        {
            case MctsAgent mcts:
            {
                var r = mcts.Search(observation, cancel);
                moves = [.. r.Moves.Select(m => new MoveAdvice(m.Move, m.Visits, m.VisitShare, m.Value, m.StdErr))];
                (best, line, lineOf, childrenOf) = (r.Best, r.Line, mcts.LineOf, mcts.ChildrenOf);
                simulations = r.Simulations;
                break;
            }
            case NeuralAgent neural:
            {
                neural.TrackOutcomes = true;
                var r = neural.Search(observation, cancel);
                var gumbel = neural.Settings.Mode == NeuralMode.Gumbel;
                moves = [.. r.Moves.Select((m, i) => new MoveAdvice(m.Move, m.Visits, gumbel ? r.PolicyTarget[i] : m.VisitShare, m.Value, m.StdErr))];
                (best, line, lineOf, childrenOf, outcomeOf) = (r.Best, r.Line, neural.LineOf, neural.ChildrenOf, neural.OutcomeOf);
                hasOutcome = neural.HasOutcomeHead;
                simulations = r.Simulations;
                break;
            }
            default:
                throw new InvalidOperationException("Not a search Agent");
        }

        var state = observation.State;
        if (state.Pending(0).Kind == DecisionKind.LibertyKeep)
            return Pairs(state, moves, best, line, simulations, lineOf, childrenOf, outcomeOf) with { HasOutcome = hasOutcome };
        moves = [.. moves.Select(m => m with { Line = lineOf([m.Move]), Outcome = outcomeOf([m.Move]) })];
        return new SearchOutcome(best, moves, line, simulations) { HasOutcome = hasOutcome };
    }

    /// <summary>
    /// The Statue of Liberty as keep/give pairs (spec A8): each kept card with each card that can then be given. A
    /// pair's share is the keep's share times the give's share of the visits under that keep; its win chance is the
    /// give's (the Statue's owner decides both). The best pair is the search's keep with its most visited give.
    /// </summary>
    static SearchOutcome Pairs(GameState state, MoveAdvice[] keeps, Move bestKeep, IReadOnlyList<LineStep> line, int simulations,
        Func<IReadOnlyList<Move>, IReadOnlyList<LineStep>> lineOf, Func<IReadOnlyList<Move>, IReadOnlyList<TreeChild>> childrenOf,
        Func<IReadOnlyList<Move>, double[]?> outcomeOf)
    {
        var pairs = new List<MoveAdvice>();
        Move? bestGive = null;
        foreach (var keep in keeps)
        {
            var after = state.Clone();
            after.Apply(keep.Move);
            var gives = after.LegalMoves();
            var children = childrenOf([keep.Move]);
            var total = children.Sum(c => c.Visits);
            var mostVisited = -1;
            foreach (var give in gives)
            {
                var child = children.FirstOrDefault(c => c.Move == give);
                var share = total == 0 ? 1.0 / gives.Count : (double)child.Visits / total;
                var visited = child.Visits > 0;
                pairs.Add(new MoveAdvice(keep.Move, child.Visits, keep.Share * share, visited ? child.Value : keep.Win,
                    visited ? child.StdErr : keep.StdErr)
                {
                    Then = give,
                    Line = lineOf([keep.Move, give]),
                    Outcome = outcomeOf([keep.Move, give]),
                });
                if (keep.Move == bestKeep && child.Visits > mostVisited) (mostVisited, bestGive) = (child.Visits, give);
            }
        }
        bestGive ??= pairs.First(p => p.Move == bestKeep).Then;
        return new SearchOutcome(bestKeep, pairs, line, simulations) { BestThen = bestGive };
    }
}

/// <summary>One step of an expected line, ready to show: who plays it, in the owner's words, and its pyramid slot (for the board's numbered markers).</summary>
public sealed record ExpectedStep(int Player, bool You, string Label, int? Slot, bool AfterReveal, bool Own, int Visits, double Win);

/// <summary>
/// One row of the compare table: the Move (and for a Statue pair, <see cref="Then"/>), the log line(s) that enter it,
/// the owner's win chance with its standard error, and the gap to the best Move. <see cref="Same"/> ("≈ same") when
/// the gap is inside the combined noise 2·√(se₁² + se₂²) (spec A8). <see cref="Reasons"/> are its reason tags,
/// <see cref="Expected"/> its expected line (the row's own Moves first, marked <see cref="ExpectedStep.Own"/>),
/// <see cref="Outcome"/> its outcome distribution (null without an outcome head).
/// </summary>
public sealed record AdviceRow(Move Move, string Line, string Label, int? Slot, int Visits, double Share, double Win, double StdErr,
    double Gap, double Noise, bool Same, bool Best)
{
    public Move? Then { get; init; }
    public string? ThenLine { get; init; }
    public IReadOnlyList<string> Reasons { get; init; } = [];
    public IReadOnlyList<ExpectedStep> Expected { get; init; } = [];
    public double[]? Outcome { get; init; }

    /// <summary>This option's key in the page's share map: its log line, and for a Statue pair both lines.</summary>
    public string Key => ThenLine is null ? Line : $"{Line}\n{ThenLine}";

    /// <summary>True when the search saw beyond the row's own Moves; otherwise the page shows "no line".</summary>
    public bool HasLine => Expected.Any(s => !s.Own);
}

/// <summary>Ranked advice for one position: every option, best first.</summary>
public sealed record Advice(string Key, string Agent, int Simulations, double Seconds, IReadOnlyList<AdviceRow> Rows,
    IReadOnlyList<LineStep> Line)
{
    public AdviceRow Best => Rows[0];

    /// <summary>True when the rows carry an outcome distribution (a network with an outcome head).</summary>
    public bool HasOutcome { get; init; }
}

/// <summary>Turns a <see cref="SearchOutcome"/> into the compare table's rows.</summary>
public static class AdviceTable
{
    /// <summary>The compare table shows at most this many Moves (spec A8).</summary>
    public const int MaxRows = 5;

    /// <summary>The combined noise of two win estimates: 2·√(se₁² + se₂²), about a 95% band for their difference.</summary>
    public static double Noise(double stdErrA, double stdErrB) => 2 * Math.Sqrt(stdErrA * stdErrA + stdErrB * stdErrB);

    /// <summary>True when two Moves' win chances differ by no more than their combined noise ("≈ same").</summary>
    public static bool WithinNoise(double winA, double stdErrA, double winB, double stdErrB) =>
        Math.Abs(winA - winB) <= Noise(stdErrA, stdErrB);

    /// <summary>
    /// All options, the search's choice first, then by share of the search (ties: higher win chance), each with its
    /// reason tags and expected line.
    /// </summary>
    public static AdviceRow[] Rank(GameState state, SearchOutcome outcome)
    {
        var player = state.PlayerToDecide;
        var best = outcome.Moves.First(m => m.Move == outcome.Best && m.Then == outcome.BestThen);
        return outcome.Moves
            .OrderByDescending(m => ReferenceEquals(m, best))
            .ThenByDescending(m => m.Share)
            .ThenByDescending(m => m.Win)
            .Select(m =>
            {
                var isBest = ReferenceEquals(m, best);
                var noise = Noise(best.StdErr, m.StdErr);
                var gap = isBest ? 0 : best.Win - m.Win;
                var label = m.Then is { } then ? $"Keep {GameData.Cards[m.Move.Card].Name}, give {GameData.Cards[then.Card].Name}" : Label(state, m.Move);
                return new AdviceRow(m.Move, $"P{player + 1} {m.Move}", label, m.Then is null ? SlotOf(state, m.Move) : null, m.Visits, m.Share,
                    m.Win, m.StdErr, gap, isBest ? 0 : noise, isBest || Math.Abs(gap) <= noise, isBest)
                {
                    Then = m.Then,
                    ThenLine = m.Then is { } t ? $"P{player + 1} {t}" : null,
                    Reasons = Reasons.For(state, m.Move, m.Then),
                    Expected = Expected(state, m),
                    Outcome = m.Outcome,
                };
            })
            .ToArray();
    }

    /// <summary>The option's expected line in the owner's words; a card not face up now is "a newly revealed card".</summary>
    static ExpectedStep[] Expected(GameState state, MoveAdvice m)
    {
        var own = m.Then is null ? 1 : 2;
        return [.. m.Line.Select((step, i) =>
        {
            var slot = SlotOf(state, step.Move);
            var label = step.Move.TakesCard && slot is null ? Unseen(step.Move)
                : step.Player != state.PlayerToDecide && step.Move.Kind == MoveKind.StartMe ? "They start the Age"
                : step.Player != state.PlayerToDecide && step.Move.Kind == MoveKind.StartOpponent ? "They let you start the Age"
                : Label(state, step.Move);
            return new ExpectedStep(step.Player, step.Player == state.PlayerToDecide, label, slot, step.AfterReveal, i < own,
                step.Visits, step.Player == state.PlayerToDecide ? step.Value : 1 - step.Value);
        })];
    }

    static string Unseen(Move m) => m.Kind switch
    {
        MoveKind.Build => "Build a newly revealed card",
        MoveKind.Discard => "Discard a newly revealed card",
        _ => $"Wonder {GameData.Wonders[m.Wonder].Name} (with a newly revealed card)",
    };

    /// <summary>The pyramid slot of the card a take-card Move takes (for the % badges), or null.</summary>
    public static int? SlotOf(GameState s, Move m)
    {
        if (!m.TakesCard) return null;
        for (var slot = 0; slot < GameState.SlotCount; slot++)
            if (s.SlotCard(slot) == m.Card) return slot;
        return null;
    }

    /// <summary>The Move in the owner's words: "Build Baths", "Wonder The Pyramids (with Baths)", "Token Law", ...</summary>
    public static string Label(GameState s, Move m)
    {
        string Card(int c) => GameData.Cards[c].Name;
        string Wonder(int w) => GameData.Wonders[w].Name;
        string Token(int t) => GameData.Tokens[t].Name;
        return m.Kind switch
        {
            MoveKind.Build => $"Build {Card(m.Card)}",
            MoveKind.Discard => $"Discard {Card(m.Card)}",
            MoveKind.Wonder => $"Wonder {Wonder(m.Wonder)} (with {Card(m.Card)})",
            MoveKind.Draft => $"Draft {Wonder(m.Wonder)}",
            MoveKind.StartMe => "Start the Age yourself",
            MoveKind.StartOpponent => "Let the opponent start",
            MoveKind.Token => $"Token {Token(m.Token)}",
            MoveKind.Library => $"Keep {Token(m.Token)}",
            MoveKind.Destroy => $"Destroy {Card(m.Card)}",
            MoveKind.Mausoleum => $"Mausoleum: {Card(m.Card)}",
            MoveKind.Messe => $"Messe: {Card(m.Card)}",
            MoveKind.LibertyKeep => $"Keep {Card(m.Card)}",
            MoveKind.LibertyGive => $"Give {Card(m.Card)}",
            _ => m.ToString(),
        };
    }
}
