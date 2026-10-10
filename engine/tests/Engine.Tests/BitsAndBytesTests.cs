namespace SevenWondersDuel.Engine.Tests;

/// <summary>The allocation-free bit iteration and the canonical state bytes added for ticket 10.</summary>
public class BitsAndBytesTests
{
    [Fact]
    public void Bits_lists_set_bits_in_ascending_order_across_both_halves()
    {
        var set = CardSet.Of([0, 5, 63, 64, 72]);
        Assert.Equal([0, 5, 63, 64, 72], set.Cards().ToArray());
        var seen = new List<int>();
        foreach (var c in set.Cards()) seen.Add(c);
        Assert.Equal([0, 5, 63, 64, 72], seen);
        Assert.Empty(CardSet.Empty.Cards());
        Assert.Equal([1, 3, 11], GameState.WondersIn(0b1000_0000_1010).ToArray());
        Assert.Equal([0, 9], ProgressTokens.In(0b10_0000_0001).ToArray());
    }

    [Fact]
    public void Equal_states_give_equal_bytes_and_a_change_changes_them()
    {
        var s = GameState.New(Ruleset.Default);
        s.Apply(new FirstPlayerReveal(1));
        var copy = s.Clone();
        Assert.Equal(s.ToBytes(), copy.ToBytes());

        copy.SetCoins(0, 8);
        Assert.NotEqual(s.ToBytes(), copy.ToBytes());
        Assert.NotEqual(GameState.New(Ruleset.Default).ToBytes(), GameState.New(new Ruleset(StartChoice.Rule2015)).ToBytes());
    }
}
