using System.Collections.Immutable;
using System.Numerics;
using Asn1.Diagnostics;

namespace Asn1.Syntax;

/// <summary>Base class of all syntax nodes. Every node records its source text and span.</summary>
public abstract class SyntaxNode
{
    /// <summary>Creates a node.</summary>
    protected SyntaxNode(SourceText source, TextSpan span)
    {
        Source = source;
        Span = span;
    }

    /// <summary>The source text containing the node.</summary>
    public SourceText Source { get; }

    /// <summary>The span of the node.</summary>
    public TextSpan Span { get; }

    /// <summary>The node location.</summary>
    public SourceLocation Location => new(Source, Span);
}

/// <summary>A name token with its location, used for definitions and references.</summary>
public sealed class NameSyntax : SyntaxNode
{
    /// <summary>Creates a name.</summary>
    public NameSyntax(SourceText source, TextSpan span, string text) : base(source, span) => Text = text;

    /// <summary>The name text.</summary>
    public string Text { get; }

    /// <inheritdoc />
    public override string ToString() => Text;
}

/// <summary>A parsed file containing one or more modules.</summary>
public sealed class CompilationUnitSyntax : SyntaxNode
{
    /// <summary>Creates a compilation unit.</summary>
    public CompilationUnitSyntax(SourceText source, TextSpan span, ImmutableArray<ModuleDefinitionSyntax> modules, LexResult lex) : base(source, span)
    {
        Modules = modules;
        Lex = lex;
    }

    /// <summary>The modules in source order.</summary>
    public ImmutableArray<ModuleDefinitionSyntax> Modules { get; }

    /// <summary>The token stream and comments of the file.</summary>
    public LexResult Lex { get; }
}

/// <summary>The tag default of a module (X.680 13.1).</summary>
public enum TagDefault
{
    /// <summary>No tag default given; equivalent to EXPLICIT.</summary>
    Unspecified,

    /// <summary>EXPLICIT TAGS.</summary>
    Explicit,

    /// <summary>IMPLICIT TAGS.</summary>
    Implicit,

    /// <summary>AUTOMATIC TAGS.</summary>
    Automatic,
}

/// <summary>One component of an object identifier value: a name, a number, or a name with a number.</summary>
public sealed class OidComponentSyntax : SyntaxNode
{
    /// <summary>Creates a component.</summary>
    public OidComponentSyntax(SourceText source, TextSpan span, NameSyntax? name, ValueSyntax? number) : base(source, span)
    {
        Name = name;
        Number = number;
    }

    /// <summary>The name form, if present.</summary>
    public NameSyntax? Name { get; }

    /// <summary>The number form (a literal or defined value), if present.</summary>
    public ValueSyntax? Number { get; }
}

/// <summary>A module definition.</summary>
public sealed class ModuleDefinitionSyntax : SyntaxNode
{
    /// <summary>Creates a module definition.</summary>
    public ModuleDefinitionSyntax(
        SourceText source,
        TextSpan span,
        NameSyntax name,
        ImmutableArray<OidComponentSyntax>? definitiveOid,
        string? iri,
        string? encodingReferenceDefault,
        TagDefault tagDefault,
        bool extensibilityImplied,
        ExportsSyntax? exports,
        ImmutableArray<ImportGroupSyntax> imports,
        ImmutableArray<AssignmentSyntax> assignments,
        string? documentation)
        : base(source, span)
    {
        Name = name;
        DefinitiveOid = definitiveOid;
        Iri = iri;
        EncodingReferenceDefault = encodingReferenceDefault;
        TagDefault = tagDefault;
        ExtensibilityImplied = extensibilityImplied;
        Exports = exports;
        Imports = imports;
        Assignments = assignments;
        Documentation = documentation;
    }

    /// <summary>Module reference.</summary>
    public NameSyntax Name { get; }

    /// <summary>Definitive object identifier, if given.</summary>
    public ImmutableArray<OidComponentSyntax>? DefinitiveOid { get; }

    /// <summary>Definitive IRI, if given.</summary>
    public string? Iri { get; }

    /// <summary>Encoding reference default (e.g. <c>XER INSTRUCTIONS</c>).</summary>
    public string? EncodingReferenceDefault { get; }

    /// <summary>Tag default.</summary>
    public TagDefault TagDefault { get; }

    /// <summary>True for <c>EXTENSIBILITY IMPLIED</c>.</summary>
    public bool ExtensibilityImplied { get; }

    /// <summary>The exports clause; null when absent (which exports everything).</summary>
    public ExportsSyntax? Exports { get; }

    /// <summary>Import groups.</summary>
    public ImmutableArray<ImportGroupSyntax> Imports { get; }

    /// <summary>Assignments in source order.</summary>
    public ImmutableArray<AssignmentSyntax> Assignments { get; }

    /// <summary>Comments directly preceding the module header.</summary>
    public string? Documentation { get; }
}

/// <summary>An EXPORTS clause.</summary>
public sealed class ExportsSyntax : SyntaxNode
{
    /// <summary>Creates an exports clause.</summary>
    public ExportsSyntax(SourceText source, TextSpan span, bool all, ImmutableArray<SymbolSyntax> symbols) : base(source, span)
    {
        All = all;
        Symbols = symbols;
    }

    /// <summary>True for <c>EXPORTS ALL</c>.</summary>
    public bool All { get; }

    /// <summary>Exported symbols (empty for <c>EXPORTS ;</c>).</summary>
    public ImmutableArray<SymbolSyntax> Symbols { get; }
}

/// <summary>A symbol in an import or export list.</summary>
public sealed class SymbolSyntax : SyntaxNode
{
    /// <summary>Creates a symbol.</summary>
    public SymbolSyntax(SourceText source, TextSpan span, NameSyntax name, bool isParameterized) : base(source, span)
    {
        Name = name;
        IsParameterized = isParameterized;
    }

    /// <summary>The symbol name.</summary>
    public NameSyntax Name { get; }

    /// <summary>True when written as <c>Name{}</c>.</summary>
    public bool IsParameterized { get; }
}

/// <summary>A <c>SymbolList FROM GlobalModuleReference</c> group.</summary>
public sealed class ImportGroupSyntax : SyntaxNode
{
    /// <summary>Creates an import group.</summary>
    public ImportGroupSyntax(SourceText source, TextSpan span, ImmutableArray<SymbolSyntax> symbols, NameSyntax moduleName, ImmutableArray<OidComponentSyntax>? moduleOid, ValueSyntax? assignedValue) : base(source, span)
    {
        Symbols = symbols;
        ModuleName = moduleName;
        ModuleOid = moduleOid;
        AssignedValue = assignedValue;
    }

    /// <summary>Imported symbols.</summary>
    public ImmutableArray<SymbolSyntax> Symbols { get; }

