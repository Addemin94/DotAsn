using System.Numerics;
using System.Text;
using Asn1.Runtime.Buffers;
using Asn1.Runtime.Primitives;

namespace Asn1.Runtime.Per;

/// <summary>Reader for BASIC-PER (aligned and unaligned variants). All reads are bounds-checked by <see cref="BitReader"/>.</summary>
internal sealed class PerReader : Asn1Reader
{
    private const int K16 = 16384;
    private const int K64 = 65536;
    private readonly long _baseBit;

    public PerReader(BitReader input, bool aligned, string ruleName, Asn1DecodingOptions options)
        : base(options)
    {
        Input = input;
        Aligned = aligned;
        RuleName = ruleName;
    }

    private PerReader(PerReader parent, BitReader input, long baseBit)
        : base(parent)
    {
        Input = input;
        Aligned = parent.Aligned;
        RuleName = parent.RuleName;
        _baseBit = baseBit;
    }

    public BitReader Input { get; }

    public bool Aligned { get; }

    public string RuleName { get; }

    public override long BitPosition => _baseBit + Input.BitPosition;

    private PerTypeInfo Info(Asn1Type type) => PerTypeInfo.Get(type, Aligned);

    private void Align()
    {
        if (Aligned)
        {
            Input.Align();
        }
    }

    // ------------------------------------------------------------------ procedures

    private BigInteger ReadConstrainedWholeNumber(BigInteger lb, BigInteger ub)
    {
        var range = ub - lb + 1;
        if (range == 1)
        {
            return lb;
        }

        BigInteger offset;
        if (!Aligned || range <= 255)
        {
            offset = Input.ReadBigBits(IntegerCodec.BitsForRange(range));
        }
        else if (range == 256)
        {
            Input.Align();
            offset = Input.ReadBits(8);
        }
        else if (range <= K64)
        {
            Input.Align();
            offset = Input.ReadBits(16);
        }
        else
        {
            var maxOctets = IntegerCodec.UnsignedLength(range - 1);
            var len = (int)ReadConstrainedWholeNumber(1, maxOctets);
            Input.Align();
            offset = IntegerCodec.FromUnsigned(Input.ReadBytes(len));
        }

        if (offset >= range)
        {
            throw Error(Asn1DecodeError.Malformed, $"constrained whole number offset {offset} exceeds the range {lb}..{ub}");
        }

        return lb + offset;
    }

    private BigInteger ReadNormallySmallNumber()
    {
        if (!Input.ReadBit())
        {
            return Input.ReadBits(6);
        }

        return ReadSemiConstrainedWholeNumber(0);
    }

    private byte[] ReadLengthPrefixedOctets(long lb, long? ub, int maxLength)
    {
        var output = new List<byte>();
        ReadLengthAndContent(lb, ub, count =>
        {
            if (output.Count + count > maxLength)
            {
                throw Error(Asn1DecodeError.LimitExceeded, $"value of more than {maxLength} octets");
            }

            Align();
            output.AddRange(Input.ReadBytes(count));
        });
        return [.. output];
    }

    private BigInteger ReadSemiConstrainedWholeNumber(BigInteger lb)
    {
        var octets = ReadLengthPrefixedOctets(0, null, Options.MaxIntegerLength);
        if (octets.Length == 0)
        {
            throw Error(Asn1DecodeError.Malformed, "empty integer encoding");
        }

        return lb + IntegerCodec.FromUnsigned(octets);
    }

    private BigInteger ReadUnconstrainedWholeNumber()
    {
        var octets = ReadLengthPrefixedOctets(0, null, Options.MaxIntegerLength);
        if (octets.Length == 0)
        {
            throw Error(Asn1DecodeError.Malformed, "empty integer encoding");
        }

        return IntegerCodec.FromTwosComplement(octets);
    }

    /// <summary>11.9: reads a length determinant and its content (with fragments); returns the total count.</summary>
    private long ReadLengthAndContent(long lb, long? ub, Action<long> readChunk)
    {
        if (ub is { } u && u < K64)
        {
            var n = (long)ReadConstrainedWholeNumber(lb, u);
            if (n > 0)
            {
                readChunk(n);
            }

            return n;
        }

        long total = 0;
        while (true)
        {
            Align();
            var first = (int)Input.ReadBits(8);
            if ((first & 0x80) == 0)
            {
                Check(total + first, lb, ub);
                if (first > 0)
                {
                    readChunk(first);
                }

                return total + first;
            }

            if ((first & 0x40) == 0)
            {
                var n = ((first & 0x3F) << 8) | (int)Input.ReadBits(8);
                Check(total + n, lb, ub);
                readChunk(n);
                return total + n;
            }

            var m = first & 0x3F;
            if (m is < 1 or > 4)
            {
                throw Error(Asn1DecodeError.InvalidLength, $"invalid fragment multiplier {m}");
            }

            var count = m * (long)K16;
            Check(total + count, 0, ub);
            readChunk(count);
            total += count;
        }
    }

