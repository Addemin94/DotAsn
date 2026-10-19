using System.Numerics;
using Asn1.Constraints;

namespace Asn1.Runtime.Per;

/// <summary>An effective PER-visible constraint on whole numbers (values or sizes).</summary>
/// <param name="Root">The values in the extension root (an over-approximation for PER is the min..max of this set).</param>
/// <param name="IsExtensible">True if the type is extensible for PER encodings.</param>
public sealed record EffectiveConstraint(IntegerSet Root, bool IsExtensible)
{
    /// <summary>No constraint.</summary>
    public static readonly EffectiveConstraint None = new(IntegerSet.All, false);

    /// <summary>The lower bound, or null.</summary>
    public BigInteger? LowerBound => Root.LowerBound;

    /// <summary>The upper bound, or null.</summary>
    public BigInteger? UpperBound => Root.UpperBound;
}

/// <summary>
/// Derives effective PER-visible constraints (X.691 10.3) from constraint trees, applying the rules for serial
/// application (X.680 50.11), set arithmetic extensibility (X.680 50.4/50.5), contained subtypes (X.680 52.4),
/// unions with non-visible parts, intersections, and EXCEPT (X.691 10.3.21).
/// </summary>
public static class PerVisibleConstraints
{
    private sealed record Result(IntegerSet Root, IntegerSet All, bool Extensible);

    private enum Domain
    {
        Integer,
        Size,
        Characters,
        Alphabet,
    }

    /// <summary>The effective constraint of an INTEGER.</summary>
    public static EffectiveConstraint Integer(IReadOnlyList<Asn1Constraint> constraints) => Serial(constraints, Domain.Integer);

    /// <summary>The effective size constraint (BIT STRING, OCTET STRING, SEQUENCE OF, SET OF, known-multiplier strings).</summary>
    public static EffectiveConstraint Size(IReadOnlyList<Asn1Constraint> constraints)
    {
        var e = Serial(constraints, Domain.Size);
        return e with { Root = e.Root.Intersect(IntegerSet.Range(0, null)) };
    }

    /// <summary>
    /// The effective permitted alphabet of a known-multiplier character string as a set of code points, or null
    /// when no PER-visible permitted-alphabet constraint applies.
    /// </summary>
    public static IntegerSet? Alphabet(IReadOnlyList<Asn1Constraint> constraints)
    {
        IntegerSet? acc = null;
        foreach (var c in GeneralFiltered(constraints))
        {
            // An extensible constraint makes its permitted alphabet invisible (X.691 10.3.11).
            if (c.IsExtensible)
            {
                continue;
            }

            var r = Analyze(c.Root, Domain.Alphabet);
            if (r is null || r.Extensible || r.Root == IntegerSet.All)
            {
                continue;
            }

            acc = acc is null ? r.Root : acc.Intersect(r.Root);
        }

        return acc;
    }

    /// <summary>Removes general constraints (table, user-defined, contents), which do not affect PER (X.680 50.11 note).</summary>
    private static IEnumerable<Asn1Constraint> GeneralFiltered(IReadOnlyList<Asn1Constraint> constraints) =>
        constraints.Where(c => !(c.Root is OpaqueElement { Kind: "table" or "user-defined" or "contents" } && c.Additions is null && !c.IsExtensible));

    private static EffectiveConstraint Serial(IReadOnlyList<Asn1Constraint> constraints, Domain domain)
    {
        var list = GeneralFiltered(constraints).ToList();
        var acc = IntegerSet.All;
        var extensible = false;
        for (var i = 0; i < list.Count; i++)
        {
            var r = AnalyzeSpec(list[i], domain);
            var last = i == list.Count - 1;
            if (r is null)
            {
                // Not PER-visible: ignored, and a final invisible constraint makes the type non-extensible (X.691 10.3.20).
                if (last)
                {
                    extensible = false;
                }

                continue;
            }

            acc = acc.Intersect(r.Root);
            if (last)
            {
                extensible = r.Extensible;
            }
        }

        return new EffectiveConstraint(acc, extensible);
    }

