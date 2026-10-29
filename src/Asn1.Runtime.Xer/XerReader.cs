using System.Globalization;
using System.Numerics;
using System.Text;
using Asn1.Constraints;
using Asn1.Runtime.Primitives;

namespace Asn1.Runtime.Xer;

/// <summary>Reader for BASIC-XER (X.693 clause 8). CANONICAL-XER strictness is checked by the rule after decoding.</summary>
internal sealed class XerReader : Asn1Reader
{
    private readonly string _ruleName;
    private XerElement _current;

    public XerReader(XerElement root, string ruleName, Asn1DecodingOptions options)
        : base(options)
    {
        _current = root;
        _ruleName = ruleName;
    }

    /// <summary>True if unknown extensions were skipped (such input cannot be checked by re-encoding).</summary>
    public bool SawUnknownExtension { get; private set; }

    public override long BitPosition => -1;

    // ------------------------------------------------------------------ content access

    private string TextContent(string what)
    {
        if (_current.Content.Count == 1 && _current.Content[0] is string single)
        {
            return single;
        }

        var sb = new StringBuilder();
        foreach (var item in _current.Content)
        {
            sb.Append(item as string ?? throw Error(Asn1DecodeError.Malformed, $"unexpected element <{((XerElement)item).Name}> in {what}"));
        }

        return sb.ToString();
    }

    private List<XerElement> Elements(string what)
    {
        var list = new List<XerElement>();
        foreach (var item in _current.Content)
        {
            if (item is XerElement e)
            {
                list.Add(e);
            }
            else if (!((string)item).All(XerSyntax.IsWhiteSpace))
            {
                throw Error(Asn1DecodeError.Malformed, $"unexpected text in {what}");
            }
        }

        return list;
    }

    private XerElement SingleElement(string what)
    {
        var elements = Elements(what);
        return elements.Count == 1 ? elements[0] : throw Error(Asn1DecodeError.Malformed, $"expected one element for {what}, found {elements.Count}");
    }

    private static bool IsEmpty(XerElement e) => e.Content.All(c => c is string s && s.Length == 0);

    private TElement DecodeElement<TElement>(CollectionType<TElement> type, XerElement element, int index)
    {
        var saved = _current;
        _current = element;
        try
        {
            return type.DecodeElement(this, index);
        }
        finally
        {
            _current = saved;
        }
    }

    private static string RawXml(XerElement e)
    {
        var sb = new StringBuilder();
        e.AppendXml(sb);
        return sb.ToString();
    }

    // ------------------------------------------------------------------ primitives

    public override bool ReadBoolean(BooleanType type)
    {
        var e = SingleElement("a BOOLEAN");
        return IsEmpty(e) && e.Name is "true" or "false"
            ? e.Name == "true"
            : throw Error(Asn1DecodeError.Malformed, $"expected <true/> or <false/>, found <{e.Name}> (X.693 8.3.5)");
    }

    public override void ReadNull(NullType type)
    {
        if (TextContent("NULL").Length != 0 && !TextContent("NULL").All(XerSyntax.IsWhiteSpace))
        {
            throw Error(Asn1DecodeError.Malformed, "a NULL value must be empty");
        }
    }

    public override bool TryReadInt64(Asn1Type type, out long value, out BigInteger large)
    {
        var text = TrimmedText("an INTEGER");
        if (XerSyntax.IsSignedNumber(text) && text.Length <= Options.MaxIntegerLength * 3
            && long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value))
        {
            large = default;
            return true;
        }

