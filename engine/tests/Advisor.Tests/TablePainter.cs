using SevenWondersDuel.Advisor.Tts;
using SevenWondersDuel.Engine;
using SevenWondersDuel.Engine.Data;

namespace SevenWondersDuel.Advisor.Tests;

/// <summary>
/// Lays out a Game State on a fake TTS table the way players of the mod would: the current Age's structure on its
/// grid (face-down cards where the state doesn't know them), cities, Wonders in their columns with Age cards tucked
/// under built ones, the draft offer, the discard deck, Progress tokens, coins, the Conflict Pawn and the looting
/// tokens. The objects go through the real snapshot.lua in MoonSharp, so tests reconcile exactly what TTS would send.
/// <para>
/// A Pantheon game adds its pieces (<see cref="PaintPantheon"/>): the Pantheon spaces, each player's Divinity board, the
/// Mythology decks in the box area, tokens on the structure and held, Enki's tokens, the Snake, Minerva, and the cards a
/// player is looking at in their hand (a draw, the Divine Theater's deck). Face-down cards carry their true names when the
/// state knows them: only snapshot.lua's filter stands between them and the Advisor.
/// </para>
/// </summary>
internal static class TablePainter
{
    static readonly PantheonNames Pantheon = TtsData.Names.Pantheon;
    static readonly Dictionary<string, string> CardNames = TtsData.Names.Cards.Concat(Pantheon.Temples).ToDictionary(kv => kv.Value, kv => kv.Key);
    static readonly Dictionary<string, string> WonderNames = TtsData.Names.Wonders.Concat(Pantheon.Wonders).ToDictionary(kv => kv.Value, kv => kv.Key);
    static readonly Dictionary<string, string> TokenNames = TtsData.Names.Tokens.Concat(Pantheon.Tokens).ToDictionary(kv => kv.Value, kv => kv.Key);
    static readonly Dictionary<string, string> DivinityNames = Pantheon.Divinities.ToDictionary(kv => kv.Value, kv => kv.Key);
    static readonly Dictionary<string, string> TokenImages = Pantheon.TokenImages.ToDictionary(kv => kv.Value, kv => kv.Key);

    public static string CardName(int card) => CardNames[GameData.Card(card).Id];
    public static string WonderName(int wonder) => WonderNames[GameData.Wonder(wonder).Id];
    public static string TokenName(int token) => TokenNames[GameData.Token(token).Id];
    public static string DivinityName(int divinity) => DivinityNames[PantheonData.Divinities[divinity].Id];
    /// <summary>A token's face image URL, as TTS reports it (only the hash at the end matters).</summary>
    public static string TokenImage(int code) =>
        $"https://steamusercontent-a.akamaihd.net/ugc/1011563432452076026/{TokenImages[PantheonState.TokenSlug(code)]}/";

    /// <summary>The players' hand zones (where the mod's HandTriggers are).</summary>
    public static readonly FakeHand[] Hands = [new("White", 16, 31, -63, -57), new("Red", -31, -16, -63, -57)];

    public static List<FakeObject> Paint(GameState s, SeatSides sides)
    {
        var places = TtsData.Places;
        var objects = new List<FakeObject>();
        string Guid() => $"p{objects.Count:000}";
        void Card(string name, double x, double z, bool faceDown, bool guild, double y = 1.6) =>
            objects.Add(new FakeObject("Card", Guid(), name, x, y, z, FaceDown: faceDown, Tags: [guild ? "purple" : "blue"]));
        // A face-down card the state can't name: any card of the right back does (snapshot.lua never names it).
        string HiddenName(int card) => card == GameState.HiddenGuild && s.TempleBacks ? "Greek Grand Temple" : "Hidden Card";
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
                if (card >= 0) Card(CardName(card), at.X, at.Z, false, GameData.Card(card).IsGuild || PantheonData.TempleSet.Contains(card), y);
                else Card(HiddenName(card), at.X, at.Z, true, card == GameState.HiddenGuild, y);
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
                Card(CardName(c), x, city.Z2 - 1.5 - 3 * (i / 8), false, GameData.Card(c).IsGuild || PantheonData.TempleSet.Contains(c));
                if (s.Pantheon?.Snake == c)
                    objects.Add(new FakeObject("Tile", Guid(), "Snake Token", x + 0.2, 1.8, city.Z2 - 1.5 - 3 * (i / 8)));
                i++;
            }
            var row = 0;
            foreach (var w in GameState.WondersIn(s.Wonders(p)))
            {
                // A 5th Wonder (one stolen with Ra) stands in the Ra row above row 1.
                var z = row < places.WonderRows.Count ? places.WonderRows[row] : places.RaRow;
                row++;
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
            Card(CardName(discard[0]), -50.6, 2.3, true, GameData.Card(discard[0]).IsGuild);
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
        if (s.Pantheon is { } pantheon) PaintPantheon(s, pantheon, sides, objects);
        return objects;
    }

