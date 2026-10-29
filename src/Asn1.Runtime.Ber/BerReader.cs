using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using Asn1.Runtime.Primitives;

namespace Asn1.Runtime.Ber;

/// <summary>Reader for BER, CER and DER. All reads are confined to the current TLV region.</summary>
internal sealed class BerReader : Asn1Reader
{
    [ThreadStatic]
    private static BerReader? t_cached;

    private readonly BerProfile _profile;
    private readonly List<(int End, bool Indefinite)> _regions = [];
    private ReadOnlyMemory<byte> _data;
    private byte[]? _array;
    private int _offset;
    private int _pos;
    private int _end;
    private bool _indefinite;

    // The header last parsed by PeekHeader, which the following ReadHeader at the same position reuses.
    private int _peekedAt = -1;
    private TlvHeader _peeked;
    private int _peekedNext;

    public BerReader(ReadOnlyMemory<byte> data, BerProfile profile, Asn1DecodingOptions options)
        : base(options)
    {
        _profile = profile;
        SetData(data);
    }

    public int Position => _pos;

    public override long BitPosition => _pos * 8L;

    private bool Canonical => _profile != BerProfile.Ber;

    // A span over an array is cheaper to create than ReadOnlyMemory.Span, which is read for every header.
    private ReadOnlySpan<byte> Span => _array is not null ? new ReadOnlySpan<byte>(_array, _offset, _data.Length) : _data.Span;

    /// <summary>A reader for one decoding, reusing this thread's previous one when it has the same settings.</summary>
    public static BerReader Rent(ReadOnlyMemory<byte> data, BerProfile profile, Asn1DecodingOptions options)
    {
        var cached = t_cached;
        if (cached is null || cached._profile != profile || !ReferenceEquals(cached.Options, options))
        {
            return new BerReader(data, profile, options);
        }

        // Taken out of the slot, so a decoding started while this one runs gets its own reader.
        t_cached = null;
        cached.ResetForReuse();
        cached._regions.Clear();
        cached.SetData(data);
        return cached;
    }

    /// <summary>Makes a reader from <see cref="Rent"/> available again; it no longer references the input.</summary>
    public void Return()
    {
        SetData(ReadOnlyMemory<byte>.Empty);
        t_cached = this;
    }

    private void SetData(ReadOnlyMemory<byte> data)
    {
        _data = data;
        if (MemoryMarshal.TryGetArray(data, out var segment))
        {
            _array = segment.Array;
            _offset = segment.Offset;
        }
        else
        {
            _array = null;
            _offset = 0;
        }

        _pos = 0;
        _end = data.Length;
        _indefinite = false;
        _peekedAt = -1;
    }

    // ------------------------------------------------------------------ regions and headers

    private TlvHeader ReadHeader()
    {
        TlvHeader header;
        if (_peekedAt == _pos && _peekedAt >= 0)
        {
            header = _peeked;
            _pos = _peekedNext;
        }
        else if (!TlvReader.TryReadHeader(Span, ref _pos, _end, out header, out var error))
        {
            throw Error(error!.StartsWith("truncated", StringComparison.Ordinal) ? Asn1DecodeError.Truncated : error.Contains("length", StringComparison.Ordinal) ? Asn1DecodeError.InvalidLength : Asn1DecodeError.InvalidTag, error);
        }

        if (Canonical && header.NonMinimalLength)
        {
            throw Error(Asn1DecodeError.NonCanonical, $"{_profile} requires the minimal length form (X.690 10.1)");
        }

        if (header.IsIndefinite && _profile == BerProfile.Der)
        {
            throw Error(Asn1DecodeError.NonCanonical, "DER forbids the indefinite length form (X.690 10.1)");
        }

        if (!header.IsIndefinite && header.Constructed && _profile == BerProfile.Cer)
        {
            throw Error(Asn1DecodeError.NonCanonical, "CER requires the indefinite length form for constructed encodings (X.690 9.1)");
        }

        return header;
    }

    private bool PeekHeader(out TlvHeader header)
    {
        var p = _pos;
        if (AtRegionEnd())
        {
            header = default;
            return false;
        }

        if (_peekedAt == _pos && _peekedAt >= 0)
        {
            header = _peeked;
            return true;
        }

        var ok = TlvReader.TryReadHeader(Span, ref p, _end, out header, out _);
        if (ok)
        {
            // Valid only within the same region: the limit (_end) is part of the check.
            _peekedAt = _pos;
            _peeked = header;
            _peekedNext = p;
        }

        return ok;
    }

