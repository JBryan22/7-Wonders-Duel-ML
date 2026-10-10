using SevenWondersDuel.Engine.Data;

namespace SevenWondersDuel.Engine.Tests;

/// <summary>Card data checks against docs/research/rules-reference.md.</summary>
public class DataTests
{
    [Theory]
    [InlineData(1, 23)]
    [InlineData(2, 23)]
    [InlineData(3, 20)]
    public void Age_decks_have_the_rulebook_card_counts(int age, int count) =>
        Assert.Equal(count, GameData.Cards.Count(c => c.Age == age && !c.IsGuild));

    [Fact]
    public void There_are_73_cards_including_7_guilds()
    {
        Assert.Equal(73, GameData.Cards.Length);
        Assert.Equal(7, GameData.Cards.Count(c => c.IsGuild));
        Assert.All(GameData.Cards.Where(c => c.IsGuild), g => Assert.NotEqual(GuildKind.None, g.Guild));
    }

    [Fact]
    public void Age_three_has_no_brown_or_grey_cards() =>
        Assert.DoesNotContain(GameData.Cards, c => c.Age == 3 && c.Color is CardColor.Brown or CardColor.Grey);

    [Fact]
    public void Every_chain_comes_from_an_earlier_age()
    {
        var chained = GameData.Cards.Where(c => c.FreeIf >= 0).ToList();
        Assert.Equal(17, chained.Count);
        Assert.All(chained, c => Assert.True(GameData.Cards[c.FreeIf].Age < c.Age, c.Id));
        // Each chain symbol is printed on exactly one card.
        Assert.Equal(chained.Count, chained.Select(c => c.FreeIf).Distinct().Count());
    }

    [Fact]
    public void Six_science_symbols_appear_on_exactly_two_green_cards_each_and_law_on_none()
    {
        var bySymbol = GameData.Cards.Where(c => c.Science is not null).GroupBy(c => c.Science!.Value).ToList();
        Assert.Equal(6, bySymbol.Count);
        Assert.All(bySymbol, g => Assert.Equal(2, g.Count()));
        Assert.DoesNotContain(bySymbol, g => g.Key == ScienceSymbol.Law);
        Assert.All(GameData.Cards.Where(c => c.Science is not null), c => Assert.Equal(CardColor.Green, c.Color));
        Assert.Equal(ScienceSymbol.Law, GameData.Tokens[GameData.TokenIndex("law")].Science);
    }

    [Fact]
    public void Costs_parse_coins_and_repeated_resources()
    {
        var caravansery = GameData.Cards[GameData.CardIndex("caravansery")];
        Assert.Equal(2, caravansery.Cost.Coins);
        Assert.Equal([0, 0, 0, 1, 1], caravansery.Cost.Resources);
        Assert.Equal([2, 0, 0, 0, 0], GameData.Cards[GameData.CardIndex("sawmill")].Produce);
        Assert.Equal(8, GameData.Cards[GameData.CardIndex("pretorium")].Cost.Coins);
    }

    [Fact]
    public void Wonders_are_12_base_plus_2_promos()
    {
        Assert.Equal(14, GameData.Wonders.Length);
        Assert.Equal(["messe", "statue-of-liberty"], GameData.Wonders.Where(w => w.IsPromo).Select(w => w.Id));
        Assert.Equal(9, GameData.Wonders[GameData.WonderIndex("pyramids")].VictoryPoints);
        Assert.Equal(WonderEffect.DestroyBrown, GameData.Wonders[GameData.WonderIndex("statue-of-zeus")].Effect);
        Assert.Equal(5, GameData.Wonders.Count(w => w.PlayAgain));
    }

    [Fact]
    public void There_are_10_progress_tokens() => Assert.Equal(10, GameData.Tokens.Length);

    [Theory]
    [InlineData(1, new[] { 2, 3, 4, 5, 6 })]
    [InlineData(2, new[] { 6, 5, 4, 3, 2 })]
    [InlineData(3, new[] { 2, 3, 4, 2, 4, 3, 2 })]
    public void Layouts_have_the_rulebook_row_shapes(int age, int[] rows)
    {
        var layout = GameData.Layout(age);
        Assert.Equal(20, layout.SlotCount);
        Assert.Equal(rows, layout.Slots.GroupBy(s => s.Row).Select(g => g.Count()));
        // Rows alternate face up and face down, top row up.
        Assert.All(layout.Slots, s => Assert.Equal(s.Row % 2 == 0, s.FaceUp));
        // Only the bottom row is uncovered at the start.
        Assert.All(layout.Slots, s => Assert.Equal(s.Row == layout.RowCount - 1, s.CoveredBy.Length == 0));
    }

    [Theory]
    [InlineData(1, 0, new[] { 2, 3 })]
    [InlineData(1, 13, new[] { 18, 19 })]
    [InlineData(2, 0, new[] { 6 })]
    [InlineData(2, 12, new[] { 15, 16 })]
    [InlineData(3, 5, new[] { 9 })]
    [InlineData(3, 6, new[] { 9 })]
    [InlineData(3, 10, new[] { 13, 14 })]
    [InlineData(3, 14, new[] { 17 })]
    public void Coverage_matches_the_rules_reference(int age, int slot, int[] coveredBy) =>
        Assert.Equal(coveredBy, GameData.Layout(age).Slots[slot].CoveredBy);

    [Fact]
    public void Slot_names_round_trip()
    {
        foreach (var layout in GameData.Layouts)
            foreach (var s in layout.Slots)
            {
                var parts = layout.SlotName(s.Index).Split('.');
                Assert.Equal(s.Index, layout.SlotAt(int.Parse(parts[0]), int.Parse(parts[1])));
            }
    }

    [Theory]
    [InlineData("Tribunal", "courthouse")]
    [InlineData("Courthouse", "courthouse")]
    [InlineData("Pretorium", "pretorium")]
    [InlineData("Traders Guild", "merchants-guild")]
    [InlineData("Stonehenge", "pyramids")]
    [InlineData("Sagrada Familia", "great-library")]
    [InlineData("The Great Library", "great-library")]
    [InlineData("Glass-Blower", "glass-blower")]
    [InlineData("No Such Card", null)]
    public void Display_names_and_aliases_resolve_to_slugs(string name, string? slug) =>
        Assert.Equal(slug, GameData.SlugForName(name));

    [Fact]
    public void Slugs_are_unique_kebab_case()
    {
        var all = GameData.Cards.Select(c => c.Id).Concat(GameData.Wonders.Select(w => w.Id)).Concat(GameData.Tokens.Select(t => t.Id)).ToList();
        Assert.Equal(all.Count, all.Distinct().Count());
        Assert.All(all, s => Assert.Matches("^[a-z]+(-[a-z]+)*$", s));
    }
}
