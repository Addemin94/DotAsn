using System.Numerics;

namespace Asn1.Constraints;

/// <summary>
/// Computes integer value sets described by constraints (ignoring elements that do not restrict integers).
/// These helpers are encoding-independent; encoding rules apply their own visibility rules on top.
/// </summary>
public static class ConstraintSets
{
    /// <summary>
    /// The integer set permitted by the root of <paramref name="constraint"/>, or null if the root contains
    /// elements that cannot be represented as integer intervals.
    /// </summary>
    public static IntegerSet? IntegerRoot(Asn1Constraint constraint) =>
        constraint.Root is null ? IntegerSet.All : IntegerElement(constraint.Root);

    /// <summary>The integer set of root plus additions, or null if not representable.</summary>
    public static IntegerSet? IntegerRootAndAdditions(Asn1Constraint constraint)
    {
        var root = IntegerRoot(constraint);
        if (constraint.Additions is null || root is null)
        {
            return root;
        }

        var add = IntegerElement(constraint.Additions);
        return add is null ? null : root.Union(add);
    }

    /// <summary>The integer set described by an element, or null if not representable.</summary>
    public static IntegerSet? IntegerElement(ConstraintElement element)
    {
        switch (element)
        {
            case AllValuesElement:
                return IntegerSet.All;
            case SingleValueElement { Value: BigInteger b }:
                return IntegerSet.Single(b);
            case ValueRangeElement r when (r.Lower is null or BigInteger) && (r.Upper is null or BigInteger):
                {
                    BigInteger? lo = r.Lower is BigInteger l ? (r.LowerOpen ? l + 1 : l) : null;
                    BigInteger? hi = r.Upper is BigInteger h ? (r.UpperOpen ? h - 1 : h) : null;
                    return IntegerSet.Range(lo, hi);
                }

            case UnionElement u:
                {
                    var acc = IntegerSet.Empty;
                    foreach (var i in u.Items)
                    {
                        var s = IntegerElement(i);
                        if (s is null)
                        {
                            return null;
                        }

                        acc = acc.Union(s);
                    }

                    return acc;
                }

            case IntersectionElement x:
                {
                    var acc = IntegerSet.All;
                    foreach (var i in x.Items)
                    {
                        var s = IntegerElement(i);
                        if (s is null)
                        {
                            return null;
                        }

                        acc = acc.Intersect(s);
                    }

                    return acc;
                }

            case ExceptElement e:
                {
                    var a = IntegerElement(e.Included);
                    var b = IntegerElement(e.Excluded);
                    return a is null || b is null ? null : a.Except(b);
                }

            case NestedElement { IsContainedSubtype: true } n:
                return IntegerRoot(n.Inner);
            case NestedElement n:
                return IntegerRootAndAdditions(n.Inner);
            default:
                return null;
        }
    }

    /// <summary>The integer set permitted by serially applied constraints (root and additions of each), or null.</summary>
    public static IntegerSet? IntegerSerial(IEnumerable<Asn1Constraint> constraints)
    {
        var acc = IntegerSet.All;
        foreach (var c in constraints)
        {
            var s = IntegerRootAndAdditions(c);
            if (s is null)
            {
                return null;
            }

            acc = acc.Intersect(s);
        }

        return acc;
    }
}
