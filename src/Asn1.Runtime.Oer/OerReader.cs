using System.Numerics;
using System.Text;
using Asn1.Runtime.Buffers;
using Asn1.Runtime.Primitives;

namespace Asn1.Runtime.Oer;

/// <summary>
/// Reader for BASIC-OER and CANONICAL-OER. BASIC-OER accepts every form the encoder may choose (non-minimal lengths and
/// numbers, any non-zero TRUE octet); CANONICAL-OER rejects everything except the single form required by X.696 clause 31.
/// </summary>
internal sealed class OerReader : Asn1Reader
{
    private readonly long _baseBit;

    public OerReader(BitReader input, bool canonical, Asn1DecodingOptions options)
        : base(options)
    {
        Input = input;
        Canonical = canonical;
    }

    private OerReader(OerReader parent, BitReader input, long baseBit)
        : base(parent)
    {
        Input = input;
        Canonical = parent.Canonical;
        _baseBit = baseBit;
    }

    public BitReader Input { get; }

    public bool Canonical { get; }

    private string RuleName => Canonical ? "COER" : "OER";

    public override long BitPosition => _baseBit + Input.BitPosition;

    // ------------------------------------------------------------------ procedures

    private int ReadOctet() => (int)Input.ReadBits(8);

    /// <summary>8.6: length determinant.</summary>
    private long ReadLength()
    {
        var first = ReadOctet();
        if ((first & 0x80) == 0)
        {
            return first;
        }

        var n = first & 0x7F;
        if (n == 0)
        {
            throw Error(Asn1DecodeError.InvalidLength, "long-form length determinant with no subsequent octets");
        }

        var bytes = Input.ReadBytes(n);
        if (Canonical && bytes[0] == 0)
        {
            throw Error(Asn1DecodeError.NonCanonical, "COER requires a minimal long-form length (X.696 31.2)");
        }

        var length = IntegerCodec.FromUnsigned(bytes);
        if (Canonical && length < 128)
        {
            throw Error(Asn1DecodeError.NonCanonical, $"COER requires the short form for length {length} (X.696 31.2)");
        }

        if (length > Input.RemainingBits / 8)
        {
            throw Error(Asn1DecodeError.Truncated, $"length {length} exceeds the {Input.RemainingBits / 8} remaining octet(s)");
        }

        return (long)length;
    }

    private byte[] ReadLengthPrefixed(int maxLength)
    {
        var n = ReadLength();
        if (n > maxLength)
        {
            throw Error(Asn1DecodeError.LimitExceeded, $"value of {n} octets exceeds the limit of {maxLength}");
        }

        if (n > Input.RemainingBits / 8)
        {
            throw Error(Asn1DecodeError.Truncated, $"length {n} exceeds the {Input.RemainingBits / 8} remaining octet(s)");
        }

        TrackAllocation(n);
        return Input.ReadBytes(n);
    }

    private OerReader Nested(byte[] bytes) => new(this, new BitReader(bytes), BitPosition - (bytes.Length * 8L));

    private T DecodeNested<T>(byte[] bytes, Func<OerReader, T> decode)
    {
        var reader = Nested(bytes);
        var value = decode(reader);
        if (reader.Input.RemainingBits > 0)
        {
            throw Error(Asn1DecodeError.Malformed, $"{reader.Input.RemainingBits / 8} unused octet(s) inside an open type");
        }

        return value;
    }

    private BigInteger ReadVariableUnsigned(string what)
    {
        var octets = ReadLengthPrefixed(Options.MaxIntegerLength);
        if (octets.Length == 0)
        {
            throw Error(Asn1DecodeError.Malformed, $"empty {what} encoding");
        }

        if (Canonical && octets.Length > 1 && octets[0] == 0)
        {
            throw Error(Asn1DecodeError.NonCanonical, $"COER requires the minimal {what} encoding (X.696 31.4/31.7)");
        }

        return IntegerCodec.FromUnsigned(octets);
    }