    private bool AtRegionEnd()
    {
        if (_indefinite)
        {
            return _pos + 1 < _end && Span[_pos] == 0 && Span[_pos + 1] == 0;
        }

        return _pos >= _end;
    }

    private void EnterRegion(TlvHeader header)
    {
        _peekedAt = -1;
        EnterNested();
        _regions.Add((_end, _indefinite));
        if (!header.IsIndefinite)
        {
            _end = header.ContentStart + header.Length;
            _indefinite = false;
        }
        else
        {
            _indefinite = true;
        }
    }

    private void ExitRegion()
    {
        if (_indefinite)
        {
            if (!(_pos + 1 < _end && Span[_pos] == 0 && Span[_pos + 1] == 0))
            {
                throw Error(_pos + 1 >= _end ? Asn1DecodeError.Truncated : Asn1DecodeError.Malformed, "expected end-of-contents octets");
            }

            _pos += 2;
        }
        else if (_pos != _end)
        {
            throw Error(Asn1DecodeError.Malformed, $"{_end - _pos} unexpected octet(s) at the end of a constructed encoding");
        }

        var (end, indefinite) = _regions[^1];
        _regions.RemoveAt(_regions.Count - 1);
        _peekedAt = -1;
        _end = end;
        _indefinite = indefinite;
        ExitNested();
    }

    /// <summary>Skips one complete TLV (including nested indefinite-length encodings).</summary>
    private void SkipElement()
    {
        var h = ReadHeader();
        if (!h.IsIndefinite)
        {
            _pos = h.ContentStart + h.Length;
            return;
        }

        EnterRegion(h);
        while (!AtRegionEnd())
        {
            if (_pos >= _end)
            {
                throw Error(Asn1DecodeError.Truncated, "missing end-of-contents octets");
            }

            SkipElement();
        }

        ExitRegion();
    }

    /// <summary>Reads explicit wrappers and the innermost header of a value; returns the header and the number of regions entered.</summary>
    private (TlvHeader Header, int Opened) OpenValue(Asn1Type type)
    {
        var opened = 0;
        foreach (var step in type.Tags)
        {
            var h = ReadHeader();
            if (h.Tag != step.Tag)
            {
                throw Error(Asn1DecodeError.InvalidTag, $"expected tag {step.Tag} for {type.Name} but found {h.Tag}");
            }

            if (step.IsExplicit)
            {
                if (!h.Constructed)
                {
                    throw Error(Asn1DecodeError.InvalidTag, $"explicit tag {step.Tag} must use the constructed form");
                }

                EnterRegion(h);
                opened++;
                continue;
            }

            return (h, opened);
        }

        return (default, opened);
    }

    private void CloseRegions(int count)
    {
        for (var i = 0; i < count; i++)
        {
            ExitRegion();
        }
    }

    /// <summary>Reads primitive contents (or concatenates constructed string segments where permitted).</summary>
    private ReadOnlySpan<byte> ReadContents(Asn1Type type, TlvHeader h, bool isString, bool isBitString, out int unusedBits)
    {
        unusedBits = 0;
        if (!h.Constructed)
        {
            _pos = h.ContentStart + h.Length;
            var content = Span.Slice(h.ContentStart, h.Length);
            if (isBitString)
            {
                if (content.IsEmpty)
                {
                    throw Error(Asn1DecodeError.Malformed, "BIT STRING contents must contain the unused-bits octet");
                }

                unusedBits = content[0];
                if (unusedBits > 7 || (content.Length == 1 && unusedBits != 0))
                {
                    throw Error(Asn1DecodeError.Malformed, $"invalid number of unused bits {unusedBits}");
                }

                if (_profile == BerProfile.Cer && h.Length > 1000)
                {
                    throw Error(Asn1DecodeError.NonCanonical, "CER requires the constructed form for strings longer than 1000 octets (X.690 9.2)");
                }

                return content[1..];
            }

            if (isString && _profile == BerProfile.Cer && h.Length > 1000)
            {
                throw Error(Asn1DecodeError.NonCanonical, "CER requires the constructed form for strings longer than 1000 octets (X.690 9.2)");
            }

            return content;
        }

        if (!isString)
        {
            throw Error(Asn1DecodeError.Malformed, $"{type.Name} must use the primitive form");
        }

        if (_profile == BerProfile.Der)
        {
            throw Error(Asn1DecodeError.NonCanonical, "DER requires the primitive form for strings (X.690 10.2)");
        }

        // Constructed string: concatenate fragments (X.690 8.6.4, 8.7.3).
        var output = new List<byte>();
        var fragments = 0;
        var lastLength = -1;
        EnterRegion(h);
        CollectFragments(output, isBitString, ref unusedBits, ref fragments, ref lastLength, depth: 0);
        ExitRegion();
        if (_profile == BerProfile.Cer && (output.Count + (isBitString ? fragments : 0)) <= 1000)
        {
            throw Error(Asn1DecodeError.NonCanonical, "CER requires the primitive form for strings of at most 1000 octets (X.690 9.2)");
        }

        return output.ToArray();
    }

