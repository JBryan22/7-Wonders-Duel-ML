using SevenWondersDuel.Engine.Data;

namespace SevenWondersDuel.Engine;

/// <summary>
/// Pantheon's block of Game State (spec-pantheon P-A3): tokens on structure slots and held, the Pantheon spaces, the
/// Mythology decks, the current draw and Enki's tokens. The rules that change it are in <see cref="PantheonModule"/>.
/// <para>
/// Like the Great Library's draw, a placed face-down Divinity and a card put back on a deck are stored with their
/// identity, because one player knows them (spec A4: the Observation hides what a player has not seen).
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
    /// <summary>The 2 Divinities drawn for the pending <c>divinity</c> decision (top card first), or -1.</summary>
    internal readonly sbyte[] Draw = new sbyte[2];
    /// <summary>The 2 Progress tokens on Enki (from its Reveal until it is activated), one bit per token.</summary>
    internal ushort EnkiTokens;
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

    /// <summary>The Divinity (or Gate) on a Pantheon space (0..5), or <see cref="EmptySpace"/>.</summary>
    public int Space(int space) => Spaces[space];
    public bool IsFaceDown(int space) => (FaceDown & 1 << space) != 0;
    /// <summary>Who placed the card on a space in Age I (meaningful for a placed Divinity).</summary>
    public int Placer(int space) => (PlacedByP2 & 1 << space) != 0 ? 1 : 0;

    public int DeckSize(Mythology m) => DeckCount[(int)m];
    /// <summary>The known card at a deck position (0 = top), or <see cref="Unknown"/>.</summary>
    public int DeckCard(Mythology m, int position) => DeckCards[(int)m * PantheonData.DeckSize + position];
    /// <summary>Who knows the card at a deck position: bit 0 P1, bit 1 P2 (0 when unknown).</summary>
    public int DeckKnownBy(Mythology m, int position) => DeckAudience[(int)m * PantheonData.DeckSize + position];
    /// <summary>The Divinities named by a Reveal so far, one bit per index.</summary>
    public ushort SeenDivinities => DivinitiesSeen;

    /// <summary>True while a <c>divinity</c> decision has its 2 cards drawn.</summary>
    public bool HasDraw => Draw[0] >= 0;
    /// <summary>The drawn cards (top first) while <see cref="HasDraw"/>.</summary>
    public int Drawn(int i) => Draw[i];
    public ushort Enki => EnkiTokens;

    public bool AwaitingMythologyTokens => (Awaiting & SetupStep.MythologyTokens) != 0;
    public bool AwaitingPantheon => (Awaiting & SetupStep.Pantheon) != 0;
    public bool AwaitingOfferings => (Awaiting & SetupStep.Offerings) != 0;
    public bool AwaitingEnki => (Awaiting & SetupStep.Enki) != 0;

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
        && EnkiTokens == o.EnkiTokens && Awaiting == o.Awaiting;

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
    }

    /// <summary>A few lines for <see cref="StatePrinter"/>.</summary>
    internal string Describe(int age)
    {
        static string Slug(int d) => PantheonData.Divinities[d].Id;
        var lines = new List<string>();
        var spaces = Enumerable.Range(0, PantheonData.SpaceCount).Select(i =>
            $"{i + 1}:{(Spaces[i] < 0 ? "--" : IsFaceDown(i) ? $"{Slug(Spaces[i])}(face down, P{Placer(i) + 1})" : Slug(Spaces[i]))}");
        lines.Add("Pantheon: " + string.Join(' ', spaces));
        var decks = PantheonData.Mythologies.Select(m =>
        {
            var known = Enumerable.Range(0, DeckCount[(int)m.Mythology])
                .Select(p => DeckCard(m.Mythology, p) is var c and >= 0 ? Slug(c) : "?");
            return $"{m.Id} [{string.Join(' ', known)}]";
        });
        lines.Add("Mythology decks (top first): " + string.Join(", ", decks));
        for (var p = 0; p < 2; p++)
        {
            var held = PantheonData.Mythologies.Where(m => MythologyTokens(p, m.Mythology) > 0)
                .Select(m => MythologyTokens(p, m.Mythology) == 1 ? m.Id : $"{m.Id} x2")
                .Concat(PantheonData.Offerings.Where(o => (HeldOfferings[p] & 1 << o.Index) != 0).Select(o => o.Id)).ToList();
            if (held.Count > 0) lines.Add($"P{p + 1} holds: {string.Join(' ', held)}");
        }
        var onSlots = Enumerable.Range(0, GameState.SlotCount).Where(i => SlotTokens[i] != NoToken && age > 0)
            .Select(i => $"{GameData.Layout(age).SlotName(i)} {TokenSlug(SlotTokens[i])}").ToList();
        if (onSlots.Count > 0) lines.Add("Tokens on the structure: " + string.Join(' ', onSlots));
        if (HasDraw) lines.Add($"Drawn: {Slug(Draw[0])} {Slug(Draw[1])}");
        if (EnkiTokens != 0) lines.Add($"Enki's tokens: {ProgressTokens.Names(EnkiTokens)}");
        return string.Join('\n', lines) + '\n';
    }
}
