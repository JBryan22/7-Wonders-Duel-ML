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

/// <summary>One legal Move as the search saw it: its share of the search, the owner's win chance (draw ½) and its standard error.</summary>
public sealed record MoveAdvice(Move Move, int Visits, double Share, double Win, double StdErr);

/// <summary>What one advice search returned, before it is ranked into the compare table.</summary>
public sealed record SearchOutcome(Move Best, IReadOnlyList<MoveAdvice> Moves, IReadOnlyList<LineStep> Line, int Simulations);

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
    /// Halving's visit counts).
    /// </summary>
    public static SearchOutcome Run(Observation observation, string spec, int simulations, ulong seed, CancellationToken cancel = default)
    {
        switch (CreateAgent(spec, simulations, seed))
        {
            case MctsAgent mcts:
            {
                var r = mcts.Search(observation, cancel);
                return new SearchOutcome(r.Best, [.. r.Moves.Select(m => new MoveAdvice(m.Move, m.Visits, m.VisitShare, m.Value, m.StdErr))],
                    r.Line, r.Simulations);
            }
            case NeuralAgent neural:
            {
                var r = neural.Search(observation, cancel);
                var gumbel = neural.Settings.Mode == NeuralMode.Gumbel;
                return new SearchOutcome(r.Best, [.. r.Moves.Select((m, i) => new MoveAdvice(m.Move, m.Visits,
                    gumbel ? r.PolicyTarget[i] : m.VisitShare, m.Value, m.StdErr))], r.Line, r.Simulations);
            }
            default:
                throw new InvalidOperationException("Not a search Agent");
        }
    }
}

/// <summary>
/// One row of the compare table: the Move, the log line that enters it, the owner's win chance with its standard
/// error, and the gap to the best Move. <see cref="Same"/> ("≈ same") when the gap is inside the combined noise
/// 2·√(se₁² + se₂²) (spec A8).
/// </summary>
public sealed record AdviceRow(Move Move, string Line, string Label, int? Slot, int Visits, double Share, double Win, double StdErr,
    double Gap, double Noise, bool Same, bool Best);

/// <summary>Ranked advice for one position: every legal Move, best first.</summary>
public sealed record Advice(string Key, string Agent, int Simulations, double Seconds, IReadOnlyList<AdviceRow> Rows,
    IReadOnlyList<LineStep> Line)
{
    public AdviceRow Best => Rows[0];
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

    /// <summary>All legal Moves, the search's choice first, then by share of the search (ties: higher win chance).</summary>
    public static AdviceRow[] Rank(GameState state, SearchOutcome outcome)
    {
        var player = state.PlayerToDecide;
        var best = outcome.Moves.First(m => m.Move == outcome.Best);
        return outcome.Moves
            .OrderByDescending(m => m.Move == outcome.Best)
            .ThenByDescending(m => m.Share)
            .ThenByDescending(m => m.Win)
            .Select(m =>
            {
                var isBest = m.Move == outcome.Best;
                var noise = Noise(best.StdErr, m.StdErr);
                var gap = isBest ? 0 : best.Win - m.Win;
                return new AdviceRow(m.Move, $"P{player + 1} {m.Move}", Label(state, m.Move), SlotOf(state, m.Move), m.Visits, m.Share,
                    m.Win, m.StdErr, gap, isBest ? 0 : noise, isBest || Math.Abs(gap) <= noise, isBest);
            })
            .ToArray();
    }

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
