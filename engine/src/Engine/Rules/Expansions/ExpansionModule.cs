using SevenWondersDuel.Engine.Data;

namespace SevenWondersDuel.Engine;

/// <summary>
/// The seam an expansion plugs into (spec A10, spec-pantheon P-A4 "Engine structure"): one module per expansion,
/// called by <see cref="GameState"/> at named points (setup, Reveals, legal Moves, applying a Move, end of turn,
/// scoring), with its state in an optional <see cref="ExpansionState"/> block. A base game has no module and no
/// block, so the base rules run exactly as before: every hook is behind a null check, with no RNG draws and no
/// change to iteration order. Modules are stateless singletons; everything they change lives in the
/// <see cref="GameState"/> and its block.
/// </summary>
internal abstract class ExpansionModule
{
    /// <summary>The module a Ruleset switches on, or null for the base game (with or without promos).</summary>
    public static ExpansionModule? For(Ruleset rules) => rules.Expansions switch
    {
        Expansions.None => null,
        Expansions.Pantheon => PantheonModule.Instance,
        _ => throw new NotSupportedException($"{rules}: only the Pantheon expansion is supported"),
    };

    /// <summary>A fresh state block for a new game, or null if the expansion keeps no state.</summary>
    public virtual ExpansionState? NewState() => null;

    /// <summary>Wonders this expansion adds to the draft pool, one bit per Wonder index.</summary>
    public virtual ushort ExtraWonders => 0;

    /// <summary>Progress tokens this expansion adds to the token pool, one bit per token index.</summary>
    public virtual ushort ExtraTokens => 0;

    /// <summary>Tokens of the box that are not in it any more (taken out by an expansion effect), one bit per token.</summary>
    public virtual ushort TokensOutOfBox(GameState s) => 0;

    /// <summary>The cards Age III's special backs hide instead of the Guilds, or null for the Guilds.</summary>
    public virtual CardSet? AgeThreeBacks => null;

    /// <summary>
    /// Setup: <paramref name="age"/> 0 when the game is created (before any Reveal), then 1..3 when each Age is laid
    /// out, after the base layout and before its Reveals and first decision.
    /// </summary>
    public virtual void Setup(GameState s, int age) { }

    /// <summary>True while the game waits for one of this expansion's Reveals (asked after the base game's own).</summary>
    public virtual bool AwaitingReveal(GameState s) => false;

    /// <summary>Applies one of this expansion's Reveals; false if it is not one (the base game then rejects it).</summary>
    public virtual bool TryApply(GameState s, Reveal reveal) => false;

    /// <summary>
    /// The Reveal a <see cref="RandomChanceSource"/> draws while <see cref="AwaitingReveal"/> (asked once the flips
    /// are revealed), using its <paramref name="rng"/>.
    /// </summary>
    public virtual Reveal NextReveal(GameState s, Rng rng) => throw new InvalidOperationException("The expansion awaits no Reveal");

    /// <summary>
    /// The legal Moves of a Pending Decision only this expansion knows, written to <paramref name="buffer"/>; -1 if
    /// the front decision is the base game's.
    /// </summary>
    public virtual int LegalMoves(GameState s, Span<Move> buffer) => -1;

    /// <summary>
    /// Adds this expansion's Moves to a base take-card decision (after the base Moves) and returns the new count.
    /// </summary>
    public virtual int AddTakeCardMoves(GameState s, int player, Span<Move> buffer, int count) => count;

    /// <summary>
    /// Judges a Move this expansion owns (one of its Move kinds, or any answer to one of its decisions): true with
    /// <paramref name="why"/> null if legal, or the reason; false if the base game judges it.
    /// </summary>
    public virtual bool TryWhyIllegal(GameState s, Move move, out string? why)
    {
        why = null;
        return false;
    }

    /// <summary>
    /// Applies a legal Move this expansion owns. <paramref name="decision"/> is the Pending Decision it answers,
    /// already taken off the queue. False if the base game applies it.
    /// </summary>
    public virtual bool TryApply(GameState s, Move move, PendingDecision decision) => false;

    /// <summary>End-of-turn checks: called as a turn ends, before the base game checks for the end of the Age.</summary>
    public virtual void EndOfTurn(GameState s) { }

    /// <summary>
    /// The price of building a card after the base rules (chain, Masonry, trading) have quoted it: an expansion may
    /// lower it. Never called for a chained card, which is free.
    /// </summary>
    public virtual PaymentQuote AdjustQuote(GameState s, int player, int card, PaymentQuote quote) => quote;

    /// <summary>The Conflict Pawn has just moved one space toward <paramref name="player"/>'s opponent's capital (not reaching it).</summary>
    public virtual void PawnAdvanced(GameState s, int player) { }

    /// <summary>How many more times a player shows a scientific symbol, beyond green cards and Progress tokens.</summary>
    public virtual int ExtraSymbols(GameState s, int player, ScienceSymbol symbol) => 0;

    /// <summary>Coins a player can spend that are kept apart from the treasury (and safe from coin losses).</summary>
    public virtual int ExtraCoins(GameState s, int player) => 0;

    /// <summary>Takes <paramref name="amount"/> of the <see cref="ExtraCoins"/> (the treasury is spent first).</summary>
    public virtual void SpendExtraCoins(GameState s, int player, int amount) =>
        throw new InvalidOperationException("The expansion keeps no coins apart");

    /// <summary>Adds this expansion's VP to one player's points per <see cref="ScoreCategory"/>.</summary>
    public virtual void Score(GameState s, int player, Span<int> points) { }
}

/// <summary>
/// An expansion's optional block of Game State (spec A10). It is copied, compared and written to the canonical bytes
/// with the rest of the state; a base game has none, so its bytes are unchanged.
/// </summary>
public abstract class ExpansionState
{
    public abstract ExpansionState Clone();
    /// <summary>Overwrites this block with <paramref name="other"/> (same expansion) without allocating.</summary>
    public abstract void CopyFrom(ExpansionState other);
    public abstract bool SameAs(ExpansionState other);
    /// <summary>Every field <see cref="SameAs"/> compares, as canonical little-endian bytes.</summary>
    public abstract void Write(BinaryWriter w);
}
