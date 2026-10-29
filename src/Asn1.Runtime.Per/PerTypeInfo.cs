using System.Numerics;
using Asn1.Constraints;
using Asn1.Runtime.Primitives;

namespace Asn1.Runtime.Per;

/// <summary>The effective permitted alphabet of a known-multiplier character string (X.691 30.5).</summary>
internal sealed class AlphabetInfo
{
    private readonly IntegerInterval[] _intervals;
    private readonly long[] _offsets;

    public AlphabetInfo(IntegerSet characters, bool aligned)
    {
        _intervals = [.. characters.Intervals];
        _offsets = new long[_intervals.Length];
        long count = 0;
        for (var i = 0; i < _intervals.Length; i++)
        {
            _offsets[i] = count;
            count += (long)(_intervals[i].Max!.Value - _intervals[i].Min!.Value + 1);
        }

        Count = count;
        var b = IntegerCodec.BitsForRange(count);
        if (aligned && b > 0)
        {
            var p = 1;
            while (p < b)
            {
                p *= 2;
            }

            b = p;
        }

        Bits = b;
        var ub = count == 0 ? BigInteger.Zero : _intervals[^1].Max!.Value;
        Direct = ub <= (BigInteger.One << b) - 1;
    }

    /// <summary>Number of characters (N).</summary>
    public long Count { get; }

    /// <summary>Bits per character (b).</summary>
    public int Bits { get; }

    /// <summary>True if characters are encoded by their own value (30.5.4 a), false if by canonical index (b).</summary>
    public bool Direct { get; }

    public bool Contains(long cp)
    {
        foreach (var i in _intervals)
        {
            if (cp >= i.Min && cp <= i.Max)
            {
                return true;
            }
        }

        return false;
    }

    public long Encode(long cp)
    {
        if (Direct)
        {
            return cp;
        }

        for (var i = 0; i < _intervals.Length; i++)
        {
            if (cp >= _intervals[i].Min && cp <= _intervals[i].Max)
            {
                return _offsets[i] + (long)(cp - _intervals[i].Min!.Value);
            }
        }

        return -1;
    }

    public long Decode(long v)
    {
        if (Direct)
        {
            return Contains(v) ? v : -1;
        }

        for (var i = 0; i < _intervals.Length; i++)
        {
            var size = (long)(_intervals[i].Max!.Value - _intervals[i].Min!.Value + 1);
            if (v >= _offsets[i] && v < _offsets[i] + size)
            {
                return (long)_intervals[i].Min!.Value + (v - _offsets[i]);
            }
        }

        return -1;
    }

    /// <summary>The full repertoire of a known-multiplier string kind.</summary>
    public static IntegerSet Repertoire(CharacterStringKind kind)
    {
        if (CharacterRepertoire.SmallAlphabet(kind) is { } small)
        {
            var set = IntegerSet.Empty;
            foreach (var cp in small)
            {
                set = set.Union(IntegerSet.Single(cp));
            }

            return set;
        }

        return kind == CharacterStringKind.Bmp ? IntegerSet.Range(0, 0xFFFF) : IntegerSet.Range(0, uint.MaxValue);
    }
}

/// <summary>How an INTEGER is encoded (X.691 13), with bounds that fit in 64 bits; <see cref="Big"/> otherwise.</summary>
internal enum PerIntegerForm
{
    Unconstrained,
    SemiConstrained,
    Constrained,
    Big,
}

/// <summary>Per-descriptor data derived for PER encoding and cached on the descriptor.</summary>
internal sealed class PerTypeInfo
{
    private readonly EffectiveConstraint _values = EffectiveConstraint.None;
    private readonly EffectiveConstraint _sizes = EffectiveConstraint.None;

    public EffectiveConstraint Values
    {
        get => _values;
        private init
        {
            _values = value;
            (IntegerForm, Lower, Upper) = (value.LowerBound, value.UpperBound) switch
            {
                ({ } lb, { } ub) => Fits(lb) && Fits(ub) ? (PerIntegerForm.Constrained, (long)lb, (long)ub) : (PerIntegerForm.Big, 0L, 0L),
                ({ } lb, null) => Fits(lb) ? (PerIntegerForm.SemiConstrained, (long)lb, 0L) : (PerIntegerForm.Big, 0L, 0L),
                _ => (PerIntegerForm.Unconstrained, 0L, 0L),
            };
        }
    }

    public EffectiveConstraint Sizes
    {
        get => _sizes;
        private init
        {
            _sizes = value;
            SizeLower = (long)(value.LowerBound ?? 0);
            SizeUpper = value.UpperBound is { } u ? (u > long.MaxValue ? long.MaxValue : (long)u) : null;
        }
    }

    /// <summary>INTEGER: the encoding procedure for values that fit in 64 bits.</summary>
    public PerIntegerForm IntegerForm { get; private set; }

    /// <summary>INTEGER: the lower bound (semi-constrained and constrained forms).</summary>
    public long Lower { get; private set; }

    /// <summary>INTEGER: the upper bound (constrained form).</summary>
    public long Upper { get; private set; }

    /// <summary>Sizes: the lower bound (0 when unbounded).</summary>
    public long SizeLower { get; private set; }

    /// <summary>Sizes: the upper bound, saturated to <see cref="long.MaxValue"/>, or null.</summary>
    public long? SizeUpper { get; private set; }

    private static bool Fits(System.Numerics.BigInteger value) => value.GetBitLength() < 64;

    public AlphabetInfo? Alphabet { get; private init; }

