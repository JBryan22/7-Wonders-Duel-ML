namespace SevenWondersDuel.Engine.Tests;

public class RngTests
{
    // Pinned values: if these change, every recorded seed replays differently. CI checks them on Linux and Windows.
    static readonly ulong[] PinnedSeed42 = [1546998764402558742, 6990951692964543102, 12544586762248559009];

    [Fact]
    public void The_same_seed_gives_the_same_numbers_on_every_machine()
    {
        var rng = new Rng(42);
        Assert.Equal(PinnedSeed42, Enumerable.Range(0, 3).Select(_ => rng.NextUInt64()).ToArray());
    }

    [Fact]
    public void Next_stays_in_range_and_hits_every_value_about_equally()
    {
        var rng = new Rng(1);
        var seen = new int[7];
        for (var i = 0; i < 7000; i++) seen[rng.Next(7)]++;
        Assert.All(seen, count => Assert.InRange(count, 850, 1150));
    }

    [Fact]
    public void A_clone_continues_the_same_sequence()
    {
        var a = new Rng(5);
        a.NextUInt64();
        var b = a.Clone();
        Assert.Equal(a.NextUInt64(), b.NextUInt64());
    }
}
