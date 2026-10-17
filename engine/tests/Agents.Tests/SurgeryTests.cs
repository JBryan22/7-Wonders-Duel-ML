using SevenWondersDuel.Engine;

namespace SevenWondersDuel.Agents.Tests;

/// <summary>
/// Network surgery (spec-pantheon P-A7, ticket 10), checked through ONNX in C#: surgery-child.onnx is the Encoding v2
/// copy of surgery-parent.onnx made by <c>sevenwd-train surgery</c> (training/sevenwd/test_models.py; the -no-derived
/// pair drops the derived inputs). On real base-game positions, encoded by each network's own version, the child's
/// value, outcome and v1 policy logits equal the parent's (to float32 rounding: a wider matrix product may add the same
/// numbers in another order), and the child plays Pantheon Matches.
/// </summary>
public class SurgeryTests
{
    static string Model(string name) => Path.Combine(AppContext.BaseDirectory, "Models", name);

    const float RelTol = 1e-4f, AbsTol = 1e-5f;

    static void AssertClose(float expected, float actual, string what)
    {
        if (Math.Abs(expected - actual) > AbsTol + RelTol * Math.Abs(expected))
            Assert.Fail($"{what}: parent {expected}, operated {actual}");
    }

    [Theory]
    [InlineData("")]
    [InlineData("-no-derived")]
    public void In_base_game_positions_the_operated_network_answers_like_its_parent(string suffix)
    {
        using var parent = NeuralNetwork.Load(Model($"surgery-parent{suffix}.onnx"), InferenceDevice.Cpu);
        using var child = NeuralNetwork.Load(Model($"surgery-child{suffix}.onnx"), InferenceDevice.Cpu);
        Assert.Same(EncodingVersion.V1, parent.Encoding);
        Assert.Same(EncodingVersion.V2, child.Encoding);
        Assert.Equal(parent.DroppedInputs, child.DroppedInputs);

        var positions = PantheonNeuralTests.Positions(Ruleset.Default, 10, 6, 3);
        positions.AddRange(PantheonNeuralTests.Positions(new Ruleset(Promos: Promos.Messe | Promos.Liberty), 11, 4, 3));
        var n = positions.Count;
        Assert.True(n > 100);
        int v1In = NetworkEncoding.InputSize, v2In = NetworkEncodingV2.InputSize;
        int v1Pol = NetworkEncoding.PolicySize, v2Pol = NetworkEncodingV2.PolicySize, classes = NetworkEncoding.OutcomeClasses.Length;
        var x1 = new float[n * v1In];
        var x2 = new float[n * v2In];
        for (var i = 0; i < n; i++)
        {
            EncodingVersion.V1.Encode(positions[i], x1.AsSpan(i * v1In, v1In));
            EncodingVersion.V2.Encode(positions[i], x2.AsSpan(i * v2In, v2In));
        }
        float[] p1 = new float[n * v1Pol], v1 = new float[n], o1 = new float[n * classes];
        float[] p2 = new float[n * v2Pol], v2 = new float[n], o2 = new float[n * classes];
        parent.Run(x1, n, p1, v1, o1);
        child.Run(x2, n, p2, v2, o2);

        var spread = 0f;
        for (var i = 0; i < n; i++)
        {
            AssertClose(v1[i], v2[i], $"value {i}");
            for (var k = 0; k < classes; k++) AssertClose(o1[i * classes + k], o2[i * classes + k], $"outcome {i}.{k}");
            for (var j = 0; j < v1Pol; j++)
            {
                AssertClose(p1[i * v1Pol + j], p2[i * v2Pol + j], $"policy {i}.{j}");
                spread = Math.Max(spread, Math.Abs(p1[i * v1Pol + j]));
            }
            // The new Moves start at 0 (uniform); in a base game they are masked anyway.
            for (var j = v1Pol; j < v2Pol; j++) Assert.Equal(0f, p2[i * v2Pol + j]);
        }
        Assert.True(spread > 0.1f, "a real network, not a zero one");
    }

    [Fact]
    public void The_operated_network_plays_Pantheon_Matches()
    {
        var spec = $"neural:{Model("surgery-child.onnx")},sims=16";
        AgentSpecs.Validate(spec, new Ruleset(Expansions: Expansions.Pantheon));
        Assert.Throws<NotSupportedException>(() =>
            AgentSpecs.Validate($"neural:{Model("surgery-parent.onnx")}", new Ruleset(Expansions: Expansions.Pantheon)));
        var report = Match.Run(new MatchSettings(spec, "random", Pairs: 2, Expansions: Expansions.Pantheon));
        Assert.True(report.Complete);
        Assert.All(report.Games, g => Assert.True(g.MovesA > 10));
    }
}
