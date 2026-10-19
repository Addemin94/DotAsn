using System.Globalization;
using System.Numerics;
using System.Text;

namespace Asn1.Runtime.Primitives;

/// <summary>
/// REAL content octets (X.690 8.5) with the canonical restrictions of X.690 11.3 (CER/DER), which X.691 and
/// X.696 also reference. Values are modelled as IEEE 754 binary64.
/// </summary>
public static class RealCodec
{
    /// <summary>Encodes canonical content octets: base 2, odd mantissa, scaling factor 0, minimal exponent.</summary>
    public static byte[] EncodeCanonical(double value)
    {
        if (value == 0)
        {
            return double.IsNegative(value) ? [0x43] : [];
        }

        if (double.IsPositiveInfinity(value))
        {
            return [0x40];
        }

        if (double.IsNegativeInfinity(value))
        {
            return [0x41];
        }

        if (double.IsNaN(value))
        {
            return [0x42];
        }

        var bits = BitConverter.DoubleToInt64Bits(value);
        var negative = bits < 0;
        var exponent = (int)((bits >> 52) & 0x7FF);
        var mantissa = bits & 0xFFFFFFFFFFFFFL;
        if (exponent == 0)
        {
            exponent = 1; // subnormal
        }
        else
        {
            mantissa |= 1L << 52;
        }

        exponent -= 1075;
        while ((mantissa & 1) == 0)
        {
            mantissa >>= 1;
            exponent++;
        }

        var expBytes = IntegerCodec.ToTwosComplement(exponent);
        var mantBytes = IntegerCodec.ToUnsigned(mantissa);
        var output = new List<byte>(2 + expBytes.Length + mantBytes.Length);
        byte first = (byte)(0x80 | (negative ? 0x40 : 0));
        if (expBytes.Length <= 3)
        {
            first |= (byte)(expBytes.Length - 1);
            output.Add(first);
        }
        else
        {
            first |= 0x03;
            output.Add(first);
            output.Add((byte)expBytes.Length);
        }

        output.AddRange(expBytes);
        output.AddRange(mantBytes);
        return [.. output];
    }

    /// <summary>
    /// Decodes content octets. With <paramref name="canonical"/> set, only the CER/DER forms are accepted.
    /// Returns null with an error message for malformed input.
    /// </summary>
    public static double? Decode(ReadOnlySpan<byte> content, bool canonical, out string? error)
    {
        error = null;
        if (content.IsEmpty)
        {
            return 0.0;
        }

        var first = content[0];
        if ((first & 0x80) != 0)
        {
            return DecodeBinary(content, canonical, out error);
        }

        if ((first & 0xC0) == 0x40)
        {
            if (content.Length != 1)
            {
                error = "special REAL value must be a single octet";
                return null;
            }

            return first switch
            {
                0x40 => double.PositiveInfinity,
                0x41 => double.NegativeInfinity,
                0x42 => double.NaN,
                0x43 => -0.0,
                _ => Fail(out error, $"reserved special REAL value 0x{first:X2}"),
            };
        }

        // Decimal encoding (ISO 6093 NR1/NR2/NR3).
        var form = first & 0x3F;
        if (form is < 1 or > 3)
        {
            error = $"invalid decimal REAL form {form}";
            return null;
        }

        if (canonical && form != 3)
        {
            error = "CER/DER require the NR3 decimal form";
            return null;
        }

        var text = Encoding.ASCII.GetString(content[1..]).Trim();
        var normalized = text.Replace(',', '.');
        if (!double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
        {
            error = $"invalid decimal REAL '{text}'";
            return null;
        }

        return d;
    }

    private static double? Fail(out string? error, string message)
    {
        error = message;
        return null;
    }

    private static double? DecodeBinary(ReadOnlySpan<byte> content, bool canonical, out string? error)
    {
        error = null;
        var first = content[0];
        var negative = (first & 0x40) != 0;
        var baseBits = (first >> 4) & 0x03;
        var scale = (first >> 2) & 0x03;
        var expFormat = first & 0x03;
        if (baseBits == 3)
        {
            error = "reserved REAL base";
            return null;
        }

        int pos = 1;
        int expLength;
        if (expFormat < 3)
        {
            expLength = expFormat + 1;
        }
        else
        {
            if (content.Length < 2)
            {
                error = "truncated REAL exponent length";
                return null;
            }

            expLength = content[1];
            pos = 2;
            if (expLength == 0)
            {
                error = "zero-length REAL exponent";
                return null;
            }
        }

        if (content.Length < pos + expLength + 1)
        {
            error = "truncated REAL";
            return null;
        }

        var expOctets = content.Slice(pos, expLength);
        var mantOctets = content[(pos + expLength)..];
        if (canonical)
        {
            if (baseBits != 0 || scale != 0)
            {
                error = "CER/DER require base 2 and scaling factor 0";
                return null;
            }

            if (IntegerCodec.IsNonMinimalTwosComplement(expOctets) || (expFormat == 3 && expLength <= 3))
            {
                error = "REAL exponent is not minimally encoded";
                return null;
            }

            if (mantOctets.Length > 1 && mantOctets[0] == 0 || (mantOctets[^1] & 1) == 0)
            {
                error = "REAL mantissa is not normalized to an odd number";
                return null;
            }
        }

        if (expLength > 8)
        {
            error = "REAL exponent too large";
            return null;
        }

        var exponent = (long)IntegerCodec.FromTwosComplement(expOctets);
        var mantissa = IntegerCodec.FromUnsigned(mantOctets);
        var b = baseBits switch { 0 => 1, 1 => 3, _ => 4 };
        var e2 = (exponent * b) + scale;
        var result = ScaleBinary(mantissa, e2);
        return negative ? -result : result;
    }

    private static double ScaleBinary(BigInteger mantissa, long exponent2)
    {
        if (mantissa.IsZero)
        {
            return 0;
        }

        // Normalize the mantissa to at most 64 significant bits before converting to double.
        var shift = (int)Math.Max(0, mantissa.GetBitLength() - 64);
        var m = (double)(mantissa >> shift);
        var e = exponent2 + shift;
        if (e > 2100)
        {
            return double.PositiveInfinity;
        }

        if (e < -2200)
        {
            return 0;
        }

        return Math.ScaleB(m, (int)e);
    }
}
