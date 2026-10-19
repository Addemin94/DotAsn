using System.Collections.Frozen;

namespace Asn1.Runtime.Xer;

/// <summary>Cached XER data for a descriptor.</summary>
internal sealed class XerTypeInfo
{
    private static readonly Asn1RuleDataSlot<XerTypeInfo> s_slot = new();

    /// <summary>The "NonParameterizedTypeName" of the type.</summary>
    public string TypeName { get; private init; } = string.Empty;

    /// <summary>SEQUENCE/SET: component indices in encoding order; CHOICE: unused.</summary>
    public int[] Order { get; private init; } = [];

    /// <summary>SEQUENCE/SET/CHOICE: component or alternative index by identifier.</summary>
    public FrozenDictionary<string, int> Members { get; private init; } = FrozenDictionary<string, int>.Empty;

    /// <summary>SEQUENCE OF/SET OF: true for "XMLValueList" (X.680 26.5-26.7), false for "XMLDelimitedItemList".</summary>
    public bool IsValueList { get; private init; }

    /// <summary>SEQUENCE OF/SET OF: the element name of an "XMLDelimitedItem" (26.9, 26.10).</summary>
    public string ItemName { get; private init; } = string.Empty;

    public static XerTypeInfo Get(Asn1Type type) => type.GetRuleData(s_slot, Create);

    private static XerTypeInfo Create(Asn1Type type)
    {
        var name = XerSyntax.TypeName(type);
        switch (type)
        {
            case ICollectionTypeInfo c:
                {
                    var element = c.ElementTypeInfo;
                    return new XerTypeInfo
                    {
                        TypeName = name,
                        IsValueList = c.ElementName is null && element.Kind is Asn1TypeKind.Boolean or Asn1TypeKind.Enumerated or Asn1TypeKind.Choice,
                        ItemName = c.ElementName ?? XerSyntax.TypeName(element),
                    };
                }

            case IStructuredTypeInfo st:
                {
                    var comps = st.ComponentInfos;
                    IEnumerable<int> order = Enumerable.Range(0, comps.Count);
                    if (type.Kind == Asn1TypeKind.Set)
                    {
                        // X.693 9.6: root components in canonical tag order, then additions in definition order.
                        order = order.Where(i => !comps[i].IsExtensionAddition).OrderBy(i => TagSets.CanonicalTag(comps[i].Type) ?? new Asn1Tag(TagClass.Private, int.MaxValue))
                            .Concat(order.Where(i => comps[i].IsExtensionAddition));
                    }

                    return new XerTypeInfo
                    {
                        TypeName = name,
                        Order = [.. order],
                        Members = comps.Select((c, i) => (c.Name, i)).ToFrozenDictionary(x => x.Name, x => x.i, StringComparer.Ordinal),
                    };
                }

            default:
                return new XerTypeInfo { TypeName = name };
        }
    }
}
