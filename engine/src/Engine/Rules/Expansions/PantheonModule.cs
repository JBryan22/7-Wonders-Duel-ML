using SevenWondersDuel.Engine.Data;

namespace SevenWondersDuel.Engine;

/// <summary>
/// The Pantheon expansion (spec-pantheon P-A3, P-A4), switched on by <c>expansion=pantheon</c>. Its data is
/// <see cref="PantheonData"/>, its state <see cref="PantheonState"/>.
/// <para>
/// So far (tickets 02-04): the bigger pools (16 Wonders with both promos, 13 Progress tokens), the Age I Mythology
/// tokens and the Divinity draws they cause, the Age II setup (the Gate, the Pantheon and Enki Reveals, the Offering
/// tokens), Age III's Grand Temples in place of the Guilds, the 3 new Progress tokens, and activating the Pantheon
/// (cost, the Sanctuary, Offering tokens, Astarte's coins) with the effects that need no decision (Ishtar, Astarte,
/// Tanit, Aphrodite, Mars) and those with one (Enki, Nisaba, Baal, Hades, Zeus, Anubis, Isis, Ra). Minerva, Neptune
/// and the Gate are activated and paid for, but their effects come in ticket 05 (until then they do nothing, like an
/// impossible effect).
/// </para>
/// </summary>
internal sealed class PantheonModule : ExpansionModule
{
    public static readonly PantheonModule Instance = new();

    const int Mythologies = 5;
    /// <summary>VP per Mythology or Offering token still held, with Mysticism.</summary>
    public const int MysticismPointsPerToken = 2;
    /// <summary>What Engineering makes a card with a chain symbol cost.</summary>
    public const int EngineeringCost = 1;

    static readonly int Mysticism = PantheonData.TokenIndex("mysticism");
    static readonly int Poliorcetics = PantheonData.TokenIndex("poliorcetics");
    static readonly int Engineering = PantheonData.TokenIndex("engineering");
    static readonly int Enki = PantheonData.DivinityIndex("enki");
    static readonly int Nisaba = PantheonData.DivinityIndex("nisaba");
    static readonly int Baal = PantheonData.DivinityIndex("baal");
    static readonly int Hades = PantheonData.DivinityIndex("hades");
    static readonly int Zeus = PantheonData.DivinityIndex("zeus");
    static readonly int Anubis = PantheonData.DivinityIndex("anubis");
    static readonly int Isis = PantheonData.DivinityIndex("isis");
    static readonly int Ra = PantheonData.DivinityIndex("ra");
    static readonly CardSet BrownAndGrey = CardSets.OfColor[(int)CardColor.Brown] | CardSets.OfColor[(int)CardColor.Grey];
    static readonly CardSet Green = CardSets.OfColor[(int)CardColor.Green];
    static readonly int Sanctuary = PantheonData.WonderIndex("sanctuary");
    /// <summary>What the Sanctuary takes off its owner's activation costs.</summary>
    public const int SanctuaryDiscount = 2;
    static readonly ushort Wonders = (ushort)PantheonData.Wonders.Aggregate(0, (m, w) => m | 1 << w.Index);
    static readonly ushort Tokens = (ushort)PantheonData.Tokens.Aggregate(0, (m, t) => m | 1 << t.Index);
    /// <summary>Per Mythology: its 3 Divinities, one bit per Divinity index.</summary>
    static readonly ushort[] DeckOf = PantheonData.Mythologies
        .Select(m => (ushort)m.Divinities.Aggregate(0, (mask, d) => mask | 1 << d)).ToArray();

    PantheonModule() { }

    static PantheonState P(GameState s) => s.Pantheon!;

    public override ExpansionState NewState() => new PantheonState();
    public override ushort ExtraWonders => Wonders;
    public override ushort ExtraTokens => Tokens;
    public override CardSet? AgeThreeBacks => PantheonData.TempleSet;

    /// <summary>Enki's 2 tokens, and box tokens a player took (the Great Library's pick), are out of the box.</summary>
    public override ushort TokensOutOfBox(GameState s) =>
        (ushort)(P(s).EnkiTokens | ((s.Tokens(0) | s.Tokens(1)) & ~s.TokensSetOut));

    // ---------------------------------------------------------------- setup

    public override void Setup(GameState s, int age)
    {
        var p = P(s);
        Array.Fill(p.SlotTokens, (sbyte)PantheonState.NoToken);
        switch (age)
        {
            case 1:
                p.Awaiting |= PantheonState.SetupStep.MythologyTokens;
                break;
            case 2:
                // The Gate goes face up in the empty space (all 5 Mythology tokens are taken in Age I, so there is one).
                var empty = Array.IndexOf(p.Spaces, (sbyte)PantheonState.EmptySpace);
                if (empty >= 0) p.Spaces[empty] = (sbyte)PantheonData.Gate;
                if (p.FaceDown != 0) p.Awaiting |= PantheonState.SetupStep.Pantheon;
                p.Awaiting |= PantheonState.SetupStep.Offerings;
                break;
        }
    }

    // ---------------------------------------------------------------- Reveals

    static bool AwaitingDraw(GameState s) =>
        s.PendingCount > 0 && s.Pending(0).Kind == DecisionKind.PlaceDivinity && !P(s).HasDraw;