    private void Check(long n, long lb, long? ub)
    {
        if (n < lb || (ub is { } u && n > u))
        {
            throw Error(Asn1DecodeError.InvalidLength, $"length {n} is outside {lb}..{(ub?.ToString() ?? "MAX")}");
        }
    }

    private int ReadNormallySmallLength()
    {
        if (!Input.ReadBit())
        {
            return (int)Input.ReadBits(6) + 1;
        }

        long n = 0;
        ReadLengthAndContent(0, null, c => n = c);
        if (n is < 1 or > 1 << 20)
        {
            throw Error(Asn1DecodeError.InvalidLength, $"invalid normally small length {n}");
        }

        return (int)n;
    }

    /// <summary>Reads an open type field and returns its contents.</summary>
    private byte[] ReadOpenTypeField()
    {
        var bytes = ReadLengthPrefixedOctets(0, null, Options.MaxStringLength);
        TrackAllocation(bytes.Length);
        return bytes;
    }

    private PerReader Nested(byte[] bytes) => new(this, new BitReader(bytes), BitPosition - (bytes.Length * 8L));

    private T DecodeNested<T>(byte[] bytes, Func<PerReader, T> decode)
    {
        var reader = Nested(bytes);
        var value = decode(reader);
        if (reader.Input.RemainingBits >= 8)
        {
            throw Error(Asn1DecodeError.Malformed, $"{reader.Input.RemainingBits / 8} unused octet(s) inside an open type field");
        }

        return value;
    }

    // ------------------------------------------------------------------ primitives

    public override bool ReadBoolean(BooleanType type) => Input.ReadBit();

    public override void ReadNull(NullType type)
    {
    }

    public override BigInteger ReadInteger(Asn1Type type)
    {
        var c = Info(type).Values;
        if (c.IsExtensible && Input.ReadBit())
        {
            return ReadUnconstrainedWholeNumber();
        }

        if (c.LowerBound is { } lb && c.UpperBound is { } ub)
        {
            return ReadConstrainedWholeNumber(lb, ub);
        }

        return c.LowerBound is { } lower ? ReadSemiConstrainedWholeNumber(lower) : ReadUnconstrainedWholeNumber();
    }

    public override EnumItemInfo ReadEnumerated(Asn1Type type, EnumerationInfo enumeration)
    {
        if (enumeration.IsExtensible && Input.ReadBit())
        {
            var index = ReadNormallySmallNumber();
            return enumeration.AdditionAt(index > int.MaxValue ? int.MaxValue : (long)index);
        }

        var root = ReadConstrainedWholeNumber(0, enumeration.Root.Length - 1);
        return enumeration.RootAt((long)root) ?? throw Error(Asn1DecodeError.Malformed, $"enumeration index {root} out of range");
    }

    public override double ReadReal(RealType type)
    {
        var content = ReadLengthPrefixedOctets(0, null, 1024);
        return RealCodec.Decode(content, canonical: true, out var error) ?? throw Error(Asn1DecodeError.Malformed, error!);
    }

