namespace SevenWondersDuel.Engine;

/// <summary>Who starts the next Age when military is tied (spec A3): the newer rule lets the last taker choose.</summary>
public enum StartChoice : byte { New, Rule2015 }

/// <summary>The optional promo Wonders switched into the Wonder pool (spec A4), one flag per promo.</summary>
[Flags]
public enum Promos : byte { None = 0, Messe = 1, Liberty = 2 }

/// <summary>The expansions the Ruleset grammar knows (spec A10). The Engine supports Pantheon; Agora is refused.</summary>
[Flags]
public enum Expansions : byte { None = 0, Pantheon = 1, Agora = 2 }

/// <summary>
/// The rule options fixed at setup and written as the Move Log's rules header (spec A4):
/// <code>rules base start-choice=new promo=messe,liberty expansion=pantheon</code>
/// Options may come in any order, each at most once; omitted ones take their default (start-choice=new, no promos,
/// no expansions). <see cref="ToString"/> writes start-choice always and the others only when switched on, in the
/// order above. <see cref="Parse"/> reads the whole grammar (Agora included), then rejects Agora with a
/// <see cref="NotSupportedException"/> ("not supported"), so a Ruleset never holds it.
/// </summary>
public sealed record Ruleset(StartChoice StartChoice = StartChoice.New, Promos Promos = Promos.None, Expansions Expansions = Expansions.None)
{
    public static readonly Ruleset Default = new();

    public bool HasPromo(Promos promo) => (Promos & promo) == promo;
    public bool HasExpansion(Expansions expansion) => (Expansions & expansion) == expansion;

    public override string ToString()
    {
        var text = $"rules base start-choice={(StartChoice == StartChoice.New ? "new" : "2015")}";
        if (Promos != Promos.None) text += " promo=" + string.Join(',', PromoNames.Where(p => HasPromo(p.Flag)).Select(p => p.Name));
        if (Expansions != Expansions.None)
            text += " expansion=" + string.Join(',', ExpansionNames.Where(e => HasExpansion(e.Flag)).Select(e => e.Name));
        return text;
    }

    static readonly (string Name, Promos Flag)[] PromoNames = [("messe", Promos.Messe), ("liberty", Promos.Liberty)];
    static readonly (string Name, Expansions Flag)[] ExpansionNames = [("pantheon", Expansions.Pantheon), ("agora", Expansions.Agora)];

    public static Ruleset Parse(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || parts[0] != "rules" || parts[1] != "base")
            throw new FormatException($"Expected a rules header starting 'rules base', got '{line}'");

        var rules = Default;
        var seen = new HashSet<string>();
        foreach (var option in parts.Skip(2))
        {
            var eq = option.IndexOf('=');
            if (eq <= 0) throw new FormatException($"Expected 'key=value', got '{option}'");
            var (key, value) = (option[..eq], option[(eq + 1)..]);
            if (!seen.Add(key)) throw new FormatException($"Rules option '{key}' is given twice");
            switch (key)
            {
                case "start-choice":
                    rules = rules with
                    {
                        StartChoice = value switch
                        {
                            "new" => StartChoice.New,
                            "2015" => StartChoice.Rule2015,
                            _ => throw new FormatException($"Unknown start-choice '{value}' (expected new or 2015)"),
                        },
                    };
                    break;
                case "promo":
                    rules = rules with { Promos = ParseList(value, PromoNames, "promo") };
                    break;
                case "expansion":
                    rules = rules with { Expansions = ParseList(value, ExpansionNames, "expansion") };
                    break;
                default:
                    throw new FormatException($"Unknown rules option '{option}'");
            }
        }

        if (rules.HasExpansion(Expansions.Agora))
            throw new NotSupportedException("Expansion 'agora' is not supported by the Engine (base game, promo Wonders and Pantheon only)");
        return rules;
    }

    /// <summary>A comma-separated list of known names ("messe,liberty"), each at most once, as flags.</summary>
    static T ParseList<T>(string value, (string Name, T Flag)[] known, string what) where T : struct, Enum
    {
        if (value.Length == 0) throw new FormatException($"'{what}=' needs at least one name");
        var bits = 0;
        foreach (var name in value.Split(','))
        {
            var match = Array.FindIndex(known, k => k.Name == name);
            if (match < 0)
                throw new FormatException($"Unknown {what} '{name}' (expected {string.Join(" or ", known.Select(k => k.Name))})");
            var flag = Convert.ToInt32(known[match].Flag);
            if ((bits & flag) != 0) throw new FormatException($"{what} '{name}' is listed twice");
            bits |= flag;
        }
        return (T)Enum.ToObject(typeof(T), bits);
    }
}
