using Asn1.Constraints;

namespace Asn1.Runtime.Oer;

/// <summary>OER-specific constraint checks (the effective value and size constraints are <see cref="NonExtensibleConstraints"/>).</summary>
internal static class OerVisibleConstraints
{
    /// <summary>
    /// True if a REAL type carries an inner subtype constraint (WITH COMPONENTS) that could select the IEEE 754
    /// binary32/binary64 forms of X.696 12.2/12.3. Such constraints are not analysed by this implementation.
    /// </summary>
    public static bool HasRealComponentConstraint(IReadOnlyList<Asn1Constraint> constraints) => constraints.Any(c => HasInner(c.Root) || HasInner(c.Additions));

    private static bool HasInner(ConstraintElement? e) => e switch
    {
        OpaqueElement { Kind: "associated-type" } or InnerComponentsElement or InnerTypeElement => true,
        UnionElement u => u.Items.Any(HasInner),
        IntersectionElement x => x.Items.Any(HasInner),
        ExceptElement x => HasInner(x.Included),
        NestedElement n => HasInner(n.Inner.Root) || HasInner(n.Inner.Additions),
        _ => false,
    };
}