    public override Asn1BitString ReadBitString(BitStringType type)
    {
        var c = Info(type).Sizes;
        var lb = (long)(c.LowerBound ?? 0);
        long? ub = c.UpperBound is { } u ? (u > long.MaxValue ? long.MaxValue : (long)u) : null;
        if (c.IsExtensible && Input.ReadBit())
        {
            lb = 0;
            ub = null;
        }
        else if (ub == 0)
        {
            return Asn1BitString.Empty;
        }
        else if (ub is { } fixedSize && fixedSize == lb && fixedSize < K64)
        {
            if (fixedSize > 16)
            {
                Align();
            }

            TrackAllocation((fixedSize + 7) / 8);
            return new Asn1BitString(Input.ReadBitString(fixedSize), fixedSize);
        }

        var parts = new List<(byte[] Bytes, long Bits)>();
        long total = 0;
        ReadLengthAndContent(lb, ub, count =>
        {
            total += count;
            TrackAllocation((count + 7) / 8);
            Align();
            parts.Add((Input.ReadBitString(count), count));
        });
        if (parts.Count == 1)
        {
            return new Asn1BitString(parts[0].Bytes, parts[0].Bits);
        }

        var all = new byte[(total + 7) / 8];
        long bit = 0;
        foreach (var (bytes, bits) in parts)
        {
            var piece = new Asn1BitString(bytes, bits);
            for (long i = 0; i < bits; i++, bit++)
            {
                if (piece[i])
                {
                    all[bit >> 3] |= (byte)(0x80 >> (int)(bit & 7));
                }
            }
        }

        return new Asn1BitString(all, total);
    }

    public override byte[] ReadOctetString(OctetStringType type)
    {
        var c = Info(type).Sizes;
        var lb = (long)(c.LowerBound ?? 0);
        long? ub = c.UpperBound is { } u ? (u > long.MaxValue ? long.MaxValue : (long)u) : null;
        if (c.IsExtensible && Input.ReadBit())
        {
            lb = 0;
            ub = null;
        }
        else if (ub == 0)
        {
            return [];
        }
        else if (ub is { } fixedSize && fixedSize == lb && fixedSize < K64)
        {
            if (fixedSize > 2)
            {
                Align();
            }

            TrackAllocation(fixedSize);
            return Input.ReadBytes(fixedSize);
        }

        var bytes = ReadLengthPrefixedOctets(lb, ub, Options.MaxStringLength);
        TrackAllocation(bytes.Length);
        return bytes;
    }

    public override ObjectIdentifier ReadObjectIdentifier(ObjectIdentifierType type) =>
        ObjectIdentifierCodec.Decode(ReadLengthPrefixedOctets(0, null, 4096), out var error) ?? throw Error(Asn1DecodeError.Malformed, error!);

    public override RelativeObjectIdentifier ReadRelativeObjectIdentifier(RelativeOidType type) =>
        ObjectIdentifierCodec.DecodeRelative(ReadLengthPrefixedOctets(0, null, 4096), out var error) ?? throw Error(Asn1DecodeError.Malformed, error!);

    public override string ReadCharacterString(CharacterStringType type)
    {
        var info = Info(type);
        if (info.Alphabet is not { } alphabet)
        {
            var bytes = ReadLengthPrefixedOctets(0, null, Options.MaxStringLength);
            TrackAllocation(bytes.Length);
            return StringCodec.Decode(type.StringKind, bytes, out var error) ?? throw Error(Asn1DecodeError.Malformed, error!);
        }

        return ReadKnownMultiplier(info.Sizes, alphabet);
    }

    private string ReadKnownMultiplier(EffectiveConstraint sizes, AlphabetInfo alphabet)
    {
        var alb = (long)(sizes.LowerBound ?? 0);
        long? aub = sizes.UpperBound is { } u ? (u > long.MaxValue ? long.MaxValue : (long)u) : null;
        if (sizes.IsExtensible && Input.ReadBit())
        {
            alb = 0;
            aub = null;
        }

        var b = alphabet.Bits;
        var sb = new StringBuilder();
        void ReadChars(long count)
        {
            if (count * Math.Max(b, 1) > Input.RemainingBits && b > 0)
            {
                throw Error(Asn1DecodeError.Truncated, $"{count} characters of {b} bits exceed the remaining input");
            }

            TrackAllocation(count * 2);
            for (long i = 0; i < count; i++)
            {
                var v = (long)Input.ReadBits(b);
                var cp = alphabet.Decode(v);
                if (cp < 0 || cp > 0x10FFFF || (cp >= 0xD800 && cp <= 0xDFFF))
                {
                    throw Error(Asn1DecodeError.Malformed, $"character value {v} is not in the permitted alphabet");
                }

                sb.Append(char.ConvertFromUtf32((int)cp));
            }
        }

        if (aub is { } fixedLength && fixedLength == alb && fixedLength < K64)
        {
            if (fixedLength * b > 16)
            {
                Align();
            }

            ReadChars(fixedLength);
            return sb.ToString();
        }

        var align = aub is null || aub.Value * b >= 16;
        ReadLengthAndContent(alb, aub, count =>
        {
            if (align)
            {
                Align();
            }

            ReadChars(count);
        });
        return sb.ToString();
    }

