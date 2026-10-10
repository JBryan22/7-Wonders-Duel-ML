namespace SevenWondersDuel.Engine;

/// <summary>
/// The part of the Game State one player is entitled to see (spec A4): what every Agent decides from (honesty
/// rule, spec A1). Made by <see cref="GameState.Observe"/>. It has no Move Log: the Game State is Markov.
/// <para>
/// <see cref="State"/> is a private copy with the Reveals this player is not in the audience of hidden, so reading
/// any of it is fair, and changing it never affects the real game. It still holds every unknown as a pool
/// (unseen cards per Age and Guilds, <see cref="GameState.UnseenWonders"/> during the draft, the tokens left in the
/// box, an opponent's <see cref="GameState.LibraryDrawHidden"/> draw): the Determinizer fills those to sample a
/// Game State consistent with the Observation.
/// </para>
/// </summary>
public sealed class Observation
{
    internal Observation(GameState state, int player)
    {
        State = state;
        Player = player;
    }

    /// <summary>The observing player.</summary>
    public int Player { get; }

    /// <summary>The Game State as this player sees it (a copy; see the class remarks).</summary>
    public GameState State { get; }

    /// <summary>The legal Moves of the decision at the front of the queue, as this player can tell them.</summary>
    public int LegalMoves(Span<Move> buffer) => State.LegalMoves(buffer);

    public List<Move> LegalMoves() => State.LegalMoves();

    /// <summary>True if both Observations are the same player's view of the same position.</summary>
    public bool SameAs(Observation other) => Player == other.Player && State.SameAs(other.State);

    public override string ToString() => $"Observation of P{Player + 1}\n{State}";
}
