namespace Asn1.Runtime.Ber;

/// <summary>A decoded TLV header.</summary>
/// <param name="Tag">The tag.</param>
/// <param name="Constructed">True for the constructed form.</param>
/// <param name="Length">Contents length, or -1 for the indefinite form.</param>
/// <param name="HeaderStart">Offset of the identifier octets.</param>
/// <param name="ContentStart">Offset of the contents.</param>
/// <param name="NonMinimalLength">True if the length used a non-minimal form (invalid in DER/CER).</param>
public readonly record struct TlvHeader(Asn1Tag Tag, bool Constructed, int Length, int HeaderStart, int ContentStart, bool NonMinimalLength)
{
    /// <summary>True for the indefinite length form.</summary>
    public bool IsIndefinite => Length < 0;
}

/// <summary>Low-level, bounds-checked parsing of identifier and length octets (X.690 8.1).</summary>
public static class TlvReader
{
    /// <summary>Reads the tag at the start of an encoding (used for canonical ordering).</summary>
    public static Asn1Tag PeekTag(ReadOnlySpan<byte> data)
    {
        var pos = 0;
        return TryReadHeader(data, ref pos, data.Length, out var header, out _) ? header.Tag : default;
    }

    /// <summary>
    /// Parses a header at <paramref name="pos"/> without reading beyond <paramref name="limit"/>.
    /// Returns false with an error message for malformed or truncated headers.
    /// </summary>
    public static bool TryReadHeader(ReadOnlySpan<byte> data, ref int pos, int limit, out TlvHeader header, out string? error)
    {
        header = default;
        error = null;
        var start = pos;
        if (pos >= limit)
        {
            error = "truncated identifier";
            return false;
        }

        var first = data[pos++];
        var cls = (TagClass)(first >> 6);
        var constructed = (first & 0x20) != 0;
        var number = first & 0x1F;
        if (number == 0x1F)
        {
            long n = 0;
            var count = 0;
            while (true)
            {
                if (pos >= limit)
                {
                    error = "truncated high tag number";
                    return false;
                }

                var b = data[pos++];
                if (count == 0 && b == 0x80)
                {
                    error = "high tag number has a leading 0x80 octet (X.690 8.1.2.4.2)";
                    return false;
                }

                n = (n << 7) | (uint)(b & 0x7F);
                if (++count > 4 || n > int.MaxValue)
                {
                    error = "tag number too large";
                    return false;
                }

                if ((b & 0x80) == 0)
                {
                    break;
                }
            }

            if (n < 31)
            {
                error = $"tag number {n} uses the high tag number form (X.690 8.1.2.4)";
                return false;
            }

            number = (int)n;
        }

        if (pos >= limit)
        {
            error = "truncated length";
            return false;
        }

        var lb = data[pos++];
        int length;
        var nonMinimal = false;
        if (lb < 0x80)
        {
            length = lb;
        }
        else if (lb == 0x80)
        {
            if (!constructed)
            {
                error = "the indefinite length form is only permitted for constructed encodings (X.690 8.1.3.2)";
                return false;
            }

            length = -1;
        }
        else if (lb == 0xFF)
        {
            error = "reserved length octet 0xFF (X.690 8.1.3.5)";
            return false;
        }
        else
        {
            var count = lb & 0x7F;
            if (pos + count > limit)
            {
                error = "truncated long-form length";
                return false;
            }

            long value = 0;
            for (var i = 0; i < count; i++)
            {
                value = (value << 8) | data[pos++];
                if (value > int.MaxValue)
                {
                    error = "length exceeds 2^31-1";
                    return false;
                }
            }

            nonMinimal = value < 0x80 || data[pos - count] == 0;
            length = (int)value;
        }

        if (length >= 0 && (long)pos + length > limit)
        {
            error = $"length {length} exceeds the {limit - pos} remaining octet(s)";
            return false;
        }

        header = new TlvHeader(new Asn1Tag(cls, number), constructed, length, start, pos, nonMinimal);
        return true;
    }
}
