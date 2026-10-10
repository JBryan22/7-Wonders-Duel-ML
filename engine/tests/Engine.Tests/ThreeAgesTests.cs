using System.Numerics;
using SevenWondersDuel.Engine.Data;

namespace SevenWondersDuel.Engine.Tests;

/// <summary>
/// Ages II and III: the layouts, the Guild back pattern, the start-of-Age choice under both Ruleset flags
/// (rules-reference §9), yellow and Guild coins when built, chains across Ages and the end of the game.
/// </summary>
public class ThreeAgesTests
{
    static int C(string slug) => GameData.CardIndex(slug);

    static readonly Ruleset New = Ruleset.Default;
    static readonly Ruleset Rule2015 = new(StartChoice.Rule2015);

    /// <summary>
    /// Plays from the start, discarding every card and answering start choices with "start me", until
    /// <paramref name="stop"/> holds before the next step. P1 plays first, so with 20 cards per Age P2 takes the last card.
    /// </summary>
    static Game PlayUntil(Ruleset rules, Func<GameState, bool> stop, ulong seed = 1)
    {
        var game = new Game(rules);
        game.Apply(new FirstPlayerReveal(0));
        var chance = new RandomChanceSource(seed);
        while (!stop(game.State))
        {
            var s = game.State;
            if (s.Step == StepKind.Reveal) game.Apply(chance.Next(s));
            else if (s.Pending(0).Kind == DecisionKind.ChooseStarter) game.Apply(Move.StartMe);
            else game.Apply(s.LegalMoves().Last(m => m.Kind == MoveKind.Discard));
        }
        return game;
    }

    /// <summary>The state just before the last card of Age I is taken.</summary>
    static Game LastCardOfAgeOne(Ruleset rules) =>
        PlayUntil(rules, s => s.Age == 1 && s.Step == StepKind.Decision && s.CardsLeftInAge == 1);

    static Game EndOfAgeOne(Ruleset rules, int pawn = 0)
    {
        var game = LastCardOfAgeOne(rules);
        game.State.SetConflictPawn(pawn);
        game.Apply(game.State.LegalMoves().Last());
        return game;
    }

    static Game AgeTwoOpened(Ruleset rules, int pawn = 0)
    {
        var game = EndOfAgeOne(rules, pawn);
        game.Apply(new RandomChanceSource(5).Next(game.State));
        return game;
    }

    static Game StartOfAgeThree() => PlayUntil(New, s => s.Age == 3);

    // ---------------------------------------------------------------- Age II and the start of an Age

    [Fact]
    public void Age_two_lays_out_six_five_four_three_two_with_twelve_face_up_and_the_bottom_two_accessible()
    {
        var game = EndOfAgeOne(New);
        var s = game.State;
        Assert.Equal(2, s.Age);
        Assert.Equal(StepKind.Reveal, s.Step);
        Assert.Equal(12, BitOperations.PopCount(s.SlotsToReveal));
        game.Apply(new RandomChanceSource(5).Next(s));
        Assert.Equal(new[] { 18, 19 }, Enumerable.Range(0, 20).Where(s.IsAccessible));
        Assert.All(Enumerable.Range(0, 20).Where(i => s.Layout.Slots[i].FaceUp), i => Assert.True(s.SlotCard(i) >= 0));
        Assert.All(Enumerable.Range(0, 20), i =>
        {
            var card = s.SlotCard(i);
            Assert.True(card == GameState.Hidden || GameData.Cards[card].Age == 2);
        });
    }

    [Fact]
    public void New_rule_on_a_military_tie_the_last_taker_chooses_after_seeing_the_new_layout()
    {
        var game = AgeTwoOpened(New);
        var s = game.State;
        Assert.Equal(StepKind.Decision, s.Step);
        Assert.Equal(DecisionKind.ChooseStarter, s.Pending(0).Kind);
        Assert.Equal(1, s.PlayerToDecide); // P2 took the 20th card
        Assert.Equal(new[] { Move.StartMe, Move.StartOpponent }, s.LegalMoves());

        game.Apply(Move.StartOpponent);
        Assert.Equal(DecisionKind.TakeCard, s.Pending(0).Kind);
        Assert.Equal(0, s.PlayerToDecide);
        Assert.Equal(0, s.CurrentPlayer);
        Assert.Equal("P2 start opponent", game.Log[^1].ToString());
    }

