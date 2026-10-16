using SevenWondersDuel.Engine.Data;

namespace SevenWondersDuel.Engine;

/// <summary>
/// Pantheon, Age I setup ("reveal myth-tokens 1.0 greek 1.2 roman 3.0 egyptian 3.2 phoenician 3.4 greek"): the
/// Mythology token on each of the 5 R1 slots, drawn from the 10 tokens (2 per Mythology); the other 5 are out of the
/// game. Public. It comes after the Age I opening Reveal.
/// </summary>
public sealed record MythologyTokensReveal((int Slot, Mythology Mythology)[] Tokens) : Reveal
{
    public override string ToString() => MoveLog.Format(this);
}

/// <summary>
/// Pantheon: the top 2 cards of a Mythology deck, drawn by the player who just took that Mythology's token
/// ("reveal P1 divinities greek zeus hades"), top card first. Cards on top of the deck that are already known (a card
/// put back by an earlier draw) are drawn first; the rest come from the deck's unseen cards. Private: only
/// <see cref="Player"/> sees it (spec-pantheon P-A3). The <c>divinity</c> decision follows.
/// </summary>
public sealed record DivinityDrawReveal(int Player, Mythology Mythology, int[] Divinities) : Reveal
{
    public override int Audience => Player;
    public override string ToString() => MoveLog.Format(this);
}

/// <summary>
/// Pantheon, Age II setup ("reveal pantheon 1 aphrodite 2 baal 3 enki 5 isis 6 mars"): every face-down Divinity in
/// the Pantheon, by space (1..6 in the log, 0..5 here). Public from here on.
/// </summary>
public sealed record PantheonReveal((int Space, int Divinity)[] Divinities) : Reveal
{
    public override string ToString() => MoveLog.Format(this);
}

/// <summary>
/// Pantheon, Age II setup ("reveal offerings 1.0 offering-3 1.2 offering-2 1.4 offering-4"): which Offering token lies
/// on each of the 3 R1 slots. Public (R4). It comes after <see cref="PantheonReveal"/>.
/// </summary>
public sealed record OfferingsReveal((int Slot, int Offering)[] Tokens) : Reveal
{
    public override string ToString() => MoveLog.Format(this);
}

/// <summary>
/// Pantheon: Enki has become face up, so 2 Progress tokens are drawn from the box onto it ("reveal enki law economy").
/// Public. At Age II setup it comes after <see cref="OfferingsReveal"/>.
/// </summary>
public sealed record EnkiReveal(int[] Tokens) : Reveal
{
    public override string ToString() => MoveLog.Format(this);
}

/// <summary>
/// Pantheon, Zeus on a face-down card ("reveal discarded baths"): the card going to the discard pile, drawn from what
/// the slot's back could hide. Public (the discard pile is). It comes right after the <c>zeus</c> Move, before the
/// flips of the cards it uncovered.
/// </summary>
public sealed record ZeusDiscardReveal(int Card) : Reveal
{
    public override string ToString() => MoveLog.Format(this);
}