    /// <summary>SEQUENCE/SET: indices of root components in encoding order.</summary>
    public int[] RootOrder { get; private init; } = [];

    /// <summary>SEQUENCE/SET: extension addition items (component indices of each item) in definition order.</summary>
    public int[][] Additions { get; private init; } = [];

    /// <summary>SEQUENCE/SET: true for items that are [[ ]] groups.</summary>
    public bool[] AdditionIsGroup { get; private init; } = [];

    /// <summary>CHOICE: PER index of each alternative (root index in canonical order, or addition index).</summary>
    public int[] ChoiceIndex { get; private init; } = [];

    /// <summary>CHOICE: alternatives by root PER index.</summary>
    public int[] RootAlternatives { get; private init; } = [];

    /// <summary>CHOICE: alternatives by addition index.</summary>
    public int[] AdditionAlternatives { get; private init; } = [];

    private static readonly Asn1RuleDataSlot<PerTypeInfo> s_unaligned = new();
    private static readonly Asn1RuleDataSlot<PerTypeInfo> s_aligned = new();

    // Static lambdas: a lambda capturing 'aligned' would allocate a delegate on every lookup.
    public static PerTypeInfo Get(Asn1Type type, bool aligned) => aligned
        ? type.GetRuleData(s_aligned, static t => Create(t, aligned: true))
        : type.GetRuleData(s_unaligned, static t => Create(t, aligned: false));

    private static PerTypeInfo Create(Asn1Type type, bool aligned)
    {
        switch (type)
        {
            case CharacterStringType s when CharacterRepertoire.IsKnownMultiplier(s.StringKind):
                {
                    var repertoire = AlphabetInfo.Repertoire(s.StringKind);
                    var visible = PerVisibleConstraints.Alphabet(type.Constraints);
                    var chars = visible is null ? repertoire : visible.Intersect(repertoire);
                    return new PerTypeInfo { Sizes = PerVisibleConstraints.Size(type.Constraints), Alphabet = new AlphabetInfo(chars, aligned) };
                }

            case CharacterStringType or TimeType or RealType:
                return new PerTypeInfo();
            case BitStringType or OctetStringType:
                return new PerTypeInfo { Sizes = PerVisibleConstraints.Size(type.Constraints) };
            case ICollectionTypeInfo:
                return new PerTypeInfo { Sizes = PerVisibleConstraints.Size(type.Constraints) };
            case IStructuredTypeInfo st when type.Kind == Asn1TypeKind.Choice:
                return ChoiceInfo(st);
            case IStructuredTypeInfo st:
                return SequenceInfo(st, type.Kind == Asn1TypeKind.Set);
            default:
                return type.Kind == Asn1TypeKind.Integer ? new PerTypeInfo { Values = PerVisibleConstraints.Integer(type.Constraints) } : new PerTypeInfo();
        }
    }

    private static PerTypeInfo SequenceInfo(IStructuredTypeInfo st, bool isSet)
    {
        var comps = st.ComponentInfos;
        IEnumerable<int> roots = Enumerable.Range(0, comps.Count).Where(i => !comps[i].IsExtensionAddition && !comps[i].IsInRootPart2)
            .Concat(Enumerable.Range(0, comps.Count).Where(i => !comps[i].IsExtensionAddition && comps[i].IsInRootPart2));
        if (isSet)
        {
            // X.691 21: root components in canonical tag order (untagged CHOICEs by their smallest root tag).
            roots = roots.OrderBy(i => CanonicalTag(comps[i].Type));
        }

        var additions = comps.Select((c, i) => (c, i)).Where(x => x.c.IsExtensionAddition).GroupBy(x => x.c.ExtensionAdditionIndex).OrderBy(g => g.Key).ToList();
        return new PerTypeInfo
        {
            RootOrder = [.. roots],
            Additions = [.. additions.Select(g => g.Select(x => x.i).ToArray())],
            AdditionIsGroup = [.. additions.Select(g => g.First().c.IsInExtensionGroup)],
        };
    }

    private static PerTypeInfo ChoiceInfo(IStructuredTypeInfo st)
    {
        var alts = st.ComponentInfos;
        var roots = Enumerable.Range(0, alts.Count).Where(i => !alts[i].IsExtensionAddition).OrderBy(i => CanonicalTag(alts[i].Type)).ToArray();
        var additions = Enumerable.Range(0, alts.Count).Where(i => alts[i].IsExtensionAddition).ToArray();
        var index = new int[alts.Count];
        for (var i = 0; i < roots.Length; i++)
        {
            index[roots[i]] = i;
        }

        for (var i = 0; i < additions.Length; i++)
        {
            index[additions[i]] = i;
        }

        return new PerTypeInfo { ChoiceIndex = index, RootAlternatives = roots, AdditionAlternatives = additions };
    }

    /// <summary>The canonical-order tag (X.691 23.3: untagged CHOICEs use the smallest tag of their extension root).</summary>
    private static Asn1Tag CanonicalTag(Asn1Type type)
    {
        if (!type.Tags.IsEmpty)
        {
            return type.Tags[0].Tag;
        }

        if (type is IStructuredTypeInfo st && type.Kind == Asn1TypeKind.Choice)
        {
            var tags = st.ComponentInfos.Where(a => !a.IsExtensionAddition).Select(a => CanonicalTag(a.Type)).ToList();
            return tags.Count == 0 ? default : tags.Min();
        }

        return new Asn1Tag(TagClass.Private, int.MaxValue);
    }
}
