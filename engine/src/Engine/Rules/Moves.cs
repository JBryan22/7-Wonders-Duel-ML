namespace SevenWondersDuel.Engine;

/// <summary>The kinds of Move. Expansion kinds come after the base ones (Pantheon: <see cref="Divinity"/> onward).</summary>
public enum MoveKind : byte { Build, Discard, StartMe, StartOpponent, Token, Draft, Wonder, Destroy, Mausoleum, Library, Messe, LibertyKeep, LibertyGive, Divinity, Activate, Offer }

/// <summary>
/// One decision that resolves the front Pending Decision. Payment is never part of a Move:
/// the Engine always pays the cheapest way (spec A4). <see cref="Card"/> is -1 for Moves that name no card;
/// for a <see cref="MoveKind.Token"/> or <see cref="MoveKind.Library"/> Move it holds the Progress token index
/// instead (see <see cref="Token"/>).
/// <see cref="Wonder"/> is the Wonder a <see cref="MoveKind.Draft"/> Move drafts or a <see cref="MoveKind.Wonder"/>
/// Move builds (the tucked Age card is then in <see cref="Card"/>); -1 for every other Move.
/// A Pantheon <see cref="MoveKind.Divinity"/> Move holds the Divinity in <see cref="Card"/> and the Pantheon space
/// (0..5) in <see cref="Wonder"/> (see <see cref="Divinity"/> and <see cref="Space"/>); an <see cref="MoveKind.Activate"/>
/// Move holds the Divinity (or the Gate) in <see cref="Card"/>, an <see cref="MoveKind.Offer"/> Move the Offering tokens
/// spent as a mask in <see cref="Card"/> (see <see cref="Offerings"/>).
/// </summary>
public readonly record struct Move(MoveKind Kind, int Card, int Wonder = -1)
{
    public static Move Build(int card) => new(MoveKind.Build, card);
    public static Move Discard(int card) => new(MoveKind.Discard, card);
    /// <summary>"wonder baths sphinx": take the Age card and build the Wonder with it, the card tucked under it.</summary>
    public static Move BuildWonder(int card, int wonder) => new(MoveKind.Wonder, card, wonder);
    /// <summary>"draft sphinx": take a Wonder from the draft offer.</summary>
    public static Move Draft(int wonder) => new(MoveKind.Draft, -1, wonder);
    /// <summary>"start me": the deciding player starts the new Age.</summary>
    public static readonly Move StartMe = new(MoveKind.StartMe, -1);
    /// <summary>"start opponent": the deciding player's opponent starts the new Age.</summary>
    public static readonly Move StartOpponent = new(MoveKind.StartOpponent, -1);
    /// <summary>"token law": take a Progress token from the board after a science pair.</summary>
    public static Move PickToken(int token) => new(MoveKind.Token, token);
    /// <summary>"destroy lumber-yard": the Statue of Zeus / Circus Maximus sends an opponent's brown / grey card to the discard pile.</summary>
    public static Move Destroy(int card) => new(MoveKind.Destroy, card);
    /// <summary>"mausoleum baths": build a card of the discard pile for free.</summary>
    public static Move Mausoleum(int card) => new(MoveKind.Mausoleum, card);
    /// <summary>"messe tavern": the Messe builds a card of the current Age's top row for free, even a covered one.</summary>
    public static Move Messe(int card) => new(MoveKind.Messe, card);
    /// <summary>"liberty-keep study": the Statue of Liberty's owner builds this returned card for free.</summary>
    public static Move LibertyKeep(int card) => new(MoveKind.LibertyKeep, card);
    /// <summary>"liberty-give circus": the opponent builds this returned card for free; the third goes to the discard pile.</summary>
    public static Move LibertyGive(int card) => new(MoveKind.LibertyGive, card);
    /// <summary>"library law": keep one of the 3 Progress tokens the Great Library drew.</summary>
    public static Move Library(int token) => new(MoveKind.Library, token);

    /// <summary>
    /// "divinity zeus 3" (Pantheon): place one of the 2 Divinities just drawn face down on an empty Pantheon space
    /// (<paramref name="space"/> 0..5 is space 1..6); the other goes back on top of its deck.
    /// </summary>
    public static Move PlaceDivinity(int divinity, int space) => new(MoveKind.Divinity, divinity, space);

    /// <summary>
    /// "activate aphrodite", "activate gate" (Pantheon, from Age II): a main action instead of taking a card. Pays the
    /// player's cost for the card's space (Offering tokens are chosen by a following <c>offer</c> decision).
    /// </summary>
    public static Move Activate(int divinity) => new(MoveKind.Activate, divinity);
    /// <summary>
    /// "offer 2+4", "offer none" (Pantheon): the Offering tokens spent on the activation just chosen, one bit per
    /// Offering index; coins pay the rest (R8).
    /// </summary>
    public static Move Offer(int offerings) => new(MoveKind.Offer, offerings);

    /// <summary>The Divinity a <see cref="MoveKind.Divinity"/> Move places or an <see cref="MoveKind.Activate"/> Move activates, or -1.</summary>
    public int Divinity => Kind is MoveKind.Divinity or MoveKind.Activate ? Card : -1;
    /// <summary>The Offering tokens an <see cref="MoveKind.Offer"/> Move spends (one bit per Offering index), or -1.</summary>
    public int Offerings => Kind == MoveKind.Offer ? Card : -1;
    /// <summary>The Pantheon space (0..5) a <see cref="MoveKind.Divinity"/> Move places on, or -1.</summary>
    public int Space => Kind == MoveKind.Divinity ? Wonder : -1;

    /// <summary>The Progress token a <see cref="MoveKind.Token"/> or <see cref="MoveKind.Library"/> Move takes, or -1.</summary>
    public int Token => Kind is MoveKind.Token or MoveKind.Library ? Card : -1;

    /// <summary>True for the Moves that answer a take-card decision: build, discard, or build a Wonder.</summary>
    public bool TakesCard => Kind is MoveKind.Build or MoveKind.Discard or MoveKind.Wonder;

    public override string ToString() => MoveLog.FormatMove(this);
}

