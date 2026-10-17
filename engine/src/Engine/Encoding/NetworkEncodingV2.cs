using SevenWondersDuel.Engine.Data;

namespace SevenWondersDuel.Engine;

/// <summary>
/// Encoding v2 (spec-pantheon P-A7): Encoding v1 unchanged, followed by appended Pantheon blocks, for both the input
/// and the policy. Every v1 index keeps its meaning: the first <see cref="NetworkEncoding.InputSize"/> inputs are
/// exactly v1's input and the first <see cref="NetworkEncoding.PolicySize"/> policy indices are v1's Moves. The new
/// identities (the Grand Temples, Pantheon's 2 Wonders and 3 Progress tokens) get their own appended blocks rather
/// than growing v1's, and v1's part simply leaves them out. In a base game every appended input is 0 and every
/// appended Move is masked, so a v2 network made from a v1 network by surgery (new columns and rows at zero) plays a
/// base game exactly like its parent.
/// <para>
/// Like v1, the input reads only what "me" may see: everything private to Pantheon comes from the Observation's state
/// (spec-pantheon P-A5), where the opponent's draw, their face-down Divinities and the deck cards only they know are
/// <see cref="PantheonState.Hidden"/> or <see cref="PantheonState.Unknown"/>. Those places show their Mythology and
/// who knows them, never a Divinity.
/// </para>
/// Generated as data/encoding/v2.json (<c>7wd manifest --version 2</c>); a test fails until the file is regenerated
/// after a change.
/// </summary>
public static class NetworkEncodingV2
{
    public const int Version = 2;

    static readonly int BaseCards = GameData.Cards.Length;
    static readonly int BaseWonders = GameData.Wonders.Length;
    static readonly int BaseTokens = GameData.Tokens.Length;
    static readonly int AllCards = BaseCards + PantheonData.TempleCount;
    static readonly int AllWonders = BaseWonders + PantheonData.WonderCount;
    static readonly int AllTokens = BaseTokens + PantheonData.TokenCount;
    static readonly int Divinities = PantheonData.Divinities.Length;
    static readonly int Mythologies = PantheonData.Mythologies.Length;
    static readonly int Offerings = PantheonData.Offerings.Length;
    const int Spaces = PantheonData.SpaceCount;
    /// <summary>Pantheon's decision kinds, from <see cref="DecisionKind.PlaceDivinity"/> on.</summary>
    static readonly int PantheonDecisionKinds = Enum.GetValues<DecisionKind>().Length - (int)DecisionKind.PlaceDivinity;
    const int MilitarySpaces = 2 * GameState.CapitalDistance + 1;

    const float CoinScale = 20f, CostScale = 8f;
    static readonly float DeckScale = PantheonData.DeckSize;

    /// <summary>The green cards (what Nisaba's Snake can sit on), in card order.</summary>
    static readonly int[] GreenCards = GameData.Cards.Where(c => c.Color == CardColor.Green).Select(c => c.Index).ToArray();
    static readonly int[] GreenIndexOfCard = Enumerable.Range(0, BaseCards).Select(c => Array.IndexOf(GreenCards, c)).ToArray();
    static readonly int[] BrownGreyCards = GameData.Cards
        .Where(c => c.Color is CardColor.Brown or CardColor.Grey).Select(c => c.Index).ToArray();

    static readonly int Sanctuary = PantheonData.WonderIndex("sanctuary");

    // ---------------------------------------------------------------- input layout

    static readonly string[] TempleLocations =
        ["my-city", "opp-city", "discard", "under-my-wonder", "under-opp-wonder", "statue-offer", "statue-kept", "statue-given"];
    static readonly string[] WonderStates = ["draft-offer", "my-unbuilt", "my-built", "opp-unbuilt", "opp-built", "removed", "unseen"];
    static readonly string[] TokenStates = ["on-board", "mine", "opp", "my-library-draw"];
    static readonly string[] SpaceFlags = ["empty", "gate", "face-down", "hidden"];
    static readonly string[] DivinityFlags =
        ["space1", "space2", "space3", "space4", "space5", "space6", "face-down", "deck1", "deck2", "deck3", "my-draw",
         "activated-by-me", "activated-by-opp", "unseen"];

