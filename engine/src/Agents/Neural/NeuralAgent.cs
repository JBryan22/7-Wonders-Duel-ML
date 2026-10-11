using System.Globalization;
using SevenWondersDuel.Engine;

namespace SevenWondersDuel.Agents;

/// <summary>
/// The neural search's root modes (spec A5): <see cref="Gumbel"/> (all real training, and the default) and
/// <see cref="Classic"/> AlphaZero, kept as debug scaffolding to tell a search bug apart from "the network isn't learning".
/// </summary>
public enum NeuralMode { Gumbel, Classic }

/// <summary>
/// The neural Agent's settings: the <see cref="Model"/> (an ONNX file, or the built-in references <c>uniform</c> and
/// <c>playout</c>), <see cref="Simulations"/> per searched decision (spec: 800 in Matches and the Advisor, 64 in
/// training) and the root <see cref="Mode"/>.
/// <para>
/// Gumbel mode (Danihelka et al. 2022, as in DeepMind's <c>mctx</c>): at most <see cref="Considered"/> root Moves are
/// sampled without replacement with Gumbel noise (scaled by <see cref="GumbelScale"/>; 0 = none) and narrowed by
/// Sequential Halving; Q-values enter as σ(q) = (<see cref="CVisit"/> + max visits) · <see cref="CScale"/> · q, with q
/// min-max normalised to 0..1. These are mctx's published defaults: 16, 50, 0.1 and scale 1.
/// </para>
/// <para>
/// Classic mode: PUCT constant <see cref="Cpuct"/>, root Dirichlet noise (weight <see cref="Noise"/>, concentration
/// <see cref="Alpha"/>) and move-choice <see cref="Temperature"/>. Noise and temperature default to 0, as a Match
/// wants, and are refused in Gumbel mode, whose Gumbel noise supplies the exploration (spec A7).
/// </para>
/// <para>
/// A network runs on <see cref="Device"/> (default the CPU: at the batch sizes a Match makes, up to one position per
/// game in progress, the CPU answers faster than a CUDA call; ticket 20 measurements) with <see cref="Threads"/> ONNX
/// Runtime CPU threads (default 4; more fight the game threads).
/// </para>
/// </summary>
public sealed record NeuralSettings(string Model, int Simulations = 800, double Cpuct = 1.25, double Noise = 0,
    double Alpha = 0.3, double Temperature = 0, InferenceDevice Device = InferenceDevice.Cpu, int Threads = 4,
    NeuralMode Mode = NeuralMode.Gumbel, int Considered = 16, double CVisit = 50, double CScale = 0.1, double GumbelScale = 1)
{
    /// <summary>The built-in reference evaluator with a uniform policy and value 0.</summary>
    public const string Uniform = "uniform";

    /// <summary>The built-in reference evaluator with a uniform policy and a random playout's result as the value.</summary>
    public const string Playout = "playout";

    /// <summary>
    /// Parses the argument of a <c>neural</c> Agent spec: the model, optionally with settings, all comma-separated in
    /// any order, e.g. <c>runs/a/gen-3.onnx,sims=64,m=16,cvisit=50,cscale=0.1,gumbel=1,device=cpu,threads=4</c> or
    /// <c>x.onnx,mode=classic,sims=200,c=1.5,noise=0.25,alpha=0.3,temp=1</c>. A part is a setting when it starts with a
    /// known setting name and '='; the rest is the model path.
    /// </summary>
    public static NeuralSettings Parse(string? argument)
    {
        if (string.IsNullOrWhiteSpace(argument))
            throw new FormatException("The neural Agent needs a model: neural:<onnx file>[,sims=800,...] (or neural:uniform / neural:playout)");
        var model = new List<string>();
        var settings = new NeuralSettings("");
        foreach (var part in argument.Split(','))
        {
            var eq = part.IndexOf('=');
            var name = eq < 0 ? "" : part[..eq].ToLowerInvariant();
            var value = eq < 0 ? "" : part[(eq + 1)..];
            switch (name)
            {
                case "device":
                    settings = settings with { Device = Enum.TryParse<InferenceDevice>(value, true, out var d) ? d : throw Bad(part, "auto, cpu or cuda") };
                    break;
                case "mode":
                    settings = settings with { Mode = value.ToLowerInvariant() switch
                    {
                        "gumbel" => NeuralMode.Gumbel,
                        "classic" => NeuralMode.Classic,
                        _ => throw Bad(part, "gumbel or classic"),
                    } };
                    break;
                case "threads":
                    settings = settings with { Threads = int.TryParse(value, out var t) && t >= 0 && t <= 1024 ? t : throw Bad(part, "a CPU thread count, 0 = ONNX Runtime's default") };
                    break;
                case "sims" or "simulations" or "c" or "cpuct" or "noise" or "alpha" or "temp" or "temperature"
                    or "m" or "considered" or "cvisit" or "cscale" or "gumbel":
                    if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || v < 0 || double.IsNaN(v) || double.IsInfinity(v))
                        throw Bad(part, "a number ≥ 0");
                    settings = name switch
                    {
                        "sims" or "simulations" => v >= 1 && v == Math.Floor(v) && v <= 10_000_000
                            ? settings with { Simulations = (int)v } : throw Bad(part, "a positive whole number"),
                        "m" or "considered" => v >= 1 && v == Math.Floor(v) && v <= GameState.MaxMoves
                            ? settings with { Considered = (int)v } : throw Bad(part, "a positive whole number"),
                        "c" or "cpuct" => settings with { Cpuct = v },
                        "cvisit" => settings with { CVisit = v },
                        "cscale" => settings with { CScale = v },
                        "gumbel" => settings with { GumbelScale = v },
                        "noise" => v <= 1 ? settings with { Noise = v } : throw Bad(part, "a weight in 0..1"),
                        "alpha" => v > 0 ? settings with { Alpha = v } : throw Bad(part, "a positive number"),
                        _ => settings with { Temperature = v },
                    };
                    break;
                default:
                    model.Add(part);
                    break;
            }
        }
        if (model.Count == 0 || model.All(string.IsNullOrWhiteSpace))
            throw new FormatException($"The neural Agent needs a model: neural:<onnx file>[,sims=800,...], not 'neural:{argument}'");
        if (settings.Mode == NeuralMode.Gumbel && (settings.Noise > 0 || settings.Temperature > 0))
            throw new FormatException("Dirichlet noise and temperature belong to classic mode (add mode=classic); Gumbel mode explores with its Gumbel noise");
        return settings with { Model = string.Join(',', model) };
    }

    static FormatException Bad(string part, string expected) => new($"Bad neural setting '{part}' (expected {expected})");
}

