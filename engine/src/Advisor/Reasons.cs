using SevenWondersDuel.Engine;
using SevenWondersDuel.Engine.Data;

namespace SevenWondersDuel.Advisor;

/// <summary>
/// Reason tags for one advised Move (spec A8): short phrases built from how the Move changes the board signals of the
/// shared derived-features module (<see cref="DerivedFeatures"/>: science symbols and pairs, the Conflict Pawn's
/// distance to each capital, VP, production, trading prices, open chains), plus what it uncovers in the pyramid and
/// what it sets up next. "You" is the player to decide (the owner), "they" the opponent.
/// <para>
/// The Move is applied to a copy of the owner's Observation, so the tags never use hidden information: a card the
/// Move uncovers face down is "a face-down card".
/// </para>
/// </summary>
public static class Reasons
{
    /// <summary>Every tag for <paramref name="move"/> (then <paramref name="then"/>, for a Statue keep/give pair) in <paramref name="before"/>; never empty.</summary>
    public static string[] For(GameState before, Move move, Move? then = null)
    {
        var me = before.PlayerToDecide;
        var opp = 1 - me;
        var after = before.Clone();
        try
        {
            after.Apply(move);
            if (then is { } second) after.Apply(second);
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException)
        {
            return [Fallback(before, move)];
        }

        var tags = new List<string>();
        if (after.Result is { } result)
        {
            tags.Add(result.IsDraw ? "ends the game in a draw"
                : result.Winner == me ? $"wins the game by {Victory(result.Victory)}"
                : $"they win by {Victory(result.Victory)}");
            return [.. tags];
        }

        TokenTag(move, tags);
        if (then is { } given) TokenTag(given, tags);
        Science(before, after, me, "your", "you", tags);
        Science(before, after, opp, "their", "they", tags);
        Denial(before, move, me, tags);
        Military(before, after, me, tags);
        Points(before, after, me, tags);
        Coins(before, after, me, move, tags);
        Production(before, after, me, tags);
        Chains(before, after, me, move, tags);
        Uncovered(before, after, me, move, tags);
        FollowUp(before, me, move, tags);
        if (tags.Count == 0) tags.Add(Fallback(before, move));
        return [.. tags];
    }

    static string Victory(VictoryType v) => v.ToString().ToLowerInvariant();

    static string Symbol(ScienceSymbol s) => s.ToString().ToLowerInvariant();

    static string Resource(Resource r) => r.ToString().ToLowerInvariant();

    static string Plural(int n, string word) => n == 1 ? $"{n} {word}" : $"{n} {word}s";

    /// <summary>Science: a completed pair earns a Progress token; a new symbol counts towards supremacy (6).</summary>
    static void Science(GameState before, GameState after, int player, string whose, string who, List<string> tags)
    {
        var newPair = false;
        foreach (var sym in Enum.GetValues<ScienceSymbol>())
            if (before.SymbolCount(player, sym) < 2 && after.SymbolCount(player, sym) >= 2)
            {
                tags.Add($"completes {whose} {Symbol(sym)} pair: {who} pick{(who == "you" ? "" : "s")} a Progress token");
                newPair = true;
            }
        var symbols = DerivedFeatures.ScienceSymbols(after, player);
        if (symbols > DerivedFeatures.ScienceSymbols(before, player) && !newPair)
            tags.Add(who == "you" ? $"new science symbol: {symbols} of 6" : $"they reach {symbols} of 6 science symbols");
        else if (symbols > DerivedFeatures.ScienceSymbols(before, player))
            tags.Add(who == "you" ? $"{symbols} of 6 science symbols" : $"they reach {symbols} of 6 science symbols");
    }

    /// <summary>Taking a card with a symbol the opponent already shows once keeps their pair (and its token) from them.</summary>
    static void Denial(GameState before, Move move, int me, List<string> tags)
    {
        if (!move.TakesCard) return;
        if (GameData.Cards[move.Card].Science is { } sym && before.SymbolCount(1 - me, sym) == 1)
            tags.Add($"denies their {Symbol(sym)} pair");
    }

