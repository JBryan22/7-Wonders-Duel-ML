using SevenWondersDuel.Engine.Data;

namespace SevenWondersDuel.Engine;

/// <summary>
/// Pantheon's block of Game State (spec-pantheon P-A3): tokens on structure slots and held, the Pantheon spaces, the
/// Mythology decks, the current draw and Enki's tokens. The rules that change it are in <see cref="PantheonModule"/>.
/// <para>
/// Like the Great Library's draw, a placed face-down Divinity and a card put back on a deck are stored with their
/// identity, because one player knows them (spec A4: the Observation hides what a player has not seen).
/// </para>
/// <para>
/// A state that does not know them (an <see cref="Observation"/>, or a log kept with the opponent's private entries
/// redacted, spec-pantheon P-A5) holds <see cref="Hidden"/> instead: a hidden draw, a face-down space showing only its
/// Mythology (<see cref="HiddenSpace"/>), or an <see cref="Unknown"/> deck card whose audience bits still say who knows
/// it. <see cref="SeenDivinities"/> is then what this state can name, so a deck's unseen cards are also the candidates
/// for every hidden place of its Mythology; the <see cref="Determinizer"/> fills those places from them.
/// </para>
/// </summary>
public sealed class PantheonState : ExpansionState
{
    /// <summary>A structure slot with no token.</summary>
    public const int NoToken = -1;
    /// <summary>Token codes on a slot: a <see cref="Mythology"/> (0..4), or this plus an Offering token index (5..7).</summary>
    public const int OfferingToken = 5;
    /// <summary>An empty Pantheon space.</summary>
    public const int EmptySpace = -1;
    /// <summary>A deck position whose card nobody has seen.</summary>
    public const int Unknown = -1;
    /// <summary>
    /// A Divinity this state does not know: a hidden draw entry, the Divinity of a redacted <c>divinity ?</c> or
    /// <c>theater-order ?</c> Move, and (below it, see <see cref="HiddenSpace"/>) a hidden face-down space.
    /// </summary>
    public const int Hidden = -2;
    /// <summary>The Minerva pawn is not on the Military track.</summary>
    public const int NoMinerva = sbyte.MinValue;

    const int Mythologies = 5;

    internal readonly sbyte[] SlotTokens = new sbyte[GameState.SlotCount];
    /// <summary>Mythology tokens held, per player and Mythology (index player * 5 + mythology).</summary>
    internal readonly byte[] HeldMythology = new byte[2 * Mythologies];
    /// <summary>Offering tokens held per player, one bit per Offering index.</summary>
    internal readonly byte[] HeldOfferings = new byte[2];
    /// <summary>Per Pantheon space: a Divinity (or the Gate), or <see cref="EmptySpace"/>.</summary>
    internal readonly sbyte[] Spaces = new sbyte[PantheonData.SpaceCount];
    /// <summary>Pantheon spaces whose card is face down, and those placed by P2, one bit per space.</summary>
    internal byte FaceDown, PlacedByP2;
    /// <summary>Cards left per Mythology deck.</summary>
    internal readonly byte[] DeckCount = new byte[Mythologies];
    /// <summary>Per deck (index mythology * 3 + position, top first): the known card there, or <see cref="Unknown"/>.</summary>
    internal readonly sbyte[] DeckCards = new sbyte[Mythologies * PantheonData.DeckSize];
    /// <summary>Who knows each known deck card: bit 0 P1, bit 1 P2.</summary>
    internal readonly byte[] DeckAudience = new byte[Mythologies * PantheonData.DeckSize];
    /// <summary>Divinities named by a Reveal so far, one bit per Divinity index: the rest of each deck is unseen.</summary>
    internal ushort DivinitiesSeen;
    /// <summary>The 2 Divinities drawn for the pending <c>divinity</c> decision (top card first), or -1; <see cref="Hidden"/> when not known.</summary>
    internal readonly sbyte[] Draw = new sbyte[2];
    /// <summary>The 2 Progress tokens on Enki (from its Reveal until it is activated), one bit per token.</summary>
    internal ushort EnkiTokens;
    /// <summary>Per player: the Divinities (and the Gate) activated, one bit per Divinity index.</summary>
    internal readonly ushort[] ActivatedBy = new ushort[2];
    /// <summary>Per player: Astarte's coins left (R7: spendable after the treasury, never lost, 1 VP each).</summary>
    internal readonly byte[] Astarte = new byte[2];
    /// <summary>The opponent's green card under Nisaba's Snake (its symbol counts for Nisaba's activator too), or -1.</summary>
    internal sbyte SnakeCard = -1;
    /// <summary>The slot whose face-down card Zeus is discarding, until its <c>reveal discarded</c>; otherwise -1.</summary>
    internal sbyte ZeusSlot = -1;
    /// <summary>True from the Gate's activation until its <c>reveal gate</c> names the deck tops.</summary>
    internal bool GatePending;
    /// <summary>The Mythology deck the Divine Theater looks through, until its private <c>reveal theater</c>; otherwise -1.</summary>
    internal sbyte TheaterPending = -1;
    /// <summary>The Military track space (-9..9, like the Conflict Pawn) holding the Minerva pawn, or <see cref="NoMinerva"/>.</summary>
    internal sbyte MinervaSpace = NoMinerva;
    /// <summary>
    /// The Age whose 3 removed cards the Statue of Liberty has returned, or 0: after Anubis, a Statue rebuilt in the
    /// same Age finds none left (ticket 06).
    /// </summary>
    internal byte ReturnedCardsAge;
    internal SetupStep Awaiting;