    /// <summary>Source module reference.</summary>
    public NameSyntax ModuleName { get; }

    /// <summary>Object identifier of the source module, if given.</summary>
    public ImmutableArray<OidComponentSyntax>? ModuleOid { get; }

    /// <summary>Defined value identifying the module, if given.</summary>
    public ValueSyntax? AssignedValue { get; }
}

/// <summary>A formal parameter of a parameterized assignment (X.683 8.3).</summary>
public sealed class ParameterSyntax : SyntaxNode
{
    /// <summary>Creates a parameter.</summary>
    public ParameterSyntax(SourceText source, TextSpan span, TypeSyntax? governor, NameSyntax name) : base(source, span)
    {
        Governor = governor;
        Name = name;
    }

    /// <summary>Governor (type or class), if present.</summary>
    public TypeSyntax? Governor { get; }

    /// <summary>Dummy reference.</summary>
    public NameSyntax Name { get; }
}

/// <summary>Base class of assignments.</summary>
public abstract class AssignmentSyntax : SyntaxNode
{
    /// <summary>Creates an assignment.</summary>
    protected AssignmentSyntax(SourceText source, TextSpan span, NameSyntax name, ImmutableArray<ParameterSyntax> parameters, string? documentation) : base(source, span)
    {
        Name = name;
        Parameters = parameters.IsDefault ? [] : parameters;
        Documentation = documentation;
    }

    /// <summary>The assigned reference name.</summary>
    public NameSyntax Name { get; }

    /// <summary>Formal parameters; empty if not parameterized.</summary>
    public ImmutableArray<ParameterSyntax> Parameters { get; }

    /// <summary>True for parameterized assignments.</summary>
    public bool IsParameterized => Parameters.Length > 0;

    /// <summary>Comments attached to the assignment.</summary>
    public string? Documentation { get; }
}

/// <summary><c>Type ::= Type</c></summary>
public sealed class TypeAssignmentSyntax : AssignmentSyntax
{
    /// <summary>Creates a type assignment.</summary>
    public TypeAssignmentSyntax(SourceText source, TextSpan span, NameSyntax name, ImmutableArray<ParameterSyntax> parameters, TypeSyntax type, string? documentation) : base(source, span, name, parameters, documentation) => Type = type;

    /// <summary>The assigned type.</summary>
    public TypeSyntax Type { get; }
}

/// <summary><c>value Type ::= Value</c>, or an information object assignment when the governor is a class.</summary>
public sealed class ValueAssignmentSyntax : AssignmentSyntax
{
    /// <summary>Creates a value assignment.</summary>
    public ValueAssignmentSyntax(SourceText source, TextSpan span, NameSyntax name, ImmutableArray<ParameterSyntax> parameters, TypeSyntax type, ValueSyntax value, string? documentation) : base(source, span, name, parameters, documentation)
    {
        Type = type;
        Value = value;
    }

    /// <summary>The governing type (or class).</summary>
    public TypeSyntax Type { get; }

    /// <summary>The value (or object).</summary>
    public ValueSyntax Value { get; }
}

/// <summary><c>Name Type ::= { ... }</c>: a value set assignment, or an object set assignment when the governor is a class.</summary>
public sealed class SetAssignmentSyntax : AssignmentSyntax
{
    /// <summary>Creates a value set or object set assignment.</summary>
    public SetAssignmentSyntax(SourceText source, TextSpan span, NameSyntax name, ImmutableArray<ParameterSyntax> parameters, TypeSyntax governor, ElementSetSpecsSyntax elements, string? documentation) : base(source, span, name, parameters, documentation)
    {
        Governor = governor;
        Elements = elements;
    }

    /// <summary>The governing type or class.</summary>
    public TypeSyntax Governor { get; }

    /// <summary>The element set.</summary>
    public ElementSetSpecsSyntax Elements { get; }
}

/// <summary><c>CLASSNAME ::= CLASS { ... } [WITH SYNTAX { ... }]</c></summary>
public sealed class ClassAssignmentSyntax : AssignmentSyntax
{
    /// <summary>Creates a class assignment.</summary>
    public ClassAssignmentSyntax(SourceText source, TextSpan span, NameSyntax name, ImmutableArray<ParameterSyntax> parameters, ObjectClassSyntax @class, string? documentation) : base(source, span, name, parameters, documentation) => Class = @class;

    /// <summary>The class definition or reference.</summary>
    public ObjectClassSyntax Class { get; }
}

/// <summary>A withdrawn X.208 MACRO definition that was skipped.</summary>
public sealed class MacroAssignmentSyntax : AssignmentSyntax
{
    /// <summary>Creates a macro placeholder.</summary>
    public MacroAssignmentSyntax(SourceText source, TextSpan span, NameSyntax name) : base(source, span, name, [], null)
    {
    }
}

// ------------------------------------------------------------------ Types

/// <summary>Base class of type notation.</summary>
public abstract class TypeSyntax : SyntaxNode
{
    /// <summary>Creates a type node.</summary>
    protected TypeSyntax(SourceText source, TextSpan span) : base(source, span)
    {
    }
}

/// <summary>Kinds of built-in types without further syntax.</summary>
public enum BuiltinTypeKind
{
    /// <summary>BOOLEAN</summary>
    Boolean,

    /// <summary>NULL</summary>
    Null,

    /// <summary>REAL</summary>
    Real,

    /// <summary>OCTET STRING</summary>
    OctetString,

    /// <summary>OBJECT IDENTIFIER</summary>
    ObjectIdentifier,

    /// <summary>RELATIVE-OID</summary>
    RelativeOid,

    /// <summary>OID-IRI</summary>
    OidIri,

    /// <summary>RELATIVE-OID-IRI</summary>
    RelativeOidIri,

    /// <summary>EXTERNAL</summary>
    External,

    /// <summary>EMBEDDED PDV</summary>
    EmbeddedPdv,

    /// <summary>CHARACTER STRING (unrestricted)</summary>
    CharacterString,

    /// <summary>UTF8String</summary>
    Utf8String,

    /// <summary>NumericString</summary>
    NumericString,

    /// <summary>PrintableString</summary>
    PrintableString,

    /// <summary>TeletexString / T61String</summary>
    TeletexString,

    /// <summary>VideotexString</summary>
    VideotexString,

    /// <summary>IA5String</summary>
    IA5String,

    /// <summary>GraphicString</summary>
    GraphicString,

    /// <summary>VisibleString / ISO646String</summary>
    VisibleString,

    /// <summary>GeneralString</summary>
    GeneralString,

    /// <summary>UniversalString</summary>
    UniversalString,

    /// <summary>BMPString</summary>
    BmpString,

    /// <summary>UTCTime</summary>
    UtcTime,

    /// <summary>GeneralizedTime</summary>
    GeneralizedTime,

    /// <summary>ObjectDescriptor</summary>
    ObjectDescriptor,

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
}

