using SevenWondersDuel.Engine.Data;

namespace SevenWondersDuel.Engine.Tests;

/// <summary>Automatic cheapest payment: rules-reference §3 and common mistakes 1 and 3.</summary>
public class PaymentTests
{
    static int C(string slug) => GameData.CardIndex(slug);

    static GameState WithCities(string[] me, string[] opponent)
    {
        var s = GameState.New(Ruleset.Default);
        foreach (var c in me) s.AddToCity(0, C(c));
        foreach (var c in opponent) s.AddToCity(1, C(c));
        return s;
    }

    static int Cost(GameState s, string card) => Payment.Quote(s, 0, C(card)).Total;

    [Fact]
    public void Trading_price_is_two_plus_the_opponents_brown_and_grey_production()
    {
        var s = WithCities([], ["quarry", "stone-pit", "glassworks"]);
        Assert.Equal(4, Payment.TradingPrice(s, 0, Resource.Stone));
        Assert.Equal(3, Payment.TradingPrice(s, 0, Resource.Glass));
        Assert.Equal(2, Payment.TradingPrice(s, 0, Resource.Wood));
        Assert.Equal(4, Cost(s, "baths")); // baths: 1 Stone
    }

    [Fact]
    public void Double_cards_count_twice_toward_the_trading_price()
    {
        var s = WithCities([], ["sawmill"]);
        Assert.Equal(4, Payment.TradingPrice(s, 0, Resource.Wood));
    }

    [Fact]
    public void Opponent_yellow_choice_production_does_not_raise_trading_prices()
    {
        // Caravansery (W/C/S) and Forum (G/P) produce for their owner but never count toward the other side's price.
        var s = WithCities([], ["caravansery", "forum"]);
        foreach (var r in Enum.GetValues<Resource>()) Assert.Equal(2, Payment.TradingPrice(s, 0, r));
    }

    [Fact]
    public void Own_production_is_never_bought_and_my_cards_do_not_change_my_prices()
    {
        var s = WithCities(["quarry"], []);
        Assert.Equal(0, Cost(s, "baths"));
        Assert.Equal(2, Payment.TradingPrice(s, 0, Resource.Stone));
    }

    [Fact]
    public void Fixed_price_cards_set_the_price_to_one()
    {
        var s = WithCities(["stone-reserve"], ["quarry", "stone-pit"]);
        Assert.Equal(1, Payment.TradingPrice(s, 0, Resource.Stone));
        Assert.Equal(1, Cost(s, "baths"));
        Assert.Equal(1, Payment.TradingPrice(WithCities(["customs-house"], ["press"]), 0, Resource.Papyrus));
    }

    [Fact]
    public void Printed_coins_are_added_to_trading_costs()
    {
        // Forum: $3 C. With no Clay that is 3 + 2.
        Assert.Equal(5, Cost(WithCities([], []), "forum"));
        Assert.Equal(new PaymentQuote(3, 2), Payment.Quote(WithCities([], []), 0, C("forum")));
    }

    [Fact]
    public void Rulebook_example_fortifications_costs_five()
    {
        // RB p.9: Bruno builds Fortifications (S S C P) producing 2 Stone; Antoine produces 1 Clay. 3 + 2 = 5.
        var s = WithCities(["quarry", "stone-pit"], ["clay-pool"]);
        Assert.Equal(5, Cost(s, "fortifications"));
    }

    [Fact]
    public void Choice_resource_goes_on_the_most_expensive_missing_resource()
    {
        // Caravansery: $2 G P. Forum gives G or P. Opponent makes 2 Glass, so Glass costs 4 and Papyrus 2:
        // the Forum covers the Glass and the Papyrus is bought for 2, plus the printed $2.
        var s = WithCities(["forum"], ["glassworks", "glass-blower"]);
        Assert.Equal(4, Cost(s, "caravansery"));
    }

    [Fact]
    public void Choice_resources_are_placed_optimally_not_by_a_fixed_order()
    {
        // Need W, C, S, G at prices 2, 4, 2, 3 with units W/C/S and G/P: the first unit belongs on the Clay (4)
        // and the second on the Glass, leaving W + S at 2 each = 4.
        var need = new[] { 1, 1, 1, 1, 0 };
        var price = new[] { 2, 4, 2, 3, 2 };
        Assert.Equal(4, Payment.Cheapest(need, price, [0b00111, 0b11000]));
        // A greedy "first resource in the list" choice would have covered Wood instead and paid 4 + 2 = 6.
        Assert.Equal(new[] { 1, 1, 1, 1, 0 }, need); // the search leaves its input untouched
    }

    [Fact]
    public void A_chain_makes_the_card_free_including_printed_coins()
    {
        // Horse Breeders (C W) chains from Stable.
        var s = WithCities(["stable"], ["clay-pool", "lumber-yard"]);
        Assert.Equal(0, Cost(s, "horse-breeders"));
        Assert.True(Cost(WithCities([], ["clay-pool", "lumber-yard"]), "horse-breeders") > 0);
    }
}