    [Fact]
    public void New_rule_start_me_lets_the_chooser_start()
    {
        var game = AgeTwoOpened(New);
        game.Apply(Move.StartMe);
        Assert.Equal(1, game.State.PlayerToDecide);
        Assert.Equal(DecisionKind.TakeCard, game.State.Pending(0).Kind);
    }

    [Fact]
    public void Rule_2015_on_a_military_tie_the_last_taker_simply_starts()
    {
        var game = AgeTwoOpened(Rule2015);
        var s = game.State;
        Assert.Equal(DecisionKind.TakeCard, s.Pending(0).Kind);
        Assert.Equal(1, s.PlayerToDecide);
        Assert.DoesNotContain(s.LegalMoves(), m => !m.TakesCard);
    }

    [Theory]
    [InlineData("new", 2, 1)]    // pawn toward P2's capital: P2 is weaker and chooses
    [InlineData("new", -3, 0)]   // toward P1's capital: P1 chooses
    [InlineData("2015", 2, 1)]   // the flag only changes the tie
    [InlineData("2015", -7, 0)]
    public void The_weaker_military_chooses_under_both_flags(string flag, int pawn, int chooser)
    {
        var game = AgeTwoOpened(Ruleset.Parse($"rules base start-choice={flag}"), pawn);
        Assert.Equal(DecisionKind.ChooseStarter, game.State.Pending(0).Kind);
        Assert.Equal(chooser, game.State.PlayerToDecide);
        game.Apply(Move.StartOpponent);
        Assert.Equal(1 - chooser, game.State.PlayerToDecide);
    }

    [Fact]
    public void Start_moves_and_card_moves_are_only_legal_for_their_own_decision()
    {
        var game = AgeTwoOpened(New);
        var s = game.State;
        var card = s.SlotCard(18);
        Assert.Contains("who starts", s.WhyIllegal(Move.Discard(card)));
        game.Apply(Move.StartMe);
        Assert.Contains("which card", s.WhyIllegal(Move.StartMe));
        Assert.Throws<InvalidOperationException>(() => s.Apply(Move.StartOpponent));
    }

    [Fact]
    public void Start_choice_round_trips_through_the_move_log()
    {
        var game = AgeTwoOpened(New);
        game.Apply(Move.StartOpponent);
        var replayed = MoveLog.Parse(game.WriteLog());
        Assert.True(replayed.State.SameAs(game.State));
        Assert.Throws<FormatException>(() => MoveLog.Parse(game.WriteLog().Replace("start opponent", "start nobody")));
    }

    // ---------------------------------------------------------------- Age III and the Guild backs

    [Fact]
    public void Age_three_waits_for_the_guild_back_pattern_before_naming_any_card()
    {
        var game = StartOfAgeThree();
        var s = game.State;
        Assert.True(s.AwaitingGuildPattern);
        Assert.Equal(StepKind.Reveal, s.Step);
        Assert.Equal(0u, s.SlotsToReveal);
        Assert.Throws<InvalidOperationException>(() => s.Apply(new SlotReveal(3, true, [(0, C("palace"))])));

        game.Apply(MoveLog.ParseEntry("reveal guilds 0.0 1.1 6.1", s));
        Assert.False(s.AwaitingGuildPattern);
        Assert.Equal(GameState.HiddenGuild, s.SlotCard(0));
        Assert.Equal(GameState.HiddenGuild, s.SlotCard(3));  // 1.1, face down: stays an unknown Guild
        Assert.Equal(GameState.HiddenGuild, s.SlotCard(19));
        Assert.Equal(GameState.Hidden, s.SlotCard(1));
        Assert.Equal(12, BitOperations.PopCount(s.SlotsToReveal));
        Assert.Equal("reveal guilds 0.0 1.1 6.1", game.Log[^1].ToString());
    }

    [Theory]
    [InlineData("reveal guilds 0.0 1.1", "3 different slots")]
    [InlineData("reveal guilds 0.0 0.0 1.1", "3 different slots")]
    [InlineData("reveal guilds 0.0 1.1 7.0", "not a slot")]
    public void Bad_guild_patterns_are_rejected(string line, string reason)
    {
        var game = StartOfAgeThree();
        var log = game.WriteLog() + line + "\n";
        var e = Assert.Throws<FormatException>(() => MoveLog.Parse(log));
        Assert.Contains(reason, e.Message);
    }

