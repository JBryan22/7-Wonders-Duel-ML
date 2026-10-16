using SevenWondersDuel.Engine.Data;

namespace SevenWondersDuel.Engine.Tests;

/// <summary>
/// Pantheon ticket 03 (spec-pantheon P-A4, R7, R8): activating the Pantheon from Age II, its cost (per space, doubled
/// for the Gate, minus 2 with the Sanctuary), paying with coins, Astarte's coins and Offering tokens (the <c>offer</c>
/// decision), and the Divinities without a decision: Ishtar, Astarte, Tanit, Aphrodite, Mars.
/// </summary>
public class PantheonActivationTests
{
    const string Rules = "rules base expansion=pantheon";

    static int D(string slug) => PantheonData.DivinityIndex(slug);
    static readonly int Sanctuary = PantheonData.WonderIndex("sanctuary");
    const int Two = 0b001, Three = 0b010, Four = 0b100;

    /// <summary>
    /// A random Pantheon game at an Age II take-card decision of <paramref name="player"/>, with nothing pending but
    /// that decision, both players' coins and Offering tokens cleared, and the Pantheon set to <paramref name="spaces"/>
    /// (space 1 first; null = empty). Nobody has built the Sanctuary; <paramref name="where"/> may ask for more.
    /// </summary>
    static Game AgeTwo(int player, params string?[] spaces) => AgeTwoWhere(player, _ => true, spaces);

    static Game AgeTwoWhere(int player, Func<GameState, bool> where, params string?[] spaces)
    {
        for (ulong seed = 0; seed < 400; seed++)
        {
            var game = new Game(Ruleset.Parse(Rules));
            var chance = new RandomChanceSource(seed);
            var rng = new Rng(seed + 1000);
            var s = game.State;
            while (!s.IsOver)
            {
                if (s.Step == StepKind.Reveal) game.Apply(chance.Next(s));
                else if (s.Age == 2 && s.PendingCount == 1 && s.Pending(0).Kind == DecisionKind.TakeCard && s.PlayerToDecide == player
                    && !s.IsWonderBuilt(Sanctuary) && where(s))
                {
                    var p = s.Pantheon!;
                    for (var i = 0; i < 6; i++) p.Spaces[i] = (sbyte)(i < spaces.Length && spaces[i] is { } d ? D(d) : PantheonState.EmptySpace);
                    p.HeldOfferings[0] = p.HeldOfferings[1] = 0;
                    s.SetCoins(0, 0);
                    s.SetCoins(1, 0);
                    s.SetConflictPawn(0);
                    return game;
                }
                else
                {
                    var moves = s.LegalMoves();
                    game.Apply(moves[rng.Next(moves.Count)]);
                }
            }
        }
        throw new InvalidOperationException("No random game reached Age II");
    }

    static List<Move> Activations(GameState s) => s.LegalMoves().Where(m => m.Kind == MoveKind.Activate).ToList();

    // ---------------------------------------------------------------- cost

    [Fact]
    public void Each_player_pays_their_cost_for_the_space_and_the_gate_costs_double()
    {
        var game = AgeTwo(0, "aphrodite", "tanit", "gate", "mars", null, "ishtar");
        var s = game.State;
        Assert.Equal(3, PantheonModule.ActivationCost(s, 0, D("aphrodite")));
        Assert.Equal(8, PantheonModule.ActivationCost(s, 1, D("aphrodite")));
        Assert.Equal(4, PantheonModule.ActivationCost(s, 0, D("tanit")));
        Assert.Equal(7, PantheonModule.ActivationCost(s, 1, D("tanit")));
        Assert.Equal(10, PantheonModule.ActivationCost(s, 0, PantheonData.Gate));
        Assert.Equal(12, PantheonModule.ActivationCost(s, 1, PantheonData.Gate));
        Assert.Equal(6, PantheonModule.ActivationCost(s, 0, D("mars")));
        Assert.Equal(8, PantheonModule.ActivationCost(s, 0, D("ishtar")));
        Assert.Equal(3, PantheonModule.ActivationCost(s, 1, D("ishtar")));

        // Exactly enough coins: only the cards the player can pay for are offered.
        s.SetCoins(0, 6);
        Assert.Equal([D("aphrodite"), D("tanit"), D("mars")], Activations(s).Select(m => m.Divinity));
        Assert.Contains("costs 10 coins, P1 has 6", s.WhyIllegal(Move.Activate(PantheonData.Gate)));
        s.SetCoins(0, 10);
        Assert.Contains(Move.Activate(PantheonData.Gate), Activations(s));
        // Empty spaces and cards not in the Pantheon cannot be activated.
        Assert.Contains("not face up in the Pantheon", s.WhyIllegal(Move.Activate(D("zeus"))));
    }