/// <summary>A built-in type keyword such as BOOLEAN or IA5String.</summary>
public sealed class BuiltinTypeSyntax : TypeSyntax
{
    /// <summary>Creates a builtin type.</summary>
    public BuiltinTypeSyntax(SourceText source, TextSpan span, BuiltinTypeKind kind) : base(source, span) => Kind = kind;

    /// <summary>The builtin kind.</summary>
    public BuiltinTypeKind Kind { get; }
}

/// <summary>A named number (<c>name(value)</c>) used by INTEGER, ENUMERATED and BIT STRING.</summary>
public sealed class NamedNumberSyntax : SyntaxNode
{
    /// <summary>Creates a named number.</summary>
    public NamedNumberSyntax(SourceText source, TextSpan span, NameSyntax name, ValueSyntax? value, string? documentation) : base(source, span)
    {
        Name = name;
        Value = value;
        Documentation = documentation;
    }

    /// <summary>The identifier.</summary>
    public NameSyntax Name { get; }

    /// <summary>The value; null for enumeration items without explicit numbers.</summary>
    public ValueSyntax? Value { get; }

    /// <summary>Attached comments.</summary>
    public string? Documentation { get; }
}

/// <summary>INTEGER with optional named numbers.</summary>
public sealed class IntegerTypeSyntax : TypeSyntax
{
    /// <summary>Creates an INTEGER type.</summary>
    public IntegerTypeSyntax(SourceText source, TextSpan span, ImmutableArray<NamedNumberSyntax> namedNumbers) : base(source, span) => NamedNumbers = namedNumbers;

    /// <summary>Named numbers.</summary>
    public ImmutableArray<NamedNumberSyntax> NamedNumbers { get; }
}

/// <summary>BIT STRING with optional named bits.</summary>
public sealed class BitStringTypeSyntax : TypeSyntax
{
    /// <summary>Creates a BIT STRING type.</summary>
    public BitStringTypeSyntax(SourceText source, TextSpan span, ImmutableArray<NamedNumberSyntax> namedBits) : base(source, span) => NamedBits = namedBits;

    /// <summary>Named bits.</summary>
    public ImmutableArray<NamedNumberSyntax> NamedBits { get; }
}

/// <summary>An exception specification (<c>! ...</c>).</summary>
public sealed class ExceptionSpecSyntax : SyntaxNode
{
    /// <summary>Creates an exception spec.</summary>
    public ExceptionSpecSyntax(SourceText source, TextSpan span, TypeSyntax? type, ValueSyntax value) : base(source, span)
    {
        Type = type;
        Value = value;
    }

    /// <summary>Exception type for the <c>Type : Value</c> form.</summary>
    public TypeSyntax? Type { get; }

    /// <summary>The exception identifier value.</summary>
    public ValueSyntax Value { get; }
}

/// <summary>ENUMERATED.</summary>
public sealed class EnumeratedTypeSyntax : TypeSyntax
{
    /// <summary>Creates an ENUMERATED type.</summary>
    public EnumeratedTypeSyntax(SourceText source, TextSpan span, ImmutableArray<NamedNumberSyntax> rootItems, bool isExtensible, ExceptionSpecSyntax? exception, ImmutableArray<NamedNumberSyntax> additionItems) : base(source, span)
    {
        RootItems = rootItems;
        IsExtensible = isExtensible;
        Exception = exception;
        AdditionItems = additionItems;
    }

    /// <summary>Root enumeration.</summary>
    public ImmutableArray<NamedNumberSyntax> RootItems { get; }

    /// <summary>True when an extension marker is present.</summary>
    public bool IsExtensible { get; }

    /// <summary>Exception spec after the extension marker.</summary>
    public ExceptionSpecSyntax? Exception { get; }

    /// <summary>Additional enumeration.</summary>
    public ImmutableArray<NamedNumberSyntax> AdditionItems { get; }
}

/// <summary>Presence attribute of a SEQUENCE/SET component.</summary>
public enum ComponentPresence
{
    /// <summary>Mandatory.</summary>
    Required,

    /// <summary>OPTIONAL.</summary>
    Optional,

    /// <summary>DEFAULT value.</summary>
    Default,
}

/// <summary>Base class of items in a component or alternative list.</summary>
public abstract class ComponentListItemSyntax : SyntaxNode
{
    /// <summary>Creates an item.</summary>
    protected ComponentListItemSyntax(SourceText source, TextSpan span) : base(source, span)
    {
    }
}

/// <summary>A named component or CHOICE alternative.</summary>
public sealed class NamedComponentSyntax : ComponentListItemSyntax
{
    /// <summary>Creates a named component.</summary>
    public NamedComponentSyntax(SourceText source, TextSpan span, NameSyntax name, TypeSyntax type, ComponentPresence presence, ValueSyntax? defaultValue, string? documentation) : base(source, span)
    {
        Name = name;
        Type = type;
        Presence = presence;
        DefaultValue = defaultValue;
        Documentation = documentation;
    }

    /// <summary>The identifier.</summary>
    public NameSyntax Name { get; }

    /// <summary>The component type.</summary>
    public TypeSyntax Type { get; }

    /// <summary>Presence.</summary>
    public ComponentPresence Presence { get; }

    /// <summary>DEFAULT value.</summary>
    public ValueSyntax? DefaultValue { get; }

    /// <summary>Attached comments.</summary>
    public string? Documentation { get; }
}

/// <summary><c>COMPONENTS OF Type</c>.</summary>
public sealed class ComponentsOfSyntax : ComponentListItemSyntax
{
    /// <summary>Creates a COMPONENTS OF item.</summary>
    public ComponentsOfSyntax(SourceText source, TextSpan span, TypeSyntax type) : base(source, span) => Type = type;

    /// <summary>The referenced type.</summary>
    public TypeSyntax Type { get; }
}

/// <summary>An extension marker <c>...</c> with an optional exception spec.</summary>
public sealed class ExtensionMarkerSyntax : ComponentListItemSyntax
{
    /// <summary>Creates a marker.</summary>
    public ExtensionMarkerSyntax(SourceText source, TextSpan span, ExceptionSpecSyntax? exception) : base(source, span) => Exception = exception;

    /// <summary>Exception spec.</summary>
    public ExceptionSpecSyntax? Exception { get; }
}

/// <summary>An extension addition group <c>[[ n: ... ]]</c>.</summary>
public sealed class ExtensionGroupSyntax : ComponentListItemSyntax
{
    /// <summary>Creates a group.</summary>
    public ExtensionGroupSyntax(SourceText source, TextSpan span, ValueSyntax? version, ImmutableArray<ComponentListItemSyntax> items) : base(source, span)
    {
        Version = version;
        Items = items;
    }

    /// <summary>Version number.</summary>
    public ValueSyntax? Version { get; }

