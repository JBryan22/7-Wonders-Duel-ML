using SevenWondersDuel.Engine;

namespace SevenWondersDuel.Agents.Tests;

/// <summary>
/// Networks and Encoding versions (spec-pantheon P-A7, ticket 09): a network encodes with the version it declares, a
/// v2 network plays base and Pantheon games, and a Pantheon game with a v1 network is refused. zero-v2.onnx is the
/// all-zero (uniform) Encoding v2 fixture from training/sevenwd/test_models.py.
/// </summary>
public class PantheonNeuralTests
{
    static string Model(string name) => Path.Combine(AppContext.BaseDirectory, "Models", name);

    static readonly string ZeroV1 = Model("zero.onnx");
    static readonly string ZeroV2 = Model("zero-v2.onnx");
    static readonly Ruleset Pantheon = new(Expansions: Expansions.Pantheon);

    /// <summary>Decision points (more than one legal Move) of seeded random games of <paramref name="rules"/>.</summary>
    static List<Observation> Positions(Ruleset rules, ulong seed, int games, int every)
    {
        var rng = new Rng(seed);
        var positions = new List<Observation>();
        for (var g = 0; g < games; g++)
        {
            var chance = new RandomChanceSource(rng.NextUInt64());
            var s = GameState.New(rules);
            var decisions = 0;
            while (!s.IsOver)
            {
                if (s.Step == StepKind.Reveal)
                {
                    s.Apply(chance.Next(s));
                    continue;
                }
                var moves = s.LegalMoves();
                if (moves.Count > 1 && decisions++ % every == 0) positions.Add(s.Clone().Observe(s.PlayerToDecide));
                s.Apply(moves[rng.Next(moves.Count)]);
            }
        }
        return positions;
    }

    [Fact]
    public void A_v2_network_loads_as_Encoding_v2_and_runs_at_its_sizes()
    {
        using var network = NeuralNetwork.Load(ZeroV2, InferenceDevice.Cpu);
        Assert.Same(EncodingVersion.V2, network.Encoding);
        Assert.Contains("Encoding v2", network.ToString());
        var input = new float[2 * NetworkEncodingV2.InputSize];
        var policy = new float[2 * NetworkEncodingV2.PolicySize];
        var value = new float[2];
        var outcome = new float[2 * NetworkEncoding.OutcomeClasses.Length];
        network.Run(input, 2, policy, value, outcome);
        Assert.All(policy, p => Assert.Equal(0, p));

        using var v1 = NeuralNetwork.Load(ZeroV1, InferenceDevice.Cpu);
        Assert.Same(EncodingVersion.V1, v1.Encoding);
        // A request sized for another version is refused rather than misread.
        using var batcher = new InferenceBatcher(network);
        Assert.Throws<ArgumentException>(() => batcher.Evaluate(new InferenceBatcher.Request()));
    }

    [Fact]
    public void A_Pantheon_game_with_an_Encoding_v1_network_is_refused()
    {
        var spec = $"neural:{ZeroV1},sims=8";
        var e = Assert.Throws<NotSupportedException>(() => AgentSpecs.Validate(spec, Pantheon));
        Assert.Contains("Encoding v1", e.Message);
        Assert.Contains("Encoding v2", e.Message);
        Assert.Throws<NotSupportedException>(() => Match.Run(new MatchSettings(spec, "random", Pairs: 1, Expansions: Expansions.Pantheon)));
        var agent = (NeuralAgent)AgentSpecs.Create(spec, 1);
        Assert.Throws<NotSupportedException>(() => agent.ChooseMove(Positions(Pantheon, 3, 1, 50)[0]));
        // The same network still plays base games, and a v2 network passes.
        AgentSpecs.Validate(spec, Ruleset.Default);
        AgentSpecs.Validate($"neural:{ZeroV2}", Pantheon);
        AgentSpecs.Validate("neural:uniform", Pantheon);
    }

    /// <summary><c>7wd match neural:&lt;v2 onnx&gt; random --expansion pantheon</c>, and the references in Pantheon games.</summary>
    [Fact]
    public void A_v2_network_and_the_references_play_Pantheon_Matches()
    {
        foreach (var spec in new[] { $"neural:{ZeroV2},sims=16", "neural:uniform,sims=16", "neural:playout,sims=16" })
        {
            var report = Match.Run(new MatchSettings(spec, "random", Pairs: 2, Expansions: Expansions.Pantheon));
            Assert.True(report.Complete, spec);
            Assert.All(report.Games, g => Assert.True(g.MovesA > 10, spec));
        }
    }

    /// <summary>
    /// In both kinds of game the zero v2 network searches exactly like the uniform reference (so Encoding v2's indices,
    /// masking and batching add nothing of their own), and in a base game exactly like the zero v1 network.
    /// </summary>
    [Fact]
    public void A_zero_v2_network_searches_like_the_uniform_reference_and_like_v1_in_base_games()
    {
        var v2 = LoadedNetworks.Get(ZeroV2, InferenceDevice.Cpu);
        var v1 = LoadedNetworks.Get(ZeroV1, InferenceDevice.Cpu);
        foreach (var mode in new[] { NeuralMode.Classic, NeuralMode.Gumbel })
        {
            var settings = new NeuralSettings(ZeroV2, Simulations: 60, Mode: mode);
            foreach (var (rules, seed) in new[] { (Ruleset.Default, 2UL), (Pantheon, 4UL) })
                foreach (var observation in Positions(rules, seed, 2, 11))
                {
                    var viaV2 = new NeuralAgent(settings, new NetworkEvaluator(v2), 5).Search(observation);
                    var reference = new NeuralAgent(settings, new UniformEvaluator(), 5).Search(observation);
                    Assert.Equal(reference.Moves, viaV2.Moves);
                    Assert.Equal(reference.Line, viaV2.Line);
                    Assert.Equal(reference.PolicyTarget, viaV2.PolicyTarget);
                    if (rules.Expansions != Expansions.None) continue;
                    var viaV1 = new NeuralAgent(settings with { Model = ZeroV1 }, new NetworkEvaluator(v1), 5).Search(observation);
                    Assert.Equal(viaV1.Moves, viaV2.Moves);
                    Assert.Equal(viaV1.PolicyTarget, viaV2.PolicyTarget);
                }
        }
    }
}
