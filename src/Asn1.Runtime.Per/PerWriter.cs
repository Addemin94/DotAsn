using System.Buffers;
using System.Collections.Immutable;
using System.Numerics;
using System.Text;
using Asn1.Runtime.Buffers;
using Asn1.Runtime.Primitives;

namespace Asn1.Runtime.Per;

/// <summary>Writer for BASIC-PER (aligned and unaligned variants).</summary>
internal sealed class PerWriter : Asn1Writer
{
    private const int K16 = 16384;
    private const int K64 = 65536;

    // Reused writers keep their buffers; larger ones are dropped so a huge encoding does not pin its memory.
    private const int MaxReusedBytes = 64 * 1024;

    private static readonly byte[] s_zeroOctet = [0];

    [ThreadStatic]
    private static PerWriter? t_aligned;

    [ThreadStatic]
    private static PerWriter? t_unaligned;

    // Writers for open type fields (X.691 11.2), shared by the root writer and all its nested writers.
    private readonly Stack<PerWriter> _nested;

    public PerWriter(BitWriter output, bool aligned, string ruleName, Asn1EncodingOptions options)
        : base(options)
    {
        Output = output;
        Aligned = aligned;
        RuleName = ruleName;
        _nested = new Stack<PerWriter>();
    }

    private PerWriter(PerWriter parent)
        : base(parent)
    {
        Output = new BitWriter(64, parent.Options.MaxEncodedSize);
        Aligned = parent.Aligned;
        RuleName = parent.RuleName;
        _nested = parent._nested;
    }

    public BitWriter Output { get; }

    public bool Aligned { get; }

    public string RuleName { get; }

    /// <summary>A root writer for one encoding, reusing this thread's previous one when it has the same options.</summary>
    public static PerWriter Rent(bool aligned, string ruleName, Asn1EncodingOptions options)
    {
        var cached = aligned ? t_aligned : t_unaligned;
        if (cached is null || !ReferenceEquals(cached.Options, options))
        {
            return new PerWriter(new BitWriter(64, options.MaxEncodedSize), aligned, ruleName, options);
        }

        // Taken out of the slot, so an encoding started while this one runs gets its own writer.
        if (aligned)
        {
            t_aligned = null;
        }
        else
        {
            t_unaligned = null;
        }

        cached.ResetForReuse();
        cached.Output.Reset(options.MaxEncodedSize);
        return cached;
    }