    private void CollectFragments(List<byte> output, bool isBitString, ref int unusedBits, ref int fragments, ref int lastLength, int depth)
    {
        var fragmentTag = isBitString ? Asn1Tag.BitString : Asn1Tag.OctetString;
        while (!AtRegionEnd())
        {
            if (unusedBits != 0)
            {
                throw Error(Asn1DecodeError.Malformed, "only the last BIT STRING fragment may have unused bits");
            }

            var h = ReadHeader();
            if (h.Tag != fragmentTag)
            {
                throw Error(Asn1DecodeError.InvalidTag, $"string fragments must be tagged {fragmentTag}");
            }

            if (h.Constructed)
            {
                if (_profile == BerProfile.Cer)
                {
                    throw Error(Asn1DecodeError.NonCanonical, "CER string fragments must be primitive");
                }

                EnterRegion(h);
                CollectFragments(output, isBitString, ref unusedBits, ref fragments, ref lastLength, depth + 1);
                ExitRegion();
                continue;
            }

            if (_profile == BerProfile.Cer && lastLength >= 0 && lastLength != 1000)
            {
                throw Error(Asn1DecodeError.NonCanonical, "CER string fragments except the last must contain 1000 octets");
            }

            var content = Span.Slice(h.ContentStart, h.Length);
            _pos = h.ContentStart + h.Length;
            if (isBitString)
            {
                if (content.IsEmpty || content[0] > 7)
                {
                    throw Error(Asn1DecodeError.Malformed, "invalid BIT STRING fragment");
                }

                unusedBits = content[0];
                content = content[1..];
            }

            TrackAllocation(content.Length);
            output.AddRange(content);
            fragments++;
            lastLength = h.Length;
        }
    }

    private ReadOnlySpan<byte> ReadPrimitive(Asn1Type type, out int opened)
    {
        var (h, n) = OpenValue(type);
        opened = n;
        return ReadContents(type, h, isString: false, isBitString: false, out _);
    }

    // ------------------------------------------------------------------ primitives

    public override bool ReadBoolean(BooleanType type)
    {
        var c = ReadPrimitive(type, out var n);
        if (c.Length != 1)
        {
            throw Error(Asn1DecodeError.Malformed, "BOOLEAN contents must be exactly one octet");
        }

        if (Canonical && c[0] is not (0x00 or 0xFF))
        {
            throw Error(Asn1DecodeError.NonCanonical, $"{_profile} requires BOOLEAN TRUE to be 0xFF (X.690 11.1)");
        }

        var value = c[0] != 0;
        CloseRegions(n);
        return value;
    }

    public override BigInteger ReadInteger(Asn1Type type)
    {
        var c = ReadPrimitive(type, out var n);
        var value = ParseInteger(c);
        CloseRegions(n);
        return value;
    }

    public override bool TryReadInt64(Asn1Type type, out long value, out BigInteger large)
    {
        var c = ReadPrimitive(type, out var n);
        CheckInteger(c);
        var fits = c.Length <= 8;
        value = fits ? ToInt64(c) : 0;
        large = fits ? default : IntegerCodec.FromTwosComplement(c);
        CloseRegions(n);
        return fits;
    }

    /// <summary>Two's complement contents of at most 8 octets.</summary>
    private static long ToInt64(ReadOnlySpan<byte> c)
    {
        long value = (sbyte)c[0];
        for (var i = 1; i < c.Length; i++)
        {
            value = (value << 8) | c[i];
        }

        return value;
    }