    /// <summary>Components of the group.</summary>
    public ImmutableArray<ComponentListItemSyntax> Items { get; }
}

/// <summary>Whether a structured type is SEQUENCE, SET or CHOICE.</summary>
public enum StructuredKind
{
    /// <summary>SEQUENCE</summary>
    Sequence,

    /// <summary>SET</summary>
    Set,

    /// <summary>CHOICE</summary>
    Choice,
}

/// <summary>SEQUENCE, SET or CHOICE with a component list. Items are kept in source order including markers.</summary>
public sealed class StructuredTypeSyntax : TypeSyntax
{
    /// <summary>Creates a structured type.</summary>
    public StructuredTypeSyntax(SourceText source, TextSpan span, StructuredKind kind, ImmutableArray<ComponentListItemSyntax> items) : base(source, span)
    {
        Kind = kind;
        Items = items;
    }

    /// <summary>Kind.</summary>
    public StructuredKind Kind { get; }

    /// <summary>Items in source order, including extension markers and groups.</summary>
    public ImmutableArray<ComponentListItemSyntax> Items { get; }
}

/// <summary>SEQUENCE OF / SET OF.</summary>
public sealed class CollectionTypeSyntax : TypeSyntax
{
    /// <summary>Creates a collection type.</summary>
    public CollectionTypeSyntax(SourceText source, TextSpan span, bool isSet, ConstraintSyntax? constraint, NameSyntax? elementName, TypeSyntax elementType) : base(source, span)
    {
        IsSet = isSet;
        Constraint = constraint;
        ElementName = elementName;
        ElementType = elementType;
    }

    /// <summary>True for SET OF.</summary>
    public bool IsSet { get; }

    /// <summary>Constraint between the keyword and OF (<c>SEQUENCE SIZE(1..4) OF</c>), if any.</summary>
    public ConstraintSyntax? Constraint { get; }

    /// <summary>Element identifier for <c>SEQUENCE OF name Type</c>.</summary>
    public NameSyntax? ElementName { get; }

    /// <summary>Element type.</summary>
    public TypeSyntax ElementType { get; }
}

/// <summary>Tag class.</summary>
public enum TagClassSyntax
{
    /// <summary>Context-specific (no class keyword).</summary>
    ContextSpecific,

    /// <summary>UNIVERSAL</summary>
    Universal,

    /// <summary>APPLICATION</summary>
    Application,

    /// <summary>PRIVATE</summary>
    Private,
}

/// <summary>Tagging mode keyword.</summary>
public enum TaggingMode
{
    /// <summary>No keyword; the module tag default applies.</summary>
    Default,

    /// <summary>IMPLICIT</summary>
    Implicit,

    /// <summary>EXPLICIT</summary>
    Explicit,
}

/// <summary><c>[class number] IMPLICIT|EXPLICIT Type</c>.</summary>
public sealed class TaggedTypeSyntax : TypeSyntax
{
    /// <summary>Creates a tagged type.</summary>
    public TaggedTypeSyntax(SourceText source, TextSpan span, TagClassSyntax tagClass, ValueSyntax number, TaggingMode mode, TypeSyntax type) : base(source, span)
    {
        TagClass = tagClass;
        Number = number;
        Mode = mode;
        Type = type;
    }

    /// <summary>Tag class.</summary>
    public TagClassSyntax TagClass { get; }

    /// <summary>Tag number (literal or defined value).</summary>
    public ValueSyntax Number { get; }

    /// <summary>Tagging mode.</summary>
    public TaggingMode Mode { get; }

    /// <summary>The underlying type.</summary>
    public TypeSyntax Type { get; }
}

/// <summary>A type with one or more serially applied constraints.</summary>
public sealed class ConstrainedTypeSyntax : TypeSyntax
{
    /// <summary>Creates a constrained type.</summary>
    public ConstrainedTypeSyntax(SourceText source, TextSpan span, TypeSyntax type, ImmutableArray<ConstraintSyntax> constraints) : base(source, span)
    {
        Type = type;
        Constraints = constraints;
    }

    /// <summary>The parent type.</summary>
    public TypeSyntax Type { get; }

    /// <summary>Constraints in application order.</summary>
    public ImmutableArray<ConstraintSyntax> Constraints { get; }
}

/// <summary>Actual parameter list of a parameterized reference; each parameter is kept as a token range for kind-directed reparsing.</summary>
public sealed class ActualParameterSyntax : SyntaxNode
{
    /// <summary>Creates an actual parameter.</summary>
    public ActualParameterSyntax(SourceText source, TextSpan span, int firstToken, int endToken, TypeSyntax? asType, ValueSyntax? asValue) : base(source, span)
    {
        FirstToken = firstToken;
        EndToken = endToken;
        AsType = asType;
        AsValue = asValue;
    }

    /// <summary>Index of the first token in the file's token list.</summary>
    public int FirstToken { get; }

    /// <summary>Exclusive end token index.</summary>
    public int EndToken { get; }

    /// <summary>The parameter parsed as a type, if that parse succeeded.</summary>
    public TypeSyntax? AsType { get; }

    /// <summary>The parameter parsed as a value, if that parse succeeded.</summary>
    public ValueSyntax? AsValue { get; }
}

/// <summary>A reference to a type (or class, value set, object set): <c>Module.Name{params}</c>.</summary>
public sealed class TypeReferenceSyntax : TypeSyntax
{
    /// <summary>Creates a type reference.</summary>
    public TypeReferenceSyntax(SourceText source, TextSpan span, NameSyntax? module, NameSyntax name, ImmutableArray<ActualParameterSyntax>? actualParameters) : base(source, span)
    {
        Module = module;
        Name = name;
        ActualParameters = actualParameters;
    }

    /// <summary>Module qualifier.</summary>
    public NameSyntax? Module { get; }

    /// <summary>Referenced name.</summary>
    public NameSyntax Name { get; }

    /// <summary>Actual parameters, if a parameterized reference.</summary>
    public ImmutableArray<ActualParameterSyntax>? ActualParameters { get; }

    /// <inheritdoc />
    public override string ToString() => Module is null ? Name.Text : $"{Module.Text}.{Name.Text}";
}

/// <summary>A field of an information object class used as a type: <c>CLASS.&amp;field</c>, or information from objects <c>object.&amp;Type</c>.</summary>
public sealed class FieldTypeSyntax : TypeSyntax
{
    /// <summary>Creates a field type.</summary>
    public FieldTypeSyntax(SourceText source, TextSpan span, TypeReferenceSyntax? classReference, NameSyntax? objectReference, ImmutableArray<NameSyntax> fieldPath) : base(source, span)
    {
        ClassReference = classReference;
        ObjectReference = objectReference;
        FieldPath = fieldPath;
    }