    static readonly int SlotTokenWidth, TempleSlotWidth, SpaceWidth, DivinityWidth, DeckWidth;

    /// <summary>The appended input blocks only (offsets in the whole v2 input).</summary>
    public static readonly EncodingBlock[] PantheonInputBlocks;
    /// <summary>The input vector's blocks, in order: v1's, then Pantheon's.</summary>
    public static readonly EncodingBlock[] InputBlocks;
    public static readonly string[] InputNames;
    public static readonly int InputSize;

    // ---------------------------------------------------------------- policy layout

    /// <summary>The appended policy blocks only (offsets in the whole v2 policy).</summary>
    public static readonly EncodingBlock[] PantheonPolicyBlocks;
    public static readonly EncodingBlock[] PolicyBlocks;
    public static readonly string[] PolicyNames;
    public static readonly int PolicySize;

    /// <summary>The Move of each appended policy index (index - v1's policy size).</summary>
    static readonly Move[] AppendedMoves;
    static readonly Dictionary<Move, int> AppendedIndex;

    static NetworkEncodingV2()
    {
        string Card(int c) => GameData.Card(c).Id;
        string Wonder(int w) => GameData.Wonder(w).Id;
        string Token(int t) => GameData.Token(t).Id;
        string Divinity(int d) => PantheonData.Divinities[d].Id;
        string Mythology(int m) => PantheonData.Mythologies[m].Id;
        var temples = Enumerable.Range(BaseCards, PantheonData.TempleCount).ToArray();
        var newWonders = Enumerable.Range(BaseWonders, PantheonData.WonderCount).ToArray();
        var newTokens = Enumerable.Range(BaseTokens, PantheonData.TokenCount).ToArray();
        var slotTokens = Enumerable.Range(0, Mythologies).Select(Mythology).Concat(PantheonData.Offerings.Select(o => o.Id)).ToArray();

        // Input.
        var blocks = new List<EncodingBlock>();
        var names = new List<string>();
        var v1 = NetworkEncoding.InputSize;
        void Block(string name, string description, IEnumerable<string> blockNames)
        {
            var start = names.Count;
            names.AddRange(blockNames.Select(n => $"{name}.{n}"));
            blocks.Add(new EncodingBlock(name, v1 + start, names.Count - start, description));
        }
        SlotTokenWidth = slotTokens.Length;
        Block("slot-tokens",
            "0/1 per slot of the current Age (as in layout) and token kind: a Mythology token (Age I) or an Offering "
            + "token (Age II) still lying on the card in that slot (public, R1/R4)",
            Enumerable.Range(0, GameState.SlotCount).SelectMany(s => slotTokens.Select(t => $"slot{s:00}.{t}")));
        TempleSlotWidth = 1 + PantheonData.TempleCount;
        Block("temple-layout",
            "per slot of the current Age: back-temple (face down, Grand Temple back, R6), then a one-hot of a face-up "
            + "Grand Temple (layout's flags still say the slot is face up and accessible)",
            Enumerable.Range(0, GameState.SlotCount).SelectMany(s =>
                temples.Select(t => $"slot{s:00}.card.{Card(t)}").Prepend($"slot{s:00}.back-temple")));
        Block("temples",
            "Grand Temple-centric 0/1 per location (as v1's cards block), then unseen (in the game's Temple pool and "
            + "never named: could still be revealed)",
            TempleLocations.SelectMany(l => temples.Select(t => $"{l}.{Card(t)}")).Concat(temples.Select(t => $"unseen.{Card(t)}")));
        Block("pantheon-wonders", "0/1 per state (as v1's wonders and unseen-wonders blocks) for Pantheon's 2 Wonders",
            WonderStates.SelectMany(st => newWonders.Select(w => $"{st}.{Wonder(w)}")));
        Block("pantheon-tokens", "0/1 per state (as v1's tokens block) for Pantheon's 3 Progress tokens",
            TokenStates.SelectMany(st => newTokens.Select(t => $"{st}.{Token(t)}")));
        Block("enki-tokens", "0/1 per Progress token (base and Pantheon): one of the 2 lying on Enki (public, from its Reveal)",
            Enumerable.Range(0, AllTokens).Select(Token));
        SpaceWidth = SpaceFlags.Length + Mythologies + 4;
        Block("pantheon-spaces",
            "per Pantheon space 1..6 (space 1 at P1's capital end): 0/1 empty, gate, face-down, hidden (face down and "
            + "not known to me: only its Mythology shows), the card's Mythology one-hot, placed by me / by the opponent "
            + $"(a Divinity placed in Age I), then my and the opponent's cost to activate it now / {CostScale} (the Gate "
            + "doubled, minus 2 with the Sanctuary; 0 on an empty space)",
            Enumerable.Range(1, Spaces).SelectMany(sp => SpaceFlags
                .Concat(Enumerable.Range(0, Mythologies).Select(m => $"mythology.{Mythology(m)}"))
                .Concat(["placed-by-me", "placed-by-opp", "my-cost", "opp-cost"])
                .Select(f => $"space{sp}.{f}")));
        DivinityWidth = DivinityFlags.Length;
        Block("divinities",
            "Divinity-centric 0/1 (the Gate last), only where I know the card: on Pantheon space 1..6, face down there, "
            + "at deck position 1..3 (top first), in my pending draw, activated by me / the opponent; unseen = not "
            + "named to me (it may be in a hidden place of its Mythology or unseen in its deck)",
            Enumerable.Range(0, Divinities).SelectMany(d => DivinityFlags.Select(f => $"{Divinity(d)}.{f}")));
        DeckWidth = 1 + 2 * PantheonData.DeckSize;
        Block("decks",
            $"per Mythology deck: cards left / {DeckScale}, then per position 1..3 (top first) 0/1 known to me, known to "
            + "the opponent (who knows a deck card is public; which card it is shows in divinities only if I know it)",
            Enumerable.Range(0, Mythologies).SelectMany(m => Enumerable.Range(1, PantheonData.DeckSize)
                .SelectMany(pos => new[] { $"pos{pos}.known-to-me", $"pos{pos}.known-to-opp" })
                .Prepend("size").Select(f => $"{Mythology(m)}.{f}")));
        Block("pantheon-held",
            "Mythology tokens held per Mythology / 2 (mine, then the opponent's), Offering tokens held 0/1 (mine, then "
            + $"the opponent's), Astarte's coins / {CoinScale} (mine, opponent's)",
            Enumerable.Range(0, Mythologies).Select(m => $"my-mythology.{Mythology(m)}")
                .Concat(Enumerable.Range(0, Mythologies).Select(m => $"opp-mythology.{Mythology(m)}"))
                .Concat(PantheonData.Offerings.Select(o => $"my-offering.{o.Id}"))
                .Concat(PantheonData.Offerings.Select(o => $"opp-offering.{o.Id}"))
                .Concat(["my-astarte-coins", "opp-astarte-coins"]));
        Block("minerva",
            "0/1: the Minerva pawn's Military track space, from my point of view (positive = toward the opponent's "
            + "capital, as v1's military lead; the capitals are -9 and 9)",
            Enumerable.Range(-GameState.CapitalDistance, MilitarySpaces).Select(x => x.ToString()));
        Block("snake", "0/1: the green card Nisaba's Snake sits on", GreenCards.Select(Card));
        Block("pantheon-turn",
            "Pantheon Pending Decision kind one-hot (front of the queue; v1's turn block covers the base kinds), then "
            + "the decision's Mythology (place a drawn Divinity, the Divine Theater's deck) and Divinity (the one an "
            + "offer pays for, the one theater-order activates), and draw-hidden: the opponent's pending draw is hidden",
            Enum.GetNames<DecisionKind>().Skip((int)DecisionKind.PlaceDivinity).Select(k => $"decision.{Kebab(k)}")
                .Concat(Enumerable.Range(0, Mythologies).Select(m => $"mythology.{Mythology(m)}"))
                .Concat(Enumerable.Range(0, Divinities).Select(d => $"divinity.{Divinity(d)}"))
                .Append("draw-hidden"));
        PantheonInputBlocks = [.. blocks];
        InputBlocks = [.. NetworkEncoding.InputBlocks, .. blocks];
        InputNames = [.. NetworkEncoding.InputNames, .. names];
        InputSize = InputNames.Length;

        // Policy.
        blocks.Clear();
        var moves = new List<Move>();
        var policyNames = new List<string>();
        var v1Policy = NetworkEncoding.PolicySize;
        void Policy(string name, string description, IEnumerable<Move> blockMoves)
        {
            var start = moves.Count;
            foreach (var m in blockMoves)
            {
                moves.Add(m);
                // Zeus is slot-named in the Move Log, and slot names repeat between Ages: its policy name adds the Age.
                policyNames.Add(m.Kind == MoveKind.Zeus ? $"zeus age{m.Wonder} {MoveLog.FormatMove(m)["zeus ".Length..]}" : MoveLog.FormatMove(m));
            }
            blocks.Add(new EncodingBlock(name, v1Policy + start, moves.Count - start, description));
        }
        var allWonders = Enumerable.Range(0, AllWonders).ToArray();
        var allCards = Enumerable.Range(0, AllCards).ToArray();
        var gods = Enumerable.Range(0, Divinities).Where(d => d != PantheonData.Gate).ToArray();
        Policy("build-temple", "build <Grand Temple>", temples.Select(Move.Build));
        Policy("discard-temple", "discard <Grand Temple>", temples.Select(Move.Discard));
        Policy("wonder-pantheon",
            $"wonder <card> <wonder> for the pairs v1 lacks: base card x Pantheon Wonder (index = card * {PantheonData.WonderCount} "
            + $"+ wonder - {BaseWonders}), then Grand Temple x every Wonder",
            Enumerable.Range(0, BaseCards).SelectMany(c => newWonders.Select(w => Move.BuildWonder(c, w)))
                .Concat(temples.SelectMany(t => allWonders.Select(w => Move.BuildWonder(t, w)))));
        Policy("draft-pantheon", "draft <Pantheon Wonder>", newWonders.Select(Move.Draft));
        Policy("token-pantheon", "token <Pantheon Progress token>", newTokens.Select(Move.PickToken));
        Policy("library-pantheon", "library <Pantheon Progress token>", newTokens.Select(Move.Library));
        Policy("mausoleum-temple", "mausoleum <Grand Temple>", temples.Select(Move.Mausoleum));
        Policy("messe-temple", "messe <Grand Temple>", temples.Select(Move.Messe));
        Policy("liberty-keep-temple", "liberty-keep <Grand Temple>", temples.Select(Move.LibertyKeep));
        Policy("liberty-give-temple", "liberty-give <Grand Temple>", temples.Select(Move.LibertyGive));
        Policy("divinity", $"divinity <divinity> <space>: index = divinity * {Spaces} + space - 1",
            gods.SelectMany(d => Enumerable.Range(0, Spaces).Select(sp => Move.PlaceDivinity(d, sp))));
        Policy("activate", "activate <divinity|gate>", Enumerable.Range(0, Divinities).Select(Move.Activate));
        Policy("offer", "offer <Offering tokens>: index = the tokens as a mask (offer none = 0)",
            Enumerable.Range(0, 1 << Offerings).Select(Move.Offer));
        Policy("enki", "enki <Progress token>", Enumerable.Range(0, AllTokens).Select(Move.Enki));
        Policy("snake", "snake <green card>", GreenCards.Select(Move.Snake));
        Policy("steal", "steal <brown or grey card>", BrownGreyCards.Select(Move.Steal));
        Policy("hades", "hades <card>, the Grand Temples included", allCards.Select(Move.Hades));
        Policy("zeus", $"zeus <slot> of an Age: index = (age - 1) * {GameState.SlotCount} + slot (named 'zeus age<N> <row.col>')",
            Enumerable.Range(1, GameState.AgeCount).SelectMany(age =>
                Enumerable.Range(0, GameState.SlotCount).Select(slot => Move.Zeus(slot, age))));
        Policy("anubis", "anubis <wonder>", allWonders.Select(Move.Anubis));
        Policy("ra", "ra <wonder>", allWonders.Select(Move.Ra));
        Policy("isis", $"isis <card> <wonder>: index = card * {AllWonders} + wonder (Grand Temples included)",
            allCards.SelectMany(c => allWonders.Select(w => Move.Isis(c, w))));
        Policy("gate", "gate <divinity>", gods.Select(Move.Gate));
        Policy("theater-deck", "theater-deck <mythology>", Enumerable.Range(0, Mythologies).Select(m => Move.TheaterDeck((Mythology)m)));
        Policy("theater", "theater <divinity>", gods.Select(Move.Theater));
        Policy("theater-order", "theater-order <divinity>", gods.Select(Move.TheaterOrder));
        Policy("minerva", "minerva <space> (-9..9, positive toward P2's capital, as the Move Log writes it)",
            Enumerable.Range(-GameState.CapitalDistance, MilitarySpaces).Select(Move.Minerva));
        Policy("neptune-discard", "neptune-discard <military token>",
            Enumerable.Range(0, GameState.MilitaryTokenCount).Select(Move.NeptuneDiscard));
        Policy("neptune-apply", "neptune-apply <military token>",
            Enumerable.Range(0, GameState.MilitaryTokenCount).Select(Move.NeptuneApply));
        PantheonPolicyBlocks = [.. blocks];
        PolicyBlocks = [.. NetworkEncoding.PolicyBlocks, .. blocks];
        PolicyNames = [.. NetworkEncoding.PolicyNames, .. policyNames];
        PolicySize = PolicyNames.Length;
        AppendedMoves = [.. moves];
        AppendedIndex = moves.Select((m, i) => (m, i)).ToDictionary(x => x.m, x => v1Policy + x.i);
    }

