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

    public PerWriter(BitWriter output, bool aligned, string ruleName, Asn1EncodingOptions options)
        : base(options)
    {
        Output = output;
        Aligned = aligned;
        RuleName = ruleName;
    }

    private PerWriter(PerWriter parent, BitWriter output)
        : base(parent)
    {
        Output = output;
        Aligned = parent.Aligned;
        RuleName = parent.RuleName;
    }

    public BitWriter Output { get; }

    public bool Aligned { get; }

    public string RuleName { get; }

    private PerTypeInfo Info(Asn1Type type) => PerTypeInfo.Get(type, Aligned);

    private void Align()
    {
        if (Aligned)
        {
            Output.Align();
        }
    }

    // ------------------------------------------------------------------ procedures (X.691 11)

    /// <summary>11.5: constrained whole number. Returns false for the ALIGNED indefinite-length case (caller must add a length).</summary>
    private void WriteConstrainedWholeNumber(BigInteger value, BigInteger lb, BigInteger ub)
    {
        var range = ub - lb + 1;
        var offset = value - lb;
        if (range == 1)
        {
            return;
        }

        if (!Aligned)
        {
            Output.WriteBits(offset, IntegerCodec.BitsForRange(range));
            return;
        }

        if (range <= 255)
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
            // 11.5.7.4 and 13.2.6 a): length in octets as a constrained whole number 1..octets(range), then the octets.
            var octets = IntegerCodec.ToUnsigned(offset);
            var maxOctets = IntegerCodec.UnsignedLength(range - 1);
            WriteConstrainedWholeNumber(octets.Length, 1, maxOctets);
            Output.Align();
            Output.WriteBytes(octets);
        }
    }

    /// <summary>11.6: normally small non-negative whole number.</summary>
    private void WriteNormallySmallNumber(BigInteger n)
    {
        if (n <= 63)
        {
            Output.WriteBit(false);
            Output.WriteBits((ulong)n, 6);
            return;
        }

        Output.WriteBit(true);
        WriteSemiConstrainedWholeNumber(n, 0);
    }

    /// <summary>11.7 with 11.9: semi-constrained whole number preceded by an unconstrained length.</summary>
    private void WriteSemiConstrainedWholeNumber(BigInteger n, BigInteger lb)
    {
        var octets = IntegerCodec.ToUnsigned(n - lb);
        WriteLengthAndContent(octets.Length, 0, null, (offset, count) =>
        {
            Align();
            Output.WriteBytes(octets.AsSpan((int)offset, (int)count));
        });
    }

    /// <summary>11.8 with 11.9: unconstrained whole number (2's complement) preceded by an unconstrained length.</summary>
    private void WriteUnconstrainedWholeNumber(BigInteger n)
    {
        var octets = IntegerCodec.ToTwosComplement(n);
        WriteLengthAndContent(octets.Length, 0, null, (offset, count) =>
        {
            Align();
            Output.WriteBytes(octets.AsSpan((int)offset, (int)count));
        });
    }

    /// <summary>
    /// 11.9: writes a length determinant for <paramref name="n"/> units followed by the content, fragmenting at 16K
    /// units when the length is unconstrained or ub ≥ 64K. <paramref name="writeChunk"/> writes units [offset, offset+count).
    /// </summary>
    private void WriteLengthAndContent(long n, long lb, long? ub, Action<long, long> writeChunk)
    {
        if (ub is { } u && u < K64)
        {
            WriteConstrainedWholeNumber(n, lb, u);
            if (n > 0)
            {
                writeChunk(0, n);
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
                    writeChunk(offset, remaining);
                }

                return;
            }

            if (remaining < K16)
            {
                Output.WriteBits(0x8000 | (ulong)remaining, 16);
                writeChunk(offset, remaining);
                return;
            }

            var m = Math.Min(4, remaining / K16);
            Output.WriteBits(0xC0 | (ulong)m, 8);
            writeChunk(offset, m * K16);
            offset += m * K16;
        }
    }

    /// <summary>11.9.3.4: normally small length (used for the extension bitmap).</summary>
    private void WriteNormallySmallLength(int n, Action writeContent)
    {
        if (n <= 64)
        {
            Output.WriteBit(false);
            Output.WriteBits((ulong)(n - 1), 6);
            writeContent();
            return;
        }

        Output.WriteBit(true);
        WriteLengthAndContent(n, 0, null, (_, _) => writeContent());
    }

    /// <summary>11.2: an open type field holding the complete encoding of a nested value.</summary>
    private void WriteOpenTypeField(Action<PerWriter> encode)
    {
        var bytes = CompleteEncoding(encode);
        WriteLengthAndContent(bytes.Length, 0, null, (offset, count) =>
        {
            Align();
            Output.WriteBytes(bytes.AsSpan((int)offset, (int)count));
        });
    }

    private byte[] CompleteEncoding(Action<PerWriter> encode)
    {
        var inner = new PerWriter(this, new BitWriter(64, Options.MaxEncodedSize));
        encode(inner);
        return inner.Output.BitPosition == 0 ? [0] : inner.Output.ToArray();
    }

    private static long CheckedLong(BigInteger v) => v > long.MaxValue ? long.MaxValue : (long)v;

    // ------------------------------------------------------------------ primitives

    public override void WriteBoolean(BooleanType type, bool value) => Output.WriteBit(value);

    public override void WriteNull(NullType type)
    {
    }

    public override void WriteInteger(Asn1Type type, BigInteger value)
    {
        var c = Info(type).Values;
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

    public override void WriteReal(RealType type, double value)
    {
        var content = RealCodec.EncodeCanonical(value);
        WriteLengthAndContent(content.Length, 0, null, (offset, count) =>
        {
            Align();
            Output.WriteBytes(content.AsSpan((int)offset, (int)count));
        });
    }

    public override void WriteBitString(BitStringType type, Asn1BitString value)
    {
        var c = Info(type).Sizes;
        var lbSize = (long)(c.LowerBound ?? 0);
        long? ubSize = c.UpperBound is { } u ? CheckedLong(u) : null;
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
        if (c.IsExtensible)
        {
            var inRoot = c.Root.Contains(n);
            Output.WriteBit(!inRoot);
            if (!inRoot)
            {
                WriteBits(value, n, 0, null);
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

        WriteBits(value, n, lbSize, ubSize);
    }

    private void WriteBits(Asn1BitString value, long n, long lb, long? ub)
    {
        WriteLengthAndContent(n, lb, ub, (offset, count) =>
        {
            Align();
            if ((offset & 7) == 0)
            {
                Output.WriteBitString(value.Bytes[(int)(offset >> 3)..], count);
            }
            else
            {
                for (var i = offset; i < offset + count; i++)
                {
                    Output.WriteBit(value[i]);
                }
            }
        });
    }

    public override void WriteOctetString(OctetStringType type, ReadOnlySpan<byte> value)
    {
        var bytes = value.ToArray();
        var c = Info(type).Sizes;
        var lbSize = (long)(c.LowerBound ?? 0);
        long? ubSize = c.UpperBound is { } u ? CheckedLong(u) : null;
        var n = bytes.Length;
        if (c.IsExtensible)
        {
            var inRoot = c.Root.Contains(n);
            Output.WriteBit(!inRoot);
            if (!inRoot)
            {
                WriteOctets(bytes, 0, null);
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

            Output.WriteBytes(bytes);
            return;
        }

        WriteOctets(bytes, lbSize, ubSize);
    }

    private void WriteOctets(byte[] bytes, long lb, long? ub) =>
        WriteLengthAndContent(bytes.Length, lb, ub, (offset, count) =>
        {
            Align();
            Output.WriteBytes(bytes.AsSpan((int)offset, (int)count));
        });

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

        WriteKnownMultiplier(type, info.Sizes, alphabet, [.. CharacterRepertoire.CodePoints(value)]);
    }

    private void WriteKnownMultiplier(Asn1Type type, EffectiveConstraint sizes, AlphabetInfo alphabet, int[] chars)
    {
        var n = chars.Length;
        var alb = (long)(sizes.LowerBound ?? 0);
        long? aub = sizes.UpperBound is { } u ? CheckedLong(u) : null;
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
        void WriteChars(long offset, long count)
        {
            for (var i = offset; i < offset + count; i++)
            {
                var v = alphabet.Encode(chars[i]);
                if (v < 0 || !alphabet.Contains(chars[i]))
                {
                    throw Error($"character U+{chars[i]:X4} is not in the permitted alphabet of {type.Name}");
                }

                Output.WriteBits((ulong)v, b);
            }
        }

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

            WriteChars(0, n);
            return;
        }

        var align = aub is null || aub.Value * b >= 16;
        WriteLengthAndContent(n, alb, aub, (offset, count) =>
        {
            if (align)
            {
                Align();
            }

            WriteChars(offset, count);
        });
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
        WriteKnownMultiplier(type, EffectiveConstraint.None, alphabet, [.. Encoding.ASCII.GetBytes(value).Select(x => (int)x)]);
    }

    // ------------------------------------------------------------------ structured

    public override void WriteSequence<T>(SequenceType<T> type, T value)
    {
        var info = Info(type);
        var comps = type.Components;
        bool Present(int i) => comps[i].IsPresent(value) && !(comps[i].Presence == Asn1Presence.Default && comps[i].HasDefaultValue(value));

        var additionsPresent = info.Additions.Select(item => item.Any(Present)).ToArray();
        if (type.IsExtensible)
        {
            Output.WriteBit(additionsPresent.Any(x => x));
        }

        WriteComponents(comps, value, info.RootOrder, Present);

        if (!type.IsExtensible || !additionsPresent.Any(x => x))
        {
            return;
        }

        // 19.7-19.9: extension bitmap (normally small length) and each present addition as an open type field.
        WriteNormallySmallLength(additionsPresent.Length, () =>
        {
            foreach (var p in additionsPresent)
            {
                Output.WriteBit(p);
            }
        });
        for (var a = 0; a < info.Additions.Length; a++)
        {
            if (!additionsPresent[a])
            {
                continue;
            }

            var item = info.Additions[a];
            if (info.AdditionIsGroup[a])
            {
                WriteOpenTypeField(w => w.WriteComponents(comps, value, item, Present));
            }
            else
            {
                WriteOpenTypeField(w => comps[item[0]].Encode(w, value));
            }
        }
    }

    /// <summary>Writes the presence bitmap of OPTIONAL/DEFAULT components followed by the present components.</summary>
    private void WriteComponents<T>(IReadOnlyList<SequenceComponent<T>> comps, T value, int[] order, Func<int, bool> present)
    {
        foreach (var i in order)
        {
            if (comps[i].IsOptional)
            {
                Output.WriteBit(present(i));
            }
        }

        foreach (var i in order)
        {
            if (comps[i].IsOptional ? present(i) : true)
            {
                if (!comps[i].IsPresent(value))
                {
                    throw Error($"mandatory component '{comps[i].Name}' is absent");
                }

                comps[i].Encode(this, value);
            }
        }
    }

    public override void WriteCollection<TElement>(CollectionType<TElement> type, IReadOnlyList<TElement> value)
    {
        var c = Info(type).Sizes;
        var n = value.Count;
        var lb = (long)(c.LowerBound ?? 0);
        long? ub = c.UpperBound is { } u && u < K64 ? (long)u : null;
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

        void Elements(long offset, long count)
        {
            for (var i = (int)offset; i < offset + count; i++)
            {
                type.EncodeElement(this, i, value[i]);
            }
        }

        if (ub is { } fixedCount && fixedCount == lb)
        {
            if (n != fixedCount)
            {
                throw Error($"{type.Name} must have exactly {fixedCount} elements but has {n}");
            }

            Elements(0, n);
            return;
        }

        WriteLengthAndContent(n, lb, ub, Elements);
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
            WriteOpenTypeField(w => w.Output.WriteBytes(unknown.Value.Encoded.Span));
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
            WriteOpenTypeField(w => alt.Encode(w, value));
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

            var bytes = value.Encoded.ToArray();
            WriteOctets(bytes, 0, null);
            return;
        }

        WriteOpenTypeField(w => value.Type!.EncodeBoxed(w, value.Value));
    }
}