    [Fact]
    public void The_sanctuary_takes_2_off_every_activation_cost_the_gate_after_doubling()
    {
        var game = AgeTwoWhere(0, s => !s.IsWonderRemoved(Sanctuary) && !s.OwnsWonder(1, Sanctuary) && s.TotalWondersBuilt < 6,
            "aphrodite", "tanit", "gate");
        var s = game.State;
        var card = Enumerable.Range(0, 20).Where(s.IsAccessible).Select(s.SlotCard).First();
        s.GiveWonder(0, Sanctuary);
        s.SetCoins(0, 50);
        game.Apply(Move.BuildWonder(card, Sanctuary));
        var chance = new RandomChanceSource(1);
        while (s.Step == StepKind.Reveal) game.Apply(chance.Next(s));
        Assert.Equal(1, PantheonModule.ActivationCost(s, 0, D("aphrodite")));
        Assert.Equal(2, PantheonModule.ActivationCost(s, 0, D("tanit")));
        Assert.Equal(8, PantheonModule.ActivationCost(s, 0, PantheonData.Gate));
        Assert.Equal(8, PantheonModule.ActivationCost(s, 1, D("aphrodite")));
        // The Sanctuary plays again: P1 activates on the extra turn for 1 coin.
        Assert.Equal(0, s.PlayerToDecide);
        s.SetCoins(0, 1);
        s.Pantheon!.HeldOfferings[0] = 0;
        game.Apply(Move.Activate(D("aphrodite")));
        Assert.Equal(0, s.Coins(0));
    }

    [Fact]
    public void The_pantheon_opens_in_age_two_and_activation_takes_no_card()
    {
        var age1 = MoveLog.Parse(string.Join('\n', ["7wd-log v1", Rules, "reveal first P1",
            "reveal tokens agriculture economy law strategy urbanism", .. Setup.Draft(0),
            "reveal age1 0.0 palisade 0.1 tavern 2.0 guard-tower 2.1 altar 2.2 lumber-yard 2.3 press "
            + "4.0 apothecary 4.1 baths 4.2 logging-camp 4.3 quarry 4.4 glassworks 4.5 stone-reserve",
            "reveal myth-tokens 1.0 greek 1.2 roman 3.0 egyptian 3.2 phoenician 3.4 greek"]));
        age1.State.Pantheon!.Spaces[0] = (sbyte)D("aphrodite");
        age1.State.SetCoins(0, 50);
        Assert.Empty(Activations(age1.State));
        Assert.Contains("from Age II", age1.State.WhyIllegal(Move.Activate(D("aphrodite"))));

        var game = AgeTwo(0, "aphrodite", null, "mars");
        var s = game.State;
        s.SetCoins(0, 3);
        var left = s.CardsLeftInAge;
        game.Apply(Move.Activate(D("aphrodite")));
        Assert.Equal(left, s.CardsLeftInAge);
        Assert.Equal(0, s.Coins(0));
        // The space stays empty; the card is P1's; the turn passes.
        Assert.Equal(PantheonState.EmptySpace, s.Pantheon!.Space(0));
        Assert.Equal(1 << D("aphrodite"), s.Pantheon.Activated(0));
        Assert.Equal(new PendingDecision(DecisionKind.TakeCard, 1), s.Pending(0));
        Assert.Equal(1, s.PendingCount);
        s.SetCoins(1, 50);
        Assert.Equal([D("mars")], Activations(s).Select(m => m.Divinity));
        Assert.Equal("P1 activate aphrodite", game.Log[^1].ToString());
    }

    // ---------------------------------------------------------------- paying