    static string Kebab(string pascal) =>
        string.Concat(pascal.Select((ch, i) => char.IsUpper(ch) ? (i > 0 ? "-" : "") + char.ToLowerInvariant(ch) : ch.ToString()));

    // ---------------------------------------------------------------- input

    public static void Encode(Observation observation, Span<float> input) => Encode(observation.State, observation.Player, input);

    /// <summary>
    /// Writes the v2 input (v1's part, then the Pantheon blocks) of a state that holds nothing <paramref name="me"/>
    /// may not see (an Observation's state, or a search's copy of one). Allocation-free.
    /// </summary>
    public static void Encode(GameState s, int me, Span<float> input)
    {
        if (input.Length < InputSize) throw new ArgumentException($"Need {InputSize} inputs", nameof(input));
        NetworkEncoding.Encode(s, me, input[..NetworkEncoding.InputSize]);
        var rest = input[NetworkEncoding.InputSize..InputSize];
        rest.Clear();
        var opp = 1 - me;
        var i = 0;
        var p = s.Pantheon;

        // Tokens on structure slots.
        if (p is not null && s.Age > 0)
            for (var slot = 0; slot < GameState.SlotCount; slot++)
                if (p.SlotToken(slot) != PantheonState.NoToken) rest[i + slot * SlotTokenWidth + p.SlotToken(slot)] = 1;
        i += GameState.SlotCount * SlotTokenWidth;

        // Grand Temples in the layout.
        var templeBack = false;
        if (s.Age > 0)
            for (var slot = 0; slot < s.Layout.SlotCount; slot++)
            {
                if (!s.IsOccupied(slot)) continue;
                var card = s.SlotCard(slot);
                var at = i + slot * TempleSlotWidth;
                if (card == GameState.HiddenGuild && s.TempleBacks)
                {
                    rest[at] = 1;
                    templeBack = true;
                }
                else if (card >= BaseCards) rest[at + 1 + card - BaseCards] = 1;
            }
        i += GameState.SlotCount * TempleSlotWidth;

        // Grand Temples elsewhere.
        i = SetTemples(rest, i, s.City(me));
        i = SetTemples(rest, i, s.City(opp));
        i = SetTemples(rest, i, s.DiscardPile);
        i = SetTemples(rest, i, s.CardsUnderWonders(me));
        i = SetTemples(rest, i, s.CardsUnderWonders(opp));
        i = SetTemples(rest, i, s.LibertyOffer);
        i = SetTemples(rest, i, s.LibertyKept >= 0 ? CardSet.Of(s.LibertyKept) : CardSet.Empty);
        i = SetTemples(rest, i, s.LibertyGiven >= 0 ? CardSet.Of(s.LibertyGiven) : CardSet.Empty);
        // As v1's Guilds: the Temples nobody has named could still appear before Age III or while a Temple back is face down.
        i = SetTemples(rest, i, s.TempleBacks && (s.Age < GameState.AgeCount || templeBack) ? s.UnseenGuilds : CardSet.Empty);

        // Pantheon's Wonders.
        var removed = s.RemovedWonders;
        i = SetHigh(rest, i, s.DraftOffer, BaseWonders, PantheonData.WonderCount);
        i = SetHigh(rest, i, (ushort)(s.Wonders(me) & ~s.BuiltWonders(me) & ~removed), BaseWonders, PantheonData.WonderCount);
        i = SetHigh(rest, i, s.BuiltWonders(me), BaseWonders, PantheonData.WonderCount);
        i = SetHigh(rest, i, (ushort)(s.Wonders(opp) & ~s.BuiltWonders(opp) & ~removed), BaseWonders, PantheonData.WonderCount);
        i = SetHigh(rest, i, s.BuiltWonders(opp), BaseWonders, PantheonData.WonderCount);
        i = SetHigh(rest, i, removed, BaseWonders, PantheonData.WonderCount);
        i = SetHigh(rest, i, s.UnseenWonders, BaseWonders, PantheonData.WonderCount);

        // Pantheon's Progress tokens.
        var deciding = s.Step == StepKind.Decision ? s.Pending(0) : default(PendingDecision?);
        i = SetHigh(rest, i, s.BoardTokens, BaseTokens, PantheonData.TokenCount);
        i = SetHigh(rest, i, s.Tokens(me), BaseTokens, PantheonData.TokenCount);
        i = SetHigh(rest, i, s.Tokens(opp), BaseTokens, PantheonData.TokenCount);
        var myDraw = deciding is { Kind: DecisionKind.Library } d && d.Player == me ? s.LibraryDraw : (ushort)0;
        i = SetHigh(rest, i, myDraw, BaseTokens, PantheonData.TokenCount);
        i = SetHigh(rest, i, p?.Enki ?? 0, 0, AllTokens);

        if (p is not null) EncodePantheon(s, p, me, deciding, rest[i..]);
        i += Spaces * SpaceWidth + Divinities * DivinityWidth + Mythologies * DeckWidth
            + 2 * Mythologies + 2 * Offerings + 2 + MilitarySpaces + GreenCards.Length
            + PantheonDecisionKinds + Mythologies + Divinities + 1;

        if (NetworkEncoding.InputSize + i != InputSize)
            throw new InvalidOperationException($"Encoding v2 wrote {NetworkEncoding.InputSize + i} of {InputSize} inputs");
    }