    [Flags]
    internal enum SetupStep : byte { None = 0, MythologyTokens = 1, Pantheon = 2, Offerings = 4, Enki = 8 }

    public PantheonState()
    {
        Array.Fill(SlotTokens, (sbyte)NoToken);
        Array.Fill(Spaces, (sbyte)EmptySpace);
        Array.Fill(DeckCount, (byte)PantheonData.DeckSize);
        Array.Fill(DeckCards, (sbyte)Unknown);
        Array.Fill(Draw, (sbyte)-1);
    }

    // ---------------------------------------------------------------- reading

    /// <summary>The token on a structure slot of the current Age: a Mythology (0..4), <see cref="OfferingToken"/> + Offering index, or <see cref="NoToken"/>.</summary>
    public int SlotToken(int slot) => SlotTokens[slot];
    /// <summary>How many tokens of a Mythology a player holds (0..2).</summary>
    public int MythologyTokens(int player, Mythology m) => HeldMythology[player * Mythologies + (int)m];
    /// <summary>All the Mythology tokens a player holds.</summary>
    public int MythologyTokenCount(int player)
    {
        var n = 0;
        for (var m = 0; m < Mythologies; m++) n += HeldMythology[player * Mythologies + m];
        return n;
    }
    /// <summary>The Offering tokens a player holds, one bit per Offering index.</summary>
    public int OfferingTokens(int player) => HeldOfferings[player];

    /// <summary>
    /// The Divinity (or Gate) on a Pantheon space (0..5), <see cref="EmptySpace"/>, or a face-down card this state does
    /// not know (<see cref="HiddenSpace"/>, below <see cref="EmptySpace"/>).
    /// </summary>
    public int Space(int space) => Spaces[space];
    public bool IsFaceDown(int space) => (FaceDown & 1 << space) != 0;
    /// <summary>True for a face-down space whose Divinity this state does not know (only its Mythology).</summary>
    public bool IsHidden(int space) => Spaces[space] <= Hidden;
    /// <summary>The value of a hidden face-down space of Mythology <paramref name="m"/>: <see cref="Hidden"/> - m.</summary>
    public static int HiddenSpace(Mythology m) => Hidden - (int)m;
    /// <summary>The Mythology of the card on a non-empty space (public even while the card is face down); null for the Gate.</summary>
    public Mythology? SpaceMythology(int space) => Spaces[space] <= Hidden
        ? (Mythology)(Hidden - Spaces[space])
        : PantheonData.Divinities[Spaces[space]].Mythology;
    /// <summary>Who placed the card on a space in Age I (meaningful for a placed Divinity).</summary>
    public int Placer(int space) => (PlacedByP2 & 1 << space) != 0 ? 1 : 0;