    [Fact]
    public void Offer_is_skipped_when_holding_no_offering_token()
    {
        var game = AgeTwo(1, "tanit");
        var s = game.State;
        s.SetCoins(1, 9);
        game.Apply(Move.Activate(D("tanit")));
        // 8 paid, 12 gained, no offer decision.
        Assert.Equal(13, s.Coins(1));
        Assert.Equal(new PendingDecision(DecisionKind.TakeCard, 0), s.Pending(0));
    }

    [Fact]
    public void Holding_offering_tokens_asks_which_to_spend_and_any_covering_subset_is_legal_overpaying_included()
    {
        var game = AgeTwo(0, null, null, null, null, "aphrodite");
        var s = game.State;
        var p = s.Pantheon!;
        // Cost 7. 0 coins, tokens 3 and 4: only both cover it.
        p.HeldOfferings[0] = Three | Four;
        Assert.Contains(Move.Activate(D("aphrodite")), Activations(s));
        game.Apply(Move.Activate(D("aphrodite")));
        Assert.Equal(new PendingDecision(DecisionKind.Offer, 0, D("aphrodite")), s.Pending(0));
        Assert.Equal(D("aphrodite"), p.Space(4));
        Assert.Equal([Move.Offer(Three | Four)], s.LegalMoves());
        Assert.Contains("do not cover", s.WhyIllegal(Move.Offer(Four)));
        Assert.Contains("does not hold", s.WhyIllegal(Move.Offer(Two | Four)));
        Assert.Contains("Offering tokens to spend", s.WhyIllegal(Move.Discard(0)));

        // With 4 coins: none (4 coins short), 3 (4 short? 3+4 = 7 yes), 4, 3+4 (overpaying).
        s.SetCoins(0, 4);
        Assert.Equal([Move.Offer(Three), Move.Offer(Four), Move.Offer(Three | Four)], s.LegalMoves());
        s.SetCoins(0, 7);
        Assert.Equal(4, s.LegalMoves().Count);
        s.SetCoins(0, 4);
        game.Apply(Move.Offer(Three | Four));
        // Overpaid: no coins spent, no change, both tokens gone.
        Assert.Equal(4, s.Coins(0));
        Assert.Equal(0, p.OfferingTokens(0));
        Assert.Equal(PantheonState.EmptySpace, p.Space(4));
        Assert.Equal(9, Scoring.Score(s, 0)[ScoreCategory.Divinities]);
        Assert.Equal(new PendingDecision(DecisionKind.TakeCard, 1), s.Pending(0));
        Assert.Equal(["P1 activate aphrodite", "P1 offer 3+4"], game.Log.TakeLast(2).Select(e => e.ToString()));
    }

    [Fact]
    public void Offering_tokens_cover_part_of_the_cost_and_coins_pay_the_rest()
    {
        var game = AgeTwo(1, null, null, "mars");
        var s = game.State;
        s.Pantheon!.HeldOfferings[1] = Two | Four;
        s.SetCoins(1, 5);
        game.Apply(Move.Activate(D("mars")));
        // Cost 6: "none" (5 coins) is not enough; "2" leaves 4 to pay.
        Assert.DoesNotContain(Move.Offer(0), s.LegalMoves());
        game.Apply(Move.Offer(Two));
        Assert.Equal(1, s.Coins(1));
        Assert.Equal(Four, s.Pantheon.OfferingTokens(1));
        Assert.Equal("P2 offer 2", game.Log[^1].ToString());
    }

    [Fact]
    public void Affordability_counts_coins_astarte_and_offering_tokens_together()
    {
        var game = AgeTwo(0, null, null, null, null, null, "aphrodite");
        var s = game.State;
        var p = s.Pantheon!;
        // Cost 8 = 3 coins + 2 of Astarte's + a 3 token.
        s.SetCoins(0, 3);
        p.Astarte[0] = 2;
        p.HeldOfferings[0] = Three;
        Assert.Contains(Move.Activate(D("aphrodite")), Activations(s));
        s.SetCoins(0, 2);
        Assert.Empty(Activations(s));
        Assert.Contains("costs 8 coins, P1 has 4 and Offering tokens worth 3", s.WhyIllegal(Move.Activate(D("aphrodite"))));
        s.SetCoins(0, 3);
        game.Apply(Move.Activate(D("aphrodite")));
        Assert.Equal([Move.Offer(Three)], s.LegalMoves());
        game.Apply(Move.Offer(Three));
        Assert.Equal(0, s.Coins(0));
        Assert.Equal(0, p.AstarteCoins(0));
    }