    [Fact]
    public void A_guild_back_reveals_only_a_guild_and_any_other_back_only_an_age_three_card()
    {
        var game = StartOfAgeThree();
        game.Apply(MoveLog.ParseEntry("reveal guilds 0.0 1.1 6.1", game.State));
        var log = game.WriteLog();

        const string Rest = "2.0 palace 2.1 town-hall 2.2 obelisk 2.3 gardens 4.0 pantheon 4.1 senate 4.2 academy 4.3 study 6.0 port";
        var ok = MoveLog.Parse(log + $"reveal age3 0.0 builders-guild 0.1 arena {Rest} 6.1 scientists-guild\n");
        Assert.Equal(C("builders-guild"), ok.State.SlotCard(0));

        var e = Assert.Throws<FormatException>(() => MoveLog.Parse(log + $"reveal age3 0.0 arena 0.1 builders-guild {Rest} 6.1 scientists-guild\n"));
        Assert.Contains("not an unseen Guild", e.Message);
        e = Assert.Throws<FormatException>(() => MoveLog.Parse(log + $"reveal age3 0.0 builders-guild 0.1 builders-guild {Rest} 6.1 scientists-guild\n"));
        Assert.Contains("not an unseen Age 3 card", e.Message);
    }

    [Fact]
    public void The_random_chance_source_fills_guild_backs_with_guilds()
    {
        for (ulong seed = 0; seed < 200; seed++)
        {
            var game = PlayUntil(New, s => s.Age == 3 && s.Step == StepKind.Decision, seed);
            var s = game.State;
            var pattern = game.Log.OfType<GuildPatternReveal>().Single().Slots;
            for (var slot = 0; slot < 20; slot++)
            {
                var card = s.SlotCard(slot);
                if (card >= 0) Assert.Equal(pattern.Contains(slot), GameData.Cards[card].IsGuild);
                else Assert.Equal(pattern.Contains(slot) ? GameState.HiddenGuild : GameState.Hidden, card);
            }
        }
    }

    [Fact]
    public void Taking_the_middle_row_card_of_age_three_opens_two_cards()
    {
        // 3.0 (slot 9) alone covers 2.0 and 2.1 (slots 5 and 6).
        var game = PlayUntil(New, s => s.Age == 3 && s.Step == StepKind.Decision && !s.IsOccupied(11) && !s.IsOccupied(12)
            && s.IsAccessible(9));
        var s = game.State;
        Assert.False(s.IsAccessible(5));
        Assert.False(s.IsAccessible(6));
        game.Apply(Move.Discard(s.SlotCard(9)));
        Assert.True(s.IsAccessible(5));
        Assert.True(s.IsAccessible(6));
    }

    [Fact]
    public void The_game_ends_with_a_civilian_result_when_age_three_is_empty()
    {
        var game = PlayUntil(New, s => s.IsOver);
        var s = game.State;
        Assert.Equal(StepKind.GameOver, s.Step);
        Assert.NotNull(s.Result);
        Assert.Equal(VictoryType.Civilian, s.Result.Victory);
        Assert.Equal(Scoring.Civilian(s), s.Result);
        Assert.Empty(s.LegalMoves());
        Assert.Throws<InvalidOperationException>(() => s.Apply(new FirstPlayerReveal(0)));
        Assert.Contains("Result:", s.ToString());
        Assert.True(MoveLog.Parse(game.WriteLog()).State.SameAs(s));
    }

    // ---------------------------------------------------------------- coins when built

    /// <summary>
    /// Opens Age III with <paramref name="left"/> and <paramref name="right"/> in the two accessible slots (6.0, 6.1),
    /// then lets the chooser start. Returns the game at the starter's first take-card decision.
    /// </summary>
    static Game AgeThreeWith(string left, string right)
    {
        var game = StartOfAgeThree();
        var s = game.State;
        var faceUp = s.Layout.Slots.Where(x => x.FaceUp).Select(x => x.Index).ToArray();
        int[] bottom = [18, 19];
        string[] chosen = [left, right];
        var guilds = new Queue<int>(CardSets.Guilds.Cards().Where(c => !chosen.Contains(GameData.Cards[c].Id)));
        var ageCards = new Queue<int>(CardSets.OfAge[3].Cards().Where(c => !chosen.Contains(GameData.Cards[c].Id)));

        var guildSlots = bottom.Where((_, i) => GameData.Cards[C(chosen[i])].IsGuild).ToList();
        foreach (var slot in faceUp.Except(bottom))
            if (guildSlots.Count < GameState.GuildsPerGame) guildSlots.Add(slot);
        game.Apply(new GuildPatternReveal([.. guildSlots.Order()]));

        var cards = faceUp.Select(slot =>
            (slot, slot == 18 ? C(left) : slot == 19 ? C(right) : guildSlots.Contains(slot) ? guilds.Dequeue() : ageCards.Dequeue()));
        game.Apply(new SlotReveal(3, true, [.. cards]));
        game.Apply(Move.StartMe);
        return game;
    }

