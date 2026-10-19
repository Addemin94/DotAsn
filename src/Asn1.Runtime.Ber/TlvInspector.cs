using System.Globalization;
using System.Text;
using Asn1.Runtime.Primitives;

namespace Asn1.Runtime.Ber;

/// <summary>Renders a schema-less, indented dump of BER/CER/DER encodings (like <c>openssl asn1parse</c>).</summary>
public static class TlvInspector
{
    private static readonly string[] s_universalNames =
    [
        "EOC", "BOOLEAN", "INTEGER", "BIT STRING", "OCTET STRING", "NULL", "OBJECT IDENTIFIER", "ObjectDescriptor",
        "EXTERNAL", "REAL", "ENUMERATED", "EMBEDDED PDV", "UTF8String", "RELATIVE-OID", "TIME", "[UNIVERSAL 15]",
        "SEQUENCE", "SET", "NumericString", "PrintableString", "TeletexString", "VideotexString", "IA5String",
        "UTCTime", "GeneralizedTime", "GraphicString", "VisibleString", "GeneralString", "UniversalString",
        "CHARACTER STRING", "BMPString", "DATE", "TIME-OF-DAY", "DATE-TIME", "DURATION", "OID-IRI", "RELATIVE-OID-IRI",
    ];

    /// <summary>Dumps every top-level element; malformed input is reported inline rather than thrown.</summary>
    public static string Dump(ReadOnlySpan<byte> data, int maxDepth = 64)
    {
        var sb = new StringBuilder();
        var pos = 0;
        while (pos < data.Length)
        {
            if (!DumpElement(data, ref pos, data.Length, 0, maxDepth, sb))
            {
                break;
            }
        }

        return sb.ToString();
    }

    private static bool DumpElement(ReadOnlySpan<byte> data, ref int pos, int limit, int depth, int maxDepth, StringBuilder sb)
    {
        var start = pos;
        if (!TlvReader.TryReadHeader(data, ref pos, limit, out var h, out var error))
        {
            sb.Append(Indent(depth)).Append($"{start,6}: ERROR {error}\n");
            return false;
        }

        var name = h.Tag.Class == TagClass.Universal && h.Tag.Number < s_universalNames.Length ? s_universalNames[h.Tag.Number] : h.Tag.ToString();
        sb.Append(CultureInfo.InvariantCulture, $"{start,6}: {Indent(depth)}{name} ({(h.Constructed ? "cons" : "prim")}) len={(h.IsIndefinite ? "indefinite" : h.Length.ToString(CultureInfo.InvariantCulture))}");
        if (!h.Constructed)
        {
            var content = data.Slice(h.ContentStart, h.Length);
            sb.Append(": ").Append(Describe(h.Tag, content));
            sb.Append('\n');
            pos = h.ContentStart + h.Length;
            return true;
        }

        sb.Append('\n');
        if (depth >= maxDepth)
        {
            sb.Append(Indent(depth + 1)).Append("... (maximum depth reached)\n");
            pos = h.IsIndefinite ? limit : h.ContentStart + h.Length;
            return true;
        }

        var end = h.IsIndefinite ? limit : h.ContentStart + h.Length;
        while (pos < end)
        {
            if (h.IsIndefinite && pos + 1 < end && data[pos] == 0 && data[pos + 1] == 0)
            {
                sb.Append(CultureInfo.InvariantCulture, $"{pos,6}: {Indent(depth + 1)}EOC\n");
                pos += 2;
                return true;
            }

            if (!DumpElement(data, ref pos, end, depth + 1, maxDepth, sb))
            {
                return false;
            }
        }

        return true;
    }

    private static string Indent(int depth) => new(' ', depth * 2);

    private static string Describe(Asn1Tag tag, ReadOnlySpan<byte> content)
    {
        if (tag.Class != TagClass.Universal)
        {
            return Hex(content);
        }

        switch (tag.Number)
        {
            case UniversalTagNumber.Boolean when content.Length == 1:
                return content[0] == 0 ? "FALSE" : "TRUE";
            case UniversalTagNumber.Integer or UniversalTagNumber.Enumerated when content.Length is > 0 and <= 64:
                return IntegerCodec.FromTwosComplement(content).ToString(CultureInfo.InvariantCulture);
            case UniversalTagNumber.ObjectIdentifier:
                return ObjectIdentifierCodec.Decode(content, out var e)?.ToString() ?? "invalid: " + e;
            case UniversalTagNumber.Null:
                return content.IsEmpty ? "NULL" : "invalid NULL";
            case UniversalTagNumber.BitString when content.Length > 0:
                return $"unused={content[0]} {Hex(content[1..])}";
            case UniversalTagNumber.Utf8String or UniversalTagNumber.PrintableString or UniversalTagNumber.IA5String or UniversalTagNumber.VisibleString or UniversalTagNumber.NumericString or UniversalTagNumber.UtcTime or UniversalTagNumber.GeneralizedTime:
                return "\"" + Encoding.UTF8.GetString(content) + "\"";
            case UniversalTagNumber.Real:
                return RealCodec.Decode(content, false, out _)?.ToString("R", CultureInfo.InvariantCulture) ?? Hex(content);
            default:
                return Hex(content);
        }
    }

    private static string Hex(ReadOnlySpan<byte> content) =>
        content.Length <= 64 ? Convert.ToHexString(content) : Convert.ToHexString(content[..64]) + $"... ({content.Length} octets)";
}
