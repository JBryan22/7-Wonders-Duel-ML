using System.Runtime.InteropServices;
using System.Text;

namespace SevenWondersDuel.Agents;

/// <summary>
/// Writes NumPy <c>.npy</c> files (format version 1.0: little-endian, C order), the arrays of an encoded shard that
/// Python memory-maps (spec A7, "Storage"). The element type picks the dtype: <see cref="Half"/> <c>&lt;f2</c>,
/// <see cref="float"/> <c>&lt;f4</c>, <see cref="long"/> <c>&lt;i8</c>, <see cref="short"/> <c>&lt;i2</c>,
/// <see cref="sbyte"/> <c>|i1</c>. Rows can be streamed (<see cref="Create{T}"/>, <see cref="Writer{T}.Write"/>) when
/// the whole array is too big to hold, as a Generation's float16 inputs are.
/// </summary>
public static class Npy
{
    static readonly byte[] Magic = [0x93, (byte)'N', (byte)'U', (byte)'M', (byte)'P', (byte)'Y', 1, 0];

    public static string Dtype<T>() where T : unmanaged => typeof(T) switch
    {
        var t when t == typeof(Half) => "<f2",
        var t when t == typeof(float) => "<f4",
        var t when t == typeof(long) => "<i8",
        var t when t == typeof(short) => "<i2",
        var t when t == typeof(sbyte) => "|i1",
        var t => throw new NotSupportedException($"No .npy dtype for {t.Name}"),
    };

    /// <summary>The header: magic, version 1.0, length, then the dict padded with spaces to a 64-byte boundary.</summary>
    public static byte[] Header(string dtype, IReadOnlyList<long> shape)
    {
        var dims = shape.Count == 1 ? $"{shape[0]}," : string.Join(", ", shape);
        var dict = $"{{'descr': '{dtype}', 'fortran_order': False, 'shape': ({dims}), }}";
        var length = Magic.Length + 2 + dict.Length + 1;
        var padded = (length + 63) / 64 * 64;
        var text = dict + new string(' ', padded - length) + "\n";
        var header = new byte[Magic.Length + 2 + text.Length];
        Magic.CopyTo(header, 0);
        header[Magic.Length] = (byte)(text.Length & 0xFF);
        header[Magic.Length + 1] = (byte)(text.Length >> 8);
        Encoding.ASCII.GetBytes(text, 0, text.Length, header, Magic.Length + 2);
        return header;
    }

    /// <summary>Saves a whole 1-D array.</summary>
    public static void Save<T>(string path, ReadOnlySpan<T> values) where T : unmanaged
    {
        using var writer = Create<T>(path, values.Length);
        writer.Write(values);
        writer.Complete();
    }

    /// <summary>Starts a file of the given shape; write exactly that many elements, then <see cref="Writer{T}.Complete"/>.</summary>
    public static Writer<T> Create<T>(string path, params long[] shape) where T : unmanaged => new(path, shape);

    public sealed class Writer<T> : IDisposable where T : unmanaged
    {
        readonly FileStream _stream;
        readonly long _expected;
        long _written;

        internal Writer(string path, long[] shape)
        {
            if (!BitConverter.IsLittleEndian) throw new PlatformNotSupportedException("The .npy writer assumes a little-endian machine");
            _expected = shape.Aggregate(1L, (a, b) => a * b);
            _stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
            _stream.Write(Header(Dtype<T>(), shape));
        }

        public void Write(ReadOnlySpan<T> values)
        {
            if (_written + values.Length > _expected) throw new InvalidOperationException("More elements than the shape holds");
            _stream.Write(MemoryMarshal.AsBytes(values));
            _written += values.Length;
        }

        /// <summary>Checks that the shape is full and flushes the file.</summary>
        public void Complete()
        {
            if (_written != _expected) throw new InvalidOperationException($"Wrote {_written} elements, the shape holds {_expected}");
            _stream.Flush(flushToDisk: true);
        }

        public void Dispose() => _stream.Dispose();
    }
}
