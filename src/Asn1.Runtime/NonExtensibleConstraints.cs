using System.Numerics;
using Asn1.Constraints;

namespace Asn1.Runtime;

/// <summary>
/// Effective constraints for encoding rules whose visible constraints are the non-extensible value and size
/// constraints: OER (X.696 8.2) and JER (X.697 7.2, BIT STRING sizes). Extensible constraints are not visible; serial
/// application removes earlier extensibility (X.696 8.2.3, X.697 7.2.3); unions with invisible parts are invisible,
/// invisible intersection operands are ignored, EXCEPT is ignored, and contained subtypes contribute their roots.
/// Which types a rule applies the result to is the rule's decision.
/// </summary>
public static class NonExtensibleConstraints
{
    private sealed record Result(IntegerSet Set, bool Extensible);

    /// <summary>The effective value constraint of an INTEGER (X.696 8.2.7).</summary>
    public static IntegerSet Integer(IReadOnlyList<Asn1Constraint> constraints) => Serial(constraints, size: false);

    /// <summary>The effective size constraint of a string type (X.696 8.2.8, X.697 7.2.8).</summary>
    public static IntegerSet Size(IReadOnlyList<Asn1Constraint> constraints) => Serial(constraints, size: true).Intersect(IntegerSet.Range(0, null));

    private static IntegerSet Serial(IReadOnlyList<Asn1Constraint> constraints, bool size)
    {
        var list = constraints.Where(c => !(c.Root is OpaqueElement { Kind: "table" or "user-defined" or "contents" } && c.Additions is null && !c.IsExtensible)).ToList();
        var acc = IntegerSet.All;
        for (var i = 0; i < list.Count; i++)
        {
            var r = Spec(list[i], size);
            if (r is null || (i == list.Count - 1 && r.Extensible))
            {
                // Not visible, or the final constraint is extensible (8.2.2 g, 8.2.3).
                continue;
            }

            acc = acc.Intersect(r.Set);
        }

        return acc;
    }

    private static Result? Spec(Asn1Constraint c, bool size)
    {
        var root = c.Root is null ? new Result(IntegerSet.All, false) : Element(c.Root, size);
        return root is null ? null : new Result(root.Set, c.IsExtensible || root.Extensible);
    }

    private static Result? Element(ConstraintElement e, bool size)
    {
        switch (e)
        {
            case AllValuesElement:
                return new Result(IntegerSet.All, false);
            case UnionElement u:
                {
                    var acc = IntegerSet.Empty;
                    var ext = false;
                    foreach (var item in u.Items)
                    {
                        if (Element(item, size) is not { } r)
                        {
                            return null;
                        }

                        acc = acc.Union(r.Set);
                        ext |= r.Extensible;
                    }

                    return new Result(acc, ext);
                }

            case IntersectionElement x:
                {
                    Result? acc = null;
                    foreach (var item in x.Items)
                    {
                        if (Element(item, size) is { } r)
                        {
                            acc = acc is null ? r : new Result(acc.Set.Intersect(r.Set), acc.Extensible || r.Extensible);
                        }
                    }

                    return acc;
                }

            case ExceptElement ex:
                return Element(ex.Included, size);
            case NestedElement { IsContainedSubtype: true } n:
                return Spec(n.Inner, size) is { } cr ? new Result(cr.Set, false) : null;
            case NestedElement n:
                return Spec(n.Inner, size);
            case SingleValueElement { Value: BigInteger b } when !size:
                return new Result(IntegerSet.Single(b), false);
            case ValueRangeElement r when !size && r.Lower is null or BigInteger && r.Upper is null or BigInteger:
                {
                    BigInteger? lo = r.Lower is BigInteger l ? (r.LowerOpen ? l + 1 : l) : null;
                    BigInteger? hi = r.Upper is BigInteger h ? (r.UpperOpen ? h - 1 : h) : null;
                    return new Result(IntegerSet.Range(lo, hi), false);
                }

            case SizeElement s when size:
                return Spec(s.Constraint, size: false) is { } sr && !sr.Extensible ? sr : null;
            default:
                return null;
        }
    }
}
