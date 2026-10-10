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
        if (state.AwaitingTokens) return new TokenReveal(Pick(ProgressTokens.Count, ProgressTokens.OnBoard));
        if (state.AwaitingDraftOffer)
        {
            // 4 of the Wonders nobody has seen, uniformly (the shuffled Wonder pile).
            var unseen = GameState.WondersIn(state.UnseenWonders).ToArray();
            return new DraftReveal(Pick(unseen.Length, GameState.DraftOfferSize).Select(i => unseen[i]).ToArray());
        }
        if (state.AwaitingGuildPattern) return new GuildPatternReveal(Pick(GameState.SlotCount, GameState.GuildsPerGame));

        var slots = state.SlotsToReveal;
        var named = CardSet.Empty;
        var cards = new List<(int Slot, int Card)>();
        for (var slot = 0; slot < GameState.SlotCount; slot++)
        {
            if ((slots & 1u << slot) == 0) continue;
            var pool = state.UnseenPoolFor(slot) & ~named;
            var card = pool.ElementAt(_rng.Next(pool.Count));
            named = named.With(card);
            cards.Add((slot, card));
        }
        return new SlotReveal(state.Age, state.AgeUntouched, [.. cards]);
    }

    /// <summary>
    /// <paramref name="count"/> distinct values below <paramref name="n"/>, uniformly, sorted: the Progress tokens
    /// set out, or the Guild back pattern (the 20 shuffled Age III cards are 17 Age cards and 3 Guilds, so every
    /// set of 3 slots is equally likely to hold the Guilds).
    /// </summary>
    int[] Pick(int n, int count)
    {
        Span<int> values = stackalloc int[n];
        for (var i = 0; i < values.Length; i++) values[i] = i;
        var picked = new int[count];
        for (var i = 0; i < picked.Length; i++)
        {
            var j = i + _rng.Next(values.Length - i);
            (values[i], values[j]) = (values[j], values[i]);
            picked[i] = values[i];
        }
        Array.Sort(picked);
        return picked;
    }
}
