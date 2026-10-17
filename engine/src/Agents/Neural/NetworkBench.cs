using System.Diagnostics;
using SevenWondersDuel.Engine;

namespace SevenWondersDuel.Agents;

/// <summary>One batch size's inference speed: milliseconds per network call and positions per second.</summary>
public readonly record struct NetworkBenchResult(int Batch, int Calls, double MsPerCall, double PositionsPerSecond)
{
    public override string ToString() =>
        $"batch {Batch,4}: {MsPerCall,8:0.000} ms per call, {PositionsPerSecond,10:N0} positions/s ({Calls:N0} calls)";
}

/// <summary>
/// <c>7wd netbench</c>: how fast a network runs on this machine at each batch size, on positions encoded from random
/// games (calibration input for Self-Play and Match settings).
/// </summary>
public static class NetworkBench
{
    public static readonly int[] DefaultBatches = [1, 4, 16, 64, 256, 1024];

    public static IEnumerable<NetworkBenchResult> Run(NeuralNetwork network, IEnumerable<int> batches, TimeSpan perBatch)
    {
        var sizes = batches.ToArray();
        var max = sizes.Max();
        var encoding = network.Encoding;
        var input = new float[max * encoding.InputSize];
        var filled = 0;
        var rng = new Rng(1);
        var chance = new RandomChanceSource(2);
        var moves = new Move[GameState.MaxMoves];
        while (filled < max)
        {
            var s = GameState.New(Ruleset.Default);
            while (!s.IsOver && filled < max)
            {
                if (s.Step == StepKind.Reveal) { s.Apply(chance.Next(s)); continue; }
                encoding.Encode(s.Observe(s.PlayerToDecide), input.AsSpan(filled++ * encoding.InputSize, encoding.InputSize));
                s.Apply(moves[rng.Next(s.LegalMoves(moves))]);
            }
        }
        var policy = new float[max * encoding.PolicySize];
        var value = new float[max];
        var outcome = new float[max * NetworkEncoding.OutcomeClasses.Length];
        foreach (var batch in sizes)
        {
            for (var i = 0; i < 3; i++) network.Run(input, batch, policy, value, outcome); // warm-up
            var clock = Stopwatch.StartNew();
            var calls = 0;
            do
            {
                network.Run(input, batch, policy, value, outcome);
                calls++;
            } while (clock.Elapsed < perBatch);
            var seconds = clock.Elapsed.TotalSeconds;
            yield return new NetworkBenchResult(batch, calls, seconds * 1000 / calls, batch * calls / seconds);
        }
    }
}
