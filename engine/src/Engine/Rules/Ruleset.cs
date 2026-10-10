namespace SevenWondersDuel.Engine;

/// <summary>Who starts the next Age when military is tied (spec A3): the newer rule lets the last taker choose.</summary>
public enum StartChoice : byte { New, Rule2015 }

/// <summary>
/// The rule options fixed at setup and written as the Move Log's rules header, e.g. "rules base start-choice=new".
/// Promo switches arrive in tickets 12-13; ticket 09 finishes the grammar.
/// </summary>
public sealed record Ruleset(StartChoice StartChoice = StartChoice.New)
{
    public static readonly Ruleset Default = new();

    public override string ToString() =>
        $"rules base start-choice={(StartChoice == StartChoice.New ? "new" : "2015")}";

    public static Ruleset Parse(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || parts[0] != "rules" || parts[1] != "base")
            throw new FormatException($"Expected a rules header starting 'rules base', got '{line}'");

        var rules = Default;
        foreach (var option in parts.Skip(2))
        {
            var eq = option.IndexOf('=');
            var (key, value) = eq < 0 ? (option, "") : (option[..eq], option[(eq + 1)..]);
            rules = key switch
            {
                "start-choice" => rules with
                {
                    StartChoice = value switch
                    {
                        "new" => StartChoice.New,
                        "2015" => StartChoice.Rule2015,
                        _ => throw new FormatException($"Unknown start-choice '{value}' (expected new or 2015)"),
                    },
                },
                "expansion" => throw new NotSupportedException($"Expansion '{value}' is not supported by the v1 Engine"),
                _ => throw new FormatException($"Unknown rules option '{option}'"),
            };
        }
        return rules;
    }
}