    /// <summary>The class (or object set) reference, for <c>CLASS.&amp;field</c>.</summary>
    public TypeReferenceSyntax? ClassReference { get; }

    /// <summary>The object reference, for <c>object.&amp;field</c>.</summary>
    public NameSyntax? ObjectReference { get; }

    /// <summary>Field names including the ampersand.</summary>
    public ImmutableArray<NameSyntax> FieldPath { get; }
}

/// <summary>Selection type <c>alternative &lt; ChoiceType</c>.</summary>
public sealed class SelectionTypeSyntax : TypeSyntax
{
    /// <summary>Creates a selection type.</summary>
    public SelectionTypeSyntax(SourceText source, TextSpan span, NameSyntax alternative, TypeSyntax choiceType) : base(source, span)
    {
        Alternative = alternative;
        ChoiceType = choiceType;
    }

    /// <summary>Selected alternative.</summary>
    public NameSyntax Alternative { get; }

    /// <summary>The CHOICE type.</summary>
    public TypeSyntax ChoiceType { get; }
}

/// <summary><c>INSTANCE OF Class</c>.</summary>
public sealed class InstanceOfTypeSyntax : TypeSyntax
{
    /// <summary>Creates an INSTANCE OF type.</summary>
    public InstanceOfTypeSyntax(SourceText source, TextSpan span, TypeReferenceSyntax classReference) : base(source, span) => ClassReference = classReference;

    /// <summary>The class.</summary>
    public TypeReferenceSyntax ClassReference { get; }
}

/// <summary>Withdrawn X.208 <c>ANY [DEFINED BY id]</c>.</summary>
public sealed class AnyTypeSyntax : TypeSyntax
{
    /// <summary>Creates an ANY type.</summary>
    public AnyTypeSyntax(SourceText source, TextSpan span, NameSyntax? definedBy) : base(source, span) => DefinedBy = definedBy;

    /// <summary>The component named in DEFINED BY.</summary>
    public NameSyntax? DefinedBy { get; }
}

/// <summary>Placeholder produced after a syntax error so later phases can continue.</summary>
public sealed class ErrorTypeSyntax : TypeSyntax
{
    /// <summary>Creates an error type.</summary>
    public ErrorTypeSyntax(SourceText source, TextSpan span) : base(source, span)
    {
    }
}

// ------------------------------------------------------------------ Information object classes

/// <summary>Base of object class notation.</summary>
public abstract class ObjectClassSyntax : SyntaxNode
{
    /// <summary>Creates a class node.</summary>
    protected ObjectClassSyntax(SourceText source, TextSpan span) : base(source, span)
    {
    }
}

/// <summary>The kind of a field spec as determined syntactically.</summary>
public enum FieldSpecKind
{
    /// <summary><c>&amp;Type</c> with no governor: a type field.</summary>
    Type,

    /// <summary><c>&amp;value Type</c>: fixed-type value field, or object field when the governor is a class.</summary>
    ValueOrObject,

    /// <summary><c>&amp;value &amp;Type</c>: variable-type value field.</summary>
    VariableTypeValue,

    /// <summary><c>&amp;Values Type</c>: fixed-type value set field, or object set field when the governor is a class.</summary>
    ValueSetOrObjectSet,

    /// <summary><c>&amp;Values &amp;Type</c>: variable-type value set field.</summary>
    VariableTypeValueSet,
}

/// <summary>A field specification inside CLASS { }.</summary>
public sealed class FieldSpecSyntax : SyntaxNode
{
    /// <summary>Creates a field spec.</summary>
    public FieldSpecSyntax(SourceText source, TextSpan span, NameSyntax name, FieldSpecKind kind, TypeSyntax? governor, ImmutableArray<NameSyntax> governorFieldPath, bool isUnique, bool isOptional, SyntaxNode? defaultSetting) : base(source, span)
    {
        Name = name;
        Kind = kind;
        Governor = governor;
        GovernorFieldPath = governorFieldPath;
        IsUnique = isUnique;
        IsOptional = isOptional;
        DefaultSetting = defaultSetting;
    }

    /// <summary>Field name including the ampersand.</summary>
    public NameSyntax Name { get; }

    /// <summary>Syntactic kind.</summary>
    public FieldSpecKind Kind { get; }

    /// <summary>Governor type or class.</summary>
    public TypeSyntax? Governor { get; }

    /// <summary>Field path for variable-type fields.</summary>
    public ImmutableArray<NameSyntax> GovernorFieldPath { get; }

    /// <summary>UNIQUE.</summary>
    public bool IsUnique { get; }

    /// <summary>OPTIONAL.</summary>
    public bool IsOptional { get; }

    /// <summary>DEFAULT setting (a TypeSyntax, ValueSyntax or ElementSetSpecsSyntax), if present.</summary>
    public SyntaxNode? DefaultSetting { get; }
}

/// <summary>An element of WITH SYNTAX: a literal word, a comma, a field reference, or an optional group.</summary>
public sealed class SyntaxTokenSpecSyntax : SyntaxNode
{
    /// <summary>Creates a syntax spec element.</summary>
    public SyntaxTokenSpecSyntax(SourceText source, TextSpan span, string? literal, NameSyntax? field, ImmutableArray<SyntaxTokenSpecSyntax> optionalGroup) : base(source, span)
    {
        Literal = literal;
        Field = field;
        OptionalGroup = optionalGroup;
    }

    /// <summary>A literal word or ",".</summary>
    public string? Literal { get; }

    /// <summary>A field reference.</summary>
    public NameSyntax? Field { get; }

    /// <summary>Contents of an optional group <c>[ ... ]</c>; default when not a group.</summary>
    public ImmutableArray<SyntaxTokenSpecSyntax> OptionalGroup { get; }

    /// <summary>True for an optional group.</summary>
    public bool IsGroup => !OptionalGroup.IsDefault;
}

/// <summary><c>CLASS { fields } [WITH SYNTAX { ... }]</c>.</summary>
public sealed class ObjectClassDefinitionSyntax : ObjectClassSyntax
{
    /// <summary>Creates a class definition.</summary>
    public ObjectClassDefinitionSyntax(SourceText source, TextSpan span, ImmutableArray<FieldSpecSyntax> fields, ImmutableArray<SyntaxTokenSpecSyntax>? withSyntax) : base(source, span)
    {
        Fields = fields;
        WithSyntax = withSyntax;
    }

    /// <summary>Field specifications.</summary>
    public ImmutableArray<FieldSpecSyntax> Fields { get; }

    /// <summary>User-defined syntax, if present.</summary>
    public ImmutableArray<SyntaxTokenSpecSyntax>? WithSyntax { get; }
}

/// <summary>The built-in TYPE-IDENTIFIER or ABSTRACT-SYNTAX classes.</summary>
public sealed class BuiltinClassSyntax : ObjectClassSyntax
{
    /// <summary>Creates a builtin class reference.</summary>
    public BuiltinClassSyntax(SourceText source, TextSpan span, string name) : base(source, span) => Name = name;

