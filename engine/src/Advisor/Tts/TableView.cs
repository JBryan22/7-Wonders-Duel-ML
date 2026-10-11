using System.Globalization;
using SevenWondersDuel.Engine.Data;

namespace SevenWondersDuel.Advisor.Tts;

/// <summary>A TTS seat colour of the 7 Wonders Duel table: Red sits left, White right (research ticket 03).</summary>
public enum Side { Red, White }

/// <summary>Where a piece is, in game terms (spec A9: "places, not coordinates").</summary>
public enum Place
{
    /// <summary>A slot of the current Age's card structure (<see cref="PlacedPiece.Slot"/>).</summary>
    Pyramid,
    /// <summary>A player's city (built cards).</summary>
    City,
    /// <summary>An Age card tucked under a Wonder (<see cref="PlacedPiece.Row"/>): that Wonder is built.</summary>
    UnderWonder,
    /// <summary>A Wonder card in a player's column (<see cref="PlacedPiece.Row"/> 1..4).</summary>
    Wonder,
    /// <summary>A Wonder dealt face up for the draft.</summary>
    WonderOffer,
    Discard,
    /// <summary>A Progress token still on the board.</summary>
    ProgressBoard,
    /// <summary>A Progress token in a player's area (taken).</summary>
    ProgressArea,
    /// <summary>A Progress token of a Great Library draw (never named).</summary>
    LibraryDraw,
    /// <summary>The Progress token bag (<see cref="TablePiece.Count"/> tokens inside).</summary>
    ProgressBag,
    /// <summary>Off the playing area: undealt and removed decks, unused Wonders, promo Wonders. Never game input.</summary>
    Storage,
    /// <summary>In a player's hand (never named).</summary>
    Hand,
    /// <summary>On the table but in no known place (off the pyramid grid, between areas, a deck formed by accident).</summary>
    Loose,
}

/// <summary>
/// One piece in its place. <see cref="Slug"/> is set only when the snapshot named the piece and the name is in
/// tts/names.json; a discard-pile deck becomes one placed piece per card.
/// </summary>
public sealed record PlacedPiece(Place Place, TablePiece Piece, Side? Side = null, int Slot = -1, int Row = 0, string? Slug = null, string? Name = null)
{
    public bool IsNamed => Name is not null;
}

/// <summary>A looting token: still on the board or taken. <see cref="Side"/> is the player who loses the coins.</summary>
public sealed record LootingToken(Side Side, int Value, bool OnBoard);

/// <summary>
/// A <see cref="TableSnapshot"/> mapped to places with tts/places.json: pyramid slots of the given Age, cities,
/// Wonders and the cards under them, the discard pile, Progress tokens, coins, the Conflict Pawn and the looting
/// tokens. This is what reconciliation (ticket 33) compares with the expected table.
/// </summary>
public sealed class TableView
{
    TableView(TableSnapshot snapshot, int? age, IReadOnlyList<PlacedPiece> pieces, IReadOnlyDictionary<Side, int> coins,
        int? pawnSpace, IReadOnlyList<LootingToken> looting)
    {
        Snapshot = snapshot;
        Age = age;
        Pieces = pieces;
        Coins = coins;
        PawnSpace = pawnSpace;
        Looting = looting;
    }

    public TableSnapshot Snapshot { get; }
    /// <summary>The Age whose pyramid grid was used, or null (no pyramid slots: cards there are <see cref="Place.Loose"/>).</summary>
    public int? Age { get; }
    public IReadOnlyList<PlacedPiece> Pieces { get; }
    /// <summary>Coins in each player's area (a soft check only).</summary>
    public IReadOnlyDictionary<Side, int> Coins { get; }
    /// <summary>The Conflict Pawn's track space, positive towards White's capital (approximate; a soft check only).</summary>
    public int? PawnSpace { get; }
    public IReadOnlyList<LootingToken> Looting { get; }

    public IEnumerable<PlacedPiece> At(Place place) => Pieces.Where(p => p.Place == place);
    public IEnumerable<PlacedPiece> At(Place place, Side side) => Pieces.Where(p => p.Place == place && p.Side == side);

    /// <summary>The pieces lying on a pyramid slot (normally zero or one).</summary>
    public IEnumerable<PlacedPiece> OnSlot(int slot) => Pieces.Where(p => p.Place == Place.Pyramid && p.Slot == slot);

    /// <summary>A Wonder counts as built when an Age card is tucked under it.</summary>
    public bool WonderBuilt(Side side, int row) => Pieces.Any(p => p.Place == Place.UnderWonder && p.Side == side && p.Row == row);

    /// <summary>The Progress tokens left in the bag, or null if the bag isn't on the table.</summary>
    public int? ProgressBagCount => At(Place.ProgressBag).Select(p => (int?)p.Piece.Count).FirstOrDefault();

