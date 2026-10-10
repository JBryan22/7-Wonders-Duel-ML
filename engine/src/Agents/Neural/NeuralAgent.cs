using System.Globalization;
using SevenWondersDuel.Engine;

namespace SevenWondersDuel.Agents;

/// <summary>The neural search's root modes (spec A5). Gumbel arrives with ticket 21; classic is debug scaffolding.</summary>
public enum NeuralMode { Classic }

/// <summary>
/// The neural Agent's settings: the <see cref="Model"/> (an ONNX file, or the built-in references <c>uniform</c> and
/// <c>playout</c>), <see cref="Simulations"/> per searched decision (spec: 800 in Matches and the Advisor, 64 in
/// training), and classic mode's PUCT constant <see cref="Cpuct"/>, root Dirichlet noise (weight <see cref="Noise"/>,
/// concentration <see cref="Alpha"/>) and move-choice <see cref="Temperature"/>. Noise and temperature default to 0, as
/// a Match wants; Self-Play in classic mode switches them on. A network runs on <see cref="Device"/> (default the CPU:
/// at the batch sizes a Match makes, up to one position per game in progress, the CPU answers faster than a CUDA call;
/// ticket 20 measurements) with <see cref="Threads"/> ONNX Runtime CPU threads (default 4; more fight the game threads).
/// </summary>
public sealed record NeuralSettings(string Model, int Simulations = 800, double Cpuct = 1.25, double Noise = 0,
    double Alpha = 0.3, double Temperature = 0, InferenceDevice Device = InferenceDevice.Cpu, int Threads = 4, NeuralMode Mode = NeuralMode.Classic)
{
    /// <summary>The built-in reference evaluator with a uniform policy and value 0.</summary>
    public const string Uniform = "uniform";

    /// <summary>The built-in reference evaluator with a uniform policy and a random playout's result as the value.</summary>
    public const string Playout = "playout";

    /// <summary>
    /// Parses the argument of a <c>neural</c> Agent spec: the model, optionally with settings, all comma-separated in
    /// any order, e.g. <c>runs/a/gen-3.onnx,sims=200,c=1.5,noise=0.25,alpha=0.3,temp=1,device=cpu,threads=4,mode=classic</c>.
    /// A part is a setting when it starts with a known setting name and '='; the rest is the model path.
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
                        "classic" => NeuralMode.Classic,
                        "gumbel" => throw new FormatException("Gumbel mode is not built yet (ticket 21); use mode=classic"),
                        _ => throw Bad(part, "classic"),
                    } };
                    break;
                case "threads":
                    settings = settings with { Threads = int.TryParse(value, out var t) && t >= 0 && t <= 1024 ? t : throw Bad(part, "a CPU thread count, 0 = ONNX Runtime's default") };
                    break;
                case "sims" or "simulations" or "c" or "cpuct" or "noise" or "alpha" or "temp" or "temperature":
                    if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || v < 0 || double.IsNaN(v) || double.IsInfinity(v))
                        throw Bad(part, "a number ≥ 0");
                    settings = name switch
                    {
                        "sims" or "simulations" => v >= 1 && v == Math.Floor(v) && v <= 10_000_000
                            ? settings with { Simulations = (int)v } : throw Bad(part, "a positive whole number"),
                        "c" or "cpuct" => settings with { Cpuct = v },
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
        return settings with { Model = string.Join(',', model) };
    }

    static FormatException Bad(string part, string expected) => new($"Bad neural setting '{part}' (expected {expected})");
}

/// <summary>One root Move after a neural search: visits, visit share, the network's prior (before noise), the deciding player's mean value as a win probability (draw ½) with its standard error.</summary>
public readonly record struct NeuralMoveStat(Move Move, int Visits, double VisitShare, double Prior, double Value, double StdErr);

/// <summary>
/// What one neural search found: per-Move statistics in legal-Move order, the Move chosen, the principal line, the
/// network's own view of the root (value as a win probability, outcome-class probabilities in
/// <see cref="NetworkEncoding.OutcomeClasses"/> order) and the training policy target (classic mode: the root's visit
/// shares, indexed like <see cref="Moves"/>).
/// </summary>
public sealed record NeuralResult(IReadOnlyList<NeuralMoveStat> Moves, Move Best, IReadOnlyList<LineStep> Line, int Simulations,
    double RootValue, IReadOnlyList<double> Outcome, IReadOnlyList<double> PolicyTarget);

/// <summary>
/// The neural search (spec A5, "one neural search, two root modes"), here in its classic AlphaZero root mode: PUCT
/// guided by a policy/value evaluator, Dirichlet noise at the root and a temperature for the move choice.
/// <para>
/// Like chance-node MCTS (<see cref="MctsAgent"/>) it searches from the Observation only: each simulation samples a
/// world with the <see cref="Determinizer"/> and walks one shared, open-loop tree of Moves, drawing every Reveal on the
/// way from what the searching player has not seen at the moment it happens. A simulation descends by PUCT until it
/// reaches a node not yet evaluated (or the end of the game), evaluates it with the network (after any Reveal that
/// precedes it, so the network sees the revealed card), and backs the value up. Each evaluated node keeps the
/// network's logits for every policy index; its priors are the softmax over the Moves legal in the current world, so
/// a Move that only some Reveals make legal still gets the network's prior. Values are kept per edge from its mover's
/// point of view ("me" is the player to decide, as in the Encoding). An unvisited Move's value is its parent's mean
/// value (first-play urgency).
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
    readonly float[] _outcome = new float[NetworkEncoding.OutcomeClasses.Length];
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
    /// queue and returns every legal Move's statistics, the chosen Move and the principal line.
    /// </summary>
    public NeuralResult Search(Observation observation)
    {
        var root = observation.State;
        var me = observation.Player;
        var rootCount = root.LegalMoves(_rootMoves);
        if (rootCount == 0) throw new InvalidOperationException("No legal Move");
        _nodeCount = 0;
        _logitCount = 0;
        _rootPlayer = me;
        NewNode(default, -1);

        // The root: evaluated once from the Observation itself.
        var rootValue = Evaluate(0, root, me);
        var rootOutcome = Softmax(_outcome);
        var rootLogits = _logits.AsSpan(_nodes[0].Logits, PolicySize);
        var prior = new double[rootCount];
        PriorsOver(rootLogits, _rootMoves.AsSpan(0, rootCount), prior);
        if (Settings.Noise > 0)
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

        var best = ChooseRootMove(stats);
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
    }



    /// <summary>Evaluates <paramref name="s"/> for <paramref name="mover"/> into <paramref name="node"/>'s logits; returns the value.</summary>
    float Evaluate(int node, GameState s, int mover)
    {
        if (_logitCount + PolicySize > _logits.Length) Array.Resize(ref _logits, _logits.Length * 2);
        var offset = _logitCount;
        _logitCount += PolicySize;
        var value = _evaluator.Evaluate(s, mover, _logits.AsSpan(offset, PolicySize), _outcome);
        _nodes[node].Logits = offset;
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
