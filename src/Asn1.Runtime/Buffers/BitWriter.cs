using System.Numerics;
using System.Runtime.InteropServices;

namespace Asn1.Runtime.Buffers;

/// <summary>
/// A growable most-significant-bit-first bit writer for bit-oriented encodings (PER). The writer owns its
/// buffer; <see cref="ToArray"/> copies the written bits padded with zero bits to a whole octet.
/// </summary>
public sealed class BitWriter
{
    // Invariant: every bit at or after _bitPosition is zero, so writes can OR into the current octet.
    private byte[] _buffer;
    private long _bitPosition;
    private long _maxBytes;

    /// <summary>Creates a writer.</summary>
    public BitWriter(int initialCapacity = 64, long maxBytes = long.MaxValue)
    {
        _buffer = new byte[Math.Max(1, initialCapacity)];
        _maxBytes = maxBytes;
    }

    /// <summary>Number of bits written.</summary>
    public long BitPosition => _bitPosition;

    /// <summary>True when the position is on an octet boundary.</summary>
    public bool IsAligned => (_bitPosition & 7) == 0;

    /// <summary>The size of the buffer in octets.</summary>
    public int Capacity => _buffer.Length;

    /// <summary>Clears the written bits so the writer (and its buffer) can be reused.</summary>
    public void Reset(long maxBytes = long.MaxValue)
    {
        _buffer.AsSpan(0, (int)((_bitPosition + 7) >> 3)).Clear();
        _bitPosition = 0;
        _maxBytes = maxBytes;
    }

    private void Ensure(long bits)
    {
        var neededBytes = (_bitPosition + bits + 7) >> 3;
        if (neededBytes > _buffer.Length || neededBytes > _maxBytes)
        {
            Grow(neededBytes);
        }
    }

    private void Grow(long neededBytes)
    {
        if (neededBytes > _maxBytes)
        {
            throw new Asn1EncodeException($"Encoding exceeds the maximum size of {_maxBytes} bytes");
        }

        var size = (long)_buffer.Length;
        while (size < neededBytes)
        {
            size *= 2;
        }

        Array.Resize(ref _buffer, (int)Math.Min(size, Array.MaxLength));
    }

    /// <summary>Writes one bit.</summary>
    public void WriteBit(bool bit)
    {
        Ensure(1);
        if (bit)
        {
            _buffer[_bitPosition >> 3] |= (byte)(0x80 >> (int)(_bitPosition & 7));
        }

        _bitPosition++;
    }

    /// <summary>Writes the low <paramref name="count"/> bits of <paramref name="value"/>, most significant first (count ≤ 64).</summary>
    public void WriteBits(ulong value, int count)
    {
        if ((uint)count > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        if (count == 0)
        {
            return;
        }

        Ensure(count);
        if (count < 64)
        {
            value &= (1UL << count) - 1;
        }

        var buffer = _buffer;
        var index = (int)(_bitPosition >> 3);
        var free = 8 - (int)(_bitPosition & 7);
        _bitPosition += count;
        if (count <= free)
        {
            buffer[index] |= (byte)(value << (free - count));
            return;
        }

        var remaining = count - free;
        buffer[index++] |= (byte)(value >> remaining);
        while (remaining >= 8)
        {
            remaining -= 8;
            buffer[index++] = (byte)(value >> remaining);
        }

        if (remaining > 0)
        {
            buffer[index] = (byte)(value << (8 - remaining));
        }
    }

    /// <summary>Writes a non-negative integer in exactly <paramref name="count"/> bits.</summary>
    public void WriteBits(BigInteger value, int count)
    {
        if (value.Sign < 0 || value.GetBitLength() > count)
        {
            throw new ArgumentOutOfRangeException(nameof(value), $"{value} does not fit in {count} bits");
        }

        if (count <= 64)
        {
            WriteBits((ulong)value, count);
            return;
        }

        Ensure(count);
        for (var i = count - 1; i >= 0; i--)
        {
            WriteBit(!((value >> i) & 1).IsZero);
        }
    }

    /// <summary>Writes whole octets (at the current, possibly unaligned, position).</summary>
    public void WriteBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return;
        }

        Ensure(bytes.Length * 8L);
        var index = (int)(_bitPosition >> 3);
        var shift = (int)(_bitPosition & 7);
        _bitPosition += bytes.Length * 8L;
        if (shift == 0)
        {
            bytes.CopyTo(_buffer.AsSpan(index));
            return;
        }

        var buffer = _buffer;
        foreach (var b in bytes)
        {
            buffer[index] |= (byte)(b >> shift);
            buffer[++index] = (byte)(b << (8 - shift));
        }
    }

    /// <summary>Writes the first <paramref name="bitCount"/> bits of <paramref name="bytes"/>.</summary>
    public void WriteBitString(ReadOnlySpan<byte> bytes, long bitCount)
    {
        var whole = (int)(bitCount >> 3);
        WriteBytes(bytes[..whole]);
        var rest = (int)(bitCount & 7);
        if (rest > 0)
        {
            WriteBits((ulong)(bytes[whole] >> (8 - rest)), rest);
        }
    }

    /// <summary>Writes zero bits up to the next octet boundary.</summary>
    public void Align()
    {
        var pad = (int)((8 - (_bitPosition & 7)) & 7);
        if (pad > 0)
        {
            Ensure(pad);
            _bitPosition += pad;
        }
    }

    /// <summary>Returns the written bits as octets, padding the final octet with zero bits.</summary>
    public byte[] ToArray() => WrittenSpan.ToArray();

    /// <summary>The written octets without copying (the final octet may be partial).</summary>
    public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, (int)((_bitPosition + 7) >> 3));
}

