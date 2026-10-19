using System.Numerics;
using System.Text;
using Asn1.Runtime.Buffers;
using Asn1.Runtime.Primitives;

namespace Asn1.Runtime.Oer;

/// <summary>Writer for BASIC-OER and CANONICAL-OER. All fields occupy whole octets.</summary>
internal sealed class OerWriter : Asn1Writer
{
    public OerWriter(BitWriter output, bool canonical, Asn1EncodingOptions options)
        : base(options)
    {
        Output = output;
        Canonical = canonical;
    }

    private OerWriter(OerWriter parent, BitWriter output)
        : base(parent)
    {
        Output = output;
        Canonical = parent.Canonical;
    }

    public BitWriter Output { get; }

    public bool Canonical { get; }

    private string RuleName => Canonical ? "COER" : "OER";

    // ------------------------------------------------------------------ procedures

    /// <summary>8.6: length determinant (short form below 128, otherwise minimal long form).</summary>
    private void WriteLength(long n)
    {
        if (n < 128)
        {
            Output.WriteBits((ulong)n, 8);
            return;
        }

        var bytes = IntegerCodec.ToUnsigned(n);
        Output.WriteBits(0x80 | (ulong)bytes.Length, 8);
        Output.WriteBytes(bytes);
    }

    private void WriteLengthPrefixed(ReadOnlySpan<byte> content)
    {
        WriteLength(content.Length);
        Output.WriteBytes(content);
    }

    private static void WriteFixed(BitWriter output, BigInteger value, int octets, bool signed)
    {
        var bytes = new byte[octets];
        var raw = value.ToByteArray(isUnsigned: !signed, isBigEndian: true);
        if (raw.Length > octets)
        {
            raw = raw[^octets..];
        }

        if (signed && value.Sign < 0)
        {
            bytes.AsSpan().Fill(0xFF);
        }

        raw.CopyTo(bytes.AsSpan(octets - raw.Length));
        output.WriteBytes(bytes);
    }

    private byte[] Nested(Action<OerWriter> encode)
    {
        var inner = new OerWriter(this, new BitWriter(64, Options.MaxEncodedSize));
        encode(inner);
        return inner.Output.ToArray();
    }

    // ------------------------------------------------------------------ primitives

    public override void WriteBoolean(BooleanType type, bool value) => Output.WriteBits(value ? 0xFFUL : 0UL, 8);

    public override void WriteNull(NullType type)
    {
    }

    public override void WriteInteger(Asn1Type type, BigInteger value)
    {
        var set = OerTypeInfo.Get(type).Values;
        var lb = set.LowerBound;
        var ub = set.UpperBound;
        if (lb is { } l && l.Sign >= 0)
        {
            // 10.3: unsigned.
            if (value < l || (ub is { } u0 && value > u0))
            {
                throw Error($"INTEGER value {value} lies outside the OER-visible range of {type.Name}");
            }

            var octets = ub is not { } u ? 0 : u <= byte.MaxValue ? 1 : u <= ushort.MaxValue ? 2 : u <= uint.MaxValue ? 4 : u <= ulong.MaxValue ? 8 : 0;
            if (octets > 0)
            {
                WriteFixed(Output, value, octets, signed: false);
            }
            else
            {
                WriteLengthPrefixed(IntegerCodec.ToUnsigned(value));
            }

            return;
        }

        // 10.4: signed.
        var signedOctets = lb is { } sl && ub is { } su
            ? (sl >= sbyte.MinValue && su <= sbyte.MaxValue ? 1 : sl >= short.MinValue && su <= short.MaxValue ? 2 : sl >= int.MinValue && su <= int.MaxValue ? 4 : sl >= long.MinValue && su <= long.MaxValue ? 8 : 0)
            : 0;
        if (signedOctets > 0)
        {
            if (value < lb!.Value || value > ub!.Value)
            {
                throw Error($"INTEGER value {value} lies outside the OER-visible range of {type.Name}");
            }

            WriteFixed(Output, value, signedOctets, signed: true);
        }
        else
        {
            WriteLengthPrefixed(IntegerCodec.ToTwosComplement(value));
        }
    }

    public override void WriteEnumerated(Asn1Type type, EnumerationInfo enumeration, EnumItemInfo item)
    {
        if (item.IsUnknown)
        {
            throw Error($"an unknown extension item of {type.Name} cannot be encoded");
        }

        // 11.3/11.4: the value itself, short form for 0..127.
        if (item.Value is >= 0 and < 128)
        {
            Output.WriteBits((ulong)item.Value, 8);
            return;
        }

        var bytes = IntegerCodec.ToTwosComplement(item.Value);
        Output.WriteBits(0x80 | (ulong)bytes.Length, 8);
        Output.WriteBytes(bytes);
    }

    public override void WriteReal(RealType type, double value)
    {
        if (OerVisibleConstraints.HasRealComponentConstraint(type.Constraints))
        {
            throw new Asn1UnsupportedException($"{RuleName} encoding of REAL with component constraints (IEEE 754 forms, X.696 12.2/12.3) is not implemented");
        }

        // 12.4: length determinant + DER contents octets.
        WriteLengthPrefixed(RealCodec.EncodeCanonical(value));
    }