    /// <summary>Places a snapshot. <paramref name="age"/> picks the pyramid grid; by default the mod's own Age record.</summary>
    public static TableView From(TableSnapshot snapshot, int? age = null) => From(snapshot, age ?? snapshot.Age, TtsData.Places, TtsData.Names);

    internal static TableView From(TableSnapshot snapshot, int? age, TablePlaces places, TtsNames names)
    {
        var layout = age is >= 1 and <= 3 ? GameData.Layout(age.Value) : null;
        var placed = new List<PlacedPiece>();
        var coins = new Dictionary<Side, int> { [Side.Red] = 0, [Side.White] = 0 };
        int? pawn = null;
        foreach (var piece in snapshot.Pieces)
        {
            switch (piece.Kind)
            {
                case PieceKind.Card:
                    placed.Add(PlaceCard(piece, layout, places, names));
                    break;
                case PieceKind.Deck:
                    PlaceDeck(piece, places, names, placed);
                    break;
                case PieceKind.Token:
                    placed.Add(PlaceToken(piece, places, names));
                    break;
                case PieceKind.Bag:
                    placed.Add(new PlacedPiece(Place.ProgressBag, piece));
                    break;
                case PieceKind.Coin:
                    foreach (var side in Enum.GetValues<Side>())
                        if (places.Sides[side].Contains(piece.X, piece.Z) && !piece.InHand) coins[side] += piece.Value;
                    break;
                case PieceKind.Pawn:
                    pawn ??= (int)Math.Round((piece.X - places.TrackCentre) / places.TrackStep, MidpointRounding.AwayFromZero);
                    break;
                case PieceKind.Military:
                    break;
            }
        }
        var looting = places.Looting
            .Select(home => new LootingToken(home.Side, home.Value, snapshot.Pieces.Any(p =>
                p.Kind == PieceKind.Military && p.Value == home.Value && Near(p, home.At, places.LootingSnap))))
            .ToList();
        return new TableView(snapshot, layout?.Age, placed, coins, pawn is null ? null : Math.Clamp(pawn.Value, -9, 9), looting);
    }

    static PlacedPiece PlaceCard(TablePiece piece, AgeLayout? layout, TablePlaces places, TtsNames names)
    {
        var isWonder = piece.Back == "wonder";
        var name = piece.Name;
        var slug = name is null ? null : isWonder ? names.Wonder(name) : names.Card(name);
        PlacedPiece At(Place place, Side? side = null, int slot = -1, int row = 0) => new(place, piece, side, slot, row, slug, name);

        if (piece.InHand) return At(Place.Hand);
        if (isWonder)
        {
            foreach (var side in Enum.GetValues<Side>())
                if (Math.Abs(piece.X - places.WonderX[side]) <= places.WonderSnap && WonderRow(piece.Z, places) is var row and > 0)
                    return At(Place.Wonder, side, row: row);
            if (places.OfferSpots.Any(s => Near(piece, s, places.OfferSnap))) return At(Place.WonderOffer);
            return places.Table.Contains(piece.X, piece.Z) ? At(Place.Loose) : At(Place.Storage);
        }
        if (places.Discard.Contains(piece.X, piece.Z)) return At(Place.Discard);
        foreach (var side in Enum.GetValues<Side>())
            if (Math.Abs(piece.X - places.TuckX(side)) <= places.WonderSnap && WonderRow(piece.Z, places) is var row and > 0)
                return At(Place.UnderWonder, side, row: row);
        if (places.PyramidArea.Contains(piece.X, piece.Z))
        {
            if (layout is not null)
                for (var slot = 0; slot < layout.SlotCount; slot++)
                    if (Near(piece, places.SlotCentre(layout, slot), places.PyramidSnap)) return At(Place.Pyramid, slot: slot);
            return At(Place.Loose);
        }
        foreach (var side in Enum.GetValues<Side>())
            if (places.Cities[side].Contains(piece.X, piece.Z)) return At(Place.City, side);
        return places.Table.Contains(piece.X, piece.Z) ? At(Place.Loose) : At(Place.Storage);
    }

    static void PlaceDeck(TablePiece piece, TablePlaces places, TtsNames names, List<PlacedPiece> placed)
    {
        if (piece.InHand)
            placed.Add(new PlacedPiece(Place.Hand, piece));
        else if (places.Discard.Contains(piece.X, piece.Z) && piece.Names is { } cards)
            placed.AddRange(cards.Select(n => new PlacedPiece(Place.Discard, piece, Slug: names.Card(n), Name: n)));
        else
            placed.Add(new PlacedPiece(places.Table.Contains(piece.X, piece.Z) ? Place.Loose : Place.Storage, piece));
    }

