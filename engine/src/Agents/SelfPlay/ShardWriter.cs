using System.Text.Json;
using SevenWondersDuel.Engine;

namespace SevenWondersDuel.Agents;

/// <summary>
/// Writes an encoded shard (spec A7, "Storage": the disposable training cache) from Game Records, in the format
/// <c>training/sevenwd/shards.py</c> reads: a directory holding <c>shard.json</c>
/// <c>{"format":"7wd-shard","encoding_version":1,"positions":N}</c>, <c>input.npy</c> float16 [N, input size],
/// the CSR sparse policies <c>policy_offset.npy</c> int64 [N+1], <c>policy_index.npy</c> int16 [M] and
/// <c>policy_prob.npy</c> float32 [M], <c>value.npy</c> float32 [N] and <c>outcome.npy</c> int8 [N].
/// <para>
/// Each searched decision is one position, in record order then Move Log order. The shard is re-derived from the
/// records by replaying each Move Log, so it can be rebuilt under a new Encoding version: the input is the Encoding of
/// the deciding player's Observation (exactly what the search's root evaluation saw), every legal Move is listed with
/// its target (the list doubles as the legal mask), the value is the result from the decider's view (+1/0/−1) and the
/// outcome class likewise. The directory appears atomically: it is written next to its final name, then renamed.
/// </para>
/// </summary>
public static class ShardWriter
{
    public const string Format = "7wd-shard";
    public const string HeaderName = "shard.json";

    /// <summary>Writes the shard of <paramref name="records"/> to <paramref name="directory"/> (replacing one there); returns its positions.</summary>
    public static int Write(string directory, IReadOnlyList<GameRecord> records)
    {
        var full = Path.GetFullPath(directory);
        var temp = full + ".tmp";
        if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
        Directory.CreateDirectory(temp);

        var positions = records.Sum(r => r.Searched.Count);
        var offsets = new long[positions + 1];
        var indices = new List<short>(positions * 16);
        var probs = new List<float>(positions * 16);
        var values = new float[positions];
        var outcomes = new sbyte[positions];
        var input = new float[NetworkEncoding.InputSize];
        var half = new Half[NetworkEncoding.InputSize];
        var moves = new Move[GameState.MaxMoves];
        var row = 0;
        using (var inputs = Npy.Create<Half>(Path.Combine(temp, "input.npy"), positions, NetworkEncoding.InputSize))
        {
            foreach (var record in records)
            {
                var parsed = MoveLog.Parse(record.Log);
                var result = parsed.State.Result ?? throw new FormatException($"Game {record.Game}: the Move Log does not finish the game");
                var game = new Game(parsed.Rules);
                var next = 0;
                for (var e = 0; e < parsed.Log.Count; e++)
                {
                    var entry = parsed.Log[e];
                    if (next < record.Searched.Count && record.Searched[next].Entry == e)
                    {
                        var decision = record.Searched[next++];
                        if (entry is not MoveEntry m || game.State.Step != StepKind.Decision)
                            throw new FormatException($"Game {record.Game}: log entry {e} is not a decision");
                        var observation = game.State.Observe(m.Player);
                        var n = observation.LegalMoves(moves);
                        if (decision.Target.Length != n)
                            throw new FormatException($"Game {record.Game}, entry {e}: {decision.Target.Length} targets for {n} legal Moves");
                        NetworkEncoding.Encode(observation, input);
                        for (var i = 0; i < input.Length; i++) half[i] = (Half)input[i];
                        inputs.Write(half);
                        for (var i = 0; i < n; i++)
                        {
                            indices.Add((short)NetworkEncoding.PolicyIndex(moves[i]));
                            probs.Add(decision.Target[i]);
                        }
                        offsets[row + 1] = indices.Count;
                        values[row] = NetworkEncoding.ValueTarget(result, m.Player);
                        outcomes[row] = (sbyte)NetworkEncoding.OutcomeClass(result, m.Player);
                        row++;
                    }
                    game.Apply(entry);
                }
                if (next != record.Searched.Count)
                    throw new FormatException($"Game {record.Game}: searched decisions out of order or past the end of the Move Log");
            }
            inputs.Complete();
        }

        Npy.Save<long>(Path.Combine(temp, "policy_offset.npy"), offsets);
        Npy.Save<short>(Path.Combine(temp, "policy_index.npy"), indices.ToArray());
        Npy.Save<float>(Path.Combine(temp, "policy_prob.npy"), probs.ToArray());
        Npy.Save<float>(Path.Combine(temp, "value.npy"), values);
        Npy.Save<sbyte>(Path.Combine(temp, "outcome.npy"), outcomes);
        File.WriteAllText(Path.Combine(temp, HeaderName), JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["format"] = Format,
            ["encoding_version"] = NetworkEncoding.Version,
            ["positions"] = positions,
        }, new JsonSerializerOptions { WriteIndented = true }) + "\n");

        if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
        Directory.Move(temp, full);
        return positions;
    }
}