/// <summary>One root Move after a neural search: visits, visit share, the network's prior (before noise), the deciding player's mean value as a win probability (draw ½) with its standard error.</summary>
public readonly record struct NeuralMoveStat(Move Move, int Visits, double VisitShare, double Prior, double Value, double StdErr);

/// <summary>
/// What one neural search found: per-Move statistics in legal-Move order, the Move chosen, the principal line, the
/// network's own view of the root (value as a win probability, outcome-class probabilities in
/// <see cref="NetworkEncoding.OutcomeClasses"/> order) and the training policy target, indexed like <see cref="Moves"/>
/// (Gumbel mode: the improved policy softmax(logits + σ(completed Q)); classic mode: the root's visit shares).
/// </summary>
public sealed record NeuralResult(IReadOnlyList<NeuralMoveStat> Moves, Move Best, IReadOnlyList<LineStep> Line, int Simulations,
    double RootValue, IReadOnlyList<double> Outcome, IReadOnlyList<double> PolicyTarget);

/// <summary>
/// The neural search (spec A5, "one neural search, two root modes"), guided by a policy/value evaluator.
/// <para>
/// <b>Gumbel mode</b> (Danihelka, Guez, Schrittwieser and Silver, "Policy improvement by planning with Gumbel", ICLR
/// 2022; follows DeepMind's <c>mctx</c> <c>gumbel_muzero_policy</c>). At the root, each legal Move gets Gumbel noise
/// g; the simulations go to the top <c>m</c> Moves by g + logit, then Sequential Halving repeatedly keeps the better
/// half by g + logit + σ(q̂), visiting the survivors evenly (the visit schedule is <see cref="SequentialHalving"/>).
/// The Move played is the survivor with the best g + logit + σ(q̂). Below the root the selection is deterministic:
/// the Move whose share of visits most lags the improved policy π' = softmax(logit + σ(q̂)). Here q̂ is the completed
/// Q: a visited Move's mean value, an unvisited one's the mixed value estimate (the network value blended with the
/// prior-weighted mean of the visited Moves' values); σ(q̂) = (c_visit + max visits) · c_scale · q̂ with q̂ min-max
/// normalised over the Moves. The training target is the root's π', which provably improves on the prior.
/// </para>
/// <para>
/// <b>Classic mode</b> (debug scaffolding): PUCT, Dirichlet noise at the root and a temperature for the move choice.
/// </para>
/// <para>
/// Like chance-node MCTS (<see cref="MctsAgent"/>) it searches from the Observation only: each simulation samples a
/// world with the <see cref="Determinizer"/> and walks one shared, open-loop tree of Moves, drawing every Reveal on the
/// way from what the searching player has not seen at the moment it happens. A simulation descends by PUCT until it
/// reaches a node not yet evaluated (or the end of the game), evaluates it with the network (after any Reveal that
/// precedes it, so the network sees the revealed card), and backs the value up. Each evaluated node keeps the
/// network's logits for every policy index; its priors are the softmax over the Moves legal in the current world, so
/// a Move that only some Reveals make legal still gets the network's prior. Values are kept per edge from its mover's
/// point of view ("me" is the player to decide, as in the Encoding). Selection at a node only weighs the Moves legal
/// in the current world. In classic mode an unvisited Move's value is its parent's mean value (first-play urgency).
/// </para>
/// <para>
/// Deterministic for a fixed seed and Observation, given that the network's output for a position does not depend on
/// what else is in its batch.
/// </para>
/// </summary>
public sealed class NeuralAgent : IAgent
{
    struct Node
    {
        public Move Move;          // the Move leading here
        public int Player;         // who played it (-1 at the root)
        public int FirstChild;     // -1 when none
        public int NextSibling;    // -1 when none
        public int Logits;         // offset of this node's policy logits in _logits; -1 until evaluated
        public int Visits;
        public int Reveals;        // simulations in which a Reveal came between the parent's decision and this Move
        public double ValueSum;    // Player's values (−1..1); at the root, the deciding player's
        public double ValueSq;
        public float RawValue;     // the network's value when evaluated, for RawMover (Gumbel's mixed value)
        public int RawMover;
    }

