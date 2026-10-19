using System.Globalization;
using System.Numerics;
using System.Text;
using Asn1.Constraints;

namespace Asn1.Runtime.Xer;

/// <summary>
/// Writer for XER. It produces the CANONICAL-XER form (X.693 clause 9), which is also a valid BASIC-XER encoding: no
/// prolog, no white-space, empty-element tags for empty values, DEFAULT values present, SET components in canonical
/// order and SET OF elements sorted. The only difference is that BASIC-XER writes CARRIAGE RETURN as "&#13;" so that
/// it survives XML end-of-line normalisation; CANONICAL-XER cannot represent it (no character references, 9.1.3).
/// </summary>
internal sealed class XerWriter : Asn1Writer
{
    private readonly StringBuilder _sb;
    private readonly bool _canonical;
    private string? _pending;

    public XerWriter(StringBuilder sb, bool canonical, Asn1EncodingOptions options)
        : base(options)
    {
        _sb = sb;
        _canonical = canonical;
    }

    private XerWriter(XerWriter parent, StringBuilder sb)
        : base(parent)
    {
        _sb = sb;
        _canonical = parent._canonical;
    }

    private string RuleName => _canonical ? "CXER" : "XER";

    // ------------------------------------------------------------------ markup

    /// <summary>Opens an element; the start tag is written lazily so that empty content becomes an empty-element tag (9.1.4).</summary>
    public void Open(string name)
    {
        Flush();
        _pending = name;
    }

    public void Close(string name)
    {
        if (_pending is not null)
        {
            _sb.Append('<').Append(_pending).Append("/>");
            _pending = null;
            return;
        }

        _sb.Append("</").Append(name).Append('>');
    }

    private void Flush()
    {
        if (_pending is not null)
        {
            _sb.Append('<').Append(_pending).Append('>');
            _pending = null;
        }
    }

    private void Text(string text)
    {
        if (text.Length > 0)
        {
            Flush();
            _sb.Append(text);
        }
    }

    private void EmptyElement(string name)
    {
        Flush();
        _sb.Append('<').Append(name).Append("/>");
    }

    private void Element(string name, Action content)
    {
        Open(name);
        content();
        Close(name);
    }

    private void CheckSize()
    {
        if (_sb.Length > Options.MaxEncodedSize)
        {
            throw Error($"the encoding exceeds the limit of {Options.MaxEncodedSize} octets");
        }
    }

    // ------------------------------------------------------------------ primitives

    /// <summary>X.693 8.3.5: "EmptyElementBoolean".</summary>
    public override void WriteBoolean(BooleanType type, bool value) => EmptyElement(value ? "true" : "false");

    /// <summary>X.680 24.3: empty.</summary>
    public override void WriteNull(NullType type)
    {
    }

    /// <summary>X.693 8.3.6: "XMLSignedNumber".</summary>
    public override void WriteInteger(Asn1Type type, BigInteger value) => Text(value.ToString(CultureInfo.InvariantCulture));

    /// <summary>X.693 8.3.7: "EmptyElementEnumerated".</summary>
    public override void WriteEnumerated(Asn1Type type, EnumerationInfo enumeration, EnumItemInfo item)
    {
        if (item.IsUnknown)
        {
            throw Error($"an unknown extension item of {type.Name} cannot be encoded");
        }

        EmptyElement(item.Name!);
    }

    /// <summary>X.693 8.3.8 and 9.2: "EmptyElementReal" for special values, otherwise the canonical "realnumber".</summary>
    public override void WriteReal(RealType type, double value)
    {
        if (double.IsNaN(value))
        {
            EmptyElement("NOT-A-NUMBER");
        }
        else if (double.IsPositiveInfinity(value))
        {
            EmptyElement("PLUS-INFINITY");
        }
        else if (double.IsNegativeInfinity(value))
        {
            EmptyElement("MINUS-INFINITY");
        }
        else
        {
            Text(XerSyntax.FormatReal(value));
        }
    }

    /// <summary>X.693 8.3.9, 9.3: "xmlbstring" without white-space; named-bit values without trailing zero bits.</summary>
    public override void WriteBitString(BitStringType type, Asn1BitString value)
    {
        RejectContents(type);
        if (type.HasNamedBits)
        {
            value = value.TrimTrailingZeros();
        }

        Text(value.ToBinaryString());
    }

    /// <summary>X.693 9.4: "xmlhstring" in upper case without white-space.</summary>
    public override void WriteOctetString(OctetStringType type, ReadOnlySpan<byte> value)
    {
        RejectContents(type);
        Text(Convert.ToHexString(value));
    }

    private void RejectContents(Asn1Type type)
    {
        // X.693 9.3.1/9.4: CANONICAL-XER must use the "XMLTypedValue" of the contained type, which descriptors do not carry.
        if (_canonical && type.Constraints.Any(c => c.Root is OpaqueElement { Kind: "contents" }))
        {
            throw new Asn1UnsupportedException($"CXER encoding of the contents-constrained {type.Name} (X.693 9.3.1/9.4) is not implemented");
        }
    }

    /// <summary>X.693 9.8: "XMLNumberForm" components.</summary>
    public override void WriteObjectIdentifier(ObjectIdentifierType type, ObjectIdentifier value) => Text(value.ToString());

    /// <summary>X.693 9.9: "XMLNumberForm" components.</summary>
    public override void WriteRelativeObjectIdentifier(RelativeOidType type, RelativeObjectIdentifier value) => Text(value.ToString());

