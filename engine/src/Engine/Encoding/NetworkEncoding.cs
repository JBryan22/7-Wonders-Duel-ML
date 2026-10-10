using SevenWondersDuel.Engine.Data;

namespace SevenWondersDuel.Engine;

/// <summary>One named, contiguous range of the input vector or the policy output.</summary>
public sealed record EncodingBlock(string Name, int Offset, int Size, string Description);

/// <summary>
/// Encoding v1 (spec A6, ADR 0003): how an <see cref="Observation"/> becomes the network's input numbers, and how the
/// policy outputs map to Moves and back. C# is the only encoder; Python reads the generated manifest
/// (<see cref="EncodingManifest"/>, committed as data/encoding/v1.json) and never encodes.
/// <para>
/// <b>Point of view:</b> "me" is the observing player, which for a training position or a search is always the player
/// to decide (spec A5). The input reads only the Observation's state, which never holds a hidden identity (ADR 0002),
/// so unknowns are zeros, never guesses. There are no Ruleset promo bits: the "unseen Wonders" block shows which
/// Wonders could still appear.
/// </para>
/// <para>
/// <b>Policy:</b> one block per Move kind, indexed by card, Wonder or token, never by slot, so an index always means the
/// same Move. Illegal Moves are masked (<see cref="LegalMask"/>).
/// </para>
/// Changing anything here changes the manifest, and the committed-manifest test fails until the file is regenerated
/// (<c>7wd manifest --out data/encoding/v1.json</c>) and, if the meaning of an index changed, <see cref="Version"/> is bumped.
/// </summary>
public static class NetworkEncoding
{
    /// <summary>The Encoding version; each ONNX model carries it and C# refuses a mismatch (spec A6).</summary>
    public const int Version = 1;

    static readonly int CardCount = GameData.Cards.Length;
    static readonly int WonderCount = GameData.Wonders.Length;
    static readonly int TokenCount = GameData.Tokens.Length;
    static readonly int DecisionKindCount = Enum.GetValues<DecisionKind>().Length;

    // ---------------------------------------------------------------- scaling (recorded in the manifest)

    const float CoinScale = 20f, CoinCap = 40f;
    const float CardsInAgeScale = GameState.SlotCount;

    /// <summary>Divisor per derived feature, in the order of <see cref="DerivedFeatures.Names"/>.</summary>
    static readonly float[] DerivedScale = DerivedFeatures.Names.Select(n => n.Split('.', 2)[1] switch
    {
        var x when x.StartsWith("production.") => 4f,
        "choice-producers" => 4f,
        var x when x.StartsWith("trading-price.") => 8f,
        "science-symbols" => 6f,
        "science-pairs" => 6f,
        "vp" => 60f,
        "wonders-built" => 4f,
        "open-chains" => 8f,
        "capital-distance" => 2 * GameState.CapitalDistance,
        var x => throw new InvalidOperationException($"No Encoding scale for derived feature '{x}'"),
    }).ToArray();

    // ---------------------------------------------------------------- input layout

    /// <summary>Per layout slot: empty, Age back, Guild back, face up, accessible, then the card one-hot.</summary>
    static readonly string[] SlotFlags = ["empty", "back-age", "back-guild", "face-up", "accessible"];
    static readonly int SlotWidth = SlotFlags.Length + CardCount;

    /// <summary>Card locations outside the layout, one 73-card block each, in this order.</summary>
    static readonly string[] CardLocations =
        ["my-city", "opp-city", "discard", "under-my-wonder", "under-opp-wonder", "statue-offer", "statue-kept", "statue-given"];

    static readonly string[] WonderStates = ["draft-offer", "my-unbuilt", "my-built", "opp-unbuilt", "opp-built", "removed"];
    static readonly string[] TokenStates = ["on-board", "mine", "opp", "my-library-draw"];

    static readonly string[] Scalars =
    [
        "my-coins", "opp-coins", "my-military-lead",
        "military-token.on-my-side.2", "military-token.on-my-side.5",
        "military-token.on-opp-side.2", "military-token.on-opp-side.5",
        "cards-left-in-age", "start-rule-2015",
    ];

    static readonly string[] TurnFlags =
        ["me-to-decide", "my-turn", "play-again", "destroy-brown", "destroy-grey", "library-draw-hidden"];

    /// <summary>The input vector's blocks, in order.</summary>
    public static readonly EncodingBlock[] InputBlocks;
    /// <summary>A name for every input index.</summary>
    public static readonly string[] InputNames;
    public static readonly int InputSize;

    // ---------------------------------------------------------------- policy layout

