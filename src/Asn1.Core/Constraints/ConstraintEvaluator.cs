using System.Numerics;
using System.Text.RegularExpressions;

namespace Asn1.Constraints;

/// <summary>The kind of value presented to the constraint evaluator.</summary>
public enum ConstraintSubjectKind
{
    /// <summary>INTEGER (and ENUMERATED values by number).</summary>
    Integer,

    /// <summary>REAL.</summary>
    Real,

    /// <summary>BOOLEAN.</summary>
    Boolean,

    /// <summary>NULL.</summary>
    Null,

    /// <summary>A character string (also time types).</summary>
    String,

    /// <summary>A single character inside a permitted alphabet constraint.</summary>
    Character,

    /// <summary>OCTET STRING.</summary>
    Octets,

    /// <summary>BIT STRING.</summary>
    Bits,

    /// <summary>OBJECT IDENTIFIER or RELATIVE-OID.</summary>
    ObjectIdentifier,

    /// <summary>SEQUENCE OF / SET OF.</summary>
    Collection,

    /// <summary>SEQUENCE, SET or CHOICE.</summary>
    Structure,

    /// <summary>Any value the evaluator cannot inspect.</summary>
    Opaque,
}

/// <summary>A value presented to <see cref="ConstraintEvaluator"/>.</summary>
public abstract class ConstraintSubject
{
    /// <summary>The kind.</summary>
    public abstract ConstraintSubjectKind Kind { get; }

    /// <summary>Integer or character code point value.</summary>
    public virtual BigInteger Integer => throw new InvalidOperationException();

    /// <summary>Scalar value for single-value comparison.</summary>
    public virtual object? Scalar => null;

    /// <summary>Size for SIZE constraints.</summary>
    public virtual long Size => throw new InvalidOperationException();

    /// <summary>Character code points for permitted alphabet constraints.</summary>
    public virtual IEnumerable<int> Characters => [];

    /// <summary>Collection elements for WITH COMPONENT.</summary>
    public virtual IEnumerable<ConstraintSubject> Elements => [];

    /// <summary>Looks up a component of a structure; returns false if absent (or not the chosen alternative).</summary>
    public virtual bool TryGetComponent(string name, out ConstraintSubject? component)
    {
        component = null;
        return false;
    }

    /// <summary>Creates an INTEGER subject.</summary>
    public static ConstraintSubject FromInteger(BigInteger value) => new IntegerSubject(value, ConstraintSubjectKind.Integer);

    /// <summary>Creates a REAL subject.</summary>
    public static ConstraintSubject FromReal(double value) => new ScalarSubject(ConstraintSubjectKind.Real, value);

    /// <summary>Creates a BOOLEAN subject.</summary>
    public static ConstraintSubject FromBoolean(bool value) => new ScalarSubject(ConstraintSubjectKind.Boolean, value);

    /// <summary>Creates a NULL subject.</summary>
    public static ConstraintSubject FromNull() => new ScalarSubject(ConstraintSubjectKind.Null, Asn1Null.Value);

    /// <summary>Creates an OBJECT IDENTIFIER subject.</summary>
    public static ConstraintSubject FromObjectIdentifier(object oid) => new ScalarSubject(ConstraintSubjectKind.ObjectIdentifier, oid);

    /// <summary>Creates a character string subject.</summary>
    public static ConstraintSubject FromString(string value) => new StringSubject(value);

    /// <summary>Creates an OCTET STRING subject.</summary>
    public static ConstraintSubject FromOctets(ReadOnlyMemory<byte> value) => new OctetsSubject(value);

    /// <summary>Creates a BIT STRING subject.</summary>
    public static ConstraintSubject FromBits(Asn1BitString value) => new BitsSubject(value);

    /// <summary>Creates a collection subject.</summary>
    public static ConstraintSubject FromCollection(long count, Func<IEnumerable<ConstraintSubject>> elements) => new CollectionSubject(count, elements);

