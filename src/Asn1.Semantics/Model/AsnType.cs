using System.Collections.Immutable;
using System.Numerics;
using Asn1.Constraints;
using Asn1.Diagnostics;
using Asn1.Syntax;

namespace Asn1.Semantics;

/// <summary>Kinds of resolved ASN.1 types.</summary>
public enum TypeKind
{
    /// <summary>Unresolvable type (after an error).</summary>
    Error,

    /// <summary>BOOLEAN</summary>
    Boolean,

    /// <summary>INTEGER</summary>
    Integer,

    /// <summary>ENUMERATED</summary>
    Enumerated,

    /// <summary>REAL</summary>
    Real,

    /// <summary>BIT STRING</summary>
    BitString,

    /// <summary>OCTET STRING</summary>
    OctetString,

    /// <summary>NULL</summary>
    Null,

    /// <summary>OBJECT IDENTIFIER</summary>
    ObjectIdentifier,

    /// <summary>RELATIVE-OID</summary>
    RelativeOid,

    /// <summary>OID-IRI</summary>
    OidIri,

    /// <summary>RELATIVE-OID-IRI</summary>
    RelativeOidIri,

    /// <summary>A restricted character string type (see <see cref="AsnType.StringKind"/>).</summary>
    CharacterString,

    /// <summary>UTCTime</summary>
    UtcTime,

    /// <summary>GeneralizedTime</summary>
    GeneralizedTime,

    /// <summary>DATE</summary>
    Date,

    /// <summary>TIME-OF-DAY</summary>
    TimeOfDay,

    /// <summary>DATE-TIME</summary>
    DateTime,

    /// <summary>DURATION</summary>
    Duration,

    /// <summary>TIME</summary>
    Time,

    /// <summary>SEQUENCE</summary>
    Sequence,

    /// <summary>SET</summary>
    Set,

    /// <summary>CHOICE</summary>
    Choice,

    /// <summary>SEQUENCE OF</summary>
    SequenceOf,

    /// <summary>SET OF</summary>
    SetOf,

    /// <summary>Open type (CLASS.&amp;Type, ANY).</summary>
    OpenType,

    /// <summary>EXTERNAL</summary>
    External,

    /// <summary>EMBEDDED PDV</summary>
    EmbeddedPdv,

    /// <summary>CHARACTER STRING (unrestricted)</summary>
    UnrestrictedCharacterString,
}

/// <summary>One tag in an effective tag list.</summary>
/// <param name="Tag">The tag.</param>
/// <param name="IsExplicit">True when this tag wraps the remaining encoding (explicit tagging); the innermost tag is not explicit.</param>
public readonly record struct TagInfo(Asn1Tag Tag, bool IsExplicit)
{
    /// <inheritdoc />
    public override string ToString() => IsExplicit ? Tag + " EXPLICIT" : Tag.ToString();
}

/// <summary>Presence of a SEQUENCE/SET component.</summary>
public enum Presence
{
    /// <summary>Mandatory.</summary>
    Required,

    /// <summary>OPTIONAL</summary>
    Optional,

    /// <summary>DEFAULT</summary>
    Default,
}

/// <summary>A component of a SEQUENCE or SET, or an alternative of a CHOICE.</summary>
public sealed class AsnComponent
{
    internal AsnComponent(string name, AsnType type, Presence presence, SourceLocation location, string? documentation)
    {
        Name = name;
        Type = type;
        Presence = presence;
        Location = location;
        Documentation = documentation;
    }

    /// <summary>The identifier.</summary>
    public string Name { get; }

    /// <summary>The component type (with effective tags, including automatic tags).</summary>
    public AsnType Type { get; internal set; }

    /// <summary>Presence.</summary>
    public Presence Presence { get; }

    /// <summary>True for OPTIONAL or DEFAULT.</summary>
    public bool IsOptional => Presence != Presence.Required;

    /// <summary>The resolved DEFAULT value.</summary>
    public AsnValue? DefaultValue { get; internal set; }