    // ---------------------------------------------------------------- Astarte

    [Fact]
    public void Astarte_gives_7_protected_coins_spent_after_the_treasury_and_worth_1_vp_each()
    {
        var game = AgeTwo(0, "astarte", "aphrodite");
        var s = game.State;
        var p = s.Pantheon!;
        s.SetCoins(0, 3);
        game.Apply(Move.Activate(D("astarte")));
        Assert.Equal(0, s.Coins(0));
        Assert.Equal(7, p.AstarteCoins(0));
        Assert.Equal(7, s.SpendableCoins(0));
        Assert.Equal(7, Scoring.Score(s, 0)[ScoreCategory.Divinities]);

        // P1's next activation: 4 coins = 2 from the treasury, then 2 of Astarte's.
        OpponentDiscards(game);
        s.SetCoins(0, 2);
        game.Apply(Move.Activate(D("aphrodite")));
        Assert.Equal(0, s.Coins(0));
        Assert.Equal(5, p.AstarteCoins(0));
        Assert.Equal(9 + 5, Scoring.Score(s, 0)[ScoreCategory.Divinities]);
        Assert.Equal(0, Scoring.Score(s, 0)[ScoreCategory.Coins]);
    }

    [Fact]
    public void Astarte_coins_pay_for_cards_after_the_treasury()
    {
        static IEnumerable<int> Dear(GameState s) => Enumerable.Range(0, 20).Where(s.IsAccessible).Select(s.SlotCard)
            .Where(c => Payment.Quote(s, 0, c).Total >= 3 && GameData.Card(c) is { Coins: 0, CoinsPer: CoinsPerKind.None });
        var game = AgeTwoWhere(0, s => Dear(s).Any());
        var s = game.State;
        var p = s.Pantheon!;
        var card = Dear(s).First();
        var cost = Payment.Quote(s, 0, card).Total;
        s.SetCoins(0, 1);
        p.Astarte[0] = (byte)(cost - 2);
        Assert.False(s.IsLegal(Move.Build(card)));
        p.Astarte[0] = (byte)(cost - 1);
        Assert.True(s.IsLegal(Move.Build(card)));
        game.Apply(Move.Build(card));
        Assert.Equal(0, s.Coins(0));
        Assert.Equal(0, p.AstarteCoins(0));
    }

    [Fact]
    public void Coin_losses_never_touch_astarte_coins()
    {
        var game = AgeTwoWhere(1, s => s.MilitaryTokenLeft(GameState.MilitaryToken(0, false)), "mars");
        var s = game.State;
        var p = s.Pantheon!;
        p.Astarte[0] = 7;
        s.SetCoins(0, 1);
        s.SetCoins(1, 8);
        // P2's Mars pushes the pawn into P1's 2-coin zone (from 2 spaces in): P1 loses the treasury's coin only.
        s.SetConflictPawn(-1);
        game.Apply(Move.Activate(D("mars")));
        Assert.Equal(-3, s.ConflictPawn);
        Assert.Equal(0, s.Coins(0));
        Assert.Equal(7, p.AstarteCoins(0));
    }

    // ---------------------------------------------------------------- the other Divinities without a decision

    [Fact]
    public void Tanit_gives_12_coins_and_aphrodite_9_vp()
    {
        var game = AgeTwo(0, "tanit", "aphrodite");
        var s = game.State;
        s.SetCoins(0, 3);
        game.Apply(Move.Activate(D("tanit")));
        Assert.Equal(12, s.Coins(0));
        Assert.Equal(0, Scoring.Score(s, 0)[ScoreCategory.Divinities]);
        OpponentDiscards(game);
        s.SetCoins(0, 12);
        var before = Scoring.Score(s, 0);
        game.Apply(Move.Activate(D("aphrodite")));
        Assert.Equal(8, s.Coins(0));
        Assert.Equal(9, Scoring.Score(s, 0)[ScoreCategory.Divinities]);
        Assert.Equal(before.Total + 9 + 8 / 3 - 12 / 3, Scoring.Score(s, 0).Total);
    }