    public override bool AwaitingReveal(GameState s) => P(s).Awaiting != 0 || AwaitingDraw(s) || P(s).AwaitingZeusDiscard;

    public override bool TryApply(GameState s, Reveal reveal)
    {
        if (reveal is not (MythologyTokensReveal or DivinityDrawReveal or PantheonReveal or OfferingsReveal or EnkiReveal
            or ZeusDiscardReveal))
            return false;
        if (s.SlotsToReveal != 0) throw new InvalidOperationException("The structure's cards are revealed first");
        switch (reveal)
        {
            case MythologyTokensReveal r: ApplyMythologyTokens(s, r); break;
            case DivinityDrawReveal r: ApplyDraw(s, r); break;
            case PantheonReveal r: ApplyPantheon(s, r); break;
            case OfferingsReveal r: ApplyOfferings(s, r); break;
            case EnkiReveal r: ApplyEnki(s, r); break;
            case ZeusDiscardReveal r: ApplyZeusDiscard(s, r); break;
        }
        return true;
    }

    static void ApplyMythologyTokens(GameState s, MythologyTokensReveal r)
    {
        var p = P(s);
        if (!p.AwaitingMythologyTokens) throw new InvalidOperationException("The Mythology tokens are placed once, at Age I setup");
        if (r.Tokens.Length != PantheonData.MythologySlots.Length
            || !r.Tokens.Select(t => t.Slot).Order().SequenceEqual(PantheonData.MythologySlots.Order()))
            throw new InvalidOperationException($"The Mythology tokens lie on Age I slots {SlotNames(1, PantheonData.MythologySlots)}");
        foreach (var group in r.Tokens.GroupBy(t => t.Mythology))
        {
            if ((int)group.Key is < 0 or >= Mythologies) throw new InvalidOperationException($"Unknown Mythology {(int)group.Key}");
            if (group.Count() > PantheonData.MythologyDef(group.Key).Tokens)
                throw new InvalidOperationException($"There are only {PantheonData.MythologyDef(group.Key).Tokens} {PantheonData.MythologyDef(group.Key).Id} tokens");
        }
        foreach (var (slot, m) in r.Tokens) p.SlotTokens[slot] = (sbyte)m;
        p.Awaiting &= ~PantheonState.SetupStep.MythologyTokens;
    }

    static void ApplyDraw(GameState s, DivinityDrawReveal r)
    {
        var p = P(s);
        if (!AwaitingDraw(s)) throw new InvalidOperationException("No Mythology token is waiting for its Divinities");
        var decision = s.Pending(0);
        var m = (Mythology)decision.Wonder;
        if (r.Player != decision.Player)
            throw new InvalidOperationException($"The draw is P{decision.Player + 1}'s, not P{r.Player + 1}'s");
        if (r.Mythology != m)
            throw new InvalidOperationException($"P{decision.Player + 1} draws from the {PantheonData.MythologyDef(m).Id} deck");
        if (r.Divinities.Length != 2 || r.Divinities[0] == r.Divinities[1])
            throw new InvalidOperationException("A Mythology token draws 2 different Divinities");
        var deck = (int)m * PantheonData.DeckSize;
        if (p.DeckCount[(int)m] < 2) throw new InvalidOperationException($"The {PantheonData.MythologyDef(m).Id} deck holds fewer than 2 cards");
        for (var i = 0; i < 2; i++)
        {
            var d = r.Divinities[i];
            var known = p.DeckCards[deck + i];
            if (known >= 0 && d != known)
                throw new InvalidOperationException($"Card {i + 1} of the {PantheonData.MythologyDef(m).Id} deck is {PantheonData.Divinities[known].Id}");
            if (known < 0 && (d < 0 || d >= PantheonData.Divinities.Length || (UnseenIn(p, m) & 1 << d) == 0))
                throw new InvalidOperationException(
                    $"{(d >= 0 && d < PantheonData.Divinities.Length ? PantheonData.Divinities[d].Id : "?")} is not an unseen card of the {PantheonData.MythologyDef(m).Id} deck");
        }
        for (var i = 0; i < 2; i++)
        {
            p.Draw[i] = (sbyte)r.Divinities[i];
            p.DivinitiesSeen |= (ushort)(1 << r.Divinities[i]);
        }
        RemoveTop(p, m, 2);
    }

    static void ApplyPantheon(GameState s, PantheonReveal r)
    {
        var p = P(s);
        if (!p.AwaitingPantheon) throw new InvalidOperationException("The Pantheon is revealed once, at Age II setup");
        var named = 0;
        foreach (var (space, d) in r.Divinities)
        {
            if (space is < 0 or >= PantheonData.SpaceCount || !p.IsFaceDown(space) || (named & 1 << space) != 0)
                throw new InvalidOperationException($"Pantheon space {space + 1} holds no face-down card to reveal");
            if (d != p.Spaces[space])
                throw new InvalidOperationException($"Pantheon space {space + 1} holds {PantheonData.Divinities[p.Spaces[space]].Id}");
            named |= 1 << space;
        }
        if (named != p.FaceDown) throw new InvalidOperationException("The Pantheon Reveal names every face-down card");
        p.FaceDown = 0;
        p.Awaiting &= ~PantheonState.SetupStep.Pantheon;
        if (Array.IndexOf(p.Spaces, (sbyte)Enki) >= 0) p.Awaiting |= PantheonState.SetupStep.Enki;
    }

