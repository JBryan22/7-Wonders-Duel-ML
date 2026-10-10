namespace SevenWondersDuel.Engine.Data;

/// <summary>The five resources, in the order used by every per-resource array.</summary>
public enum Resource : byte { Wood, Clay, Stone, Glass, Papyrus }

public enum CardColor : byte { Brown, Grey, Red, Green, Yellow, Blue, Purple }

/// <summary>Scientific symbols. Law exists only on the Law Progress token.</summary>
public enum ScienceSymbol : byte { Globe, Law, Sundial, Mortar, Compass, Quill, Wheel }

public enum GuildKind : byte { None, Merchants, Shipowners, Builders, Magistrates, Scientists, Moneylenders, Tacticians }

/// <summary>What a yellow Age III card pays one-time coins per.</summary>
public enum CoinsPerKind : byte { None, Brown, Grey, Red, Yellow, Wonder }

/// <summary>A Wonder power that asks its owner for a choice.</summary>
public enum WonderEffect : byte { None, DestroyBrown, DestroyGrey, Library, Mausoleum, Messe, Liberty }

public static class Resources
{
    public const int Count = 5;

    public static Resource FromLetter(char c) => c switch
    {
        'W' => Resource.Wood,
        'C' => Resource.Clay,
        'S' => Resource.Stone,
        'G' => Resource.Glass,
        'P' => Resource.Papyrus,
        _ => throw new FormatException($"Unknown resource letter '{c}'"),
    };

    public static char Letter(Resource r) => "WCSGP"[(int)r];
}