    [Fact]
    public void Ishtar_gives_the_law_symbol_which_completes_a_science_pair()
    {
        var game = AgeTwo(0, null, null, "ishtar");
        var s = game.State;
        if (!s.HasToken(0, ProgressTokens.Law))
        {
            s.SetBoardTokens((ushort)(s.BoardTokens | 1 << ProgressTokens.Law));
            s.TakeToken(0, ProgressTokens.Law);
        }
        Assert.Equal(1, s.SymbolCount(0, ScienceSymbol.Law));
        var free = Enumerable.Range(0, 10).First(t => !s.HasToken(0, t) && !s.HasToken(1, t));
        s.SetBoardTokens((ushort)(1 << free));
        s.SetCoins(0, 5);
        game.Apply(Move.Activate(D("ishtar")));
        Assert.Equal(2, s.SymbolCount(0, ScienceSymbol.Law));
        Assert.Equal(new PendingDecision(DecisionKind.ChooseToken, 0), s.Pending(0));
        Assert.Equal(1, s.PendingCount);
        game.Apply(s.LegalMoves()[0]);
        Assert.Equal(new PendingDecision(DecisionKind.TakeCard, 1), s.Pending(0));
    }

    [Fact]
    public void Ishtar_counts_for_science_supremacy()
    {
        var game = AgeTwoWhere(0, s => !s.HasToken(0, ProgressTokens.Law), null, null, "ishtar");
        var s = game.State;
        // 5 different symbols in green cards, none of them Law.
        foreach (var symbol in new[] { ScienceSymbol.Globe, ScienceSymbol.Sundial, ScienceSymbol.Mortar, ScienceSymbol.Compass, ScienceSymbol.Wheel })
            if (s.SymbolCount(0, symbol) == 0)
                s.AddToCity(0, GameData.Cards.First(c => c.Science == symbol && !s.City(1).Contains(c.Index)).Index);
        Assert.Equal(5, s.DistinctSymbols(0));
        s.SetCoins(0, 5);
        game.Apply(Move.Activate(D("ishtar")));
        Assert.True(s.IsOver);
        Assert.Equal(new Result(0, VictoryType.Science, s.Result!.P1, s.Result.P2), s.Result);
        Assert.Equal(0, s.PendingCount);
    }

    [Fact]
    public void Mars_moves_the_pawn_2_spaces_and_can_win()
    {
        var game = AgeTwo(0, null, null, null, "mars");
        var s = game.State;
        s.SetCoins(0, 6);
        game.Apply(Move.Activate(D("mars")));
        Assert.Equal(2, s.ConflictPawn);
        Assert.False(s.IsOver);

        game = AgeTwo(0, null, null, null, "mars");
        s = game.State;
        s.SetCoins(0, 6);
        s.SetConflictPawn(7);
        game.Apply(Move.Activate(D("mars")));
        Assert.True(s.IsOver);
        Assert.Equal(0, s.Result!.Winner);
        Assert.Equal(VictoryType.Military, s.Result.Victory);
        Assert.Equal(0, s.PendingCount);
        Assert.Equal(GameState.CapitalDistance, s.ConflictPawn);
    }

    [Fact]
    public void Activating_a_divinity_whose_effect_is_impossible_is_allowed_and_does_nothing()
    {
        // Nisaba with an opponent who has no green card: the Snake has nowhere to go.
        var game = AgeTwoWhere(0, s => s.CardsOfColor(1, CardColor.Green) == 0, "nisaba");
        var s = game.State;
        var cityBefore = (s.City(0), s.City(1));
        s.SetCoins(0, 3);
        Assert.Contains(Move.Activate(D("nisaba")), Activations(s));
        game.Apply(Move.Activate(D("nisaba")));
        Assert.Equal(cityBefore, (s.City(0), s.City(1)));
        Assert.Equal(PantheonState.EmptySpace, s.Pantheon!.Space(0));
        Assert.Equal(1 << D("nisaba"), s.Pantheon.Activated(0));
        Assert.Equal(new PendingDecision(DecisionKind.TakeCard, 1), s.Pending(0));
    }

    // ---------------------------------------------------------------- log, copies, random games

