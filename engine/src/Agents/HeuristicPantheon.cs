using SevenWondersDuel.Engine;
using SevenWondersDuel.Engine.Data;

namespace SevenWondersDuel.Agents;

/// <summary>
/// The Heuristic's Pantheon values (spec-pantheon P-A6). Everything here is reached only through a Pantheon game's
/// <see cref="GameState.Pantheon"/> block, a Pantheon Move kind or a Pantheon identity, so a base game's choices are
/// exactly what they were. Activating a Divinity is scored like any other Move (apply it, follow the player's own
/// choices, evaluate); these terms add what <see cref="HeuristicAgent.Evaluate"/> cannot see on the board: the
/// Divinities still waiting in the Pantheon (worth more to the player they are cheap for), Minerva's protection,
/// tokens held for later, Grand Temples and the new Progress tokens and Wonders.
/// </summary>
public sealed partial class HeuristicAgent
{
    static readonly int Aphrodite = PantheonData.DivinityIndex("aphrodite");
    static readonly int Hades = PantheonData.DivinityIndex("hades");
    static readonly int Zeus = PantheonData.DivinityIndex("zeus");
    static readonly int Astarte = PantheonData.DivinityIndex("astarte");
    static readonly int Baal = PantheonData.DivinityIndex("baal");
    static readonly int Tanit = PantheonData.DivinityIndex("tanit");
    static readonly int Enki = PantheonData.DivinityIndex("enki");
    static readonly int Ishtar = PantheonData.DivinityIndex("ishtar");
    static readonly int Nisaba = PantheonData.DivinityIndex("nisaba");
    static readonly int Anubis = PantheonData.DivinityIndex("anubis");
    static readonly int Isis = PantheonData.DivinityIndex("isis");
    static readonly int Ra = PantheonData.DivinityIndex("ra");
    static readonly int Mars = PantheonData.DivinityIndex("mars");
    static readonly int Minerva = PantheonData.DivinityIndex("minerva");
    static readonly int Neptune = PantheonData.DivinityIndex("neptune");
    static readonly int Sanctuary = PantheonData.WonderIndex("sanctuary");
    static readonly int Mysticism = PantheonData.TokenIndex("mysticism");
    static readonly int Poliorcetics = PantheonData.TokenIndex("poliorcetics");
    static readonly int Engineering = PantheonData.TokenIndex("engineering");

    /// <summary>
    /// The share of a waiting Divinity's net value counted as a player's own: both players compete for it and only some
    /// get activated, so a Divinity cheap for one side is an edge, not a sure gain. Lower in Age I (the board can still
    /// change, and Age II setup comes first).
    /// </summary>
    static double PotentialShare(GameState s) => s.Age <= 1 ? 0.3 : 0.45;

    /// <summary>The Pantheon part of one player's value (added to the base terms in a Pantheon game).</summary>
    static double PantheonPlayerValue(GameState s, PantheonState pan, int p, HeuristicWeights w)
    {
        var value = 0.0;
        var age = AgeLeft(s);

        // Divinities still waiting in the Pantheon: what activating them would be worth, net of this player's cost.
        if (s.Age <= 3)
        {
            var share = PotentialShare(s);
            var coin = CoinWorth(s, w);
            for (var space = 0; space < PantheonData.SpaceCount; space++)
            {
                var d = pan.Space(space);
                if (d == PantheonState.EmptySpace) continue;
                var gain = pan.IsHidden(space) ? MythologyValue(s, p, pan.SpaceMythology(space)!.Value, w) : DivinityValue(s, p, d, w);
                var cost = PantheonData.Spaces[space].Cost(p) * (d == PantheonData.Gate ? 2 : 1);
                if ((s.BuiltWonders(p) & 1 << Sanctuary) != 0) cost = Math.Max(0, cost - 2);
                value += share * Math.Max(0, gain - coin * cost);
            }
        }

        // Tokens held: Mythology tokens make their Grand Temple free in Age III; Offering tokens pay for activations.
        var myth = pan.MythologyTokenCount(p);
        if (myth > 0 && s.Age <= 3) value += 1.2 * w.Points * myth;
        var offerings = pan.OfferingTokens(p);
        if (offerings != 0 && s.Age <= 3)
            foreach (var o in PantheonData.Offerings)
                if ((offerings & 1 << o.Index) != 0) value += 0.6 * CoinWorth(s, w) * o.Value;

        // Minerva on this player's side of the Conflict Pawn stops the opponent's next push into her space.
        if (pan.Minerva != PantheonState.NoMinerva)
        {
            var mine = p == 0 ? pan.Minerva : -pan.Minerva;
            var lead = s.MilitaryLead(p);
            if (mine < lead) value += w.Military * (1.5 + 0.25 * Math.Max(0, -lead)) * 2.0 / (lead - mine);
        }

        // The new Progress tokens beyond their printed VP (Mysticism's VP is in the score already).
        if (s.HasToken(p, Poliorcetics)) value += 2.0 * age * w.Military;
        if (s.HasToken(p, Engineering)) value += 1.5 * age * w.Economy;
        if (s.HasToken(p, Mysticism)) value += 0.5;
        return value;
    }

