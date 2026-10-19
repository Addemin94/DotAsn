using System.Globalization;
using System.Numerics;
using System.Text.Json;
using Asn1.Runtime.Primitives;

namespace Asn1.Runtime.Jer;

/// <summary>Writer for JER (X.697) without encoding instructions.</summary>
internal sealed class JerWriter : Asn1Writer
{
    private readonly Utf8JsonWriter _json;

    public JerWriter(Utf8JsonWriter json, Asn1EncodingOptions options)
        : base(options) => _json = json;

    // ------------------------------------------------------------------ primitives

    public override void WriteBoolean(BooleanType type, bool value) => _json.WriteBooleanValue(value);

    public override void WriteNull(NullType type) => _json.WriteNullValue();

    /// <summary>21: a JSON number with no fractional part and no exponent.</summary>
    public override void WriteInteger(Asn1Type type, BigInteger value) => _json.WriteRawValue(value.ToString(CultureInfo.InvariantCulture), skipInputValidation: true);

    public override void WriteInteger(Asn1Type type, long value) => _json.WriteNumberValue(value);

    public override void WriteEnumerated(Asn1Type type, EnumerationInfo enumeration, EnumItemInfo item)
    {
        if (item.IsUnknown)
        {
            throw Error($"an unknown extension item of {type.Name} cannot be encoded");
        }

        // 22: the identifier as a JSON string.
        _json.WriteStringValue(item.Name);
    }

    public override void WriteReal(RealType type, double value)
    {
        // 23.2: special values as strings; 23.3: every other (base-2) value as a JSON number.
        if (double.IsNaN(value))
        {
            _json.WriteStringValue("NaN");
        }
        else if (double.IsPositiveInfinity(value))
        {
            _json.WriteStringValue("INF");
        }
        else if (double.IsNegativeInfinity(value))
        {
            _json.WriteStringValue("-INF");
        }
        else if (value == 0 && double.IsNegative(value))
        {
            _json.WriteStringValue("-0");
        }
        else
        {
            _json.WriteRawValue(value.ToString("R", CultureInfo.InvariantCulture), skipInputValidation: true);
        }
    }

    public override void WriteBitString(BitStringType type, Asn1BitString value)
    {
        var info = JerTypeInfo.Get(type);
        if (type.HasNamedBits)
        {
            // 24.2.2 (and X.680 22.7): trailing zero bits are removed, then added up to the effective lower bound.
            value = value.TrimTrailingZeros();
            if (info.Sizes.LowerBound is { } lb && value.Length < lb)
            {
                value = value.WithLength((long)lb);
            }
        }

        if (info.FixedSize is { } fixedSize)
        {
            if (value.Length != fixedSize)
            {
                throw Error($"BIT STRING of {value.Length} bits does not have the fixed size {fixedSize} of {type.Name}");
            }

            _json.WriteStringValue(Convert.ToHexString(value.Bytes));
            return;
        }

        // 24.3: {"value": hex, "length": bits}.
        _json.WriteStartObject();
        _json.WriteString("value", Convert.ToHexString(value.Bytes));
        _json.WriteNumber("length", value.Length);
        _json.WriteEndObject();
    }

    /// <summary>25.3: hexadecimal JSON string.</summary>
    public override void WriteOctetString(OctetStringType type, ReadOnlySpan<byte> value) => _json.WriteStringValue(Convert.ToHexString(value));

    /// <summary>32: XMLObjectIdentifierValue (dotted numbers).</summary>
    public override void WriteObjectIdentifier(ObjectIdentifierType type, ObjectIdentifier value) => _json.WriteStringValue(value.ToString());

    /// <summary>33: XMLRelativeOIDValue (dotted numbers).</summary>
    public override void WriteRelativeObjectIdentifier(RelativeOidType type, RelativeObjectIdentifier value) => _json.WriteStringValue(value.ToString());

    public override void WriteCharacterString(CharacterStringType type, string value)
    {
        if (JerReader.IsJsonStringKind(type.StringKind))
        {
            // 38.1: the character string itself.
            _json.WriteStringValue(value);
            return;
        }

        // 38.2 (and 7.4.5 for ObjectDescriptor): as an OCTET STRING of the X.690 8.23.5 octets.
        _json.WriteStringValue(Convert.ToHexString(StringCodec.Encode(type.StringKind, value)));
    }

    /// <summary>40 and 7.4.5: the time value as a JSON string.</summary>
    public override void WriteTime(TimeType type, string value) => _json.WriteStringValue(value);

    // ------------------------------------------------------------------ structured

    public override void WriteSequence<T>(SequenceType<T> type, T value)
    {
        // 27.3: an object with one member per present component, in textual order. DEFAULT values are omitted.
        _json.WriteStartObject();
        foreach (var c in type.Components)
        {
            if (!c.IsPresent(value))
            {
                if (c.IsOptional || c.IsExtensionAddition)
                {
                    continue;
                }

                throw Error($"mandatory component '{c.Name}' is absent");
            }

            if (c.Presence == Asn1Presence.Default && c.HasDefaultValue(value))
            {
                continue;
            }

            _json.WritePropertyName(c.Name);
            c.Encode(this, value);
        }

        _json.WriteEndObject();
    }

    /// <summary>28, 30.2: a JSON array.</summary>
    public override void WriteCollection<TElement>(CollectionType<TElement> type, IReadOnlyList<TElement> value)
    {
        _json.WriteStartArray();
        for (var i = 0; i < value.Count; i++)
        {
            type.EncodeElement(this, i, value[i]);
        }

        _json.WriteEndArray();
    }

    public override void WriteChoice<T>(ChoiceType<T> type, T value)
    {
        var index = type.GetIndex(value);
        if (index < 0)
        {
            var unknown = type.GetUnknown(value) ?? throw Error($"no alternative of {type.Name} is selected");
            if (unknown.Value.EncodingRule != "JER")
            {
                throw Error($"an unknown alternative decoded with {unknown.Value.EncodingRule} cannot be re-encoded with JER");
            }

            // JerReader keeps an unknown alternative as its complete object {"name": value}.
            _json.WriteRawValue(unknown.Value.Encoded.Span, skipInputValidation: false);
            return;
        }

        // 31.3: an object with exactly one member named after the chosen alternative.
        var alt = type.Alternatives[index];
        _json.WriteStartObject();
        _json.WritePropertyName(alt.Name);
        alt.Encode(this, value);
        _json.WriteEndObject();
    }

    public override void WriteOpenType(OpenType type, Asn1OpenValue value)
    {
        // 41: the encoding of the contained value.
        if (!value.IsRaw)
        {
            value.Type!.EncodeBoxed(this, value.Value);
            return;
        }

        if (value.EncodingRule != "JER")
        {
            throw Error($"raw {value.EncodingRule} contents cannot be embedded in a JER encoding");
        }

        _json.WriteRawValue(value.Encoded.Span, skipInputValidation: false);
    }
}
