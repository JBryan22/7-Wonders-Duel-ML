using System.Globalization;
using SevenWondersDuel.Engine;
using SevenWondersDuel.Engine.Data;

namespace SevenWondersDuel.Advisor.Tts;

/// <summary>Which TTS seat colour each player sits at (P1/P2 are labels; Red sits left, White right).</summary>
public readonly record struct SeatSides(Side P1)
{
    public Side Of(int player) => player == 0 ? P1 : Other(P1);
    public int PlayerAt(Side side) => side == P1 ? 0 : 1;

    /// <summary>The owner (<paramref name="ownerSeat"/>) sits at <paramref name="ownerSide"/>, the opponent opposite.</summary>
    public static SeatSides ForOwner(int ownerSeat, Side ownerSide) => new(ownerSeat == 0 ? ownerSide : Other(ownerSide));

    public static Side Other(Side side) => side == Side.Red ? Side.White : Side.Red;

    /// <summary>"White (P2)": how the Advisor names a player when it talks about the table.</summary>
    public string Name(int player) => $"{Of(player)} (P{player + 1})";
    public string Name(Side side) => $"{side} (P{PlayerAt(side) + 1})";
}

/// <summary>
/// One piece where the Game State says it should be: the <b>expected table</b> (spec A9, ADR 0004). This is the seam a
/// later AI-controlled seat would use: it moves pieces until the table matches these, and reconciliation confirms it.
/// <see cref="Slot"/> is the pyramid slot of the current Age; <see cref="Card"/>, <see cref="Wonder"/> and
/// <see cref="Token"/> are Engine indices (-1 when the piece is a face-down card).
/// </summary>
public sealed record ExpectedPiece(Place Place, Side? Side = null, int Slot = -1, int Card = -1, int Wonder = -1, int Token = -1,
    bool Guild = false, bool Built = false, bool Returned = false);

/// <summary>
/// The expected table for a Game State, and the comparison of a <see cref="TableView"/> with it. Both sides are turned
/// into the same one-line <b>facts</b> ("White (P2)'s city: Baths"), so a difference is a list of plain sentences
/// (a Desync's mismatch list). Only the <b>inputs</b> are compared: cards, Wonders and Progress tokens. Coins, the
/// Conflict Pawn and looting tokens are <see cref="SoftChecks"/> (spec A9).
/// </summary>
public static class ExpectedTable
{
    /// <summary>
    /// Where every card, Wonder and Progress token should be. The pyramid of an Age that is not dealt yet (an Age
    /// started by the Engine whose opening Reveal hasn't come) is expected empty: the table catches up when it is dealt.
    /// </summary>
    public static IReadOnlyList<ExpectedPiece> Pieces(GameState s, SeatSides sides)
    {
        var pieces = new List<ExpectedPiece>();
        if (IsDealt(s))
            for (var slot = 0; slot < GameState.SlotCount; slot++)
            {
                if (!s.IsOccupied(slot)) continue;
                var card = s.SlotCard(slot);
                pieces.Add(card >= 0
                    ? new ExpectedPiece(Place.Pyramid, Slot: slot, Card: card, Guild: GameData.Cards[card].IsGuild)
                    : new ExpectedPiece(Place.Pyramid, Slot: slot, Guild: card == GameState.HiddenGuild));
            }
        for (var p = 0; p < 2; p++)
        {
            var side = sides.Of(p);
            foreach (var c in s.City(p).Cards()) pieces.Add(new ExpectedPiece(Place.City, side, Card: c));
            foreach (var w in GameState.WondersIn(s.Wonders(p)))
                if (!s.IsWonderRemoved(w))
                    pieces.Add(new ExpectedPiece(Place.Wonder, side, Wonder: w, Built: (s.BuiltWonders(p) & 1 << w) != 0));
            foreach (var t in ProgressTokens.In(s.Tokens(p))) pieces.Add(new ExpectedPiece(Place.ProgressArea, side, Token: t));
        }
        if (s.Age == 0)
            foreach (var w in GameState.WondersIn(s.DraftOffer)) pieces.Add(new ExpectedPiece(Place.WonderOffer, Wonder: w));
        foreach (var c in s.DiscardPile.Cards()) pieces.Add(new ExpectedPiece(Place.Discard, Card: c));
        foreach (var t in ProgressTokens.In(s.BoardTokens)) pieces.Add(new ExpectedPiece(Place.ProgressBoard, Token: t));
        foreach (var c in Returned(s).Cards()) pieces.Add(new ExpectedPiece(Place.Loose, Card: c, Returned: true));
        return pieces;
    }