    /// <summary>TYPE-IDENTIFIER or ABSTRACT-SYNTAX.</summary>
    public string Name { get; }
}

// ------------------------------------------------------------------ Values

/// <summary>Base class of value notation.</summary>
public abstract class ValueSyntax : SyntaxNode
{
    /// <summary>Creates a value node.</summary>
    protected ValueSyntax(SourceText source, TextSpan span) : base(source, span)
    {
    }
}

/// <summary>TRUE or FALSE.</summary>
public sealed class BooleanValueSyntax : ValueSyntax
{
    /// <summary>Creates a boolean value.</summary>
    public BooleanValueSyntax(SourceText source, TextSpan span, bool value) : base(source, span) => Value = value;

    /// <summary>The value.</summary>
    public bool Value { get; }
}

/// <summary>An integer literal, possibly negative.</summary>
public sealed class NumberValueSyntax : ValueSyntax
{
    /// <summary>Creates a number.</summary>
    public NumberValueSyntax(SourceText source, TextSpan span, BigInteger value) : base(source, span) => Value = value;

    /// <summary>The value.</summary>
    public BigInteger Value { get; }
}

/// <summary>A real literal or special real.</summary>
public sealed class RealValueSyntax : ValueSyntax
{
    /// <summary>Creates a real value.</summary>
    public RealValueSyntax(SourceText source, TextSpan span, string text, double value) : base(source, span)
    {
        Text = text;
        Value = value;
    }

    /// <summary>Source text of the literal (or PLUS-INFINITY etc.).</summary>
    public string Text { get; }

    /// <summary>Parsed value.</summary>
    public double Value { get; }
}

/// <summary>A character string literal.</summary>
public sealed class StringValueSyntax : ValueSyntax
{
    /// <summary>Creates a string value.</summary>
    public StringValueSyntax(SourceText source, TextSpan span, string value) : base(source, span) => Value = value;

    /// <summary>The decoded string.</summary>
    public string Value { get; }
}

/// <summary>A bstring or hstring literal.</summary>
public sealed class BinaryStringValueSyntax : ValueSyntax
{
    /// <summary>Creates a binary string value.</summary>
    public BinaryStringValueSyntax(SourceText source, TextSpan span, string digits, bool isHex) : base(source, span)
    {
        Digits = digits;
        IsHex = isHex;
    }

    /// <summary>Digits without quotes or suffix.</summary>
    public string Digits { get; }

    /// <summary>True for hstring.</summary>
    public bool IsHex { get; }
}

/// <summary>NULL.</summary>
public sealed class NullValueSyntax : ValueSyntax
{
    /// <summary>Creates a NULL value.</summary>
    public NullValueSyntax(SourceText source, TextSpan span) : base(source, span)
    {
    }
}

/// <summary>A value reference (also enumeration identifiers and named numbers): <c>Module.name{params}</c>.</summary>
public sealed class ValueReferenceSyntax : ValueSyntax
{
    /// <summary>Creates a value reference.</summary>
    public ValueReferenceSyntax(SourceText source, TextSpan span, NameSyntax? module, NameSyntax name, ImmutableArray<ActualParameterSyntax>? actualParameters) : base(source, span)
    {
        Module = module;
        Name = name;
        ActualParameters = actualParameters;
    }

    /// <summary>Module qualifier.</summary>
    public NameSyntax? Module { get; }

    /// <summary>The referenced name.</summary>
    public NameSyntax Name { get; }

    /// <summary>Actual parameters.</summary>
    public ImmutableArray<ActualParameterSyntax>? ActualParameters { get; }

    /// <inheritdoc />
    public override string ToString() => Module is null ? Name.Text : $"{Module.Text}.{Name.Text}";
}

/// <summary>A CHOICE value <c>alternative : value</c>.</summary>
public sealed class ChoiceValueSyntax : ValueSyntax
{
    /// <summary>Creates a choice value.</summary>
    public ChoiceValueSyntax(SourceText source, TextSpan span, NameSyntax alternative, ValueSyntax value) : base(source, span)
    {
        Alternative = alternative;
        Value = value;
    }

    /// <summary>The alternative.</summary>
    public NameSyntax Alternative { get; }

    /// <summary>The value.</summary>
    public ValueSyntax Value { get; }
}

/// <summary>
/// A braced value <c>{ ... }</c>. Braced notation is ambiguous without type information (SEQUENCE value,
/// OBJECT IDENTIFIER, BIT STRING named bits, SEQUENCE OF, information object, ...), so the contents are kept
/// as a token range and re-parsed by semantic analysis once the governing type is known.
/// </summary>
public sealed class BracedValueSyntax : ValueSyntax
{
    /// <summary>Creates a braced value.</summary>
    public BracedValueSyntax(SourceText source, TextSpan span, LexResult lex, int openBrace, int closeBrace) : base(source, span)
    {
        Lex = lex;
        OpenBrace = openBrace;
        CloseBrace = closeBrace;
    }

    /// <summary>The token stream.</summary>
    public LexResult Lex { get; }

    /// <summary>Token index of <c>{</c>.</summary>
    public int OpenBrace { get; }

    /// <summary>Token index of <c>}</c>.</summary>
    public int CloseBrace { get; }

    /// <summary>True for <c>{}</c>.</summary>
    public bool IsEmpty => CloseBrace == OpenBrace + 1;
}

/// <summary><c>CONTAINING value</c>.</summary>
public sealed class ContainingValueSyntax : ValueSyntax
{
    /// <summary>Creates a containing value.</summary>
    public ContainingValueSyntax(SourceText source, TextSpan span, ValueSyntax value) : base(source, span) => Value = value;

    /// <summary>The contained value.</summary>
    public ValueSyntax Value { get; }
}

/// <summary>An open type value <c>Type : value</c> (X.681 14.6).</summary>
public sealed class OpenTypeValueSyntax : ValueSyntax
{
    /// <summary>Creates an open type value.</summary>
    public OpenTypeValueSyntax(SourceText source, TextSpan span, TypeSyntax type, ValueSyntax value) : base(source, span)
    {
        Type = type;
        Value = value;
    }

    /// <summary>The actual type.</summary>
    public TypeSyntax Type { get; }

    /// <summary>The value.</summary>
    public ValueSyntax Value { get; }
}

/// <summary>Information from an object: <c>object.&amp;field</c>.</summary>
public sealed class FieldValueSyntax : ValueSyntax
{
    /// <summary>Creates a field value reference.</summary>
    public FieldValueSyntax(SourceText source, TextSpan span, NameSyntax? module, NameSyntax objectReference, ImmutableArray<NameSyntax> fieldPath) : base(source, span)
    {
        Module = module;
        ObjectReference = objectReference;
        FieldPath = fieldPath;
    }

