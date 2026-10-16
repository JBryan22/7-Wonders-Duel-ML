using System.Diagnostics;
using System.Numerics;
using SevenWondersDuel.Engine;
using SevenWondersDuel.Engine.Data;

namespace SevenWondersDuel.Agents;

/// <summary>What a <see cref="Soak"/> run found: games played, how they ended, and every failure with its seed.</summary>
public sealed record SoakReport(int Games, int Civilian, int Military, int Science, int Draws, IReadOnlyList<string> Failures, double Seconds)
{
    public bool Passed => Failures.Count == 0;

    public override string ToString() =>
        $"{Games:N0} games in {Seconds:N1} s: {Civilian:N0} civilian ({Draws:N0} draws), {Military:N0} military, {Science:N0} science; "
        + (Passed ? "all invariants hold" : $"{Failures.Count} FAILED");
}

/// <summary>
/// The random-play invariants (spec A11.3), checked after every log entry of seeded random games: coins never
/// negative; every game ends; a legal Move exists, and each one is legal, whenever a decision is pending; replaying the
/// written Move Log gives an identical Game State and the same text; a copied state played to the end never affects the
/// original; the Observation never holds the opponent's private Reveal (and otherwise equals the state; with Pantheon,
/// worlds sampled from it are legal and look the same, and each player's redacted log gives the same view). Plus the
/// bookkeeping the rules imply: the pawn stays on the track, looted military tokens never return, Progress tokens are
/// owned at most once, cached production matches the city, and every card taken is in a city, the discard pile or
/// under a Wonder. Seeds alternate between the two start-choice rules; the promo Wonders are switched on per run.
/// A failure names its seed and rules.
/// </summary>
public static class Soak
{
    /// <summary>No game comes near this many log entries (about 100 is typical); reaching it means the game does not end.</summary>
    public const int MaxLogEntries = 1000;

    /// <summary>The rules a soak seed plays: even seeds the newer start-of-Age rule, odd seeds the 2015 one, with the given promos.</summary>
    public static Ruleset RulesFor(ulong seed, Promos promos = Promos.None, Expansions expansions = Expansions.None) =>
        new(seed % 2 == 0 ? StartChoice.New : StartChoice.Rule2015, promos, expansions);

    /// <summary>Soaks <paramref name="games"/> games from <paramref name="firstSeed"/> on, using every core.</summary>
    public static SoakReport Run(int games, ulong firstSeed = 0, int? threads = null, Promos promos = Promos.None,
        Expansions expansions = Expansions.None)
    {
        var clock = Stopwatch.StartNew();
        var outcomes = new (VictoryType? Victory, bool Draw, string? Failure)[games];
        var options = new ParallelOptions { MaxDegreeOfParallelism = threads ?? Environment.ProcessorCount };
        Parallel.For(0, games, options, i => outcomes[i] = CheckGame(firstSeed + (ulong)i, promos, expansions));
        var failures = outcomes.Where(o => o.Failure is not null).Select(o => o.Failure!).ToList();
        int Count(VictoryType v) => outcomes.Count(o => o.Victory == v);
        return new SoakReport(games, Count(VictoryType.Civilian), Count(VictoryType.Military), Count(VictoryType.Science),
            outcomes.Count(o => o.Draw), failures, clock.Elapsed.TotalSeconds);
    }

    /// <summary>Plays one seeded random game, checking every invariant; returns how it ended, or the first failure.</summary>
    public static (VictoryType? Victory, bool Draw, string? Failure) CheckGame(ulong seed, Promos promos = Promos.None,
        Expansions expansions = Expansions.None)
    {
        var rules = RulesFor(seed, promos, expansions);
        try
        {
            var result = Play(seed, rules);
            return (result.Victory, result.IsDraw, null);
        }
        catch (Exception e)
        {
            return (null, false, $"seed {seed} ({rules}): {e.Message}");
        }
    }