    static readonly int PolicySize = NetworkEncoding.PolicySize;

    readonly Rng _rng;
    readonly ILeafEvaluator _evaluator;
    readonly Determinizer _determinizer = new(0);
    readonly GameState _world = GameState.New(Ruleset.Default);
    readonly Move[] _rootMoves = new Move[GameState.MaxMoves];
    readonly Move[] _moves = new Move[GameState.MaxMoves];
    readonly double[] _priors = new double[GameState.MaxMoves];
    readonly int[] _children = new int[GameState.MaxMoves];
    readonly double[] _sigma = new double[GameState.MaxMoves];     // Gumbel: σ(completed Q) per Move
    readonly double[] _logPrior = new double[GameState.MaxMoves];  // Gumbel: normalised logits per Move
    readonly double[] _rootGumbel = new double[NetworkEncoding.PolicySize];
    int[] _halving = [];
    int _simulation;
    readonly float[] _outcome = new float[NetworkEncoding.OutcomeClasses.Length];
    readonly double[] _leafOutcome = new double[NetworkEncoding.OutcomeClasses.Length];
    // With TrackOutcomes: per node in the first two plies, the sum of the leaf outcome distributions (root player's view).
    readonly Dictionary<int, double[]> _outcomeSums = [];
    readonly int[] _path = new int[512];
    readonly bool[] _pathReveal = new bool[512];
    Node[] _nodes = new Node[1024];
    float[] _logits = new float[64 * NetworkEncoding.PolicySize];
    int _nodeCount, _logitCount, _rootPlayer;

    public NeuralAgent(NeuralSettings settings, ILeafEvaluator evaluator, ulong seed)
    {
        Settings = settings;
        _evaluator = evaluator;
        _rng = new Rng(seed);
    }

    /// <summary>
    /// Builds the Agent of a <c>neural:</c> spec: the shared batcher of its ONNX model (loaded once per process, after
    /// the Encoding-version handshake), or one of the built-in reference evaluators.
    /// </summary>
    public static NeuralAgent Create(NeuralSettings settings, ulong seed) => new(settings, settings.Model.ToLowerInvariant() switch
    {
        NeuralSettings.Uniform => new UniformEvaluator(),
        NeuralSettings.Playout => new PlayoutEvaluator(seed ^ 0x5851F42D4C957F2DUL),
        _ => new NetworkEvaluator(LoadedNetworks.Get(settings.Model, settings.Device, settings.Threads)),
    }, seed);

    public NeuralSettings Settings { get; }