    /// <summary>The cards a <c>destroy</c> Move can name (brown and grey), in card order, and each card's index among them.</summary>
    static readonly int[] DestroyCards = GameData.Cards
        .Where(c => c.Color is CardColor.Brown or CardColor.Grey).Select(c => c.Index).ToArray();
    static readonly int[] DestroyIndexOfCard = Enumerable.Range(0, GameData.Cards.Length)
        .Select(c => Array.IndexOf(DestroyCards, c)).ToArray();

    static readonly int BuildAt, DiscardAt, WonderAt, DraftAt, TokenAt, LibraryAt, DestroyAt, MausoleumAt, MesseAt,
        LibertyKeepAt, LibertyGiveAt, StartAt;

    /// <summary>The policy output's blocks, in order.</summary>
    public static readonly EncodingBlock[] PolicyBlocks;
    /// <summary>A name for every policy index: the Move it stands for, as the Move Log writes it ("wonder baths sphinx").</summary>
    public static readonly string[] PolicyNames;
    public static readonly int PolicySize;

    // ---------------------------------------------------------------- heads

    /// <summary>The outcome-class head's classes (spec A6), from the point of view of "me".</summary>
    public static readonly string[] OutcomeClasses =
        ["win-military", "win-science", "win-civilian", "draw", "loss-military", "loss-science", "loss-civilian"];

    static NetworkEncoding()
    {
        var blocks = new List<EncodingBlock>();
        var names = new List<string>();
        void Block(string name, string description, IEnumerable<string> blockNames)
        {
            var start = names.Count;
            names.AddRange(blockNames.Select(n => $"{name}.{n}"));
            blocks.Add(new EncodingBlock(name, start, names.Count - start, description));
        }
        string Card(int c) => GameData.Cards[c].Id;
        string Wonder(int w) => GameData.Wonders[w].Id;
        string Token(int t) => GameData.Tokens[t].Id;
        var cards = Enumerable.Range(0, CardCount);

        Block("layout",
            "20 slots of the current Age (slot index as in data/base/layouts.json, empty slots past an Age's last slot "
            + "stay 0); per slot 0/1 flags empty, back-age (face down, unknown Age card), back-guild (face down, purple "
            + "Guild back), face-up (known card), accessible, then a one-hot of the face-up card; all 0 before Age I",
            Enumerable.Range(0, GameState.SlotCount).SelectMany(s =>
                SlotFlags.Select(f => $"slot{s:00}.{f}").Concat(cards.Select(c => $"slot{s:00}.card.{Card(c)}"))));
        Block("age", "current Age one-hot (all 0 during the Wonder draft)", ["1", "2", "3"]);
        Block("cards",
            "card-centric 0/1 per location: my city, opponent's city, discard pile, tucked under my / the opponent's "
            + "Wonders, the Statue of Liberty's undecided returned cards, the card its owner kept (while liberty-give is "
            + "pending), the card given to the opponent (until it is built)",
            CardLocations.SelectMany(l => cards.Select(c => $"{l}.{Card(c)}")));
        Block("unseen-cards",
            "0/1: never named so far and could still be revealed (Age cards of the current or a later Age; Guilds before "
            + "Age III or while a Guild back is face down)",
            cards.Select(Card));
        Block("wonders", "0/1 per Wonder and state; removed = the unbuilt Wonder removed when 7 are built",
            WonderStates.SelectMany(st => Enumerable.Range(0, WonderCount).Select(w => $"{st}.{Wonder(w)}")));
        Block("unseen-wonders", "0/1: in the Wonder pool and not offered yet (could still appear in the draft)",
            Enumerable.Range(0, WonderCount).Select(Wonder));
        Block("tokens", "0/1 per Progress token: on the board, mine, the opponent's, in my pending Great Library draw",
            TokenStates.SelectMany(st => Enumerable.Range(0, TokenCount).Select(t => $"{st}.{Token(t)}")));
        Block("scalars",
            $"coins / {CoinScale} (capped at {CoinCap}); my military lead / {GameState.CapitalDistance} (-1..1); "
            + "military tokens still on the board (on my side = the opponent's pawn loots me); cards left in the Age / "
            + $"{CardsInAgeScale}; start-rule-2015 = 1 under start-choice=2015",
            Scalars);
        Block("turn",
            "Pending Decision kind one-hot (front of the queue; all 0 if none), then 0/1: I am the player to decide, it "
            + "is my turn, a play-again is pending, the destroy target colour, the opponent's Great Library draw is hidden",
            Enum.GetNames<DecisionKind>().Select(k => $"decision.{Kebab(k)}").Concat(TurnFlags));
        Block("derived",
            "DerivedFeatures (me, then opponent, then capital distances) divided by: production 4, choice producers 4, "
            + "trading price 8, science symbols 6, science pairs 6, VP 60, Wonders built 4, open chains 8, capital "
            + $"distance {2 * GameState.CapitalDistance}",
            DerivedFeatures.Names);
        InputBlocks = [.. blocks];
        InputNames = [.. names];
        InputSize = names.Count;

        blocks.Clear();
        names.Clear();
        void Policy(string name, string description, IEnumerable<string> moveNames, out int at)
        {
            at = names.Count;
            names.AddRange(moveNames);
            blocks.Add(new EncodingBlock(name, at, names.Count - at, description));
        }
        Policy("build", "build <card>", cards.Select(c => $"build {Card(c)}"), out BuildAt);
        Policy("discard", "discard <card>", cards.Select(c => $"discard {Card(c)}"), out DiscardAt);
        Policy("wonder", $"wonder <card> <wonder>: index = card * {WonderCount} + wonder",
            cards.SelectMany(c => Enumerable.Range(0, WonderCount).Select(w => $"wonder {Card(c)} {Wonder(w)}")), out WonderAt);
        Policy("draft", "draft <wonder>", Enumerable.Range(0, WonderCount).Select(w => $"draft {Wonder(w)}"), out DraftAt);
        Policy("token", "token <token>", Enumerable.Range(0, TokenCount).Select(t => $"token {Token(t)}"), out TokenAt);
        Policy("library", "library <token>", Enumerable.Range(0, TokenCount).Select(t => $"library {Token(t)}"), out LibraryAt);
        Policy("destroy", "destroy <card>, brown and grey cards only", DestroyCards.Select(c => $"destroy {Card(c)}"), out DestroyAt);
        Policy("mausoleum", "mausoleum <card>", cards.Select(c => $"mausoleum {Card(c)}"), out MausoleumAt);
        Policy("messe", "messe <card>", cards.Select(c => $"messe {Card(c)}"), out MesseAt);
        Policy("liberty-keep", "liberty-keep <card>", cards.Select(c => $"liberty-keep {Card(c)}"), out LibertyKeepAt);
        Policy("liberty-give", "liberty-give <card>", cards.Select(c => $"liberty-give {Card(c)}"), out LibertyGiveAt);
        Policy("start", "start me / start opponent", ["start me", "start opponent"], out StartAt);
        PolicyBlocks = [.. blocks];
        PolicyNames = [.. names];
        PolicySize = names.Count;
    }