public enum DecisionKind : byte
{
    /// <summary>Take an accessible card: build it, discard it, or build a Wonder with it.</summary>
    TakeCard,
    /// <summary>Choose who starts the new Age: the weaker military, or on a tie per <see cref="Ruleset.StartChoice"/>.</summary>
    ChooseStarter,
    /// <summary>Take a Progress token from the board after a science pair: compulsory while any remain (spec A3).</summary>
    ChooseToken,
    /// <summary>Take a Wonder from the draft offer (picks A-B-B-A, rules-reference §1). The last Wonder is still a Move.</summary>
    DraftWonder,
    /// <summary>
    /// The Statue of Zeus (brown) or Circus Maximus (grey): send one of the opponent's cards of that colour to the
    /// discard pile. Compulsory; never pushed when the opponent has no card of the colour.
    /// </summary>
    Destroy,
    /// <summary>The Mausoleum: build one card of the discard pile for free. Compulsory; never pushed on an empty pile.</summary>
    Mausoleum,
    /// <summary>The Great Library: keep 1 of the 3 Progress tokens drawn privately from the box (a Reveal comes first).</summary>
    Library,
    /// <summary>
    /// The Messe (promo): build one card of the current Age's top row for free, covered or not. Compulsory; never
    /// pushed when the top row is empty.
    /// </summary>
    Messe,
    /// <summary>
    /// The Statue of Liberty (promo): choose which of the 3 returned cards its owner builds for free. Comes after the
    /// cards' Reveal; never skipped.
    /// </summary>
    LibertyKeep,
    /// <summary>
    /// The Statue of Liberty: choose which of the 2 other returned cards the opponent builds for free (the last one
    /// goes to the discard pile). The owner decides; then the owner's card resolves fully, then the opponent's.
    /// </summary>
    LibertyGive,
    /// <summary>
    /// Pantheon: place one of the 2 Divinities drawn for a Mythology token on an empty Pantheon space. Comes after
    /// the draw's private Reveal; <see cref="PendingDecision.Wonder"/> holds the <see cref="Data.Mythology"/>.
    /// </summary>
    PlaceDivinity,
    /// <summary>
    /// Pantheon: which Offering tokens to spend on the activation just chosen (R8). Pushed only when the player holds
    /// any and the cost is above 0; <see cref="PendingDecision.Wonder"/> holds the Divinity (or Gate) being activated,
    /// which stays on its space until the payment.
    /// </summary>
    Offer,
}