    public int DeckSize(Mythology m) => DeckCount[(int)m];
    /// <summary>The known card at a deck position (0 = top), or <see cref="Unknown"/>.</summary>
    public int DeckCard(Mythology m, int position) => DeckCards[(int)m * PantheonData.DeckSize + position];
    /// <summary>Who knows the card at a deck position: bit 0 P1, bit 1 P2 (0 when unknown).</summary>
    public int DeckKnownBy(Mythology m, int position) => DeckAudience[(int)m * PantheonData.DeckSize + position];
    /// <summary>The Divinities named by a Reveal so far, one bit per index.</summary>
    public ushort SeenDivinities => DivinitiesSeen;

    /// <summary>True while a <c>divinity</c> decision has its cards drawn (known or <see cref="DrawHidden"/>).</summary>
    public bool HasDraw => Draw[0] != -1;
    /// <summary>True while the pending draw is the opponent's and this state does not know it (each card <see cref="Hidden"/>).</summary>
    public bool DrawHidden => Draw[0] == Hidden;
    /// <summary>The drawn cards (top first) while <see cref="HasDraw"/>: a Divinity, <see cref="Hidden"/>, or -1 past a one-card draw.</summary>
    public int Drawn(int i) => Draw[i];
    public ushort Enki => EnkiTokens;
    /// <summary>The Divinities (and the Gate) a player has activated, one bit per Divinity index.</summary>
    public ushort Activated(int player) => ActivatedBy[player];
    /// <summary>Astarte's coins a player has left.</summary>
    public int AstarteCoins(int player) => Astarte[player];
    /// <summary>The green card under Nisaba's Snake, or -1.</summary>
    public int Snake => SnakeCard;
    /// <summary>True while Zeus waits for the face-down card it discards to be named (<c>reveal discarded</c>).</summary>
    public bool AwaitingZeusDiscard => ZeusSlot >= 0;
    /// <summary>The slot Zeus is emptying while <see cref="AwaitingZeusDiscard"/>, otherwise -1.</summary>
    public int ZeusTarget => ZeusSlot;
    /// <summary>True while the Gate waits for its public Reveal of the deck tops.</summary>
    public bool AwaitingGateReveal => GatePending;
    /// <summary>True while the Divine Theater waits for its private Reveal of a whole deck.</summary>
    public bool AwaitingTheaterReveal => TheaterPending >= 0;
    /// <summary>The deck the Divine Theater is about to reveal while <see cref="AwaitingTheaterReveal"/>.</summary>
    public Mythology TheaterDeck => (Mythology)TheaterPending;
    /// <summary>The Military track space holding the Minerva pawn (-9..9), or <see cref="NoMinerva"/>.</summary>
    public int Minerva => MinervaSpace;

    public bool AwaitingMythologyTokens => (Awaiting & SetupStep.MythologyTokens) != 0;
    public bool AwaitingPantheon => (Awaiting & SetupStep.Pantheon) != 0;
    public bool AwaitingOfferings => (Awaiting & SetupStep.Offerings) != 0;
    public bool AwaitingEnki => (Awaiting & SetupStep.Enki) != 0;

    /// <summary>
    /// What activating a face-up card of the Pantheon costs a player now (P-A4): their cost for its space, doubled for
    /// the Gate, minus 2 with the Sanctuary. For the Advisor's prices.
    /// </summary>
    public static int ActivationCost(GameState s, int player, int divinity) => PantheonModule.ActivationCost(s, player, divinity);

