using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Asn1.Runtime;

namespace Asn1.Dynamic;

/// <summary>Thrown when a JSON value does not match the ASN.1 type.</summary>
public sealed class Asn1JsonException(string message, string path) : Exception(path.Length == 0 ? message : $"{message} at {path}")
{
    /// <summary>The JSON path of the error.</summary>
    public string JsonPath { get; } = path;
}

/// <summary>
/// The lossless JSON value notation used by the CLI (documented in docs/value-notation-json.md). INTEGERs beyond
/// ±(2^53-1) are written as decimal strings; OCTET STRINGs as hexadecimal strings; BIT STRINGs as
/// <c>{"hex": "...", "length": n}</c>; OBJECT IDENTIFIERs as dotted strings; CHOICE values as single-member objects;
/// absent OPTIONAL components are omitted.
/// </summary>
public static class DynamicJson
{
    private static readonly BigInteger s_maxSafe = (BigInteger.One << 53) - 1;

    private static Asn1Type Unbox(Asn1Type t) => t is IBoxedType b ? Unbox(b.InnerType) : t;

    // ------------------------------------------------------------------ writing

    /// <summary>Serializes a dynamic value to JSON text.</summary>
    public static string Serialize(Asn1Type type, object? value, bool indented = true)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = indented, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            Write(w, type, value);
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>Writes a dynamic value.</summary>
    public static void Write(Utf8JsonWriter w, Asn1Type type, object? value)
    {
        var inner = Unbox(type);
        switch (value)
        {
            case null:
                w.WriteNullValue();
                return;
            case BigInteger b:
                if (BigInteger.Abs(b) <= s_maxSafe)
                {
                    w.WriteNumberValue((long)b);
                }
                else
                {
                    w.WriteStringValue(b.ToString(CultureInfo.InvariantCulture));
                }

                return;
            case bool b:
                w.WriteBooleanValue(b);
                return;
            case double d:
                if (double.IsPositiveInfinity(d))
                {
                    w.WriteStringValue("PLUS-INFINITY");
                }
                else if (double.IsNegativeInfinity(d))
                {
                    w.WriteStringValue("MINUS-INFINITY");
                }
                else if (double.IsNaN(d))
                {
                    w.WriteStringValue("NOT-A-NUMBER");
                }
                else if (d == 0 && double.IsNegative(d))
                {
                    w.WriteStringValue("-0");
                }
                else
                {
                    w.WriteRawValue(d.ToString("R", CultureInfo.InvariantCulture));
                }

                return;
            case string s:
                w.WriteStringValue(s);
                return;
            case byte[] bytes:
                w.WriteStringValue(Convert.ToHexString(bytes));
                return;
            case Asn1BitString bits:
                w.WriteStartObject();
                w.WriteString("hex", Convert.ToHexString(bits.Bytes));
                w.WriteNumber("length", bits.Length);
                w.WriteEndObject();
                return;
            case ObjectIdentifier oid:
                w.WriteStringValue(oid.ToString());
                return;
            case RelativeObjectIdentifier rel:
                w.WriteStringValue(rel.ToString());
                return;
            case Asn1Null:
                w.WriteNullValue();
                return;
            case Asn1Record record:
                {
                    var info = inner as IStructuredTypeInfo;
                    w.WriteStartObject();
                    foreach (var (name, v) in record)
                    {
                        var comp = info?.ComponentInfos.FirstOrDefault(c => c.Name == name);
                        w.WritePropertyName(name);
                        Write(w, comp?.Type ?? type, v);
                    }

                    w.WriteEndObject();
                    return;
                }

            case Asn1Choice choice:
                {
                    w.WriteStartObject();
                    if (choice.Alternative is null)
                    {
                        // An alternative from a later version, kept for relaying: {"encoding", "hex"} plus the PER
                        // extension index that re-encoding with UPER/APER needs.
                        w.WritePropertyName("$unknownExtension");
                        w.WriteStartObject();
                        if (choice.Value is Asn1OpenValue { IsRaw: true } raw)
                        {
                            w.WriteString("encoding", raw.EncodingRule);
                            w.WriteString("hex", Convert.ToHexString(raw.Encoded.Span));
                        }

                        if (choice.UnknownIndex >= 0)
                        {
                            w.WriteNumber("index", choice.UnknownIndex);
                        }

                        w.WriteEndObject();
                    }
                    else
                    {
                        var alt = (inner as IStructuredTypeInfo)?.ComponentInfos.FirstOrDefault(c => c.Name == choice.Alternative);
                        w.WritePropertyName(choice.Alternative);
                        Write(w, alt?.Type ?? type, choice.Value);
                    }

                    w.WriteEndObject();
                    return;
                }

            case List<object?> list:
                {
                    var element = (inner as ICollectionTypeInfo)?.ElementTypeInfo ?? type;
                    w.WriteStartArray();
                    foreach (var e in list)
                    {
                        Write(w, element, e);
                    }

                    w.WriteEndArray();
                    return;
                }

            case Asn1OpenValue open:
                w.WriteStartObject();
                if (open.IsRaw)
                {
                    w.WriteString("encoding", open.EncodingRule);
                    w.WriteString("hex", Convert.ToHexString(open.Encoded.Span));
                }
                else
                {
                    w.WriteString("type", open.Type!.Name);
                    w.WritePropertyName("value");
                    Write(w, open.Type, open.Value);
                }

                w.WriteEndObject();
                return;
            default:
                w.WriteStringValue(value.ToString());
                return;
        }
    }