/// <summary>
/// A bounds-checked most-significant-bit-first bit reader. Every read validates the remaining length first and
/// reports truncation through <see cref="Asn1DecodeException"/>.
/// </summary>
public sealed class BitReader
{
    private ReadOnlyMemory<byte> _data;
    private byte[]? _array;
    private int _offset;
    private long _bitLength;
    private long _bitPosition;

    /// <summary>Creates a reader over all bits of <paramref name="data"/>.</summary>
    public BitReader(ReadOnlyMemory<byte> data, long bitLength = -1) => Reset(data, bitLength);

    /// <summary>The current bit position.</summary>
    public long BitPosition => _bitPosition;

    /// <summary>Number of unread bits.</summary>
    public long RemainingBits => _bitLength - _bitPosition;

    /// <summary>True when on an octet boundary.</summary>
    public bool IsAligned => (_bitPosition & 7) == 0;

    /// <summary>The underlying data.</summary>
    public ReadOnlyMemory<byte> Data => _data;

    // A span over an array is cheaper to create than ReadOnlyMemory.Span, which is read for every operation.
    private ReadOnlySpan<byte> Span => _array is not null ? new ReadOnlySpan<byte>(_array, _offset, _data.Length) : _data.Span;

    /// <summary>Starts reading other data from its first bit (so the reader can be reused).</summary>
    public void Reset(ReadOnlyMemory<byte> data, long bitLength = -1)
    {
        _data = data;
        if (MemoryMarshal.TryGetArray(data, out var segment))
        {
            _array = segment.Array;
            _offset = segment.Offset;
        }
        else
        {
            _array = null;
            _offset = 0;
        }

        _bitLength = bitLength < 0 ? data.Length * 8L : Math.Min(bitLength, data.Length * 8L);
        _bitPosition = 0;
    }

    /// <summary>Moves to an earlier or later bit position within the data.</summary>
    public void Seek(long bitPosition)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bitPosition);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bitPosition, _bitLength);
        _bitPosition = bitPosition;
    }

    private void Require(long bits)
    {
        if (bits < 0 || bits > RemainingBits)
        {
            throw new Asn1DecodeException(Asn1DecodeError.Truncated, $"Need {bits} more bit(s) but only {RemainingBits} remain", _bitPosition);
        }
    }

    /// <summary>Reads one bit.</summary>
    public bool ReadBit()
    {
        Require(1);
        var bit = (Span[(int)(_bitPosition >> 3)] & (0x80 >> (int)(_bitPosition & 7))) != 0;
        _bitPosition++;
        return bit;
    }

    /// <summary>Reads <paramref name="count"/> (≤ 64) bits as an unsigned integer.</summary>
    public ulong ReadBits(int count)
    {
        if ((uint)count > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        Require(count);
        if (count == 0)
        {
            return 0;
        }

        var span = Span;
        var index = (int)(_bitPosition >> 3);
        var used = (int)(_bitPosition & 7);
        _bitPosition += count;
        var remaining = count;
        ulong value = 0;
        if (used != 0)
        {
            var available = 8 - used;
            var take = Math.Min(available, remaining);
            value = (uint)((span[index] >> (available - take)) & ((1 << take) - 1));
            remaining -= take;
            index++;
        }

        while (remaining >= 8)
        {
            value = (value << 8) | span[index++];
            remaining -= 8;
        }

        if (remaining > 0)
        {
            value = (value << remaining) | (uint)(span[index] >> (8 - remaining));
        }

        return value;
    }

    /// <summary>Reads <paramref name="count"/> bits as a non-negative big integer.</summary>
    public BigInteger ReadBigBits(int count)
    {
        if (count <= 64)
        {
            return ReadBits(count);
        }

        Require(count);
        var bytes = ReadBitString(count);
        var value = new BigInteger(bytes, isUnsigned: true, isBigEndian: true);
        var pad = (8 - (count & 7)) & 7;
        return value >> pad;
    }

    /// <summary>Reads whole octets.</summary>
    public byte[] ReadBytes(long count)
    {
        Require(count * 8);
        var result = new byte[count];
        CopyBytes(result);
        return result;
    }

    /// <summary>Reads <paramref name="destination"/>.Length whole octets into <paramref name="destination"/>.</summary>
    public void ReadBytes(Span<byte> destination)
    {
        Require(destination.Length * 8L);
        CopyBytes(destination);
    }

    private void CopyBytes(Span<byte> destination)
    {
        var span = Span;
        var index = (int)(_bitPosition >> 3);
        var shift = (int)(_bitPosition & 7);
        _bitPosition += destination.Length * 8L;
        if (shift == 0)
        {
            span.Slice(index, destination.Length).CopyTo(destination);
            return;
        }

        for (var i = 0; i < destination.Length; i++, index++)
        {
            destination[i] = (byte)((span[index] << shift) | (span[index + 1] >> (8 - shift)));
        }
    }

    /// <summary>Reads <paramref name="bitCount"/> bits into a zero-padded byte array.</summary>
    public byte[] ReadBitString(long bitCount)
    {
        Require(bitCount);
        var result = new byte[(bitCount + 7) >> 3];
        var whole = (int)(bitCount >> 3);
        CopyBytes(result.AsSpan(0, whole));
        var rest = (int)(bitCount & 7);
        if (rest > 0)
        {
            result[whole] = (byte)(ReadBits(rest) << (8 - rest));
        }

        return result;
    }

    /// <summary>Skips to the next octet boundary.</summary>
    public void Align()
    {
        var pad = (8 - (_bitPosition & 7)) & 7;
        Require(pad);
        _bitPosition += pad;
    }

    /// <summary>Skips bits.</summary>
    public void Skip(long bits)
    {
        Require(bits);
        _bitPosition += bits;
    }
}