    /// <summary>The last search's result (null before the first search, and after a forced Move).</summary>
    public NeuralResult? LastSearch { get; private set; }

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
    /// Runs <see cref="NeuralSettings.Simulations"/> simulations for the decision at the front of the Observation's
    /// queue and returns every legal Move's statistics, the chosen Move and the principal line. <paramref name="cancel"/>
    /// stops the search between simulations by throwing <see cref="OperationCanceledException"/>.
    /// </summary>
    public NeuralResult Search(Observation observation, CancellationToken cancel = default)
    {
        var root = observation.State;
        var me = observation.Player;
        var rootCount = root.LegalMoves(_rootMoves);
        if (rootCount == 0) throw new InvalidOperationException("No legal Move");
        _nodeCount = 0;
        _outcomeSums.Clear();
        _logitCount = 0;
        _rootPlayer = me;
        NewNode(default, -1);

        // The root: evaluated once from the Observation itself.
        var rootValue = Evaluate(0, root, me);
        var rootOutcome = Softmax(_outcome);
        var rootLogits = _logits.AsSpan(_nodes[0].Logits, PolicySize);
        var prior = new double[rootCount];
        PriorsOver(rootLogits, _rootMoves.AsSpan(0, rootCount), prior);
        var gumbel = Settings.Mode == NeuralMode.Gumbel;
        if (gumbel)
        {
            // One Gumbel(0, 1) sample per root Move, drawn in legal-Move order from the Agent's own stream.
            for (var m = 0; m < rootCount; m++)
                _rootGumbel[NetworkEncoding.PolicyIndex(_rootMoves[m])] =
                    Settings.GumbelScale * -Math.Log(-Math.Log(Math.Max(NextDouble(), 1e-300)));
            _halving = SequentialHalving(Math.Min(Settings.Considered, rootCount), Settings.Simulations);
        }
        else if (Settings.Noise > 0)
        {
            // Dirichlet noise mixed into the root priors, written back as logits so the root needs no special case.
            var noise = Dirichlet(rootCount, Settings.Alpha);
            rootLogits.Fill(float.NegativeInfinity);
            for (var m = 0; m < rootCount; m++)
            {
                var p = (1 - Settings.Noise) * prior[m] + Settings.Noise * noise[m];
                rootLogits[NetworkEncoding.PolicyIndex(_rootMoves[m])] = (float)Math.Log(Math.Max(p, 1e-30));
            }
        }

        for (var sim = 0; sim < Settings.Simulations; sim++)
        {
            cancel.ThrowIfCancellationRequested();
            _simulation = sim;
            var seed = _rng.NextUInt64();
            _determinizer.Reseed(seed);
            _determinizer.Sample(observation, _world);
            Simulate(_world);
        }

        // Root statistics in legal-Move order.
        var stats = new NeuralMoveStat[rootCount];
        var target = new double[rootCount];
        var rootVisits = 0;
        for (var c = _nodes[0].FirstChild; c >= 0; c = _nodes[c].NextSibling) rootVisits += _nodes[c].Visits;
        var nodes = new int[rootCount];
        for (var m = 0; m < rootCount; m++)
        {
            var c = nodes[m] = FindChild(0, _rootMoves[m]);
            var visits = c < 0 ? 0 : _nodes[c].Visits;
            target[m] = rootVisits == 0 ? 1.0 / rootCount : (double)visits / rootVisits;
            stats[m] = c < 0
                ? new NeuralMoveStat(_rootMoves[m], 0, 0, prior[m], 0.5, 0.5)
                : new NeuralMoveStat(_rootMoves[m], visits, rootVisits == 0 ? 0 : (double)visits / rootVisits, prior[m],
                    WinProbability(Mean(_nodes[c])), StdErr(_nodes[c]));
        }

        int best;
        if (gumbel)
        {
            // The improved policy over the root's Moves, and the played Move: among the Moves Sequential Halving
            // kept to the end (the most visited), the best by g + logit + σ(q̂).
            _rootMoves.AsSpan(0, rootCount).CopyTo(_moves);
            prior.CopyTo(_priors, 0);
            for (var m = 0; m < rootCount; m++) _children[m] = nodes[m];
            Sigma(0, me, rootCount);
            var maxVisits = stats.Max(s => s.Visits);
            best = -1;
            var bestScore = double.NegativeInfinity;
            for (var m = 0; m < rootCount; m++)
            {
                if (stats[m].Visits != maxVisits) continue;
                var score = _rootGumbel[NetworkEncoding.PolicyIndex(_rootMoves[m])] + _logPrior[m] + _sigma[m];
                if (best < 0 || score > bestScore) (best, bestScore) = (m, score);
            }
            var improved = new double[rootCount];
            for (var m = 0; m < rootCount; m++) improved[m] = _logPrior[m] + _sigma[m];
            SoftmaxInPlace(improved);
            target = improved;
        }
        else best = ChooseRootMove(stats);
        return new NeuralResult(stats, _rootMoves[best], PrincipalLine(nodes[best]), Settings.Simulations,
            WinProbability(rootValue), rootOutcome, target);
    }