    /// <summary>True if this component is an extension addition.</summary>
    public bool IsExtensionAddition => ExtensionAdditionIndex >= 0;

    /// <summary>Index of the extension addition (one per single addition or per [[ ]] group); -1 for root components.</summary>
    public int ExtensionAdditionIndex { get; internal set; } = -1;

    /// <summary>True if the component is a member of an extension addition group <c>[[ ]]</c>.</summary>
    public bool IsInExtensionGroup { get; internal set; }

    /// <summary>The version number of the addition group, if given.</summary>
    public BigInteger? GroupVersion { get; internal set; }

    /// <summary>True if the component follows the second extension marker (second part of the root).</summary>
    public bool IsInRootPart2 { get; internal set; }

    /// <summary>True if the component was copied by COMPONENTS OF.</summary>
    public bool FromComponentsOf { get; internal set; }

    /// <summary>Source location.</summary>
    public SourceLocation Location { get; }

    /// <summary>Comments attached to the component.</summary>
    public string? Documentation { get; }

    /// <inheritdoc />
    public override string ToString() => $"{Name} {Type}";
}

/// <summary>An ENUMERATED item.</summary>
/// <param name="Name">Identifier.</param>
/// <param name="Value">Assigned or explicit value.</param>
/// <param name="IsExtension">True for additional enumerations.</param>
/// <param name="Location">Source location.</param>
/// <param name="Documentation">Attached comments.</param>
public sealed record EnumItem(string Name, BigInteger Value, bool IsExtension, SourceLocation Location, string? Documentation);

/// <summary>A named number (INTEGER) or named bit (BIT STRING).</summary>
/// <param name="Name">Identifier.</param>
/// <param name="Value">Number or bit position.</param>
/// <param name="Location">Source location.</param>
/// <param name="Documentation">Attached comments.</param>
public sealed record NamedNumber(string Name, BigInteger Value, SourceLocation Location, string? Documentation);

/// <summary>Identifies an information object class field used as a type.</summary>
/// <param name="Class">The class.</param>
/// <param name="Field">The field.</param>
public sealed record ClassFieldReference(ObjectClass Class, ClassField Field);

/// <summary>A (simple or component relation) table constraint.</summary>
/// <param name="ObjectSet">The constraining object set.</param>
/// <param name="RelatedPath">For component relation constraints: the referenced component path (<c>@a.b</c>).</param>
/// <param name="Level">Number of leading dots in the at-notation.</param>
public sealed record TableConstraintInfo(ObjectSet ObjectSet, ImmutableArray<string> RelatedPath, int Level);

/// <summary>Information about an open type (a class type field or ANY), including its table constraint when present.</summary>
public sealed class OpenTypeInfo
{
    /// <summary>The information object class, if known.</summary>
    public ObjectClass? Class { get; init; }

    /// <summary>The field name (e.g. <c>&amp;Type</c>).</summary>
    public string? FieldName { get; init; }

    /// <summary>The object set constraining the type, if any.</summary>
    public ObjectSet? ObjectSet { get; init; }

    /// <summary>The referenced component path of a component relation constraint (<c>@id</c>).</summary>
    public ImmutableArray<string> RelatedComponentPath { get; init; } = [];

    /// <summary>The at-notation level (number of leading dots).</summary>
    public int RelatedComponentLevel { get; init; }

    /// <summary>For ANY DEFINED BY: the identifier of the defining component.</summary>
    public string? DefinedBy { get; init; }
}

/// <summary>
/// A resolved ASN.1 type. Each occurrence of a type in the source yields a node carrying its own effective
/// tags and constraints. A reference occurrence points to the referenced assignment and its resolved type
/// (<see cref="Target"/>); structural information (components, items, element type) is shared through
/// <see cref="Definition"/>, which makes recursive types finite.
/// </summary>
public sealed class AsnType
{
    private ImmutableArray<AsnComponent> _components = [];
    private ImmutableArray<EnumItem> _enumItems = [];
    private ImmutableArray<NamedNumber> _namedNumbers = [];