    static PlacedPiece PlaceToken(TablePiece piece, TablePlaces places, TtsNames names)
    {
        var name = piece.Name;
        var slug = name is null ? null : names.Token(name);
        PlacedPiece At(Place place, Side? side = null) => new(place, piece, side, Slug: slug, Name: name);

        if (piece.InHand) return At(Place.Hand);
        if (places.ProgressBoard.Contains(piece.X, piece.Z)) return At(Place.ProgressBoard);
        foreach (var side in Enum.GetValues<Side>())
            if (places.LibraryDraw[side].Contains(piece.X, piece.Z)) return At(Place.LibraryDraw, side);
        foreach (var side in Enum.GetValues<Side>())
            if (places.Sides[side].Contains(piece.X, piece.Z)) return At(Place.ProgressArea, side);
        return places.Table.Contains(piece.X, piece.Z) ? At(Place.Loose) : At(Place.Storage);
    }

    /// <summary>The Wonder row 1..4 at this z, or 0.</summary>
    static int WonderRow(double z, TablePlaces places)
    {
        for (var i = 0; i < places.WonderRows.Count; i++)
            if (Math.Abs(z - places.WonderRows[i]) <= places.WonderSnap) return i + 1;
        return 0;
    }

    static bool Near(TablePiece piece, TablePoint p, double snap) => Math.Abs(piece.X - p.X) <= snap && Math.Abs(piece.Z - p.Z) <= snap;

    /// <summary>
    /// The table place by place, one sorted line per piece plus coins, pawn and looting tokens: what the owner's live
    /// check compares with the committed save (seats are left out: they depend on who sits down).
    /// </summary>
    public IReadOnlyList<string> Facts()
    {
        var facts = Pieces.Select(Fact).ToList();
        foreach (var side in Enum.GetValues<Side>()) facts.Add($"coins {side}: {Coins[side]}");
        facts.Add(PawnSpace switch
        {
            null => "conflict pawn: not found",
            0 => "conflict pawn: centre",
            > 0 and var s => $"conflict pawn: {s} towards White",
            var s => $"conflict pawn: {-s} towards Red",
        });
        facts.AddRange(Looting.Select(t => $"looting token {t.Side} {t.Value}: {(t.OnBoard ? "on the board" : "taken")}"));
        facts.Sort(StringComparer.Ordinal);
        return facts;
    }

    string Fact(PlacedPiece p) => p.Place switch
    {
        Place.Pyramid => $"pyramid {GameData.Layout(Age!.Value).SlotName(p.Slot)}: {Label(p)}",
        Place.City => $"city {p.Side}: {Label(p)}",
        Place.UnderWonder => $"under wonder {p.Side} {p.Row}: {Label(p)}",
        Place.Wonder => $"wonder {p.Side} {p.Row}: {Label(p)}",
        Place.WonderOffer => $"wonder offer: {Label(p)}",
        Place.Discard => $"discard: {Label(p)}",
        Place.ProgressBoard => $"progress board: {Label(p)}",
        Place.ProgressArea => $"progress {p.Side}: {Label(p)}",
        Place.LibraryDraw => $"library draw {p.Side}: {Label(p)}",
        Place.ProgressBag => $"progress bag: {p.Piece.Count} tokens",
        Place.Storage => $"storage: {Label(p)}",
        Place.Hand => $"hand: {Label(p)}",
        _ => string.Create(CultureInfo.InvariantCulture, $"loose: {Label(p)} near ({p.Piece.X:0}, {p.Piece.Z:0})"),
    };

    /// <summary>A piece in words: its slug, an unknown name in quotes, or what can be seen of a nameless piece.</summary>
    public static string Label(PlacedPiece p)
    {
        if (p.Slug is not null) return p.Slug;
        if (p.Name is not null) return $"unknown \"{p.Name}\"";
        return p.Piece.Kind switch
        {
            PieceKind.Deck => $"deck of {p.Piece.Count}",
            PieceKind.Token => p.Piece.FaceDown ? "face-down token" : "unnamed token",
            PieceKind.Card => $"{(p.Piece.FaceDown ? "face-down" : "unnamed")} {p.Piece.Back} card",
            var k => k.ToString().ToLowerInvariant(),
        };
    }

    /// <summary>
    /// The differences between two tables place by place, as "missing: ..." (in <paramref name="expected"/> only)
    /// and "extra: ..." lines; empty when they match.
    /// </summary>
    public static IReadOnlyList<string> Compare(IReadOnlyList<string> expected, IReadOnlyList<string> actual)
    {
        var left = expected.GroupBy(f => f).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var right = actual.GroupBy(f => f).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var diffs = new List<string>();
        foreach (var (fact, n) in left)
            for (var i = right.GetValueOrDefault(fact); i < n; i++) diffs.Add($"missing: {fact}");
        foreach (var (fact, n) in right)
            for (var i = left.GetValueOrDefault(fact); i < n; i++) diffs.Add($"extra:   {fact}");
        diffs.Sort(StringComparer.Ordinal);
        return diffs;
    }
}
