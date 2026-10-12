using SevenWondersDuel.Engine;
using SevenWondersDuel.Engine.Data;

namespace SevenWondersDuel.Advisor.Tts;

/// <summary>
/// The TTS game life cycle around reconciliation (spec A9, ticket 34): which colour the owner sits at, whether the
/// table shows a fresh setup (a new game), and which promo Wonders the table uses although the game was started
/// without them. All of it is read from a Table Snapshot; nothing here changes the Move Log.
/// </summary>
public static class TableLifecycle
{
    /// <summary>
    /// The owner's colour: <paramref name="choice"/> when it is "White" or "Red" (the override), otherwise the host's seat
    /// (the owner hosts the TTS game). Null when the host isn't seated at White or Red (not seated, or another colour):
    /// the Advisor then asks.
    /// </summary>
    public static Side? OwnerSide(string choice, TableSnapshot? snapshot)
    {
        Side? Parse(string? colour) => colour switch { "White" => Side.White, "Red" => Side.Red, _ => null };
        return Parse(choice) ?? Parse(snapshot?.HostColor);
    }

    /// <summary>
    /// True when the table shows a game that hasn't really begun: no Age dealt, and no card, Wonder or Progress token
    /// taken by anyone (cities, Wonder columns, the discard pile and the Progress-token areas are empty). The setup
    /// itself (tokens on the board, Wonders on offer) may already be out.
    /// </summary>
    public static bool LooksFresh(TableSnapshot snapshot)
    {
        if (snapshot.Age is not null) return false;
        var view = TableView.From(snapshot, 0);
        return !view.Pieces.Any(p => p.Place is Place.Pyramid or Place.City or Place.UnderWonder or Place.Wonder or Place.Discard
            or Place.ProgressArea or Place.LibraryDraw);
    }

    /// <summary>
    /// The promo Wonders on offer in the draft, or already drafted into a player's column, that <paramref name="rules"/>
    /// leaves out of the Wonder pool (its box was unticked at New game). Unused promos lying in the box area are ignored.
    /// </summary>
    public static Promos MissingPromos(TableSnapshot snapshot, Ruleset rules)
    {
        var view = TableView.From(snapshot, 0);
        var missing = Promos.None;
        foreach (var p in view.Pieces)
        {
            if (p.Place is not (Place.WonderOffer or Place.Wonder) || p.Slug is null || !GameData.TryWonderIndex(p.Slug, out var w)) continue;
            var promo = PromoOf(GameData.Wonders[w]);
            if (promo != Promos.None && !rules.HasPromo(promo)) missing |= promo;
        }
        return missing;
    }

    /// <summary>The promo switch a Wonder belongs to (None for a base Wonder).</summary>
    public static Promos PromoOf(WonderDef wonder) => !wonder.IsPromo ? Promos.None : wonder.Effect switch
    {
        WonderEffect.Messe => Promos.Messe,
        WonderEffect.Liberty => Promos.Liberty,
        _ => Promos.None,
    };

    /// <summary>The promos one by one, with the Wonder's name ("The Messe") and the Move Log's name ("messe").</summary>
    public static IEnumerable<(Promos Flag, string Id, string Name)> Each(Promos promos)
    {
        foreach (var w in GameData.Wonders)
        {
            var flag = PromoOf(w);
            if (flag != Promos.None && (promos & flag) != 0) yield return (flag, flag == Promos.Messe ? "messe" : "liberty", w.Name);
        }
    }
}