    public override void WriteBitString(BitStringType type, Asn1BitString value)
    {
        var info = OerTypeInfo.Get(type);
        if (type.HasNamedBits)
        {
            // 13.2.4 / 31.6: trailing zero bits adjusted to the smallest size satisfying the effective size constraint.
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

            Output.WriteBytes(value.Bytes);
            return;
        }

        WriteLength(value.Bytes.Length + 1);
        Output.WriteBits((ulong)value.UnusedBits, 8);
        Output.WriteBytes(value.Bytes);
    }

    public override void WriteOctetString(OctetStringType type, ReadOnlySpan<byte> value)
    {
        var info = OerTypeInfo.Get(type);
        if (info.FixedSize is { } fixedSize)
        {
            if (value.Length != fixedSize)
            {
                throw Error($"OCTET STRING of {value.Length} octets does not have the fixed size {fixedSize} of {type.Name}");
            }

            Output.WriteBytes(value);
            return;
        }

        WriteLengthPrefixed(value);
    }

    public override void WriteObjectIdentifier(ObjectIdentifierType type, ObjectIdentifier value) => WriteLengthPrefixed(ObjectIdentifierCodec.Encode(value));

    public override void WriteRelativeObjectIdentifier(RelativeOidType type, RelativeObjectIdentifier value) => WriteLengthPrefixed(ObjectIdentifierCodec.Encode(value));

    public override void WriteCharacterString(CharacterStringType type, string value)
    {
        var bytes = StringCodec.Encode(type.StringKind, value);
        var info = OerTypeInfo.Get(type);
        if (info.FixedSize is { } fixedSize && CharacterRepertoire.IsKnownMultiplier(type.StringKind))
        {
            if (CharacterRepertoire.CountCharacters(value) != fixedSize)
            {
                throw Error($"string does not have the fixed size {fixedSize} of {type.Name}");
            }

            Output.WriteBytes(bytes);
            return;
        }

        WriteLengthPrefixed(bytes);
    }

    public override void WriteTime(TimeType type, string value)
    {
        if (type.TimeKind is not (TimeKind.UtcTime or TimeKind.GeneralizedTime))
        {
            throw new Asn1UnsupportedException($"{RuleName} encoding of {TimeType.Keyword(type.TimeKind)} (X.696 clause 29) is not implemented");
        }

        if (Canonical && !TimeSyntax.IsCanonical(type.TimeKind, value))
        {
            throw Error($"COER requires the canonical {TimeType.Keyword(type.TimeKind)} form (X.696 31.11); got '{value}'");
        }

        WriteLengthPrefixed(Encoding.ASCII.GetBytes(value));
    }

    /// <summary>27.4: octets per character of a known-multiplier character string type.</summary>
    internal static int OctetsPerCharacter(CharacterStringKind kind) => kind switch
    {
        CharacterStringKind.Bmp => 2,
        CharacterStringKind.Universal => 4,
        _ => 1,
    };

    // ------------------------------------------------------------------ structured

    public override void WriteSequence<T>(SequenceType<T> type, T value)
    {
        var info = OerTypeInfo.Get(type);
        var comps = type.Components;
        bool Present(int i) => comps[i].IsPresent(value) && !(comps[i].Presence == Asn1Presence.Default && comps[i].HasDefaultValue(value));

        var additionsPresent = info.Additions.Select(item => item.Any(Present)).ToArray();
        var extended = type.IsExtensible && additionsPresent.Any(x => x);
        WriteComponents(comps, value, info.RootOrder, Present, type.IsExtensible ? extended : null);
        if (!extended)
        {
            return;
        }

        // 16.4: extension presence bitmap as a length-prefixed bit string.
        var n = additionsPresent.Length;
        var bitmap = new byte[(n + 7) / 8];
        for (var i = 0; i < n; i++)
        {
            if (additionsPresent[i])
            {
                bitmap[i >> 3] |= (byte)(0x80 >> (i & 7));
            }
        }

        WriteLength(bitmap.Length + 1);
        Output.WriteBits((ulong)((8 - (n % 8)) % 8), 8);
        Output.WriteBytes(bitmap);
        for (var a = 0; a < info.Additions.Length; a++)
        {
            if (!additionsPresent[a])
            {
                continue;
            }

            var item = info.Additions[a];
            var bytes = info.AdditionIsGroup[a]
                ? Nested(w => w.WriteComponents(comps, value, item, Present, null))
                : Nested(w => comps[item[0]].Encode(w, value));
            WriteLengthPrefixed(bytes);
        }
    }

