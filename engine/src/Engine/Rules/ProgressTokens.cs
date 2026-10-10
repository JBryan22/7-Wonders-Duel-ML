using SevenWondersDuel.Engine.Data;

namespace SevenWondersDuel.Engine;

/// <summary>
/// The Progress tokens' indices, for the effects the Engine implements by id (rules-reference §6). VP, immediate
/// coins and Law's science symbol come from the token data; everything else is a rule in the Engine.
/// </summary>
public static class ProgressTokens
{
    /// <summary>Tokens set out face up on the board at setup; the rest stay in the box.</summary>
    public const int OnBoard = 5;
    /// <summary>VP per token owned, Mathematics included, at the end of the game.</summary>
    public const int MathematicsPointsPerToken = 3;
    /// <summary>Coins Urbanism pays each time its owner builds a card for free through a chain.</summary>
    public const int UrbanismChainCoins = 4;
    /// <summary>Resources Architecture (Wonders) and Masonry (blue cards) take off each cost.</summary>
    public const int Discount = 2;

    public static int Count => GameData.Tokens.Length;
    /// <summary>Every token, one bit per index.</summary>
    public static readonly ushort All = (ushort)((1 << GameData.Tokens.Length) - 1);

    public static readonly int Agriculture = GameData.TokenIndex("agriculture");
    public static readonly int Architecture = GameData.TokenIndex("architecture");
    public static readonly int Economy = GameData.TokenIndex("economy");
    public static readonly int Law = GameData.TokenIndex("law");
    public static readonly int Masonry = GameData.TokenIndex("masonry");
    public static readonly int Mathematics = GameData.TokenIndex("mathematics");
    public static readonly int Philosophy = GameData.TokenIndex("philosophy");
    public static readonly int Strategy = GameData.TokenIndex("strategy");
    public static readonly int Theology = GameData.TokenIndex("theology");
    public static readonly int Urbanism = GameData.TokenIndex("urbanism");

    /// <summary>The tokens in a bitmask, in index order.</summary>
    public static Bits In(ushort mask) => new(mask);

    public static string Names(ushort mask) => string.Join(' ', In(mask).Select(t => GameData.Tokens[t].Id));
}
