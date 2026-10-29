using System.Collections.Immutable;
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

    [ThreadStatic]
    private static PerReader? t_aligned;

    [ThreadStatic]
    private static PerReader? t_unaligned;

    // Readers for open type fields, shared by the root reader and all its nested readers.
    private readonly Stack<PerReader> _nested;
    private long _baseBit;

    public PerReader(BitReader input, bool aligned, string ruleName, Asn1DecodingOptions options)
        : base(options)
    {
        Input = input;
        Aligned = aligned;
        RuleName = ruleName;
        _nested = new Stack<PerReader>();
    }

    private PerReader(PerReader parent, BitReader input, long baseBit)
        : base(parent)
    {
        Input = input;
        Aligned = parent.Aligned;
        RuleName = parent.RuleName;
        _baseBit = baseBit;
        _nested = parent._nested;
    }

    public BitReader Input { get; }

    public bool Aligned { get; }

    public string RuleName { get; }

    public override long BitPosition => _baseBit + Input.BitPosition;

    /// <summary>A root reader for one decoding, reusing this thread's previous one when it has the same options.</summary>
    public static PerReader Rent(ReadOnlyMemory<byte> data, bool aligned, string ruleName, Asn1DecodingOptions options)
    {
        var cached = aligned ? t_aligned : t_unaligned;
        if (cached is null || !ReferenceEquals(cached.Options, options))
        {
            return new PerReader(new BitReader(data), aligned, ruleName, options);
        }

        // Taken out of the slot, so a decoding started while this one runs gets its own reader.
        if (aligned)
        {
            t_aligned = null;
        }
        else
        {
            t_unaligned = null;
        }

        cached.ResetForReuse();
        cached.Input.Reset(data);
        return cached;
    }

    /// <summary>Makes a reader from <see cref="Rent"/> available again; it no longer references the input.</summary>
    public void Return()
    {
        Input.Reset(ReadOnlyMemory<byte>.Empty);
        if (Aligned)
        {
            t_aligned = this;
        }
        else
        {
            t_unaligned = this;
        }
    }

    private PerTypeInfo Info(Asn1Type type) => PerTypeInfo.Get(type, Aligned);

    private void Align()
    {
        if (Aligned)
        {
            Input.Align();
        }
    }

    // ------------------------------------------------------------------ procedures

    /// <summary>11.5: constrained whole number lb..ub.</summary>
    private long ReadConstrainedWholeNumber(long lb, long ub)
    {
        var rangeMinusOne = (ulong)(ub - lb);
        if (rangeMinusOne == 0)
        {
            return lb;
        }

        ulong offset;
        if (!Aligned || rangeMinusOne < 255)
        {
            offset = Input.ReadBits(64 - BitOperations.LeadingZeroCount(rangeMinusOne));
        }
        else if (rangeMinusOne == 255)
        {
            Input.Align();
            offset = Input.ReadBits(8);
        }
        else if (rangeMinusOne < K64)
        {
            Input.Align();
            offset = Input.ReadBits(16);
        }
        else
        {
            var len = (int)ReadConstrainedWholeNumber(1, IntegerCodec.UnsignedLength(rangeMinusOne));
            Input.Align();
            offset = Input.ReadBits(len * 8);
        }

        if (offset > rangeMinusOne)
        {
            throw Error(Asn1DecodeError.Malformed, $"constrained whole number offset {offset} exceeds the range {lb}..{ub}");
        }

        return lb + (long)offset;
    }

    /// <summary>11.5 for bounds beyond 64 bits.</summary>
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

    /// <summary>11.6: normally small non-negative whole number (saturated to <see cref="long.MaxValue"/>).</summary>
    private long ReadNormallySmallNumber()
    {
        if (!Input.ReadBit())
        {
            return (long)Input.ReadBits(6);
        }

        var n = ReadSemiConstrainedWholeNumber(0);
        return n > long.MaxValue ? long.MaxValue : (long)n;
    }

    /// <summary>
    /// Reads an integer of 1..8 octets preceded by a one-octet length (11.9.3.6), the form of every 64-bit value; returns
    /// false, at the original position, for any other length so that the general procedure reports it.
    /// </summary>
    private bool TryReadShortOctets(out ulong octets, out int length)
    {
        var start = Input.BitPosition;
        Align();
        if (Input.RemainingBits >= 8)
        {
            var n = (int)Input.ReadBits(8);
            if (n is >= 1 and <= 8 && n <= Options.MaxIntegerLength)
            {
                Align();
                octets = Input.ReadBits(n * 8);
                length = n;
                return true;
            }
        }

        Input.Seek(start);
        octets = 0;
        length = 0;
        return false;
    }

    private bool TryReadSemiConstrained(long lb, out long value, out BigInteger large)
    {
        if (!TryReadShortOctets(out var octets, out _))
        {
            large = ReadSemiConstrainedWholeNumber(lb);
            return IntegerCodec.TryToInt64(large, out value);
        }

        var sum = (Int128)lb + octets;
        if (sum >= long.MinValue && sum <= long.MaxValue)
        {
            value = (long)sum;
            large = default;
            return true;
        }

        value = 0;
        large = (BigInteger)sum;
        return false;
    }

    private bool TryReadUnconstrained(out long value, out BigInteger large)
    {
        if (!TryReadShortOctets(out var octets, out var length))
        {
            large = ReadUnconstrainedWholeNumber();
            return IntegerCodec.TryToInt64(large, out value);
        }

        // Sign-extend the two's complement value.
        var shift = 64 - (length * 8);
        value = (long)(octets << shift) >> shift;
        large = default;
        return true;
    }

    /// <summary>Reads count units of a length-prefixed content.</summary>
    private interface IContent
    {
        void Read(PerReader reader, long count);
    }

    /// <summary>Collects length-prefixed octets; only fragmented content needs more than the result array.</summary>
    private struct Octets(int maxLength) : IContent
    {
        private byte[]? _first;
        private List<byte[]>? _more;
        private long _total;

        public void Read(PerReader reader, long count)
        {
            if (_total + count > maxLength)
            {
                throw reader.Error(Asn1DecodeError.LimitExceeded, $"value of more than {maxLength} octets");
            }

            reader.Align();
            var bytes = reader.Input.ReadBytes(count);
            if (_first is null)
            {
                _first = bytes;
            }
            else
            {
                (_more ??= []).Add(bytes);
            }

            _total += count;
        }

        public readonly byte[] ToArray()
        {
            if (_more is null)
            {
                return _first ?? [];
            }

            var result = new byte[_total];
            _first!.CopyTo(result, 0);
            var at = _first.Length;
            foreach (var part in _more)
            {
                part.CopyTo(result, at);
                at += part.Length;
            }

            return result;
        }
    }

    /// <summary>Records the length of the (last) fragment without reading content.</summary>
    private struct LengthOnly : IContent
    {
        public long Count { get; private set; }

        public void Read(PerReader reader, long count) => Count = count;
    }

    private byte[] ReadLengthPrefixedOctets(long lb, long? ub, int maxLength)
    {
        var content = new Octets(maxLength);
        ReadLengthAndContent(lb, ub, ref content);
        return content.ToArray();
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
    private long ReadLengthAndContent<TContent>(long lb, long? ub, ref TContent content)
        where TContent : IContent, allows ref struct
    {
        if (ub is { } u && u < K64)
        {
            var n = ReadConstrainedWholeNumber(lb, u);
            if (n > 0)
            {
                content.Read(this, n);
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
                    content.Read(this, first);
                }

                return total + first;
            }

            if ((first & 0x40) == 0)
            {
                var n = ((first & 0x3F) << 8) | (int)Input.ReadBits(8);
                Check(total + n, lb, ub);
                content.Read(this, n);
                return total + n;
            }

            var m = first & 0x3F;
            if (m is < 1 or > 4)
            {
                throw Error(Asn1DecodeError.InvalidLength, $"invalid fragment multiplier {m}");
            }

            var count = m * (long)K16;
            Check(total + count, 0, ub);
            content.Read(this, count);
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

        var length = new LengthOnly();
        ReadLengthAndContent(0, null, ref length);
        var n = length.Count;
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

    /// <summary>A reader for the contents of an open type field, to pass to <see cref="EndNested"/>.</summary>
    private PerReader BeginNested(byte[] bytes)
    {
        var baseBit = BitPosition - (bytes.Length * 8L);
        if (!_nested.TryPop(out var nested))
        {
            return new PerReader(this, new BitReader(bytes), baseBit);
        }

        nested.Input.Reset(bytes);
        nested._baseBit = baseBit;
        nested.ContinueFrom(this);
        return nested;
    }

    /// <summary>Checks that <paramref name="nested"/> consumed its open type field.</summary>
    private void EndNested(PerReader nested)
    {
        if (nested.Input.RemainingBits >= 8)
        {
            throw Error(Asn1DecodeError.Malformed, $"{nested.Input.RemainingBits / 8} unused octet(s) inside an open type field");
        }

        nested.Input.Reset(ReadOnlyMemory<byte>.Empty);
        _nested.Push(nested);
    }

    // ------------------------------------------------------------------ primitives

    public override bool ReadBoolean(BooleanType type) => Input.ReadBit();

    public override void ReadNull(NullType type)
    {
    }

    public override bool TryReadInt64(Asn1Type type, out long value, out BigInteger large)
    {
        var info = Info(type);
        if (info.IntegerForm == PerIntegerForm.Big)
        {
            return base.TryReadInt64(type, out value, out large);
        }

        if (info.Values.IsExtensible && Input.ReadBit())
        {
            return TryReadUnconstrained(out value, out large);
        }

        switch (info.IntegerForm)
        {
            case PerIntegerForm.Constrained:
                value = ReadConstrainedWholeNumber(info.Lower, info.Upper);
                large = default;
                return true;
            case PerIntegerForm.SemiConstrained:
                return TryReadSemiConstrained(info.Lower, out value, out large);
            default:
                return TryReadUnconstrained(out value, out large);
        }
    }

    public override BigInteger ReadInteger(Asn1Type type)
    {
        var info = Info(type);
        if (info.IntegerForm != PerIntegerForm.Big)
        {
            return TryReadInt64(type, out var value, out var large) ? value : large;
        }

        var c = info.Values;
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
            return enumeration.AdditionAt(Math.Min(ReadNormallySmallNumber(), int.MaxValue));
        }

        var root = ReadConstrainedWholeNumber(0, enumeration.Root.Length - 1);
        return enumeration.RootAt(root) ?? throw Error(Asn1DecodeError.Malformed, $"enumeration index {root} out of range");
    }

    public override double ReadReal(RealType type)
    {
        var content = ReadLengthPrefixedOctets(0, null, 1024);
        return RealCodec.Decode(content, canonical: true, out var error) ?? throw Error(Asn1DecodeError.Malformed, error!);
    }

    /// <summary>Collects the fragments of a BIT STRING.</summary>
    private struct BitParts : IContent
    {
        private (byte[] Bytes, long Bits) _first;
        private List<(byte[] Bytes, long Bits)>? _more;

        public long Total { get; private set; }

        public void Read(PerReader reader, long count)
        {
            Total += count;
            reader.TrackAllocation((count + 7) / 8);
            reader.Align();
            var part = (reader.Input.ReadBitString(count), count);
            if (_first.Bytes is null)
            {
                _first = part;
            }
            else
            {
                (_more ??= []).Add(part);
            }
        }

        public readonly Asn1BitString ToBitString()
        {
            if (_more is null)
            {
                return _first.Bytes is null ? new Asn1BitString([], 0) : new Asn1BitString(_first.Bytes, _first.Bits);
            }

            var all = new byte[(Total + 7) / 8];
            long bit = 0;
            foreach (var (bytes, bits) in (IEnumerable<(byte[], long)>)[_first, .. _more])
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

            return new Asn1BitString(all, Total);
        }
    }

    public override Asn1BitString ReadBitString(BitStringType type)
    {
        var info = Info(type);
        var lb = info.SizeLower;
        var ub = info.SizeUpper;
        if (info.Sizes.IsExtensible && Input.ReadBit())
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

        var parts = new BitParts();
        ReadLengthAndContent(lb, ub, ref parts);
        return parts.ToBitString();
    }

    public override byte[] ReadOctetString(OctetStringType type)
    {
        var info = Info(type);
        var lb = info.SizeLower;
        var ub = info.SizeUpper;
        if (info.Sizes.IsExtensible && Input.ReadBit())
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

        return ReadKnownMultiplier(info.Sizes, info.SizeLower, info.SizeUpper, alphabet);
    }

    /// <summary>Reads the characters of a known-multiplier string.</summary>
    private readonly struct Characters(AlphabetInfo alphabet, StringBuilder text, bool align) : IContent
    {
        public void Read(PerReader reader, long count)
        {
            if (align)
            {
                reader.Align();
            }

            var b = alphabet.Bits;
            if (count * Math.Max(b, 1) > reader.Input.RemainingBits && b > 0)
            {
                throw reader.Error(Asn1DecodeError.Truncated, $"{count} characters of {b} bits exceed the remaining input");
            }

            reader.TrackAllocation(count * 2);
            text.EnsureCapacity(text.Length + (int)count);
            Span<char> pair = stackalloc char[2];
            for (long i = 0; i < count; i++)
            {
                var v = (long)reader.Input.ReadBits(b);
                var cp = alphabet.Decode(v);
                if (cp < 0 || cp > 0x10FFFF || (cp >= 0xD800 && cp <= 0xDFFF))
                {
                    throw reader.Error(Asn1DecodeError.Malformed, $"character value {v} is not in the permitted alphabet");
                }

                if (cp <= 0xFFFF)
                {
                    text.Append((char)cp);
                }
                else
                {
                    new Rune((int)cp).EncodeToUtf16(pair);
                    text.Append(pair);
                }
            }
        }
    }

    private string ReadKnownMultiplier(EffectiveConstraint sizes, long alb, long? aub, AlphabetInfo alphabet)
    {
        if (sizes.IsExtensible && Input.ReadBit())
        {
            alb = 0;
            aub = null;
        }

        var b = alphabet.Bits;
        var text = new StringBuilder();
        if (aub is { } fixedLength && fixedLength == alb && fixedLength < K64)
        {
            if (fixedLength * b > 16)
            {
                Align();
            }

            new Characters(alphabet, text, align: false).Read(this, fixedLength);
            return text.ToString();
        }

        var content = new Characters(alphabet, text, align: aub is null || aub.Value * b >= 16);
        ReadLengthAndContent(alb, aub, ref content);
        return text.ToString();
    }

    public override string ReadTime(TimeType type)
    {
        if (type.TimeKind is not (TimeKind.UtcTime or TimeKind.GeneralizedTime))
        {
            throw new Asn1UnsupportedException($"{RuleName} decoding of {TimeType.Keyword(type.TimeKind)} (X.691 clause 32) is not implemented");
        }

        var value = ReadKnownMultiplier(EffectiveConstraint.None, 0, null, new AlphabetInfo(AlphabetInfo.Repertoire(CharacterStringKind.Visible), Aligned));
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
        Span<bool> present = comps.Length <= 128 ? stackalloc bool[comps.Length] : new bool[comps.Length];
        ReadComponents(comps, value, info.RootOrder, present);

        if (extended)
        {
            var n = ReadNormallySmallLength();
            Span<bool> bitmap = n <= 128 ? stackalloc bool[n] : new bool[n];
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
                var nested = BeginNested(bytes);
                if (info.AdditionIsGroup[a])
                {
                    nested.ReadComponents(comps, value, item, present);
                }
                else
                {
                    comps[item[0]].Decode(nested, value);
                    present[item[0]] = true;
                }

                EndNested(nested);
            }
        }

        for (var i = 0; i < comps.Length; i++)
        {
            if (comps[i].Presence == Asn1Presence.Default && !present[i])
            {
                comps[i].ApplyDefault(value);
            }
        }

        return value;
    }

    private void ReadComponents<T>(ImmutableArray<SequenceComponent<T>> comps, T value, int[] order, Span<bool> present)
    {
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
    }

    /// <summary>Reads collection elements.</summary>
    private readonly struct Elements<TElement>(CollectionType<TElement> type) : IContent
    {
        public List<TElement> List { get; } = [];

        public void Read(PerReader reader, long count)
        {
            var list = List;
            reader.CheckCollectionCount(list.Count + count);

            // Bounded so that a large announced count is not allocated before its elements have been read.
            list.EnsureCapacity(list.Count + (int)Math.Min(count, 1024));
            for (long i = 0; i < count; i++)
            {
                if ((list.Count & 1023) == 0)
                {
                    reader.Options.CancellationToken.ThrowIfCancellationRequested();
                }

                list.Add(type.DecodeElement(reader, list.Count));
            }
        }
    }

    public override List<TElement> ReadCollection<TElement>(CollectionType<TElement> type)
    {
        var info = Info(type);
        var lb = info.SizeLower;
        long? ub = info.SizeUpper is { } u && u < K64 ? u : null;
        if (info.Sizes.IsExtensible && Input.ReadBit())
        {
            lb = 0;
            ub = null;
        }

        var content = new Elements<TElement>(type);
        if (ub is { } fixedCount && fixedCount == lb)
        {
            content.Read(this, fixedCount);
            return content.List;
        }

        ReadLengthAndContent(lb, ub, ref content);
        return content.List;
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
                var nested = BeginNested(bytes);
                var value = alt.Decode(nested);
                EndNested(nested);
                return value;
            }

            if (type.SupportsUnknownExtensions)
            {
                return type.CreateUnknown((int)Math.Min(index, int.MaxValue), Asn1OpenValue.FromEncoded(bytes, RuleName));
            }

            throw Error(Asn1DecodeError.Unsupported, $"unknown extension alternative #{index} of {type.Name}");
        }

        var rootIndex = (int)ReadConstrainedWholeNumber(0, info.RootAlternatives.Length - 1);
        return type.Alternatives[info.RootAlternatives[rootIndex]].Decode(this);
    }

    public override Asn1OpenValue ReadOpenType(OpenType type) => Asn1OpenValue.FromEncoded(ReadOpenTypeField(), RuleName);
}