    static void ApplyOfferings(GameState s, OfferingsReveal r)
    {
        var p = P(s);
        if (!p.AwaitingOfferings) throw new InvalidOperationException("The Offering tokens are placed once, at Age II setup");
        if (p.AwaitingPantheon) throw new InvalidOperationException("The Pantheon is revealed before the Offering tokens");
        if (r.Tokens.Length != PantheonData.OfferingSlots.Length
            || !r.Tokens.Select(t => t.Slot).Order().SequenceEqual(PantheonData.OfferingSlots.Order()))
            throw new InvalidOperationException($"The Offering tokens lie on Age II slots {SlotNames(2, PantheonData.OfferingSlots)}");
        if (!r.Tokens.Select(t => t.Offering).Order().SequenceEqual(Enumerable.Range(0, PantheonData.Offerings.Length)))
            throw new InvalidOperationException("The Offering Reveal places each of the 3 Offering tokens once");
        foreach (var (slot, o) in r.Tokens) p.SlotTokens[slot] = (sbyte)(PantheonState.OfferingToken + o);
        p.Awaiting &= ~PantheonState.SetupStep.Offerings;
    }

    static void ApplyEnki(GameState s, EnkiReveal r)
    {
        var p = P(s);
        if (!p.AwaitingEnki) throw new InvalidOperationException("Enki has not just become face up");
        if (p.AwaitingOfferings) throw new InvalidOperationException("The Offering tokens are revealed before Enki's tokens");
        if (r.Tokens.Length != 2 || r.Tokens[0] == r.Tokens[1])
            throw new InvalidOperationException("Enki draws 2 different Progress tokens");
        ushort mask = 0;
        foreach (var t in r.Tokens)
        {
            if (!GameData.IsToken(t) || (s.TokensSetAside & 1 << t) == 0)
                throw new InvalidOperationException($"{(GameData.IsToken(t) ? GameData.Token(t).Id : "?")} is not in the box");
            mask |= (ushort)(1 << t);
        }
        p.EnkiTokens = mask;
        p.Awaiting &= ~PantheonState.SetupStep.Enki;
    }

    /// <summary>Zeus's face-down card is named, goes to the discard pile, and its token goes to Zeus's activator (R2).</summary>
    static void ApplyZeusDiscard(GameState s, ZeusDiscardReveal r)
    {
        var p = P(s);
        if (!p.AwaitingZeusDiscard) throw new InvalidOperationException("Zeus is not discarding a face-down card");
        var slot = p.ZeusSlot;
        if (!GameData.IsCard(r.Card) || !s.UnseenPoolFor(slot).Contains(r.Card))
            throw new InvalidOperationException(
                $"{(GameData.IsCard(r.Card) ? GameData.Card(r.Card).Id : "?")} cannot be the face-down card in {GameData.Layout(s.Age).SlotName(slot)}");
        p.ZeusSlot = -1;
        ZeusDiscards(s, s.CurrentPlayer, slot, r.Card);
    }

    public override Reveal NextReveal(GameState s, Rng rng)
    {
        var p = P(s);
        if (p.AwaitingZeusDiscard)
        {
            var pool = s.UnseenPoolFor(p.ZeusSlot);
            return new ZeusDiscardReveal(pool.ElementAt(rng.Next(pool.Count)));
        }
        if (p.AwaitingMythologyTokens)
        {
            // 5 of the 10 tokens (2 per Mythology), uniformly, on the R1 slots in order.
            var box = Enumerable.Range(0, Mythologies * 2).Select(i => (Mythology)(i / 2)).ToArray();
            var slots = PantheonData.MythologySlots;
            var picked = Shuffled(rng, box.Length, slots.Length);
            return new MythologyTokensReveal(slots.Select((slot, i) => (slot, box[picked[i]])).ToArray());
        }
        if (p.AwaitingPantheon)
            return new PantheonReveal(Enumerable.Range(0, PantheonData.SpaceCount).Where(p.IsFaceDown)
                .Select(space => (space, (int)p.Spaces[space])).ToArray());
        if (p.AwaitingOfferings)
        {
            var order = Shuffled(rng, PantheonData.Offerings.Length, PantheonData.Offerings.Length);
            return new OfferingsReveal(PantheonData.OfferingSlots.Select((slot, i) => (slot, order[i])).ToArray());
        }
        if (p.AwaitingEnki)
        {
            var box = ProgressTokens.In(s.TokensSetAside).ToArray();
            return new EnkiReveal(RandomChanceSource.Pick(rng, box.Length, 2).Select(i => box[i]).ToArray());
        }
        if (AwaitingDraw(s))
        {
            // Known cards on top are drawn first; each unknown position is a uniform unseen card of the deck.
            var decision = s.Pending(0);
            var m = (Mythology)decision.Wonder;
            var unseen = UnseenIn(p, m);
            var drawn = new int[2];
            for (var i = 0; i < 2; i++)
            {
                var known = p.DeckCards[(int)m * PantheonData.DeckSize + i];
                if (known >= 0) drawn[i] = known;
                else
                {
                    var pool = new Bits(unseen).ToArray();
                    drawn[i] = pool[rng.Next(pool.Length)];
                    unseen &= (ushort)~(1 << drawn[i]);
                }
            }
            return new DivinityDrawReveal(decision.Player, m, drawn);
        }
        throw new InvalidOperationException("Pantheon awaits no Reveal");
    }