    internal AsnType(TypeKind kind, SourceLocation location)
    {
        Kind = kind;
        Location = location;
    }

    /// <summary>The kind of the (ultimately) referenced type.</summary>
    public TypeKind Kind { get; internal set; }

    /// <summary>Location of this occurrence.</summary>
    public SourceLocation Location { get; }

    /// <summary>The syntax this node was resolved from, if any.</summary>
    public TypeSyntax? Syntax { get; internal set; }

    /// <summary>The assignment referenced by this occurrence (for reference occurrences).</summary>
    public TypeAssignmentSymbol? Reference { get; internal set; }

    /// <summary>The resolved type of <see cref="Reference"/>, or the instantiated type of a parameterized reference.</summary>
    public AsnType? Target { get; internal set; }

    /// <summary>The assignment whose right-hand side this node is, if any.</summary>
    public TypeAssignmentSymbol? Assignment { get; internal set; }

    /// <summary>For instances of parameterized types: the parameterized assignment.</summary>
    public TypeAssignmentSymbol? InstanceOf { get; internal set; }

    /// <summary>The node that owns the structural information (itself for inline definitions).</summary>
    public AsnType Definition => Target?.Definition ?? this;

    /// <summary>True if this node is a reference occurrence.</summary>
    public bool IsReference => Target is not null;

    /// <summary>Effective tags, outermost first. Empty for untagged CHOICE and open types.</summary>
    public ImmutableArray<TagInfo> Tags { get; internal set; } = [];

    /// <summary>Effective serially-applied constraints (inherited plus own), outermost/last applied last.</summary>
    public ImmutableArray<Asn1Constraint> Constraints { get; internal set; } = [];

    /// <summary>Constraints written at this occurrence only.</summary>
    public ImmutableArray<Asn1Constraint> OwnConstraints { get; internal set; } = [];

    /// <summary>Character string kind for <see cref="TypeKind.CharacterString"/>.</summary>
    public CharacterStringKind StringKind => Target?.StringKind ?? _stringKind;

    internal CharacterStringKind _stringKind;

    /// <summary>Components (SEQUENCE/SET) or alternatives (CHOICE) in textual order.</summary>
    public ImmutableArray<AsnComponent> Components => Target is not null ? Target.Components : _components;

    /// <summary>True if the SEQUENCE/SET/CHOICE/ENUMERATED type has an extension marker.</summary>
    public bool IsExtensible => Target?.IsExtensible ?? _isExtensible;

    internal bool _isExtensible;

    /// <summary>ENUMERATED items (root then additions, textual order).</summary>
    public ImmutableArray<EnumItem> EnumItems => Target is not null ? Target.EnumItems : _enumItems;

    /// <summary>INTEGER named numbers or BIT STRING named bits.</summary>
    public ImmutableArray<NamedNumber> NamedNumbers => Target is not null ? Target.NamedNumbers : _namedNumbers;

    /// <summary>Element type of SEQUENCE OF / SET OF.</summary>
    public AsnType? ElementType => Target is not null ? Target.ElementType : _elementType;

    internal AsnType? _elementType;

    /// <summary>Element identifier for <c>SEQUENCE OF name Type</c>.</summary>
    public string? ElementName => Target is not null ? Target.ElementName : _elementName;

    internal string? _elementName;

    /// <summary>Open type details.</summary>
    public OpenTypeInfo? OpenType => Target?.OpenType ?? _openType;

    internal OpenTypeInfo? _openType;

    /// <summary>Comments attached to the defining assignment or component.</summary>
    public string? Documentation { get; internal set; }

    /// <summary>For types written as <c>CLASS.&amp;field</c>: the class and field.</summary>
    public ClassFieldReference? FieldOrigin { get; internal set; }

    /// <summary>The table constraint applied to this occurrence, if any.</summary>
    public TableConstraintInfo? TableConstraint { get; internal set; }

    /// <summary>For BIT STRING / OCTET STRING with a contents constraint: the contained type.</summary>
    public AsnType? ContainedType { get; internal set; }

