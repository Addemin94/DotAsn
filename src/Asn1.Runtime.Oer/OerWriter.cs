using System.Collections.Immutable;
using System.Numerics;
using System.Text;
using Asn1.Runtime.Buffers;
using Asn1.Runtime.Primitives;

namespace Asn1.Runtime.Oer;

/// <summary>Writer for BASIC-OER and CANONICAL-OER. All fields occupy whole octets.</summary>
internal sealed class OerWriter : Asn1Writer
{
    // Reused writers keep their buffers; larger ones are dropped so a huge encoding does not pin its memory.
    private const int MaxReusedBytes = 64 * 1024;

    [ThreadStatic]
    private static OerWriter? t_basic;

    [ThreadStatic]
    private static OerWriter? t_canonical;

    // Writers for nested encodings (open types, extension additions), shared by the root writer and its nested writers.
    private readonly Stack<OerWriter> _nested;

    public OerWriter(BitWriter output, bool canonical, Asn1EncodingOptions options)
        : base(options)
    {
        Output = output;
        Canonical = canonical;
        _nested = new Stack<OerWriter>();
    }

    private OerWriter(OerWriter parent)
        : base(parent)
    {
        Output = new BitWriter(64, parent.Options.MaxEncodedSize);
        Canonical = parent.Canonical;
        _nested = parent._nested;
    }

    public BitWriter Output { get; }

    public bool Canonical { get; }

    private string RuleName => Canonical ? "COER" : "OER";