    /// <summary>Temperature 0: the most visited Move (ties to the better value, then the higher prior); otherwise visits^(1/T).</summary>
    int ChooseRootMove(NeuralMoveStat[] stats)
    {
        if (Settings.Temperature > 0)
        {
            var weights = new double[stats.Length];
            var max = stats.Max(s => s.Visits);
            var total = 0.0;
            for (var m = 0; m < stats.Length; m++)
                total += weights[m] = max == 0 ? 1 : Math.Pow((double)stats[m].Visits / max, 1 / Settings.Temperature);
            var r = NextDouble() * total;
            for (var m = 0; m < stats.Length; m++)
                if ((r -= weights[m]) < 0) return m;
            for (var m = stats.Length - 1; m >= 0; m--)
                if (weights[m] > 0) return m;
        }
        var best = 0;
        for (var m = 1; m < stats.Length; m++)
        {
            var (a, b) = (stats[m], stats[best]);
            if (a.Visits > b.Visits || a.Visits == b.Visits && (a.Value > b.Value || a.Value == b.Value && a.Prior > b.Prior))
                best = m;
        }
        return best;
    }

    /// <summary>One simulation on the sampled world <paramref name="s"/> (overwritten): select by PUCT, evaluate one new node, back up.</summary>
    void Simulate(GameState s)
    {
        var depth = 0;
        var node = 0;
        var revealed = false;
        double value0; // the simulation's value for P1 (player 0)
        while (true)
        {
            if (s.IsOver)
            {
                value0 = NetworkEncoding.ValueTarget(s.Result!, 0);
                if (TracksOutcomes)
                {
                    Array.Clear(_leafOutcome);
                    _leafOutcome[NetworkEncoding.OutcomeClass(s.Result!, _rootPlayer)] = 1;
                }
                break;
            }
            if (s.Step == StepKind.Reveal)
            {
                s.Apply(_determinizer.Next(s));
                revealed = true;
                continue;
            }
            var mover = s.PlayerToDecide;
            if (_nodes[node].Logits < 0)
            {
                var v = Evaluate(node, s, mover);
                value0 = mover == 0 ? v : -v;
                if (TracksOutcomes) LeafOutcome(mover);
                break;
            }

            var n = s.LegalMoves(_moves);
            PriorsOver(_logits.AsSpan(_nodes[node].Logits, PolicySize), _moves.AsSpan(0, n), _priors);
            var parentVisits = 0;
            for (var i = 0; i < n; i++)
            {
                var c = _children[i] = FindChild(node, _moves[i]);
                if (c >= 0) parentVisits += _nodes[c].Visits;
            }
            var pick = Settings.Mode == NeuralMode.Gumbel
                ? node == 0 ? GumbelRootPick(n) : GumbelInteriorPick(node, mover, n, parentVisits)
                : PuctPick(node, mover, n, parentVisits);
            var next = _children[pick];
            if (next < 0)
            {
                next = NewNode(_moves[pick], mover);
                _nodes[next].NextSibling = _nodes[node].FirstChild;
                _nodes[node].FirstChild = next;
            }
            if (depth == _path.Length) throw new InvalidOperationException("Search path too deep");
            _path[depth] = next;
            _pathReveal[depth] = revealed;
            depth++;
            revealed = false;
            s.Apply(_moves[pick]);
            node = next;
        }

        ref var r = ref _nodes[0];
        var rootValue = _rootPlayer == 0 ? value0 : -value0;
        r.Visits++;
        r.ValueSum += rootValue;
        r.ValueSq += rootValue * rootValue;
        for (var d = 0; d < depth; d++)
        {
            ref var x = ref _nodes[_path[d]];
            var v = x.Player == 0 ? value0 : -value0;
            x.Visits++;
            x.ValueSum += v;
            x.ValueSq += v * v;
            if (_pathReveal[d]) x.Reveals++;
        }
        if (!TracksOutcomes) return;
        for (var d = 0; d < Math.Min(depth, 2); d++)
        {
            if (!_outcomeSums.TryGetValue(_path[d], out var sum)) _outcomeSums[_path[d]] = sum = new double[_leafOutcome.Length];
            for (var k = 0; k < sum.Length; k++) sum[k] += _leafOutcome[k];
        }
    }

    /// <summary>
    /// When true (the Advisor sets it), the search also averages the network's outcome-class distribution over the
    /// simulations through each node of the first two plies, for <see cref="OutcomeOf"/>. Results are unchanged.
    /// </summary>
    public bool TrackOutcomes { get; set; }

    /// <summary>True when the evaluator has an outcome head (a real network, not the built-in references).</summary>
    public bool HasOutcomeHead => _evaluator.HasOutcomeHead;