    static Result Play(ulong seed, Ruleset rules)
    {
        // The same seed derivation as GameRunner.PlayRandom, plus one stream for the copy check.
        var seeds = new Rng(seed);
        var chance = new RandomChanceSource(seeds.NextUInt64());
        IAgent[] agents = [new RandomAgent(seeds.NextUInt64()), new RandomAgent(seeds.NextUInt64())];
        var extra = new Rng(seeds.NextUInt64());
        var copyAt = extra.Next(100);

        var game = new Game(rules);
        var s = game.State;
        // Pantheon (spec-pantheon P-A5, P-A10): each player's own log, with the opponent's private entries redacted as
        // the Advisor keeps it, and a Determinizer for the worlds sampled from each Observation.
        var withPantheon = rules.Expansions == Expansions.Pantheon;
        Game[]? views = withPantheon ? [new Game(rules), new Game(rules)] : null;
        var determinizer = withPantheon ? new Determinizer(seed) : null;
        Span<Move> moves = stackalloc Move[GameState.MaxMoves];
        Span<Move> observed = stackalloc Move[GameState.MaxMoves];
        var militaryTokens = s.MilitaryTokensLeft;
        while (s.Step != StepKind.GameOver)
        {
            Check(game.Log.Count < MaxLogEntries, $"the game did not end within {MaxLogEntries} log entries");
            if (game.Log.Count == copyAt) CheckCopyIsIndependent(s, extra.NextUInt64());

            if (s.Step == StepKind.Reveal)
            {
                game.Apply(chance.Next(s));
            }
            else
            {
                var player = s.PlayerToDecide;
                var n = s.LegalMoves(moves);
                Check(n > 0, "a decision is pending but there is no legal Move");
                for (var i = 0; i < n; i++)
                    Check(s.WhyIllegal(moves[i]) is null, $"generated Move '{moves[i]}' is illegal: {s.WhyIllegal(moves[i])}");
                for (var p = 0; p < 2; p++)
                {
                    var o = s.Observe(p);
                    var pantheonPrivate = s.Pantheon is { } sp && HasPrivatePantheon(s, sp, p);
                    if (s.LibraryDraw != 0 && p != player)
                        Check(o.State.LibraryDraw == 0 && o.State.LibraryDrawHidden, "an Observation holds the opponent's Great Library draw");
                    else if (!pantheonPrivate)
                        Check(o.State.SameAs(s), "an Observation differs from the state with nothing private to hide");
                    if (p == player)
                        Check(o.LegalMoves(observed) == n && observed[..n].SequenceEqual(moves[..n]), "the Observation's legal Moves differ");
                    if (pantheonPrivate) CheckPantheonObservation(s, o, determinizer!);
                }
                game.Apply(agents[player].ChooseMove(s.Observe(player)));
            }
            if (views is not null)
                for (var v = 0; v < 2; v++)
                {
                    views[v].Apply(MoveLog.AsSeenBy(game.Log[^1], v));
                    Check(views[v].State.Observe(v).SameAs(s.Observe(v)),
                        $"P{v + 1}'s redacted log gives a different Observation after '{MoveLog.Format(views[v].Log[^1])}'");
                }

            for (var p = 0; p < 2; p++)
            {
                Check(s.Coins(p) >= 0, $"P{p + 1} has {s.Coins(p)} coins");
                foreach (var r in Enum.GetValues<Resource>())
                {
                    var produced = 0;
                    foreach (var c in s.City(p).Cards()) produced += GameData.Card(c).Produce[(int)r];
                    Check(s.Production(p, r) == produced, $"P{p + 1}'s cached {r} production is {s.Production(p, r)}, the city makes {produced}");
                }
            }
            Check(Math.Abs(s.ConflictPawn) <= GameState.CapitalDistance, $"the pawn is off the track at {s.ConflictPawn}");
            Check(s.MilitaryTokensLeft <= militaryTokens, "a looted military token came back");
            militaryTokens = s.MilitaryTokensLeft;
            Check((s.Tokens(0) & s.Tokens(1)) == 0, "both players own the same Progress token");
            Check((s.BoardTokens & (s.Tokens(0) | s.Tokens(1))) == 0, "a Progress token is both owned and on the board");
            Check(s.TotalWondersBuilt <= GameState.MaxWondersBuilt, "more than 7 Wonders built");
            Check((s.LibertyOffer & ~s.Seen).IsEmpty && (s.LibertyOffer & (s.City(0) | s.City(1) | s.DiscardPile)).IsEmpty,
                "a Statue of Liberty card on offer is unrevealed or already placed");
            if (s.Pantheon is { } pantheon) CheckPantheon(s, pantheon);
        }

        Check(s.Result is not null, "the game is over without a Result");
        // The Messe takes a second card from the structure, without a take-card Move; the Statue of Liberty adds its
        // 3 returned cards (two built, one discarded), less the given card when the owner's kept card won first.
        var taken = game.Log.Count(e => e is MoveEntry { Move.TakesCard: true } or MoveEntry { Move.Kind: MoveKind.Messe })
            + GameState.ReturnedCardCount * game.Log.Count(e => e is MoveEntry { Move.Kind: MoveKind.LibertyGive })
            // Pantheon: Zeus sends a structure card to the discard pile without a take-card Move.
            + game.Log.Count(e => e is MoveEntry { Move.Kind: MoveKind.Zeus });
        // (A given card once built can only move between the cities and the discard pile: Zeus, Circus Maximus,
        // Mausoleum; with Pantheon also under a Wonder: Isis.)
        var tucked = s.CardsUnderWonders(0) | s.CardsUnderWonders(1);
        if (game.Log.LastOrDefault(e => e is MoveEntry { Move.Kind: MoveKind.LibertyGive }) is MoveEntry given
            && !(s.City(0) | s.City(1) | s.DiscardPile | tucked).Contains(given.Move.Card))
            taken--;
        Check(taken == s.City(0).Count + s.City(1).Count + s.DiscardPile.Count + s.TotalWondersBuilt,
            "a taken card is not in a city, the discard pile or under a Wonder");
        Check(BitOperations.PopCount((uint)(s.Wonders(0) | s.Wonders(1))) == GameState.DraftSize, "8 Wonders were not drafted");

        // Replaying the written Move Log gives the identical state, and writes the identical text.
        var text = game.WriteLog();
        var replayed = MoveLog.Parse(text);
        Check(replayed.State.SameAs(s), "replaying the Move Log gives a different Game State");
        Check(replayed.WriteLog() == text, "the replayed Move Log writes different text");
        return s.Result!;
    }

