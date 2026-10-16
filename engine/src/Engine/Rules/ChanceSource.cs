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
        if (state.AwaitingTokens)
        {
            // 5 of the game's token pool (10 base tokens, 13 with Pantheon), uniformly.
            var pool = ProgressTokens.In(state.TokenPool).ToArray();
            return new TokenReveal(Pick(pool.Length, ProgressTokens.OnBoard).Select(i => pool[i]).ToArray());
        }
        if (state.AwaitingDraftOffer)
        {
            // 4 of the Wonders nobody has seen, uniformly (the shuffled Wonder pile).
            var unseen = GameState.WondersIn(state.UnseenWonders).ToArray();
            return new DraftReveal(Pick(unseen.Length, GameState.DraftOfferSize).Select(i => unseen[i]).ToArray());
        }
        if (state.AwaitingGuildPattern)
            return new GuildPatternReveal(Pick(GameState.SlotCount, GameState.GuildsPerGame), state.TempleBacks);
        if (state.SlotsToReveal == 0 && state.AwaitingLibraryDraw)
        {
            // 3 of the tokens left in the box, uniformly; the flips of uncovered cards came first.
            var box = ProgressTokens.In(state.TokensSetAside).ToArray();
            return new LibraryReveal(state.PlayerToDecide,
                Pick(box.Length, GameState.LibraryDrawSize).Select(i => box[i]).ToArray());
        }
        if (state.SlotsToReveal == 0 && state.AwaitingReturnedCards)
        {
            // The 3 removed cards are exchangeable with the face-down slots: 3 of the Age's unseen cards, uniformly.
            var pool = state.UnseenPool(state.Age);
            return new ReturnedReveal(state.Age,
                Pick(pool.Count, GameState.ReturnedCardCount).Select(pool.ElementAt).ToArray());
        }
        // An expansion's Reveals (Pantheon's setup and draws) come once the flips are revealed.
        if (state.SlotsToReveal == 0 && state.Module is { } module && module.AwaitingReveal(state))
            return module.NextReveal(state, _rng);

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
    int[] Pick(int n, int count) => Pick(_rng, n, count);

    /// <summary><see cref="Pick(int, int)"/> with any <see cref="Rng"/> (expansion Reveals draw the same way).</summary>
    internal static int[] Pick(Rng rng, int n, int count)
    {
        Span<int> values = stackalloc int[n];
        for (var i = 0; i < values.Length; i++) values[i] = i;
        var picked = new int[count];
        for (var i = 0; i < picked.Length; i++)
        {
            var j = i + rng.Next(values.Length - i);
            (values[i], values[j]) = (values[j], values[i]);
            picked[i] = values[i];
        }
        Array.Sort(picked);
        return picked;
    }
}
