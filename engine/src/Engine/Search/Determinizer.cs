namespace SevenWondersDuel.Engine;

/// <summary>
/// The uniform Determinizer (spec A4): turns an <see cref="Observation"/> into one sampled Game State consistent with
/// it, and then supplies every Reveal that sampled world needs, filling each unknown uniformly from what the observing
/// player has not seen. All search uses it, so search sees only the Observation (spec A5).
/// <para>
/// The Game State never stores hidden identities (ADR 0002): face-down cards, the cards removed at setup, the tokens
/// left in the box and the Wonders not yet offered exist only as pools of unseen things. So a sampled world is the
/// Observation's state with the one private thing it can hold filled in, an opponent's
/// <see cref="GameState.LibraryDrawHidden"/> draw (3 tokens of <see cref="GameState.TokensSetAside"/>), and an
/// expansion's (Pantheon: each hidden Divinity and deck card a uniform unseen card of its Mythology), plus this
/// object as the world's Chance Source: each later Reveal (a flipped card, an Age III slot behind a Guild back, a draft
/// offer, the Statue of Liberty's returned cards, a Great Library draw) is drawn uniformly from the matching unseen pool
/// at the moment it happens. Because unseen cards are exchangeable, that is the same distribution as dealing every
/// unknown uniformly up front, without letting search plan around a card before it is revealed.
/// </para>
/// <para>
/// Seeded and reseedable: <see cref="Reseed"/> with the same seed gives the same world and the same Reveals, which
/// search uses to compare Moves on common luck.
/// </para>
/// </summary>
public sealed class Determinizer : IChanceSource
{
    Rng _rng = null!;
    RandomChanceSource _chance = null!;

    public Determinizer(ulong seed) => Reseed(seed);

    /// <summary>Restarts the Determinizer: the same seed gives the same samples and Reveals again.</summary>
    public void Reseed(ulong seed)
    {
        _rng = new Rng(seed);
        _chance = new RandomChanceSource(_rng.NextUInt64());
    }

    /// <summary>A new sampled Game State consistent with <paramref name="observation"/>.</summary>
    public GameState Sample(Observation observation)
    {
        var world = observation.State.Clone();
        Fill(world);
        return world;
    }

    /// <summary>Overwrites <paramref name="world"/> with a sampled Game State consistent with <paramref name="observation"/>, without allocating a state.</summary>
    public void Sample(Observation observation, GameState world)
    {
        world.CopyFrom(observation.State);
        Fill(world);
    }

    /// <summary>The next Reveal of a sampled world: uniform over what nobody in it has seen.</summary>
    public Reveal Next(GameState state) => _chance.Next(state);

    void Fill(GameState world)
    {
        if (world.LibraryDrawHidden) FillLibraryDraw(world);
        // An expansion's private information (Pantheon: hidden Divinities and deck cards). None in a base game.
        world.Module?.Determinize(world, _rng);
    }

    void FillLibraryDraw(GameState world)
    {
        // The opponent's private Great Library draw: 3 of the tokens left in the box, uniformly.
        Span<int> box = stackalloc int[ProgressTokens.Count];
        var n = 0;
        foreach (var token in ProgressTokens.In(world.TokensSetAside)) box[n++] = token;
        ushort draw = 0;
        for (var i = 0; i < GameState.LibraryDrawSize; i++)
        {
            var j = i + _rng.Next(n - i);
            (box[i], box[j]) = (box[j], box[i]);
            draw |= (ushort)(1 << box[i]);
        }
        world.FillLibraryDraw(draw);
    }
}