    private BigInteger ParseInteger(ReadOnlySpan<byte> c)
    {
        CheckInteger(c);
        return IntegerCodec.FromTwosComplement(c);
    }

    private void CheckInteger(ReadOnlySpan<byte> c)
    {
        if (c.IsEmpty)
        {
            throw Error(Asn1DecodeError.Malformed, "INTEGER contents must not be empty (X.690 8.3.1)");
        }

        if (IntegerCodec.IsNonMinimalTwosComplement(c))
        {
            throw Error(Asn1DecodeError.Malformed, "INTEGER is not minimally encoded (X.690 8.3.2)");
        }

        if (c.Length > Options.MaxIntegerLength)
        {
            throw Error(Asn1DecodeError.LimitExceeded, $"INTEGER of {c.Length} octets exceeds the maximum of {Options.MaxIntegerLength}");
        }
    }

    public override EnumItemInfo ReadEnumerated(Asn1Type type, EnumerationInfo enumeration)
    {
        var c = ReadPrimitive(type, out var n);
        CheckInteger(c);
        if (c.Length <= 8)
        {
            var small = ToInt64(c);
            CloseRegions(n);
            return enumeration.FindByValue(small) ?? (enumeration.IsExtensible
                ? new EnumItemInfo(null, small, true, -1)
                : throw Error(Asn1DecodeError.Malformed, $"{small} is not a value of the enumeration {type.Name}"));
        }

        var value = IntegerCodec.FromTwosComplement(c);
        CloseRegions(n);
        if (value < long.MinValue || value > long.MaxValue || enumeration.FindByValue((long)value) is not { } item)
        {
            if (enumeration.IsExtensible)
            {
                return new EnumItemInfo(null, value >= long.MinValue && value <= long.MaxValue ? (long)value : 0, true, -1);
            }

            throw Error(Asn1DecodeError.Malformed, $"{value} is not a value of the enumeration {type.Name}");
        }

        return item;
    }

    public override double ReadReal(RealType type)
    {
        var c = ReadPrimitive(type, out var n);
        var value = RealCodec.Decode(c, Canonical, out var error);
        if (value is null)
        {
            throw Error(Canonical && error!.Contains("require", StringComparison.Ordinal) ? Asn1DecodeError.NonCanonical : Asn1DecodeError.Malformed, error!);
        }

        CloseRegions(n);
        return value.Value;
    }

    public override Asn1BitString ReadBitString(BitStringType type)
    {
        var (h, n) = OpenValue(type);
        var content = ReadContents(type, h, isString: true, isBitString: true, out var unused);
        TrackAllocation(content.Length);
        if (unused > 0 && content.Length > 0)
        {
            var mask = (byte)((1 << unused) - 1);
            if ((content[^1] & mask) != 0 && Canonical)
            {
                throw Error(Asn1DecodeError.NonCanonical, $"{_profile} requires unused BIT STRING bits to be zero (X.690 11.2.1)");
            }
        }

        var value = new Asn1BitString(content, (content.Length * 8L) - unused);
        if (Canonical && type.HasNamedBits && value.Length > 0 && !value[value.Length - 1])
        {
            throw Error(Asn1DecodeError.NonCanonical, $"{_profile} requires trailing zero bits to be removed for BIT STRING types with named bits (X.690 11.2.2)");
        }

        CloseRegions(n);
        return value;
    }

    public override byte[] ReadOctetString(OctetStringType type)
    {
        var (h, n) = OpenValue(type);
        var content = ReadContents(type, h, isString: true, isBitString: false, out _);
        TrackAllocation(content.Length);
        var value = content.ToArray();
        CloseRegions(n);
        return value;
    }

    public override void ReadNull(NullType type)
    {
        var c = ReadPrimitive(type, out var n);
        if (!c.IsEmpty)
        {
            throw Error(Asn1DecodeError.Malformed, "NULL contents must be empty");
        }

        CloseRegions(n);
    }

    public override ObjectIdentifier ReadObjectIdentifier(ObjectIdentifierType type)
    {
        var c = ReadPrimitive(type, out var n);
        var oid = ObjectIdentifierCodec.Decode(c, out var error) ?? throw Error(Asn1DecodeError.Malformed, error!);
        CloseRegions(n);
        return oid;
    }

