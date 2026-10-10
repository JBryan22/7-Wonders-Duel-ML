namespace SevenWondersDuel.Engine;

/// <summary>
/// Our own seeded random generator (xoshiro256**, seeded through SplitMix64), so the same seed gives
/// the same numbers on every OS and .NET version (spec A11, determinism). System.Random makes no such promise.
/// </summary>
public sealed class Rng
{
    ulong _s0, _s1, _s2, _s3;

    public Rng(ulong seed)
    {
        _s0 = SplitMix(ref seed);
        _s1 = SplitMix(ref seed);
        _s2 = SplitMix(ref seed);
        _s3 = SplitMix(ref seed);
    }

    Rng(Rng other) => (_s0, _s1, _s2, _s3) = (other._s0, other._s1, other._s2, other._s3);

    public Rng Clone() => new(this);

    public ulong NextUInt64()
    {
        var result = ulong.RotateLeft(_s1 * 5, 7) * 9;
        var t = _s1 << 17;
        _s2 ^= _s0;
        _s3 ^= _s1;
        _s1 ^= _s2;
        _s0 ^= _s3;
        _s2 ^= t;
        _s3 = ulong.RotateLeft(_s3, 45);
        return result;
    }

    /// <summary>A uniform integer in [0, n), without modulo bias (Lemire's method).</summary>
    public int Next(int n)
    {
        if (n <= 0) throw new ArgumentOutOfRangeException(nameof(n));
        var bound = (ulong)n;
        var threshold = (0 - bound) % bound;
        while (true)
        {
            var high = Math.BigMul(NextUInt64(), bound, out var low);
            if (low >= threshold) return (int)high;
        }
    }

    static ulong SplitMix(ref ulong x)
    {
        var z = x += 0x9E3779B97F4A7C15;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EB;
        return z ^ (z >> 31);
    }
}