    static void Military(GameState before, GameState after, int me, List<string> tags)
    {
        var opp = 1 - me;
        var towardThem = DerivedFeatures.DistanceToCapital(before, opp) - DerivedFeatures.DistanceToCapital(after, opp);
        if (towardThem > 0)
            tags.Add($"+{Plural(towardThem, "shield")}: pawn {DerivedFeatures.DistanceToCapital(after, opp)} from their capital");
        else if (towardThem < 0)
            tags.Add($"they +{Plural(-towardThem, "shield")}: pawn {DerivedFeatures.DistanceToCapital(after, me)} from your capital");
        for (var victim = 0; victim < 2; victim++)
        {
            var lost = 0;
            foreach (var large in new[] { false, true })
            {
                var t = GameState.MilitaryToken(victim, large);
                if (before.MilitaryTokenLeft(t) && !after.MilitaryTokenLeft(t)) lost += GameState.MilitaryTokenCoins(t);
            }
            if (lost > 0) tags.Add(victim == opp ? $"military token: they lose up to {lost} coins" : $"military token: you lose up to {lost} coins");
        }
    }

    /// <summary>VP gained, leaving out the treasury (1 VP per 3 coins): coins have their own tag.</summary>
    static void Points(GameState before, GameState after, int me, List<string> tags)
    {
        static int NonCoin(GameState s, int p) => DerivedFeatures.Vp(s, p) - s.Coins(p) / 3;
        var mine = NonCoin(after, me) - NonCoin(before, me);
        var theirs = NonCoin(after, 1 - me) - NonCoin(before, 1 - me);
        if (mine != 0) tags.Add($"{(mine > 0 ? "+" : "−")}{Math.Abs(mine)} VP for you");
        if (theirs != 0) tags.Add($"{(theirs > 0 ? "+" : "−")}{Math.Abs(theirs)} VP for them");
    }

    static void Coins(GameState before, GameState after, int me, Move move, List<string> tags)
    {
        var opp = 1 - me;
        var mine = after.Coins(me) - before.Coins(me);
        var theirs = after.Coins(opp) - before.Coins(opp);
        if (mine > 0) tags.Add($"+{Plural(mine, "coin")}");
        else if (mine < 0) tags.Add($"costs {Plural(-mine, "coin")}");
        if (theirs < 0 && !LostToMilitary(before, after, opp)) tags.Add($"they lose {Plural(-theirs, "coin")}");
        else if (theirs > 0) tags.Add($"they get {Plural(theirs, "coin")}");
    }

    static bool LostToMilitary(GameState before, GameState after, int victim) =>
        before.MilitaryTokenLeft(GameState.MilitaryToken(victim, false)) != after.MilitaryTokenLeft(GameState.MilitaryToken(victim, false))
        || before.MilitaryTokenLeft(GameState.MilitaryToken(victim, true)) != after.MilitaryTokenLeft(GameState.MilitaryToken(victim, true));

    /// <summary>Production gained or destroyed, a flexible resource, and trading prices that change because of it.</summary>
    static void Production(GameState before, GameState after, int me, List<string> tags)
    {
        var opp = 1 - me;
        foreach (var r in Enum.GetValues<Resource>())
        {
            var mine = DerivedFeatures.Production(after, me, r) - DerivedFeatures.Production(before, me, r);
            var theirs = DerivedFeatures.Production(after, opp, r) - DerivedFeatures.Production(before, opp, r);
            if (mine > 0) tags.Add($"+{mine} {Resource(r)}");
            if (theirs < 0) tags.Add($"they lose {-theirs} {Resource(r)}");
            if (theirs > 0) tags.Add($"they +{theirs} {Resource(r)}");
            var theirPrice = DerivedFeatures.TradingPrice(after, opp, r);
            if (theirPrice > DerivedFeatures.TradingPrice(before, opp, r)) tags.Add($"{Resource(r)} costs them {theirPrice} to buy");
            var myPrice = DerivedFeatures.TradingPrice(after, me, r);
            if (myPrice < DerivedFeatures.TradingPrice(before, me, r)) tags.Add($"you buy {Resource(r)} at {myPrice}");
        }
        var choice = DerivedFeatures.ChoiceProducers(after, me) - DerivedFeatures.ChoiceProducers(before, me);
        if (choice > 0) tags.Add($"+{choice} resource of your choice each turn");
    }