    /// <summary>Pantheon's pieces where players of the mod leave them (see the class summary).</summary>
    static void PaintPantheon(GameState s, PantheonState p, SeatSides sides, List<FakeObject> objects)
    {
        var places = TtsData.Places;
        string Guid() => $"q{objects.Count:000}";
        // A Divinity this state can't name (the opponent's): any card of its Mythology, the back is what shows.
        static string Name(int d, Mythology m) => d >= 0 ? DivinityName(d) : DivinityName(PantheonData.MythologyDef(m).Divinities[0]);
        void Divinity(string name, double x, double z, bool faceDown) =>
            objects.Add(new FakeObject("Card", Guid(), name, x, 1.7, z, FaceDown: faceDown, Tags: ["rotate flip"]));
        void Token(int code, double x, double z) =>
            objects.Add(new FakeObject("Tile", Guid(), "", x, 1.8, z, Image: TokenImage(code)));

        // The Pantheon: the Gate goes on its space when players set up Age II.
        for (var space = 0; space < PantheonData.SpaceCount; space++)
        {
            var d = p.Space(space);
            if (d == PantheonState.EmptySpace) continue;
            var at = places.PantheonSpaces[ExpectedTable.EngineSpace(space, sides)];
            if (d == PantheonData.Gate)
            {
                if (ExpectedTable.IsDealt(s)) Divinity("The Gate", at.X, at.Z, false);
                continue;
            }
            Divinity(p.IsHidden(space) ? Name(-1, p.SpaceMythology(space)!.Value) : DivinityName(d), at.X, at.Z, p.IsFaceDown(space));
            if (d >= 0 && PantheonData.Divinities[d].Id == "enki" && !p.IsFaceDown(space))
            {
                var k = 0;
                foreach (var t in ProgressTokens.In(p.Enki))
                    objects.Add(new FakeObject("Generic", Guid(), TokenName(t), at.X - 1.2 + 2.4 * k++, 1.8, at.Z + 1, Tags: ["progress token"]));
            }
        }
        // Until then (or once activated) it lies in the box area, or on its activator's Divinity board.
        if (!(ExpectedTable.IsDealt(s) && Enumerable.Range(0, PantheonData.SpaceCount).Any(i => p.Space(i) == PantheonData.Gate))
            && ((p.Activated(0) | p.Activated(1)) & 1 << PantheonData.Gate) == 0)
            Divinity("The Gate", 51, -3.81, true);

        for (var player = 0; player < 2; player++)
        {
            var side = sides.Of(player);
            var sign = side == Side.Red ? -1 : 1;
            var k = 0;
            foreach (var d in new Bits(p.Activated(player))) Divinity(DivinityName(d), sign * (12 + 3.5 * k++), -46, false);
            var t = 0;
            foreach (var m in PantheonData.Mythologies)
                for (var n = 0; n < p.MythologyTokens(player, m.Mythology); n++) Token((int)m.Mythology, sign * (6 + 2 * t++), -40);
            foreach (var o in PantheonData.Offerings)
                if ((p.OfferingTokens(player) & 1 << o.Index) != 0) Token(PantheonState.OfferingToken + o.Index, sign * (6 + 2 * t++), -40);
        }

        // Tokens on the structure, about 0.9 above their card's centre (the mod's setup scripts).
        if (ExpectedTable.IsDealt(s))
            for (var slot = 0; slot < GameState.SlotCount; slot++)
                if (s.IsOccupied(slot) && p.SlotToken(slot) != PantheonState.NoToken)
                {
                    var at = places.SlotCentre(s.Layout, slot);
                    Token(p.SlotToken(slot), at.X + 0.05, at.Z + places.SlotTokenOffsetZ);
                }

        // The Mythology decks in the box area; while the Gate's choice is pending, the deck tops it revealed lie face up.
        var gate = s.Step == StepKind.Decision && s.Pending(0).Kind == DecisionKind.Gate;
        foreach (var m in PantheonData.Mythologies)
        {
            var x = 51 + 7.5 * (int)m.Mythology;
            var cards = Enumerable.Range(0, p.DeckSize(m.Mythology)).Select(i => p.DeckCard(m.Mythology, i))
                .Select(d => Name(d, m.Mythology)).ToList();
            if (gate && cards.Count > 0)
            {
                Divinity(cards[0], x, 4, false);
                cards.RemoveAt(0);
            }
            if (cards.Count == 1) Divinity(cards[0], x, 10.2, true);
            else if (cards.Count > 1)
                objects.Add(new FakeObject("Deck", Guid(), "", x, 1.7, 10.2, FaceDown: true,
                    Contents: cards.Select((c, n) => new FakeContent($"{Guid()}c{n}", c, ["rotate flip"])).ToList()));
        }

        // What a player looks at in their hand: their draw, or the deck their Divine Theater looks through.
        if (s.Step == StepKind.Decision)
        {
            var decision = s.Pending(0);
            var hand = new List<string>();
            if (decision.Kind == DecisionKind.PlaceDivinity && p.HasDraw)
                for (var i = 0; i < 2; i++)
                    if (p.Drawn(i) != -1) hand.Add(Name(p.Drawn(i), (Mythology)decision.Wonder));
            if (decision.Kind is DecisionKind.Theater)
            {
                var m = (Mythology)decision.Wonder;
                hand.AddRange(Enumerable.Range(0, p.DeckSize(m)).Select(i => Name(p.DeckCard(m, i), m)));
            }
            if (decision.Kind is DecisionKind.TheaterOrder)
            {
                // Still deciding which card goes back on top: the chosen card and the 2 others are all still in hand.
                var chosen = decision.Wonder;
                var m = PantheonData.Divinities[chosen].Mythology!.Value;
                hand.Add(DivinityName(chosen));
                hand.AddRange(Enumerable.Range(0, p.DeckSize(m)).Select(i => Name(p.DeckCard(m, i), m)));
            }
            var handX = sides.Of(decision.Player) == Side.Red ? -23.6 : 23.6;
            for (var i = 0; i < hand.Count; i++) Divinity(hand[i], handX - 3 + 3 * i, -60, false);
        }

        // Nisaba's Snake (in a city: painted with its card), the Minerva pawn.
        if (p.Snake < 0) objects.Add(new FakeObject("Tile", Guid(), "Snake Token", 53.04, 1.6, 19.31));
        objects.Add(p.Minerva == PantheonState.NoMinerva
            ? new FakeObject("Generic", Guid(), "Minerva Pawn", 53.01, 1.6, 21.92, Tags: ["conflict pawn"])
            : new FakeObject("Generic", Guid(), "Minerva Pawn",
                places.TrackCentre + places.TrackStep * (sides.P1 == Side.Red ? p.Minerva : -p.Minerva), 1.7, 20.5, Tags: ["conflict pawn"]));
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
        TableSnapshot.Parse(new FakeTable(objects, players ?? FakeTable.ProbePlayers, modAge ?? (s.Age == 0 ? "None" : $"Age{s.Age}"), Hands).Snapshot());

    public static TableSnapshot Snapshot(GameState s, SeatSides sides, IReadOnlyList<(string Color, bool Host)>? players = null) =>
        Snapshot(Paint(s, sides), s, players: players);
}