    /// <summary>
    /// Pantheon bookkeeping (spec-pantheon P-A10): each Mythology's 3 Divinities are in its deck, on the Pantheon, in
    /// the current draw, chosen through the Divine Theater or activated, each in one place; the Gate is in play from
    /// Age II, once; the 5 Mythology tokens and 3 Offering tokens are on the structure or held by one player (every
    /// Mythology token held once Age I is over; spent Offering tokens leave the game); face-down cards only until the
    /// Age II `reveal pantheon`; Astarte's coins within 0..7; Enki's tokens owned by nobody; Minerva on the track; the
    /// Snake on a green card in a city. (The Observation invariant is <see cref="CheckPantheonObservation"/>.)
    /// </summary>
    static void CheckPantheon(GameState s, PantheonState p)
    {
        var activated = (ushort)(p.Activated(0) | p.Activated(1));
        Check((p.Activated(0) & p.Activated(1)) == 0, "both players activated the same Divinity");
        var seen = 0;
        void Place(int d, string where)
        {
            Check((seen & 1 << d) == 0, $"Divinity {PantheonData.Divinities[d].Id} is in two places (again {where})");
            seen |= 1 << d;
        }
        for (var i = 0; i < PantheonData.SpaceCount; i++)
        {
            if (p.Space(i) != PantheonState.EmptySpace) Place(p.Space(i), $"on space {i + 1}");
            else Check(!p.IsFaceDown(i), $"empty space {i + 1} is face down");
            Check(!p.IsFaceDown(i) || s.Age <= 1 || p.AwaitingPantheon, $"space {i + 1} is still face down in Age {s.Age}");
        }
        for (var i = 0; i < 2 && p.HasDraw; i++)
            if (p.Drawn(i) >= 0) Place(p.Drawn(i), "in the draw");
        foreach (var d in new Bits(activated)) Place(d, "activated");
        // The Divine Theater's choice waits for the restack before its effect (and its activation).
        if (s.Step == StepKind.Decision && s.Pending(0).Kind == DecisionKind.TheaterOrder)
            Place(s.Pending(0).Wonder, "being activated");
        foreach (var m in PantheonData.Mythologies)
        {
            Check(p.DeckSize(m.Mythology) <= PantheonData.DeckSize, $"the {m.Id} deck holds {p.DeckSize(m.Mythology)} cards");
            for (var pos = 0; pos < p.DeckSize(m.Mythology); pos++)
                if (p.DeckCard(m.Mythology, pos) is var d and >= 0)
                {
                    Check(PantheonData.Divinities[d].Mythology == m.Mythology, $"{PantheonData.Divinities[d].Id} is in the {m.Id} deck");
                    Place(d, $"in the {m.Id} deck");
                }
            var inDeck = Enumerable.Range(0, p.DeckSize(m.Mythology)).Select(pos => p.DeckCard(m.Mythology, pos)).ToHashSet();
            var outside = m.Divinities.Count(d => (seen & 1 << d) != 0 && !inDeck.Contains(d));
            Check(outside + p.DeckSize(m.Mythology) == PantheonData.DeckSize,
                $"the {m.Id} Divinities do not add up: {p.DeckSize(m.Mythology)} in the deck, {outside} out of it");
        }
        var gate = (seen & 1 << PantheonData.Gate) != 0;
        Check(gate == s.Age >= 2, $"the Gate is {(gate ? "" : "not ")}in play in Age {s.Age}");

        int mythology = 0, offerings = 0;
        for (var player = 0; player < 2; player++)
        {
            mythology += p.MythologyTokenCount(player);
            offerings += BitOperations.PopCount((uint)p.OfferingTokens(player));
            Check(p.AstarteCoins(player) is >= 0 and <= 7, $"P{player + 1} has {p.AstarteCoins(player)} of Astarte's coins");
        }
        Check((p.OfferingTokens(0) & p.OfferingTokens(1)) == 0, "both players hold the same Offering token");
        for (var slot = 0; slot < GameState.SlotCount; slot++)
            if (p.SlotToken(slot) != PantheonState.NoToken)
            {
                Check(s.Age is 1 or 2, $"a token lies on the structure in Age {s.Age}");
                if (p.SlotToken(slot) >= PantheonState.OfferingToken)
                {
                    offerings++;
                    var bit = 1 << p.SlotToken(slot) - PantheonState.OfferingToken;
                    Check(((p.OfferingTokens(0) | p.OfferingTokens(1)) & bit) == 0, "an Offering token is both held and on the structure");
                }
                else mythology++;
            }
        Check(mythology <= 5 && (s.Age < 2 || mythology == 5), $"{mythology} Mythology tokens in Age {s.Age}");
        Check(offerings <= 3, $"{offerings} Offering tokens");

        Check((p.Enki & (s.Tokens(0) | s.Tokens(1) | s.BoardTokens)) == 0, "one of Enki's tokens is also owned or on the board");
        Check(p.Minerva == PantheonState.NoMinerva || Math.Abs(p.Minerva) <= GameState.CapitalDistance, $"Minerva is off the track at {p.Minerva}");
        if (p.Snake >= 0)
            Check(GameData.Card(p.Snake).Color == CardColor.Green && (s.City(0) | s.City(1)).Contains(p.Snake),
                "the Snake is not on a green card in a city");
    }