    private BigInteger ReadVariableSigned()
    {
        var octets = ReadLengthPrefixed(Options.MaxIntegerLength);
        if (octets.Length == 0)
        {
            throw Error(Asn1DecodeError.Malformed, "empty integer encoding");
        }

        if (Canonical && IntegerCodec.IsNonMinimalTwosComplement(octets))
        {
            throw Error(Asn1DecodeError.NonCanonical, "COER requires the minimal integer encoding (X.696 31.4)");
        }

        return IntegerCodec.FromTwosComplement(octets);
    }

    // ------------------------------------------------------------------ primitives

    public override bool ReadBoolean(BooleanType type)
    {
        var b = ReadOctet();
        if (Canonical && b is not (0 or 0xFF))
        {
            throw Error(Asn1DecodeError.NonCanonical, $"COER requires TRUE to be encoded as FF, got {b:X2} (X.696 31.3)");
        }

        return b != 0;
    }

    public override void ReadNull(NullType type)
    {
    }

    public override BigInteger ReadInteger(Asn1Type type)
    {
        var set = OerTypeInfo.Get(type).Values;
        var lb = set.LowerBound;
        var ub = set.UpperBound;
        BigInteger value;
        if (lb is { } l && l.Sign >= 0)
        {
            var octets = ub is not { } u ? 0 : u <= byte.MaxValue ? 1 : u <= ushort.MaxValue ? 2 : u <= uint.MaxValue ? 4 : u <= ulong.MaxValue ? 8 : 0;
            value = octets > 0 ? IntegerCodec.FromUnsigned(Input.ReadBytes(octets)) : ReadVariableUnsigned("integer");
        }
        else
        {
            var octets = lb is { } sl && ub is { } su
                ? (sl >= sbyte.MinValue && su <= sbyte.MaxValue ? 1 : sl >= short.MinValue && su <= short.MaxValue ? 2 : sl >= int.MinValue && su <= int.MaxValue ? 4 : sl >= long.MinValue && su <= long.MaxValue ? 8 : 0)
                : 0;
            value = octets > 0 ? IntegerCodec.FromTwosComplement(Input.ReadBytes(octets)) : ReadVariableSigned();
        }

        if ((lb is { } min && value < min) || (ub is { } max && value > max))
        {
            throw Error(Asn1DecodeError.ConstraintViolation, $"INTEGER value {value} lies outside the OER-visible range of {type.Name}");
        }

        return value;
    }

    public override EnumItemInfo ReadEnumerated(Asn1Type type, EnumerationInfo enumeration)
    {
        var first = ReadOctet();
        BigInteger value;
        if ((first & 0x80) == 0)
        {
            value = first;
        }
        else
        {
            var n = first & 0x7F;
            if (n == 0)
            {
                throw Error(Asn1DecodeError.Malformed, "long-form enumerated value with no subsequent octets");
            }

            var octets = Input.ReadBytes(n);
            value = IntegerCodec.FromTwosComplement(octets);
            if (Canonical && (IntegerCodec.IsNonMinimalTwosComplement(octets) || (value >= 0 && value < 128)))
            {
                throw Error(Asn1DecodeError.NonCanonical, $"COER requires the short or minimal long form for enumerated value {value} (X.696 31.5)");
            }
        }

        if (value < long.MinValue || value > long.MaxValue)
        {
            throw Error(Asn1DecodeError.Malformed, $"enumerated value {value} is out of range");
        }

        return enumeration.FindByValue((long)value) ?? (enumeration.IsExtensible
            ? new EnumItemInfo(null, (long)value, true, -1)
            : throw Error(Asn1DecodeError.Malformed, $"{value} is not a value of {type.Name}"));
    }

    public override double ReadReal(RealType type)
    {
        if (OerVisibleConstraints.HasRealComponentConstraint(type.Constraints))
        {
            throw new Asn1UnsupportedException($"{RuleName} decoding of REAL with component constraints (IEEE 754 forms, X.696 12.2/12.3) is not implemented");
        }

        var content = ReadLengthPrefixed(1024);
        return RealCodec.Decode(content, canonical: true, out var error) ?? throw Error(Asn1DecodeError.Malformed, error!);
    }

