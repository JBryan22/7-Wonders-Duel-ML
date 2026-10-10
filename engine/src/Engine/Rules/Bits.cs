using System.Collections;
using System.Numerics;

namespace SevenWondersDuel.Engine;

/// <summary>
/// The set bits of a mask up to 128 bits wide, in ascending order. A <c>foreach</c> over it allocates nothing (it uses
/// the struct enumerator); LINQ still works through <see cref="IEnumerable{T}"/>. Card sets, Wonder masks and token
/// masks are iterated this way on the hot paths (ticket 10, <c>7wd bench</c>).
/// </summary>
public readonly struct Bits(UInt128 mask) : IEnumerable<int>
{
    public Enumerator GetEnumerator() => new((ulong)mask, (ulong)(mask >> 64));
    IEnumerator<int> IEnumerable<int>.GetEnumerator() => GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public struct Enumerator(ulong lo, ulong hi) : IEnumerator<int>
    {
        ulong _lo = lo, _hi = hi;

        public int Current { get; private set; } = -1;
        readonly object IEnumerator.Current => Current;

        public bool MoveNext()
        {
            if (_lo != 0)
            {
                Current = BitOperations.TrailingZeroCount(_lo);
                _lo &= _lo - 1;
                return true;
            }
            if (_hi != 0)
            {
                Current = 64 + BitOperations.TrailingZeroCount(_hi);
                _hi &= _hi - 1;
                return true;
            }
            return false;
        }

        public readonly void Reset() => throw new NotSupportedException();
        public readonly void Dispose() { }
    }
}