    /// <summary>True if the state holds Pantheon knowledge <paramref name="viewer"/> does not have.</summary>
    static bool HasPrivatePantheon(GameState s, PantheonState p, int viewer)
    {
        if (p.HasDraw && s.Pending(0).Player != viewer) return true;
        for (var i = 0; i < PantheonData.SpaceCount; i++)
            if (p.IsFaceDown(i) && p.Placer(i) != viewer) return true;
        foreach (var m in PantheonData.Mythologies)
            for (var pos = 0; pos < p.DeckSize(m.Mythology); pos++)
                if (p.DeckCard(m.Mythology, pos) >= 0 && (p.DeckKnownBy(m.Mythology, pos) & 1 << viewer) == 0) return true;
        return false;
    }

    /// <summary>
    /// The Pantheon Observation invariant (spec-pantheon P-A10): the Observation never holds the opponent's draw, the
    /// Divinities they placed face down, or a deck card the viewer is not in the audience of; everything else about the
    /// Pantheon is as in the state, and the Divinities it names are exactly those it shows. A world sampled from it is a
    /// legal state (the bookkeeping holds, a decision has legal Moves) that the viewer cannot tell from the truth.
    /// </summary>
    static void CheckPantheonObservation(GameState s, Observation o, Determinizer determinizer)
    {
        var p = s.Pantheon!;
        var op = o.State.Pantheon!;
        var viewer = o.Player;
        ushort shown = 0;
        void Show(int d) => shown |= (ushort)(1 << d);
        for (var i = 0; i < PantheonData.SpaceCount; i++)
        {
            Check(op.IsFaceDown(i) == p.IsFaceDown(i) && op.Placer(i) == p.Placer(i), "an Observation moves a Pantheon card");
            if (p.IsFaceDown(i) && p.Placer(i) != viewer)
                Check(op.IsHidden(i) && op.SpaceMythology(i) == p.SpaceMythology(i), "an Observation holds the opponent's face-down Divinity");
            else
            {
                Check(op.Space(i) == p.Space(i), "an Observation changes a Pantheon space the viewer sees");
                if (p.Space(i) >= 0) Show(p.Space(i));
            }
        }
        if (p.HasDraw)
        {
            var theirs = s.Pending(0).Player != viewer;
            for (var i = 0; i < 2; i++)
            {
                Check(op.Drawn(i) == (theirs && p.Drawn(i) >= 0 ? PantheonState.Hidden : p.Drawn(i)),
                    theirs ? "an Observation holds the opponent's draw" : "an Observation changes the viewer's own draw");
                if (!theirs && p.Drawn(i) >= 0) Show(p.Drawn(i));
            }
        }
        else Check(!op.HasDraw, "an Observation has a draw the state does not");
        foreach (var m in PantheonData.Mythologies)
        {
            Check(op.DeckSize(m.Mythology) == p.DeckSize(m.Mythology), "an Observation changes a deck's size");
            for (var pos = 0; pos < PantheonData.DeckSize; pos++)
            {
                var d = p.DeckCard(m.Mythology, pos);
                var known = d >= 0 && (p.DeckKnownBy(m.Mythology, pos) & 1 << viewer) != 0;
                Check(op.DeckKnownBy(m.Mythology, pos) == p.DeckKnownBy(m.Mythology, pos), "an Observation changes who knows a deck card");
                Check(op.DeckCard(m.Mythology, pos) == (known ? d : PantheonState.Unknown),
                    known ? "an Observation loses a deck card the viewer knows" : "an Observation holds a deck card only the opponent knows");
                if (known) Show(d);
            }
        }
        Show(PantheonData.Gate);
        var activated = p.Activated(0) | p.Activated(1);
        if (s.Step == StepKind.Decision && s.Pending(0).Kind == DecisionKind.TheaterOrder) activated |= 1 << s.Pending(0).Wonder;
        Check(op.SeenDivinities == (p.SeenDivinities & (shown | activated)), "an Observation names a Divinity it does not show");

        var world = determinizer.Sample(o);
        Check(world.Observe(viewer).SameAs(o), "a sampled world looks different to the viewer");
        Check(world.Step != StepKind.Decision || world.LegalMoves().Count > 0, "a sampled world has no legal Move");
        CheckPantheon(world, world.Pantheon!);
    }

    /// <summary>A copy played to the end with its own Reveals and Moves leaves the original byte-identical.</summary>
    static void CheckCopyIsIndependent(GameState s, ulong seed)
    {
        var before = s.ToBytes();
        var copy = s.Clone();
        Check(copy.SameAs(s), "a copy differs from its original");
        var rng = new Rng(seed);
        var chance = new RandomChanceSource(rng.NextUInt64());
        var agent = new RandomAgent(rng.NextUInt64());
        for (var i = 0; copy.Step != StepKind.GameOver && i < MaxLogEntries; i++)
            if (copy.Step == StepKind.Reveal) copy.Apply(chance.Next(copy));
            else copy.Apply(agent.ChooseMove(copy.Observe(copy.PlayerToDecide)));
        Check(s.ToBytes().AsSpan().SequenceEqual(before), "playing a copy changed the original");
    }

    static void Check(bool condition, string message)
    {
        if (!condition) throw new SoakFailure(message);
    }

    sealed class SoakFailure(string message) : Exception(message);
}