    bool TracksOutcomes => TrackOutcomes && _evaluator.HasOutcomeHead;

    /// <summary>The softmax of the leaf's outcome logits, turned from <paramref name="mover"/>'s view to the root player's.</summary>
    void LeafOutcome(int mover)
    {
        var p = Softmax(_outcome);
        var swap = mover != _rootPlayer;
        var half = (p.Length - 1) / 2; // wins 0..2, draw 3, losses 4..6
        for (var k = 0; k < p.Length; k++)
            _leafOutcome[k] = !swap || k == half ? p[k] : k < half ? p[k + half + 1] : p[k - half - 1];
    }

    /// <summary>
    /// After a search with <see cref="TrackOutcomes"/>: the mean outcome-class distribution (in
    /// <see cref="NetworkEncoding.OutcomeClasses"/> order, the searching player's view) over the simulations through
    /// the node reached by <paramref name="path"/> (one or two Moves from the root); null when not tracked or not visited.
    /// </summary>
    public double[]? OutcomeOf(IReadOnlyList<Move> path)
    {
        if (!TracksOutcomes || path.Count is 0 or > 2) return null;
        var node = 0;
        foreach (var move in path)
            if ((node = FindChild(node, move)) < 0) return null;
        if (!_outcomeSums.TryGetValue(node, out var sum) || _nodes[node].Visits == 0) return null;
        return [.. sum.Select(x => x / _nodes[node].Visits)];
    }