    /// <summary><paramref name="count"/> distinct values below <paramref name="n"/>, uniformly, in draw order (not sorted).</summary>
    static int[] Shuffled(Rng rng, int n, int count)
    {
        var values = Enumerable.Range(0, n).ToArray();
        for (var i = 0; i < count; i++)
        {
            var j = i + rng.Next(n - i);
            (values[i], values[j]) = (values[j], values[i]);
        }
        return values[..count];
    }

    /// <summary>A deck's cards nobody has named yet: what its unknown positions hold.</summary>
    static ushort UnseenIn(PantheonState p, Mythology m) => (ushort)(DeckOf[(int)m] & ~p.DivinitiesSeen);

    static void RemoveTop(PantheonState p, Mythology m, int count)
    {
        var deck = (int)m * PantheonData.DeckSize;
        for (var i = 0; i < PantheonData.DeckSize; i++)
        {
            var from = i + count;
            p.DeckCards[deck + i] = from < PantheonData.DeckSize ? p.DeckCards[deck + from] : (sbyte)PantheonState.Unknown;
            p.DeckAudience[deck + i] = from < PantheonData.DeckSize ? p.DeckAudience[deck + from] : (byte)0;
        }
        p.DeckCount[(int)m] -= (byte)count;
    }

    static void PutOnTop(PantheonState p, Mythology m, int divinity, byte knownBy)
    {
        var deck = (int)m * PantheonData.DeckSize;
        for (var i = PantheonData.DeckSize - 1; i > 0; i--)
        {
            p.DeckCards[deck + i] = p.DeckCards[deck + i - 1];
            p.DeckAudience[deck + i] = p.DeckAudience[deck + i - 1];
        }
        p.DeckCards[deck] = (sbyte)divinity;
        p.DeckAudience[deck] = knownBy;
        p.DeckCount[(int)m]++;
    }

    static string SlotNames(int age, int[] slots) => string.Join(' ', slots.Select(GameData.Layout(age).SlotName));

    // ---------------------------------------------------------------- decisions

    public override int LegalMoves(GameState s, Span<Move> buffer)
    {
        if (s.Pending(0).Kind == DecisionKind.Offer) return LegalOffers(s, buffer);
        if (s.Pending(0).Kind > DecisionKind.Offer) return LegalEffectMoves(s, s.Pending(0), buffer);
        if (s.Pending(0).Kind != DecisionKind.PlaceDivinity) return -1;
        var p = P(s);
        var n = 0;
        for (var i = 0; i < 2; i++)
            for (var space = 0; space < PantheonData.SpaceCount; space++)
                if (p.Spaces[space] == PantheonState.EmptySpace) buffer[n++] = Move.PlaceDivinity(p.Draw[i], space);
        return n;
    }

    public override bool TryWhyIllegal(GameState s, Move move, out string? why)
    {
        var decision = s.Pending(0);
        if (decision.Kind == DecisionKind.Offer)
        {
            why = WhyOfferIllegal(s, move, decision);
            return true;
        }
        if (decision.Kind == DecisionKind.TakeCard && move.Kind == MoveKind.Activate)
        {
            why = WhyActivateIllegal(s, decision.Player, move.Divinity);
            return true;
        }
        if (decision.Kind > DecisionKind.Offer)
        {
            why = WhyEffectMoveIllegal(s, decision, move);
            return true;
        }
        if (decision.Kind != DecisionKind.PlaceDivinity)
        {
            why = move.Kind switch
            {
                MoveKind.Divinity => "no drawn Divinity is waiting to be placed",
                MoveKind.Activate => "the Pantheon is activated instead of taking a card",
                MoveKind.Offer => "no activation is waiting for its Offering tokens",
                > MoveKind.Offer => $"no {move.Kind.ToString().ToLowerInvariant()} decision is pending",
                _ => null,
            };
            return why is not null;
        }
        var p = P(s);
        why = move.Kind != MoveKind.Divinity ? "the decision is which drawn Divinity to place, and on which empty Pantheon space"
            : move.Divinity != p.Draw[0] && move.Divinity != p.Draw[1]
                ? $"{(move.Divinity >= 0 && move.Divinity < PantheonData.Divinities.Length ? PantheonData.Divinities[move.Divinity].Id : "?")} was not drawn"
            : move.Space is < 0 or >= PantheonData.SpaceCount ? $"there is no Pantheon space {move.Space + 1}"
            : p.Spaces[move.Space] != PantheonState.EmptySpace ? $"Pantheon space {move.Space + 1} is taken"
            : null;
        return true;
    }

