using SevenWondersDuel.Engine.Data;

namespace SevenWondersDuel.Engine;

/// <summary>
/// Pantheon's lines of the Move Log (spec-pantheon P-A3, P-A4), written and read for <see cref="MoveLog"/>:
/// <code>
/// rules base start-choice=new expansion=pantheon
/// reveal myth-tokens 1.0 greek 1.2 roman 3.0 egyptian 3.2 phoenician 3.4 greek   (after "reveal age1 ...")
/// reveal P1 divinities greek zeus hades     (private to P1: top card first)
/// P1 divinity zeus 3                        (space 1..6, counted from P1's capital end)
/// reveal pantheon 1 aphrodite 2 baal 3 enki 5 isis 6 mars                        (Age II, after "reveal age2 ...")
/// reveal offerings 1.0 offering-3 1.2 offering-2 1.4 offering-4
/// reveal enki law economy                   (only if Enki is in the Pantheon)
/// reveal temples 0.1 3.0 5.2                (Age III: Grand Temple backs instead of Guild backs)
/// </code>
/// </summary>
internal static class PantheonLog
{
    public static string? Format(LogEntry entry) => entry switch
    {
        MythologyTokensReveal r => "reveal myth-tokens " + string.Join(' ',
            r.Tokens.Select(t => $"{SlotName(1, t.Slot)} {MythologySlug(t.Mythology)}")),
        DivinityDrawReveal r => $"reveal P{r.Player + 1} divinities {MythologySlug(r.Mythology)} "
            + string.Join(' ', r.Divinities.Select(DivinitySlug)),
        PantheonReveal r => "reveal pantheon " + string.Join(' ', r.Divinities.Select(d => $"{d.Space + 1} {DivinitySlug(d.Divinity)}")),
        OfferingsReveal r => "reveal offerings " + string.Join(' ',
            r.Tokens.Select(t => $"{SlotName(2, t.Slot)} {OfferingSlug(t.Offering)}")),
        EnkiReveal r => "reveal enki " + string.Join(' ', r.Tokens.Select(TokenSlug)),
        _ => null,
    };

    public static string? FormatMove(Move move) => move.Kind switch
    {
        MoveKind.Divinity => $"divinity {DivinitySlug(move.Divinity)} {move.Space + 1}",
        _ => null,
    };

    /// <summary>Parses a Pantheon line (already split on spaces), or returns null if it is not one.</summary>
    public static LogEntry? TryParse(string[] t)
    {
        if (t[0] == "reveal" && t.Length > 1)
        {
            switch (t[1])
            {
                case "myth-tokens":
                    return new MythologyTokensReveal(Pairs(t, 2, "reveal myth-tokens <slot> <mythology> ...")
                        .Select(p => (Slot(1, p.A), PantheonData.MythologyOf(p.B))).ToArray());
                case "pantheon":
                    return new PantheonReveal(Pairs(t, 2, "reveal pantheon <space> <divinity> ...")
                        .Select(p => (Space(p.A), PantheonData.DivinityIndex(p.B))).ToArray());
                case "offerings":
                    return new OfferingsReveal(Pairs(t, 2, "reveal offerings <slot> <offering> ...")
                        .Select(p => (Slot(2, p.A), PantheonData.OfferingIndex(p.B))).ToArray());
                case "enki":
                    return new EnkiReveal(t.Skip(2).Select(GameData.AnyTokenIndex).ToArray());
            }
            if (t.Length > 2 && t[2] == "divinities" && t[1] is "P1" or "P2")
            {
                if (t.Length != 6) throw new FormatException("Expected 'reveal P<n> divinities <mythology> <divinity> <divinity>'");
                return new DivinityDrawReveal(t[1] == "P1" ? 0 : 1, PantheonData.MythologyOf(t[3]),
                    [PantheonData.DivinityIndex(t[4]), PantheonData.DivinityIndex(t[5])]);
            }
            return null;
        }
        if (t.Length > 1 && t[1] == "divinity" && t[0] is "P1" or "P2")
        {
            if (t.Length != 4) throw new FormatException("Expected 'P<n> divinity <divinity> <space>'");
            return new MoveEntry(t[0] == "P1" ? 0 : 1, Move.PlaceDivinity(PantheonData.DivinityIndex(t[2]), Space(t[3])));
        }
        return null;
    }

    static IEnumerable<(string A, string B)> Pairs(string[] t, int from, string expected)
    {
        if (t.Length == from || (t.Length - from) % 2 != 0) throw new FormatException($"Expected '{expected}'");
        for (var i = from; i < t.Length; i += 2) yield return (t[i], t[i + 1]);
    }

    /// <summary>A Pantheon space as logged (1..6) to its index (0..5).</summary>
    static int Space(string text) =>
        int.TryParse(text, out var n) && n >= 1 && n <= PantheonData.SpaceCount ? n - 1
        : throw new FormatException($"'{text}' is not a Pantheon space (1 to {PantheonData.SpaceCount})");

    static int Slot(int age, string text)
    {
        var layout = GameData.Layout(age);
        var dot = text.IndexOf('.');
        if (dot < 0 || !int.TryParse(text.AsSpan(0, dot), out var row) || !int.TryParse(text.AsSpan(dot + 1), out var pos)
            || row < 0 || row >= layout.RowCount || pos < 0 || pos >= layout.Slots.Count(s => s.Row == row))
            throw new FormatException($"'{text}' is not a slot of the Age {age} layout");
        return layout.SlotAt(row, pos);
    }

    static string SlotName(int age, int slot) => slot is >= 0 and < GameState.SlotCount ? GameData.Layout(age).SlotName(slot) : $"#{slot}";
    static string MythologySlug(Mythology m) => (int)m is >= 0 and < 5 ? PantheonData.MythologyDef(m).Id : $"#{(int)m}";
    static string DivinitySlug(int d) => d >= 0 && d < PantheonData.Divinities.Length ? PantheonData.Divinities[d].Id : $"#{d}";
    static string OfferingSlug(int o) => o >= 0 && o < PantheonData.Offerings.Length ? PantheonData.Offerings[o].Id : $"#{o}";
    static string TokenSlug(int t) => GameData.IsToken(t) ? GameData.Token(t).Id : $"#{t}";
}