    private static Result? AnalyzeSpec(Asn1Constraint c, Domain domain)
    {
        var root = c.Root is null ? new Result(IntegerSet.All, IntegerSet.All, false) : Analyze(c.Root, domain);
        if (root is null)
        {
            return null;
        }

        var all = root.All;
        if (c.Additions is not null && Analyze(c.Additions, domain) is { } adds)
        {
            all = all.Union(adds.All);
        }

        return new Result(root.Root, all, c.IsExtensible || root.Extensible);
    }

    private static Result? Analyze(ConstraintElement? e, Domain domain)
    {
        switch (e)
        {
            case null:
            case AllValuesElement:
                return new Result(IntegerSet.All, IntegerSet.All, false);

            case UnionElement u:
                {
                    var root = IntegerSet.Empty;
                    var all = IntegerSet.Empty;
                    var ext = false;
                    foreach (var item in u.Items)
                    {
                        var r = Analyze(item, domain);
                        if (r is null)
                        {
                            return null;
                        }

                        root = root.Union(r.Root);
                        all = all.Union(r.All);
                        ext |= r.Extensible;
                    }

                    return new Result(root, all, ext);
                }

            case IntersectionElement x:
                {
                    Result? acc = null;
                    foreach (var item in x.Items)
                    {
                        if (Analyze(item, domain) is not { } r)
                        {
                            continue;
                        }

                        acc = acc is null ? r : new Result(acc.Root.Intersect(r.Root), acc.All.Intersect(r.All), acc.Extensible || r.Extensible);
                    }

                    return acc;
                }

            case ExceptElement ex:
                // The EXCEPT part is ignored entirely for PER (X.691 10.3.21).
                return Analyze(ex.Included, domain);

            case NestedElement { IsContainedSubtype: true } n:
                {
                    var r = AnalyzeSpec(n.Inner, domain);
                    return r is null ? null : new Result(r.Root, r.Root, false);
                }

            case NestedElement n:
                return AnalyzeSpec(n.Inner, domain);

            case SingleValueElement sv:
                return domain switch
                {
                    Domain.Integer when sv.Value is BigInteger b => Single(b),
                    Domain.Characters when sv.Value is string s => Chars(s),
                    _ => null,
                };

            case ValueRangeElement r:
                {
                    if (domain == Domain.Integer && r.Lower is null or BigInteger && r.Upper is null or BigInteger)
                    {
                        BigInteger? lo = r.Lower is BigInteger l ? (r.LowerOpen ? l + 1 : l) : null;
                        BigInteger? hi = r.Upper is BigInteger h ? (r.UpperOpen ? h - 1 : h) : null;
                        var set = IntegerSet.Range(lo, hi);
                        return new Result(set, set, false);
                    }

                    if (domain == Domain.Characters && r.Lower is null or string && r.Upper is null or string)
                    {
                        BigInteger? lo = r.Lower is string ls ? CharacterRepertoire.CodePoints(ls).First() + (r.LowerOpen ? 1 : 0) : null;
                        BigInteger? hi = r.Upper is string us ? CharacterRepertoire.CodePoints(us).First() - (r.UpperOpen ? 1 : 0) : null;
                        var set = IntegerSet.Range(lo, hi);
                        return new Result(set, set, false);
                    }

                    return null;
                }

            case SizeElement s:
                return domain switch
                {
                    Domain.Size => AnalyzeSpec(s.Constraint, Domain.Integer),
                    Domain.Alphabet => new Result(IntegerSet.All, IntegerSet.All, false),
                    _ => null,
                };

            case PermittedAlphabetElement pa:
                {
                    if (domain == Domain.Size)
                    {
                        return new Result(IntegerSet.All, IntegerSet.All, false);
                    }

                    if (domain != Domain.Alphabet)
                    {
                        return null;
                    }

                    var r = AnalyzeSpec(pa.Constraint, Domain.Characters);
                    return r is null || r.Extensible ? null : new Result(r.Root, r.Root, false);
                }

            default:
                // PATTERN, inner subtyping, settings, opaque: not PER-visible.
                return null;
        }
    }

    private static Result Single(BigInteger b)
    {
        var set = IntegerSet.Single(b);
        return new Result(set, set, false);
    }

    private static Result Chars(string s)
    {
        var set = IntegerSet.Empty;
        foreach (var cp in CharacterRepertoire.CodePoints(s))
        {
            set = set.Union(IntegerSet.Single(cp));
        }

        return new Result(set, set, false);
    }
}