    /// <summary>The coins a set of Offering tokens is worth (one bit per Offering index).</summary>
    public static int OfferingValue(int offerings) => PantheonModule.OfferingValue(offerings);

    /// <summary>The Move Log slug of a slot token code: the Mythology's, or the Offering token's.</summary>
    public static string TokenSlug(int code) => code >= OfferingToken
        ? PantheonData.Offerings[code - OfferingToken].Id
        : PantheonData.Mythologies[code].Id;

    // ---------------------------------------------------------------- copying, comparing, bytes

    public override ExpansionState Clone()
    {
        var copy = new PantheonState();
        copy.CopyFrom(this);
        return copy;
    }

    public override void CopyFrom(ExpansionState other)
    {
        if (other is not PantheonState o) throw new ArgumentException("Not a Pantheon state", nameof(other));
        o.SlotTokens.CopyTo(SlotTokens, 0);
        o.HeldMythology.CopyTo(HeldMythology, 0);
        o.HeldOfferings.CopyTo(HeldOfferings, 0);
        o.Spaces.CopyTo(Spaces, 0);
        FaceDown = o.FaceDown;
        PlacedByP2 = o.PlacedByP2;
        o.DeckCount.CopyTo(DeckCount, 0);
        o.DeckCards.CopyTo(DeckCards, 0);
        o.DeckAudience.CopyTo(DeckAudience, 0);
        DivinitiesSeen = o.DivinitiesSeen;
        o.Draw.CopyTo(Draw, 0);
        EnkiTokens = o.EnkiTokens;
        o.ActivatedBy.CopyTo(ActivatedBy, 0);
        o.Astarte.CopyTo(Astarte, 0);
        SnakeCard = o.SnakeCard;
        ZeusSlot = o.ZeusSlot;
        GatePending = o.GatePending;
        TheaterPending = o.TheaterPending;
        MinervaSpace = o.MinervaSpace;
        ReturnedCardsAge = o.ReturnedCardsAge;
        Awaiting = o.Awaiting;
    }

    public override bool SameAs(ExpansionState other) =>
        other is PantheonState o
        && SlotTokens.AsSpan().SequenceEqual(o.SlotTokens)
        && HeldMythology.AsSpan().SequenceEqual(o.HeldMythology)
        && HeldOfferings.AsSpan().SequenceEqual(o.HeldOfferings)
        && Spaces.AsSpan().SequenceEqual(o.Spaces)
        && FaceDown == o.FaceDown && PlacedByP2 == o.PlacedByP2
        && DeckCount.AsSpan().SequenceEqual(o.DeckCount)
        && DeckCards.AsSpan().SequenceEqual(o.DeckCards)
        && DeckAudience.AsSpan().SequenceEqual(o.DeckAudience)
        && DivinitiesSeen == o.DivinitiesSeen
        && Draw.AsSpan().SequenceEqual(o.Draw)
        && EnkiTokens == o.EnkiTokens && Awaiting == o.Awaiting
        && ActivatedBy.AsSpan().SequenceEqual(o.ActivatedBy) && Astarte.AsSpan().SequenceEqual(o.Astarte)
        && SnakeCard == o.SnakeCard && ZeusSlot == o.ZeusSlot
        && GatePending == o.GatePending && TheaterPending == o.TheaterPending && MinervaSpace == o.MinervaSpace
        && ReturnedCardsAge == o.ReturnedCardsAge;

    public override void Write(BinaryWriter w)
    {
        foreach (var t in SlotTokens) w.Write(t);
        w.Write(HeldMythology);
        w.Write(HeldOfferings);
        foreach (var d in Spaces) w.Write(d);
        w.Write(FaceDown);
        w.Write(PlacedByP2);
        w.Write(DeckCount);
        foreach (var c in DeckCards) w.Write(c);
        w.Write(DeckAudience);
        w.Write(DivinitiesSeen);
        foreach (var d in Draw) w.Write(d);
        w.Write(EnkiTokens);
        w.Write((byte)Awaiting);
        foreach (var a in ActivatedBy) w.Write(a);
        w.Write(Astarte);
        w.Write(SnakeCard);
        w.Write(ZeusSlot);
        w.Write(GatePending);
        w.Write(TheaterPending);
        w.Write(MinervaSpace);
        w.Write(ReturnedCardsAge);
    }

