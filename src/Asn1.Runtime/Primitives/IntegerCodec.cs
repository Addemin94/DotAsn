using System.Numerics;

namespace Asn1.Runtime.Primitives;

/// <summary>
/// Integer content encodings shared by several rules: minimal two's complement (X.690 8.3, X.691 10.4,
/// X.696 10) and minimal non-negative binary (X.691 10.3, X.696 10).
/// </summary>
public static class IntegerCodec
{
    /// <summary>Number of octets of the minimal two's complement encoding of <paramref name="value"/>.</summary>
    public static int TwosComplementLength(BigInteger value) => value.GetByteCount(isUnsigned: false);

    /// <summary>Minimal two's complement octets, big-endian.</summary>
    public static byte[] ToTwosComplement(BigInteger value) => value.ToByteArray(isUnsigned: false, isBigEndian: true);

    /// <summary>Minimal two's complement octets of a 64-bit value, big-endian.</summary>
    public static int WriteTwosComplement(long value, Span<byte> destination)
    {
        var length = TwosComplementLength(value);
        for (var i = length - 1; i >= 0; i--)
        {
            destination[i] = (byte)value;
            value >>= 8;
        }

        return length;
    }

    /// <summary>Minimal two's complement length of a 64-bit value.</summary>
    public static int TwosComplementLength(long value)
    {
        var length = 1;
        while (length < 8 && (value < -(1L << ((length * 8) - 1)) || value >= 1L << ((length * 8) - 1)))
        {
            length++;
        }

        return length;
    }

    /// <summary>Parses two's complement octets, big-endian.</summary>
    public static BigInteger FromTwosComplement(ReadOnlySpan<byte> octets) =>
        octets.IsEmpty ? BigInteger.Zero : new BigInteger(octets, isUnsigned: false, isBigEndian: true);

    /// <summary>True if the encoding is not minimal (X.690 8.3.2: the first nine bits must not be all zeros or all ones).</summary>
    public static bool IsNonMinimalTwosComplement(ReadOnlySpan<byte> octets) =>
        octets.Length > 1 && ((octets[0] == 0x00 && (octets[1] & 0x80) == 0) || (octets[0] == 0xFF && (octets[1] & 0x80) != 0));

    /// <summary>Number of octets of the minimal non-negative binary integer encoding (at least one octet).</summary>
    public static int UnsignedLength(BigInteger value) => value.IsZero ? 1 : value.GetByteCount(isUnsigned: true);

    /// <summary>Number of octets of the minimal non-negative binary integer encoding of a 64-bit value (at least one octet).</summary>
    public static int UnsignedLength(ulong value) => value == 0 ? 1 : (71 - BitOperations.LeadingZeroCount(value)) / 8;

    /// <summary>Converts <paramref name="value"/> to a <see cref="long"/> when it fits in 64 bits.</summary>
    public static bool TryToInt64(BigInteger value, out long result)
    {
        // GetBitLength excludes the sign bit, so values of up to 63 bits fit (long.MinValue included).
        if (value.GetBitLength() < 64)
        {
            result = (long)value;
            return true;
        }

        result = 0;
        return false;
    }

    /// <summary>Minimal non-negative binary integer octets, big-endian (at least one octet).</summary>
    public static byte[] ToUnsigned(BigInteger value)
    {
        if (value.Sign < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "value must be non-negative");
        }

        return value.IsZero ? [0] : value.ToByteArray(isUnsigned: true, isBigEndian: true);
    }

    /// <summary>Parses non-negative binary integer octets, big-endian.</summary>
    public static BigInteger FromUnsigned(ReadOnlySpan<byte> octets) =>
        octets.IsEmpty ? BigInteger.Zero : new BigInteger(octets, isUnsigned: true, isBigEndian: true);

    /// <summary>Number of bits needed to represent values 0..<paramref name="range"/>-1 (i.e. ceil(log2(range))).</summary>
    public static int BitsForRange(BigInteger range)
    {
        if (range <= 1)
        {
            return 0;
        }

        return (int)(range - 1).GetBitLength();
    }
}