    /// <summary>
    /// After a search: the line that starts with the Moves of <paramref name="path"/> from the root and then follows the
    /// most visited child, each step with at least <see cref="MctsAgent.MinLineVisits"/> visits. Valid until the next search.
    /// </summary>
    public IReadOnlyList<LineStep> LineOf(IReadOnlyList<Move> path)
    {
        var node = 0;
        var prefix = new List<LineStep>();
        for (var i = 0; i < path.Count - 1; i++)
        {
            node = FindChild(node, path[i]);
            if (node < 0 || _nodes[node].Visits < MctsAgent.MinLineVisits) return prefix;
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
            children.Add(new TreeChild(_nodes[c].Move, _nodes[c].Player, _nodes[c].Visits, WinProbability(Mean(_nodes[c])), StdErr(_nodes[c])));
        return children;
    }

    LineStep Step(int c) =>
        new(_nodes[c].Player, _nodes[c].Move, _nodes[c].Visits, WinProbability(Mean(_nodes[c])), _nodes[c].Reveals * 2 > _nodes[c].Visits);

    /// <summary>Classic: PUCT, Q + c·P·√N/(1+n), an unvisited Move valued at its parent's mean.</summary>
    int PuctPick(int node, int mover, int n, int parentVisits)
    {
        ref var here = ref _nodes[node];
        var parentMean = Mean(here);
        var fpu = node == 0 || here.Player == mover ? parentMean : -parentMean;
        var sqrt = Math.Sqrt(Math.Max(1, parentVisits));
        var pick = 0;
        var bestScore = double.NegativeInfinity;
        for (var i = 0; i < n; i++)
        {
            var c = _children[i];
            var visits = c < 0 ? 0 : _nodes[c].Visits;
            var q = visits == 0 ? fpu : Mean(_nodes[c]);
            var score = q + Settings.Cpuct * _priors[i] * sqrt / (1 + visits);
            if (score > bestScore) (bestScore, pick) = (score, i);
        }
        return pick;
    }

    /// <summary>
    /// Gumbel at the root: Sequential Halving's schedule says how many visits a Move must have to be considered now;
    /// of those, the best by g + logit + σ(q̂). Moves outside the top <c>m</c>, or halved away, never have that count.
    /// </summary>
    int GumbelRootPick(int n)
    {
        Sigma(0, _rootPlayer, n);
        var considered = _halving[Math.Min(_simulation, _halving.Length - 1)];
        int pick = -1, fallback = 0;
        double bestScore = double.NegativeInfinity, fallbackScore = double.NegativeInfinity;
        for (var i = 0; i < n; i++)
        {
            var visits = _children[i] < 0 ? 0 : _nodes[_children[i]].Visits;
            var score = _rootGumbel[NetworkEncoding.PolicyIndex(_moves[i])] + _logPrior[i] + _sigma[i];
            if (visits == considered && (pick < 0 || score > bestScore)) (pick, bestScore) = (i, score);
            if (score > fallbackScore) (fallback, fallbackScore) = (i, score);
        }
        return pick >= 0 ? pick : fallback;
    }

    /// <summary>Gumbel below the root (deterministic): the Move maximising π'(a) − n(a)/(1 + N), π' = softmax(logit + σ(q̂)).</summary>
    int GumbelInteriorPick(int node, int mover, int n, int parentVisits)
    {
        Sigma(node, mover, n);
        Span<double> improved = stackalloc double[n];
        for (var i = 0; i < n; i++) improved[i] = _logPrior[i] + _sigma[i];
        SoftmaxInPlace(improved);
        var pick = 0;
        var bestScore = double.NegativeInfinity;
        for (var i = 0; i < n; i++)
        {
            var visits = _children[i] < 0 ? 0 : _nodes[_children[i]].Visits;
            var score = improved[i] - (double)visits / (1 + parentVisits);
            if (score > bestScore) (bestScore, pick) = (score, i);
        }
        return pick;
    }

    /// <summary>
    /// Fills <c>_logPrior</c> (the masked log-policy) and <c>_sigma</c> = σ(completed Q) for the <paramref name="n"/>
    /// Moves in <c>_moves</c>, with their children in <c>_children</c> and priors in <c>_priors</c>, at a node where
    /// <paramref name="mover"/> decides (mctx's <c>qtransform_completed_by_mix_value</c>).
    /// </summary>
    void Sigma(int node, int mover, int n)
    {
        ref var here = ref _nodes[node];
        var raw = here.RawMover == mover ? here.RawValue : -here.RawValue;
        int sumVisits = 0, maxVisits = 0;
        double visitedPrior = 0, weightedQ = 0;
        for (var i = 0; i < n; i++)
        {
            var p = Math.Max(_priors[i], 1e-30);
            _logPrior[i] = Math.Log(p);
            var c = _children[i];
            var visits = c < 0 ? 0 : _nodes[c].Visits;
            if (visits == 0) continue;
            sumVisits += visits;
            maxVisits = Math.Max(maxVisits, visits);
            visitedPrior += p;
            weightedQ += p * Mean(_nodes[c]);
        }
        // The mixed value: the network's value blended with the prior-weighted mean value of the visited Moves.
        var mixed = sumVisits == 0 ? raw : (raw + sumVisits * (weightedQ / visitedPrior)) / (1 + sumVisits);
        double min = double.PositiveInfinity, max = double.NegativeInfinity;
        for (var i = 0; i < n; i++)
        {
            var c = _children[i];
            var q = c < 0 || _nodes[c].Visits == 0 ? mixed : Mean(_nodes[c]);
            _sigma[i] = q;
            min = Math.Min(min, q);
            max = Math.Max(max, q);
        }
        var scale = (Settings.CVisit + maxVisits) * Settings.CScale / Math.Max(max - min, 1e-8);
        for (var i = 0; i < n; i++) _sigma[i] = (_sigma[i] - min) * scale;
    }

    /// <summary>
    /// The root visit counts Sequential Halving asks for, simulation by simulation (mctx's
    /// <c>get_sequence_of_considered_visits</c>): with <paramref name="considered"/> Moves, element k is the visit
    /// count a Move must have to receive simulation k. Each of the ⌈log2 m⌉ phases spends about an equal share of the
    /// budget visiting every surviving Move evenly, then the survivors are halved (down to 2).
    /// </summary>
    public static int[] SequentialHalving(int considered, int simulations)
    {
        var sequence = new List<int>(simulations);
        if (considered <= 1)
        {
            for (var k = 0; k < simulations; k++) sequence.Add(k);
            return [.. sequence];
        }
        var log2 = (int)Math.Ceiling(Math.Log2(considered));
        var visits = new int[considered];
        var survivors = considered;
        while (sequence.Count < simulations)
        {
            var extra = Math.Max(1, simulations / (log2 * survivors));
            for (var e = 0; e < extra; e++)
            {
                for (var i = 0; i < survivors; i++) sequence.Add(visits[i]);
                for (var i = 0; i < survivors; i++) visits[i]++;
            }
            survivors = Math.Max(2, survivors / 2);
        }
        return [.. sequence.Take(simulations)];
    }

    static void SoftmaxInPlace(Span<double> x)
    {
        var max = double.NegativeInfinity;
        foreach (var v in x) max = Math.Max(max, v);
        var sum = 0.0;
        for (var i = 0; i < x.Length; i++) sum += x[i] = Math.Exp(x[i] - max);
        for (var i = 0; i < x.Length; i++) x[i] /= sum;
    }

    /// <summary>Evaluates <paramref name="s"/> for <paramref name="mover"/> into <paramref name="node"/>'s logits; returns the value.</summary>
    float Evaluate(int node, GameState s, int mover)
    {
        if (_logitCount + PolicySize > _logits.Length) Array.Resize(ref _logits, _logits.Length * 2);
        var offset = _logitCount;
        _logitCount += PolicySize;
        var value = _evaluator.Evaluate(s, mover, _logits.AsSpan(offset, PolicySize), _outcome);
        _nodes[node].Logits = offset;
        _nodes[node].RawValue = value;
        _nodes[node].RawMover = mover;
        if (node == 0)
        {
            ref var root = ref _nodes[0];
            root.Visits = 1;
            root.ValueSum = value;
            root.ValueSq = (double)value * value;
        }
        return value;
    }

    /// <summary>The softmax of the logits over the given Moves (the masked policy).</summary>
    static void PriorsOver(ReadOnlySpan<float> logits, ReadOnlySpan<Move> moves, Span<double> priors)
    {
        var max = double.NegativeInfinity;
        for (var i = 0; i < moves.Length; i++)
            max = Math.Max(max, priors[i] = logits[NetworkEncoding.PolicyIndex(moves[i])]);
        if (double.IsNegativeInfinity(max))
        {
            priors[..moves.Length].Fill(1.0 / moves.Length);
            return;
        }
        var sum = 0.0;
        for (var i = 0; i < moves.Length; i++) sum += priors[i] = Math.Exp(priors[i] - max);
        for (var i = 0; i < moves.Length; i++) priors[i] /= sum;
    }

    static double[] Softmax(ReadOnlySpan<float> logits)
    {
        var p = new double[logits.Length];
        var max = double.NegativeInfinity;
        foreach (var l in logits) max = Math.Max(max, l);
        var sum = 0.0;
        for (var i = 0; i < p.Length; i++) sum += p[i] = Math.Exp(logits[i] - max);
        for (var i = 0; i < p.Length; i++) p[i] /= sum;
        return p;
    }

    /// <summary>A Dirichlet(alpha, ..., alpha) sample of size <paramref name="n"/>, from the Agent's own random stream.</summary>
    double[] Dirichlet(int n, double alpha)
    {
        var x = new double[n];
        var sum = 0.0;
        for (var i = 0; i < n; i++) sum += x[i] = Gamma(alpha);
        for (var i = 0; i < n; i++) x[i] = sum > 0 ? x[i] / sum : 1.0 / n;
        return x;
    }

    /// <summary>Gamma(shape, 1) by Marsaglia and Tsang, boosted for shape &lt; 1.</summary>
    double Gamma(double shape)
    {
        if (shape < 1) return Gamma(shape + 1) * Math.Pow(Math.Max(NextDouble(), 1e-300), 1 / shape);
        var d = shape - 1.0 / 3;
        var c = 1 / Math.Sqrt(9 * d);
        while (true)
        {
            double x, v;
            do
            {
                // Box–Muller normal.
                x = Math.Sqrt(-2 * Math.Log(Math.Max(NextDouble(), 1e-300))) * Math.Cos(2 * Math.PI * NextDouble());
                v = 1 + c * x;
            } while (v <= 0);
            v = v * v * v;
            var u = NextDouble();
            if (u < 1 - 0.0331 * x * x * x * x || Math.Log(Math.Max(u, 1e-300)) < 0.5 * x * x + d * (1 - v + Math.Log(v)))
                return d * v;
        }
    }

    double NextDouble() => (_rng.NextUInt64() >> 11) * (1.0 / (1UL << 53));

    static double Mean(in Node n) => n.Visits == 0 ? 0 : n.ValueSum / n.Visits;

    /// <summary>A value in −1..1 as a win probability (a draw counts ½), as the other Agents report it.</summary>
    static double WinProbability(double value) => (value + 1) / 2;

    /// <summary>The standard error of the mean value, on the win-probability scale.</summary>
    static double StdErr(in Node n)
    {
        if (n.Visits < 2) return 0.5;
        var mean = n.ValueSum / n.Visits;
        var variance = Math.Max(0, (n.ValueSq - n.Visits * mean * mean) / (n.Visits - 1));
        return Math.Sqrt(variance / n.Visits) / 2;
    }

    List<LineStep> PrincipalLine(int first)
    {
        var line = new List<LineStep>();
        for (var c = first; c >= 0 && _nodes[c].Visits >= MctsAgent.MinLineVisits;)
        {
            ref var n = ref _nodes[c];
            line.Add(new LineStep(n.Player, n.Move, n.Visits, WinProbability(Mean(n)), n.Reveals * 2 > n.Visits));
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
        _nodes[_nodeCount] = new Node { Move = move, Player = player, FirstChild = -1, NextSibling = -1, Logits = -1 };
        return _nodeCount++;
    }
}
