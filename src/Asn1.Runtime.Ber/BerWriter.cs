using System.Numerics;
using System.Text;
using Asn1.Runtime.Primitives;

namespace Asn1.Runtime.Ber;

/// <summary>The X.690 profile.</summary>
public enum BerProfile
{
    /// <summary>Basic Encoding Rules.</summary>
    Ber,

    /// <summary>Canonical Encoding Rules (X.690 clause 9).</summary>
    Cer,

    /// <summary>Distinguished Encoding Rules (X.690 clause 10).</summary>
    Der,
}

/// <summary>Writer for BER, CER and DER.</summary>
internal sealed class BerWriter : Asn1Writer
{
    private const int CerFragmentSize = 1000;
    private readonly BerProfile _profile;
    private readonly bool _indefinite;
    private readonly bool _encodeDefaults;
    private readonly List<int> _marks = [];

    public BerWriter(TlvWriter output, BerProfile profile, Asn1EncodingOptions options, bool useIndefiniteLength, bool encodeDefaults)
        : base(options)
    {
        Output = output;
        _profile = profile;
        _indefinite = profile == BerProfile.Cer || (profile == BerProfile.Ber && useIndefiniteLength);
        _encodeDefaults = profile == BerProfile.Ber && encodeDefaults;
    }

    public TlvWriter Output { get; }

    private bool Canonical => _profile != BerProfile.Ber;

    // ------------------------------------------------------------------ framing

    private void BeginConstructed(Asn1Tag tag)
    {
        if (_indefinite)
        {
            Output.BeginIndefinite(tag);
            _marks.Add(-1);
        }
        else
        {
            _marks.Add(Output.BeginDefinite(tag));
        }
    }

    private void EndConstructed()
    {
        var mark = _marks[^1];
        _marks.RemoveAt(_marks.Count - 1);
        if (mark < 0)
        {
            Output.EndIndefinite();
        }
        else
        {
            Output.EndDefinite(mark);
        }
    }

    /// <summary>Opens explicit tag wrappers; returns how many were opened.</summary>
    private int OpenExplicit(Asn1Type type)
    {
        var n = 0;
        foreach (var step in type.Tags)
        {
            if (!step.IsExplicit)
            {
                break;
            }

            BeginConstructed(step.Tag);
            n++;
        }

        return n;
    }

    private void Close(int count)
    {
        for (var i = 0; i < count; i++)
        {
            EndConstructed();
        }
    }

    private Asn1Tag InnermostTag(Asn1Type type)
    {
        var last = type.Tags[^1];
        if (last.IsExplicit)
        {
            throw Error($"{type.Name} has no innermost tag");
        }

        return last.Tag;
    }

    private void WritePrimitive(Asn1Type type, ReadOnlySpan<byte> content)
    {
        var n = OpenExplicit(type);
        Output.WriteIdentifier(InnermostTag(type), constructed: false);
        Output.WriteLength(content.Length);
        Output.WriteBytes(content);
        Close(n);
    }

    /// <summary>Writes string contents, segmenting per CER (X.690 9.2) when longer than 1000 octets.</summary>
    private void WriteStringContents(Asn1Type type, ReadOnlySpan<byte> content, bool isBitString, int unusedBits)
    {
        var total = content.Length + (isBitString ? 1 : 0);
        if (_profile != BerProfile.Cer || total <= CerFragmentSize)
        {
            if (!isBitString)
            {
                WritePrimitive(type, content);
                return;
            }

            var n = OpenExplicit(type);
            Output.WriteIdentifier(InnermostTag(type), constructed: false);
            Output.WriteLength(total);
            Output.WriteByte((byte)unusedBits);
            Output.WriteBytes(content);
            Close(n);
            return;
        }

        var opened = OpenExplicit(type);
        BeginConstructed(InnermostTag(type));
        var fragmentTag = isBitString ? Asn1Tag.BitString : Asn1Tag.OctetString;
        var dataPerFragment = isBitString ? CerFragmentSize - 1 : CerFragmentSize;
        for (var offset = 0; offset < content.Length; offset += dataPerFragment)
        {
            var len = Math.Min(dataPerFragment, content.Length - offset);
            var last = offset + len >= content.Length;
            Output.WriteIdentifier(fragmentTag, constructed: false);
            Output.WriteLength(len + (isBitString ? 1 : 0));
            if (isBitString)
            {
                Output.WriteByte((byte)(last ? unusedBits : 0));
            }

            Output.WriteBytes(content.Slice(offset, len));
        }

        EndConstructed();
        Close(opened);
    }

    // ------------------------------------------------------------------ primitives

    public override void WriteBoolean(BooleanType type, bool value) => WritePrimitive(type, [value ? (byte)0xFF : (byte)0x00]);

    public override void WriteInteger(Asn1Type type, long value)
    {
        Span<byte> buffer = stackalloc byte[8];
        var n = IntegerCodec.WriteTwosComplement(value, buffer);
        WritePrimitive(type, buffer[..n]);
    }

    public override void WriteInteger(Asn1Type type, BigInteger value) => WritePrimitive(type, IntegerCodec.ToTwosComplement(value));

    public override void WriteEnumerated(Asn1Type type, EnumerationInfo enumeration, EnumItemInfo item) => WriteInteger(type, item.Value);

    public override void WriteReal(RealType type, double value) => WritePrimitive(type, RealCodec.EncodeCanonical(value));

    public override void WriteBitString(BitStringType type, Asn1BitString value)
    {
        if (Canonical && type.HasNamedBits)
        {
            // X.690 11.2.2: trailing zero bits are removed for types with named bits.
            value = value.TrimTrailingZeros();
        }

        WriteStringContents(type, value.Bytes, isBitString: true, value.UnusedBits);
    }