    /// <summary>Module qualifier.</summary>
    public NameSyntax? Module { get; }

    /// <summary>The object (or object set) reference.</summary>
    public NameSyntax ObjectReference { get; }

    /// <summary>Field path.</summary>
    public ImmutableArray<NameSyntax> FieldPath { get; }
}

/// <summary>A value whose syntax failed to parse.</summary>
public sealed class ErrorValueSyntax : ValueSyntax
{
    /// <summary>Creates an error value.</summary>
    public ErrorValueSyntax(SourceText source, TextSpan span) : base(source, span)
    {
    }
}

// ------------------------------------------------------------------ Constraints

/// <summary>A parenthesized constraint <c>( spec ! exception )</c>.</summary>
public sealed class ConstraintSyntax : SyntaxNode
{
    /// <summary>Creates a constraint.</summary>
    public ConstraintSyntax(SourceText source, TextSpan span, ConstraintSpecSyntax spec, ExceptionSpecSyntax? exception) : base(source, span)
    {
        Spec = spec;
        Exception = exception;
    }

    /// <summary>Constraint spec.</summary>
    public ConstraintSpecSyntax Spec { get; }

    /// <summary>Exception spec.</summary>
    public ExceptionSpecSyntax? Exception { get; }
}

/// <summary>Base of constraint specifications.</summary>
public abstract class ConstraintSpecSyntax : SyntaxNode
{
    /// <summary>Creates a spec.</summary>
    protected ConstraintSpecSyntax(SourceText source, TextSpan span) : base(source, span)
    {
    }
}

/// <summary>Subtype constraint: <c>Root [, ... [, Additions]]</c>.</summary>
public sealed class ElementSetSpecsSyntax : ConstraintSpecSyntax
{
    /// <summary>Creates element set specs.</summary>
    public ElementSetSpecsSyntax(SourceText source, TextSpan span, ElementSetSyntax? root, bool isExtensible, ElementSetSyntax? additions) : base(source, span)
    {
        Root = root;
        IsExtensible = isExtensible;
        Additions = additions;
    }

    /// <summary>Root element set (may be null for <c>{ ... }</c> object sets).</summary>
    public ElementSetSyntax? Root { get; }

    /// <summary>True when an extension marker is present.</summary>
    public bool IsExtensible { get; }

    /// <summary>Additional element set.</summary>
    public ElementSetSyntax? Additions { get; }
}

/// <summary>Table constraint: <c>({ObjectSet})</c> or <c>({ObjectSet}{@a.b, @.c})</c>.</summary>
public sealed class TableConstraintSyntax : ConstraintSpecSyntax
{
    /// <summary>Creates a table constraint.</summary>
    public TableConstraintSyntax(SourceText source, TextSpan span, ElementSetSpecsSyntax objectSet, ImmutableArray<AtNotationSyntax> atNotations) : base(source, span)
    {
        ObjectSet = objectSet;
        AtNotations = atNotations;
    }

    /// <summary>The object set.</summary>
    public ElementSetSpecsSyntax ObjectSet { get; }

    /// <summary>Component relations; empty for a simple table constraint.</summary>
    public ImmutableArray<AtNotationSyntax> AtNotations { get; }
}

/// <summary>An at-notation <c>@.a.b</c>.</summary>
public sealed class AtNotationSyntax : SyntaxNode
{
    /// <summary>Creates an at-notation.</summary>
    public AtNotationSyntax(SourceText source, TextSpan span, int level, ImmutableArray<NameSyntax> path) : base(source, span)
    {
        Level = level;
        Path = path;
    }

    /// <summary>Number of leading dots (0 means relative to the outermost type).</summary>
    public int Level { get; }

    /// <summary>Component identifiers.</summary>
    public ImmutableArray<NameSyntax> Path { get; }
}

/// <summary><c>CONSTRAINED BY { ... }</c>.</summary>
public sealed class UserDefinedConstraintSyntax : ConstraintSpecSyntax
{
    /// <summary>Creates a user-defined constraint.</summary>
    public UserDefinedConstraintSyntax(SourceText source, TextSpan span) : base(source, span)
    {
    }
}

/// <summary><c>CONTAINING Type [ENCODED BY value]</c> / <c>ENCODED BY value</c>.</summary>
public sealed class ContentsConstraintSyntax : ConstraintSpecSyntax
{
    /// <summary>Creates a contents constraint.</summary>
    public ContentsConstraintSyntax(SourceText source, TextSpan span, TypeSyntax? containedType, ValueSyntax? encodedBy) : base(source, span)
    {
        ContainedType = containedType;
        EncodedBy = encodedBy;
    }

    /// <summary>The contained type.</summary>
    public TypeSyntax? ContainedType { get; }

    /// <summary>The encoding OID value.</summary>
    public ValueSyntax? EncodedBy { get; }
}

/// <summary>Base of element set expressions.</summary>
public abstract class ElementSetSyntax : SyntaxNode
{
    /// <summary>Creates an element set node.</summary>
    protected ElementSetSyntax(SourceText source, TextSpan span) : base(source, span)
    {
    }
}

/// <summary>Union of element sets (<c>|</c> or UNION).</summary>
public sealed class UnionSyntax : ElementSetSyntax
{
    /// <summary>Creates a union.</summary>
    public UnionSyntax(SourceText source, TextSpan span, ImmutableArray<ElementSetSyntax> items) : base(source, span) => Items = items;

    /// <summary>Operands.</summary>
    public ImmutableArray<ElementSetSyntax> Items { get; }
}

/// <summary>Intersection (<c>^</c> or INTERSECTION).</summary>
public sealed class IntersectionSyntax : ElementSetSyntax
{
    /// <summary>Creates an intersection.</summary>
    public IntersectionSyntax(SourceText source, TextSpan span, ImmutableArray<ElementSetSyntax> items) : base(source, span) => Items = items;

    /// <summary>Operands.</summary>
    public ImmutableArray<ElementSetSyntax> Items { get; }
}

/// <summary><c>A EXCEPT B</c>.</summary>
public sealed class ExceptSyntax : ElementSetSyntax
{
    /// <summary>Creates an exclusion.</summary>
    public ExceptSyntax(SourceText source, TextSpan span, ElementSetSyntax included, ElementSetSyntax excluded) : base(source, span)
    {
        Included = included;
        Excluded = excluded;
    }

    /// <summary>Included set.</summary>
    public ElementSetSyntax Included { get; }

    /// <summary>Excluded set.</summary>
    public ElementSetSyntax Excluded { get; }
}

/// <summary><c>ALL EXCEPT B</c>.</summary>
public sealed class AllExceptSyntax : ElementSetSyntax
{
    /// <summary>Creates a complement.</summary>
    public AllExceptSyntax(SourceText source, TextSpan span, ElementSetSyntax excluded) : base(source, span) => Excluded = excluded;

