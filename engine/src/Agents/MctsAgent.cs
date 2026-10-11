using System.Globalization;
using SevenWondersDuel.Engine;

namespace SevenWondersDuel.Agents;

/// <summary>
/// Chance-node MCTS's settings (spec A5, Agent Ladder): <see cref="Simulations"/> per searched decision (the spec's
/// 1,000), the UCT exploration constant <see cref="Exploration"/>, and <see cref="Margin"/>, the weight of the mean
/// final VP margin added to a Move's win rate when ranking it (the same steadier signal PIMC needs; 0 = wins only).
/// </summary>
public sealed record MctsSettings(int Simulations = 1000, double Exploration = 0.7, double Margin = 0.02)
{
    public static readonly MctsSettings Default = new();

    /// <summary>
    /// Parses the argument of an <c>mcts</c> Agent spec: empty for the default, or overrides such as
    /// <c>sims=4000,c=1,margin=0</c>.
    /// </summary>
    public static MctsSettings Parse(string? argument)
    {
        var settings = Default;
        if (string.IsNullOrEmpty(argument)) return settings;
        foreach (var part in argument.Split(','))
        {
            var eq = part.IndexOf('=');
            if (eq < 0 || !double.TryParse(part[(eq + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || v < 0)
                throw new FormatException($"Bad MCTS setting '{part}' (expected name=number)");
            var name = part[..eq].ToLowerInvariant();
            if (name is "sims" or "simulations" && (v < 1 || v != Math.Floor(v) || v > 10_000_000))
                throw new FormatException($"Bad MCTS setting '{part}' ({name} is a positive whole number)");
            settings = name switch
            {
                "sims" or "simulations" => settings with { Simulations = (int)v },
                "c" or "exploration" => settings with { Exploration = v },
                "margin" => settings with { Margin = v },
                _ => throw new FormatException($"Unknown MCTS setting '{part[..eq]}' (sims, c, margin)"),
            };
        }
        return settings;
    }
}

/// <summary>
/// One root Move's MCTS statistics: simulations through it, its share of the root's visits, the deciding player's
/// mean result (win 1, draw ½) with its standard error, and the mean final VP margin (mine minus the opponent's).
/// </summary>
public readonly record struct MctsMoveStat(Move Move, int Visits, double VisitShare, double Value, double StdErr, double Margin);

/// <summary>
/// One step of the principal line: <paramref name="Player"/> plays <paramref name="Move"/>, the most-visited Move at
/// that point of the tree. <paramref name="AfterReveal"/> marks that a chance step (a face-down card or another unknown
/// revealed) usually comes first; its outcome is not part of the line, since it differs between simulations.
/// </summary>
public readonly record struct LineStep(int Player, Move Move, int Visits, double Value, bool AfterReveal);

/// <summary>
/// One child of a searched tree node (the Advisor reads below the root after a search): the Move, who plays it, its
/// visits, and its mover's mean result as a win probability (draw ½) with the standard error.
/// </summary>
public readonly record struct TreeChild(Move Move, int Player, int Visits, double Value, double StdErr);

/// <summary>What one MCTS search found: per-Move statistics in legal-Move order, the Move chosen and the principal line (starting with it).</summary>
public sealed record MctsResult(IReadOnlyList<MctsMoveStat> Moves, Move Best, IReadOnlyList<LineStep> Line, int Simulations);

/// <summary>
/// The fourth rung of the Agent Ladder and the Anchor (spec A5): chance-node MCTS, searching from the Observation only.
/// <para>
/// Each simulation samples a world from the Observation with the <see cref="Determinizer"/> and walks one shared tree
/// of Moves (single-observer ISMCTS). Chance is resolved at the moment it happens: whenever the walk reaches a Reveal
/// the Determinizer draws it from what the searching player has not seen, so the search never plans around a card
/// before it is turned over (no strategy fusion). The tree is open-loop over chance: a node is "this sequence of
/// Moves", whatever was revealed in between, so the many possible Reveals share one set of statistics instead of
/// splitting the budget into thousands of rarely visited branches. Because what is legal can depend on what was
/// revealed, selection uses availability counts: a child's UCT bonus counts only the simulations in which it was
/// legal.
/// </para>
/// <para>
/// Selection is UCT on the deciding player's mean result plus <see cref="MctsSettings.Margin"/> × mean final VP margin;
/// each simulation expands one new node and finishes the game with uniformly random Moves for both players. The
/// played Move is the most visited root child (ties to the better value). Simulations are seeded from the Agent's own
/// seed, so a search is deterministic for a fixed seed and Observation. States are reused with
/// <see cref="GameState.CopyFrom"/>.
/// </para>
/// </summary>
public sealed class MctsAgent : IAgent
{
    /// <summary>A principal-line step needs at least this many visits; below it the line stops (too little search depth).</summary>
    public const int MinLineVisits = 4;

    struct Node
    {
        public Move Move;          // the Move leading here
        public int Player;         // who played it
        public int FirstChild;     // -1 when none
        public int NextSibling;    // -1 when none
        public int Visits;
        public int Available;      // simulations in which this Move was legal at its parent
        public int Reveals;        // simulations in which a Reveal came between the parent's decision and this Move
        public double Score;       // Player's results (win 1, draw ½)
        public double ScoreSq;
        public double MarginSum;   // Player's final VP margins
    }

    readonly Rng _rng;
    readonly Determinizer _determinizer = new(0);
    readonly GameState _world = GameState.New(Ruleset.Default);
    readonly Move[] _rootMoves = new Move[GameState.MaxMoves];
    readonly Move[] _moves = new Move[GameState.MaxMoves];
    readonly int[] _path = new int[512];
    readonly bool[] _pathReveal = new bool[512];
    Node[] _nodes = new Node[1024];
    int _nodeCount;

    public MctsAgent(MctsSettings settings, ulong seed)
    {
        Settings = settings;
        _rng = new Rng(seed);
    }

    public MctsSettings Settings { get; }

    /// <summary>The last search's result (null before the first search, and after a forced Move).</summary>
    public MctsResult? LastSearch { get; private set; }

    public Move ChooseMove(Observation observation)
    {
        var n = observation.LegalMoves(_moves);
        if (n == 0) throw new InvalidOperationException("No legal Move");
        if (n == 1)
        {
            LastSearch = null;
            return _moves[0];
        }
        return (LastSearch = Search(observation)).Best;
    }

    /// <summary>
    /// Runs <see cref="MctsSettings.Simulations"/> simulations for the decision at the front of the Observation's
    /// queue ("me" is the player to decide) and returns every legal Move's statistics and the principal line.
    /// <paramref name="cancel"/> stops the search between simulations (the Advisor Window drops a search when the
    /// position changes) by throwing <see cref="OperationCanceledException"/>; it never changes a finished result.
    /// </summary>
    public MctsResult Search(Observation observation, CancellationToken cancel = default)
    {
        var root = observation.State;
        var rootCount = root.LegalMoves(_rootMoves);
        if (rootCount == 0) throw new InvalidOperationException("No legal Move");
        _nodeCount = 0;
        NewNode(default, -1);

        for (var sim = 0; sim < Settings.Simulations; sim++)
        {
            cancel.ThrowIfCancellationRequested();
            var seed = _rng.NextUInt64();
            _determinizer.Reseed(seed);
            _determinizer.Sample(observation, _world);
            Simulate(_world, new Rng(seed ^ 0xD1B54A32D192ED03UL));
        }

        // Root statistics in legal-Move order; the most visited Move is played.
        var stats = new MctsMoveStat[rootCount];
        var rootVisits = 0;
        for (var c = _nodes[0].FirstChild; c >= 0; c = _nodes[c].NextSibling) rootVisits += _nodes[c].Visits;
        var best = -1;
        var bestNode = -1;
        for (var m = 0; m < rootCount; m++)
        {
            var c = FindChild(0, _rootMoves[m]);
            if (c < 0)
            {
                stats[m] = new MctsMoveStat(_rootMoves[m], 0, 0, 0, 0.5, 0);
                continue;
            }
            ref var node = ref _nodes[c];
            stats[m] = new MctsMoveStat(_rootMoves[m], node.Visits, rootVisits == 0 ? 0 : (double)node.Visits / rootVisits,
                Mean(node), StdErr(node), node.Visits == 0 ? 0 : node.MarginSum / node.Visits);
            if (bestNode < 0 || node.Visits > _nodes[bestNode].Visits
                || node.Visits == _nodes[bestNode].Visits && Rank(node) > Rank(_nodes[bestNode]))
                (best, bestNode) = (m, c);
        }
        if (best < 0) best = 0;
        return new MctsResult(stats, _rootMoves[best], PrincipalLine(bestNode), Settings.Simulations);
    }

    /// <summary>One simulation on the sampled world <paramref name="s"/> (overwritten): select, expand one node, play out, back up.</summary>
    void Simulate(GameState s, Rng policy)
    {
        var depth = 0;
        var node = 0;
        var revealed = false;
        var expanded = false;
        while (!s.IsOver)
        {
            if (s.Step == StepKind.Reveal)
            {
                s.Apply(_determinizer.Next(s));
                revealed = true;
                continue;
            }
            if (expanded)
            {
                // Playout: uniformly random Moves for both players.
                var count = s.LegalMoves(_moves);
                s.Apply(_moves[policy.Next(count)]);
                continue;
            }

            var n = s.LegalMoves(_moves);
            var player = s.PlayerToDecide;
            var pick = -1;
            var untried = 0;
            // Availability: every legal child counts this simulation; an untried legal Move is expanded first.
            for (var i = 0; i < n; i++)
            {
                var c = FindChild(node, _moves[i]);
                if (c < 0)
                {
                    // Reservoir-pick one untried Move uniformly.
                    if (policy.Next(++untried) == 0) pick = i;
                }
                else _nodes[c].Available++;
            }
            int next;
            if (untried > 0)
            {
                next = NewNode(_moves[pick], player);
                _nodes[next].NextSibling = _nodes[node].FirstChild;
                _nodes[node].FirstChild = next;
                _nodes[next].Available = 1;
                expanded = true;
            }
            else
            {
                next = -1;
                var bestUcb = double.NegativeInfinity;
                for (var i = 0; i < n; i++)
                {
                    var c = FindChild(node, _moves[i]);
                    ref var child = ref _nodes[c];
                    var ucb = Rank(child) + Settings.Exploration * Math.Sqrt(Math.Log(child.Available) / child.Visits);
                    if (ucb > bestUcb) (bestUcb, next) = (ucb, c);
                }
            }
            _path[depth] = next;
            _pathReveal[depth] = revealed;
            depth++;
            revealed = false;
            s.Apply(_nodes[next].Move);
            node = next;
        }

        var result = s.Result!;
        double score0 = result.IsDraw ? 0.5 : result.Winner == 0 ? 1 : 0;
        var margin0 = Scoring.Total(s, 0) - Scoring.Total(s, 1);
        for (var d = 0; d < depth; d++)
        {
            ref var n = ref _nodes[_path[d]];
            var score = n.Player == 0 ? score0 : 1 - score0;
            n.Visits++;
            n.Score += score;
            n.ScoreSq += score * score;
            n.MarginSum += n.Player == 0 ? margin0 : -margin0;
            if (_pathReveal[d]) n.Reveals++;
        }
    }

    double Rank(in Node n) => n.Visits == 0 ? double.NegativeInfinity : Mean(n) + Settings.Margin * n.MarginSum / n.Visits;

    static double Mean(in Node n) => n.Visits == 0 ? 0 : n.Score / n.Visits;

    static double StdErr(in Node n)
    {
        if (n.Visits < 2) return 0.5;
        var mean = n.Score / n.Visits;
        var variance = Math.Max(0, (n.ScoreSq - n.Visits * mean * mean) / (n.Visits - 1));
        return Math.Sqrt(variance / n.Visits);
    }

    /// <summary>
    /// After a search: the line that starts with the Moves of <paramref name="path"/> from the root and then follows the
    /// most visited child, each step with at least <see cref="MinLineVisits"/> visits. Shorter (possibly empty) when
    /// the search did not go that deep. Valid until the next search.
    /// </summary>
    public IReadOnlyList<LineStep> LineOf(IReadOnlyList<Move> path)
    {
        var node = 0;
        var prefix = new List<LineStep>();
        for (var i = 0; i < path.Count - 1; i++)
        {
            node = FindChild(node, path[i]);
            if (node < 0 || _nodes[node].Visits < MinLineVisits) return prefix;
            prefix.Add(Step(node));
        }
        var last = path.Count == 0 ? -1 : FindChild(node, path[^1]);
        return [.. prefix, .. PrincipalLine(last)];
    }

    /// <summary>After a search: the children of the node reached by <paramref name="path"/> from the root (empty when it was not reached).</summary>
    public IReadOnlyList<TreeChild> ChildrenOf(IReadOnlyList<Move> path)
    {
        var node = 0;
        foreach (var move in path)
            if ((node = FindChild(node, move)) < 0) return [];
        var children = new List<TreeChild>();
        for (var c = _nodes[node].FirstChild; c >= 0; c = _nodes[c].NextSibling)
            children.Add(new TreeChild(_nodes[c].Move, _nodes[c].Player, _nodes[c].Visits, Mean(_nodes[c]), StdErr(_nodes[c])));
        return children;
    }

    LineStep Step(int c) => new(_nodes[c].Player, _nodes[c].Move, _nodes[c].Visits, Mean(_nodes[c]), _nodes[c].Reveals * 2 > _nodes[c].Visits);

    List<LineStep> PrincipalLine(int first)
    {
        var line = new List<LineStep>();
        for (var c = first; c >= 0 && _nodes[c].Visits >= MinLineVisits;)
        {
            ref var n = ref _nodes[c];
            line.Add(new LineStep(n.Player, n.Move, n.Visits, Mean(n), n.Reveals * 2 > n.Visits));
            var next = -1;
            for (var k = n.FirstChild; k >= 0; k = _nodes[k].NextSibling)
                if (next < 0 || _nodes[k].Visits > _nodes[next].Visits) next = k;
            c = next;
        }
        return line;
    }

    int FindChild(int parent, Move move)
    {
        for (var c = _nodes[parent].FirstChild; c >= 0; c = _nodes[c].NextSibling)
            if (_nodes[c].Move == move) return c;
        return -1;
    }

    int NewNode(Move move, int player)
    {
        if (_nodeCount == _nodes.Length) Array.Resize(ref _nodes, _nodes.Length * 2);
        _nodes[_nodeCount] = new Node { Move = move, Player = player, FirstChild = -1, NextSibling = -1 };
        return _nodeCount++;
    }
}
