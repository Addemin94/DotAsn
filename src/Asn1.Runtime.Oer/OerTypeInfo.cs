using Asn1.Constraints;

namespace Asn1.Runtime.Oer;

/// <summary>How an INTEGER is encoded (X.696 10).</summary>
internal enum OerIntegerForm
{
    /// <summary>10.3: a fixed number of octets, unsigned.</summary>
    UnsignedFixed,

    /// <summary>10.3: length-prefixed, unsigned.</summary>
    UnsignedVariable,

    /// <summary>10.4: a fixed number of octets, two's complement.</summary>
    SignedFixed,

    /// <summary>10.4: length-prefixed, two's complement.</summary>
    SignedVariable,
}

/// <summary>Cached OER layout data for a descriptor.</summary>
internal sealed class OerTypeInfo
{
    private static readonly Asn1RuleDataSlot<OerTypeInfo> s_slot = new();
    private static readonly Int128 s_minimum = Int128.MinValue;
    private readonly IntegerSet _values = IntegerSet.All;

    public IntegerSet Values
    {
        get => _values;
        private init
        {
            _values = value;
            var lb = value.LowerBound;
            var ub = value.UpperBound;
            if (lb is { } l && l.Sign >= 0)
            {
                FixedOctets = ub is not { } u ? 0 : u <= byte.MaxValue ? 1 : u <= ushort.MaxValue ? 2 : u <= uint.MaxValue ? 4 : u <= ulong.MaxValue ? 8 : 0;
                IntegerForm = FixedOctets > 0 ? OerIntegerForm.UnsignedFixed : OerIntegerForm.UnsignedVariable;
            }
            else
            {
                FixedOctets = lb is { } sl && ub is { } su
                    ? (sl >= sbyte.MinValue && su <= sbyte.MaxValue ? 1 : sl >= short.MinValue && su <= short.MaxValue ? 2 : sl >= int.MinValue && su <= int.MaxValue ? 4 : sl >= long.MinValue && su <= long.MaxValue ? 8 : 0)
                    : 0;
                IntegerForm = FixedOctets > 0 ? OerIntegerForm.SignedFixed : OerIntegerForm.SignedVariable;
            }

            // Bounds within 128 bits allow checks without BigInteger: Int128 holds every value of 8 octets or fewer.
            HasSmallBounds = (lb is null || l128(lb.Value)) && (ub is null || l128(ub.Value));
            if (HasSmallBounds)
            {
                Lower = lb is { } x ? (Int128)x : s_minimum;
                Upper = ub is { } y ? (Int128)y : Int128.MaxValue;
            }

            static bool l128(System.Numerics.BigInteger v) => v.GetBitLength() < 128;
        }
    }

    /// <summary>INTEGER: the encoding form.</summary>
    public OerIntegerForm IntegerForm { get; private set; } = OerIntegerForm.SignedVariable;

    /// <summary>INTEGER: octets of the fixed forms.</summary>
    public int FixedOctets { get; private set; }

    /// <summary>INTEGER: true when the bounds fit in <see cref="Int128"/> (<see cref="Lower"/> and <see cref="Upper"/> are valid).</summary>
    public bool HasSmallBounds { get; private set; } = true;

    /// <summary>INTEGER: the lower bound, or <see cref="Int128.MinValue"/> when there is none.</summary>
    public Int128 Lower { get; private set; } = s_minimum;

    /// <summary>INTEGER: the upper bound, or <see cref="Int128.MaxValue"/> when there is none.</summary>
    public Int128 Upper { get; private set; } = Int128.MaxValue;

    private readonly IntegerSet _sizes = IntegerSet.Range(0, null);

    public IntegerSet Sizes
    {
        get => _sizes;
        private init
        {
            _sizes = value;
            FixedSize = value.LowerBound is { } lb && value.UpperBound is { } ub && lb == ub ? (long)lb : null;
        }
    }

    /// <summary>Fixed size when lb == ub (strings), otherwise null.</summary>
    public long? FixedSize { get; private set; }

    public int[] RootOrder { get; private init; } = [];

    public int[][] Additions { get; private init; } = [];

    public bool[] AdditionIsGroup { get; private init; } = [];

    public static OerTypeInfo Get(Asn1Type type) => type.GetRuleData(s_slot, Create);

    private static OerTypeInfo Create(Asn1Type type)
    {
        switch (type)
        {
            case CharacterStringType s:
                return CharacterRepertoire.IsKnownMultiplier(s.StringKind) ? new OerTypeInfo { Sizes = NonExtensibleConstraints.Size(type.Constraints) } : new OerTypeInfo();
            case BitStringType or OctetStringType:
                return new OerTypeInfo { Sizes = NonExtensibleConstraints.Size(type.Constraints) };
            case IStructuredTypeInfo st when type.Kind is Asn1TypeKind.Sequence or Asn1TypeKind.Set:
                {
                    var comps = st.ComponentInfos;
                    IEnumerable<int> roots = Enumerable.Range(0, comps.Count).Where(i => !comps[i].IsExtensionAddition && !comps[i].IsInRootPart2)
                        .Concat(Enumerable.Range(0, comps.Count).Where(i => !comps[i].IsExtensionAddition && comps[i].IsInRootPart2));
                    if (type.Kind == Asn1TypeKind.Set)
                    {
                        roots = roots.OrderBy(i => CanonicalTag(comps[i].Type));
                    }

                    var additions = comps.Select((c, i) => (c, i)).Where(x => x.c.IsExtensionAddition).GroupBy(x => x.c.ExtensionAdditionIndex).OrderBy(g => g.Key).ToList();
                    return new OerTypeInfo
                    {
                        RootOrder = [.. roots],
                        Additions = [.. additions.Select(g => g.Select(x => x.i).ToArray())],
                        AdditionIsGroup = [.. additions.Select(g => g.First().c.IsInExtensionGroup)],
                    };
                }

            default:
                return type.Kind == Asn1TypeKind.Integer ? new OerTypeInfo { Values = NonExtensibleConstraints.Integer(type.Constraints) } : new OerTypeInfo();
        }
    }

    /// <summary>X.696 18.2: untagged CHOICEs are ordered by the smallest tag of their root alternatives.</summary>
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
