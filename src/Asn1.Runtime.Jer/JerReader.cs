using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Asn1.Runtime.Primitives;

namespace Asn1.Runtime.Jer;

/// <summary>
/// Reader for JER (X.697) without encoding instructions. It walks a parsed JSON value and accepts every sender's
/// option: members in any order (27.3.3), null members for absent optional components (27.3.4), upper- or lower-case
/// hexadecimal, any JSON escapes (7.6.3), and REAL values as numbers, special-value strings or base10Value objects.
/// </summary>
internal sealed class JerReader : Asn1Reader
{
    private JsonElement _current;

    public JerReader(JsonElement root, Asn1DecodingOptions options)
        : base(options) => _current = root;

    /// <summary>Text rules have no bit offsets; errors carry the component path instead.</summary>
    public override long BitPosition => -1;

    /// <summary>38.1: character string types encoded as JSON strings (the others are encoded as hexadecimal octets).</summary>
    internal static bool IsJsonStringKind(CharacterStringKind kind) => kind is
        CharacterStringKind.IA5 or CharacterStringKind.Visible or CharacterStringKind.Numeric or CharacterStringKind.Printable or
        CharacterStringKind.Bmp or CharacterStringKind.Universal or CharacterStringKind.Utf8;

    private JsonElement Expect(JsonValueKind kind, string what)
    {
        if (_current.ValueKind != kind)
        {
            throw Error(Asn1DecodeError.Malformed, $"expected {what}, found a JSON {_current.ValueKind.ToString().ToLowerInvariant()}");
        }

        return _current;
    }

    private string ExpectString(string what) => Expect(JsonValueKind.String, what).GetString()!;