    // ------------------------------------------------------------------ reading

    /// <summary>Parses JSON text into a dynamic value of <paramref name="type"/>.</summary>
    public static object? Parse(Asn1Type type, string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 256, CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        return Read(type, doc.RootElement, "$");
    }

    /// <summary>Converts a JSON element into a dynamic value of <paramref name="type"/>.</summary>
    public static object? Read(Asn1Type type, JsonElement e, string path)
    {
        var inner = Unbox(type);
        switch (inner)
        {
            case BooleanType:
                return e.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => throw new Asn1JsonException("expected true or false", path),
                };
            case NullType:
                return e.ValueKind == JsonValueKind.Null ? Asn1Null.Value : throw new Asn1JsonException("expected null", path);
            case RealType:
                return ReadReal(e, path);
            case BitStringType bs:
                return ReadBits(bs, e, path);
            case OctetStringType:
                return ReadOctets(e, path);
            case ObjectIdentifierType:
                return ObjectIdentifier.TryParse(ExpectString(e, path), out var oid, out var err) ? oid : throw new Asn1JsonException(err!, path);
            case RelativeOidType:
                try
                {
                    return RelativeObjectIdentifier.Parse(ExpectString(e, path));
                }
                catch (FormatException ex)
                {
                    throw new Asn1JsonException(ex.Message, path);
                }

            case CharacterStringType or TimeType:
                return ExpectString(e, path);
            case OpenType:
                {
                    if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("hex", out var hex))
                    {
                        var rule = e.TryGetProperty("encoding", out var enc) ? enc.GetString() ?? "BER" : "BER";
                        return Asn1OpenValue.FromEncoded(ParseHex(hex.GetString() ?? string.Empty, path), rule);
                    }

                    throw new Asn1JsonException("open type values are written as {\"encoding\": \"BER\", \"hex\": \"...\"}", path);
                }