/// <summary>
/// A choice the Game State is waiting on, owned by the player who must make it. <see cref="Wonder"/> is the Wonder
/// whose power asked for it (Destroy, Mausoleum, Library, Messe, LibertyKeep, LibertyGive), the Mythology of a
/// Pantheon <see cref="DecisionKind.PlaceDivinity"/>, the Divinity of a Pantheon <see cref="DecisionKind.Offer"/>, or -1.
/// </summary>
public readonly record struct PendingDecision(DecisionKind Kind, int Player, int Wonder = -1);

/// <summary>What the Game State needs next.</summary>
public enum StepKind : byte { Reveal, Decision, GameOver }

/// <summary>One line of the Move Log: a Move or a Reveal.</summary>
public abstract record LogEntry
{
    public override string ToString() => MoveLog.Format(this);
}

/// <summary>A Move made by a player ("P1 build lumber-yard").</summary>
public sealed record MoveEntry(int Player, Move Move) : LogEntry
{
    public override string ToString() => MoveLog.Format(this);
}

/// <summary>A chance outcome at the moment it becomes known (ADR 0002). <see cref="Audience"/> is -1 for both players.</summary>
public abstract record Reveal : LogEntry
{
    public virtual int Audience => -1;
}

/// <summary>Who plays first ("reveal first P1").</summary>
public sealed record FirstPlayerReveal(int Player) : Reveal
{
    public override string ToString() => MoveLog.Format(this);
}

/// <summary>
/// The 5 Progress tokens set out face up on the board at setup ("reveal tokens agriculture law ..."). Comes right
/// after the first player; the other 5 stay in the box unnamed (the Great Library draws from them).
/// </summary>
public sealed record TokenReveal(int[] Tokens) : Reveal
{
    public override string ToString() => MoveLog.Format(this);
}

/// <summary>
/// The 4 Wonders offered in one draft round ("reveal draft pyramids colossus sphinx appian-way"), drawn from the
/// Wonders of the pool nobody has seen yet. Round 1 comes after the Progress tokens, round 2 after the 4th pick.
/// </summary>
public sealed record DraftReveal(int[] Wonders) : Reveal
{
    public override string ToString() => MoveLog.Format(this);
}

/// <summary>
/// Which Age III slots hold a Guild ("reveal guilds 0.1 3.0 5.2"). Guild backs are purple, so both players
/// see where the 3 Guilds lie before any card is named; the opening Reveal then names the face-up cards.
/// With Pantheon the 3 special backs are Grand Temples instead (<see cref="Temples"/>, "reveal temples 0.1 3.0 5.2", R6).
/// </summary>
public sealed record GuildPatternReveal(int[] Slots, bool Temples = false) : Reveal
{
    public override string ToString() => MoveLog.Format(this);
}

/// <summary>
/// Layout cards becoming known: the face-up cards of a new Age (<see cref="Opening"/>, "reveal age1 0.0 a 0.1 b ...")
/// or a face-down card flipped once it becomes accessible ("reveal 3.1 baths").
/// </summary>
public sealed record SlotReveal(int Age, bool Opening, (int Slot, int Card)[] Cards) : Reveal
{
    public override string ToString() => MoveLog.Format(this);
}

/// <summary>
/// The Great Library's draw ("reveal P1 library law strategy theology"): 3 of the 5 Progress tokens left in the
/// box at setup. Private: only <see cref="Player"/> sees it (spec A4), so <see cref="GameState.Observe"/> hides it
/// from the opponent.
/// <para>
/// A <see cref="Redacted"/> draw ("reveal P2 library ? ? ?", no tokens) is how a log kept by someone outside the
/// audience records it, e.g. the Advisor logging the opponent's draw: it sets <see cref="GameState.LibraryDrawHidden"/>
/// and the tokens stay unknown forever.
/// </para>
/// </summary>
public sealed record LibraryReveal(int Player, int[] Tokens) : Reveal
{
    /// <summary>The draw as logged by someone who did not see it.</summary>
    public static LibraryReveal Hidden(int player) => new(player, []);

    public bool Redacted => Tokens.Length == 0;
    public override int Audience => Player;
    public override string ToString() => MoveLog.Format(this);
}

/// <summary>
/// The Statue of Liberty's 3 returned cards ("reveal returned age2 brewery caravansery school"): the cards of the
/// current Age removed at setup (in Age III never Guilds), drawn from the Age's unseen non-Guild cards. Public; it
/// comes right after the flips caused by the Statue's own Age card, before liberty-keep.
/// </summary>
public sealed record ReturnedReveal(int Age, int[] Cards) : Reveal
{
    public override string ToString() => MoveLog.Format(this);
}
