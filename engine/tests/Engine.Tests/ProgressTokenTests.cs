using SevenWondersDuel.Engine.Data;

namespace SevenWondersDuel.Engine.Tests;

/// <summary>
/// The 10 Progress token effects (rules-reference §6). Where an effect shows on a build, the test builds the same
/// card in two copies of one position, with and without the token, and compares.
/// </summary>
public class ProgressTokenTests
{
    static int C(string slug) => GameData.CardIndex(slug);
    static int W(string slug) => GameData.WonderIndex(slug);

    static GameState Empty() => GameState.New(Ruleset.Default);

    static int Progress(GameState s, int player) => Scoring.Score(s, player)[ScoreCategory.Progress];

    /// <summary>Builds <paramref name="card"/> for the player to decide in a copy of the state, with <paramref name="setup"/> applied first.</summary>
    static GameState BuildIn(GameState s, int card, Action<GameState>? setup = null)
    {
        var copy = s.Clone();
        setup?.Invoke(copy);
        copy.Apply(Move.Build(card));
        return copy;
    }

    // ---------------------------------------------------------------- coins and VP

    [Fact]
    public void Agriculture_gives_six_coins_now_and_four_vp()
    {
        var s = Empty();
        s.TakeToken(0, ProgressTokens.Agriculture);
        Assert.Equal(GameState.StartingCoins + 6, s.Coins(0));
        Assert.Equal(GameState.StartingCoins, s.Coins(1));
        Assert.Equal(4, Progress(s, 0));
        Assert.Equal(0, Progress(s, 1));
    }

    [Fact]
    public void Philosophy_is_worth_seven_vp()
    {
        var s = Empty();
        s.TakeToken(1, ProgressTokens.Philosophy);
        Assert.Equal(7, Progress(s, 1));
        Assert.Equal(GameState.StartingCoins, s.Coins(1));
    }

    [Fact]
    public void Mathematics_is_worth_three_vp_per_token_including_itself()
    {
        var s = Empty();
        s.TakeToken(0, ProgressTokens.Mathematics);
        Assert.Equal(3, Progress(s, 0));
        s.TakeToken(0, ProgressTokens.Agriculture);
        s.TakeToken(0, ProgressTokens.Philosophy);
        Assert.Equal(4 + 7 + 3 * 3, Progress(s, 0));
        Assert.Equal(4 + 7 + 3 * 3, Scoring.ProgressPoints(s, 0));
        // The opponent's tokens do not count.
        s.TakeToken(1, ProgressTokens.Strategy);
        Assert.Equal(4 + 7 + 3 * 3, Progress(s, 0));
        Assert.Equal(0, Progress(s, 1));
    }

    [Fact]
    public void Progress_vp_count_toward_the_civilian_total()
    {
        var s = Empty();
        s.TakeToken(0, ProgressTokens.Philosophy);
        Assert.Equal(7 + GameState.StartingCoins / 3, Scoring.Score(s, 0).Total);
    }

    // ---------------------------------------------------------------- Urbanism

    [Fact]
    public void Urbanism_gives_six_coins_now()
    {
        var s = Empty();
        s.TakeToken(0, ProgressTokens.Urbanism);
        Assert.Equal(GameState.StartingCoins + 6, s.Coins(0));
    }

    [Fact]
    public void Urbanism_gives_four_coins_for_each_card_built_free_through_a_chain()
    {
        var (game, card) = ScienceTests.PlayUntilAccessible(c => c.FreeIf >= 0);
        var s = game.State;
        var me = s.PlayerToDecide;
        s.AddToCity(me, GameData.Cards[card].FreeIf);
        Assert.Equal(0, Payment.Quote(s, me, card).Total);

        var without = BuildIn(s, card);
        var with = BuildIn(s, card, x => x.TakeToken(me, ProgressTokens.Urbanism));
        Assert.Equal(without.Coins(me) + 6 + 4, with.Coins(me));
        Assert.Equal(without.Coins(1 - me), with.Coins(1 - me));
    }

    [Fact]
    public void Urbanism_gives_nothing_for_a_card_built_without_a_chain()
    {
        var (game, card) = ScienceTests.PlayUntilAccessible(c => c.Age == 1 && c.Cost.Coins == 0 && c.Cost.ResourceTotal == 0);
        var s = game.State;
        var me = s.PlayerToDecide;
        var without = BuildIn(s, card);
        var with = BuildIn(s, card, x => x.TakeToken(me, ProgressTokens.Urbanism));
        Assert.Equal(without.Coins(me) + 6, with.Coins(me));
    }