    public override RelativeObjectIdentifier ReadRelativeObjectIdentifier(RelativeOidType type)
    {
        var c = ReadPrimitive(type, out var n);
        var oid = ObjectIdentifierCodec.DecodeRelative(c, out var error) ?? throw Error(Asn1DecodeError.Malformed, error!);
        CloseRegions(n);
        return oid;
    }

    public override string ReadCharacterString(CharacterStringType type)
    {
        var (h, n) = OpenValue(type);
        var content = ReadContents(type, h, isString: true, isBitString: false, out _);
        TrackAllocation(content.Length);
        var value = StringCodec.Decode(type.StringKind, content, out var error) ?? throw Error(Asn1DecodeError.Malformed, error!);
        CloseRegions(n);
        return value;
    }

    public override string ReadTime(TimeType type)
    {
        var (h, n) = OpenValue(type);
        var content = ReadContents(type, h, isString: true, isBitString: false, out _);
        foreach (var b in content)
        {
            if (b is < 0x20 or > 0x7E)
            {
                throw Error(Asn1DecodeError.Malformed, "time values must consist of visible ASCII characters");
            }
        }

        var value = Encoding.ASCII.GetString(content);
        if (Canonical && type.TimeKind is TimeKind.UtcTime or TimeKind.GeneralizedTime && !TimeSyntax.IsCanonical(type.TimeKind, value))
        {
            throw Error(Asn1DecodeError.NonCanonical, $"{_profile} requires the canonical {TimeType.Keyword(type.TimeKind)} form (X.690 11.7/11.8); got '{value}'");
        }

        CloseRegions(n);
        return value;
    }

    // ------------------------------------------------------------------ structured

    // A tagged type can only begin with its outermost tag; the tag set is needed for untagged CHOICEs and open types.
    private static bool Matches(Asn1Type type, Asn1Tag tag) =>
        !type.Tags.IsEmpty ? type.Tags[0].Tag == tag : TagSets.PossibleTags(type) is not { } set || set.Contains(tag);

    public override T ReadSequence<T>(SequenceType<T> type)
    {
        var (h, n) = OpenValue(type);
        if (!h.Constructed)
        {
            throw Error(Asn1DecodeError.Malformed, $"{type.Name} must use the constructed form");
        }

        EnterRegion(h);
        var value = type.CreateInstance();
        var components = type.Components;
        Span<bool> present = components.Length <= 128 ? stackalloc bool[components.Length] : new bool[components.Length];
        if (type.Kind == Asn1TypeKind.Sequence)
        {
            for (var i = 0; i < components.Length; i++)
            {
                var c = components[i];
                if (!PeekHeader(out var next) || !Matches(c.Type, next.Tag))
                {
                    if (!c.IsOptional && !c.IsExtensionAddition)
                    {
                        throw Error(Asn1DecodeError.Malformed, AtRegionEnd() ? $"mandatory component '{c.Name}' of {type.Name} is missing" : $"expected component '{c.Name}' of {type.Name} but found tag {next.Tag}");
                    }

                    continue;
                }

                c.Decode(this, value);
                present[i] = true;
                CheckDefault(c, value);
            }
        }
        else
        {
            Asn1Tag? previous = null;
            while (!AtRegionEnd())
            {
                if (!PeekHeader(out var next))
                {
                    throw Error(Asn1DecodeError.Truncated, "truncated SET component");
                }

                var index = -1;
                for (var i = 0; i < components.Length; i++)
                {
                    if (Matches(components[i].Type, next.Tag))
                    {
                        index = i;
                        break;
                    }
                }

                if (index < 0)
                {
                    break;
                }

                if (present[index])
                {
                    throw Error(Asn1DecodeError.Malformed, $"component '{components[index].Name}' of {type.Name} occurs more than once");
                }

                var orderTag = _profile == BerProfile.Cer ? TagSets.CanonicalTag(components[index].Type) ?? next.Tag : next.Tag;
                if (Canonical && previous is { } p && p.CompareTo(orderTag) >= 0)
                {
                    throw Error(Asn1DecodeError.NonCanonical, $"{_profile} requires SET components in canonical tag order (X.690 {(_profile == BerProfile.Cer ? "9.3" : "10.3")})");
                }

                previous = orderTag;
                components[index].Decode(this, value);
                present[index] = true;
                CheckDefault(components[index], value);
            }

            for (var i = 0; i < components.Length; i++)
            {
                if (!present[i] && !components[i].IsOptional && !components[i].IsExtensionAddition)
                {
                    throw Error(Asn1DecodeError.Malformed, $"mandatory component '{components[i].Name}' of {type.Name} is missing");
                }
            }
        }

        for (var i = 0; i < components.Length; i++)
        {
            if (!present[i] && components[i].Presence == Asn1Presence.Default)
            {
                components[i].ApplyDefault(value);
            }
        }

        while (!AtRegionEnd())
        {
            if (!type.IsExtensible)
            {
                PeekHeader(out var extra);
                throw Error(Asn1DecodeError.Malformed, $"unexpected element with tag {extra.Tag} in {type.Name}");
            }

            // Unknown extension addition from a later version: skip it (X.690 / X.680 52).
            SkipElement();
        }

        ExitRegion();
        CloseRegions(n);
        return value;
    }