    static string Kebab(string pascal) =>
        string.Concat(pascal.Select((ch, i) => char.IsUpper(ch) ? (i > 0 ? "-" : "") + char.ToLowerInvariant(ch) : ch.ToString()));

    // ---------------------------------------------------------------- input

    /// <summary>
    /// Writes the input vector of <paramref name="observation"/> (from its player's point of view) into
    /// <paramref name="input"/>, at least <see cref="InputSize"/> long. Allocation-free.
    /// </summary>
    public static void Encode(Observation observation, Span<float> input) => Encode(observation.State, observation.Player, input);

    /// <summary>
    /// The same from a state that holds nothing <paramref name="me"/> may not see: an Observation's state, or a state
    /// search reuses after copying one. Never pass the true Game State of a game with an opponent's pending Great
    /// Library draw.
    /// </summary>
    public static void Encode(GameState s, int me, Span<float> input)
    {
        if (input.Length < InputSize) throw new ArgumentException($"Need {InputSize} inputs", nameof(input));
        if (me is not (0 or 1)) throw new ArgumentOutOfRangeException(nameof(me));
        input = input[..InputSize];
        input.Clear();
        var opp = 1 - me;
        var i = 0;

        // Layout.
        if (s.Age > 0)
        {
            var slotCount = s.Layout.SlotCount;
            for (var slot = 0; slot < slotCount; slot++)
            {
                var at = i + slot * SlotWidth;
                var card = s.SlotCard(slot);
                if (!s.IsOccupied(slot)) input[at] = 1;
                else
                {
                    if (card == GameState.Hidden) input[at + 1] = 1;
                    else if (card == GameState.HiddenGuild) input[at + 2] = 1;
                    else if (card >= 0)
                    {
                        input[at + 3] = 1;
                        input[at + SlotFlags.Length + card] = 1;
                    }
                    if (s.IsAccessible(slot)) input[at + 4] = 1;
                }
            }
        }
        i += GameState.SlotCount * SlotWidth;
        if (s.Age > 0) input[i + s.Age - 1] = 1;
        i += GameState.AgeCount;

        // Cards elsewhere.
        i = Set(input, i, s.City(me));
        i = Set(input, i, s.City(opp));
        i = Set(input, i, s.DiscardPile);
        i = Set(input, i, s.CardsUnderWonders(me));
        i = Set(input, i, s.CardsUnderWonders(opp));
        i = Set(input, i, s.LibertyOffer);
        i = Set(input, i, s.LibertyKept >= 0 ? CardSet.Of(s.LibertyKept) : CardSet.Empty);
        i = Set(input, i, s.LibertyGiven >= 0 ? CardSet.Of(s.LibertyGiven) : CardSet.Empty);

        // Unseen cards.
        var unseen = CardSet.Empty;
        for (var age = Math.Max(s.Age, 1); age <= GameState.AgeCount; age++) unseen |= s.UnseenPool(age);
        if (s.Age < GameState.AgeCount || AnyGuildBack(s)) unseen |= s.UnseenGuilds;
        i = Set(input, i, unseen);

        // Wonders.
        var removed = s.RemovedWonders;
        i = Set(input, i, s.DraftOffer, WonderCount);
        i = Set(input, i, (ushort)(s.Wonders(me) & ~s.BuiltWonders(me) & ~removed), WonderCount);
        i = Set(input, i, s.BuiltWonders(me), WonderCount);
        i = Set(input, i, (ushort)(s.Wonders(opp) & ~s.BuiltWonders(opp) & ~removed), WonderCount);
        i = Set(input, i, s.BuiltWonders(opp), WonderCount);
        i = Set(input, i, removed, WonderCount);
        i = Set(input, i, s.UnseenWonders, WonderCount);

        // Progress tokens.
        var deciding = s.Step == StepKind.Decision ? s.Pending(0) : default(PendingDecision?);
        i = Set(input, i, s.BoardTokens, TokenCount);
        i = Set(input, i, s.Tokens(me), TokenCount);
        i = Set(input, i, s.Tokens(opp), TokenCount);
        var myDraw = deciding is { Kind: DecisionKind.Library } d && d.Player == me ? s.LibraryDraw : (ushort)0;
        i = Set(input, i, myDraw, TokenCount);

        // Scalars.
        input[i++] = Math.Min(s.Coins(me), CoinCap) / CoinScale;
        input[i++] = Math.Min(s.Coins(opp), CoinCap) / CoinScale;
        input[i++] = (float)s.MilitaryLead(me) / GameState.CapitalDistance;
        input[i++] = s.MilitaryTokenLeft(GameState.MilitaryToken(me, large: false)) ? 1 : 0;
        input[i++] = s.MilitaryTokenLeft(GameState.MilitaryToken(me, large: true)) ? 1 : 0;
        input[i++] = s.MilitaryTokenLeft(GameState.MilitaryToken(opp, large: false)) ? 1 : 0;
        input[i++] = s.MilitaryTokenLeft(GameState.MilitaryToken(opp, large: true)) ? 1 : 0;
        input[i++] = s.Age > 0 ? s.CardsLeftInAge / CardsInAgeScale : 0;
        input[i++] = s.Rules.StartChoice == StartChoice.Rule2015 ? 1 : 0;

        // Turn.
        if (deciding is { } front) input[i + (int)front.Kind] = 1;
        i += DecisionKindCount;
        input[i++] = deciding is { } p && p.Player == me ? 1 : 0;
        input[i++] = s.CurrentPlayer == me ? 1 : 0;
        input[i++] = s.PlayAgain ? 1 : 0;
        var colour = s.DestroyColor;
        input[i++] = colour == CardColor.Brown ? 1 : 0;
        input[i++] = colour == CardColor.Grey ? 1 : 0;
        input[i++] = s.LibraryDrawHidden ? 1 : 0;

        // Derived.
        Span<int> derived = stackalloc int[DerivedFeatures.Count];
        DerivedFeatures.Write(s, me, derived);
        for (var k = 0; k < DerivedFeatures.Count; k++) input[i++] = derived[k] / DerivedScale[k];

        if (i != InputSize) throw new InvalidOperationException($"Encoding wrote {i} of {InputSize} inputs");
    }

