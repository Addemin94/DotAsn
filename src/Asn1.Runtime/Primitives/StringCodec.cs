using System.Text;

namespace Asn1.Runtime.Primitives;

/// <summary>
/// Octet encodings of restricted character strings used by BER/DER/CER and OER: UTF-8 for UTF8String,
/// ASCII for the 7-bit types, UCS-2 (big-endian) for BMPString, UCS-4 (big-endian) for UniversalString, and
/// one octet per character (ISO 8859-1 mapping, no ISO 2022 escape processing) for Teletex/Videotex/Graphic/General.
/// </summary>
public static class StringCodec
{
    private static readonly UTF8Encoding s_strictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Encodes a validated string.</summary>
    public static byte[] Encode(CharacterStringKind kind, string value)
    {
        switch (kind)
        {
            case CharacterStringKind.Utf8:
                return s_strictUtf8.GetBytes(value);
            case CharacterStringKind.Bmp:
                {
                    var bytes = new byte[value.Length * 2];
                    for (var i = 0; i < value.Length; i++)
                    {
                        bytes[2 * i] = (byte)(value[i] >> 8);
                        bytes[(2 * i) + 1] = (byte)value[i];
                    }

                    return bytes;
                }

            case CharacterStringKind.Universal:
                {
                    var cps = CharacterRepertoire.CodePoints(value).ToArray();
                    var bytes = new byte[cps.Length * 4];
                    for (var i = 0; i < cps.Length; i++)
                    {
                        bytes[4 * i] = (byte)(cps[i] >> 24);
                        bytes[(4 * i) + 1] = (byte)(cps[i] >> 16);
                        bytes[(4 * i) + 2] = (byte)(cps[i] >> 8);
                        bytes[(4 * i) + 3] = (byte)cps[i];
                    }

                    return bytes;
                }

            default:
                {
                    var bytes = new byte[value.Length];
                    for (var i = 0; i < value.Length; i++)
                    {
                        bytes[i] = value[i] <= 0xFF ? (byte)value[i] : throw new Asn1EncodeException($"character U+{(int)value[i]:X4} cannot be encoded in {CharacterRepertoire.Keyword(kind)}");
                    }

                    return bytes;
                }
        }
    }

    /// <summary>Decodes content octets; returns null and an error for malformed input.</summary>
    public static string? Decode(CharacterStringKind kind, ReadOnlySpan<byte> content, out string? error)
    {
        error = null;
        switch (kind)
        {
            case CharacterStringKind.Utf8:
                try
                {
                    return s_strictUtf8.GetString(content);
                }
                catch (DecoderFallbackException)
                {
                    error = "invalid UTF-8";
                    return null;
                }

            case CharacterStringKind.Bmp:
                {
                    if (content.Length % 2 != 0)
                    {
                        error = "BMPString length is not a multiple of 2";
                        return null;
                    }

                    var chars = new char[content.Length / 2];
                    for (var i = 0; i < chars.Length; i++)
                    {
                        chars[i] = (char)((content[2 * i] << 8) | content[(2 * i) + 1]);
                    }

                    return new string(chars);
                }

            case CharacterStringKind.Universal:
                {
                    if (content.Length % 4 != 0)
                    {
                        error = "UniversalString length is not a multiple of 4";
                        return null;
                    }

                    var sb = new StringBuilder(content.Length / 4);
                    for (var i = 0; i < content.Length; i += 4)
                    {
                        var cp = (content[i] << 24) | (content[i + 1] << 16) | (content[i + 2] << 8) | content[i + 3];
                        if (cp is < 0 or > 0x10FFFF || (cp >= 0xD800 && cp <= 0xDFFF))
                        {
                            error = $"invalid UniversalString character 0x{cp:X8}";
                            return null;
                        }

                        sb.Append(char.ConvertFromUtf32(cp));
                    }

                    return sb.ToString();
                }

            default:
                {
                    var chars = new char[content.Length];
                    for (var i = 0; i < content.Length; i++)
                    {
                        chars[i] = (char)content[i];
                    }

                    return new string(chars);
                }
        }
    }
}