            default:
                break;
        }

        switch (inner.Kind)
        {
            case Asn1TypeKind.Integer:
                return ReadInteger(e, path);
            case Asn1TypeKind.Enumerated:
                {
                    var name = ExpectString(e, path);
                    if (inner is EnumeratedType<object?> en && en.Enumeration.FindByName(name) is null)
                    {
                        throw new Asn1JsonException($"'{name}' is not an item of {inner.Name}", path);
                    }

                    return name;
                }

            case Asn1TypeKind.Sequence:
            case Asn1TypeKind.Set:
                {
                    if (e.ValueKind != JsonValueKind.Object)
                    {
                        throw new Asn1JsonException($"expected an object for {inner.Name}", path);
                    }

                    var info = (IStructuredTypeInfo)inner;
                    var record = new Asn1Record();
                    foreach (var p in e.EnumerateObject())
                    {
                        var comp = info.ComponentInfos.FirstOrDefault(c => c.Name == p.Name) ?? throw new Asn1JsonException($"'{p.Name}' is not a component of {inner.Name}", path);
                        record[p.Name] = Read(comp.Type, p.Value, path + "." + p.Name);
                    }

                    foreach (var c in info.ComponentInfos)
                    {
                        if (!c.IsOptional && !c.IsExtensionAddition && !record.Contains(c.Name))
                        {
                            throw new Asn1JsonException($"mandatory component '{c.Name}' of {inner.Name} is missing", path);
                        }
                    }

                    return record;
                }

            case Asn1TypeKind.Choice:
                {
                    if (e.ValueKind != JsonValueKind.Object)
                    {
                        throw new Asn1JsonException($"expected a single-member object selecting an alternative of {inner.Name}", path);
                    }

                    var members = e.EnumerateObject().ToList();
                    if (members.Count != 1)
                    {
                        throw new Asn1JsonException($"a CHOICE value must have exactly one member (found {members.Count})", path);
                    }

                    var info = (IStructuredTypeInfo)inner;
                    if (members[0].Name == "$unknownExtension" && info.IsExtensible)
                    {
                        var u = members[0].Value;
                        if (u.ValueKind != JsonValueKind.Object || !u.TryGetProperty("hex", out var unknownHex) || !u.TryGetProperty("encoding", out var unknownRule))
                        {
                            throw new Asn1JsonException("an unknown alternative is written as {\"encoding\": \"UPER\", \"hex\": \"...\", \"index\": n}", path);
                        }

                        var index = u.TryGetProperty("index", out var i) && i.TryGetInt32(out var n) ? n : -1;
                        return new Asn1Choice(null, Asn1OpenValue.FromEncoded(ParseHex(unknownHex.GetString() ?? string.Empty, path), unknownRule.GetString() ?? string.Empty), index);
                    }

                    var alt = info.ComponentInfos.FirstOrDefault(c => c.Name == members[0].Name) ?? throw new Asn1JsonException($"'{members[0].Name}' is not an alternative of {inner.Name}", path);
                    return new Asn1Choice(alt.Name, Read(alt.Type, members[0].Value, path + "." + alt.Name));
                }

            case Asn1TypeKind.SequenceOf:
            case Asn1TypeKind.SetOf:
                {
                    if (e.ValueKind != JsonValueKind.Array)
                    {
                        throw new Asn1JsonException($"expected an array for {inner.Name}", path);
                    }

                    var element = ((ICollectionTypeInfo)inner).ElementTypeInfo;
                    var list = new List<object?>();
                    var i = 0;
                    foreach (var item in e.EnumerateArray())
                    {
                        list.Add(Read(element, item, $"{path}[{i++}]"));
                    }

                    return list;
                }

            default:
                throw new Asn1JsonException($"unsupported type {inner.Name}", path);
        }
    }

    private static string ExpectString(JsonElement e, string path) =>
        e.ValueKind == JsonValueKind.String ? e.GetString()! : throw new Asn1JsonException("expected a string", path);

    private static BigInteger ReadInteger(JsonElement e, string path)
    {
        var text = e.ValueKind switch
        {
            JsonValueKind.Number => e.GetRawText(),
            JsonValueKind.String => e.GetString()!,
            _ => throw new Asn1JsonException("expected an integer (number or decimal string)", path),
        };
        if (!BigInteger.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value))
        {
            throw new Asn1JsonException($"'{text}' is not an integer", path);
        }

        return value;
    }

    private static double ReadReal(JsonElement e, string path)
    {
        if (e.ValueKind == JsonValueKind.Number)
        {
            return double.Parse(e.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        return ExpectString(e, path) switch
        {
            "PLUS-INFINITY" or "INF" => double.PositiveInfinity,
            "MINUS-INFINITY" or "-INF" => double.NegativeInfinity,
            "NOT-A-NUMBER" or "NaN" => double.NaN,
            "-0" => -0.0,
            var s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
            var s => throw new Asn1JsonException($"'{s}' is not a REAL value", path),
        };
    }

    private static byte[] ParseHex(string hex, string path)
    {
        try
        {
            return Convert.FromHexString(hex);
        }
        catch (FormatException)
        {
            throw new Asn1JsonException($"'{hex}' is not a hexadecimal string", path);
        }
    }

    private static byte[] ReadOctets(JsonElement e, string path)
    {
        if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("base64", out var b64))
        {
            try
            {
                return Convert.FromBase64String(b64.GetString() ?? string.Empty);
            }
            catch (FormatException)
            {
                throw new Asn1JsonException("invalid base64", path);
            }
        }

        return ParseHex(ExpectString(e, path), path);
    }

    private static Asn1BitString ReadBits(BitStringType type, JsonElement e, string path)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                {
                    if (!e.TryGetProperty("hex", out var hex) || !e.TryGetProperty("length", out var length) || !length.TryGetInt64(out var bits))
                    {
                        throw new Asn1JsonException("a BIT STRING object needs \"hex\" and \"length\"", path);
                    }

                    var bytes = ParseHex(hex.GetString() ?? string.Empty, path);
                    if (bits < 0 || bits > bytes.Length * 8L || (bytes.Length * 8L) - bits >= 8)
                    {
                        throw new Asn1JsonException($"length {bits} does not match {bytes.Length} hex octet(s)", path);
                    }

                    return new Asn1BitString(bytes, bits);
                }

            case JsonValueKind.Array:
                {
                    var positions = new List<long>();
                    foreach (var n in e.EnumerateArray())
                    {
                        var name = n.GetString();
                        var bit = type.NamedBits.FirstOrDefault(b => b.Name == name);
                        if (bit.Name is null)
                        {
                            throw new Asn1JsonException($"'{name}' is not a named bit of {type.Name}", path);
                        }

                        positions.Add(bit.Value);
                    }

                    return Asn1BitString.FromSetBits(positions);
                }

            case JsonValueKind.String:
                {
                    var s = e.GetString()!;
                    try
                    {
                        if (s.Length >= 3 && s[0] == '\'' && s[^2] == '\'' && s[^1] is 'B' or 'b')
                        {
                            return Asn1BitString.FromBinary(s[1..^2]);
                        }

                        if (s.Length >= 3 && s[0] == '\'' && s[^2] == '\'' && s[^1] is 'H' or 'h')
                        {
                            return Asn1BitString.FromHex(s[1..^2]);
                        }

                        return Asn1BitString.FromBinary(s);
                    }
                    catch (FormatException ex)
                    {
                        throw new Asn1JsonException(ex.Message, path);
                    }
                }

            default:
                throw new Asn1JsonException("expected a BIT STRING ({\"hex\", \"length\"}, named-bit array or '0101'B)", path);
        }
    }
}