    /// <summary>A coin's worth in VP-equivalents when spent on an activation: its third of a VP plus its buying power.</summary>
    static double CoinWorth(GameState s, HeuristicWeights w) => w.Points / 3 + w.Coins * CoinValue(s);

    /// <summary>The average value of a face-down Divinity known only by its Mythology.</summary>
    static double MythologyValue(GameState s, int p, Mythology m, HeuristicWeights w)
    {
        var deck = PantheonData.MythologyDef(m).Divinities;
        var total = 0.0;
        foreach (var d in deck) total += DivinityValue(s, p, d, w);
        return total / deck.Length;
    }

    /// <summary>
    /// A rough value of activating Divinity <paramref name="d"/> now for player <paramref name="p"/>, before its cost,
    /// in VP-equivalents. Only used for Divinities still waiting: an actual activation is scored by playing it out.
    /// </summary>
    static double DivinityValue(GameState s, int p, int d, HeuristicWeights w)
    {
        var opp = 1 - p;
        var age = AgeLeft(s);
        if (d == Aphrodite) return 9 * w.Points;
        if (d == Astarte) return 7 * w.Points + 2 * w.Coins * CoinValue(s);
        if (d == Tanit) return 4 * w.Points + 8 * w.Coins * CoinValue(s);
        if (d == Mars)
        {
            var lead = s.MilitaryLead(p);
            if (lead + 2 >= GameState.CapitalDistance) return 40;
            return w.Military * (MilitaryPull(lead + 2) - MilitaryPull(lead))
                + w.Points * (Scoring.MilitaryPoints(lead + 2) - Scoring.MilitaryPoints(lead)) + 1;
        }
        if (d == Ishtar)
        {
            var distinct = DerivedFeatures.ScienceSymbols(s, p);
            if (!DerivedFeatures.IsNewSymbol(s, p, ScienceSymbol.Law)) return 2.5 + 0.5 * w.Science;
            if (distinct + 1 >= GameState.ScienceSupremacySymbols) return 40;
            return w.Science * (ScienceCurve[distinct + 1] - ScienceCurve[distinct]) + 0.5;
        }
        if (d == Nisaba) return s.City(opp).Count > 0 ? 1.5 + 1.5 * w.Science : 0.5;
        if (d == Enki) return 3.5 + 0.5 * w.Science;
        if (d == Baal) return 1 + w.Economy * ProductionScale * age * 0.8;
        if (d == Hades) return s.DiscardPile.Count > 0 ? 4 : 0.5;
        if (d == Zeus) return 2;
        if (d == Anubis) return s.BuiltWonders(opp) != 0 ? 3 : 0.5;
        if (d == Isis) return s.DiscardPile.Count > 0 && s.BuildableWonders(p) != 0 ? 4 : 0.5;
        if (d == Ra) return s.BuildableWonders(opp) != 0 ? 4 : 0.5;
        if (d == Minerva) return 1 + 2 * w.Military;
        if (d == Neptune) return 2.5;
        return 4; // the Gate: the best of the deck tops, at double cost
    }

    /// <summary>What one more Grand Temple adds to <paramref name="player"/>'s score (5 / 12 / 21 for 1 / 2 / 3).</summary>
    static double TempleGain(GameState s, int player)
    {
        var built = (s.City(player) & PantheonData.TempleSet).Count;
        return built >= PantheonData.TemplePoints.Length ? 0
            : PantheonData.TemplePoints[built] - (built == 0 ? 0 : PantheonData.TemplePoints[built - 1]);
    }

    /// <summary>The draft value of a Pantheon Wonder's power beyond its VP and play-again.</summary>
    double PantheonWonderPower(WonderDef w) =>
        w.Index == Sanctuary ? 3.0 // activations 2 coins cheaper
        : w.Effect == WonderEffect.Theater ? 4.0 // the best Divinity of a deck, at once
        : 0;
}