    // ---------------------------------------------------------------- Strategy

    [Fact]
    public void Strategy_adds_one_shield_to_red_cards_built_after_it()
    {
        var (game, card) = ScienceTests.PlayUntilAccessible(c => c.Color == CardColor.Red);
        var s = game.State;
        var me = s.PlayerToDecide;
        var shields = GameData.Cards[card].Shields;
        var without = BuildIn(s, card);
        var with = BuildIn(s, card, x => x.TakeToken(me, ProgressTokens.Strategy));
        Assert.Equal(shields, without.MilitaryLead(me));
        Assert.Equal(shields + 1, with.MilitaryLead(me));
        // The opponent's Strategy does nothing for this player.
        Assert.Equal(shields, BuildIn(s, card, x => x.TakeToken(1 - me, ProgressTokens.Strategy)).MilitaryLead(me));
    }

    [Fact]
    public void Strategy_does_not_move_the_pawn_for_red_cards_already_built()
    {
        var s = Empty();
        s.AddToCity(0, C("garrison"));
        s.TakeToken(0, ProgressTokens.Strategy);
        Assert.Equal(0, s.ConflictPawn);
    }

    [Fact]
    public void Strategy_does_not_touch_cards_that_are_not_red()
    {
        var (game, card) = ScienceTests.PlayUntilAccessible(c => c.Color != CardColor.Red && c.Shields == 0 && c.Age == 1);
        var s = game.State;
        var me = s.PlayerToDecide;
        Assert.Equal(0, BuildIn(s, card, x => x.TakeToken(me, ProgressTokens.Strategy)).ConflictPawn);
    }

    // ---------------------------------------------------------------- Economy

    [Fact]
    public void Economy_sends_the_opponents_trading_coins_to_its_owner()
    {
        var (game, card) = ScienceTests.PlayUntilAccessible(c => c.Age == 1 && c.Cost.ResourceTotal > 0);
        var s = game.State;
        var me = s.PlayerToDecide;
        var opp = 1 - me;
        var quote = Payment.Quote(s, me, card);
        Assert.True(quote.TradeCoins > 0);

        var without = BuildIn(s, card);
        var with = BuildIn(s, card, x => x.TakeToken(opp, ProgressTokens.Economy));
        Assert.Equal(without.Coins(me), with.Coins(me)); // the buyer pays the same
        Assert.Equal(s.Coins(opp), without.Coins(opp));
        Assert.Equal(without.Coins(opp) + quote.TradeCoins, with.Coins(opp));
    }

    [Fact]
    public void Economy_does_not_pass_on_coin_costs_printed_on_cards()
    {
        var (game, card) = ScienceTests.PlayUntilAccessible(c => c.Age == 1 && c.Cost.Coins > 0 && c.Cost.ResourceTotal == 0);
        var s = game.State;
        var me = s.PlayerToDecide;
        var opp = 1 - me;
        var with = BuildIn(s, card, x => x.TakeToken(opp, ProgressTokens.Economy));
        Assert.Equal(s.Coins(opp), with.Coins(opp));
        Assert.Equal(s.Coins(me) - GameData.Cards[card].Cost.Coins + GameData.Cards[card].Coins, with.Coins(me));
    }

    [Fact]
    public void Economy_does_nothing_for_its_owners_own_purchases()
    {
        var (game, card) = ScienceTests.PlayUntilAccessible(c => c.Age == 1 && c.Cost.ResourceTotal > 0);
        var s = game.State;
        var me = s.PlayerToDecide;
        var without = BuildIn(s, card);
        var with = BuildIn(s, card, x => x.TakeToken(me, ProgressTokens.Economy));
        Assert.Equal(without.Coins(me), with.Coins(me));
        Assert.Equal(without.Coins(1 - me), with.Coins(1 - me));
    }

    // ---------------------------------------------------------------- Masonry and Architecture