        // Other text gets the checks and messages of ReadInteger.
        large = ReadInteger(type);
        return IntegerCodec.TryToInt64(large, out value);
    }

    /// <summary>The text content without leading and trailing white-space (no copy for a single text node).</summary>
    private ReadOnlySpan<char> TrimmedText(string what) => TextContent(what).AsSpan().Trim(XerSyntax.WhiteSpace);

    public override BigInteger ReadInteger(Asn1Type type)
    {
        var text = XerSyntax.TrimWhiteSpace(TextContent("an INTEGER"));
        if (!XerSyntax.IsSignedNumber(text))
        {
            throw Error(Asn1DecodeError.Malformed, $"'{text}' is not an XMLSignedNumber (X.693 8.3.6)");
        }

        if (text.Length > Options.MaxIntegerLength * 3)
        {
            throw Error(Asn1DecodeError.LimitExceeded, $"INTEGER of {text.Length} digits exceeds the limit");
        }

        return BigInteger.Parse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
    }

    public override EnumItemInfo ReadEnumerated(Asn1Type type, EnumerationInfo enumeration)
    {
        var e = SingleElement("an ENUMERATED value");
        if (!IsEmpty(e))
        {
            throw Error(Asn1DecodeError.Malformed, $"an enumeration identifier must be an empty element, found <{e.Name}> with content (X.693 8.3.7)");
        }

        if (enumeration.FindByName(e.Name) is { } item)
        {
            return item;
        }

        if (enumeration.IsExtensible)
        {
            // X.693 8.6.4: an unknown extension item.
            SawUnknownExtension = true;
            return new EnumItemInfo(null, 0, true, -1);
        }

        throw Error(Asn1DecodeError.Malformed, $"<{e.Name}/> is not an identifier of {type.Name}");
    }

    public override double ReadReal(RealType type)
    {
        if (_current.Content.Any(c => c is XerElement))
        {
            var e = SingleElement("a REAL");
            return IsEmpty(e) ? e.Name switch
            {
                "PLUS-INFINITY" => double.PositiveInfinity,
                "MINUS-INFINITY" => double.NegativeInfinity,
                "NOT-A-NUMBER" => double.NaN,
                _ => throw Error(Asn1DecodeError.Malformed, $"<{e.Name}/> is not a special REAL value (X.693 8.3.8)"),
            } : throw Error(Asn1DecodeError.Malformed, "a special REAL value must be an empty element");
        }

        var text = XerSyntax.TrimWhiteSpace(TextContent("a REAL"));
        return XerSyntax.IsRealNumber(text)
            ? double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture)
            : throw Error(Asn1DecodeError.Malformed, $"'{text}' is not a realnumber (X.680 12.9)");
    }

    public override Asn1BitString ReadBitString(BitStringType type)
    {
        RejectTypedContents(type);
        var text = XerSyntax.RemoveWhiteSpace(TextContent("a BIT STRING"));
        if (!text.All(c => c is '0' or '1'))
        {
            throw Error(Asn1DecodeError.Malformed, "a BIT STRING must be an xmlbstring of 0 and 1 (X.693 8.3.9)");
        }

        TrackAllocation(text.Length / 8);
        return Asn1BitString.FromBinary(text);
    }

    public override byte[] ReadOctetString(OctetStringType type)
    {
        RejectTypedContents(type);
        var text = XerSyntax.RemoveWhiteSpace(TextContent("an OCTET STRING"));
        if (text.Length % 2 != 0 || !text.All(char.IsAsciiHexDigit))
        {
            throw Error(Asn1DecodeError.Malformed, "an OCTET STRING must be an xmlhstring with an even number of hexadecimal digits (X.680 23.8)");
        }

        TrackAllocation(text.Length / 2);
        return Convert.FromHexString(text);
    }

    private void RejectTypedContents(Asn1Type type)
    {
        if (_current.Content.Any(c => c is XerElement) && type.Constraints.Any(c => c.Root is OpaqueElement { Kind: "contents" }))
        {
            throw new Asn1UnsupportedException($"XER decoding of the XMLTypedValue form of the contents-constrained {type.Name} is not implemented");
        }
    }

    public override ObjectIdentifier ReadObjectIdentifier(ObjectIdentifierType type)
    {
        var arcs = ReadArcs("an OBJECT IDENTIFIER", allowNames: true);
        try
        {
            return new ObjectIdentifier(arcs);
        }
        catch (ArgumentException e)
        {
            throw Error(Asn1DecodeError.Malformed, e.Message);
        }
    }

    public override RelativeObjectIdentifier ReadRelativeObjectIdentifier(RelativeOidType type) =>
        new(ReadArcs("a RELATIVE-OID", allowNames: false));

    /// <summary>X.680 32.3/33.3: components separated by "."; number, name(number) or (for OIDs) a well-known name.</summary>
    private List<BigInteger> ReadArcs(string what, bool allowNames)
    {
        var text = XerSyntax.TrimWhiteSpace(TextContent(what));
        var arcs = new List<BigInteger>();
        foreach (var part in text.Split('.'))
        {
            var open = part.IndexOf('(', StringComparison.Ordinal);
            var number = open > 0 && part.EndsWith(')') ? part[(open + 1)..^1] : part;
            if (number.Length > 0 && number.All(char.IsAsciiDigit) && (number.Length == 1 || number[0] != '0'))
            {
                arcs.Add(BigInteger.Parse(number, CultureInfo.InvariantCulture));
            }
            else if (allowNames && open < 0 && WellKnownOids.Lookup(arcs, part) is { } known)
            {
                arcs.Add(known);
            }
            else
            {
                throw Error(Asn1DecodeError.Malformed, $"'{part}' is not a valid component of {what}");
            }
        }

        return arcs;
    }

    public override string ReadCharacterString(CharacterStringType type)
    {
        // X.680 12.15: text with control characters as empty elements; white-space is significant (41.9).
        var sb = new StringBuilder();
        foreach (var item in _current.Content)
        {
            if (item is string s)
            {
                sb.Append(s);
            }
            else if (item is XerElement e && IsEmpty(e) && XerSyntax.ControlCharacter(e.Name) is >= 0 and var c)
            {
                sb.Append((char)c);
            }
            else
            {
                throw Error(Asn1DecodeError.Malformed, $"unexpected element <{((XerElement)item).Name}> in a character string");
            }
        }

        var text = sb.ToString();
        TrackAllocation(text.Length * 2L);
        return CharacterRepertoire.Validate(type.StringKind, text) is { } invalid ? throw Error(Asn1DecodeError.Malformed, invalid) : text;
    }

    public override string ReadTime(TimeType type) => XerSyntax.TrimWhiteSpace(TextContent("a time value"));

    // ------------------------------------------------------------------ structured

    public override T ReadSequence<T>(SequenceType<T> type)
    {
        var members = XerTypeInfo.Get(type).Members;
        var comps = type.Components;
        var value = type.CreateInstance();
        Span<bool> present = comps.Length <= 128 ? stackalloc bool[comps.Length] : new bool[comps.Length];

        // Text between the component elements may only be white-space; checked before any component is decoded.
        foreach (var item in _current.Content)
        {
            if (item is string text && text.AsSpan().ContainsAnyExcept(XerSyntax.WhiteSpace))
            {
                throw Error(Asn1DecodeError.Malformed, $"unexpected text in {type.Name}");
            }
        }

        foreach (var item in _current.Content)
        {
            if (item is not XerElement e)
            {
                continue;
            }

            if (!members.TryGetValue(e.Name, out var i))
            {
                if (type.IsExtensible)
                {
                    // X.693 8.6.2: an unknown extension addition.
                    SawUnknownExtension = true;
                    continue;
                }

                throw Error(Asn1DecodeError.Malformed, $"<{e.Name}> is not a component of {type.Name}");
            }

            if (present[i])
            {
                throw Error(Asn1DecodeError.Malformed, $"component <{e.Name}> appears more than once");
            }

            var saved = _current;
            _current = e;
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
                throw Error(Asn1DecodeError.Malformed, $"mandatory component <{comps[i].Name}> is missing");
            }
        }

        return value;
    }

    public override List<TElement> ReadCollection<TElement>(CollectionType<TElement> type)
    {
        var info = XerTypeInfo.Get(type);
        var items = Elements(type.Name);
        CheckCollectionCount(items.Count);
        var list = new List<TElement>(items.Count);
        foreach (var e in items)
        {
            if ((list.Count & 1023) == 0)
            {
                Options.CancellationToken.ThrowIfCancellationRequested();
            }

            if (info.IsValueList)
            {
                // X.680 26.5-26.7: the item is the value itself (<true/>, <red/>, <alternative>...</alternative>).
                var holder = new XerElement(e.Name);
                holder.Content.Add(e);
                list.Add(DecodeElement(type, holder, list.Count));
            }
            else if (e.Name == info.ItemName)
            {
                list.Add(DecodeElement(type, e, list.Count));
            }
            else
            {
                throw Error(Asn1DecodeError.Malformed, $"expected <{info.ItemName}> items, found <{e.Name}> (X.680 26.9/26.10)");
            }
        }

        return list;
    }

    public override T ReadChoice<T>(ChoiceType<T> type)
    {
        var e = SingleElement(type.Name);
        if (XerTypeInfo.Get(type).Members.TryGetValue(e.Name, out var index))
        {
            var alt = type.Alternatives[index];
            var saved = _current;
            _current = e;
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
            throw Error(Asn1DecodeError.Malformed, $"<{e.Name}> is not an alternative of {type.Name}");
        }

        SawUnknownExtension = true;
        if (!type.SupportsUnknownExtensions)
        {
            throw Error(Asn1DecodeError.Unsupported, $"unknown extension alternative <{e.Name}> of {type.Name}");
        }

        // X.693 8.6.3: kept as the complete element so that it is relayed unchanged.
        return type.CreateUnknown(-1, Asn1OpenValue.FromEncoded(Encoding.UTF8.GetBytes(RawXml(e)), _ruleName));
    }

    /// <summary>X.693 8.5: the "XMLTypedValue" (or "xmlhstring") of the contained value, kept as raw XML.</summary>
    public override Asn1OpenValue ReadOpenType(OpenType type)
    {
        var sb = new StringBuilder();
        _current.AppendContent(sb);
        return Asn1OpenValue.FromEncoded(Encoding.UTF8.GetBytes(XerSyntax.TrimWhiteSpace(sb.ToString())), _ruleName);
    }
}
