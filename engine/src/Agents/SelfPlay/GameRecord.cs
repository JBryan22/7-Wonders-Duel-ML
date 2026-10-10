using SevenWondersDuel.Engine;

namespace SevenWondersDuel.Agents;

/// <summary>
/// One searched decision of a Self-Play game: <see cref="Entry"/> is the index of its Move in the Move Log (counting
/// every line after the rules header, Reveals included), <see cref="Target"/> the search's policy target over the legal
/// Moves in legal-Move order (<see cref="GameState.LegalMoves(Span{Move})"/>; Gumbel: the improved policy, classic: the
/// visit shares; rounded to <see cref="SelfPlay.Decimals"/> decimals, so the sum is 1 only to within that), and <see cref="Q"/> the search's own value of the position for the player deciding (−1..1, the
/// visit-weighted mean over the root Moves). Q is unused in v1 (no value-target mixing) but can't be recomputed later
/// without rerunning the search, so it is kept.
/// </summary>
public sealed record SearchedDecision(int Entry, float Q, float[] Target);

/// <summary>
/// The Game Record of one Self-Play game (spec A7, "Storage"; kept forever, a few KB): the Move Log (with its rules
/// header), every searched decision's policy target, the result (<see cref="Winner"/> 0 = P1, 1 = P2, −1 = a draw, with
/// how it was won and both final VP totals) and the seeds, so that the game replays exactly: <see cref="ChanceSeed"/>
/// for the Chance Source and <see cref="SeedP1"/> / <see cref="SeedP2"/> for each player's search (its Gumbel noise
/// and its Determinizer). All three come from <see cref="Seed"/> (the Self-Play first seed plus <see cref="Game"/>).
/// Forced decisions (one legal Move) are not searched and not listed: only searched decisions become training examples.
/// </summary>
public sealed record GameRecord(int Game, ulong Seed, ulong ChanceSeed, ulong SeedP1, ulong SeedP2, string Log,
    int Winner, VictoryType Victory, int VpP1, int VpP2, IReadOnlyList<SearchedDecision> Searched, double Seconds)
{
    /// <summary>The rules the game was played under (from the Move Log's header).</summary>
    public Ruleset Rules => Ruleset.Parse(Log.Split('\n', 3)[1]);
}