    /// <summary>Creates a structure subject from a component lookup.</summary>
    public static ConstraintSubject FromStructure(Func<string, ConstraintSubject?> lookup) => new StructureSubject(lookup);

    /// <summary>A subject whose value cannot be inspected.</summary>
    public static ConstraintSubject Opaque { get; } = new ScalarSubject(ConstraintSubjectKind.Opaque, null);

    private sealed class IntegerSubject(BigInteger value, ConstraintSubjectKind kind) : ConstraintSubject
    {
        public override ConstraintSubjectKind Kind => kind;

        public override BigInteger Integer => value;

        public override object? Scalar => value;
    }

    private sealed class ScalarSubject(ConstraintSubjectKind kind, object? value) : ConstraintSubject
    {
        public override ConstraintSubjectKind Kind => kind;

        public override object? Scalar => value;
    }

    private sealed class StringSubject(string value) : ConstraintSubject
    {
        public override ConstraintSubjectKind Kind => ConstraintSubjectKind.String;

        public override object? Scalar => value;

        public override long Size => CharacterRepertoire.CountCharacters(value);

        public override IEnumerable<int> Characters => CharacterRepertoire.CodePoints(value);
    }

    private sealed class OctetsSubject(ReadOnlyMemory<byte> value) : ConstraintSubject
    {
        public override ConstraintSubjectKind Kind => ConstraintSubjectKind.Octets;

        public override object? Scalar => value;

        public override long Size => value.Length;
    }

    private sealed class BitsSubject(Asn1BitString value) : ConstraintSubject
    {
        public override ConstraintSubjectKind Kind => ConstraintSubjectKind.Bits;

        public override object? Scalar => value;

        public override long Size => value.Length;
    }

    private sealed class CollectionSubject(long count, Func<IEnumerable<ConstraintSubject>> elements) : ConstraintSubject
    {
        public override ConstraintSubjectKind Kind => ConstraintSubjectKind.Collection;

        public override long Size => count;

        public override IEnumerable<ConstraintSubject> Elements => elements();
    }

    private sealed class StructureSubject(Func<string, ConstraintSubject?> lookup) : ConstraintSubject
    {
        public override ConstraintSubjectKind Kind => ConstraintSubjectKind.Structure;

        public override bool TryGetComponent(string name, out ConstraintSubject? component)
        {
            component = lookup(name);
            return component is not null;
        }
    }
}

/// <summary>Classification of a value against a constraint.</summary>
public enum ConstraintCheck
{
    /// <summary>The value is in the extension root.</summary>
    InRoot,

    /// <summary>The value is in the extension additions.</summary>
    InAdditions,

    /// <summary>The value is outside root and additions of an extensible constraint (a value of a later version).</summary>
    OutsideExtensible,

    /// <summary>The value violates the constraint.</summary>
    Violation,
}

