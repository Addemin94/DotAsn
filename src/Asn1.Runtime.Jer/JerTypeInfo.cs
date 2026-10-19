using System.Collections.Frozen;
using Asn1.Constraints;

namespace Asn1.Runtime.Jer;

/// <summary>Cached JER data for a descriptor.</summary>
internal sealed class JerTypeInfo
{
    private static readonly Asn1RuleDataSlot<JerTypeInfo> s_slot = new();

    /// <summary>SEQUENCE/SET/CHOICE: component or alternative index by identifier.</summary>
    public FrozenDictionary<string, int> Members { get; private init; } = FrozenDictionary<string, int>.Empty;

    /// <summary>BIT STRING: the effective size constraint (X.697 7.2.8).</summary>
    public IntegerSet Sizes { get; private init; } = IntegerSet.Range(0, null);

    /// <summary>BIT STRING: the fixed size when the effective size constraint has identical bounds (24.2).</summary>
    public long? FixedSize => Sizes.LowerBound is { } lb && Sizes.UpperBound is { } ub && lb == ub ? (long)lb : null;

    /// <summary>BIT STRING, OCTET STRING: true if a contents constraint (CONTAINING) is present.</summary>
    public bool HasContentsConstraint { get; private init; }

    public static JerTypeInfo Get(Asn1Type type) => type.GetRuleData(s_slot, Create);

    private static JerTypeInfo Create(Asn1Type type) => type switch
    {
        IStructuredTypeInfo st => new JerTypeInfo
        {
            Members = st.ComponentInfos.Select((c, i) => (c.Name, i)).ToFrozenDictionary(x => x.Name, x => x.i, StringComparer.Ordinal),
        },
        BitStringType => new JerTypeInfo { Sizes = NonExtensibleConstraints.Size(type.Constraints), HasContentsConstraint = HasContents(type) },
        OctetStringType => new JerTypeInfo { HasContentsConstraint = HasContents(type) },
        _ => new JerTypeInfo(),
    };

    private static bool HasContents(Asn1Type type) => type.Constraints.Any(c => c.Root is OpaqueElement { Kind: "contents" });
}