    private void CheckDefault<T>(SequenceComponent<T> c, T value)
    {
        if (Canonical && c.Presence == Asn1Presence.Default && c.HasDefaultValue(value))
        {
            throw Error(Asn1DecodeError.NonCanonical, $"{_profile} forbids encoding the DEFAULT value of '{c.Name}' (X.690 11.5)");
        }
    }

    public override List<TElement> ReadCollection<TElement>(CollectionType<TElement> type)
    {
        var (h, n) = OpenValue(type);
        if (!h.Constructed)
        {
            throw Error(Asn1DecodeError.Malformed, $"{type.Name} must use the constructed form");
        }

        EnterRegion(h);
        // A small initial capacity: the content length does not tell how many elements there are.
        var list = new List<TElement>(h.IsIndefinite ? 4 : Math.Clamp(h.Length / 8, 4, 64));
        var previousStart = -1;
        var previousEnd = -1;
        while (!AtRegionEnd())
        {
            CheckCollectionCount(list.Count + 1);
            if ((list.Count & 1023) == 0)
            {
                Options.CancellationToken.ThrowIfCancellationRequested();
            }

            var start = _pos;
            list.Add(type.DecodeElement(this, list.Count));
            if (Canonical && type.IsSet && previousStart >= 0 && CompareEncodings(Span[previousStart..previousEnd], Span[start.._pos]) > 0)
            {
                throw Error(Asn1DecodeError.NonCanonical, $"{_profile} requires SET OF elements in ascending order of their encodings (X.690 11.6)");
            }

            previousStart = start;
            previousEnd = _pos;
        }

        ExitRegion();
        CloseRegions(n);
        return list;
    }

    private static int CompareEncodings(ReadOnlySpan<byte> x, ReadOnlySpan<byte> y)
    {
        var common = Math.Min(x.Length, y.Length);
        var c = x[..common].SequenceCompareTo(y[..common]);
        if (c != 0)
        {
            return c;
        }

        var rest = x.Length > y.Length ? x[common..] : y[common..];
        foreach (var r in rest)
        {
            if (r != 0)
            {
                return x.Length > y.Length ? 1 : -1;
            }
        }

        return 0;
    }

    public override T ReadChoice<T>(ChoiceType<T> type)
    {
        var (_, n) = OpenValue(type);
        if (!PeekHeader(out var next))
        {
            throw Error(Asn1DecodeError.Truncated, $"missing alternative of {type.Name}");
        }

        var alternatives = type.Alternatives;
        for (var i = 0; i < alternatives.Length; i++)
        {
            if (Matches(alternatives[i].Type, next.Tag))
            {
                var value = alternatives[i].Decode(this);
                CloseRegions(n);
                return value;
            }
        }

        if (type.IsExtensible && type.SupportsUnknownExtensions)
        {
            var start = _pos;
            SkipElement();
            var raw = Asn1OpenValue.FromEncoded(_data[start.._pos].ToArray(), _profile.ToString().ToUpperInvariant());
            CloseRegions(n);
            return type.CreateUnknown(-1, raw);
        }

        throw Error(Asn1DecodeError.InvalidTag, $"no alternative of {type.Name} has tag {next.Tag}");
    }

    public override Asn1OpenValue ReadOpenType(OpenType type)
    {
        var (_, n) = OpenValue(type);
        var start = _pos;
        SkipElement();
        TrackAllocation(_pos - start);
        var raw = Asn1OpenValue.FromEncoded(_data[start.._pos].ToArray(), _profile.ToString().ToUpperInvariant());
        CloseRegions(n);
        return raw;
    }
}