    /// <summary>A free build through a chain, and the chains a new card opens.</summary>
    static void Chains(GameState before, GameState after, int me, Move move, List<string> tags)
    {
        if (move.Kind == MoveKind.Build && GameData.Cards[move.Card].FreeIf is var from and >= 0 && Payment.IsChained(before, me, move.Card))
            tags.Add($"free through the {GameData.Cards[from].Name} chain");
        if (DerivedFeatures.OpenChains(after, me) <= DerivedFeatures.OpenChains(before, me)) return;
        var gone = after.City(0) | after.City(1) | after.DiscardPile | after.CardsUnderWonders(0) | after.CardsUnderWonders(1);
        foreach (var built in (after.City(me) & ~before.City(me)).Cards())
            foreach (var target in GameData.Cards.Where(t => t.FreeIf == built && !gone.Contains(t.Index)))
                tags.Add($"chain: {target.Name} free later");
    }

    /// <summary>
    /// The pyramid cards a take-card Move makes accessible, and for whom: face-up cards by name, face-down ones as such.
    /// The last card of an Age says so instead.
    /// </summary>
    static void Uncovered(GameState before, GameState after, int me, Move move, List<string> tags)
    {
        if (!move.TakesCard) return;
        if (move.Kind == MoveKind.Wonder && before.WonderPlaysAgain(me, move.Wonder)) tags.Add("you play again");
        if (after.Age != before.Age || after.CardsLeftInAge == 0)
        {
            tags.Add($"takes the last card of Age {before.Age}");
            return;
        }
        var forWhom = move.Kind == MoveKind.Wonder && before.WonderPlaysAgain(me, move.Wonder) ? "you" : "them";
        var named = new List<string>();
        var faceDown = 0;
        for (var slot = 0; slot < GameState.SlotCount; slot++)
        {
            if (!after.IsOccupied(slot) || !after.IsAccessible(slot) || before.IsAccessible(slot)) continue;
            var card = after.SlotCard(slot);
            if (card >= 0) named.Add(GameData.Cards[card].Name);
            else faceDown++;
        }
        if (named.Count > 0) tags.Add($"uncovers {string.Join(" and ", named)} for {forWhom}");
        if (faceDown > 0) tags.Add(faceDown == 1 ? "uncovers a face-down card" : $"uncovers {faceDown} face-down cards");
    }

    /// <summary>What a Wonder's power asks for next.</summary>
    static void FollowUp(GameState before, int me, Move move, List<string> tags)
    {
        if (move.Kind != MoveKind.Wonder) return;
        var w = GameData.Wonders[move.Wonder];
        var text = w.Effect switch
        {
            WonderEffect.DestroyBrown => "then you destroy one of their brown cards",
            WonderEffect.DestroyGrey => "then you destroy one of their grey cards",
            WonderEffect.Library => "then you draw 3 unused Progress tokens and keep one",
            WonderEffect.Mausoleum => "then you build a discarded card for free",
            WonderEffect.Messe => "then you build a top-row card for free",
            WonderEffect.Liberty => "then the 3 removed cards return: you keep one and give one",
            _ => null,
        };
        if (text is not null) tags.Add(text);
    }

    /// <summary>Progress tokens whose effect is not (only) an immediate change on the board.</summary>
    static void TokenTag(Move move, List<string> tags)
    {
        if (move.Kind is MoveKind.Token or MoveKind.Library && TokenEffect(GameData.Tokens[move.Token].Id) is { } text) tags.Add(text);
    }

    /// <summary>What a Progress token does later, for the tokens whose effect is not an immediate board change.</summary>
    internal static string? TokenEffect(string id) => id switch
    {
        "architecture" => "your Wonders cost 2 fewer resources",
        "economy" => "you receive the coins they spend on trading",
        "masonry" => "your blue cards cost 2 fewer resources",
        "mathematics" => "3 VP per Progress token you own at the end",
        "strategy" => "+1 shield on every later military card",
        "theology" => "every Wonder you build lets you play again",
        "urbanism" => "+4 coins every time you build through a chain",
        _ => null,
    };

    /// <summary>A tag for a Move that changes no board signal (a draft pick, who starts, a Statue keep).</summary>
    static string Fallback(GameState s, Move move) => move.Kind switch
    {
        MoveKind.Draft => $"{GameData.Wonders[move.Wonder].Name}: {BoardView.WonderSummary(GameData.Wonders[move.Wonder])}",
        MoveKind.StartMe => $"you play first in Age {s.Age}",
        MoveKind.StartOpponent => $"they play first in Age {s.Age}",
        MoveKind.LibertyKeep => $"keeps {GameData.Cards[move.Card].Name} to build",
        MoveKind.LibertyGive => $"gives {GameData.Cards[move.Card].Name} to them",
        MoveKind.Discard => "no change on the board",
        _ => "no immediate change on the board",
    };
}