    /// <summary>The Pantheon board, decks, held tokens and decision blocks (all 0 in a base game).</summary>
    static void EncodePantheon(GameState s, PantheonState p, int me, PendingDecision? deciding, Span<float> x)
    {
        var opp = 1 - me;
        var i = 0;

        // Spaces.
        for (var space = 0; space < Spaces; space++, i += SpaceWidth)
        {
            var card = p.Space(space);
            if (card == PantheonState.EmptySpace)
            {
                x[i] = 1;
                continue;
            }
            var gate = card == PantheonData.Gate;
            if (gate) x[i + 1] = 1;
            if (p.IsFaceDown(space)) x[i + 2] = 1;
            if (p.IsHidden(space)) x[i + 3] = 1;
            if (p.SpaceMythology(space) is { } m) x[i + SpaceFlags.Length + (int)m] = 1;
            var at = i + SpaceFlags.Length + Mythologies;
            if (!gate)
            {
                if (p.Placer(space) == me) x[at] = 1;
                else x[at + 1] = 1;
            }
            x[at + 2] = Cost(s, me, space, gate) / CostScale;
            x[at + 3] = Cost(s, opp, space, gate) / CostScale;
        }

        // Divinities, where this state knows them.
        for (var space = 0; space < Spaces; space++)
        {
            var d = p.Space(space);
            if (d < 0) continue;
            x[i + d * DivinityWidth + space] = 1;
            if (p.IsFaceDown(space)) x[i + d * DivinityWidth + 6] = 1;
        }
        for (var m = 0; m < Mythologies; m++)
            for (var pos = 0; pos < p.DeckSize((Mythology)m); pos++)
                if (p.DeckCard((Mythology)m, pos) is var d and >= 0) x[i + d * DivinityWidth + 7 + pos] = 1;
        if (p.HasDraw && !p.DrawHidden && deciding is { Kind: DecisionKind.PlaceDivinity } draw && draw.Player == me)
            for (var k = 0; k < 2; k++)
                if (p.Drawn(k) >= 0) x[i + p.Drawn(k) * DivinityWidth + 10] = 1;
        for (var d = 0; d < Divinities; d++)
        {
            var at = i + d * DivinityWidth;
            if ((p.Activated(me) & 1 << d) != 0) x[at + 11] = 1;
            if ((p.Activated(opp) & 1 << d) != 0) x[at + 12] = 1;
            if ((p.SeenDivinities & 1 << d) == 0) x[at + 13] = 1;
        }
        i += Divinities * DivinityWidth;

        // Decks.
        for (var m = 0; m < Mythologies; m++, i += DeckWidth)
        {
            var size = p.DeckSize((Mythology)m);
            x[i] = size / DeckScale;
            for (var pos = 0; pos < size; pos++)
            {
                var who = p.DeckKnownBy((Mythology)m, pos);
                if ((who & 1 << me) != 0) x[i + 1 + 2 * pos] = 1;
                if ((who & 1 << opp) != 0) x[i + 2 + 2 * pos] = 1;
            }
        }

        // Held tokens and Astarte's coins.
        for (var m = 0; m < Mythologies; m++)
        {
            x[i + m] = p.MythologyTokens(me, (Mythology)m) / 2f;
            x[i + Mythologies + m] = p.MythologyTokens(opp, (Mythology)m) / 2f;
        }
        i += 2 * Mythologies;
        for (var o = 0; o < Offerings; o++)
        {
            if ((p.OfferingTokens(me) & 1 << o) != 0) x[i + o] = 1;
            if ((p.OfferingTokens(opp) & 1 << o) != 0) x[i + Offerings + o] = 1;
        }
        i += 2 * Offerings;
        x[i++] = p.AstarteCoins(me) / CoinScale;
        x[i++] = p.AstarteCoins(opp) / CoinScale;

        // Minerva, from my point of view.
        if (p.Minerva != PantheonState.NoMinerva) x[i + (me == 0 ? p.Minerva : -p.Minerva) + GameState.CapitalDistance] = 1;
        i += MilitarySpaces;

        // Nisaba's Snake.
        if (p.Snake >= 0 && p.Snake < BaseCards && GreenIndexOfCard[p.Snake] >= 0) x[i + GreenIndexOfCard[p.Snake]] = 1;
        i += GreenCards.Length;

        // Pantheon decisions.
        if (deciding is { } front && front.Kind >= DecisionKind.PlaceDivinity)
        {
            x[i + (front.Kind - DecisionKind.PlaceDivinity)] = 1;
            var at = i + PantheonDecisionKinds;
            if (front.Kind is DecisionKind.PlaceDivinity or DecisionKind.Theater && front.Wonder >= 0) x[at + front.Wonder] = 1;
            if (front.Kind is DecisionKind.Offer or DecisionKind.TheaterOrder && front.Wonder >= 0) x[at + Mythologies + front.Wonder] = 1;
        }
        i += PantheonDecisionKinds + Mythologies + Divinities;
        x[i] = p.DrawHidden ? 1 : 0;
    }