    /// <summary>
    /// True once the current Age's structure is on the table: some card of it is known, a card was taken, or the Age III
    /// Guild backs were revealed. Before that (the Engine has started the Age, its opening Reveal hasn't come) the table's
    /// pyramid is expected empty.
    /// </summary>
    public static bool IsDealt(GameState s)
    {
        if (s.Age == 0) return false;
        if (!s.AgeUntouched) return true;
        for (var slot = 0; slot < GameState.SlotCount; slot++)
            if (s.SlotCard(slot) != GameState.Hidden) return true;
        return false;
    }

    /// <summary>The Statue of Liberty's returned cards not yet in a city: they lie loose on the table, face up.</summary>
    public static CardSet Returned(GameState s)
    {
        var set = s.LibertyOffer;
        if (s.LibertyKept >= 0) set = set.With(s.LibertyKept);
        if (s.LibertyGiven >= 0) set = set.With(s.LibertyGiven);
        return set;
    }

    /// <summary>The expected table as sorted facts.</summary>
    public static IReadOnlyList<string> Facts(GameState s, SeatSides sides)
    {
        var facts = Pieces(s, sides).Select(p => Fact(p, s, sides)).ToList();
        facts.Sort(StringComparer.Ordinal);
        return facts;
    }

    static string Fact(ExpectedPiece p, GameState s, SeatSides sides) => p.Place switch
    {
        Place.Pyramid => PyramidFact(s.Age, s.Layout.SlotName(p.Slot),
            p.Card >= 0 ? CardName(p.Card) : p.Guild ? FaceDownGuild : FaceDownCard),
        Place.City => CityFact(sides, p.Side!.Value, CardName(p.Card)),
        Place.Wonder => WonderFact(sides, p.Side!.Value, WonderName(p.Wonder), p.Built),
        Place.WonderOffer => $"Wonder draft offer: {WonderName(p.Wonder)}",
        Place.Discard => $"Discard pile: {CardName(p.Card)}",
        Place.ProgressBoard => $"Progress token on the board: {TokenName(p.Token)}",
        Place.ProgressArea => $"{sides.Name(p.Side!.Value)}'s Progress token: {TokenName(p.Token)}",
        Place.Loose when p.Returned => ReturnedFact(CardName(p.Card)),
        _ => throw new ArgumentException($"No expected fact for {p.Place}"),
    };

    /// <summary>
    /// The table's input facts, read with the pyramid grid of the state's Age. Storage, hands, the Progress token bag
    /// and the Great Library draw strips are never input (the draw is private; the bag is a count). A removed Wonder
    /// (the 8th, once 7 are built) may stay in its column or go back to the box. Anything else in no known place is a
    /// fact of its own ("Loose on the table: ..."), so an untidy table never matches.
    /// </summary>
    public static IReadOnlyList<string> Facts(TableView view, GameState s, SeatSides sides)
    {
        var facts = new List<string>();
        var returned = Returned(s);
        foreach (var p in view.Pieces)
        {
            switch (p.Place)
            {
                case Place.Pyramid:
                    facts.Add(PyramidFact(view.Age!.Value, GameData.Layout(view.Age.Value).SlotName(p.Slot), Label(p)));
                    break;
                case Place.City:
                    facts.Add(CityFact(sides, p.Side!.Value, Label(p)));
                    break;
                case Place.Wonder:
                    if (p.Slug is not null && GameData.TryWonderIndex(p.Slug, out var w) && s.IsWonderRemoved(w)) break;
                    facts.Add(WonderFact(sides, p.Side!.Value, Label(p), view.WonderBuilt(p.Side.Value, p.Row)));
                    break;
                case Place.UnderWonder:
                    if (!view.At(Place.Wonder, p.Side!.Value).Any(x => x.Row == p.Row))
                        facts.Add($"{sides.Name(p.Side.Value)}'s Wonders: an Age card tucked in row {p.Row}, where no Wonder lies");
                    break;
                case Place.WonderOffer:
                    facts.Add($"Wonder draft offer: {Label(p)}");
                    break;
                case Place.Discard:
                    facts.Add($"Discard pile: {Label(p)}");
                    break;
                case Place.ProgressBoard:
                    facts.Add($"Progress token on the board: {Label(p)}");
                    break;
                case Place.ProgressArea:
                    facts.Add($"{sides.Name(p.Side!.Value)}'s Progress token: {Label(p)}");
                    break;
                case Place.Loose:
                    if (p.Slug is not null && p.Piece.Kind == PieceKind.Card && p.Piece.Back != "wonder"
                        && GameData.TryCardIndex(p.Slug, out var c) && returned.Contains(c))
                        facts.Add(ReturnedFact(Label(p)));
                    else
                        facts.Add(string.Create(CultureInfo.InvariantCulture,
                            $"Loose on the table near ({p.Piece.X:0}, {p.Piece.Z:0}): {Label(p)}"));
                    break;
            }
        }
        facts.Sort(StringComparer.Ordinal);
        return facts;
    }

