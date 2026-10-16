namespace SevenWondersDuel.Engine;

/// <summary>
/// The Pantheon expansion (spec-pantheon P-A3, P-A4), switched on by <c>expansion=pantheon</c>. So far only the seam:
/// its hooks do nothing yet, so a Pantheon game plays like the base game. The rules arrive with the later Pantheon
/// tickets (setup and tokens, activation, the Divinities). Its data is <see cref="Data.PantheonData"/>.
/// </summary>
internal sealed class PantheonModule : ExpansionModule
{
    public static readonly PantheonModule Instance = new();

    PantheonModule() { }

    public override ExpansionState NewState() => new PantheonState();
}

/// <summary>
/// Pantheon's block of Game State (spec-pantheon P-A3): the Pantheon spaces, Mythology decks, tokens on structure
/// slots and held, Astarte's coins, the Snake, Minerva and Enki's tokens. Empty for now: the fields arrive with the
/// rules that use them.
/// </summary>
public sealed class PantheonState : ExpansionState
{
    public override ExpansionState Clone()
    {
        var copy = new PantheonState();
        copy.CopyFrom(this);
        return copy;
    }

    public override void CopyFrom(ExpansionState other)
    {
        if (other is not PantheonState) throw new ArgumentException("Not a Pantheon state", nameof(other));
    }

    public override bool SameAs(ExpansionState other) => other is PantheonState;

    public override void Write(BinaryWriter w) { }
}