    public override bool TryApply(GameState s, Move move, PendingDecision decision)
    {
        if (move.Kind == MoveKind.Activate)
        {
            // R8: a player holding Offering tokens chooses which to spend, unless there is nothing to pay.
            if (ActivationCost(s, decision.Player, move.Divinity) > 0 && P(s).HeldOfferings[decision.Player] != 0)
                s.PushDecision(new PendingDecision(DecisionKind.Offer, decision.Player, move.Divinity));
            else Activate(s, decision.Player, move.Divinity, 0);
            return true;
        }
        if (decision.Kind == DecisionKind.Offer)
        {
            Activate(s, decision.Player, decision.Wonder, move.Offerings);
            return true;
        }
        if (decision.Kind > DecisionKind.Offer)
        {
            ApplyEffectMove(s, decision.Player, move);
            return true;
        }
        if (decision.Kind != DecisionKind.PlaceDivinity) return false;
        // Placed face down; the other card goes back on top of its deck, known only to this player. The take-card
        // decision of the next turn is already queued behind (the token was taken as the turn ended).
        var p = P(s);
        var other = p.Draw[0] == move.Divinity ? p.Draw[1] : p.Draw[0];
        p.Spaces[move.Space] = (sbyte)move.Divinity;
        p.FaceDown |= (byte)(1 << move.Space);
        if (decision.Player == 1) p.PlacedByP2 |= (byte)(1 << move.Space);
        PutOnTop(p, (Mythology)decision.Wonder, other, (byte)(1 << decision.Player));
        p.Draw[0] = p.Draw[1] = -1;
        return true;
    }

    // ---------------------------------------------------------------- activating the Pantheon

    /// <summary>The Pantheon space holding a face-up Divinity (or the Gate), or -1.</summary>
    static int SpaceOf(PantheonState p, int divinity)
    {
        for (var space = 0; space < PantheonData.SpaceCount; space++)
            if (p.Spaces[space] == divinity && !p.IsFaceDown(space)) return space;
        return -1;
    }

    /// <summary>
    /// What activating a card in the Pantheon costs a player (P-A4): their cost for its space, doubled for the Gate,
    /// minus 2 with the Sanctuary (never below 0).
    /// </summary>
    public static int ActivationCost(GameState s, int player, int divinity)
    {
        var space = SpaceOf(P(s), divinity);
        var cost = PantheonData.Spaces[space].Cost(player) * (divinity == PantheonData.Gate ? 2 : 1);
        if ((s.BuiltWonders(player) & 1 << Sanctuary) != 0) cost -= SanctuaryDiscount;
        return Math.Max(0, cost);
    }

    /// <summary>The coins a set of Offering tokens is worth (one bit per Offering index).</summary>
    public static int OfferingValue(int offerings)
    {
        var value = 0;
        foreach (var o in PantheonData.Offerings)
            if ((offerings & 1 << o.Index) != 0) value += o.Value;
        return value;
    }

    /// <summary>The Pantheon is activated from Age II, instead of taking a card, if the player can pay with everything they have.</summary>
    public override int AddTakeCardMoves(GameState s, int player, Span<Move> buffer, int count)
    {
        if (s.Age < 2) return count;
        var p = P(s);
        var means = s.SpendableCoins(player) + OfferingValue(p.HeldOfferings[player]);
        for (var space = 0; space < PantheonData.SpaceCount; space++)
        {
            var d = p.Spaces[space];
            if (d >= 0 && !p.IsFaceDown(space) && ActivationCost(s, player, d) <= means) buffer[count++] = Move.Activate(d);
        }
        return count;
    }

    static string? WhyActivateIllegal(GameState s, int player, int divinity)
    {
        if (s.Age < 2) return "the Pantheon can be activated from Age II";
        if (divinity < 0 || divinity >= PantheonData.Divinities.Length) return "unknown Divinity";
        var id = PantheonData.Divinities[divinity].Id;
        var p = P(s);
        if (SpaceOf(p, divinity) < 0) return $"{id} is not face up in the Pantheon";
        var cost = ActivationCost(s, player, divinity);
        var offerings = OfferingValue(p.HeldOfferings[player]);
        return cost <= s.SpendableCoins(player) + offerings ? null
            : $"activating {id} costs {cost} coins, P{player + 1} has {s.SpendableCoins(player)}"
                + (offerings > 0 ? $" and Offering tokens worth {offerings}" : "");
    }

    /// <summary>R8: any held tokens that, with the player's coins, cover the cost (overpaying is allowed, no change given).</summary>
    static bool CanOffer(GameState s, int player, int cost, int offerings) =>
        (offerings & ~P(s).HeldOfferings[player]) == 0 && OfferingValue(offerings) + s.SpendableCoins(player) >= cost;

    static int LegalOffers(GameState s, Span<Move> buffer)
    {
        var decision = s.Pending(0);
        var cost = ActivationCost(s, decision.Player, decision.Wonder);
        var n = 0;
        for (var offerings = 0; offerings < 1 << PantheonData.Offerings.Length; offerings++)
            if (CanOffer(s, decision.Player, cost, offerings)) buffer[n++] = Move.Offer(offerings);
        return n;
    }

    static string? WhyOfferIllegal(GameState s, Move move, PendingDecision decision)
    {
        if (move.Kind != MoveKind.Offer)
            return $"the decision is which Offering tokens to spend on {PantheonData.Divinities[decision.Wonder].Id}";
        if (move.Offerings < 0 || move.Offerings >= 1 << PantheonData.Offerings.Length) return "unknown Offering tokens";
        if ((move.Offerings & ~P(s).HeldOfferings[decision.Player]) != 0)
            return $"P{decision.Player + 1} does not hold all of those Offering tokens";
        var cost = ActivationCost(s, decision.Player, decision.Wonder);
        return CanOffer(s, decision.Player, cost, move.Offerings) ? null
            : $"activating {PantheonData.Divinities[decision.Wonder].Id} costs {cost} coins: those tokens and P{decision.Player + 1}'s {s.SpendableCoins(decision.Player)} coins do not cover it";
    }