    public override void WriteOctetString(OctetStringType type, ReadOnlySpan<byte> value) => WriteStringContents(type, value, isBitString: false, 0);

    public override void WriteNull(NullType type) => WritePrimitive(type, []);

    public override void WriteObjectIdentifier(ObjectIdentifierType type, ObjectIdentifier value) => WritePrimitive(type, ObjectIdentifierCodec.Encode(value));

    public override void WriteRelativeObjectIdentifier(RelativeOidType type, RelativeObjectIdentifier value) => WritePrimitive(type, ObjectIdentifierCodec.Encode(value));

    public override void WriteCharacterString(CharacterStringType type, string value) =>
        WriteStringContents(type, StringCodec.Encode(type.StringKind, value), isBitString: false, 0);

    public override void WriteTime(TimeType type, string value)
    {
        if (Canonical && type.TimeKind is TimeKind.UtcTime or TimeKind.GeneralizedTime && !TimeSyntax.IsCanonical(type.TimeKind, value))
        {
            throw Error($"{_profile} requires {TimeType.Keyword(type.TimeKind)} in UTC ('Z') with seconds and no trailing fractional zeros (X.690 11.7/11.8); got '{value}'");
        }

        WriteStringContents(type, Encoding.ASCII.GetBytes(value), isBitString: false, 0);
    }

    // ------------------------------------------------------------------ structured

    public override void WriteSequence<T>(SequenceType<T> type, T value)
    {
        var n = OpenExplicit(type);
        BeginConstructed(InnermostTag(type));
        var contentStart = Output.Position;
        var segments = type.Kind == Asn1TypeKind.Set && _profile == BerProfile.Der ? new List<(int Start, int End)>() : null;
        IEnumerable<SequenceComponent<T>> order = type.Components;
        if (type.Kind == Asn1TypeKind.Set && _profile == BerProfile.Cer)
        {
            // CER 9.3: canonical order of X.680 8.6, untagged CHOICEs ordered by their smallest tag (static order).
            order = type.Components.OrderBy(c => TagSets.CanonicalTag(c.Type) ?? default);
        }

        foreach (var c in order)
        {
            if (!c.IsPresent(value))
            {
                continue;
            }

            if (c.Presence == Asn1Presence.Default && !_encodeDefaults && c.HasDefaultValue(value))
            {
                continue;
            }

            var start = Output.Position;
            c.Encode(this, value);
            segments?.Add((start, Output.Position));
        }

        if (segments is { Count: > 1 })
        {
            // DER 10.3: SET components in ascending order of the tags of the encoded values.
            ReorderSegments(contentStart, segments, CompareByTag);
        }

        EndConstructed();
        Close(n);
    }

    public override void WriteCollection<TElement>(CollectionType<TElement> type, IReadOnlyList<TElement> value)
    {
        var n = OpenExplicit(type);
        BeginConstructed(InnermostTag(type));
        var contentStart = Output.Position;
        var segments = type.IsSet && Canonical ? new List<(int Start, int End)>(value.Count) : null;
        for (var i = 0; i < value.Count; i++)
        {
            var start = Output.Position;
            type.EncodeElement(this, i, value[i]);
            segments?.Add((start, Output.Position));
        }

        if (segments is { Count: > 1 })
        {
            // DER 11.6 / CER 9.3: SET OF elements in ascending order of their encodings.
            ReorderSegments(contentStart, segments, CompareEncodings);
        }

        EndConstructed();
        Close(n);
    }

    public override void WriteChoice<T>(ChoiceType<T> type, T value)
    {
        var n = OpenExplicit(type);
        var index = type.GetIndex(value);
        if (index < 0)
        {
            var unknown = type.GetUnknown(value) ?? throw Error($"no alternative of {type.Name} is selected");
            WriteRaw(unknown.Value);
        }
        else
        {
            type.Alternatives[index].Encode(this, value);
        }

        Close(n);
    }

    public override void WriteOpenType(OpenType type, Asn1OpenValue value)
    {
        var n = OpenExplicit(type);
        if (value.IsRaw)
        {
            WriteRaw(value);
        }
        else
        {
            value.Type!.EncodeBoxed(this, value.Value);
        }

        Close(n);
    }

    private void WriteRaw(Asn1OpenValue raw)
    {
        if (raw.EncodingRule is not ("BER" or "CER" or "DER"))
        {
            throw Error($"raw {raw.EncodingRule} contents cannot be embedded in a {_profile} encoding");
        }

        Output.WriteBytes(raw.Encoded.Span);
    }

    // ------------------------------------------------------------------ canonical ordering

    private ReadOnlySpan<byte> Segment((int Start, int End) s) => Output.WrittenSpan[s.Start..s.End];

    private int CompareByTag((int Start, int End) a, (int Start, int End) b) =>
        TlvReader.PeekTag(Segment(a)).CompareTo(TlvReader.PeekTag(Segment(b)));

    private int CompareEncodings((int Start, int End) a, (int Start, int End) b)
    {
        var x = Segment(a);
        var y = Segment(b);
        var common = Math.Min(x.Length, y.Length);
        var c = x[..common].SequenceCompareTo(y[..common]);
        if (c != 0)
        {
            return c;
        }

        // The shorter encoding is padded with trailing zero octets for the comparison.
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

    private void ReorderSegments(int contentStart, List<(int Start, int End)> segments, Comparison<(int Start, int End)> comparison)
    {
        var sorted = segments.ToList();
        sorted.Sort(comparison);
        var end = segments[^1].End;
        var copy = Output.WrittenSpan[contentStart..end].ToArray();
        Output.Truncate(contentStart);
        foreach (var s in sorted)
        {
            Output.WriteBytes(copy.AsSpan(s.Start - contentStart, s.End - s.Start));
        }
    }
}
