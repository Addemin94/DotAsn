using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace Asn1.Constraints;

/// <summary>
/// One parenthesized constraint, i.e. X.680 <c>ElementSetSpecs</c>: an extension root, an optional extension
/// marker, and optional extension additions. Element values use the canonical runtime representation
/// (<see cref="BigInteger"/> for INTEGER/ENUMERATED, <see cref="string"/> for character strings,
/// <see cref="byte"/>[] for OCTET STRING, <see cref="Asn1BitString"/>, <see cref="ObjectIdentifier"/>, <see cref="double"/>, <see cref="bool"/>).
/// </summary>
public sealed class Asn1Constraint
{
    /// <summary>Creates a constraint.</summary>
    public Asn1Constraint(ConstraintElement? root, bool isExtensible = false, ConstraintElement? additions = null)
    {
        Root = root;
        IsExtensible = isExtensible;
        Additions = additions;
    }

    /// <summary>The extension root, or null for <c>(...)</c>.</summary>
    public ConstraintElement? Root { get; }

    /// <summary>True if the constraint carries an extension marker.</summary>
    public bool IsExtensible { get; }

    /// <summary>Extension additions.</summary>
    public ConstraintElement? Additions { get; }

    /// <summary>Returns a copy without extensibility (used for non-final serial constraints).</summary>
    public Asn1Constraint WithoutExtension() => IsExtensible ? new Asn1Constraint(Root ?? AllValuesElement.Instance) : this;

    /// <inheritdoc />
    public override string ToString()
    {
        var sb = new StringBuilder("(");
        if (Root is not null)
        {
            sb.Append(Root);
        }

        if (IsExtensible)
        {
            sb.Append(Root is null ? "..." : ", ...");
            if (Additions is not null)
            {
                sb.Append(", ").Append(Additions);
            }
        }

        return sb.Append(')').ToString();
    }
}

/// <summary>Base class of constraint elements (X.680 51 and X.682).</summary>
public abstract class ConstraintElement
{
    /// <summary>Renders ASN.1 notation.</summary>
    public abstract override string ToString();

    internal static string Format(object? value) => value switch
    {
        null => "?",
        string s => "\"" + s.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"",
        BigInteger b => b.ToString(CultureInfo.InvariantCulture),
        bool b => b ? "TRUE" : "FALSE",
        byte[] bytes => "'" + Convert.ToHexString(bytes) + "'H",
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        ObjectIdentifier oid => "{ " + string.Join(' ', oid.Arcs) + " }",
        _ => value.ToString() ?? "?",
    };
}

/// <summary>Permits every value (also the result of an empty/unsupported constraint).</summary>
public sealed class AllValuesElement : ConstraintElement
{
    /// <summary>The singleton.</summary>
    public static readonly AllValuesElement Instance = new();

    private AllValuesElement()
    {
    }

    /// <inheritdoc />
    public override string ToString() => "ALL";
}

/// <summary>A single value.</summary>
public sealed class SingleValueElement : ConstraintElement
{
    /// <summary>Creates a single value element.</summary>
    public SingleValueElement(object value) => Value = value;

    /// <summary>The value in canonical runtime representation.</summary>
    public object Value { get; }

    /// <inheritdoc />
    public override string ToString() => Format(Value);
}

/// <summary>A value range; null bounds denote MIN/MAX.</summary>
public sealed class ValueRangeElement : ConstraintElement
{
    /// <summary>Creates a range.</summary>
    public ValueRangeElement(object? lower, object? upper, bool lowerOpen = false, bool upperOpen = false)
    {
        Lower = lower;
        Upper = upper;
        LowerOpen = lowerOpen;
        UpperOpen = upperOpen;
    }

    /// <summary>Lower bound or null for MIN.</summary>
    public object? Lower { get; }

    /// <summary>Upper bound or null for MAX.</summary>
    public object? Upper { get; }

    /// <summary>True if the lower bound is excluded.</summary>
    public bool LowerOpen { get; }

    /// <summary>True if the upper bound is excluded.</summary>
    public bool UpperOpen { get; }

    /// <inheritdoc />
    public override string ToString() =>
        (Lower is null ? "MIN" : Format(Lower)) + (LowerOpen ? "<" : string.Empty) + ".." + (UpperOpen ? "<" : string.Empty) + (Upper is null ? "MAX" : Format(Upper));
}

/// <summary><c>SIZE (constraint)</c>.</summary>
public sealed class SizeElement : ConstraintElement
{
    /// <summary>Creates a size element.</summary>
    public SizeElement(Asn1Constraint constraint) => Constraint = constraint;

    /// <summary>Constraint on the number of items (characters, octets, bits or elements).</summary>
    public Asn1Constraint Constraint { get; }

    /// <inheritdoc />
    public override string ToString() => "SIZE" + Constraint;
}

/// <summary><c>FROM (constraint)</c>.</summary>
public sealed class PermittedAlphabetElement : ConstraintElement
{
    /// <summary>Creates a permitted alphabet element.</summary>
    public PermittedAlphabetElement(Asn1Constraint constraint) => Constraint = constraint;

    /// <summary>Constraint on individual characters.</summary>
    public Asn1Constraint Constraint { get; }

    /// <inheritdoc />
    public override string ToString() => "FROM" + Constraint;
}

/// <summary><c>PATTERN "regex"</c> using the X.680 Annex A regular expression syntax.</summary>
public sealed class PatternElement : ConstraintElement
{
    /// <summary>Creates a pattern element.</summary>
    public PatternElement(string pattern) => Pattern = pattern;