    public override string ReadTime(TimeType type)
    {
        if (type.TimeKind is not (TimeKind.UtcTime or TimeKind.GeneralizedTime))
        {
            throw new Asn1UnsupportedException($"{RuleName} decoding of {TimeType.Keyword(type.TimeKind)} (X.691 clause 32) is not implemented");
        }

        var value = ReadKnownMultiplier(EffectiveConstraint.None, new AlphabetInfo(AlphabetInfo.Repertoire(CharacterStringKind.Visible), Aligned));
        if (!TimeSyntax.IsCanonical(type.TimeKind, value))
        {
            throw Error(Asn1DecodeError.NonCanonical, $"PER requires the canonical {TimeType.Keyword(type.TimeKind)} form; got '{value}'");
        }

        return value;
    }

    // ------------------------------------------------------------------ structured

    public override T ReadSequence<T>(SequenceType<T> type)
    {
        var info = Info(type);
        var comps = type.Components;
        var value = type.CreateInstance();
        var extended = type.IsExtensible && Input.ReadBit();
        var present = ReadComponents(comps, value, info.RootOrder);

        if (extended)
        {
            var n = ReadNormallySmallLength();
            var bitmap = new bool[n];
            for (var i = 0; i < n; i++)
            {
                bitmap[i] = Input.ReadBit();
            }

            for (var a = 0; a < n; a++)
            {
                if (!bitmap[a])
                {
                    continue;
                }

                var bytes = ReadOpenTypeField();
                if (a >= info.Additions.Length)
                {
                    // Unknown addition from a later version: skipped (X.691 19.9).
                    continue;
                }

                var item = info.Additions[a];
                if (info.AdditionIsGroup[a])
                {
                    var groupPresent = DecodeNested(bytes, r => r.ReadComponents(comps, value, item));
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
        }

        for (var i = 0; i < comps.Length; i++)
        {
            if (comps[i].Presence == Asn1Presence.Default && !present.GetValueOrDefault(i))
            {
                comps[i].ApplyDefault(value);
            }
        }

        return value;
    }

    private Dictionary<int, bool> ReadComponents<T>(IReadOnlyList<SequenceComponent<T>> comps, T value, int[] order)
    {
        var present = new Dictionary<int, bool>();
        foreach (var i in order)
        {
            present[i] = !comps[i].IsOptional || Input.ReadBit();
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
        var c = Info(type).Sizes;
        var lb = (long)(c.LowerBound ?? 0);
        long? ub = c.UpperBound is { } u && u < K64 ? (long)u : null;
        if (c.IsExtensible && Input.ReadBit())
        {
            lb = 0;
            ub = null;
        }

        var list = new List<TElement>();
        void Elements(long count)
        {
            CheckCollectionCount(list.Count + count);
            for (long i = 0; i < count; i++)
            {
                if ((list.Count & 1023) == 0)
                {
                    Options.CancellationToken.ThrowIfCancellationRequested();
                }

                list.Add(type.DecodeElement(this, list.Count));
            }
        }

        if (ub is { } fixedCount && fixedCount == lb)
        {
            Elements(fixedCount);
            return list;
        }

        ReadLengthAndContent(lb, ub, Elements);
        return list;
    }

    public override T ReadChoice<T>(ChoiceType<T> type)
    {
        var info = Info(type);
        if (type.IsExtensible && Input.ReadBit())
        {
            var index = ReadNormallySmallNumber();
            var bytes = ReadOpenTypeField();
            if (index < info.AdditionAlternatives.Length)
            {
                var alt = type.Alternatives[info.AdditionAlternatives[(int)index]];
                return DecodeNested(bytes, r => alt.Decode(r));
            }

            if (type.SupportsUnknownExtensions)
            {
                return type.CreateUnknown((int)BigInteger.Min(index, int.MaxValue), Asn1OpenValue.FromEncoded(bytes, RuleName));
            }

            throw Error(Asn1DecodeError.Unsupported, $"unknown extension alternative #{index} of {type.Name}");
        }

        var rootIndex = (int)ReadConstrainedWholeNumber(0, info.RootAlternatives.Length - 1);
        return type.Alternatives[info.RootAlternatives[rootIndex]].Decode(this);
    }

    public override Asn1OpenValue ReadOpenType(OpenType type) => Asn1OpenValue.FromEncoded(ReadOpenTypeField(), RuleName);
}
