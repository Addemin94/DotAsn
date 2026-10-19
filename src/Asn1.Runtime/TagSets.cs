using System.Collections.Frozen;

namespace Asn1.Runtime;

/// <summary>
/// Computes the set of tags with which a value of a type can begin (X.680 8.6 / 25.5): the outermost tag, or
/// the union over the alternatives of an untagged CHOICE. Open types can begin with any tag (null).
/// </summary>
public static class TagSets
{
    private static readonly Asn1RuleDataSlot<Holder> s_slot = new();

    private sealed class Holder(FrozenSet<Asn1Tag>? tags)
    {
        public FrozenSet<Asn1Tag>? Tags { get; } = tags;
    }

    /// <summary>The possible first tags, or null if any tag is possible.</summary>
    public static FrozenSet<Asn1Tag>? PossibleTags(Asn1Type type) =>
        type.GetRuleData(s_slot, static t => new Holder(Compute(t, 0))).Tags;

    private static FrozenSet<Asn1Tag>? Compute(Asn1Type type, int depth)
    {
        if (!type.Tags.IsEmpty)
        {
            return FrozenSet.Create(type.Tags[0].Tag);
        }

        if (type is IStructuredTypeInfo { } s && type.Kind == Asn1TypeKind.Choice && depth < 64)
        {
            var set = new HashSet<Asn1Tag>();
            foreach (var alt in s.ComponentInfos)
            {
                var inner = Compute(alt.Type, depth + 1);
                if (inner is null)
                {
                    return null;
                }

                set.UnionWith(inner);
            }

            return set.ToFrozenSet();
        }

        return null;
    }

    /// <summary>The smallest possible tag in canonical order (X.680 8.6), used for CER SET ordering and PER CHOICE indices.</summary>
    public static Asn1Tag? CanonicalTag(Asn1Type type)
    {
        var tags = PossibleTags(type);
        return tags is null || tags.Count == 0 ? null : tags.Min();
    }
}