    [Fact]
    public void Lighthouse_pays_one_coin_per_yellow_card_counting_itself_and_merchants_guild_counts_the_richer_city()
    {
        var game = AgeThreeWith("lighthouse", "merchants-guild");
        var s = game.State;
        var me = s.PlayerToDecide;
        var opp = 1 - me;
        foreach (var p in new[] { 0, 1 }) s.SetCoins(p, 20);
        s.AddToCity(me, C("stone-reserve"));
        s.AddToCity(me, C("clay-reserve"));
        s.AddToCity(opp, C("garrison"));

        // Lighthouse (C C G): Clay at 1 each thanks to the Clay Reserve, Glass at 2. Then 3 yellow cards, itself included.
        game.Apply(Move.Build(C("lighthouse")));
        Assert.Equal(20 - 4 + 3, s.Coins(me));

        game.Apply(new RandomChanceSource(3).Next(s)); // 5.0 is now accessible and flips
        // Merchants Guild (C W G P) at 2 each; 1 coin per yellow card in the city with the most: the opponent's 3.
        game.Apply(Move.Build(C("merchants-guild")));
        Assert.Equal(20 - 8 + 3, s.Coins(opp));
    }

    [Theory]
    [InlineData("armory", 2)]              // 1 per red card: garrison, walls
    [InlineData("port", 6)]                // 2 per brown card: lumber-yard, sawmill, clay-pit (cards, not resources)
    [InlineData("chamber-of-commerce", 3)] // 3 per grey card: glassworks
    [InlineData("arena", 0)]               // 2 per Wonder built: none until ticket 07
    public void Age_three_yellow_cards_pay_once_for_the_builders_own_city(string card, int coins)
    {
        var game = AgeThreeWith(card, "obelisk");
        var s = game.State;
        var me = s.PlayerToDecide;
        foreach (var c in new[] { "garrison", "walls", "lumber-yard", "sawmill", "clay-pit", "glassworks" }) s.AddToCity(me, C(c));
        foreach (var c in new[] { "guard-tower", "stable", "palisade", "quarry", "press", "drying-room" }) s.AddToCity(1 - me, C(c));
        s.SetCoins(me, 30);
        var price = Payment.Quote(s, me, C(card)).Total;
        game.Apply(Move.Build(C(card)));
        Assert.Equal(30 - price + coins, s.Coins(me));
    }

    [Theory]
    [InlineData("merchants-guild", 2)]    // yellow: 1 vs 2
    [InlineData("shipowners-guild", 3)]   // brown + grey in one city: 1 + 2 = 3 beats 2 + 0 (separate maxima would wrongly give 4)
    [InlineData("magistrates-guild", 1)]  // blue
    [InlineData("scientists-guild", 2)]   // green
    [InlineData("tacticians-guild", 3)]   // red
    [InlineData("builders-guild", 0)]     // never pays coins
    [InlineData("moneylenders-guild", 0)] // never pays coins
    public void Guild_coins_count_the_city_with_the_most(string guild, int coins)
    {
        var s = GameState.New(Ruleset.Default);
        foreach (var c in new[] { "tavern", "lumber-yard", "glassworks", "press", "theater", "workshop", "garrison" })
            s.AddToCity(0, C(c));
        foreach (var c in new[] { "brewery", "forum", "quarry", "clay-pool", "apothecary", "pharmacist", "stable", "palisade", "walls" })
            s.AddToCity(1, C(c));
        Assert.Equal(coins, Scoring.GuildCoins(s, GameData.Cards[C(guild)].Guild));
    }

    [Fact]
    public void Every_chain_makes_its_card_free_across_ages()
    {
        foreach (var card in GameData.Cards.Where(c => c.FreeIf >= 0))
        {
            var s = GameState.New(Ruleset.Default);
            Assert.True(Payment.Quote(s, 0, card.Index).Total > 0, card.Id);
            s.AddToCity(0, card.FreeIf);
            Assert.Equal(0, Payment.Quote(s, 0, card.Index).Total);
        }
    }
}