    /// <summary>What activating the card on a space costs a player now (P-A4), before Offering tokens.</summary>
    static int Cost(GameState s, int player, int space, bool gate)
    {
        var cost = PantheonData.Spaces[space].Cost(player) * (gate ? 2 : 1);
        if ((s.BuiltWonders(player) & 1 << Sanctuary) != 0) cost -= PantheonModule.SanctuaryDiscount;
        return Math.Max(0, cost);
    }

    static int SetTemples(Span<float> x, int at, CardSet set)
    {
        for (var t = 0; t < PantheonData.TempleCount; t++)
            if (set.Contains(BaseCards + t)) x[at + t] = 1;
        return at + PantheonData.TempleCount;
    }

    /// <summary>Bits <paramref name="first"/>..first+count-1 of a mask, as 0/1 at <paramref name="at"/>..</summary>
    static int SetHigh(Span<float> x, int at, ushort mask, int first, int count)
    {
        for (var k = 0; k < count; k++)
            if ((mask & 1 << (first + k)) != 0) x[at + k] = 1;
        return at + count;
    }

    public static float[] Encode(Observation observation)
    {
        var input = new float[InputSize];
        Encode(observation, input);
        return input;
    }

    // ---------------------------------------------------------------- policy

    /// <summary>The policy index of a Move: v1's index for a Move v1 has, otherwise its appended index.</summary>
    public static int PolicyIndex(Move m) =>
        NetworkEncoding.HasPolicyIndex(m) ? NetworkEncoding.PolicyIndex(m)
        : AppendedIndex.TryGetValue(m, out var index) ? index
        : throw new ArgumentException($"No Encoding v2 policy index for {m}");

    /// <summary>The Move a policy index stands for (the inverse of <see cref="PolicyIndex"/>).</summary>
    public static Move MoveAt(int index)
    {
        if ((uint)index >= (uint)PolicySize) throw new ArgumentOutOfRangeException(nameof(index));
        return index < NetworkEncoding.PolicySize ? NetworkEncoding.MoveAt(index) : AppendedMoves[index - NetworkEncoding.PolicySize];
    }

    /// <summary>Sets <paramref name="mask"/> true exactly at the legal Moves' indices; returns how many there are.</summary>
    public static int LegalMask(Observation observation, Span<bool> mask)
    {
        if (mask.Length < PolicySize) throw new ArgumentException($"Need {PolicySize} entries", nameof(mask));
        mask[..PolicySize].Clear();
        Span<Move> moves = stackalloc Move[GameState.MaxMoves];
        var n = observation.LegalMoves(moves);
        for (var k = 0; k < n; k++) mask[PolicyIndex(moves[k])] = true;
        return n;
    }
}
