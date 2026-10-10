namespace SevenWondersDuel.Engine;

/// <summary>Whatever supplies Reveals: a seeded random draw in Self-Play and search, or the human / TTS in Advisor mode.</summary>
public interface IChanceSource
{
    /// <summary>The Reveal the state is waiting for. Only called when <see cref="GameState.Step"/> is <see cref="StepKind.Reveal"/>.</summary>
    Reveal Next(GameState state);
}

/// <summary>Draws every Reveal uniformly from what nobody has seen yet, using our own seeded <see cref="Rng"/>.</summary>
public sealed class RandomChanceSource(ulong seed) : IChanceSource
{
    readonly Rng _rng = new(seed);

    public Reveal Next(GameState state)
    {
        if (state.Step != StepKind.Reveal) throw new InvalidOperationException("The game is not waiting for a Reveal");
        if (state.FirstPlayer < 0) return new FirstPlayerReveal(_rng.Next(2));

        var slots = state.SlotsToReveal;
        var pool = state.UnseenPool(state.Age);
        var cards = new List<(int Slot, int Card)>();
        for (var slot = 0; slot < GameState.SlotCount; slot++)
        {
            if ((slots & 1u << slot) == 0) continue;
            var card = pool.ElementAt(_rng.Next(pool.Count));
            pool = pool.Without(card);
            cards.Add((slot, card));
        }
        return new SlotReveal(state.Age, state.AgeUntouched, [.. cards]);
    }
}