    /// <summary>A float array holding the input vector of <paramref name="observation"/>.</summary>
    public static float[] Encode(Observation observation)
    {
        var input = new float[InputSize];
        Encode(observation, input);
        return input;
    }

    static bool AnyGuildBack(GameState s)
    {
        if (s.Age != GameState.AgeCount) return false;
        for (var slot = 0; slot < GameState.SlotCount; slot++)
            if (s.SlotCard(slot) == GameState.HiddenGuild && s.IsOccupied(slot)) return true;
        return false;
    }

    static int Set(Span<float> input, int at, CardSet set)
    {
        foreach (var c in set.Cards()) input[at + c] = 1;
        return at + CardCount;
    }

    static int Set(Span<float> input, int at, ushort mask, int size)
    {
        foreach (var b in new Bits(mask)) input[at + b] = 1;
        return at + size;
    }

    // ---------------------------------------------------------------- policy

    /// <summary>The policy index of a Move. Every Move the Engine can generate has exactly one.</summary>
    public static int PolicyIndex(Move m) => m.Kind switch
    {
        MoveKind.Build => BuildAt + m.Card,
        MoveKind.Discard => DiscardAt + m.Card,
        MoveKind.Wonder => WonderAt + m.Card * WonderCount + m.Wonder,
        MoveKind.Draft => DraftAt + m.Wonder,
        MoveKind.Token => TokenAt + m.Token,
        MoveKind.Library => LibraryAt + m.Token,
        MoveKind.Destroy => DestroyIndexOfCard[m.Card] is var d and >= 0 ? DestroyAt + d
            : throw new ArgumentException($"No policy index for {m}: only brown and grey cards can be destroyed"),
        MoveKind.Mausoleum => MausoleumAt + m.Card,
        MoveKind.Messe => MesseAt + m.Card,
        MoveKind.LibertyKeep => LibertyKeepAt + m.Card,
        MoveKind.LibertyGive => LibertyGiveAt + m.Card,
        MoveKind.StartMe => StartAt,
        MoveKind.StartOpponent => StartAt + 1,
        _ => throw new ArgumentException($"No policy index for {m}"),
    };