    /// <summary>True when structural resolution of the definition has completed.</summary>
    internal bool IsComplete { get; set; }

    internal void SetComponents(ImmutableArray<AsnComponent> components) => _components = components;

    internal void SetEnumItems(ImmutableArray<EnumItem> items) => _enumItems = items;

    internal void SetNamedNumbers(ImmutableArray<NamedNumber> items) => _namedNumbers = items;

    /// <summary>True for SEQUENCE, SET and CHOICE.</summary>
    public bool IsStructured => Kind is TypeKind.Sequence or TypeKind.Set or TypeKind.Choice;

    /// <summary>True for SEQUENCE OF and SET OF.</summary>
    public bool IsCollection => Kind is TypeKind.SequenceOf or TypeKind.SetOf;

    /// <summary>True for character strings and the string-based time types.</summary>
    public bool IsStringLike => Kind is TypeKind.CharacterString or TypeKind.UtcTime or TypeKind.GeneralizedTime or TypeKind.Date or TypeKind.TimeOfDay or TypeKind.DateTime or TypeKind.Duration or TypeKind.Time or TypeKind.OidIri or TypeKind.RelativeOidIri;

    /// <summary>The outermost tag, or null for untagged CHOICE/open types.</summary>
    public Asn1Tag? OutermostTag => Tags.IsDefaultOrEmpty ? null : Tags[0].Tag;

    /// <summary>The root components (excluding extension additions) in textual order.</summary>
    public IEnumerable<AsnComponent> RootComponents => Components.Where(c => !c.IsExtensionAddition);

    /// <summary>The extension addition components in textual order.</summary>
    public IEnumerable<AsnComponent> ExtensionComponents => Components.Where(c => c.IsExtensionAddition);

    /// <summary>A display name: the assignment or reference name, or the builtin notation.</summary>
    public string DisplayName => Assignment?.Name ?? Reference?.Name ?? KindName(Kind, StringKind);

    /// <summary>Renders the ASN.1 keyword of a kind.</summary>
    public static string KindName(TypeKind kind, CharacterStringKind stringKind = default) => kind switch
    {
        TypeKind.Boolean => "BOOLEAN",
        TypeKind.Integer => "INTEGER",
        TypeKind.Enumerated => "ENUMERATED",
        TypeKind.Real => "REAL",
        TypeKind.BitString => "BIT STRING",
        TypeKind.OctetString => "OCTET STRING",
        TypeKind.Null => "NULL",
        TypeKind.ObjectIdentifier => "OBJECT IDENTIFIER",
        TypeKind.RelativeOid => "RELATIVE-OID",
        TypeKind.OidIri => "OID-IRI",
        TypeKind.RelativeOidIri => "RELATIVE-OID-IRI",
        TypeKind.CharacterString => CharacterRepertoire.Keyword(stringKind),
        TypeKind.UtcTime => "UTCTime",
        TypeKind.GeneralizedTime => "GeneralizedTime",
        TypeKind.Date => "DATE",
        TypeKind.TimeOfDay => "TIME-OF-DAY",
        TypeKind.DateTime => "DATE-TIME",
        TypeKind.Duration => "DURATION",
        TypeKind.Time => "TIME",
        TypeKind.Sequence => "SEQUENCE",
        TypeKind.Set => "SET",
        TypeKind.Choice => "CHOICE",
        TypeKind.SequenceOf => "SEQUENCE OF",
        TypeKind.SetOf => "SET OF",
        TypeKind.OpenType => "open type",
        TypeKind.External => "EXTERNAL",
        TypeKind.EmbeddedPdv => "EMBEDDED PDV",
        TypeKind.UnrestrictedCharacterString => "CHARACTER STRING",
        _ => "<error>",
    };

    /// <inheritdoc />
    public override string ToString()
    {
        var name = DisplayName;
        var tags = Tags.IsDefaultOrEmpty ? string.Empty : string.Join(' ', Tags) + " ";
        var constraints = string.Concat(OwnConstraints.Select(c => c.ToString()));
        return tags + name + constraints;
    }
}
