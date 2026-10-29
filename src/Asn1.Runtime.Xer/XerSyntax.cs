using System.Collections.Frozen;
using System.Globalization;
using System.Text;

namespace Asn1.Runtime.Xer;

/// <summary>Lexical helpers for the XML value notation used by XER (X.680 12.9, 12.15, 14.2, Table 4).</summary>
internal static class XerSyntax
{
    /// <summary>X.680 Table 3: control characters represented by empty-element tags in an "xmlcstring".</summary>
    private static readonly string?[] s_controlNames =
    [
        "nul", "soh", "stx", "etx", "eot", "enq", "ack", "bel", "bs", null, null, "vt", "ff", null, "so", "si",
        "dle", "dc1", "dc2", "dc3", "dc4", "nak", "syn", "etb", "can", "em", "sub", "esc", "is4", "is3", "is2", "is1",
    ];

    private static readonly FrozenDictionary<string, int> s_controlByName =
        s_controlNames.Select((n, i) => (n, i)).Where(x => x.n is not null).ToFrozenDictionary(x => x.n!, x => x.i, StringComparer.Ordinal);

    /// <summary>The code point of a control-character element name, or -1.</summary>
    public static int ControlCharacter(string name) => s_controlByName.TryGetValue(name, out var c) ? c : -1;

    /// <summary>
    /// The "NonParameterizedTypeName" of a descriptor (X.680 14.2): the type reference without its module, or the
    /// "xmlasn1typename" of a built-in type (Table 4), with "_" prepended to names beginning with "XML".
    /// </summary>
    public static string TypeName(Asn1Type type)
    {
        var name = type.Name;
        var dot = name.LastIndexOf('.');
        name = dot >= 0 ? name[(dot + 1)..] : name.Replace(' ', '_').Replace('-', '_');
        return name.StartsWith("XML", StringComparison.OrdinalIgnoreCase) ? "_" + name : name;
    }

    /// <summary>
    /// Appends an "xmlcstring" (12.15) with the required escapes and control-character elements. XML processors
    /// normalise CARRIAGE RETURN to LINE FEED (X.680 12.15.3 note 2); BASIC-XER preserves it with the character
    /// reference "&#13;" (12.15.8), which CANONICAL-XER does not allow (X.693 9.1.3).
    /// </summary>
    public static void AppendCharacters(StringBuilder sb, string value, bool escapeCarriageReturn, Func<string, Exception> error)
    {
        foreach (var rune in value.EnumerateRunes())
        {
            var c = rune.Value;
            switch (c)
            {
                case '&':
                    sb.Append("&amp;");
                    break;
                case '<':
                    sb.Append("&lt;");
                    break;
                case '>':
                    sb.Append("&gt;");
                    break;
                case 13 when escapeCarriageReturn:
                    sb.Append("&#13;");
                    break;
                case 9 or 10 or 13:
                    sb.Append((char)c);
                    break;
                case < 32:
                    sb.Append('<').Append(s_controlNames[c]).Append("/>");
                    break;
                case 0xFFFE or 0xFFFF:
                    throw error($"U+{c:X4} cannot be represented in an XML character string (X.680 12.15.1)");
                default:
                    sb.Append(rune.ToString());
                    break;
            }
        }
    }

    /// <summary>The CANONICAL-XER form of a REAL value (X.693 9.2): "0", "-0", or d.dddE[-]n with no trailing zeros.</summary>
    public static string FormatReal(double value)
    {
        if (value == 0)
        {
            return double.IsNegative(value) ? "-0" : "0";
        }

        var text = Math.Abs(value).ToString("R", CultureInfo.InvariantCulture);
        var e = text.IndexOfAny(['E', 'e']);
        var exponent = e < 0 ? 0 : int.Parse(text.AsSpan(e + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        var mantissa = e < 0 ? text : text[..e];
        var point = mantissa.IndexOf('.', StringComparison.Ordinal);
        var integerDigits = point < 0 ? mantissa.Length : point;
        var digits = point < 0 ? mantissa : mantissa.Remove(point, 1);
        var leading = digits.Length - digits.TrimStart('0').Length;
        digits = digits.Trim('0');
        exponent += integerDigits - leading - 1;
        var fraction = digits.Length > 1 ? digits[1..] : "0";
        return (value < 0 ? "-" : string.Empty) + digits[0] + "." + fraction + "E" + exponent.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>True if <paramref name="text"/> is an optionally negative "realnumber" (X.680 12.9).</summary>
    public static bool IsRealNumber(ReadOnlySpan<char> text)
    {
        var i = 0;
        if (i < text.Length && text[i] == '-')
        {
            i++;
        }

        var start = i;
        while (i < text.Length && char.IsAsciiDigit(text[i]))
        {
            i++;
        }

        if (i == start)
        {
            return false;
        }

        if (i < text.Length && text[i] == '.')
        {
            i++;
            while (i < text.Length && char.IsAsciiDigit(text[i]))
            {
                i++;
            }
        }

        if (i < text.Length && text[i] is 'e' or 'E')
        {
            i++;
            if (i < text.Length && text[i] is '+' or '-')
            {
                i++;
            }

            var expStart = i;
            while (i < text.Length && char.IsAsciiDigit(text[i]))
            {
                i++;
            }

            if (i == expStart)
            {
                return false;
            }
        }

        return i == text.Length;
    }

    /// <summary>True if <paramref name="text"/> is an "XMLSignedNumber" (X.680 19.9, 12.8: no leading zeros).</summary>
    public static bool IsSignedNumber(ReadOnlySpan<char> text)
    {
        if (text.StartsWith("-"))
        {
            text = text[1..];
        }

        return text.Length > 0 && !text.ContainsAnyExceptInRange('0', '9') && (text.Length == 1 || text[0] != '0');
    }

    /// <summary>X.693 8.1.4: the white-space characters.</summary>
    public const string WhiteSpace = " \t\n\r";

    /// <summary>X.693 8.1.4: white-space characters.</summary>
    public static bool IsWhiteSpace(char c) => c is ' ' or '\t' or '\n' or '\r';

    public static string TrimWhiteSpace(string s) => s.Trim(' ', '\t', '\n', '\r');

    public static string RemoveWhiteSpace(string s) => string.Concat(s.Where(c => !IsWhiteSpace(c)));

    /// <summary>XML 1.0 2.11 end-of-line handling: CR LF and lone CR become LF.</summary>
    public static string NormalizeLineEnds(string s) => s.Contains('\r', StringComparison.Ordinal) ? s.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n') : s;
}