    /// <summary>The X.680 regular expression.</summary>
    public string Pattern { get; }

    /// <inheritdoc />
    public override string ToString() => "PATTERN " + Format(Pattern);
}

/// <summary><c>WITH COMPONENT (constraint)</c> on SEQUENCE OF / SET OF elements.</summary>
public sealed class InnerTypeElement : ConstraintElement
{
    /// <summary>Creates an inner type element.</summary>
    public InnerTypeElement(Asn1Constraint constraint) => Constraint = constraint;

    /// <summary>Constraint applied to every element.</summary>
    public Asn1Constraint Constraint { get; }

    /// <inheritdoc />
    public override string ToString() => "WITH COMPONENT " + Constraint;
}

/// <summary>Presence constraint in WITH COMPONENTS.</summary>
public enum ComponentPresenceConstraint
{
    /// <summary>Unspecified.</summary>
    None,

    /// <summary>PRESENT</summary>
    Present,

    /// <summary>ABSENT</summary>
    Absent,

    /// <summary>OPTIONAL</summary>
    Optional,
}

/// <summary>A named component constraint.</summary>
/// <param name="Name">Component identifier.</param>
/// <param name="Value">Value constraint, if any.</param>
/// <param name="Presence">Presence constraint.</param>
public sealed record ComponentConstraint(string Name, Asn1Constraint? Value, ComponentPresenceConstraint Presence);

/// <summary><c>WITH COMPONENTS { ... }</c> on SEQUENCE, SET or CHOICE.</summary>
public sealed class InnerComponentsElement : ConstraintElement
{
    /// <summary>Creates an inner components element.</summary>
    public InnerComponentsElement(bool isPartial, ImmutableArray<ComponentConstraint> components)
    {
        IsPartial = isPartial;
        Components = components;
    }

    /// <summary>True for a partial specification.</summary>
    public bool IsPartial { get; }

    /// <summary>The named constraints.</summary>
    public ImmutableArray<ComponentConstraint> Components { get; }

    /// <inheritdoc />
    public override string ToString()
    {
        var parts = Components.Select(c => c.Name + (c.Value is null ? string.Empty : " " + c.Value) + c.Presence switch
        {
            ComponentPresenceConstraint.Present => " PRESENT",
            ComponentPresenceConstraint.Absent => " ABSENT",
            ComponentPresenceConstraint.Optional => " OPTIONAL",
            _ => string.Empty,
        });
        return "WITH COMPONENTS { " + (IsPartial ? "..., " : string.Empty) + string.Join(", ", parts) + " }";
    }
}

/// <summary>Union of elements.</summary>
public sealed class UnionElement : ConstraintElement
{
    /// <summary>Creates a union.</summary>
    public UnionElement(ImmutableArray<ConstraintElement> items) => Items = items;

    /// <summary>Operands.</summary>
    public ImmutableArray<ConstraintElement> Items { get; }

    /// <inheritdoc />
    public override string ToString() => string.Join(" | ", Items.Select(i => i is IntersectionElement or ExceptElement ? "(" + i + ")" : i.ToString()));
}

/// <summary>Intersection of elements.</summary>
public sealed class IntersectionElement : ConstraintElement
{
    /// <summary>Creates an intersection.</summary>
    public IntersectionElement(ImmutableArray<ConstraintElement> items) => Items = items;

    /// <summary>Operands.</summary>
    public ImmutableArray<ConstraintElement> Items { get; }

    /// <inheritdoc />
    public override string ToString() => string.Join(" ^ ", Items.Select(i => i is UnionElement ? "(" + i + ")" : i.ToString()));
}

/// <summary><c>Included EXCEPT Excluded</c> (and <c>ALL EXCEPT x</c> with <see cref="AllValuesElement"/>).</summary>
public sealed class ExceptElement : ConstraintElement
{
    /// <summary>Creates an exclusion.</summary>
    public ExceptElement(ConstraintElement included, ConstraintElement excluded)
    {
        Included = included;
        Excluded = excluded;
    }

    /// <summary>Included set.</summary>
    public ConstraintElement Included { get; }

    /// <summary>Excluded set.</summary>
    public ConstraintElement Excluded { get; }

    /// <inheritdoc />
    public override string ToString() => $"{Included} EXCEPT {Excluded}";
}

/// <summary>A nested parenthesized constraint (which carries its own extensibility), or the constraints of a contained subtype.</summary>
public sealed class NestedElement : ConstraintElement
{
    /// <summary>Creates a nested element.</summary>
    public NestedElement(Asn1Constraint inner, bool isContainedSubtype = false)
    {
        Inner = inner;
        IsContainedSubtype = isContainedSubtype;
    }

    /// <summary>Nested constraint.</summary>
    public Asn1Constraint Inner { get; }

    /// <summary>True when the element stems from a contained subtype reference.</summary>
    public bool IsContainedSubtype { get; }

    /// <inheritdoc />
    public override string ToString() => Inner.ToString();
}

/// <summary>
/// A constraint that is preserved as metadata but not evaluated (user-defined constraints, table constraints,
/// contents constraints, property settings). Evaluation treats it as permitting all values.
/// </summary>
public sealed class OpaqueElement : ConstraintElement
{
    /// <summary>Creates an opaque element.</summary>
    public OpaqueElement(string kind, string notation)
    {
        Kind = kind;
        Notation = notation;
    }

    /// <summary>Kind, e.g. "user-defined", "table", "contents", "settings".</summary>
    public string Kind { get; }

    /// <summary>Source notation for display.</summary>
    public string Notation { get; }

    /// <inheritdoc />
    public override string ToString() => Notation;
}
