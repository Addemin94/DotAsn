using System.Text;

namespace Asn1;

/// <summary>Restricted character string types (X.680 41) plus ObjectDescriptor.</summary>
public enum CharacterStringKind
{
    /// <summary>UTF8String</summary>
    Utf8,

    /// <summary>NumericString</summary>
    Numeric,

    /// <summary>PrintableString</summary>
    Printable,

    /// <summary>TeletexString / T61String</summary>
    Teletex,

    /// <summary>VideotexString</summary>
    Videotex,

    /// <summary>IA5String</summary>
    IA5,

    /// <summary>GraphicString</summary>
    Graphic,

    /// <summary>VisibleString / ISO646String</summary>
    Visible,

    /// <summary>GeneralString</summary>
    General,

    /// <summary>UniversalString</summary>
    Universal,

    /// <summary>BMPString</summary>
    Bmp,

    /// <summary>ObjectDescriptor (a GraphicString subtype)</summary>
    ObjectDescriptor,
}

/// <summary>Character repertoire facts for the restricted character string types.</summary>
public static class CharacterRepertoire
{
    private static readonly int[] s_numeric = [.. " 0123456789".Select(c => (int)c)];
    private static readonly int[] s_printable = [.. " '()+,-./0123456789:=?ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz".Select(c => (int)c).Order()];
    private static readonly int[] s_visible = [.. Enumerable.Range(32, 95)];
    private static readonly int[] s_ia5 = [.. Enumerable.Range(0, 128)];

    /// <summary>The universal tag number of a string kind.</summary>
    public static int UniversalTag(CharacterStringKind kind) => kind switch
    {
        CharacterStringKind.Utf8 => UniversalTagNumber.Utf8String,
        CharacterStringKind.Numeric => UniversalTagNumber.NumericString,
        CharacterStringKind.Printable => UniversalTagNumber.PrintableString,
        CharacterStringKind.Teletex => UniversalTagNumber.TeletexString,
        CharacterStringKind.Videotex => UniversalTagNumber.VideotexString,
        CharacterStringKind.IA5 => UniversalTagNumber.IA5String,
        CharacterStringKind.Graphic => UniversalTagNumber.GraphicString,
        CharacterStringKind.Visible => UniversalTagNumber.VisibleString,
        CharacterStringKind.General => UniversalTagNumber.GeneralString,
        CharacterStringKind.Universal => UniversalTagNumber.UniversalString,
        CharacterStringKind.Bmp => UniversalTagNumber.BmpString,
        CharacterStringKind.ObjectDescriptor => UniversalTagNumber.ObjectDescriptor,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>The ASN.1 keyword of a string kind.</summary>
    public static string Keyword(CharacterStringKind kind) => kind switch
    {
        CharacterStringKind.Utf8 => "UTF8String",
        CharacterStringKind.Numeric => "NumericString",
        CharacterStringKind.Printable => "PrintableString",
        CharacterStringKind.Teletex => "TeletexString",
        CharacterStringKind.Videotex => "VideotexString",
        CharacterStringKind.IA5 => "IA5String",
        CharacterStringKind.Graphic => "GraphicString",
        CharacterStringKind.Visible => "VisibleString",
        CharacterStringKind.General => "GeneralString",
        CharacterStringKind.Universal => "UniversalString",
        CharacterStringKind.Bmp => "BMPString",
        CharacterStringKind.ObjectDescriptor => "ObjectDescriptor",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>
    /// True for the known-multiplier character string types (X.691 3.7.16): each character is encoded
    /// with a fixed number of bits and PER applies size and permitted-alphabet constraints.
    /// </summary>
    public static bool IsKnownMultiplier(CharacterStringKind kind) => kind is
        CharacterStringKind.Numeric or CharacterStringKind.Printable or CharacterStringKind.Visible or
        CharacterStringKind.IA5 or CharacterStringKind.Bmp or CharacterStringKind.Universal;

    /// <summary>
    /// The sorted code points of a small repertoire, or null for large repertoires (BMP, Universal, UTF-8, ISO 2022 types).
    /// </summary>
    public static IReadOnlyList<int>? SmallAlphabet(CharacterStringKind kind) => kind switch
    {
        CharacterStringKind.Numeric => s_numeric,
        CharacterStringKind.Printable => s_printable,
        CharacterStringKind.Visible => s_visible,
        CharacterStringKind.IA5 => s_ia5,
        _ => null,
    };

    /// <summary>Lowest and highest code point permitted by the repertoire.</summary>
    public static (int Min, int Max) CodePointRange(CharacterStringKind kind) => kind switch
    {
        CharacterStringKind.Numeric => (32, 57),
        CharacterStringKind.Printable => (32, 122),
        CharacterStringKind.Visible => (32, 126),
        CharacterStringKind.IA5 => (0, 127),
        CharacterStringKind.Bmp => (0, 0xFFFF),
        CharacterStringKind.Teletex or CharacterStringKind.Videotex or CharacterStringKind.Graphic or CharacterStringKind.General or CharacterStringKind.ObjectDescriptor => (0, 255),
        _ => (0, 0x10FFFF),
    };

    /// <summary>True if <paramref name="codePoint"/> belongs to the repertoire of <paramref name="kind"/>.</summary>
    public static bool IsValid(CharacterStringKind kind, int codePoint)
    {
        var small = SmallAlphabet(kind);
        if (small is not null)
        {
            return ((int[])small).AsSpan().BinarySearch(codePoint) >= 0;
        }

        var (min, max) = CodePointRange(kind);
        if (codePoint < min || codePoint > max)
        {
            return false;
        }

        // Surrogate code points are not characters.
        return codePoint is < 0xD800 or > 0xDFFF;
    }

    /// <summary>Validates that every character of <paramref name="value"/> is permitted; returns an error message or null.</summary>
    public static string? Validate(CharacterStringKind kind, string value)
    {
        var index = 0;
        foreach (var cp in CodePoints(value))
        {
            if (cp < 0)
            {
                return $"invalid UTF-16 sequence at character {index}";
            }

            if (!IsValid(kind, cp))
            {
                return $"character U+{cp:X4} at position {index} is not permitted in {Keyword(kind)}";
            }

            index++;
        }

        return null;
    }

    /// <summary>Enumerates Unicode code points; lone surrogates yield -1.</summary>
    public static IEnumerable<int> CodePoints(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                {
                    yield return char.ConvertToUtf32(c, value[i + 1]);
                    i++;
                    continue;
                }

                yield return -1;
                continue;
            }

            if (char.IsLowSurrogate(c))
            {
                yield return -1;
                continue;
            }

            yield return c;
        }
    }

    /// <summary>Number of abstract characters (code points), as used by SIZE constraints.</summary>
    public static int CountCharacters(string value)
    {
        var n = 0;
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsHighSurrogate(value[i]) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                i++;
            }

            n++;
        }

        return n;
    }

    /// <summary>Builds a string from code points.</summary>
    public static string FromCodePoints(IEnumerable<int> codePoints)
    {
        var sb = new StringBuilder();
        foreach (var cp in codePoints)
        {
            sb.Append(char.ConvertFromUtf32(cp));
        }

        return sb.ToString();
    }
}