    /// <summary>Writes a preamble (optional extension bit, presence bits, padding) and the present components.</summary>
    private void WriteComponents<T>(IReadOnlyList<SequenceComponent<T>> comps, T value, int[] order, Func<int, bool> present, bool? extensionBit)
    {
        var bits = new BitWriter(8);
        if (extensionBit is { } e)
        {
            bits.WriteBit(e);
        }

        foreach (var i in order)
        {
            if (comps[i].IsOptional)
            {
                bits.WriteBit(present(i));
            }
        }

        Output.WriteBytes(bits.WrittenSpan);
        foreach (var i in order)
        {
            if (!comps[i].IsOptional || present(i))
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
        // 17.2: quantity field = length determinant + minimal unsigned count.
        WriteLengthPrefixed(IntegerCodec.ToUnsigned(value.Count));
        if (!(Canonical && type.IsSet) || value.Count < 2)
        {
            for (var i = 0; i < value.Count; i++)
            {
                type.EncodeElement(this, i, value[i]);
            }

            return;
        }

        // 31.8: SET OF elements in ascending order of their encodings.
        var encoded = new List<byte[]>(value.Count);
        for (var i = 0; i < value.Count; i++)
        {
            var index = i;
            encoded.Add(Nested(w => type.EncodeElement(w, index, value[index])));
        }

        encoded.Sort(CompareEncodings);
        foreach (var e in encoded)
        {
            Output.WriteBytes(e);
        }
    }

    internal static int CompareEncodings(byte[] x, byte[] y)
    {
        var common = Math.Min(x.Length, y.Length);
        var c = x.AsSpan(0, common).SequenceCompareTo(y.AsSpan(0, common));
        if (c != 0)
        {
            return c;
        }

        var rest = x.Length > y.Length ? x.AsSpan(common) : y.AsSpan(common);
        foreach (var r in rest)
        {
            if (r != 0)
            {
                return x.Length > y.Length ? 1 : -1;
            }
        }

        return 0;
    }

    /// <summary>8.7: tag encoding (class in bits 8-7, number in bits 6-1 or the long form).</summary>
    internal static void WriteTag(BitWriter output, Asn1Tag tag)
    {
        var cls = (ulong)(byte)tag.Class << 6;
        if (tag.Number < 63)
        {
            output.WriteBits(cls | (ulong)(uint)tag.Number, 8);
            return;
        }

        output.WriteBits(cls | 0x3F, 8);
        Span<byte> tmp = stackalloc byte[5];
        var n = 0;
        var v = (uint)tag.Number;
        do
        {
            tmp[n++] = (byte)(v & 0x7F);
            v >>= 7;
        }
        while (v != 0);

        for (var i = n - 1; i >= 0; i--)
        {
            output.WriteBits((ulong)(tmp[i] | (i > 0 ? 0x80 : 0)), 8);
        }
    }

    public override void WriteChoice<T>(ChoiceType<T> type, T value)
    {
        var index = type.GetIndex(value);
        if (index < 0)
        {
            var unknown = type.GetUnknown(value) ?? throw Error($"no alternative of {type.Name} is selected");
            if (unknown.Value.EncodingRule is not ("OER" or "COER"))
            {
                throw Error($"an unknown alternative decoded with {unknown.Value.EncodingRule} cannot be re-encoded with {RuleName}");
            }

            // OerReader stores an unknown alternative as its complete encoding (tag, length and contents).
            Output.WriteBytes(unknown.Value.Encoded.Span);
            return;
        }

        var alt = type.Alternatives[index];
        if (alt.Type.Tags.IsEmpty)
        {
            // 20.1 note 3: an untagged CHOICE alternative has the tag of its own chosen alternative as its outermost
            // tag, so that tag appears twice: once for this CHOICE and once inside the nested CHOICE encoding.
            var inner = Nested(w => alt.Encode(w, value));
            Output.WriteBytes(inner.AsSpan(0, TagLength(inner)));
            if (alt.IsExtensionAddition)
            {
                WriteLengthPrefixed(inner);
            }
            else
            {
                Output.WriteBytes(inner);
            }

            return;
        }

        WriteTag(Output, alt.Type.Tags[0].Tag);
        if (alt.IsExtensionAddition)
        {
            WriteLengthPrefixed(Nested(w => alt.Encode(w, value)));
        }
        else
        {
            alt.Encode(this, value);
        }
    }

    private static int TagLength(byte[] encoding)
    {
        if (encoding.Length == 0 || (encoding[0] & 0x3F) != 0x3F)
        {
            return Math.Min(1, encoding.Length);
        }

        var i = 1;
        while (i < encoding.Length && (encoding[i] & 0x80) != 0)
        {
            i++;
        }

        return Math.Min(i + 1, encoding.Length);
    }

    public override void WriteOpenType(OpenType type, Asn1OpenValue value)
    {
        if (value.IsRaw)
        {
            if (value.EncodingRule is not ("OER" or "COER"))
            {
                throw Error($"raw {value.EncodingRule} contents cannot be embedded in an {RuleName} encoding");
            }

            WriteLengthPrefixed(value.Encoded.Span);
            return;
        }

        WriteLengthPrefixed(Nested(w => value.Type!.EncodeBoxed(w, value.Value)));
    }
}