    /// <summary>Makes a writer from <see cref="Rent"/> available again after its output has been copied.</summary>
    public void Return()
    {
        if (Output.Capacity > MaxReusedBytes)
        {
            return;
        }

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
            Output.Align();
        }
    }

    // ------------------------------------------------------------------ procedures (X.691 11)

    /// <summary>11.5: constrained whole number lb..ub.</summary>
    private void WriteConstrainedWholeNumber(long value, long lb, long ub)
    {
        // The offset is written in a fixed number of bits, so a value outside the range would be silently truncated.
        if (value < lb || value > ub)
        {
            throw Error($"{value} lies outside the PER-visible range {lb}..{ub}");
        }

        var rangeMinusOne = (ulong)(ub - lb);
        if (rangeMinusOne == 0)
        {
            return;
        }

        var offset = (ulong)(value - lb);
        if (!Aligned || rangeMinusOne < 255)
        {
            Output.WriteBits(offset, 64 - BitOperations.LeadingZeroCount(rangeMinusOne));
        }
        else if (rangeMinusOne == 255)
        {
            Output.Align();
            Output.WriteBits(offset, 8);
        }
        else if (rangeMinusOne < K64)
        {
            Output.Align();
            Output.WriteBits(offset, 16);
        }
        else
        {
            // 11.5.7.4 and 13.2.6 a): length in octets as a constrained whole number 1..octets(range), then the octets.
            var octets = IntegerCodec.UnsignedLength(offset);
            WriteConstrainedWholeNumber(octets, 1, IntegerCodec.UnsignedLength(rangeMinusOne));
            Output.Align();
            Output.WriteBits(offset, octets * 8);
        }
    }

    /// <summary>11.5 for bounds beyond 64 bits.</summary>
    private void WriteConstrainedWholeNumber(BigInteger value, BigInteger lb, BigInteger ub)
    {
        if (value < lb || value > ub)
        {
            throw Error($"{value} lies outside the PER-visible range {lb}..{ub}");
        }

        var range = ub - lb + 1;
        var offset = value - lb;
        if (range == 1)
        {
            return;
        }

        if (!Aligned || range <= 255)
        {
            Output.WriteBits(offset, IntegerCodec.BitsForRange(range));
        }
        else if (range == 256)
        {
            Output.Align();
            Output.WriteBits(offset, 8);
        }
        else if (range <= K64)
        {
            Output.Align();
            Output.WriteBits(offset, 16);
        }
        else
        {
            var octets = IntegerCodec.ToUnsigned(offset);
            WriteConstrainedWholeNumber(octets.Length, 1, IntegerCodec.UnsignedLength(range - 1));
            Output.Align();
            Output.WriteBytes(octets);
        }
    }

    /// <summary>11.6: normally small non-negative whole number.</summary>
    private void WriteNormallySmallNumber(long n)
    {
        if (n <= 63)
        {
            Output.WriteBit(false);
            Output.WriteBits((ulong)n, 6);
            return;
        }

        Output.WriteBit(true);
        WriteUnsignedOctets((ulong)n);
    }

    /// <summary>11.7 with 11.9: a non-negative binary integer in the minimum octets, preceded by an unconstrained length.</summary>
    private void WriteUnsignedOctets(ulong value)
    {
        var length = IntegerCodec.UnsignedLength(value);
        Align();
        Output.WriteBits((ulong)length, 8);
        Align();
        Output.WriteBits(value, length * 8);
    }

    /// <summary>11.8 with 11.9: unconstrained whole number (2's complement) preceded by an unconstrained length.</summary>
    private void WriteUnconstrainedWholeNumber(long value)
    {
        var length = IntegerCodec.TwosComplementLength(value);
        Align();
        Output.WriteBits((ulong)length, 8);
        Align();
        Output.WriteBits((ulong)value, length * 8);
    }

    private void WriteSemiConstrainedWholeNumber(BigInteger n, BigInteger lb) => WriteOctets(IntegerCodec.ToUnsigned(n - lb), 0, null);

    private void WriteUnconstrainedWholeNumber(BigInteger n) => WriteOctets(IntegerCodec.ToTwosComplement(n), 0, null);

    /// <summary>Writes units [offset, offset + count) of a length-prefixed content.</summary>
    private interface IContent
    {
        void Write(PerWriter writer, long offset, long count);
    }

    /// <summary>
    /// 11.9: writes a length determinant for <paramref name="n"/> units followed by the content, fragmenting at 16K
    /// units when the length is unconstrained or ub ≥ 64K.
    /// </summary>
    private void WriteLengthAndContent<TContent>(long n, long lb, long? ub, ref TContent content)
        where TContent : IContent, allows ref struct
    {
        if (ub is { } u && u < K64)
        {
            if (n < lb || n > u)
            {
                throw Error($"length {n} lies outside the PER-visible size {lb}..{u}");
            }

            WriteConstrainedWholeNumber(n, lb, u);
            if (n > 0)
            {
                content.Write(this, 0, n);
            }

            return;
        }

        long offset = 0;
        while (true)
        {
            var remaining = n - offset;
            Align();
            if (remaining < 128)
            {
                Output.WriteBits((ulong)remaining, 8);
                if (remaining > 0)
                {
                    content.Write(this, offset, remaining);
                }

                return;
            }

            if (remaining < K16)
            {
                Output.WriteBits(0x8000 | (ulong)remaining, 16);
                content.Write(this, offset, remaining);
                return;
            }

            var m = Math.Min(4, remaining / K16);
            Output.WriteBits(0xC0 | (ulong)m, 8);
            content.Write(this, offset, m * K16);
            offset += m * K16;
        }
    }

    private readonly ref struct Octets(ReadOnlySpan<byte> bytes) : IContent
    {
        private readonly ReadOnlySpan<byte> _bytes = bytes;

        public void Write(PerWriter writer, long offset, long count)
        {
            writer.Align();
            writer.Output.WriteBytes(_bytes.Slice((int)offset, (int)count));
        }
    }

    private readonly ref struct Bitmap(ReadOnlySpan<bool> bits) : IContent
    {
        private readonly ReadOnlySpan<bool> _bits = bits;

        public void Write(PerWriter writer, long offset, long count)
        {
            foreach (var bit in _bits.Slice((int)offset, (int)count))
            {
                writer.Output.WriteBit(bit);
            }
        }
    }

    private void WriteOctets(ReadOnlySpan<byte> bytes, long lb, long? ub)
    {
        var content = new Octets(bytes);
        WriteLengthAndContent(bytes.Length, lb, ub, ref content);
    }

    /// <summary>11.9.3.4 and 19.8: the extension bitmap, preceded by its normally small length.</summary>
    private void WriteBitmap(ReadOnlySpan<bool> bits)
    {
        if (bits.Length <= 64)
        {
            Output.WriteBit(false);
            Output.WriteBits((ulong)(bits.Length - 1), 6);
            foreach (var bit in bits)
            {
                Output.WriteBit(bit);
            }

            return;
        }

        Output.WriteBit(true);
        var content = new Bitmap(bits);
        WriteLengthAndContent(bits.Length, 0, null, ref content);
    }

    /// <summary>A writer for the complete encoding of an open type field's value (11.2), to pass to <see cref="EndOpenType"/>.</summary>
    private PerWriter BeginOpenType()
    {
        if (_nested.TryPop(out var nested))
        {
            nested.ContinueFrom(this);
            nested.Output.Reset(Options.MaxEncodedSize);
            return nested;
        }

        return new PerWriter(this);
    }

    /// <summary>Writes the open type field encoded by <paramref name="nested"/> as a length-prefixed octet string.</summary>
    private void EndOpenType(PerWriter nested)
    {
        // 11.1: an empty encoding is one zero octet.
        WriteOctets(nested.Output.BitPosition == 0 ? s_zeroOctet : nested.Output.WrittenSpan, 0, null);
        if (nested.Output.Capacity <= MaxReusedBytes)
        {
            _nested.Push(nested);
        }
    }

    // ------------------------------------------------------------------ primitives

    public override void WriteBoolean(BooleanType type, bool value) => Output.WriteBit(value);

    public override void WriteNull(NullType type)
    {
    }

    public override void WriteInteger(Asn1Type type, long value)
    {
        var info = Info(type);
        if (info.IntegerForm == PerIntegerForm.Big)
        {
            WriteInteger(type, (BigInteger)value);
            return;
        }

        var c = info.Values;
        if (c.IsExtensible)
        {
            var inRoot = c.Root.Contains(value);
            Output.WriteBit(!inRoot);
            if (!inRoot)
            {
                WriteUnconstrainedWholeNumber(value);
                return;
            }
        }

        switch (info.IntegerForm)
        {
            case PerIntegerForm.Constrained:
                if (value < info.Lower || value > info.Upper)
                {
                    throw Error($"INTEGER value {value} lies outside the PER-visible range {info.Lower}..{info.Upper} of {type.Name}");
                }

                WriteConstrainedWholeNumber(value, info.Lower, info.Upper);
                break;
            case PerIntegerForm.SemiConstrained:
                if (value < info.Lower)
                {
                    throw Error($"INTEGER value {value} is below the lower bound {info.Lower} of {type.Name}");
                }

                WriteUnsignedOctets((ulong)(value - info.Lower));
                break;
            default:
                WriteUnconstrainedWholeNumber(value);
                break;
        }
    }

    public override void WriteInteger(Asn1Type type, BigInteger value)
    {
        var info = Info(type);
        if (info.IntegerForm != PerIntegerForm.Big && value.GetBitLength() < 64)
        {
            WriteInteger(type, (long)value);
            return;
        }

        var c = info.Values;
        if (c.IsExtensible)
        {
            var inRoot = c.Root.Contains(value);
            Output.WriteBit(!inRoot);
            if (!inRoot)
            {
                WriteUnconstrainedWholeNumber(value);
                return;
            }
        }

        if (c.LowerBound is { } lb && c.UpperBound is { } ub)
        {
            if (value < lb || value > ub)
            {
                throw Error($"INTEGER value {value} lies outside the PER-visible range {lb}..{ub} of {type.Name}");
            }

            WriteConstrainedWholeNumber(value, lb, ub);
        }
        else if (c.LowerBound is { } lower)
        {
            if (value < lower)
            {
                throw Error($"INTEGER value {value} is below the lower bound {lower} of {type.Name}");
            }

            WriteSemiConstrainedWholeNumber(value, lower);
        }
        else
        {
            WriteUnconstrainedWholeNumber(value);
        }
    }

    public override void WriteEnumerated(Asn1Type type, EnumerationInfo enumeration, EnumItemInfo item)
    {
        if (enumeration.IsExtensible)
        {
            Output.WriteBit(item.IsExtension);
            if (item.IsExtension)
            {
                if (item.IsUnknown)
                {
                    throw Error($"an unknown extension item of {type.Name} cannot be encoded");
                }

                WriteNormallySmallNumber(item.Index);
                return;
            }
        }

        WriteConstrainedWholeNumber(item.Index, 0, enumeration.Root.Length - 1);
    }

    public override void WriteReal(RealType type, double value) => WriteOctets(RealCodec.EncodeCanonical(value), 0, null);

    private readonly ref struct Bits(Asn1BitString value) : IContent
    {
        private readonly Asn1BitString _value = value;

        public void Write(PerWriter writer, long offset, long count)
        {
            writer.Align();
            if ((offset & 7) == 0)
            {
                writer.Output.WriteBitString(_value.Bytes[(int)(offset >> 3)..], count);
                return;
            }

            for (var i = offset; i < offset + count; i++)
            {
                writer.Output.WriteBit(_value[i]);
            }
        }
    }

    public override void WriteBitString(BitStringType type, Asn1BitString value)
    {
        var info = Info(type);
        var c = info.Sizes;
        var lbSize = info.SizeLower;
        var ubSize = info.SizeUpper;
        if (type.HasNamedBits)
        {
            // 16.2/16.3: no trailing zeros, then pad to the smallest permitted size.
            value = value.TrimTrailingZeros();
            if (value.Length < lbSize)
            {
                value = value.WithLength(lbSize);
            }
        }

        var n = value.Length;
        var content = new Bits(value);
        if (c.IsExtensible)
        {
            var inRoot = c.Root.Contains(n);
            Output.WriteBit(!inRoot);
            if (!inRoot)
            {
                WriteLengthAndContent(n, 0, null, ref content);
                return;
            }
        }

        if (ubSize == 0)
        {
            return;
        }

        if (ubSize is { } fixedSize && fixedSize == lbSize && fixedSize < K64)
        {
            if (n != fixedSize)
            {
                throw Error($"BIT STRING of {n} bits does not have the fixed size {fixedSize} of {type.Name}");
            }

            if (fixedSize > 16)
            {
                Align();
            }

            Output.WriteBitString(value.Bytes, n);
            return;
        }

        WriteLengthAndContent(n, lbSize, ubSize, ref content);
    }

    public override void WriteOctetString(OctetStringType type, ReadOnlySpan<byte> value)
    {
        var info = Info(type);
        var c = info.Sizes;
        var lbSize = info.SizeLower;
        var ubSize = info.SizeUpper;
        var n = value.Length;
        if (c.IsExtensible)
        {
            var inRoot = c.Root.Contains(n);
            Output.WriteBit(!inRoot);
            if (!inRoot)
            {
                WriteOctets(value, 0, null);
                return;
            }
        }

        if (ubSize == 0)
        {
            return;
        }

        if (ubSize is { } fixedSize && fixedSize == lbSize && fixedSize < K64)
        {
            if (n != fixedSize)
            {
                throw Error($"OCTET STRING of {n} octets does not have the fixed size {fixedSize} of {type.Name}");
            }

            if (fixedSize > 2)
            {
                Align();
            }

            Output.WriteBytes(value);
            return;
        }

        WriteOctets(value, lbSize, ubSize);
    }

    public override void WriteObjectIdentifier(ObjectIdentifierType type, ObjectIdentifier value) => WriteOctets(ObjectIdentifierCodec.Encode(value), 0, null);

    public override void WriteRelativeObjectIdentifier(RelativeOidType type, RelativeObjectIdentifier value) => WriteOctets(ObjectIdentifierCodec.Encode(value), 0, null);

    public override void WriteCharacterString(CharacterStringType type, string value)
    {
        var info = Info(type);
        if (info.Alphabet is not { } alphabet)
        {
            // 30.6: not a known-multiplier type: octets with an unconstrained length.
            WriteOctets(StringCodec.Encode(type.StringKind, value), 0, null);
            return;
        }

        // A string has at most as many code points as UTF-16 code units.
        var chars = ArrayPool<int>.Shared.Rent(value.Length);
        var count = 0;
        foreach (var cp in CharacterRepertoire.CodePoints(value))
        {
            chars[count++] = cp;
        }

        WriteKnownMultiplier(type, info.Sizes, info.SizeLower, info.SizeUpper, alphabet, chars.AsSpan(0, count));
        ArrayPool<int>.Shared.Return(chars);
    }

    private readonly ref struct Characters(Asn1Type type, AlphabetInfo alphabet, ReadOnlySpan<int> chars, bool align) : IContent
    {
        private readonly ReadOnlySpan<int> _chars = chars;

        public void Write(PerWriter writer, long offset, long count)
        {
            if (align)
            {
                writer.Align();
            }

            var bits = alphabet.Bits;
            foreach (var c in _chars.Slice((int)offset, (int)count))
            {
                var v = alphabet.Encode(c);
                if (v < 0 || !alphabet.Contains(c))
                {
                    throw writer.Error($"character U+{c:X4} is not in the permitted alphabet of {type.Name}");
                }

                writer.Output.WriteBits((ulong)v, bits);
            }
        }
    }

    private void WriteKnownMultiplier(Asn1Type type, EffectiveConstraint sizes, long alb, long? aub, AlphabetInfo alphabet, ReadOnlySpan<int> chars)
    {
        var n = chars.Length;
        if (sizes.IsExtensible)
        {
            var inRoot = sizes.Root.Contains(n);
            Output.WriteBit(!inRoot);
            if (!inRoot)
            {
                alb = 0;
                aub = null;
            }
        }

        var b = alphabet.Bits;
        if (aub is { } fixedLength && fixedLength == alb && fixedLength < K64)
        {
            if (n != fixedLength)
            {
                throw Error($"string of {n} characters does not have the fixed size {fixedLength} of {type.Name}");
            }

            if (fixedLength * b > 16)
            {
                Align();
            }

            new Characters(type, alphabet, chars, align: false).Write(this, 0, n);
            return;
        }

        var content = new Characters(type, alphabet, chars, align: aub is null || aub.Value * b >= 16);
        WriteLengthAndContent(n, alb, aub, ref content);
    }

    public override void WriteTime(TimeType type, string value)
    {
        if (type.TimeKind is not (TimeKind.UtcTime or TimeKind.GeneralizedTime))
        {
            throw new Asn1UnsupportedException($"{RuleName} encoding of {TimeType.Keyword(type.TimeKind)} (X.691 clause 32) is not implemented");
        }

        if (!TimeSyntax.IsCanonical(type.TimeKind, value))
        {
            throw Error($"PER requires the canonical {TimeType.Keyword(type.TimeKind)} form of X.690 11.7/11.8 (X.691 10.6.5); got '{value}'");
        }

        // 10.6.5: encoded as the VisibleString of its definition; constraints on useful types are not PER-visible.
        var alphabet = new AlphabetInfo(AlphabetInfo.Repertoire(CharacterStringKind.Visible), Aligned);
        int[] chars = [.. Encoding.ASCII.GetBytes(value).Select(x => (int)x)];
        WriteKnownMultiplier(type, EffectiveConstraint.None, 0, null, alphabet, chars);
    }

    // ------------------------------------------------------------------ structured

    public override void WriteSequence<T>(SequenceType<T> type, T value)
    {
        var info = Info(type);
        var comps = type.Components;

        // Whether each component is encoded: present, and for DEFAULT components not holding the default.
        Span<bool> present = comps.Length <= 128 ? stackalloc bool[comps.Length] : new bool[comps.Length];
        for (var i = 0; i < comps.Length; i++)
        {
            var c = comps[i];
            present[i] = c.IsPresent(value) && !(c.Presence == Asn1Presence.Default && c.HasDefaultValue(value));
        }

        var additions = info.Additions;
        Span<bool> additionsPresent = additions.Length <= 128 ? stackalloc bool[additions.Length] : new bool[additions.Length];
        var anyAddition = false;
        for (var a = 0; a < additions.Length; a++)
        {
            foreach (var k in additions[a])
            {
                additionsPresent[a] |= present[k];
            }

            anyAddition |= additionsPresent[a];
        }

        if (type.IsExtensible)
        {
            Output.WriteBit(anyAddition);
        }

        WriteComponents(comps, value, info.RootOrder, present);

        if (!type.IsExtensible || !anyAddition)
        {
            return;
        }

        // 19.7-19.9: extension bitmap (normally small length) and each present addition as an open type field.
        WriteBitmap(additionsPresent);
        for (var a = 0; a < additions.Length; a++)
        {
            if (!additionsPresent[a])
            {
                continue;
            }

            var nested = BeginOpenType();
            if (info.AdditionIsGroup[a])
            {
                nested.WriteComponents(comps, value, additions[a], present);
            }
            else
            {
                comps[additions[a][0]].Encode(nested, value);
            }

            EndOpenType(nested);
        }
    }

    /// <summary>Writes the presence bitmap of OPTIONAL/DEFAULT components followed by the present components.</summary>
    private void WriteComponents<T>(ImmutableArray<SequenceComponent<T>> comps, T value, int[] order, ReadOnlySpan<bool> present)
    {
        foreach (var i in order)
        {
            if (comps[i].IsOptional)
            {
                Output.WriteBit(present[i]);
            }
        }

        foreach (var i in order)
        {
            if (present[i])
            {
                comps[i].Encode(this, value);
            }
            else if (!comps[i].IsOptional)
            {
                throw Error($"mandatory component '{comps[i].Name}' is absent");
            }
        }
    }

    private readonly struct Elements<TElement>(CollectionType<TElement> type, IReadOnlyList<TElement> list) : IContent
    {
        public void Write(PerWriter writer, long offset, long count)
        {
            for (var i = (int)offset; i < offset + count; i++)
            {
                type.EncodeElement(writer, i, list[i]);
            }
        }
    }

    public override void WriteCollection<TElement>(CollectionType<TElement> type, IReadOnlyList<TElement> value)
    {
        var info = Info(type);
        var c = info.Sizes;
        var n = value.Count;
        var lb = info.SizeLower;
        long? ub = info.SizeUpper is { } u && u < K64 ? u : null;
        if (c.IsExtensible)
        {
            var inRoot = c.Root.Contains(n);
            Output.WriteBit(!inRoot);
            if (!inRoot)
            {
                lb = 0;
                ub = null;
            }
        }

        var content = new Elements<TElement>(type, value);
        if (ub is { } fixedCount && fixedCount == lb)
        {
            if (n != fixedCount)
            {
                throw Error($"{type.Name} must have exactly {fixedCount} elements but has {n}");
            }

            content.Write(this, 0, n);
            return;
        }

        WriteLengthAndContent(n, lb, ub, ref content);
    }

    public override void WriteChoice<T>(ChoiceType<T> type, T value)
    {
        var info = Info(type);
        var index = type.GetIndex(value);
        if (index < 0)
        {
            var unknown = type.GetUnknown(value) ?? throw Error($"no alternative of {type.Name} is selected");
            if (unknown.Value.EncodingRule != RuleName || unknown.Index < 0)
            {
                throw Error($"an unknown extension alternative decoded with {unknown.Value.EncodingRule} cannot be re-encoded with {RuleName}");
            }

            Output.WriteBit(true);
            WriteNormallySmallNumber(unknown.Index);
            var raw = BeginOpenType();
            raw.Output.WriteBytes(unknown.Value.Encoded.Span);
            EndOpenType(raw);
            return;
        }

        var alt = type.Alternatives[index];
        if (type.IsExtensible)
        {
            Output.WriteBit(alt.IsExtensionAddition);
        }

        if (alt.IsExtensionAddition)
        {
            WriteNormallySmallNumber(info.ChoiceIndex[index]);
            var nested = BeginOpenType();
            alt.Encode(nested, value);
            EndOpenType(nested);
            return;
        }

        WriteConstrainedWholeNumber(info.ChoiceIndex[index], 0, info.RootAlternatives.Length - 1);
        alt.Encode(this, value);
    }

    public override void WriteOpenType(OpenType type, Asn1OpenValue value)
    {
        if (value.IsRaw)
        {
            if (value.EncodingRule != RuleName)
            {
                throw Error($"raw {value.EncodingRule} contents cannot be embedded in a {RuleName} encoding");
            }

            WriteOctets(value.Encoded.Span, 0, null);
            return;
        }

        var nested = BeginOpenType();
        value.Type!.EncodeBoxed(nested, value.Value);
        EndOpenType(nested);
    }
}