    /// <summary>Excluded set.</summary>
    public ElementSetSyntax Excluded { get; }
}

/// <summary>A single value element (or object reference / inline object in object sets).</summary>
public sealed class SingleValueSyntax : ElementSetSyntax
{
    /// <summary>Creates a single value.</summary>
    public SingleValueSyntax(SourceText source, TextSpan span, ValueSyntax value) : base(source, span) => Value = value;

    /// <summary>The value.</summary>
    public ValueSyntax Value { get; }
}

/// <summary>A contained subtype, type constraint, or object set reference element.</summary>
public sealed class ContainedSubtypeSyntax : ElementSetSyntax
{
    /// <summary>Creates a contained subtype.</summary>
    public ContainedSubtypeSyntax(SourceText source, TextSpan span, TypeSyntax type, bool includes) : base(source, span)
    {
        Type = type;
        Includes = includes;
    }

    /// <summary>The type.</summary>
    public TypeSyntax Type { get; }

    /// <summary>True when written with INCLUDES.</summary>
    public bool Includes { get; }
}

/// <summary>An endpoint of a value range.</summary>
public sealed class RangeEndpointSyntax : SyntaxNode
{
    /// <summary>Creates an endpoint.</summary>
    public RangeEndpointSyntax(SourceText source, TextSpan span, ValueSyntax? value, bool isOpen) : base(source, span)
    {
        Value = value;
        IsOpen = isOpen;
    }

    /// <summary>The value; null for MIN or MAX.</summary>
    public ValueSyntax? Value { get; }

    /// <summary>True when the endpoint is excluded (<c>&lt;</c>).</summary>
    public bool IsOpen { get; }
}

/// <summary><c>lower..upper</c>.</summary>
public sealed class ValueRangeSyntax : ElementSetSyntax
{
    /// <summary>Creates a value range.</summary>
    public ValueRangeSyntax(SourceText source, TextSpan span, RangeEndpointSyntax lower, RangeEndpointSyntax upper) : base(source, span)
    {
        Lower = lower;
        Upper = upper;
    }

    /// <summary>Lower endpoint.</summary>
    public RangeEndpointSyntax Lower { get; }

    /// <summary>Upper endpoint.</summary>
    public RangeEndpointSyntax Upper { get; }
}

/// <summary><c>SIZE (constraint)</c>.</summary>
public sealed class SizeConstraintSyntax : ElementSetSyntax
{
    /// <summary>Creates a size constraint.</summary>
    public SizeConstraintSyntax(SourceText source, TextSpan span, ConstraintSyntax constraint) : base(source, span) => Constraint = constraint;

    /// <summary>The constraint on the size.</summary>
    public ConstraintSyntax Constraint { get; }
}

/// <summary><c>FROM (constraint)</c>.</summary>
public sealed class PermittedAlphabetSyntax : ElementSetSyntax
{
    /// <summary>Creates a permitted alphabet constraint.</summary>
    public PermittedAlphabetSyntax(SourceText source, TextSpan span, ConstraintSyntax constraint) : base(source, span) => Constraint = constraint;

    /// <summary>The constraint on characters.</summary>
    public ConstraintSyntax Constraint { get; }
}

/// <summary><c>PATTERN value</c>.</summary>
public sealed class PatternConstraintSyntax : ElementSetSyntax
{
    /// <summary>Creates a pattern constraint.</summary>
    public PatternConstraintSyntax(SourceText source, TextSpan span, ValueSyntax pattern) : base(source, span) => Pattern = pattern;

    /// <summary>The pattern value.</summary>
    public ValueSyntax Pattern { get; }
}

/// <summary><c>SETTINGS "..."</c> property settings for time types.</summary>
public sealed class PropertySettingsSyntax : ElementSetSyntax
{
    /// <summary>Creates property settings.</summary>
    public PropertySettingsSyntax(SourceText source, TextSpan span, string settings) : base(source, span) => Settings = settings;

    /// <summary>The settings string.</summary>
    public string Settings { get; }
}

/// <summary><c>WITH COMPONENT (constraint)</c>.</summary>
public sealed class SingleTypeConstraintSyntax : ElementSetSyntax
{
    /// <summary>Creates an inner single-type constraint.</summary>
    public SingleTypeConstraintSyntax(SourceText source, TextSpan span, ConstraintSyntax constraint) : base(source, span) => Constraint = constraint;

    /// <summary>The element constraint.</summary>
    public ConstraintSyntax Constraint { get; }
}

/// <summary>Presence constraint in WITH COMPONENTS.</summary>
public enum PresenceConstraint
{
    /// <summary>Not specified.</summary>
    None,

    /// <summary>PRESENT</summary>
    Present,

    /// <summary>ABSENT</summary>
    Absent,

    /// <summary>OPTIONAL</summary>
    Optional,
}

/// <summary>A named constraint within WITH COMPONENTS.</summary>
public sealed class NamedConstraintSyntax : SyntaxNode
{
    /// <summary>Creates a named constraint.</summary>
    public NamedConstraintSyntax(SourceText source, TextSpan span, NameSyntax name, ConstraintSyntax? valueConstraint, PresenceConstraint presence) : base(source, span)
    {
        Name = name;
        ValueConstraint = valueConstraint;
        Presence = presence;
    }

    /// <summary>The component identifier.</summary>
    public NameSyntax Name { get; }

    /// <summary>The value constraint.</summary>
    public ConstraintSyntax? ValueConstraint { get; }

    /// <summary>Presence constraint.</summary>
    public PresenceConstraint Presence { get; }
}

/// <summary><c>WITH COMPONENTS { [..., ] named constraints }</c>.</summary>
public sealed class MultipleTypeConstraintsSyntax : ElementSetSyntax
{
    /// <summary>Creates a multiple type constraint.</summary>
    public MultipleTypeConstraintsSyntax(SourceText source, TextSpan span, bool isPartial, ImmutableArray<NamedConstraintSyntax> constraints) : base(source, span)
    {
        IsPartial = isPartial;
        Constraints = constraints;
    }

    /// <summary>True for a partial specification (leading <c>...</c>).</summary>
    public bool IsPartial { get; }

    /// <summary>Named constraints.</summary>
    public ImmutableArray<NamedConstraintSyntax> Constraints { get; }
}

/// <summary>A parenthesized nested element set <c>( ... )</c>, which may itself be extensible.</summary>
public sealed class NestedElementSetSyntax : ElementSetSyntax
{
    /// <summary>Creates a nested set.</summary>
    public NestedElementSetSyntax(SourceText source, TextSpan span, ElementSetSpecsSyntax inner) : base(source, span) => Inner = inner;

    /// <summary>The nested specification.</summary>
    public ElementSetSpecsSyntax Inner { get; }
}