    /// <summary>A few lines for <see cref="StatePrinter"/>.</summary>
    internal string Describe(int age)
    {
        static string Slug(int d) => PantheonData.Divinities[d].Id;
        var lines = new List<string>();
        var spaces = Enumerable.Range(0, PantheonData.SpaceCount).Select(i =>
            $"{i + 1}:{(Spaces[i] == EmptySpace ? "--"
                : IsHidden(i) ? $"?{PantheonData.MythologyDef(SpaceMythology(i)!.Value).Id}(face down, P{Placer(i) + 1})"
                : IsFaceDown(i) ? $"{Slug(Spaces[i])}(face down, P{Placer(i) + 1})" : Slug(Spaces[i]))}");
        lines.Add("Pantheon: " + string.Join(' ', spaces));
        var decks = PantheonData.Mythologies.Select(m =>
        {
            var known = Enumerable.Range(0, DeckCount[(int)m.Mythology])
                .Select(p => DeckCard(m.Mythology, p) is var c and >= 0 ? Slug(c)
                    : DeckKnownBy(m.Mythology, p) is var who and > 0 ? $"?(P{(who == 1 ? 1 : 2)} knows)" : "?");
            return $"{m.Id} [{string.Join(' ', known)}]";
        });
        lines.Add("Mythology decks (top first): " + string.Join(", ", decks));
        for (var p = 0; p < 2; p++)
        {
            var held = PantheonData.Mythologies.Where(m => MythologyTokens(p, m.Mythology) > 0)
                .Select(m => MythologyTokens(p, m.Mythology) == 1 ? m.Id : $"{m.Id} x2")
                .Concat(PantheonData.Offerings.Where(o => (HeldOfferings[p] & 1 << o.Index) != 0).Select(o => o.Id)).ToList();
            if (held.Count > 0) lines.Add($"P{p + 1} holds: {string.Join(' ', held)}");
            if (ActivatedBy[p] != 0)
                lines.Add($"P{p + 1} activated: {string.Join(' ', new Bits(ActivatedBy[p]).ToArray().Select(Slug))}"
                    + (Astarte[p] > 0 ? $" (Astarte's coins: {Astarte[p]})" : ""));
        }
        var onSlots = Enumerable.Range(0, GameState.SlotCount).Where(i => SlotTokens[i] != NoToken && age > 0)
            .Select(i => $"{GameData.Layout(age).SlotName(i)} {TokenSlug(SlotTokens[i])}").ToList();
        if (onSlots.Count > 0) lines.Add("Tokens on the structure: " + string.Join(' ', onSlots));
        if (HasDraw) lines.Add($"Drawn: {string.Join(' ', Draw.Where(d => d != -1).Select(d => d == Hidden ? "?" : Slug(d)))}");
        if (EnkiTokens != 0) lines.Add($"Enki's tokens: {ProgressTokens.Names(EnkiTokens)}");
        if (SnakeCard >= 0) lines.Add($"Nisaba's Snake: {GameData.Card(SnakeCard).Id}");
        if (MinervaSpace != NoMinerva) lines.Add($"Minerva: Military track space {MinervaSpace}");
        if (GatePending) lines.Add("The Gate: waiting for the deck tops' Reveal");
        if (TheaterPending >= 0) lines.Add($"The Divine Theater: looking through the {PantheonData.Mythologies[TheaterPending].Id} deck");
        if (ZeusSlot >= 0 && age > 0) lines.Add($"Zeus: discarding the face-down card on {GameData.Layout(age).SlotName(ZeusSlot)}");
        return string.Join('\n', lines) + '\n';
    }
}