/// <summary>Evaluates structured constraints against values.</summary>
public static class ConstraintEvaluator
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Regex?> s_patterns = new(StringComparer.Ordinal);

    /// <summary>
    /// Checks serially applied constraints. Per X.680, only the final constraint's extension marker
    /// determines extensibility; earlier constraints are evaluated as if not extensible.
    /// </summary>
    public static ConstraintCheck CheckSerial(IReadOnlyList<Asn1Constraint> constraints, ConstraintSubject subject)
    {
        var result = ConstraintCheck.InRoot;
        for (var i = 0; i < constraints.Count; i++)
        {
            var c = constraints[i];
            var r = i == constraints.Count - 1 ? Check(c, subject) : (Satisfies(c.WithoutExtension(), subject) ? ConstraintCheck.InRoot : ConstraintCheck.Violation);
            if (r == ConstraintCheck.Violation)
            {
                return r;
            }

            if (r > result)
            {
                result = r;
            }
        }

        return result;
    }

    /// <summary>Checks one constraint, distinguishing root, additions and extension-outside values.</summary>
    public static ConstraintCheck Check(Asn1Constraint constraint, ConstraintSubject subject)
    {
        if (constraint.Root is null || Evaluate(constraint.Root, subject))
        {
            if (constraint.Root is not null || !constraint.IsExtensible)
            {
                return ConstraintCheck.InRoot;
            }
        }

        if (!constraint.IsExtensible)
        {
            return ConstraintCheck.Violation;
        }

        if (constraint.Additions is not null && Evaluate(constraint.Additions, subject))
        {
            return ConstraintCheck.InAdditions;
        }

        return ConstraintCheck.OutsideExtensible;
    }

    /// <summary>True if the subject satisfies the root or the additions.</summary>
    public static bool Satisfies(Asn1Constraint constraint, ConstraintSubject subject) =>
        Check(constraint, subject) is ConstraintCheck.InRoot or ConstraintCheck.InAdditions;

    /// <summary>Evaluates an element.</summary>
    public static bool Evaluate(ConstraintElement element, ConstraintSubject subject)
    {
        switch (element)
        {
            case AllValuesElement:
            case OpaqueElement:
                return true;
            case SingleValueElement sv:
                return EqualsValue(sv.Value, subject);
            case ValueRangeElement r:
                return InRange(r, subject);
            case SizeElement s:
                if (subject.Kind is not (ConstraintSubjectKind.String or ConstraintSubjectKind.Octets or ConstraintSubjectKind.Bits or ConstraintSubjectKind.Collection))
                {
                    return true;
                }

                return Satisfies(s.Constraint, ConstraintSubject.FromInteger(subject.Size));
            case PermittedAlphabetElement pa:
                if (subject.Kind != ConstraintSubjectKind.String)
                {
                    return true;
                }

                foreach (var cp in subject.Characters)
                {
                    if (!Satisfies(pa.Constraint, new CharacterSubject(cp)))
                    {
                        return false;
                    }
                }

                return true;
            case PatternElement p:
                return subject.Kind != ConstraintSubjectKind.String || MatchesPattern(p.Pattern, (string)subject.Scalar!);
            case InnerTypeElement it:
                return subject.Kind != ConstraintSubjectKind.Collection || subject.Elements.All(e => Satisfies(it.Constraint, e));
            case InnerComponentsElement ic:
                return EvaluateComponents(ic, subject);
            case UnionElement u:
                return u.Items.Any(i => Evaluate(i, subject));
            case IntersectionElement x:
                return x.Items.All(i => Evaluate(i, subject));
            case ExceptElement e:
                return Evaluate(e.Included, subject) && !Evaluate(e.Excluded, subject);
            case NestedElement n:
                // X.680 52.4: a contained subtype contributes only the extension root of the referenced type's constraint.
                return n.IsContainedSubtype ? n.Inner.Root is null || Evaluate(n.Inner.Root, subject) : Satisfies(n.Inner, subject);
            default:
                return true;
        }
    }

    private static bool EvaluateComponents(InnerComponentsElement ic, ConstraintSubject subject)
    {
        if (subject.Kind != ConstraintSubjectKind.Structure)
        {
            return true;
        }

        foreach (var c in ic.Components)
        {
            var present = subject.TryGetComponent(c.Name, out var component);
            switch (c.Presence)
            {
                case ComponentPresenceConstraint.Present when !present:
                    return false;
                case ComponentPresenceConstraint.Absent when present:
                    return false;
            }

            if (present && c.Value is not null && !Satisfies(c.Value, component!))
            {
                return false;
            }
        }

        return true;
    }

    private static bool EqualsValue(object value, ConstraintSubject subject)
    {
        switch (subject.Kind)
        {
            case ConstraintSubjectKind.Integer:
                return value is BigInteger b && b == subject.Integer;
            case ConstraintSubjectKind.Character:
                return value is string s ? CharacterRepertoire.CodePoints(s).Contains((int)subject.Integer) : value is BigInteger cb && cb == subject.Integer;
            case ConstraintSubjectKind.Real:
                return value switch
                {
                    double d => d.Equals((double)subject.Scalar!),
                    BigInteger bi => (double)bi == (double)subject.Scalar!,
                    _ => false,
                };
            case ConstraintSubjectKind.Octets:
                return value is byte[] bytes && ((ReadOnlyMemory<byte>)subject.Scalar!).Span.SequenceEqual(bytes);
            case ConstraintSubjectKind.Bits:
                return value is Asn1BitString bits && (bits.Equals(subject.Scalar) || bits.TrimTrailingZeros().Equals(((Asn1BitString)subject.Scalar!).TrimTrailingZeros()));
            case ConstraintSubjectKind.String:
            case ConstraintSubjectKind.Boolean:
            case ConstraintSubjectKind.Null:
            case ConstraintSubjectKind.ObjectIdentifier:
                return Equals(value, subject.Scalar);
            default:
                return true;
        }
    }

    private static bool InRange(ValueRangeElement r, ConstraintSubject subject)
    {
        switch (subject.Kind)
        {
            case ConstraintSubjectKind.Integer:
                {
                    var v = subject.Integer;
                    if (r.Lower is BigInteger lo && (r.LowerOpen ? v <= lo : v < lo))
                    {
                        return false;
                    }

                    if (r.Upper is BigInteger hi && (r.UpperOpen ? v >= hi : v > hi))
                    {
                        return false;
                    }

                    return true;
                }

            case ConstraintSubjectKind.Character:
                {
                    var v = (int)subject.Integer;
                    var lo = CharBound(r.Lower);
                    var hi = CharBound(r.Upper);
                    if (lo is not null && (r.LowerOpen ? v <= lo : v < lo))
                    {
                        return false;
                    }

                    return hi is null || (r.UpperOpen ? v < hi : v <= hi);
                }

            case ConstraintSubjectKind.Real:
                {
                    var v = (double)subject.Scalar!;
                    var lo = RealBound(r.Lower);
                    var hi = RealBound(r.Upper);
                    if (lo is not null && (r.LowerOpen ? v <= lo : v < lo))
                    {
                        return false;
                    }

                    return hi is null || (r.UpperOpen ? v < hi : v <= hi);
                }

            case ConstraintSubjectKind.String:
                {
                    // Value ranges on time types compare lexically on the canonical string form.
                    var v = (string)subject.Scalar!;
                    if (r.Lower is string lo && (r.LowerOpen ? string.CompareOrdinal(v, lo) <= 0 : string.CompareOrdinal(v, lo) < 0))
                    {
                        return false;
                    }

                    return r.Upper is not string hi || (r.UpperOpen ? string.CompareOrdinal(v, hi) < 0 : string.CompareOrdinal(v, hi) <= 0);
                }

            default:
                return true;
        }
    }

    private static int? CharBound(object? bound) => bound switch
    {
        string s when s.Length > 0 => CharacterRepertoire.CodePoints(s).First(),
        BigInteger b => (int)b,
        _ => null,
    };

    private static double? RealBound(object? bound) => bound switch
    {
        double d => d,
        BigInteger b => (double)b,
        _ => null,
    };

    /// <summary>Evaluates an X.680 Annex A regular expression (translated to .NET syntax).</summary>
    public static bool MatchesPattern(string pattern, string value)
    {
        var regex = s_patterns.GetOrAdd(pattern, static p =>
        {
            try
            {
                return new Regex("^(?:" + Asn1RegexTranslator.Translate(p) + ")$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            }
            catch (ArgumentException)
            {
                return null;
            }
        });
        if (regex is null)
        {
            return true;
        }

        try
        {
            return regex.IsMatch(value);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private sealed class CharacterSubject(int codePoint) : ConstraintSubject
    {
        public override ConstraintSubjectKind Kind => ConstraintSubjectKind.Character;

        public override BigInteger Integer => codePoint;

        public override object? Scalar => codePoint;
    }
}