    /// <summary>
    /// Pays (the Offering tokens spent, then coins: the treasury first, then Astarte's, R7), takes the card off its
    /// space (which stays empty) and applies its effect; the turn then ends unless the effect queued a decision.
    /// </summary>
    static void Activate(GameState s, int player, int divinity, int offerings)
    {
        var p = P(s);
        var cost = ActivationCost(s, player, divinity);
        p.HeldOfferings[player] &= (byte)~offerings;
        s.Spend(player, Math.Max(0, cost - OfferingValue(offerings)));
        p.Spaces[SpaceOf(p, divinity)] = (sbyte)PantheonState.EmptySpace;
        p.ActivatedBy[player] |= (ushort)(1 << divinity);

        // The effects without a decision (Aphrodite's VP are scored at the end), then the decision of those with one.
        // Minerva, Neptune and the Gate come in ticket 05.
        var def = PantheonData.Divinities[divinity];
        if (def.Coins > 0) s.SetCoins(player, s.Coins(player) + def.Coins);
        if (def.ProtectedCoins > 0) p.Astarte[player] += (byte)def.ProtectedCoins;
        if (def.Shields > 0) s.AddShields(player, def.Shields);
        if (def.Science is { } symbol && !s.IsOver) s.GainSymbol(player, symbol);
        if (!s.IsOver && EffectDecision(s, player, divinity) is { } kind) s.PushDecision(new PendingDecision(kind, player));
        s.FinishDecision();
    }

    // ---------------------------------------------------------------- the Divinities' decisions

    /// <summary>
    /// The decision a Divinity's effect asks for, or null if it has none or nothing to choose from (then it does nothing,
    /// FAQ). Zeus always has a card to discard: the Pantheon is activated instead of taking one, so the structure is not empty.
    /// </summary>
    static DecisionKind? EffectDecision(GameState s, int player, int divinity)
    {
        var opponent = 1 - player;
        if (divinity == Enki) return P(s).EnkiTokens != 0 ? DecisionKind.Enki : null;
        if (divinity == Nisaba) return !(s.City(opponent) & Green).IsEmpty ? DecisionKind.Snake : null;
        if (divinity == Baal) return !(s.City(opponent) & BrownAndGrey).IsEmpty ? DecisionKind.Steal : null;
        if (divinity == Hades) return !s.DiscardPile.IsEmpty ? DecisionKind.Hades : null;
        if (divinity == Zeus) return s.CardsLeftInAge > 0 ? DecisionKind.Zeus : null;
        if (divinity == Anubis) return (s.BuiltWonders(0) | s.BuiltWonders(1)) != 0 ? DecisionKind.Anubis : null;
        if (divinity == Isis) return !s.DiscardPile.IsEmpty && s.BuildableWonders(player) != 0 ? DecisionKind.Isis : null;
        if (divinity == Ra) return s.BuildableWonders(opponent) != 0 ? DecisionKind.Ra : null;
        return null;
    }

    static int LegalEffectMoves(GameState s, PendingDecision decision, Span<Move> buffer)
    {
        var player = decision.Player;
        var opponent = 1 - player;
        var n = 0;
        switch (decision.Kind)
        {
            case DecisionKind.Enki:
                foreach (var t in ProgressTokens.In(P(s).EnkiTokens)) buffer[n++] = Move.Enki(t);
                break;
            case DecisionKind.Snake:
                foreach (var c in (s.City(opponent) & Green).Cards()) buffer[n++] = Move.Snake(c);
                break;
            case DecisionKind.Steal:
                foreach (var c in (s.City(opponent) & BrownAndGrey).Cards()) buffer[n++] = Move.Steal(c);
                break;
            case DecisionKind.Hades:
                foreach (var c in s.DiscardPile.Cards()) buffer[n++] = Move.Hades(c);
                break;
            case DecisionKind.Zeus:
                for (var slot = 0; slot < GameState.SlotCount; slot++)
                    if (s.IsOccupied(slot)) buffer[n++] = Move.Zeus(slot, s.Age);
                break;
            case DecisionKind.Anubis:
                foreach (var w in GameState.WondersIn((ushort)(s.BuiltWonders(0) | s.BuiltWonders(1)))) buffer[n++] = Move.Anubis(w);
                break;
            case DecisionKind.Isis:
                foreach (var c in s.DiscardPile.Cards())
                    foreach (var w in GameState.WondersIn(s.BuildableWonders(player))) buffer[n++] = Move.Isis(c, w);
                break;
            case DecisionKind.Ra:
                foreach (var w in GameState.WondersIn(s.BuildableWonders(opponent))) buffer[n++] = Move.Ra(w);
                break;
            default:
                throw new InvalidOperationException($"Unknown Pantheon decision {decision.Kind}");
        }
        return n;
    }

