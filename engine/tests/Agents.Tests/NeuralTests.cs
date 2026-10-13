using System.Text.Json;
using SevenWondersDuel.Engine;

namespace SevenWondersDuel.Agents.Tests;

/// <summary>
/// Running the network from C# (spec P6; ticket 20): ONNX inference and its Encoding-version handshake, batching, and
/// the neural search in both root modes (Gumbel: ticket 21). The models are tiny fixtures from training/sevenwd/test_models.py.
/// </summary>
public class NeuralTests
{
    const Promos BothPromos = Promos.Messe | Promos.Liberty;

    static string Model(string name) => Path.Combine(AppContext.BaseDirectory, "Models", name);

    static readonly string Tiny = Model("tiny.onnx");
    static readonly string Zero = Model("zero.onnx");

    /// <summary>Decision points from random games (both promos every other game), each with its Observation.</summary>
    static List<Observation> Positions(ulong seed, int games, int every = 5)
    {
        var rng = new Rng(seed);
        var positions = new List<Observation>();
        for (var g = 0; g < games; g++)
        {
            var chance = new RandomChanceSource(rng.NextUInt64());
            var s = GameState.New(new Ruleset(Promos: g % 2 == 0 ? Promos.None : BothPromos));
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

    // ---- Inference and the version handshake ------------------------------------------------------------------

    [Fact]
    public void Onnx_inference_matches_the_torch_outputs_the_model_was_exported_with()
    {
        using var network = NeuralNetwork.Load(Tiny, InferenceDevice.Cpu);
        Assert.Equal(InferenceDevice.Cpu, network.Device);
        Assert.Equal("1", network.Metadata["encoding_version"]);

        using var expected = JsonDocument.Parse(File.ReadAllText(Model("tiny.expected.json")));
        var size = NetworkEncoding.InputSize;
        var input = new float[2 * size];
        for (var i = 0; i < size; i++) input[size + i] = (float)(i % 7) / 7;
        var policy = new float[2 * NetworkEncoding.PolicySize];
        var value = new float[2];
        var outcome = new float[2 * NetworkEncoding.OutcomeClasses.Length];
        network.Run(input, 2, policy, value, outcome);

        for (var row = 0; row < 2; row++)
        {
            Close(expected.RootElement.GetProperty("value")[row].GetSingle(), value[row]);
            var p = expected.RootElement.GetProperty("policy")[row];
            for (var i = 0; i < NetworkEncoding.PolicySize; i++)
                Close(p[i].GetSingle(), policy[row * NetworkEncoding.PolicySize + i]);
            var o = expected.RootElement.GetProperty("outcome")[row];
            for (var i = 0; i < NetworkEncoding.OutcomeClasses.Length; i++)
                Close(o[i].GetSingle(), outcome[row * NetworkEncoding.OutcomeClasses.Length + i]);
        }
    }

    static void Close(float expected, float actual) => Assert.InRange(actual, expected - 1e-4f, expected + 1e-4f);

    [Fact]
    public void A_model_of_another_Encoding_version_is_refused()
    {
        var path = Model("wrong-version.onnx");
        var e = Assert.Throws<FormatException>(() => NeuralNetwork.Load(path, InferenceDevice.Cpu));
        Assert.Contains("Encoding v999", e.Message);
        Assert.Contains($"Encoding v{NetworkEncoding.Version}", e.Message);
        // Through the Agent spec too, which is how a Match or Self-Play would meet it, before any game is played.
        Assert.Throws<FormatException>(() => AgentSpecs.Validate($"neural:{path}"));
        Assert.Throws<FormatException>(() => Match.Run(new MatchSettings($"neural:{path}", "random", Pairs: 1)));
    }

    [Fact]
    public void A_network_without_the_derived_features_ignores_them_and_an_unknown_dropped_block_is_refused()
    {
        using var network = NeuralNetwork.Load(Model("no-derived.onnx"), InferenceDevice.Cpu);
        Assert.Equal(["derived"], network.DroppedInputs);
        Assert.Contains("without derived", network.ToString());
        using (var tiny = NeuralNetwork.Load(Tiny, InferenceDevice.Cpu)) Assert.Empty(tiny.DroppedInputs);

        // Same inputs except the derived block: same outputs. Changing another block does change them.
        var derived = NetworkEncoding.InputBlocks.Single(b => b.Name == "derived");
        var size = NetworkEncoding.InputSize;
        var input = new float[3 * size];
        for (var i = 0; i < size; i++) input[i] = input[size + i] = input[2 * size + i] = (float)(i % 7) / 7;
        for (var i = derived.Offset; i < derived.Offset + derived.Size; i++) input[size + i] = 1f - input[i];
        input[2 * size] = 1f - input[0];
        var policy = new float[3 * NetworkEncoding.PolicySize];
        var value = new float[3];
        var outcome = new float[3 * NetworkEncoding.OutcomeClasses.Length];
        network.Run(input, 3, policy, value, outcome);
        Assert.Equal(value[0], value[1]);
        Assert.Equal(policy[..NetworkEncoding.PolicySize], policy[NetworkEncoding.PolicySize..(2 * NetworkEncoding.PolicySize)]);
        Assert.NotEqual(value[0], value[2]);

        var e = Assert.Throws<FormatException>(() => NeuralNetwork.Load(Model("bad-drop.onnx"), InferenceDevice.Cpu));
        Assert.Contains("no-such-block", e.Message);
    }

    [Fact]
    public void Files_that_are_not_7wd_networks_are_refused()
    {
        Assert.Throws<FileNotFoundException>(() => NeuralNetwork.Load(Model("missing.onnx"), InferenceDevice.Cpu));
        Assert.Throws<FileNotFoundException>(() => AgentSpecs.Validate($"neural:{Model("missing.onnx")}"));
        var notOnnx = Path.Combine(Path.GetTempPath(), $"7wd-not-a-model-{Guid.NewGuid():N}.onnx");
        File.WriteAllText(notOnnx, "not a model");
        try
        {
            Assert.ThrowsAny<Exception>(() => NeuralNetwork.Load(notOnnx, InferenceDevice.Cpu));
        }
        finally
        {
            File.Delete(notOnnx);
        }
    }

    /// <summary>
    /// Many searches evaluating at once share one network call, and each gets exactly the numbers it would get alone,
    /// whatever else is in its batch, which is what keeps the neural search deterministic under any thread count.
    /// </summary>
    [Fact]
    public void Concurrent_evaluations_are_batched_and_each_gets_its_own_outputs()
    {
        using var network = NeuralNetwork.Load(Tiny, InferenceDevice.Cpu);
        using var batcher = new InferenceBatcher(network, maxBatch: 64);
        var positions = Positions(1, 6, every: 3).Take(64).ToArray();
        var alone = positions.Select(p =>
        {
            var r = new InferenceBatcher.Request();
            NetworkEncoding.Encode(p, r.Input);
            batcher.Evaluate(r);
            return (Policy: r.Policy.ToArray(), r.Value, Outcome: r.Outcome.ToArray());
        }).ToArray();
        Assert.Equal(positions.Length, batcher.Batches);

        for (var round = 0; round < 5; round++)
        {
            var together = new (float[] Policy, float Value, float[] Outcome)[positions.Length];
            using var start = new Barrier(positions.Length);
            var threads = positions.Select((p, i) => new Thread(() =>
            {
                var r = new InferenceBatcher.Request();
                NetworkEncoding.Encode(p, r.Input);
                start.SignalAndWait();
                batcher.Evaluate(r);
                together[i] = (r.Policy.ToArray(), r.Value, r.Outcome.ToArray());
            })).ToList();
            threads.ForEach(t => t.Start());
            threads.ForEach(t => t.Join());
            for (var i = 0; i < positions.Length; i++)
            {
                Assert.Equal(alone[i].Value, together[i].Value);
                Assert.Equal(alone[i].Policy, together[i].Policy);
                Assert.Equal(alone[i].Outcome, together[i].Outcome);
            }
        }
        Assert.True(batcher.Positions > batcher.Batches, "no evaluation was ever batched with another");
    }

    // ---- Agent specs ------------------------------------------------------------------------------------------

    [Fact]
    public void Specs_name_the_model_and_settings_in_any_order()
    {
        Assert.Equal(new NeuralSettings(Tiny), ((NeuralAgent)AgentSpecs.Create($"neural:{Tiny}", 1)).Settings);
        Assert.Equal(new NeuralSettings(Tiny, Simulations: 64, Cpuct: 2, Noise: 0.25, Alpha: 0.5, Temperature: 1, Threads: 2, Mode: NeuralMode.Classic),
            NeuralSettings.Parse($"sims=64,{Tiny},c=2,noise=0.25,alpha=0.5,temp=1,threads=2,mode=classic"));
        Assert.Equal(NeuralMode.Gumbel, NeuralSettings.Parse("x.onnx").Mode);
        Assert.Equal(new NeuralSettings(Tiny, Simulations: 64, Considered: 8, CVisit: 40, CScale: 1, GumbelScale: 0),
            NeuralSettings.Parse($"{Tiny},m=8,sims=64,cvisit=40,cscale=1,gumbel=0,mode=gumbel"));
        Assert.Equal(800, NeuralSettings.Parse("x.onnx").Simulations);
        Assert.Equal(InferenceDevice.Cpu, NeuralSettings.Parse("x.onnx").Device);
        Assert.Equal(InferenceDevice.Cuda, NeuralSettings.Parse("x.onnx,device=CUDA").Device);
        Assert.Equal("a,b.onnx", NeuralSettings.Parse("a,b.onnx,sims=3").Model);
        Assert.IsType<NeuralAgent>(AgentSpecs.Create("neural:uniform", 1));
        Assert.IsType<NeuralAgent>(AgentSpecs.Create("neural:playout,sims=10", 1));
        foreach (var bad in new[] { "neural", "neural:", "neural:sims=5", "neural:x.onnx,sims=0", "neural:x.onnx,sims=1.5",
                     "neural:x.onnx,noise=2", "neural:x.onnx,alpha=0", "neural:x.onnx,c=-1", "neural:x.onnx,device=tpu",
                     "neural:x.onnx,mode=puct", "neural:x.onnx,threads=-1", "neural:x.onnx,noise=0.25", "neural:x.onnx,temp=1",
                     "neural:x.onnx,mode=gumbel,temp=1", "neural:x.onnx,m=0", "neural:x.onnx,m=2.5", "neural:x.onnx,cscale=-1" })
            Assert.Throws<FormatException>(() => NeuralSettings.Parse(bad.Length > 7 ? bad[7..] : null));
        Assert.Null(Ladder.Elo($"neural:{Tiny}"));
    }

    // ---- The search --------------------------------------------------------------------------------------------

    /// <summary>Spec P6 runnable: a network exported to ONNX playing through a Match against Random.</summary>
    [Fact]
    public void A_network_plays_a_Match_against_Random()
    {
        var report = Match.Run(new MatchSettings($"neural:{Tiny},sims=16", "random", Pairs: 4, Promos: BothPromos));
        Assert.True(report.Complete);
        Assert.Equal(8, report.GameCount);
        Assert.All(report.Pairs.SelectMany(p => p.Games), g => Assert.True(g.MovesA > 10));
    }

    /// <summary>
    /// The search-bug check of spec P6, part 1: an ONNX network with a uniform policy and value 0 (all weights zero)
    /// searches exactly like the built-in uniform reference evaluator: same visits, values and principal lines. So
    /// everything between the search and ONNX Runtime (encoding, batching, masking, softmax) adds nothing of its own.
    /// </summary>
    [Fact]
    public void A_zero_network_searches_exactly_like_the_uniform_reference()
    {
        var batcher = LoadedNetworks.Get(Zero, InferenceDevice.Cpu);
        foreach (var mode in new[] { NeuralMode.Classic, NeuralMode.Gumbel })
        {
            var settings = new NeuralSettings(Zero, Simulations: 120, Mode: mode);
            foreach (var observation in Positions(2, 4, every: 9))
            {
                var viaOnnx = new NeuralAgent(settings, new NetworkEvaluator(batcher), 5).Search(observation);
                var reference = new NeuralAgent(settings, new UniformEvaluator(), 5).Search(observation);
                Assert.Equal(reference.Moves, viaOnnx.Moves);
                Assert.Equal(reference.Line, viaOnnx.Line);
                Assert.Equal(reference.Best, viaOnnx.Best);
                Assert.Equal(reference.PolicyTarget, viaOnnx.PolicyTarget);
                Assert.Equal(0.5, viaOnnx.RootValue);
                Assert.All(viaOnnx.Outcome, p => Assert.Equal(1.0 / 7, p, 9));
            }
        }
    }

    // ---- What the Advisor reads after a search (ticket 31) -------------------------------------------------------

    /// <summary>A fake network sure that the player to decide wins by military, whatever the position.</summary>
    sealed class MilitaryOptimist : ILeafEvaluator
    {
        public float Evaluate(GameState state, int me, Span<float> policy, Span<float> outcome)
        {
            policy.Clear();
            outcome.Fill(-20);
            outcome[0] = 20;
            return 0;
        }

        public bool HasOutcomeHead => true;
    }

    [Fact]
    public void Outcome_tracking_leaves_the_search_unchanged_and_averages_the_outcome_head_per_Move()
    {
        foreach (var mode in new[] { NeuralMode.Classic, NeuralMode.Gumbel })
        {
            var settings = new NeuralSettings(Tiny, Simulations: 150, Mode: mode);
            var batcher = LoadedNetworks.Get(Tiny, InferenceDevice.Cpu);
            foreach (var observation in Positions(9, 3, every: 11))
            {
                var plain = new NeuralAgent(settings, new NetworkEvaluator(batcher), 3).Search(observation);
                var agent = new NeuralAgent(settings, new NetworkEvaluator(batcher), 3) { TrackOutcomes = true };
                var tracked = agent.Search(observation);
                Assert.Equal(plain.Moves, tracked.Moves);
                Assert.Equal(plain.Line, tracked.Line);
                Assert.Equal(plain.Best, tracked.Best);
                Assert.True(agent.HasOutcomeHead);
                Assert.Equal(tracked.Line, agent.LineOf([tracked.Best]));
                foreach (var m in tracked.Moves)
                {
                    var p = agent.OutcomeOf([m.Move]);
                    if (m.Visits == 0) { Assert.Null(p); continue; }
                    Assert.Equal(1.0, p!.Sum(), 9);
                    var children = agent.ChildrenOf([m.Move]);
                    Assert.True(children.Sum(c => c.Visits) < m.Visits);
                    foreach (var c in children.Where(c => c.Visits > 0))
                        Assert.Equal(1.0, agent.OutcomeOf([m.Move, c.Move])!.Sum(), 9);
                }
            }
        }

        // The references have no outcome head, so there is nothing to show.
        var uniform = new NeuralAgent(new NeuralSettings("uniform", Simulations: 50), new UniformEvaluator(), 1) { TrackOutcomes = true };
        var r = uniform.Search(Positions(9, 1)[0]);
        Assert.False(uniform.HasOutcomeHead);
        Assert.Null(uniform.OutcomeOf([r.Best]));
    }

    [Fact]
    public void The_outcome_head_is_read_from_the_searching_players_point_of_view()
    {
        // Each leaf says "the player to decide wins by military": from the root player's view that is a military win
        // where they decide and a military loss where the opponent does, so only those two classes get any weight.
        var settings = new NeuralSettings("fake", Simulations: 200, Mode: NeuralMode.Classic);
        var lossMilitary = Array.IndexOf(NetworkEncoding.OutcomeClasses, "loss-military");
        var opponentLeaves = 0.0;
        foreach (var observation in Positions(10, 2, every: 13).Where(o => o.State.Age is 1 or 2).Take(6))
        {
            var agent = new NeuralAgent(settings, new MilitaryOptimist(), 4) { TrackOutcomes = true };
            var r = agent.Search(observation);
            foreach (var m in r.Moves.Where(m => m.Visits > 0))
            {
                var p = agent.OutcomeOf([m.Move])!;
                Assert.True(p[0] + p[lossMilitary] > 0.95, string.Join(",", p));
                opponentLeaves += p[lossMilitary];
            }
        }
        Assert.True(opponentLeaves > 1, "Leaves where the opponent decides count as military losses");
    }

    // ---- Gumbel mode (ticket 21) -------------------------------------------------------------------------------

    /// <summary>Sequential Halving's root visit schedule, checked against mctx's get_sequence_of_considered_visits.</summary>
    [Fact]
    public void Sequential_halving_schedules_the_root_visits_like_mctx()
    {
        Assert.Equal([0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5], NeuralAgent.SequentialHalving(4, 16));
        Assert.Equal([0, 1, 2, 3, 4], NeuralAgent.SequentialHalving(1, 5));
        Assert.Equal([0, 0, 1, 1, 2, 2, 3], NeuralAgent.SequentialHalving(2, 7));
        Assert.Equal([0, 0, 0], NeuralAgent.SequentialHalving(16, 3));
        var sixtyFour = NeuralAgent.SequentialHalving(16, 64);
        Assert.Equal(64, sixtyFour.Length);
        Assert.Equal(Enumerable.Repeat(0, 16).Concat(Enumerable.Repeat(1, 8)).Concat(Enumerable.Repeat(2, 8))
            .Concat(new[] { 3, 4, 5, 6 }.SelectMany(v => Enumerable.Repeat(v, 4)))
            .Concat(Enumerable.Range(7, 8).SelectMany(v => Enumerable.Repeat(v, 2))), sixtyFour);
    }

    /// <summary>
    /// The improved policy (spec A6 policy target) is a probability distribution over the legal Moves equal to
    /// softmax(log prior + σ(completed Q)), recomputed here independently from the reported statistics; the visits
    /// follow Sequential Halving (only the top m Moves are ever visited, and the played Move is among the most visited).
    /// </summary>
    [Fact]
    public void Gumbel_search_returns_a_valid_improved_policy()
    {
        var agent = (NeuralAgent)AgentSpecs.Create($"neural:{Tiny},sims=64", 13);
        var settings = agent.Settings;
        var searches = 0;
        foreach (var observation in Positions(7, 3, every: 2))
        {
            var move = agent.ChooseMove(observation);
            var result = agent.LastSearch!;
            searches++;
            Assert.Equal(move, result.Best);
            var n = result.Moves.Count;
            Assert.Equal(observation.LegalMoves(), result.Moves.Select(m => m.Move));
            Assert.Equal(64, result.Moves.Sum(m => m.Visits));
            Assert.Equal(Math.Min(16, n), result.Moves.Count(m => m.Visits > 0));
            Assert.Equal(result.Moves.Max(m => m.Visits), result.Moves.First(m => m.Move == move).Visits);
            Assert.Equal(n, result.PolicyTarget.Count);
            Assert.All(result.PolicyTarget, p => Assert.InRange(p, 0, 1));
            Assert.Equal(1, result.PolicyTarget.Sum(), 9);

            // σ(completed Q) from the statistics, as mctx's qtransform_completed_by_mix_value computes it.
            var raw = 2 * result.RootValue - 1;
            var visited = result.Moves.Where(m => m.Visits > 0).ToList();
            var sumVisits = visited.Sum(m => m.Visits);
            var weighted = visited.Sum(m => Math.Max(m.Prior, 1e-30) * (2 * m.Value - 1)) / visited.Sum(m => Math.Max(m.Prior, 1e-30));
            var mixed = (raw + sumVisits * weighted) / (1 + sumVisits);
            var q = result.Moves.Select(m => m.Visits > 0 ? 2 * m.Value - 1 : mixed).ToArray();
            var (min, max) = (q.Min(), q.Max());
            var scale = (settings.CVisit + result.Moves.Max(m => m.Visits)) * settings.CScale / Math.Max(max - min, 1e-8);
            var logits = result.Moves.Select((m, i) => Math.Log(Math.Max(m.Prior, 1e-30)) + (q[i] - min) * scale).ToArray();
            var top = logits.Max();
            var expected = logits.Select(l => Math.Exp(l - top)).ToArray();
            var total = expected.Sum();
            for (var i = 0; i < n; i++) Assert.Equal(expected[i] / total, result.PolicyTarget[i], 6);
        }
        Assert.True(searches > 10);
    }

    /// <summary>
    /// Policy improvement: with no knowledge (uniform policy, value 0), wherever a Move wins on the spot and Gumbel
    /// considers it (all Moves are considered when there are at most m), the search plays a winning Move and the
    /// improved policy puts most of its mass on winning Moves, far above their uniform prior.
    /// </summary>
    [Fact]
    public void Gumbel_search_finds_immediate_wins_and_its_policy_target_prefers_them()
    {
        var found = 0;
        foreach (var observation in Positions(4, 300, every: 1))
        {
            var me = observation.Player;
            var legal = observation.LegalMoves();
            var winning = legal.Where(m =>
            {
                var s = observation.State.Clone();
                s.Apply(m);
                return s.IsOver && s.Result!.Winner == me;
            }).ToHashSet();
            if (winning.Count == 0 || winning.Count == legal.Count || legal.Count > 16) continue;
            found++;
            var result = new NeuralAgent(new NeuralSettings(NeuralSettings.Uniform, Simulations: 64), new UniformEvaluator(), (ulong)found).Search(observation);
            Assert.Contains(result.Best, winning);
            var winningMass = result.Moves.Select((m, i) => winning.Contains(m.Move) ? result.PolicyTarget[i] : 0).Sum();
            Assert.True(winningMass > 0.9, $"{winningMass} on {winning.Count} of {legal.Count} Moves");
            if (found == 12) break;
        }
        Assert.True(found >= 12, $"only {found} positions with an immediate win");
    }

    /// <summary>Same seed, same choice: Gumbel's noise comes from the Agent's own seeded stream.</summary>
    [Fact]
    public void Gumbel_choices_depend_only_on_the_seed_and_the_Observation()
    {
        string Log(ulong seed, string spec) => GameRunner.Play(new Ruleset(Promos: BothPromos), new RandomChanceSource(3),
            AgentSpecs.Create(spec, seed), new HeuristicAgent(HeuristicWeights.Balanced, 4)).WriteLog();
        var spec = $"neural:{Tiny},sims=32";
        Assert.Equal(Log(7, spec), Log(7, spec));
        Assert.NotEqual(Log(7, spec), Log(8, spec));

        foreach (var observation in Positions(5, 2, every: 7))
        {
            var first = ((NeuralAgent)AgentSpecs.Create(spec, 11)).Search(observation);
            var second = ((NeuralAgent)AgentSpecs.Create(spec, 11)).Search(observation);
            Assert.Equal(first.Best, second.Best);
            Assert.Equal(first.Moves, second.Moves);
            Assert.Equal(first.PolicyTarget, second.PolicyTarget);
            Assert.Equal(first.Line, second.Line);
        }
    }

    /// <summary>
    /// Given plain MCTS's own estimate (uniform policy, one random playout per leaf), Gumbel search at the training
    /// budget beats Random clearly. (It is weaker than chance-node MCTS at an equal count, ~25 %: Gumbel trusts its
    /// values and a uniform prior, and one playout is a very noisy value; see the ticket 21 notes.)
    /// </summary>
    [Fact]
    public void Gumbel_search_with_random_playouts_beats_Random()
    {
        var vsRandom = Match.Run(new MatchSettings("neural:playout,sims=64", "random", Pairs: 10));
        Assert.True(vsRandom.WinRate.Score >= 0.9, vsRandom.ToString());
    }

    /// <summary>
    /// Part 2: with no knowledge (uniform policy, value 0) classic search behaves like plain MCTS without playouts. Far
    /// from the end every root Move gets an even share of the simulations (PUCT with equal priors and equal values
    /// visits the least-visited Move), and where a Move wins on the spot it finds it, as chance-node MCTS does.
    /// </summary>
    [Fact]
    public void With_a_uniform_policy_and_zero_value_it_behaves_like_plain_MCTS()
    {
        // Opening positions: no game can end within reach, so visits spread evenly.
        foreach (var observation in Positions(3, 6, every: 4).Where(o => o.State.Age == 1).Take(12))
        {
            var result = new NeuralAgent(new NeuralSettings(NeuralSettings.Uniform, Simulations: 150, Mode: NeuralMode.Classic), new UniformEvaluator(), 9).Search(observation);
            Assert.Equal(150, result.Moves.Sum(m => m.Visits));
            Assert.InRange(result.Moves.Max(m => m.Visits) - result.Moves.Min(m => m.Visits), 0, 1);
            Assert.All(result.Moves, m => Assert.Equal(0.5, m.Value, 9));
            Assert.Equal(result.Moves.Select(m => m.VisitShare), result.PolicyTarget);
        }

        // Positions with an immediately winning Move: the uniform search chooses one, and MCTS a sure win.
        var found = 0;
        foreach (var observation in Positions(4, 300, every: 1))
        {
            var me = observation.Player;
            var winning = observation.LegalMoves().Where(m =>
            {
                var s = observation.State.Clone();
                s.Apply(m);
                return s.IsOver && s.Result!.Winner == me;
            }).ToHashSet();
            if (winning.Count == 0 || winning.Count == observation.LegalMoves().Count) continue;
            found++;
            var neural = new NeuralAgent(new NeuralSettings(NeuralSettings.Uniform, Simulations: 200, Mode: NeuralMode.Classic), new UniformEvaluator(), 1).Search(observation);
            // Margin 0: the Anchor's VP-margin bonus can prefer a big civilian win later to a military win now.
            var mcts = new MctsAgent(new MctsSettings(Simulations: 200, Margin: 0), 1).Search(observation);
            // MCTS finds a sure win too: this one, or another Move whose every playout wins.
            Assert.True(winning.Contains(mcts.Best) || mcts.Moves.First(m => m.Move == mcts.Best).Value == 1, string.Join(" / ", mcts.Moves));
            Assert.Contains(neural.Best, winning);
            Assert.Equal(1, neural.Moves.First(m => m.Move == neural.Best).Value, 9);
            if (found == 12) break;
        }
        Assert.True(found >= 12, $"only {found} positions with an immediate win");
    }

    /// <summary>
    /// Part 3: given plain MCTS's own estimate (a uniform policy and one random playout per leaf), classic search is
    /// about as strong as chance-node MCTS at the same simulation count (no margin term, which the neural value lacks).
    /// </summary>
    [Fact]
    public void With_random_playouts_as_the_value_it_plays_about_as_well_as_MCTS()
    {
        var report = Match.Run(new MatchSettings("neural:playout,sims=300,mode=classic", "mcts:sims=300,margin=0", Pairs: 40));
        Assert.InRange(report.WinRate.Score, 0.35, 0.65);
        var vsRandom = Match.Run(new MatchSettings("neural:playout,sims=100,mode=classic", "random", Pairs: 10));
        Assert.True(vsRandom.WinRate.Score >= 0.9, vsRandom.ToString());
    }

    [Fact]
    public void Its_choices_and_statistics_depend_only_on_its_seed_and_the_Observation()
    {
        string Log(ulong seed, string spec) => GameRunner.Play(new Ruleset(Promos: BothPromos), new RandomChanceSource(3),
            AgentSpecs.Create(spec, seed), new HeuristicAgent(HeuristicWeights.Balanced, 4)).WriteLog();
        var spec = $"neural:{Tiny},sims=40";
        Assert.Equal(Log(7, spec), Log(7, spec));
        var noisy = $"neural:{Tiny},sims=40,mode=classic,noise=0.25,temp=1";
        Assert.Equal(Log(7, noisy), Log(7, noisy));
        Assert.NotEqual(Log(7, noisy), Log(8, noisy));

        var observation = Positions(5, 1)[3];
        var first = ((NeuralAgent)AgentSpecs.Create(spec, 11)).Search(observation);
        var second = ((NeuralAgent)AgentSpecs.Create(spec, 11)).Search(observation);
        Assert.Equal(first.Moves, second.Moves);
        Assert.Equal(first.Line, second.Line);
    }

    /// <summary>No-peeking (spec A5): the same Observation with a different hidden Great Library draw gives the same search.</summary>
    [Fact]
    public void Same_Observation_with_different_hidden_cards_gives_the_same_result()
    {
        var rng = new Rng(21);
        var checkedDraws = 0;
        for (var game = 0; game < 400 && checkedDraws < 4; game++)
        {
            var chance = new RandomChanceSource(rng.NextUInt64());
            var s = GameState.New(new Ruleset(Promos: game % 2 == 0 ? Promos.None : BothPromos));
            while (!s.IsOver)
            {
                if (s.Step == StepKind.Reveal)
                {
                    if (s.SlotsToReveal == 0 && s.AwaitingLibraryDraw)
                    {
                        var drawer = s.PlayerToDecide;
                        var box = ProgressTokens.In(s.TokensSetAside).ToArray();
                        var a = s.Clone();
                        a.Apply(new LibraryReveal(drawer, box[..3]));
                        var b = s.Clone();
                        b.Apply(new LibraryReveal(drawer, box[^3..]));
                        var seed = rng.NextUInt64();
                        var resultA = ((NeuralAgent)AgentSpecs.Create($"neural:{Tiny},sims=60", seed)).Search(a.Observe(1 - drawer));
                        var resultB = ((NeuralAgent)AgentSpecs.Create($"neural:{Tiny},sims=60", seed)).Search(b.Observe(1 - drawer));
                        Assert.Equal(resultA.Moves, resultB.Moves);
                        Assert.Equal(resultA.Line, resultB.Line);
                        Assert.Equal(resultA.Outcome, resultB.Outcome);
                        checkedDraws++;
                    }
                    s.Apply(chance.Next(s));
                    continue;
                }
                var moves = s.LegalMoves();
                s.Apply(moves[rng.Next(moves.Count)]);
            }
        }
        Assert.True(checkedDraws >= 4, $"only {checkedDraws} Great Library draws");
    }

    /// <summary>The Agent output (spec A5): visit shares, priors, values with standard errors, the outcome distribution and the principal line.</summary>
    [Fact]
    public void It_reports_visits_priors_values_outcome_and_the_principal_line()
    {
        var agent = (NeuralAgent)AgentSpecs.Create($"neural:{Tiny},sims=100,mode=classic,noise=0.25,temp=1", 3);
        var searches = 0;
        foreach (var observation in Positions(6, 3, every: 2))
        {
            var move = agent.ChooseMove(observation);
            var result = agent.LastSearch!;
            searches++;
            Assert.Equal(move, result.Best);
            Assert.True(observation.State.IsLegal(move));
            Assert.Equal(observation.LegalMoves(), result.Moves.Select(m => m.Move));
            Assert.Equal(100, result.Moves.Sum(m => m.Visits));
            Assert.Equal(1, result.Moves.Sum(m => m.VisitShare), 9);
            Assert.Equal(1, result.Moves.Sum(m => m.Prior), 6);
            Assert.Equal(1, result.PolicyTarget.Sum(), 9);
            Assert.Equal(1, result.Outcome.Sum(), 9);
            Assert.Equal(NetworkEncoding.OutcomeClasses.Length, result.Outcome.Count);
            Assert.InRange(result.RootValue, 0, 1);
            Assert.All(result.Moves, m =>
            {
                Assert.InRange(m.Value, 0, 1);
                Assert.InRange(m.StdErr, 0, 0.5);
                Assert.InRange(m.Prior, 0, 1);
            });
            if (result.Line.Count > 0)
            {
                Assert.Equal(move, result.Line[0].Move);
                Assert.Equal(observation.Player, result.Line[0].Player);
            }
        }
        Assert.True(searches > 10);
    }
}