    /// <summary>X.680 41.9 and 12.15: "xmlcstring".</summary>
    public override void WriteCharacterString(CharacterStringType type, string value)
    {
        if (value.Length == 0)
        {
            return;
        }

        Flush();
        XerSyntax.AppendCharacters(_sb, value, !_canonical, Error);
        CheckSize();
    }

    public override void WriteTime(TimeType type, string value)
    {
        if (_canonical)
        {
            value = CanonicalTime(type, value);
        }

        Text(value);
    }

    private string CanonicalTime(TimeType type, string value)
    {
        switch (type.TimeKind)
        {
            case TimeKind.UtcTime or TimeKind.GeneralizedTime:
                // X.693 9.10/9.11 are the X.690 11.7/11.8 restrictions.
                return TimeSyntax.IsCanonical(type.TimeKind, value)
                    ? value
                    : throw Error($"CXER requires the canonical {TimeType.Keyword(type.TimeKind)} form (X.693 9.10/9.11); got '{value}'");
            default:
                if (value.Contains('/', StringComparison.Ordinal) || value.Contains('P', StringComparison.Ordinal) || value.Contains('R', StringComparison.Ordinal))
                {
                    throw new Asn1UnsupportedException($"CXER normalisation of intervals, durations and recurrences (X.693 9.13 c/d) is not implemented");
                }

                // X.693 9.13 a) and b): full stop as the decimal sign; no minutes in whole-hour time differences.
                value = value.Replace(',', '.');
                return value.Length >= 6 && value[^6] is '+' or '-' && value.EndsWith(":00", StringComparison.Ordinal) ? value[..^3] : value;
        }
    }

    // ------------------------------------------------------------------ structured

    public override void WriteSequence<T>(SequenceType<T> type, T value)
    {
        var info = XerTypeInfo.Get(type);
        var comps = type.Components;
        T? defaults = default;
        foreach (var i in info.Order)
        {
            var c = comps[i];
            if (c.IsPresent(value))
            {
                Element(c.Name, () => c.Encode(this, value));
                continue;
            }

            if (c.Presence == Asn1Presence.Default)
            {
                // X.693 9.5/9.6.3: DEFAULT values are always textually present.
                if (defaults is null)
                {
                    defaults = type.CreateInstance();
                }

                c.ApplyDefault(defaults);
                var d = defaults;
                Element(c.Name, () => c.Encode(this, d));
            }
            else if (c.Presence == Asn1Presence.Required && !c.IsExtensionAddition)
            {
                throw Error($"mandatory component '{c.Name}' is absent");
            }
        }

        CheckSize();
    }

    public override void WriteCollection<TElement>(CollectionType<TElement> type, IReadOnlyList<TElement> value)
    {
        var info = XerTypeInfo.Get(type);
        void Item(XerWriter w, int i)
        {
            if (info.IsValueList)
            {
                type.EncodeElement(w, i, value[i]);
            }
            else
            {
                w.Element(info.ItemName, () => type.EncodeElement(w, i, value[i]));
            }
        }

        if (!type.IsSet || value.Count < 2)
        {
            for (var i = 0; i < value.Count; i++)
            {
                Item(this, i);
            }

            CheckSize();
            return;
        }

        // X.693 9.7: SET OF items sorted by their CXER character strings (code point order, shorter first).
        Flush();
        var items = new List<string>(value.Count);
        for (var i = 0; i < value.Count; i++)
        {
            var sb = new StringBuilder();
            Item(new XerWriter(this, sb), i);
            items.Add(sb.ToString());
        }

        items.Sort(CompareCodePoints);
        foreach (var item in items)
        {
            _sb.Append(item);
        }

        CheckSize();
    }

    internal static int CompareCodePoints(string x, string y)
    {
        var a = x.EnumerateRunes().GetEnumerator();
        var b = y.EnumerateRunes().GetEnumerator();
        while (true)
        {
            var hasA = a.MoveNext();
            var hasB = b.MoveNext();
            if (!hasA || !hasB)
            {
                return hasA ? 1 : hasB ? -1 : 0;
            }

            var c = a.Current.Value.CompareTo(b.Current.Value);
            if (c != 0)
            {
                return c;
            }
        }
    }

    public override void WriteChoice<T>(ChoiceType<T> type, T value)
    {
        var index = type.GetIndex(value);
        if (index < 0)
        {
            var unknown = type.GetUnknown(value) ?? throw Error($"no alternative of {type.Name} is selected");
            if (unknown.Value.EncodingRule is not ("XER" or "CXER"))
            {
                throw Error($"an unknown alternative decoded with {unknown.Value.EncodingRule} cannot be re-encoded with {RuleName}");
            }

            // XerReader keeps an unknown alternative as its complete element.
            Flush();
            _sb.Append(Encoding.UTF8.GetString(unknown.Value.Encoded.Span));
            return;
        }

        var alt = type.Alternatives[index];
        Element(alt.Name, () => alt.Encode(this, value));
    }

    /// <summary>X.693 8.5, 9.12: the "XMLTypedValue" of the contained value.</summary>
    public override void WriteOpenType(OpenType type, Asn1OpenValue value)
    {
        if (!value.IsRaw)
        {
            var name = XerTypeInfo.Get(value.Type!).TypeName;
            Element(name, () => value.Type!.EncodeBoxed(this, value.Value));
            return;
        }

        if (value.EncodingRule is not ("XER" or "CXER"))
        {
            throw Error($"raw {value.EncodingRule} contents cannot be embedded in a {RuleName} encoding");
        }

        Flush();
        _sb.Append(Encoding.UTF8.GetString(value.Encoded.Span));
    }
}