    static string? WhyEffectMoveIllegal(GameState s, PendingDecision decision, Move move)
    {
        var player = decision.Player;
        var opponent = 1 - player;
        var expected = decision.Kind switch
        {
            DecisionKind.Enki => (MoveKind.Enki, "which of Enki's Progress tokens to keep"),
            DecisionKind.Snake => (MoveKind.Snake, "which of the opponent's green cards gets Nisaba's Snake"),
            DecisionKind.Steal => (MoveKind.Steal, "which of the opponent's brown or grey cards Baal steals"),
            DecisionKind.Hades => (MoveKind.Hades, "which discarded card Hades builds"),
            DecisionKind.Zeus => (MoveKind.Zeus, "which structure slot's card Zeus discards"),
            DecisionKind.Anubis => (MoveKind.Anubis, "which built Wonder Anubis unbuilds"),
            DecisionKind.Isis => (MoveKind.Isis, "which discarded card Isis builds which of your Wonders with"),
            DecisionKind.Ra => (MoveKind.Ra, "which of the opponent's unbuilt Wonders Ra takes"),
            _ => throw new InvalidOperationException($"Unknown Pantheon decision {decision.Kind}"),
        };
        if (move.Kind != expected.Item1) return "the decision is " + expected.Item2;
        static string CardId(int c) => GameData.IsCard(c) ? GameData.Card(c).Id : "?";
        static string WonderId(int w) => GameData.IsWonder(w) ? GameData.Wonder(w).Id : "?";
        switch (move.Kind)
        {
            case MoveKind.Enki:
                return GameData.IsToken(move.Token) && (P(s).EnkiTokens & 1 << move.Token) != 0 ? null
                    : $"{(GameData.IsToken(move.Token) ? GameData.Token(move.Token).Id : "?")} is not on Enki";
            case MoveKind.Snake:
                return GameData.IsCard(move.Card) && (s.City(opponent) & Green).Contains(move.Card) ? null
                    : $"{CardId(move.Card)} is not a green card in P{opponent + 1}'s city";
            case MoveKind.Steal:
                return GameData.IsCard(move.Card) && (s.City(opponent) & BrownAndGrey).Contains(move.Card) ? null
                    : $"{CardId(move.Card)} is not a brown or grey card in P{opponent + 1}'s city";
            case MoveKind.Hades:
                return GameData.IsCard(move.Card) && s.DiscardPile.Contains(move.Card) ? null
                    : $"{CardId(move.Card)} is not in the discard pile";
            case MoveKind.Zeus:
                return move.Wonder != s.Age ? $"the structure is Age {s.Age}'s"
                    : move.Slot is < 0 or >= GameState.SlotCount || !s.IsOccupied(move.Slot) ? "that slot holds no card"
                    : null;
            case MoveKind.Anubis:
                return GameData.IsWonder(move.Wonder) && s.IsWonderBuilt(move.Wonder) ? null : $"{WonderId(move.Wonder)} is not built";
            case MoveKind.Isis:
                return !GameData.IsCard(move.Card) || !s.DiscardPile.Contains(move.Card) ? $"{CardId(move.Card)} is not in the discard pile"
                    : !GameData.IsWonder(move.Wonder) || (s.BuildableWonders(player) & 1 << move.Wonder) == 0
                        ? $"{WonderId(move.Wonder)} is not an unbuilt Wonder of P{player + 1}'s"
                    : null;
            default: // Ra
                return GameData.IsWonder(move.Wonder) && (s.BuildableWonders(opponent) & 1 << move.Wonder) != 0 ? null
                    : $"{WonderId(move.Wonder)} is not an unbuilt Wonder of P{opponent + 1}'s";
        }
    }

    /// <summary>Applies a Divinity's decision; the turn then ends unless something more was queued (a token pick, a Wonder's power).</summary>
    static void ApplyEffectMove(GameState s, int player, Move move)
    {
        var p = P(s);
        var opponent = 1 - player;
        switch (move.Kind)
        {
            case MoveKind.Enki:
                // The other token goes back to the box (it is out of the box only while on Enki).
                p.EnkiTokens = 0;
                s.TakeToken(player, move.Token);
                break;
            case MoveKind.Snake:
                // Nisaba now shows the card's symbol too (FAQ: it counts for pairs and supremacy).
                p.SnakeCard = (sbyte)move.Card;
                s.GainSymbol(player, GameData.Card(move.Card).Science!.Value);
                break;
            case MoveKind.Steal:
                s.RemoveFromCity(opponent, move.Card);
                s.AddToCity(player, move.Card);
                break;
            case MoveKind.Hades:
                s.TakeFromDiscard(move.Card);
                s.Build(player, move.Card, free: true);
                break;
            case MoveKind.Zeus:
                if (s.SlotCard(move.Slot) < 0)
                {
                    // A face-down card: its public Reveal names it first (reveal discarded), then the turn goes on.
                    p.ZeusSlot = (sbyte)move.Slot;
                    return;
                }
                ZeusDiscards(s, player, move.Slot, s.SlotCard(move.Slot));
                return;
            case MoveKind.Anubis:
                s.UnbuildWonder(move.Wonder);
                break;
            case MoveKind.Isis:
                s.TakeFromDiscard(move.Card);
                s.BuildWonder(player, move.Card, move.Wonder, free: true);
                break;
            case MoveKind.Ra:
                s.StealWonder(player, move.Wonder);
                break;
        }
        s.FinishDecision();
    }