    public override Asn1BitString ReadBitString(BitStringType type)
    {
        var info = OerTypeInfo.Get(type);
        if (info.FixedSize is { } fixedSize)
        {
            TrackAllocation((fixedSize + 7) / 8);
            var fixedBytes = Input.ReadBytes((fixedSize + 7) / 8);
            CheckPadding(fixedBytes, (int)((8 - (fixedSize % 8)) % 8));
            return new Asn1BitString(fixedBytes, fixedSize);
        }

        var content = ReadLengthPrefixed(Options.MaxStringLength);
        if (content.Length == 0)
        {
            throw Error(Asn1DecodeError.Malformed, "BIT STRING encoding without the unused-bits octet");
        }

        var unused = content[0];
        if (unused > 7 || (content.Length == 1 && unused != 0))
        {
            throw Error(Asn1DecodeError.Malformed, $"invalid unused-bits count {unused}");
        }

        var bytes = content[1..];
        CheckPadding(bytes, unused);
        var length = (bytes.Length * 8L) - unused;
        var value = new Asn1BitString(bytes, length);
        if (info.Sizes.LowerBound is { } lb && length < lb || info.Sizes.UpperBound is { } ub && length > ub)
        {
            throw Error(Asn1DecodeError.ConstraintViolation, $"BIT STRING of {length} bits violates the size constraint of {type.Name}");
        }

        if (Canonical && type.HasNamedBits && length > (long)(info.Sizes.LowerBound ?? 0) && !value[length - 1])
        {
            throw Error(Asn1DecodeError.NonCanonical, "COER forbids trailing zero bits in a BIT STRING with named bits (X.696 31.6)");
        }

        return value;
    }

    private void CheckPadding(byte[] bytes, int unused)
    {
        if (unused > 0 && bytes.Length > 0 && (bytes[^1] & ((1 << unused) - 1)) != 0)
        {
            if (Canonical)
            {
                throw Error(Asn1DecodeError.NonCanonical, "unused bits of a BIT STRING must be zero (X.696 13.2.2/13.3.3)");
            }

            bytes[^1] &= (byte)~((1 << unused) - 1);
        }
    }

    public override byte[] ReadOctetString(OctetStringType type)
    {
        var info = OerTypeInfo.Get(type);
        if (info.FixedSize is { } fixedSize)
        {
            TrackAllocation(fixedSize);
            return Input.ReadBytes(fixedSize);
        }

        var bytes = ReadLengthPrefixed(Options.MaxStringLength);
        if (info.Sizes.LowerBound is { } lb && bytes.Length < lb || info.Sizes.UpperBound is { } ub && bytes.Length > ub)
        {
            throw Error(Asn1DecodeError.ConstraintViolation, $"OCTET STRING of {bytes.Length} octets violates the size constraint of {type.Name}");
        }

        return bytes;
    }

    public override ObjectIdentifier ReadObjectIdentifier(ObjectIdentifierType type) =>
        ObjectIdentifierCodec.Decode(ReadLengthPrefixed(4096), out var error) ?? throw Error(Asn1DecodeError.Malformed, error!);

    public override RelativeObjectIdentifier ReadRelativeObjectIdentifier(RelativeOidType type) =>
        ObjectIdentifierCodec.DecodeRelative(ReadLengthPrefixed(4096), out var error) ?? throw Error(Asn1DecodeError.Malformed, error!);

    public override string ReadCharacterString(CharacterStringType type)
    {
        var info = OerTypeInfo.Get(type);
        byte[] bytes;
        if (info.FixedSize is { } fixedSize && CharacterRepertoire.IsKnownMultiplier(type.StringKind))
        {
            var octets = fixedSize * OerWriter.OctetsPerCharacter(type.StringKind);
            TrackAllocation(octets);
            bytes = Input.ReadBytes(octets);
        }
        else
        {
            bytes = ReadLengthPrefixed(Options.MaxStringLength);
        }

        return StringCodec.Decode(type.StringKind, bytes, out var error) ?? throw Error(Asn1DecodeError.Malformed, error!);
    }