    [Fact]
    public void The_log_writes_and_reads_activate_and_offer()
    {
        var s = GameState.New(Ruleset.Parse(Rules));
        Assert.Equal(new MoveEntry(0, Move.Activate(PantheonData.Gate)), MoveLog.ParseEntry("P1 activate gate", s));
        Assert.Equal(new MoveEntry(1, Move.Offer(Two | Four)), MoveLog.ParseEntry("P2 offer 2+4", s));
        Assert.Equal(new MoveEntry(1, Move.Offer(Four | Two)), MoveLog.ParseEntry("P2 offer 4+2", s));
        Assert.Equal(new MoveEntry(0, Move.Offer(0)), MoveLog.ParseEntry("P1 offer none", s));
        Assert.Equal("P2 offer 2+3+4", new MoveEntry(1, Move.Offer(Two | Three | Four)).ToString());
        Assert.Equal("P1 offer none", new MoveEntry(0, Move.Offer(0)).ToString());
        Assert.Equal("P1 activate gate", new MoveEntry(0, Move.Activate(PantheonData.Gate)).ToString());
        Assert.Throws<FormatException>(() => MoveLog.ParseEntry("P1 offer 5", s));
        Assert.Throws<FormatException>(() => MoveLog.ParseEntry("P1 offer 2+2", s));
        Assert.Throws<KeyNotFoundException>(() => MoveLog.ParseEntry("P1 activate thor", s));
        Assert.Throws<FormatException>(() => MoveLog.ParseEntry("P1 activate", s));
    }

    [Fact]
    public void Random_pantheon_games_activate_offer_and_replay()
    {
        var activations = 0;
        var offers = 0;
        var divinityPoints = 0;
        for (ulong seed = 0; seed < 150; seed++)
        {
            var game = new Game(Ruleset.Parse(Rules));
            var chance = new RandomChanceSource(seed);
            var rng = new Rng(seed + 5);
            var s = game.State;
            while (!s.IsOver)
            {
                if (s.Step == StepKind.Reveal) game.Apply(chance.Next(s));
                else
                {
                    var moves = s.LegalMoves();
                    Assert.All(moves, m => Assert.Null(s.WhyIllegal(m)));
                    // Favour activations so they occur often.
                    var activate = moves.Where(m => m.Kind == MoveKind.Activate).ToList();
                    game.Apply(activate.Count > 0 && rng.Next(3) == 0 ? activate[rng.Next(activate.Count)] : moves[rng.Next(moves.Count)]);
                    Assert.True(s.Coins(0) >= 0 && s.Coins(1) >= 0);
                }
            }
            var text = game.WriteLog();
            activations += text.Split('\n').Count(l => l.Contains(" activate "));
            offers += text.Split('\n').Count(l => l.Contains(" offer "));
            divinityPoints += s.Result!.P1[ScoreCategory.Divinities] + s.Result.P2[ScoreCategory.Divinities];
            var replayed = MoveLog.Parse(text);
            Assert.True(replayed.State.SameAs(s));
            Assert.Equal(s.ToBytes(), replayed.State.ToBytes());
            Assert.Equal(text, replayed.WriteLog());
            Assert.Equal(s.ToBytes(), s.Clone().ToBytes());
        }
        Assert.True(activations > 100, $"{activations} activations");
        Assert.True(offers > 10, $"{offers} offers");
        Assert.True(divinityPoints > 0);
    }

    [Fact]
    public void The_state_printer_shows_activated_divinities_and_astarte()
    {
        var game = AgeTwo(0, "astarte");
        game.State.SetCoins(0, 3);
        game.Apply(Move.Activate(D("astarte")));
        Assert.Contains("P1 activated: astarte (Astarte's coins: 7)", game.State.ToString());
    }

    /// <summary>The player to move (the opponent, after an activation) discards an accessible card; Reveals follow.</summary>
    static void OpponentDiscards(Game game)
    {
        var s = game.State;
        var chance = new RandomChanceSource(99);
        var player = s.PlayerToDecide;
        game.Apply(Move.Discard(Enumerable.Range(0, 20).Where(s.IsAccessible).Select(s.SlotCard).First()));
        while (s.Step == StepKind.Reveal) game.Apply(chance.Next(s));
        Assert.Equal(1 - player, s.PlayerToDecide);
        // Offering tokens the discard uncovered would add an offer decision to the activations under test.
        s.Pantheon!.HeldOfferings[0] = s.Pantheon.HeldOfferings[1] = 0;
    }
}