    /// <summary>A root writer for one encoding, reusing this thread's previous one when it has the same options.</summary>
    public static OerWriter Rent(bool canonical, Asn1EncodingOptions options)
    {
        var cached = canonical ? t_canonical : t_basic;
        if (cached is null || !ReferenceEquals(cached.Options, options))
        {
            return new OerWriter(new BitWriter(64, options.MaxEncodedSize), canonical, options);
        }

        // Taken out of the slot, so an encoding started while this one runs gets its own writer.
        if (canonical)
        {
            t_canonical = null;
        }
        else
        {
            t_basic = null;
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

        if (Canonical)
        {
            t_canonical = this;
        }
        else
        {
            t_basic = this;
        }
    }

    // ------------------------------------------------------------------ procedures

    /// <summary>8.6: length determinant (short form below 128, otherwise minimal long form).</summary>
    private void WriteLength(long n)
    {
        if (n < 128)
        {
            Output.WriteBits((ulong)n, 8);
            return;
        }

        var length = IntegerCodec.UnsignedLength((ulong)n);
        Output.WriteBits(0x80 | (ulong)length, 8);
        Output.WriteBits((ulong)n, length * 8);
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

    /// <summary>A writer for a nested encoding; its output is used before <see cref="EndNested"/> returns it.</summary>
    private OerWriter BeginNested()
    {
        if (_nested.TryPop(out var nested))
        {
            nested.ContinueFrom(this);
            nested.Output.Reset(Options.MaxEncodedSize);
            return nested;
        }

        return new OerWriter(this);
    }

    private void EndNested(OerWriter nested)
    {
        if (nested.Output.Capacity <= MaxReusedBytes)
        {
            _nested.Push(nested);
        }
    }

    /// <summary>Writes the encoding produced by <paramref name="nested"/> with a length determinant.</summary>
    private void EndNestedLengthPrefixed(OerWriter nested)
    {
        WriteLengthPrefixed(nested.Output.WrittenSpan);
        EndNested(nested);
    }

    // ------------------------------------------------------------------ primitives

    public override void WriteBoolean(BooleanType type, bool value) => Output.WriteBits(value ? 0xFFUL : 0UL, 8);

    public override void WriteNull(NullType type)
    {
    }

    public override void WriteInteger(Asn1Type type, long value)
    {
        var info = OerTypeInfo.Get(type);
        if (!info.HasSmallBounds)
        {
            WriteInteger(type, (BigInteger)value);
            return;
        }

        // The variable-length signed form (10.4 without a fixed size) is written as given, like the BigInteger path.
        if (info.IntegerForm != OerIntegerForm.SignedVariable && (value < info.Lower || value > info.Upper))
        {
            throw Error($"INTEGER value {value} lies outside the OER-visible range of {type.Name}");
        }

        switch (info.IntegerForm)
        {
            case OerIntegerForm.UnsignedFixed or OerIntegerForm.SignedFixed:
                Output.WriteBits((ulong)value, info.FixedOctets * 8);
                break;
            case OerIntegerForm.UnsignedVariable:
                {
                    var length = IntegerCodec.UnsignedLength((ulong)value);
                    Output.WriteBits((ulong)length, 8);
                    Output.WriteBits((ulong)value, length * 8);
                    break;
                }

            default:
                {
                    var length = IntegerCodec.TwosComplementLength(value);
                    Output.WriteBits((ulong)length, 8);
                    Output.WriteBits((ulong)value, length * 8);
                    break;
                }
        }
    }

    public override void WriteInteger(Asn1Type type, BigInteger value)
    {
        if (value.GetBitLength() < 64 && OerTypeInfo.Get(type).HasSmallBounds)
        {
            WriteInteger(type, (long)value);
            return;
        }

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

        var length = IntegerCodec.TwosComplementLength(item.Value);
        Output.WriteBits(0x80 | (ulong)length, 8);
        Output.WriteBits((ulong)item.Value, length * 8);
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

        // Whether each component is encoded: present, and for DEFAULT components not holding the default.
        Span<bool> present = comps.Length <= 128 ? stackalloc bool[comps.Length] : new bool[comps.Length];
        for (var i = 0; i < comps.Length; i++)
        {
            var c = comps[i];
            present[i] = c.IsPresent(value) && !(c.Presence == Asn1Presence.Default && c.HasDefaultValue(value));
        }

        var additions = info.Additions;
        Span<bool> additionsPresent = additions.Length <= 128 ? stackalloc bool[additions.Length] : new bool[additions.Length];
        var extended = false;
        for (var a = 0; a < additions.Length; a++)
        {
            foreach (var k in additions[a])
            {
                additionsPresent[a] |= present[k];
            }

            extended |= additionsPresent[a];
        }

        extended &= type.IsExtensible;
        WriteComponents(comps, value, info.RootOrder, present, type.IsExtensible ? extended : null);
        if (!extended)
        {
            return;
        }

        // 16.4: extension presence bitmap as a length-prefixed bit string.
        var n = additionsPresent.Length;
        var octets = (n + 7) / 8;
        WriteLength(octets + 1);
        Output.WriteBits((ulong)((8 - (n % 8)) % 8), 8);
        foreach (var p in additionsPresent)
        {
            Output.WriteBit(p);
        }

        Output.Align();
        for (var a = 0; a < additions.Length; a++)
        {
            if (!additionsPresent[a])
            {
                continue;
            }

            var nested = BeginNested();
            if (info.AdditionIsGroup[a])
            {
                nested.WriteComponents(comps, value, additions[a], present, null);
            }
            else
            {
                comps[additions[a][0]].Encode(nested, value);
            }

            EndNestedLengthPrefixed(nested);
        }
    }

    /// <summary>Writes a preamble (optional extension bit, presence bits, padding) and the present components.</summary>
    private void WriteComponents<T>(ImmutableArray<SequenceComponent<T>> comps, T value, int[] order, ReadOnlySpan<bool> present, bool? extensionBit)
    {
        // The preamble is written straight into the output, whose position is on an octet boundary here.
        var any = false;
        if (extensionBit is { } e)
        {
            Output.WriteBit(e);
            any = true;
        }

        foreach (var i in order)
        {
            if (comps[i].IsOptional)
            {
                Output.WriteBit(present[i]);
                any = true;
            }
        }

        if (any)
        {
            Output.Align();
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

    public override void WriteCollection<TElement>(CollectionType<TElement> type, IReadOnlyList<TElement> value)
    {
        // 17.2: quantity field = length determinant + minimal unsigned count.
        var quantityLength = IntegerCodec.UnsignedLength((ulong)value.Count);
        Output.WriteBits((ulong)quantityLength, 8);
        Output.WriteBits((ulong)value.Count, quantityLength * 8);
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
            var nested = BeginNested();
            type.EncodeElement(nested, i, value[i]);
            encoded.Add(nested.Output.ToArray());
            EndNested(nested);
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
            var nested = BeginNested();
            alt.Encode(nested, value);
            var inner = nested.Output.WrittenSpan;
            Output.WriteBytes(inner[..TagLength(inner)]);
            if (alt.IsExtensionAddition)
            {
                WriteLengthPrefixed(inner);
            }
            else
            {
                Output.WriteBytes(inner);
            }

            EndNested(nested);
            return;
        }

        WriteTag(Output, alt.Type.Tags[0].Tag);
        if (alt.IsExtensionAddition)
        {
            var nested = BeginNested();
            alt.Encode(nested, value);
            EndNestedLengthPrefixed(nested);
        }
        else
        {
            alt.Encode(this, value);
        }
    }

    private static int TagLength(ReadOnlySpan<byte> encoding)
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

        var nested = BeginNested();
        value.Type!.EncodeBoxed(nested, value.Value);
        EndNestedLengthPrefixed(nested);
    }
}