    public override string ReadTime(TimeType type)
    {
        if (type.TimeKind is not (TimeKind.UtcTime or TimeKind.GeneralizedTime))
        {
            throw new Asn1UnsupportedException($"{RuleName} decoding of {TimeType.Keyword(type.TimeKind)} (X.696 clause 29) is not implemented");
        }

        var bytes = ReadLengthPrefixed(64);
        var value = StringCodec.Decode(CharacterStringKind.Visible, bytes, out var error) ?? throw Error(Asn1DecodeError.Malformed, error!);
        if (Canonical && !TimeSyntax.IsCanonical(type.TimeKind, value))
        {
            throw Error(Asn1DecodeError.NonCanonical, $"COER requires the canonical {TimeType.Keyword(type.TimeKind)} form (X.696 31.11); got '{value}'");
        }

        return value;
    }

    // ------------------------------------------------------------------ structured

    public override T ReadSequence<T>(SequenceType<T> type)
    {
        var info = OerTypeInfo.Get(type);
        var comps = type.Components;
        var value = type.CreateInstance();
        var present = ReadComponents(comps, value, info.RootOrder, type.IsExtensible, out var extended);
        if (extended)
        {
            // 16.4: extension addition presence bitmap.
            var bitmap = ReadLengthPrefixed(Options.MaxStringLength);
            if (bitmap.Length == 0 || bitmap[0] > 7 || (bitmap.Length == 1 && bitmap[0] != 0))
            {
                throw Error(Asn1DecodeError.Malformed, "invalid extension addition presence bitmap");
            }

            var n = ((bitmap.Length - 1) * 8L) - bitmap[0];
            var any = false;
            for (var a = 0L; a < n; a++)
            {
                if ((bitmap[1 + (a >> 3)] & (0x80 >> (int)(a & 7))) == 0)
                {
                    continue;
                }

                any = true;
                var bytes = ReadLengthPrefixed(Options.MaxStringLength);
                if (a >= info.Additions.Length)
                {
                    // Unknown addition from a later version of the specification: skipped.
                    continue;
                }

                var item = info.Additions[a];
                if (info.AdditionIsGroup[a])
                {
                    var groupPresent = DecodeNested(bytes, r => r.ReadComponents(comps, value, item, false, out _));
                    foreach (var (k, p) in groupPresent)
                    {
                        present[k] = p;
                    }
                }
                else
                {
                    DecodeNested(bytes, r =>
                    {
                        comps[item[0]].Decode(r, value);
                        return true;
                    });
                    present[item[0]] = true;
                }
            }

            if (Canonical && !any)
            {
                throw Error(Asn1DecodeError.NonCanonical, "COER: the extension bit is set but no extension addition is present");
            }
        }

        for (var i = 0; i < comps.Length; i++)
        {
            if (comps[i].Presence != Asn1Presence.Default)
            {
                continue;
            }

            if (!present.GetValueOrDefault(i))
            {
                comps[i].ApplyDefault(value);
            }
            else if (Canonical && comps[i].HasDefaultValue(value))
            {
                throw Error(Asn1DecodeError.NonCanonical, $"COER: component '{comps[i].Name}' is encoded with its DEFAULT value (X.696 31.9)");
            }
        }

        return value;
    }

    /// <summary>Reads a preamble (optional extension bit, presence bits, zero padding) followed by the present components.</summary>
    private Dictionary<int, bool> ReadComponents<T>(IReadOnlyList<SequenceComponent<T>> comps, T value, int[] order, bool extensible, out bool extended)
    {
        var present = new Dictionary<int, bool>();
        var bits = 0;
        extended = extensible && Input.ReadBit();
        bits += extensible ? 1 : 0;
        foreach (var i in order)
        {
            if (comps[i].IsOptional)
            {
                present[i] = Input.ReadBit();
                bits++;
            }
            else
            {
                present[i] = true;
            }
        }

        var padding = (8 - (bits % 8)) % 8;
        if (padding > 0 && Input.ReadBits(padding) != 0 && Canonical)
        {
            throw Error(Asn1DecodeError.NonCanonical, "unused preamble bits must be zero (X.696 16.2.4)");
        }

        foreach (var i in order)
        {
            if (present[i])
            {
                comps[i].Decode(this, value);
            }
        }

        return present;
    }