    public const string FaceDownCard = "a face-down card";
    public const string FaceDownGuild = "a face-down Guild";

    static string PyramidFact(int age, string slot, string label) => $"Age {Roman(age)} pyramid {slot}: {label}";
    static string CityFact(SeatSides sides, Side side, string label) => $"{sides.Name(side)}'s city: {label}";
    static string WonderFact(SeatSides sides, Side side, string label, bool built) =>
        $"{sides.Name(side)}'s Wonder: {label}{(built ? " (built)" : "")}";
    static string ReturnedFact(string label) => $"Statue of Liberty's returned card: {label}";

    static string Roman(int age) => age switch { 1 => "I", 2 => "II", 3 => "III", _ => age.ToString(CultureInfo.InvariantCulture) };

    public static string CardName(int card) => GameData.Cards[card].Name;
    public static string WonderName(int wonder) => GameData.Wonders[wonder].Name;
    public static string TokenName(int token) => GameData.Tokens[token].Name;

    /// <summary>A placed piece in words, with the Engine's display names (the mod's own names can differ: see TtsNames).</summary>
    public static string Label(PlacedPiece p)
    {
        if (p.Slug is not null)
        {
            if (p.Piece.Kind == PieceKind.Token && GameData.TryTokenIndex(p.Slug, out var t)) return TokenName(t);
            if (p.Piece.Back == "wonder" && GameData.TryWonderIndex(p.Slug, out var w)) return WonderName(w);
            if (GameData.TryCardIndex(p.Slug, out var c)) return CardName(c);
        }
        if (p.Name is not null) return $"\"{p.Name}\" (a name the Advisor doesn't know)";
        return p.Piece.Kind switch
        {
            PieceKind.Card when p.Piece.Back == "wonder" => p.Piece.FaceDown ? "a face-down Wonder" : "an unnamed Wonder",
            PieceKind.Card when p.Piece.FaceDown => p.Piece.Back == "guild" ? FaceDownGuild : FaceDownCard,
            PieceKind.Card => "an unnamed card",
            PieceKind.Token => p.Piece.FaceDown ? "a face-down Progress token" : "an unnamed Progress token",
            PieceKind.Deck => $"a deck of {p.Piece.Count} cards",
            var k => k.ToString().ToLowerInvariant(),
        };
    }

    /// <summary>
    /// The soft checks (spec A9): coins, the Conflict Pawn and the looting tokens. Each difference is a note such as
    /// "Coins: TTS shows 7 for White (P2), expected 9"; never a Desync. The pawn's position is read approximately, so
    /// one space off is not reported.
    /// </summary>
    public static IReadOnlyList<string> SoftChecks(GameState s, TableView view, SeatSides sides)
    {
        var notes = new List<string>();
        if (s.TokensSetOut == 0) return notes; // before setup there is nothing to check
        for (var p = 0; p < 2; p++)
        {
            var shown = view.Coins[sides.Of(p)];
            if (shown != s.Coins(p)) notes.Add($"Coins: TTS shows {shown} for {sides.Name(p)}, expected {s.Coins(p)}");
        }
        // ConflictPawn > 0: P1 leads, so the pawn stands towards P2's capital; PawnSpace > 0 is towards White.
        var expectedPawn = sides.P1 == Side.Red ? s.ConflictPawn : -s.ConflictPawn;
        if (view.PawnSpace is not { } pawn)
            notes.Add("Conflict Pawn: not found on the track");
        else if (Math.Abs(pawn - expectedPawn) > 1)
            notes.Add($"Conflict Pawn: TTS shows it {Where(pawn)}, expected {Where(expectedPawn)}");
        for (var victim = 0; victim < 2; victim++)
            foreach (var large in new[] { false, true })
            {
                var value = GameState.MilitaryTokenCoins(GameState.MilitaryToken(victim, large));
                var left = s.MilitaryTokenLeft(GameState.MilitaryToken(victim, large));
                var token = view.Looting.FirstOrDefault(t => t.Side == sides.Of(victim) && t.Value == value);
                if (token is not null && token.OnBoard != left)
                    notes.Add($"Looting token: the {value}-coin token on {sides.Name(victim)}'s side should be {(left ? "on the board" : "taken")}");
            }
        return notes;
    }

    static string Where(int space) => space switch
    {
        0 => "in the centre",
        > 0 => $"{space} towards White",
        _ => $"{-space} towards Red",
    };
}
