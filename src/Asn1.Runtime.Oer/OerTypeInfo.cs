using Asn1.Constraints;

namespace Asn1.Runtime.Oer;

/// <summary>Cached OER layout data for a descriptor.</summary>
internal sealed class OerTypeInfo
{
    private static readonly Asn1RuleDataSlot<OerTypeInfo> s_slot = new();

    public IntegerSet Values { get; private init; } = IntegerSet.All;

    public IntegerSet Sizes { get; private init; } = IntegerSet.Range(0, null);

    /// <summary>Fixed size when lb == ub (strings), otherwise null.</summary>
    public long? FixedSize => Sizes.LowerBound is { } lb && Sizes.UpperBound is { } ub && lb == ub ? (long)lb : null;

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