    /// <summary>Zeus's card goes to the discard pile and the player who used Zeus takes any token on it (R2).</summary>
    static void ZeusDiscards(GameState s, int player, int slot, int card)
    {
        s.DiscardFromSlot(slot, card);
        TakeSlotToken(s, player, slot);
        s.FinishDecision();
    }

    /// <summary>
    /// Ishtar's symbol (Law), and Nisaba's copy of the symbol under its Snake: both count for pairs and supremacy (FAQ).
    /// </summary>
    public override int ExtraSymbols(GameState s, int player, ScienceSymbol symbol)
    {
        var n = 0;
        var p = P(s);
        var activated = p.ActivatedBy[player];
        if (activated == 0) return 0;
        foreach (var d in new Bits(activated))
            if (PantheonData.Divinities[d].Science == symbol) n++;
        if ((activated & 1 << Nisaba) != 0 && p.SnakeCard >= 0 && GameData.Card(p.SnakeCard).Science == symbol) n++;
        return n;
    }

    public override int ExtraCoins(GameState s, int player) => P(s).Astarte[player];

    public override void SpendExtraCoins(GameState s, int player, int amount)
    {
        var p = P(s);
        if (amount > p.Astarte[player]) throw new InvalidOperationException($"P{player + 1} cannot pay {amount} more coins");
        p.Astarte[player] -= (byte)amount;
    }

    // ---------------------------------------------------------------- turns

    /// <summary>
    /// Taking tokens (spec-pantheon P-A3): a card holding a token that the turn has made accessible gives the token to
    /// the player whose turn it is, before anything else (including play again). A Mythology token queues a
    /// <c>divinity</c> decision (after its private draw); two tokens queue two, in slot order.
    /// </summary>
    public override void EndOfTurn(GameState s)
    {
        var p = P(s);
        for (var slot = 0; slot < GameState.SlotCount; slot++)
            if (p.SlotTokens[slot] != PantheonState.NoToken && s.IsAccessible(slot)) TakeSlotToken(s, s.CurrentPlayer, slot);
    }

    /// <summary>A player takes the token on a slot, if any (an Offering token is just held).</summary>
    static void TakeSlotToken(GameState s, int player, int slot)
    {
        var p = P(s);
        var token = p.SlotTokens[slot];
        if (token == PantheonState.NoToken) return;
        p.SlotTokens[slot] = (sbyte)PantheonState.NoToken;
        if (token >= PantheonState.OfferingToken)
        {
            p.HeldOfferings[player] |= (byte)(1 << (token - PantheonState.OfferingToken));
            return;
        }
        p.HeldMythology[player * Mythologies + token]++;
        if (Array.IndexOf(p.Spaces, (sbyte)PantheonState.EmptySpace) >= 0)
            s.PushDecision(new PendingDecision(DecisionKind.PlaceDivinity, player, token));
    }

    // ---------------------------------------------------------------- payment, military, scoring

    /// <summary>
    /// A Grand Temple is free while holding a token of its Mythology (R9: not a chain, so no Urbanism; the token is
    /// kept). Engineering: a card showing a chain symbol under its cost costs 1 coin (not a chain build either).
    /// </summary>
    public override PaymentQuote AdjustQuote(GameState s, int player, int card, PaymentQuote quote)
    {
        if (quote.Total == 0) return quote;
        if (PantheonData.TempleSet.Contains(card)
            && P(s).MythologyTokens(player, PantheonData.Temples[card - GameData.Cards.Length].Mythology) > 0)
            return default;
        if (quote.Total > EngineeringCost && GameData.Card(card).FreeIf >= 0 && s.HasToken(player, Engineering))
            return new PaymentQuote(EngineeringCost, 0);
        return quote;
    }

    /// <summary>Poliorcetics: each space the owner moves the Conflict Pawn forward costs the opponent 1 coin.</summary>
    public override void PawnAdvanced(GameState s, int player)
    {
        if (s.HasToken(player, Poliorcetics)) s.LoseCoins(1 - player, 1);
    }

    /// <summary>
    /// Grand Temples: 5 / 12 / 21 VP for 1 / 2 / 3. Mysticism: 2 VP per Mythology and Offering token held (Progress).
    /// Divinities: Aphrodite's 9 VP and 1 VP per Astarte coin left.
    /// </summary>
    public override void Score(GameState s, int player, Span<int> points)
    {
        var activated = P(s).ActivatedBy[player];
        if (activated != 0)
            foreach (var d in new Bits(activated)) points[(int)ScoreCategory.Divinities] += PantheonData.Divinities[d].VictoryPoints;
        points[(int)ScoreCategory.Divinities] += P(s).Astarte[player];
        var temples = (s.City(player) & PantheonData.TempleSet).Count;
        if (temples > 0) points[(int)ScoreCategory.Temples] += PantheonData.TemplePoints[temples - 1];
        if (s.HasToken(player, Mysticism))
        {
            var p = P(s);
            var held = p.MythologyTokenCount(player) + System.Numerics.BitOperations.PopCount((uint)p.HeldOfferings[player]);
            points[(int)ScoreCategory.Progress] += MysticismPointsPerToken * held;
        }
    }
}