    public override List<TElement> ReadCollection<TElement>(CollectionType<TElement> type)
    {
        var quantity = ReadVariableUnsigned("quantity");
        CheckCollectionCount(quantity > long.MaxValue ? long.MaxValue : (long)quantity);
        var count = (long)quantity;
        var list = new List<TElement>();
        var checkOrder = Canonical && type.IsSet;
        byte[]? previous = null;
        for (long i = 0; i < count; i++)
        {
            if ((i & 1023) == 0)
            {
                Options.CancellationToken.ThrowIfCancellationRequested();
            }

            var start = Input.BitPosition;
            list.Add(type.DecodeElement(this, list.Count));
            if (checkOrder)
            {
                var encoding = Input.Data.Slice((int)(start / 8), (int)((Input.BitPosition - start) / 8)).ToArray();
                if (previous is not null && OerWriter.CompareEncodings(previous, encoding) > 0)
                {
                    throw Error(Asn1DecodeError.NonCanonical, "COER requires SET OF elements in ascending order of their encodings (X.696 31.8)");
                }

                previous = encoding;
            }
        }

        return list;
    }

    private Asn1Tag ReadTag()
    {
        var first = ReadOctet();
        var cls = (TagClass)(first >> 6);
        var number = first & 0x3F;
        if (number < 63)
        {
            return new Asn1Tag(cls, number);
        }

        long n = 0;
        var firstSubsequent = true;
        while (true)
        {
            var b = ReadOctet();
            if (firstSubsequent && (b & 0x7F) == 0)
            {
                throw Error(Asn1DecodeError.InvalidTag, "tag number with a leading zero septet (X.696 8.7.2.3 c)");
            }

            firstSubsequent = false;
            n = (n << 7) | (uint)(b & 0x7F);
            if (n > int.MaxValue)
            {
                throw Error(Asn1DecodeError.InvalidTag, "tag number too large");
            }

            if ((b & 0x80) == 0)
            {
                break;
            }
        }

        if (n < 63)
        {
            throw Error(Asn1DecodeError.InvalidTag, $"tag number {n} must use the short form (X.696 8.7.2.2)");
        }

        return new Asn1Tag(cls, (int)n);
    }

    public override T ReadChoice<T>(ChoiceType<T> type)
    {
        var tagStart = Input.BitPosition;
        var tag = ReadTag();
        var alternatives = type.Alternatives;
        for (var i = 0; i < alternatives.Length; i++)
        {
            var alt = alternatives[i];
            if (!(TagSets.PossibleTags(alt.Type)?.Contains(tag) ?? false))
            {
                continue;
            }

            if (alt.IsExtensionAddition)
            {
                var bytes = ReadLengthPrefixed(Options.MaxStringLength);
                return DecodeNested(bytes, r => alt.Decode(r));
            }

            // 20.1 note 3: for an untagged CHOICE alternative the nested CHOICE encoding repeats the tag just read.
            return alt.Decode(this);
        }

        if (type.IsExtensible)
        {
            // An unknown extension alternative: kept as its complete encoding (tag, length, contents) for relaying.
            var contents = ReadLengthPrefixed(Options.MaxStringLength);
            var complete = Input.Data.Slice((int)(tagStart / 8), (int)((Input.BitPosition - tagStart) / 8)).ToArray();
            _ = contents;
            if (type.SupportsUnknownExtensions)
            {
                return type.CreateUnknown(-1, Asn1OpenValue.FromEncoded(complete, RuleName));
            }

            throw Error(Asn1DecodeError.Unsupported, $"unknown extension alternative with tag {tag} in {type.Name}");
        }

        throw Error(Asn1DecodeError.InvalidTag, $"tag {tag} does not identify an alternative of {type.Name}");
    }

    public override Asn1OpenValue ReadOpenType(OpenType type) => Asn1OpenValue.FromEncoded(ReadLengthPrefixed(Options.MaxStringLength), RuleName);
}
