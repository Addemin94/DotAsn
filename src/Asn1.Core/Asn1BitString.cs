using System.Text;

namespace Asn1;

/// <summary>
/// An immutable BIT STRING value with an exact bit length. Bit 0 is the most significant bit of the first
/// byte (X.680 22.?, X.690 8.6). Unused trailing bits of the last byte are always zero.
/// </summary>
public sealed class Asn1BitString : IEquatable<Asn1BitString>
{
    private readonly byte[] _bytes;

    /// <summary>The empty bit string.</summary>
    public static readonly Asn1BitString Empty = new([], 0);

    /// <summary>Creates a bit string from bytes and an exact bit length.</summary>
    /// <param name="bytes">Packed bits, most significant bit first. Copied.</param>
    /// <param name="length">Number of significant bits.</param>
    public Asn1BitString(ReadOnlySpan<byte> bytes, long length)
    {
        if (length < 0 || length > (long)bytes.Length * 8)
        {
            throw new ArgumentOutOfRangeException(nameof(length), $"Bit length {length} does not fit in {bytes.Length} bytes.");
        }

        var byteCount = (int)((length + 7) / 8);
        _bytes = bytes[..byteCount].ToArray();
        Length = length;
        var unused = (int)(byteCount * 8 - length);
        if (unused > 0)
        {
            _bytes[^1] &= (byte)(0xFF << unused);
        }
    }

    /// <summary>Creates a bit string from whole bytes.</summary>
    public Asn1BitString(ReadOnlySpan<byte> bytes) : this(bytes, (long)bytes.Length * 8)
    {
    }

    /// <summary>Number of bits.</summary>
    public long Length { get; }

    /// <summary>The packed bytes (copy-free view). Unused trailing bits are zero.</summary>
    public ReadOnlySpan<byte> Bytes => _bytes;

    /// <summary>Number of unused bits in the final byte (0..7).</summary>
    public int UnusedBits => (int)((8 - (Length % 8)) % 8);

    /// <summary>Gets the bit at <paramref name="index"/>.</summary>
    public bool this[long index]
    {
        get
        {
            if (index < 0 || index >= Length)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            return (_bytes[index >> 3] & (0x80 >> (int)(index & 7))) != 0;
        }
    }

    /// <summary>Returns a copy of the packed bytes.</summary>
    public byte[] ToArray() => (byte[])_bytes.Clone();

    /// <summary>Parses a binary digit string such as <c>"0101"</c>.</summary>
    public static Asn1BitString FromBinary(string digits)
    {
        var bytes = new byte[(digits.Length + 7) / 8];
        for (var i = 0; i < digits.Length; i++)
        {
            switch (digits[i])
            {
                case '1':
                    bytes[i >> 3] |= (byte)(0x80 >> (i & 7));
                    break;
                case '0':
                    break;
                default:
                    throw new FormatException($"Invalid binary digit '{digits[i]}'.");
            }
        }

        return new Asn1BitString(bytes, digits.Length);
    }

    /// <summary>Parses hexadecimal digits; each digit contributes four bits.</summary>
    public static Asn1BitString FromHex(string hex)
    {
        var bytes = new byte[(hex.Length + 1) / 2];
        for (var i = 0; i < hex.Length; i++)
        {
            var v = Convert.ToByte(hex[i].ToString(), 16);
            bytes[i / 2] |= (byte)((i % 2 == 0) ? v << 4 : v);
        }

        return new Asn1BitString(bytes, hex.Length * 4L);
    }

    /// <summary>Creates a bit string with the given bit positions set and the minimal length that includes the highest set bit (X.680 22.7).</summary>
    public static Asn1BitString FromSetBits(IEnumerable<long> positions, long minimumLength = 0)
    {
        var list = positions.ToList();
        var length = Math.Max(minimumLength, list.Count == 0 ? 0 : list.Max() + 1);
        var bytes = new byte[(length + 7) / 8];
        foreach (var p in list)
        {
            bytes[p >> 3] |= (byte)(0x80 >> (int)(p & 7));
        }

        return new Asn1BitString(bytes, length);
    }

    /// <summary>Returns the bit string with trailing zero bits removed (used for named-bit canonical forms, X.690 11.2.2).</summary>
    public Asn1BitString TrimTrailingZeros()
    {
        var len = Length;
        while (len > 0 && !this[len - 1])
        {
            len--;
        }

        return len == Length ? this : new Asn1BitString(_bytes, len);
    }

    /// <summary>Returns a copy resized to <paramref name="length"/> bits, padding with zeros.</summary>
    public Asn1BitString WithLength(long length)
    {
        var bytes = new byte[(length + 7) / 8];
        _bytes.AsSpan(0, Math.Min(_bytes.Length, bytes.Length)).CopyTo(bytes);
        return new Asn1BitString(bytes, length);
    }

    /// <summary>Formats as binary digits.</summary>
    public string ToBinaryString()
    {
        var sb = new StringBuilder((int)Math.Min(Length, int.MaxValue));
        for (long i = 0; i < Length; i++)
        {
            sb.Append(this[i] ? '1' : '0');
        }

        return sb.ToString();
    }

    /// <inheritdoc />
    public bool Equals(Asn1BitString? other) =>
        other is not null && other.Length == Length && _bytes.AsSpan().SequenceEqual(other._bytes);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as Asn1BitString);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var h = new HashCode();
        h.Add(Length);
        h.AddBytes(_bytes);
        return h.ToHashCode();
    }

    /// <summary>Equality operator.</summary>
    public static bool operator ==(Asn1BitString? a, Asn1BitString? b) => a is null ? b is null : a.Equals(b);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(Asn1BitString? a, Asn1BitString? b) => !(a == b);

    /// <inheritdoc />
    public override string ToString() => $"'{ToBinaryString()}'B";
}
