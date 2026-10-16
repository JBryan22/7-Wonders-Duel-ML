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
/// P1 activate aphrodite                     (from Age II, instead of taking a card; "activate gate" for the Gate)
/// P1 offer 2+4                              (the Offering tokens spent, by value; "offer none")
/// P1 enki law                               (Enki: the token kept)
/// P1 snake study                            (Nisaba: the opponent's green card)
/// P1 steal quarry                           (Baal: the opponent's brown or grey card)
/// P1 hades baths                            (Hades: the discarded card built)
/// P1 zeus 2.3                               (Zeus: always by slot of the current Age, the card may be face down)
/// reveal discarded baths                    (Zeus's face-down card, public, before the flips)
/// P1 anubis pyramids                        (Anubis: a built Wonder of either player)
/// P1 isis baths pyramids                    (Isis: the discarded card, then the Wonder)
/// P1 ra pyramids                            (Ra: the opponent's unbuilt Wonder)
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
        ZeusDiscardReveal r => "reveal discarded " + CardSlug(r.Card),
        _ => null,
    };

    public static string? FormatMove(Move move) => move.Kind switch
    {
        MoveKind.Divinity => $"divinity {DivinitySlug(move.Divinity)} {move.Space + 1}",
        MoveKind.Activate => $"activate {DivinitySlug(move.Divinity)}",
        MoveKind.Offer => "offer " + OfferingsText(move.Offerings),
        MoveKind.Enki => $"enki {TokenSlug(move.Token)}",
        MoveKind.Snake => $"snake {CardSlug(move.Card)}",
        MoveKind.Steal => $"steal {CardSlug(move.Card)}",
        MoveKind.Hades => $"hades {CardSlug(move.Card)}",
        MoveKind.Zeus => $"zeus {(move.Wonder is >= 1 and <= GameState.AgeCount ? SlotName(move.Wonder, move.Slot) : $"#{move.Slot}")}",
        MoveKind.Anubis => $"anubis {WonderSlug(move.Wonder)}",
        MoveKind.Isis => $"isis {CardSlug(move.Card)} {WonderSlug(move.Wonder)}",
        MoveKind.Ra => $"ra {WonderSlug(move.Wonder)}",
        _ => null,
    };

    /// <summary>
    /// Parses a Pantheon line (already split on spaces), or returns null if it is not one. The state supplies the
    /// current Age for Zeus's slot.
    /// </summary>
    public static LogEntry? TryParse(string[] t, GameState state)
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
                case "discarded":
                    if (t.Length != 3) throw new FormatException("Expected 'reveal discarded <card>'");
                    return new ZeusDiscardReveal(GameData.AnyCardIndex(t[2]));
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
        if (t.Length > 1 && t[1] == "activate" && t[0] is "P1" or "P2")
        {
            if (t.Length != 3) throw new FormatException("Expected 'P<n> activate <divinity|gate>'");
            return new MoveEntry(t[0] == "P1" ? 0 : 1, Move.Activate(PantheonData.DivinityIndex(t[2])));
        }
        if (t.Length > 1 && t[1] == "offer" && t[0] is "P1" or "P2")
        {
            if (t.Length != 3) throw new FormatException("Expected 'P<n> offer <values>' (e.g. 'offer 2+4', 'offer none')");
            return new MoveEntry(t[0] == "P1" ? 0 : 1, Move.Offer(ParseOfferings(t[2])));
        }
        if (t.Length > 1 && t[0] is "P1" or "P2" && t[1] is "enki" or "snake" or "steal" or "hades" or "zeus" or "anubis" or "isis" or "ra")
        {
            var isis = t[1] == "isis";
            if (t.Length != (isis ? 4 : 3))
                throw new FormatException(isis ? "Expected 'P<n> isis <card> <wonder>'" : $"Expected 'P<n> {t[1]} <argument>'");
            var move = t[1] switch
            {
                "enki" => Move.Enki(GameData.AnyTokenIndex(t[2])),
                "snake" => Move.Snake(GameData.AnyCardIndex(t[2])),
                "steal" => Move.Steal(GameData.AnyCardIndex(t[2])),
                "hades" => Move.Hades(GameData.AnyCardIndex(t[2])),
                "zeus" => state.Age >= 1 ? Move.Zeus(Slot(state.Age, t[2]), state.Age)
                    : throw new FormatException($"Slot '{t[2]}' named before any Age has started"),
                "anubis" => Move.Anubis(GameData.AnyWonderIndex(t[2])),
                "isis" => Move.Isis(GameData.AnyCardIndex(t[2]), GameData.AnyWonderIndex(t[3])),
                _ => Move.Ra(GameData.AnyWonderIndex(t[2])),
            };
            return new MoveEntry(t[0] == "P1" ? 0 : 1, move);
        }
        return null;
    }

    /// <summary>Offering tokens by value, smallest first ("2+4"), or "none".</summary>
    static string OfferingsText(int offerings) => offerings == 0 ? "none"
        : string.Join('+', PantheonData.Offerings.Where(o => (offerings & 1 << o.Index) != 0).Select(o => o.Value));

    static int ParseOfferings(string text)
    {
        if (text == "none") return 0;
        var mask = 0;
        foreach (var part in text.Split('+'))
        {
            var o = Array.FindIndex(PantheonData.Offerings, o => o.Value.ToString() == part);
            if (o < 0 || (mask & 1 << o) != 0)
                throw new FormatException($"'{text}' is not a set of Offering tokens (values {string.Join(", ", PantheonData.Offerings.Select(x => x.Value))}, joined by '+', or 'none')");
            mask |= 1 << o;
        }
        return mask;
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
    static string CardSlug(int c) => GameData.IsCard(c) ? GameData.Card(c).Id : $"#{c}";
    static string WonderSlug(int w) => GameData.IsWonder(w) ? GameData.Wonder(w).Id : $"#{w}";
}