    /// <summary>The Move a policy index stands for (the inverse of <see cref="PolicyIndex"/>).</summary>
    public static Move MoveAt(int index)
    {
        if ((uint)index >= (uint)PolicySize) throw new ArgumentOutOfRangeException(nameof(index));
        if (index < DiscardAt) return Move.Build(index - BuildAt);
        if (index < WonderAt) return Move.Discard(index - DiscardAt);
        if (index < DraftAt) return Move.BuildWonder((index - WonderAt) / WonderCount, (index - WonderAt) % WonderCount);
        if (index < TokenAt) return Move.Draft(index - DraftAt);
        if (index < LibraryAt) return Move.PickToken(index - TokenAt);
        if (index < DestroyAt) return Move.Library(index - LibraryAt);
        if (index < MausoleumAt) return Move.Destroy(DestroyCards[index - DestroyAt]);
        if (index < MesseAt) return Move.Mausoleum(index - MausoleumAt);
        if (index < LibertyKeepAt) return Move.Messe(index - MesseAt);
        if (index < LibertyGiveAt) return Move.LibertyKeep(index - LibertyKeepAt);
        if (index < StartAt) return Move.LibertyGive(index - LibertyGiveAt);
        return index == StartAt ? Move.StartMe : Move.StartOpponent;
    }

    /// <summary>
    /// Sets <paramref name="mask"/> (at least <see cref="PolicySize"/> long) true exactly at the legal Moves' indices
    /// and returns how many there are; the network's other logits are masked to −∞ before the softmax.
    /// </summary>
    public static int LegalMask(Observation observation, Span<bool> mask)
    {
        if (mask.Length < PolicySize) throw new ArgumentException($"Need {PolicySize} entries", nameof(mask));
        mask[..PolicySize].Clear();
        Span<Move> moves = stackalloc Move[GameState.MaxMoves];
        var n = observation.LegalMoves(moves);
        for (var k = 0; k < n; k++) mask[PolicyIndex(moves[k])] = true;
        return n;
    }

    // ---------------------------------------------------------------- heads

    /// <summary>The value target: +1 if <paramref name="me"/> won, 0 for a draw, -1 for a loss.</summary>
    public static float ValueTarget(Result result, int me) => result.Winner < 0 ? 0 : result.Winner == me ? 1 : -1;

    /// <summary>The outcome-class target: an index into <see cref="OutcomeClasses"/>, from <paramref name="me"/>'s point of view.</summary>
    public static int OutcomeClass(Result result, int me)
    {
        if (result.Winner < 0) return 3;
        var byType = result.Victory switch
        {
            VictoryType.Military => 0,
            VictoryType.Science => 1,
            VictoryType.Civilian => 2,
            _ => throw new ArgumentException($"No outcome class for {result.Victory}"),
        };
        return result.Winner == me ? byType : 4 + byType;
    }
}