    [Fact]
    public void Masonry_waives_the_two_most_expensive_missing_resources_of_a_blue_card()
    {
        var s = Empty();
        s.AddToCity(1, C("glassworks")); // Glass costs P1 3 coins, the rest 2
        // Palace: C S W G G.
        Assert.Equal(new PaymentQuote(0, 2 + 2 + 2 + 3 + 3), Payment.Quote(s, 0, C("palace")));
        s.TakeToken(0, ProgressTokens.Masonry);
        Assert.Equal(new PaymentQuote(0, 2 + 2 + 2), Payment.Quote(s, 0, C("palace")));
        // Baths costs one Stone: free with Masonry.
        Assert.Equal(new PaymentQuote(0, 0), Payment.Quote(s, 0, C("baths")));
        // Not for the opponent.
        Assert.Equal(new PaymentQuote(0, 2 * 4), Payment.Quote(s, 1, C("palace"))); // P2 makes its own Glass
    }

    [Fact]
    public void Masonry_combines_with_choice_production_to_find_the_cheapest_payment()
    {
        var s = Empty();
        s.AddToCity(0, C("forum"));    // Glass or Papyrus
        s.AddToCity(1, C("glassworks"));
        s.AddToCity(1, C("glass-blower"));
        // Palace C S W G G, Glass at 4: the Forum covers one Glass; Masonry waives the other Glass and one 2-coin resource.
        Assert.Equal(2 + 2 + 2 + 4, Payment.Quote(s, 0, C("palace")).TradeCoins);
        s.TakeToken(0, ProgressTokens.Masonry);
        Assert.Equal(2 + 2, Payment.Quote(s, 0, C("palace")).TradeCoins);
    }

    [Fact]
    public void Masonry_does_not_discount_cards_that_are_not_blue()
    {
        var s = Empty();
        var before = Payment.Quote(s, 0, C("academy"));
        s.TakeToken(0, ProgressTokens.Masonry);
        Assert.Equal(before, Payment.Quote(s, 0, C("academy")));
        Assert.Equal(new PaymentQuote(0, 2 * 4), before);
    }

    [Fact]
    public void Masonry_never_makes_production_already_held_cheaper_than_free()
    {
        var s = Empty();
        s.AddToCity(0, C("quarry"));
        s.TakeToken(0, ProgressTokens.Masonry);
        // Obelisk S S G: one Stone produced, one Stone and the Glass waived.
        Assert.Equal(new PaymentQuote(0, 0), Payment.Quote(s, 0, C("obelisk")));
    }

    [Fact]
    public void Architecture_waives_two_resources_of_a_wonder_on_the_most_expensive_ones()
    {
        var s = Empty();
        s.AddToCity(1, C("quarry")); // Stone costs P1 3 coins
        // The Pyramids: P S S S.
        Assert.Equal(new PaymentQuote(0, 2 + 3 * 3), Payment.QuoteWonder(s, 0, W("pyramids")));
        s.TakeToken(0, ProgressTokens.Architecture);
        Assert.Equal(new PaymentQuote(0, 2 + 3), Payment.QuoteWonder(s, 0, W("pyramids")));
        Assert.Equal(new PaymentQuote(0, 2 * 3), Payment.QuoteWonder(s, 1, W("pyramids"))); // P2: own Stone, no discount
    }

    [Fact]
    public void Architecture_does_not_discount_cards()
    {
        var s = Empty();
        var before = Payment.Quote(s, 0, C("palace"));
        s.TakeToken(0, ProgressTokens.Architecture);
        Assert.Equal(before, Payment.Quote(s, 0, C("palace")));
    }

    // ---------------------------------------------------------------- Theology and Law

    [Fact]
    public void Theology_gives_every_future_wonder_play_again_once()
    {
        var s = Empty();
        Assert.False(s.WonderPlaysAgain(0, W("pyramids")));
        Assert.True(s.WonderPlaysAgain(0, W("sphinx")));
        s.TakeToken(0, ProgressTokens.Theology);
        Assert.True(s.WonderPlaysAgain(0, W("pyramids")));
        Assert.True(s.WonderPlaysAgain(0, W("sphinx")));
        Assert.False(s.WonderPlaysAgain(1, W("pyramids")));
    }

    [Fact]
    public void Law_gives_its_symbol_and_no_coins_or_vp()
    {
        var s = Empty();
        s.TakeToken(0, ProgressTokens.Law);
        Assert.Equal(1, s.SymbolCount(0, ScienceSymbol.Law));
        Assert.Equal(1, s.DistinctSymbols(0));
        Assert.Equal(GameState.StartingCoins, s.Coins(0));
        Assert.Equal(0, Progress(s, 0));
    }
}
