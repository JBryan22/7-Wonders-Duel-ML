namespace SevenWondersDuel.Engine;

/// <summary>
/// One Encoding version as a value: what a loaded network declares (its <c>encoding_version</c>) and what the search,
/// Self-Play's shards and the manifest then use. <see cref="V1"/> is <see cref="NetworkEncoding"/> (base game only);
/// <see cref="V2"/> is <see cref="NetworkEncodingV2"/> (v1 plus appended Pantheon blocks; base games and Pantheon).
/// The version is chosen by the network, never by the game: a v1 network keeps encoding base games exactly as before,
/// and a Pantheon game with a v1 network is refused (<see cref="CheckSupports"/>).
/// </summary>
public sealed class EncodingVersion
{
    public static readonly EncodingVersion V1 = new(NetworkEncoding.Version);
    public static readonly EncodingVersion V2 = new(NetworkEncodingV2.Version);

    /// <summary>Every version this build can encode, oldest first.</summary>
    public static IReadOnlyList<EncodingVersion> All { get; } = [V1, V2];

    EncodingVersion(int number) => Number = number;

    /// <summary>The version number (the manifest's <c>version</c>, the model's <c>encoding_version</c>).</summary>
    public int Number { get; }

    /// <summary>The version numbered <paramref name="number"/>, or null if this build has none.</summary>
    public static EncodingVersion? TryOf(int number) => All.FirstOrDefault(v => v.Number == number);

    public static EncodingVersion Of(int number) => TryOf(number)
        ?? throw new ArgumentOutOfRangeException(nameof(number), $"No Encoding v{number} (this build has v{string.Join(", v", All.Select(v => v.Number))})");

    /// <summary>The oldest version that can encode a game of these rules: v1 for the base game, v2 with Pantheon.</summary>
    public static EncodingVersion For(Ruleset rules) => rules.Expansions == Expansions.None ? V1 : V2;

    /// <summary>True if this version can encode a game of these rules (promos always; Pantheon from v2).</summary>
    public bool Supports(Ruleset rules) => rules.Expansions == Expansions.None || Number >= 2 && rules.Expansions == Expansions.Pantheon;

    /// <summary>Throws <see cref="NotSupportedException"/> unless this version can encode a game of these rules.</summary>
    public void CheckSupports(Ruleset rules, string? who = null)
    {
        if (!Supports(rules))
            throw new NotSupportedException(
                $"{who ?? "This network"} uses Encoding v{Number}, which cannot encode a game with {rules.Expansions}: "
                + $"use an Encoding v{For(rules).Number} network (made from a base network by surgery)");
    }

    bool IsV1 => Number == 1;

    public int InputSize => IsV1 ? NetworkEncoding.InputSize : NetworkEncodingV2.InputSize;
    public int PolicySize => IsV1 ? NetworkEncoding.PolicySize : NetworkEncodingV2.PolicySize;
    public EncodingBlock[] InputBlocks => IsV1 ? NetworkEncoding.InputBlocks : NetworkEncodingV2.InputBlocks;
    public string[] InputNames => IsV1 ? NetworkEncoding.InputNames : NetworkEncodingV2.InputNames;
    public EncodingBlock[] PolicyBlocks => IsV1 ? NetworkEncoding.PolicyBlocks : NetworkEncodingV2.PolicyBlocks;
    public string[] PolicyNames => IsV1 ? NetworkEncoding.PolicyNames : NetworkEncodingV2.PolicyNames;
    /// <summary>The outcome head's classes: the same in every version.</summary>
    public string[] OutcomeClasses => NetworkEncoding.OutcomeClasses;

    /// <summary>Where this version's manifest is committed, relative to the repository root.</summary>
    public string ManifestPath => $"data/encoding/v{Number}.json";

    public void Encode(GameState s, int me, Span<float> input)
    {
        if (IsV1) NetworkEncoding.Encode(s, me, input);
        else NetworkEncodingV2.Encode(s, me, input);
    }

    public void Encode(Observation observation, Span<float> input) => Encode(observation.State, observation.Player, input);

    public float[] Encode(Observation observation)
    {
        var input = new float[InputSize];
        Encode(observation, input);
        return input;
    }

    public int PolicyIndex(Move m) => IsV1 ? NetworkEncoding.PolicyIndex(m) : NetworkEncodingV2.PolicyIndex(m);

    public Move MoveAt(int index) => IsV1 ? NetworkEncoding.MoveAt(index) : NetworkEncodingV2.MoveAt(index);

    public int LegalMask(Observation observation, Span<bool> mask) =>
        IsV1 ? NetworkEncoding.LegalMask(observation, mask) : NetworkEncodingV2.LegalMask(observation, mask);

    public override string ToString() => $"Encoding v{Number}";
}