    /// <summary>
    /// The UTF-8 contents of the current JSON string when it has no escapes and only ASCII characters (then each octet is
    /// one character), so it can be used without creating a string; false otherwise.
    /// </summary>
    private bool TryGetSimpleString(out ReadOnlySpan<byte> utf8)
    {
        var raw = JsonMarshal.GetRawUtf8Value(_current);
        utf8 = raw[1..^1];
        foreach (var b in utf8)
        {
            if (b is (byte)'\\' or >= 0x80)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A property name as characters (written to <paramref name="buffer"/> when it has no escapes), or null.</summary>
    private static ReadOnlySpan<char> SimpleName(JsonProperty property, Span<char> buffer, out bool simple)
    {
        var raw = JsonMarshal.GetRawUtf8PropertyName(property);
        simple = raw.Length <= buffer.Length && raw.IndexOf((byte)'\\') < 0;
        return simple ? buffer[..Encoding.UTF8.GetChars(raw, buffer)] : default;
    }

    private byte[] Hex(string what)
    {
        if (!TryGetSimpleString(out var utf8))
        {
            return Hex(_current.GetString()!, what);
        }

        if (utf8.Length % 2 != 0)
        {
            throw Error(Asn1DecodeError.Malformed, $"{what} must have an even number of hexadecimal digits");
        }

        if (utf8.Length / 2 > Options.MaxStringLength)
        {
            throw Error(Asn1DecodeError.LimitExceeded, $"{what} of {utf8.Length / 2} octets exceeds the limit of {Options.MaxStringLength}");
        }

        var bytes = new byte[utf8.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
        {
            var high = HexDigit(utf8[2 * i]);
            var low = HexDigit(utf8[(2 * i) + 1]);
            if ((high | low) < 0)
            {
                throw Error(Asn1DecodeError.Malformed, $"{what} contains characters other than hexadecimal digits");
            }

            bytes[i] = (byte)((high << 4) | low);
        }

        TrackAllocation(bytes.Length);
        return bytes;
    }

    private static int HexDigit(byte c) => c switch
    {
        >= (byte)'0' and <= (byte)'9' => c - '0',
        >= (byte)'A' and <= (byte)'F' => c - 'A' + 10,
        >= (byte)'a' and <= (byte)'f' => c - 'a' + 10,
        _ => -1,
    };

    private byte[] Hex(string text, string what)
    {
        if (text.Length % 2 != 0)
        {
            throw Error(Asn1DecodeError.Malformed, $"{what} must have an even number of hexadecimal digits");
        }

        if (text.Length / 2 > Options.MaxStringLength)
        {
            throw Error(Asn1DecodeError.LimitExceeded, $"{what} of {text.Length / 2} octets exceeds the limit of {Options.MaxStringLength}");
        }

        try
        {
            var bytes = Convert.FromHexString(text);
            TrackAllocation(bytes.Length);
            return bytes;
        }
        catch (FormatException)
        {
            throw Error(Asn1DecodeError.Malformed, $"{what} contains characters other than hexadecimal digits");
        }
    }

    private void RejectContaining(JerTypeInfo info, Asn1Type type)
    {
        if (_current.ValueKind == JsonValueKind.Object && info.HasContentsConstraint && _current.TryGetProperty("containing", out _))
        {
            throw new Asn1UnsupportedException($"JER decoding of the \"containing\" form of {type.Name} (X.697 24.4/25.4) is not implemented");
        }
    }

    // ------------------------------------------------------------------ primitives

    public override bool ReadBoolean(BooleanType type) => _current.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw Error(Asn1DecodeError.Malformed, $"expected true or false, found a JSON {_current.ValueKind.ToString().ToLowerInvariant()}"),
    };

    public override void ReadNull(NullType type) => Expect(JsonValueKind.Null, "null");

    public override bool TryReadInt64(Asn1Type type, out long value, out BigInteger large)
    {
        var raw = JsonMarshal.GetRawUtf8Value(Expect(JsonValueKind.Number, "a JSON number"));
        if (raw.IndexOfAny("."u8 + "eE"u8) < 0 && raw.Length <= Options.MaxIntegerLength * 3
            && long.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value))
        {
            large = default;
            return true;
        }

        // Other numbers get the checks and messages of ReadInteger.
        large = ReadInteger(type);
        return IntegerCodec.TryToInt64(large, out value);
    }

    public override BigInteger ReadInteger(Asn1Type type)
    {
        var raw = Expect(JsonValueKind.Number, "a JSON number").GetRawText();
        if (raw.AsSpan().IndexOfAny(".eE") >= 0)
        {
            throw Error(Asn1DecodeError.Malformed, $"INTEGER '{raw}' has a fractional part or an exponent (X.697 21)");
        }

        if (raw.Length > Options.MaxIntegerLength * 3)
        {
            throw Error(Asn1DecodeError.LimitExceeded, $"INTEGER of {raw.Length} digits exceeds the limit");
        }

        return BigInteger.Parse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
    }

    public override EnumItemInfo ReadEnumerated(Asn1Type type, EnumerationInfo enumeration)
    {
        Expect(JsonValueKind.String, "an enumeration identifier");
        if (TryGetSimpleString(out var utf8) && utf8.Length <= 256)
        {
            Span<char> chars = stackalloc char[utf8.Length];
            Encoding.ASCII.GetChars(utf8, chars);
            if (enumeration.FindByName(chars) is { } item)
            {
                return item;
            }
        }

        var name = _current.GetString()!;
        return enumeration.FindByName(name) ?? (enumeration.IsExtensible
            ? new EnumItemInfo(null, 0, true, -1)
            : throw Error(Asn1DecodeError.Malformed, $"'{name}' is not an identifier of {type.Name}"));
    }

    public override double ReadReal(RealType type)
    {
        switch (_current.ValueKind)
        {
            case JsonValueKind.Number:
                return double.Parse(_current.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture);
            case JsonValueKind.String:
                return _current.GetString() switch
                {
                    "NaN" => double.NaN,
                    "INF" => double.PositiveInfinity,
                    "-INF" => double.NegativeInfinity,
                    "-0" => -0.0,
                    var s => throw Error(Asn1DecodeError.Malformed, $"'{s}' is not a special REAL value (X.697 23.2)"),
                };
            case JsonValueKind.Object:
                {
                    // 23.4: {"base10Value": number}.
                    var members = _current.EnumerateObject().ToList();
                    if (members is [{ Name: "base10Value", Value.ValueKind: JsonValueKind.Number } m])
                    {
                        return double.Parse(m.Value.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture);
                    }

                    throw Error(Asn1DecodeError.Malformed, "a REAL object must have exactly one member \"base10Value\" holding a number (X.697 23.4)");
                }

            default:
                throw Error(Asn1DecodeError.Malformed, $"expected a REAL, found a JSON {_current.ValueKind.ToString().ToLowerInvariant()}");
        }
    }

    public override Asn1BitString ReadBitString(BitStringType type)
    {
        var info = JerTypeInfo.Get(type);
        RejectContaining(info, type);
        if (info.FixedSize is { } fixedSize)
        {
            // 24.2: hexadecimal string padded to a whole number of octets.
            Expect(JsonValueKind.String, "a hexadecimal BIT STRING");
            var bytes = Hex("BIT STRING");
            if (bytes.Length != (fixedSize + 7) / 8)
            {
                throw Error(Asn1DecodeError.Malformed, $"BIT STRING of fixed size {fixedSize} must have {(fixedSize + 7) / 8} octets, found {bytes.Length}");
            }

            return new Asn1BitString(bytes, fixedSize);
        }

        // 24.3: {"value": hex, "length": bits}.
        var obj = Expect(JsonValueKind.Object, "a BIT STRING object with \"value\" and \"length\"");
        JsonElement? value = null, length = null;
        foreach (var m in obj.EnumerateObject())
        {
            switch (m.Name)
            {
                case "value" when value is null:
                    value = m.Value;
                    break;
                case "length" when length is null:
                    length = m.Value;
                    break;
                default:
                    throw Error(Asn1DecodeError.Malformed, $"unexpected or repeated member \"{m.Name}\" in a BIT STRING (X.697 24.3)");
            }
        }

        if (value is not { ValueKind: JsonValueKind.String } v || length is not { ValueKind: JsonValueKind.Number } l || !l.TryGetInt64(out var bits) || bits < 0)
        {
            throw Error(Asn1DecodeError.Malformed, "a BIT STRING object needs a hexadecimal \"value\" and a non-negative integer \"length\" (X.697 24.3)");
        }

        var data = Hex(v.GetString()!, "BIT STRING");
        if (data.Length != (bits + 7) / 8)
        {
            throw Error(Asn1DecodeError.Malformed, $"BIT STRING of {bits} bits must have {(bits + 7) / 8} octets, found {data.Length}");
        }

        return new Asn1BitString(data, bits);
    }

    public override byte[] ReadOctetString(OctetStringType type)
    {
        RejectContaining(JerTypeInfo.Get(type), type);
        Expect(JsonValueKind.String, "a hexadecimal OCTET STRING");
        return Hex("OCTET STRING");
    }

    public override ObjectIdentifier ReadObjectIdentifier(ObjectIdentifierType type)
    {
        var text = ExpectString("an OBJECT IDENTIFIER string");
        return ObjectIdentifier.TryParse(text, out var oid, out var error) ? oid! : throw Error(Asn1DecodeError.Malformed, error!);
    }

    public override RelativeObjectIdentifier ReadRelativeObjectIdentifier(RelativeOidType type)
    {
        var text = ExpectString("a RELATIVE-OID string");
        try
        {
            return RelativeObjectIdentifier.Parse(text);
        }
        catch (FormatException e)
        {
            throw Error(Asn1DecodeError.Malformed, e.Message);
        }
    }

    public override string ReadCharacterString(CharacterStringType type)
    {
        var text = ExpectString("a JSON string");
        if (!IsJsonStringKind(type.StringKind))
        {
            // 38.2: hexadecimal X.690 8.23.5 octets.
            return StringCodec.Decode(type.StringKind, Hex(text, type.Name), out var error) ?? throw Error(Asn1DecodeError.Malformed, error!);
        }

        TrackAllocation(text.Length * 2L);
        return CharacterRepertoire.Validate(type.StringKind, text) is { } invalid ? throw Error(Asn1DecodeError.Malformed, invalid) : text;
    }

    public override string ReadTime(TimeType type) => ExpectString("a time string");

    // ------------------------------------------------------------------ structured

    public override T ReadSequence<T>(SequenceType<T> type)
    {
        var obj = Expect(JsonValueKind.Object, "a JSON object");
        var members = JerTypeInfo.Get(type).Members;
        var comps = type.Components;
        var value = type.CreateInstance();
        Span<bool> seen = comps.Length <= 128 ? stackalloc bool[comps.Length] : new bool[comps.Length];
        Span<bool> present = comps.Length <= 128 ? stackalloc bool[comps.Length] : new bool[comps.Length];
        var byName = members.GetAlternateLookup<ReadOnlySpan<char>>();
        Span<char> nameBuffer = stackalloc char[128];
        foreach (var m in obj.EnumerateObject())
        {
            var name = SimpleName(m, nameBuffer, out var simple);
            if (!(simple ? byName.TryGetValue(name, out var i) : members.TryGetValue(m.Name, out i)))
            {
                if (type.IsExtensible)
                {
                    // An extension addition of a later version of the specification: ignored.
                    continue;
                }

                throw Error(Asn1DecodeError.Malformed, $"\"{m.Name}\" is not a component of {type.Name}");
            }

            if (seen[i])
            {
                throw Error(Asn1DecodeError.Malformed, $"component \"{m.Name}\" appears more than once");
            }

            seen[i] = true;
            if (m.Value.ValueKind == JsonValueKind.Null && comps[i].IsOptional && comps[i].Type.Kind is not (Asn1TypeKind.Null or Asn1TypeKind.OpenType))
            {
                // 27.3.4: null denotes an absent OPTIONAL or DEFAULT component.
                continue;
            }

            var saved = _current;
            _current = m.Value;
            try
            {
                comps[i].Decode(this, value);
            }
            finally
            {
                _current = saved;
            }

            present[i] = true;
        }

        for (var i = 0; i < comps.Length; i++)
        {
            if (present[i])
            {
                continue;
            }

            if (comps[i].Presence == Asn1Presence.Default)
            {
                comps[i].ApplyDefault(value);
            }
            else if (comps[i].Presence == Asn1Presence.Required && !comps[i].IsExtensionAddition)
            {
                throw Error(Asn1DecodeError.Malformed, $"mandatory component \"{comps[i].Name}\" is missing");
            }
        }

        return value;
    }

    public override List<TElement> ReadCollection<TElement>(CollectionType<TElement> type)
    {
        var array = Expect(JsonValueKind.Array, "a JSON array");
        CheckCollectionCount(array.GetArrayLength());
        var list = new List<TElement>(array.GetArrayLength());
        foreach (var e in array.EnumerateArray())
        {
            if ((list.Count & 1023) == 0)
            {
                Options.CancellationToken.ThrowIfCancellationRequested();
            }

            var saved = _current;
            _current = e;
            try
            {
                list.Add(type.DecodeElement(this, list.Count));
            }
            finally
            {
                _current = saved;
            }
        }

        return list;
    }

    public override T ReadChoice<T>(ChoiceType<T> type)
    {
        var obj = Expect(JsonValueKind.Object, "a JSON object with one member");
        var count = 0;
        JsonProperty m = default;
        foreach (var member in obj.EnumerateObject())
        {
            m = member;
            count++;
        }

        if (count != 1)
        {
            throw Error(Asn1DecodeError.Malformed, $"a CHOICE must be an object with exactly one member, found {count} (X.697 31.3)");
        }

        var members = JerTypeInfo.Get(type).Members;
        Span<char> nameBuffer = stackalloc char[128];
        var name = SimpleName(m, nameBuffer, out var simple);
        if (simple ? members.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(name, out var index) : members.TryGetValue(m.Name, out index))
        {
            var alt = type.Alternatives[index];
            var saved = _current;
            _current = m.Value;
            try
            {
                return alt.Decode(this);
            }
            finally
            {
                _current = saved;
            }
        }

        if (!type.IsExtensible)
        {
            throw Error(Asn1DecodeError.Malformed, $"\"{m.Name}\" is not an alternative of {type.Name}");
        }

        if (!type.SupportsUnknownExtensions)
        {
            throw Error(Asn1DecodeError.Unsupported, $"unknown extension alternative \"{m.Name}\" of {type.Name}");
        }

        // Kept as the complete object so that it is relayed unchanged.
        return type.CreateUnknown(-1, Asn1OpenValue.FromEncoded(Encoding.UTF8.GetBytes(obj.GetRawText()), "JER"));
    }

    public override Asn1OpenValue ReadOpenType(OpenType type) => Asn1OpenValue.FromEncoded(Encoding.UTF8.GetBytes(_current.GetRawText()), "JER");
}
