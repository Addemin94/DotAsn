using System.Numerics;

namespace Asn1.Runtime.Buffers;

/// <summary>
/// A growable most-significant-bit-first bit writer for bit-oriented encodings (PER). The writer owns its
/// buffer; <see cref="ToArray"/> copies the written bits padded with zero bits to a whole octet.
/// </summary>
public sealed class BitWriter
{
    private byte[] _buffer;
    private long _bitPosition;
    private readonly long _maxBytes;

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

    private void Ensure(long bits)
    {
        var neededBytes = (_bitPosition + bits + 7) >> 3;
        if (neededBytes > _maxBytes)
        {
            throw new Asn1EncodeException($"Encoding exceeds the maximum size of {_maxBytes} bytes");
        }

        if (neededBytes > _buffer.Length)
        {
            var size = (long)_buffer.Length;
            while (size < neededBytes)
            {
                size *= 2;
            }

            Array.Resize(ref _buffer, (int)Math.Min(size, Array.MaxLength));
        }
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
        if (count is < 0 or > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        Ensure(count);
        for (var i = count - 1; i >= 0; i--)
        {
            if (((value >> i) & 1) != 0)
            {
                _buffer[_bitPosition >> 3] |= (byte)(0x80 >> (int)(_bitPosition & 7));
            }

            _bitPosition++;
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
        Ensure(bytes.Length * 8L);
        if (IsAligned)
        {
            bytes.CopyTo(_buffer.AsSpan((int)(_bitPosition >> 3)));
            _bitPosition += bytes.Length * 8L;
            return;
        }

        foreach (var b in bytes)
        {
            WriteBits(b, 8);
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
    public byte[] ToArray() => _buffer.AsSpan(0, (int)((_bitPosition + 7) >> 3)).ToArray();

    /// <summary>The written octets without copying (the final octet may be partial).</summary>
    public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, (int)((_bitPosition + 7) >> 3));
}

/// <summary>
/// A bounds-checked most-significant-bit-first bit reader. Every read validates the remaining length first and
/// reports truncation through <see cref="Asn1DecodeException"/>.
/// </summary>
public sealed class BitReader
{
    private readonly ReadOnlyMemory<byte> _data;
    private readonly long _bitLength;
    private long _bitPosition;

    /// <summary>Creates a reader over all bits of <paramref name="data"/>.</summary>
    public BitReader(ReadOnlyMemory<byte> data, long bitLength = -1)
    {
        _data = data;
        _bitLength = bitLength < 0 ? data.Length * 8L : Math.Min(bitLength, data.Length * 8L);
    }

    /// <summary>The current bit position.</summary>
    public long BitPosition => _bitPosition;

    /// <summary>Number of unread bits.</summary>
    public long RemainingBits => _bitLength - _bitPosition;

    /// <summary>True when on an octet boundary.</summary>
    public bool IsAligned => (_bitPosition & 7) == 0;

    /// <summary>The underlying data.</summary>
    public ReadOnlyMemory<byte> Data => _data;

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
        var bit = (_data.Span[(int)(_bitPosition >> 3)] & (0x80 >> (int)(_bitPosition & 7))) != 0;
        _bitPosition++;
        return bit;
    }

    /// <summary>Reads <paramref name="count"/> (≤ 64) bits as an unsigned integer.</summary>
    public ulong ReadBits(int count)
    {
        if (count is < 0 or > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        Require(count);
        var span = _data.Span;
        ulong value = 0;
        for (var i = 0; i < count; i++)
        {
            value = (value << 1) | (uint)((span[(int)(_bitPosition >> 3)] >> (7 - (int)(_bitPosition & 7))) & 1);
            _bitPosition++;
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
        if (IsAligned)
        {
            _data.Span.Slice((int)(_bitPosition >> 3), (int)count).CopyTo(result);
            _bitPosition += count * 8;
            return result;
        }

        for (var i = 0; i < count; i++)
        {
            result[i] = (byte)ReadBits(8);
        }

        return result;
    }

    /// <summary>Reads <paramref name="bitCount"/> bits into a zero-padded byte array.</summary>
    public byte[] ReadBitString(long bitCount)
    {
        Require(bitCount);
        var result = new byte[(bitCount + 7) >> 3];
        var whole = bitCount >> 3;
        if (IsAligned)
        {
            _data.Span.Slice((int)(_bitPosition >> 3), (int)whole).CopyTo(result);
            _bitPosition += whole * 8;
        }
        else
        {
            for (var i = 0; i < whole; i++)
            {
                result[i] = (byte)ReadBits(8);
            }
        }

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
