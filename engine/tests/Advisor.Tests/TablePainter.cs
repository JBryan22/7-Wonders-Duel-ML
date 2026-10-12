using SevenWondersDuel.Advisor.Tts;
using SevenWondersDuel.Engine;
using SevenWondersDuel.Engine.Data;

namespace SevenWondersDuel.Advisor.Tests;

/// <summary>
/// Lays out a Game State on a fake TTS table the way players of the mod would: the current Age's structure on its
/// grid (face-down cards where the state doesn't know them), cities, Wonders in their columns with Age cards tucked
/// under built ones, the draft offer, the discard deck, Progress tokens, coins, the Conflict Pawn and the looting
/// tokens. The objects go through the real snapshot.lua in MoonSharp, so tests reconcile exactly what TTS would send.
/// </summary>
internal static class TablePainter
{
    static readonly Dictionary<string, string> CardNames = TtsData.Names.Cards.ToDictionary(kv => kv.Value, kv => kv.Key);
    static readonly Dictionary<string, string> WonderNames = TtsData.Names.Wonders.ToDictionary(kv => kv.Value, kv => kv.Key);
    static readonly Dictionary<string, string> TokenNames = TtsData.Names.Tokens.ToDictionary(kv => kv.Value, kv => kv.Key);

    public static string CardName(int card) => CardNames[GameData.Cards[card].Id];
    public static string WonderName(int wonder) => WonderNames[GameData.Wonders[wonder].Id];
    public static string TokenName(int token) => TokenNames[GameData.Tokens[token].Id];

    public static List<FakeObject> Paint(GameState s, SeatSides sides)
    {
        var places = TtsData.Places;
        var objects = new List<FakeObject>();
        string Guid() => $"p{objects.Count:000}";
        void Card(string name, double x, double z, bool faceDown, bool guild, double y = 1.6) =>
            objects.Add(new FakeObject("Card", Guid(), name, x, y, z, FaceDown: faceDown, Tags: [guild ? "purple" : "blue"]));
        void Wonder(int w, double x, double z) =>
            objects.Add(new FakeObject("Card", Guid(), WonderName(w), x, 1.6, z, Tags: ["wonder"]));
        void Token(int t, double x, double z) =>
            objects.Add(new FakeObject("Generic", Guid(), TokenName(t), x, 1.7, z, Tags: ["progress token"]));

        if (ExpectedTable.IsDealt(s))
        {
            var layout = s.Layout;
            for (var slot = 0; slot < GameState.SlotCount; slot++)
            {
                if (!s.IsOccupied(slot)) continue;
                var at = places.SlotCentre(layout, slot);
                var card = s.SlotCard(slot);
                var y = 1.6 + 0.05 * layout.Slots[slot].Row;
                if (card >= 0) Card(CardName(card), at.X, at.Z, false, GameData.Cards[card].IsGuild, y);
                else Card("Hidden Card", at.X, at.Z, true, card == GameState.HiddenGuild, y);
            }
        }

        for (var p = 0; p < 2; p++)
        {
            var side = sides.Of(p);
            var sign = side == Side.Red ? -1 : 1;
            var city = places.Cities[side];
            var i = 0;
            foreach (var c in s.City(p).Cards())
            {
                var x = side == Side.Red ? city.X1 + 1.5 + 2.5 * (i % 8) : city.X2 - 1.5 - 2.5 * (i % 8);
                Card(CardName(c), x, city.Z2 - 1.5 - 3 * (i / 8), false, GameData.Cards[c].IsGuild);
                i++;
            }
            var row = 0;
            foreach (var w in GameState.WondersIn(s.Wonders(p)))
            {
                var z = places.WonderRows[row++];
                Wonder(w, places.WonderX[side], z);
                if ((s.BuiltWonders(p) & 1 << w) != 0) Card("Hidden Card", places.TuckX(side), z, true, false, 1.5);
            }
            var k = 0;
            foreach (var t in ProgressTokens.In(s.Tokens(p))) Token(t, sign * (18 + 2.5 * k++), -3);
            AddCoins(objects, s.Coins(p), sign * 30, -38);
        }

        if (s.Age == 0)
        {
            var spot = 0;
            foreach (var w in GameState.WondersIn(s.DraftOffer)) Wonder(w, places.OfferSpots[spot].X, places.OfferSpots[spot++].Z);
        }

        var discard = s.DiscardPile.Cards().ToList();
        if (discard.Count == 1)
            Card(CardName(discard[0]), -50.6, 2.3, true, GameData.Cards[discard[0]].IsGuild);
        else if (discard.Count > 1)
            objects.Add(new FakeObject("Deck", Guid(), "", -50.6, 2.4, 2.3, FaceDown: true,
                Contents: discard.Select((c, n) => new FakeContent($"d{n}", CardName(c), ["blue"])).ToList()));

        var b = 0;
        foreach (var t in ProgressTokens.In(s.BoardTokens)) Token(t, -10 + 5 * b++, 24);
        var r = 0;
        foreach (var c in ExpectedTable.Returned(s).Cards()) Card(CardName(c), -6 + 6 * r++, -45, false, false);

        if (s.TokensSetOut != 0)
        {
            objects.Add(new FakeObject("Generic", Guid(), "Conflict Pawn",
                places.TrackCentre + places.TrackStep * (sides.P1 == Side.Red ? s.ConflictPawn : -s.ConflictPawn), 1.7, 18,
                Tags: ["conflict pawn"]));
            foreach (var home in places.Looting)
            {
                var victim = sides.PlayerAt(home.Side);
                var left = s.MilitaryTokenLeft(GameState.MilitaryToken(victim, home.Value == 5));
                var (x, z) = left ? (home.At.X, home.At.Z) : (home.At.X, 28.0);
                objects.Add(new FakeObject("Generic", Guid(), "Military token", x, 1.7, z, Tags: ["military token"],
                    Description: $"The player loses {home.Value} coins"));
            }
        }
        return objects;
    }

    /// <summary>Coins as 6s, 3s and 1s, piled at one spot of a player's area.</summary>
    public static void AddCoins(List<FakeObject> objects, int coins, double x, double z)
    {
        foreach (var value in new[] { 6, 3, 1 })
            for (; coins >= value; coins -= value)
                objects.Add(new FakeObject("Generic", $"g{objects.Count:000}", $"{value} Gold", x, 1.7, z));
    }

    /// <summary>The snapshot TTS would send for these objects (through the real snapshot.lua).</summary>
    public static TableSnapshot Snapshot(IReadOnlyList<FakeObject> objects, GameState s, string? modAge = null,
        IReadOnlyList<(string Color, bool Host)>? players = null) =>
        TableSnapshot.Parse(new FakeTable(objects, players ?? FakeTable.ProbePlayers, modAge ?? (s.Age == 0 ? "None" : $"Age{s.Age}")).Snapshot());

    public static TableSnapshot Snapshot(GameState s, SeatSides sides) => Snapshot(Paint(s, sides), s);
}
