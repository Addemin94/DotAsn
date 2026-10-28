using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;

namespace Asn1.Diagnostics;

/// <summary>
/// The central registry of every diagnostic the toolchain can emit. Identifiers are stable:
/// once released, an identifier keeps its meaning. Ranges:
/// <list type="bullet">
/// <item><c>ASN0xxx</c> internal and driver diagnostics</item>
/// <item><c>ASN1xxx</c> lexical and syntax errors</item>
/// <item><c>ASN2xxx</c> semantic errors</item>
/// <item><c>ASN3xxx</c> constraint errors</item>
/// <item><c>ASN4xxx</c> unsupported features</item>
/// <item><c>ASN5xxx</c> lint style and maintainability rules</item>
/// <item><c>ASN6xxx</c> encoding-rule interoperability rules</item>
/// <item><c>ASN7xxx</c> code-generation diagnostics</item>
/// </list>
/// </summary>
public static class DiagnosticCatalog
{
    private const DiagnosticSeverity E = DiagnosticSeverity.Error;
    private const DiagnosticSeverity W = DiagnosticSeverity.Warning;
    private const DiagnosticSeverity I = DiagnosticSeverity.Info;

    private static DiagnosticDescriptor D(string id, DiagnosticCategory cat, DiagnosticSeverity sev, string title, string fmt) => new(id, title, fmt, cat, sev);

    // ---- ASN0xxx: internal / driver ----

    /// <summary>An unexpected failure inside the toolchain.</summary>
    public static readonly DiagnosticDescriptor InternalError = D("ASN0001", DiagnosticCategory.Internal, E, "Internal compiler error", "Internal compiler error: {0}");

    /// <summary>An input file could not be read.</summary>
    public static readonly DiagnosticDescriptor FileReadError = D("ASN0002", DiagnosticCategory.Internal, E, "Cannot read input", "Cannot read '{0}': {1}");

    /// <summary>An input exceeds a configured resource limit.</summary>
    public static readonly DiagnosticDescriptor ResourceLimitExceeded = D("ASN0003", DiagnosticCategory.Internal, E, "Resource limit exceeded", "Resource limit exceeded: {0}");

    /// <summary>No input files were found.</summary>
    public static readonly DiagnosticDescriptor NoInputs = D("ASN0004", DiagnosticCategory.Internal, E, "No input files", "No ASN.1 input files were found{0}");

    /// <summary>Invalid configuration.</summary>
    public static readonly DiagnosticDescriptor InvalidConfiguration = D("ASN0005", DiagnosticCategory.Internal, E, "Invalid configuration", "Invalid configuration: {0}");

    // ---- ASN1xxx: lexical / syntax ----

    /// <summary>Character not allowed in ASN.1 notation.</summary>
    public static readonly DiagnosticDescriptor UnexpectedCharacter = D("ASN1001", DiagnosticCategory.Syntax, E, "Unexpected character", "Unexpected character '{0}'");

    /// <summary>Character string literal not terminated.</summary>
    public static readonly DiagnosticDescriptor UnterminatedString = D("ASN1002", DiagnosticCategory.Syntax, E, "Unterminated string", "Unterminated character string literal");

    /// <summary>Block comment not terminated.</summary>
    public static readonly DiagnosticDescriptor UnterminatedComment = D("ASN1003", DiagnosticCategory.Syntax, E, "Unterminated comment", "Unterminated /* */ comment");

    /// <summary>Bad binary or hexadecimal string literal.</summary>
    public static readonly DiagnosticDescriptor InvalidBinaryString = D("ASN1004", DiagnosticCategory.Syntax, E, "Invalid bstring or hstring", "Invalid bit or hexadecimal string literal: {0}");

    /// <summary>Malformed reference name.</summary>
    public static readonly DiagnosticDescriptor InvalidIdentifier = D("ASN1005", DiagnosticCategory.Syntax, E, "Invalid identifier", "Invalid identifier '{0}': {1}");

    /// <summary>Malformed number literal.</summary>
    public static readonly DiagnosticDescriptor InvalidNumber = D("ASN1006", DiagnosticCategory.Syntax, E, "Invalid number", "Invalid number literal '{0}'");

    /// <summary>Generic parse error.</summary>
    public static readonly DiagnosticDescriptor UnexpectedToken = D("ASN1010", DiagnosticCategory.Syntax, E, "Syntax error", "Expected {0} but found {1}");

    /// <summary>Module without END.</summary>
    public static readonly DiagnosticDescriptor MissingEnd = D("ASN1011", DiagnosticCategory.Syntax, E, "Missing END", "Module '{0}' is missing its END keyword");

    /// <summary>Input does not contain a module.</summary>
    public static readonly DiagnosticDescriptor NoModule = D("ASN1012", DiagnosticCategory.Syntax, E, "No module definition", "The input does not contain an ASN.1 module definition");

    /// <summary>Invalid tag syntax.</summary>
    public static readonly DiagnosticDescriptor InvalidTag = D("ASN1013", DiagnosticCategory.Syntax, E, "Invalid tag", "Invalid tag: {0}");

    /// <summary>Use of the withdrawn ANY type or other X.208 notation.</summary>
    public static readonly DiagnosticDescriptor DeprecatedNotation = D("ASN1014", DiagnosticCategory.Syntax, W, "Deprecated notation", "'{0}' is withdrawn X.208 notation; {1}");

    /// <summary>Encoding control section skipped.</summary>
    public static readonly DiagnosticDescriptor EncodingControlIgnored = D("ASN1015", DiagnosticCategory.Unsupported, W, "Encoding control ignored", "ENCODING-CONTROL section for '{0}' is not supported and was ignored");

    /// <summary>Unexpected tokens after the last module.</summary>
    public static readonly DiagnosticDescriptor TrailingContent = D("ASN1016", DiagnosticCategory.Syntax, E, "Unexpected content", "Unexpected content after module END: {0}");

    /// <summary>Invalid real number literal.</summary>
    public static readonly DiagnosticDescriptor InvalidReal = D("ASN1017", DiagnosticCategory.Syntax, E, "Invalid real number", "Invalid real number literal '{0}'");

    /// <summary>Nesting deeper than the configured limit.</summary>
    public static readonly DiagnosticDescriptor NestingTooDeep = D("ASN1018", DiagnosticCategory.Syntax, E, "Nesting too deep", "Definition nesting exceeds the maximum depth of {0}");

    // ---- ASN2xxx: semantic ----

    /// <summary>Unresolved reference.</summary>
    public static readonly DiagnosticDescriptor UndefinedReference = D("ASN2001", DiagnosticCategory.Semantic, E, "Undefined reference", "'{0}' is not defined{1}");

    /// <summary>Two definitions with the same name in one module.</summary>
    public static readonly DiagnosticDescriptor DuplicateDefinition = D("ASN2002", DiagnosticCategory.Semantic, E, "Duplicate definition", "'{0}' is already defined in module '{1}'");

    /// <summary>Two modules with the same name.</summary>
    public static readonly DiagnosticDescriptor DuplicateModule = D("ASN2003", DiagnosticCategory.Semantic, E, "Duplicate module", "Module '{0}' is defined more than once");

    /// <summary>Import from an unknown module.</summary>
    public static readonly DiagnosticDescriptor ImportedModuleNotFound = D("ASN2004", DiagnosticCategory.Semantic, E, "Imported module not found", "Module '{0}' imported by '{1}' was not found among the inputs");

    /// <summary>Imported symbol not available in the source module.</summary>
    public static readonly DiagnosticDescriptor ImportedSymbolNotFound = D("ASN2005", DiagnosticCategory.Semantic, E, "Imported symbol not found", "'{0}' is not defined or not exported by module '{1}'");

    /// <summary>Exported symbol not defined.</summary>
    public static readonly DiagnosticDescriptor ExportedSymbolNotDefined = D("ASN2006", DiagnosticCategory.Semantic, E, "Invalid export", "Exported symbol '{0}' is not defined or imported in module '{1}'");

    /// <summary>Same symbol imported from several modules and used unqualified.</summary>
    public static readonly DiagnosticDescriptor AmbiguousReference = D("ASN2007", DiagnosticCategory.Semantic, E, "Ambiguous reference", "'{0}' is ambiguous between modules {1}; use a module-qualified reference");

    /// <summary>A type definition that refers to itself without an intervening structure that can terminate.</summary>
    public static readonly DiagnosticDescriptor IllegalRecursion = D("ASN2008", DiagnosticCategory.Semantic, E, "Illegal recursive definition", "Illegal recursive definition: {0}");

    /// <summary>Duplicate component or alternative identifier.</summary>
    public static readonly DiagnosticDescriptor DuplicateComponent = D("ASN2009", DiagnosticCategory.Semantic, E, "Duplicate component", "Duplicate identifier '{0}' in {1}");

    /// <summary>Tags that do not distinguish components.</summary>
    public static readonly DiagnosticDescriptor AmbiguousTags = D("ASN2010", DiagnosticCategory.Semantic, E, "Ambiguous tags", "Tag {0} of '{1}' is not distinct from '{2}' in {3}");

    /// <summary>Duplicate enumeration identifier.</summary>
    public static readonly DiagnosticDescriptor DuplicateEnumIdentifier = D("ASN2011", DiagnosticCategory.Semantic, E, "Duplicate enumeration identifier", "Duplicate enumeration identifier '{0}'");

    /// <summary>Duplicate enumeration value.</summary>
    public static readonly DiagnosticDescriptor DuplicateEnumValue = D("ASN2012", DiagnosticCategory.Semantic, E, "Duplicate enumeration value", "Enumeration value {0} of '{1}' is already used by '{2}'");

    /// <summary>Duplicate named number or named bit.</summary>
    public static readonly DiagnosticDescriptor DuplicateNamedNumber = D("ASN2013", DiagnosticCategory.Semantic, E, "Duplicate named number", "Duplicate named number or bit {0}");

    /// <summary>Value does not conform to its type.</summary>
    public static readonly DiagnosticDescriptor ValueTypeMismatch = D("ASN2014", DiagnosticCategory.Semantic, E, "Invalid value", "Value is not valid for type {0}: {1}");

    /// <summary>A reference of the wrong kind (for example a value where a type is required).</summary>
    public static readonly DiagnosticDescriptor WrongReferenceKind = D("ASN2016", DiagnosticCategory.Semantic, E, "Wrong reference kind", "'{0}' is a {1} but a {2} is required here");

    /// <summary>COMPONENTS OF applied to a non-SEQUENCE/SET type.</summary>
    public static readonly DiagnosticDescriptor InvalidComponentsOf = D("ASN2017", DiagnosticCategory.Semantic, E, "Invalid COMPONENTS OF", "COMPONENTS OF requires a {0} type but '{1}' is {2}");

    /// <summary>Selection type errors.</summary>
    public static readonly DiagnosticDescriptor InvalidSelection = D("ASN2018", DiagnosticCategory.Semantic, E, "Invalid selection type", "Invalid selection type: {0}");

    /// <summary>A DEFAULT value violates the component's constraint.</summary>
    public static readonly DiagnosticDescriptor DefaultViolatesConstraint = D("ASN2019", DiagnosticCategory.Semantic, E, "DEFAULT violates constraint", "DEFAULT value of '{0}' does not satisfy the constraint of its type");

    /// <summary>Invalid tag number.</summary>
    public static readonly DiagnosticDescriptor InvalidTagNumber = D("ASN2020", DiagnosticCategory.Semantic, E, "Invalid tag number", "Tag number must be a non-negative integer: {0}");

    /// <summary>IMPLICIT applied to an untagged CHOICE or open type.</summary>
    public static readonly DiagnosticDescriptor ImplicitTagOnChoice = D("ASN2021", DiagnosticCategory.Semantic, E, "Illegal IMPLICIT tag", "IMPLICIT tagging cannot be applied to {0}; the tag is treated as EXPLICIT");

    /// <summary>Wrong number of actual parameters.</summary>
    public static readonly DiagnosticDescriptor ParameterCountMismatch = D("ASN2022", DiagnosticCategory.Semantic, E, "Parameter count mismatch", "'{0}' expects {1} parameter(s) but {2} were supplied");

    /// <summary>Parameterized assignment referenced without actual parameters.</summary>
    public static readonly DiagnosticDescriptor MissingActualParameters = D("ASN2023", DiagnosticCategory.Semantic, E, "Missing actual parameters", "Parameterized '{0}' must be referenced with an actual parameter list");

    /// <summary>Import module identifier mismatch.</summary>
    public static readonly DiagnosticDescriptor ModuleIdentifierMismatch = D("ASN2024", DiagnosticCategory.Semantic, W, "Module identifier mismatch", "Import of '{0}' specifies an object identifier that differs from the module definition");

    /// <summary>CHOICE without alternatives.</summary>
    public static readonly DiagnosticDescriptor EmptyChoice = D("ASN2027", DiagnosticCategory.Semantic, E, "Empty CHOICE", "CHOICE type must have at least one alternative");

    /// <summary>Unknown component in a value.</summary>
    public static readonly DiagnosticDescriptor UnknownComponentInValue = D("ASN2028", DiagnosticCategory.Semantic, E, "Unknown component", "'{0}' is not a component of {1}");

    /// <summary>Missing mandatory component in a value.</summary>
    public static readonly DiagnosticDescriptor MissingComponentInValue = D("ASN2029", DiagnosticCategory.Semantic, E, "Missing component", "Value of {0} is missing mandatory component '{1}'");

    /// <summary>Information object errors.</summary>
    public static readonly DiagnosticDescriptor InformationObjectError = D("ASN2030", DiagnosticCategory.Semantic, E, "Invalid information object", "Invalid information object or class usage: {0}");

    /// <summary>Value definition depends on itself.</summary>
    public static readonly DiagnosticDescriptor CyclicValue = D("ASN2031", DiagnosticCategory.Semantic, E, "Cyclic value", "Value '{0}' is defined in terms of itself");

    /// <summary>Invalid object identifier value.</summary>
    public static readonly DiagnosticDescriptor InvalidObjectIdentifier = D("ASN2032", DiagnosticCategory.Semantic, E, "Invalid object identifier", "Invalid object identifier value: {0}");

    /// <summary>Extensibility errors (markers, version brackets).</summary>
    public static readonly DiagnosticDescriptor InvalidExtension = D("ASN2033", DiagnosticCategory.Semantic, E, "Invalid extension", "Invalid extension notation: {0}");

    /// <summary>Optional/default component rules.</summary>
    public static readonly DiagnosticDescriptor InvalidOptionality = D("ASN2034", DiagnosticCategory.Semantic, E, "Invalid OPTIONAL/DEFAULT", "{0}");

    // ---- ASN3xxx: constraints ----

    /// <summary>Constraint not applicable to the parent type.</summary>
    public static readonly DiagnosticDescriptor ConstraintNotApplicable = D("ASN3001", DiagnosticCategory.Constraint, E, "Constraint not applicable", "{0} constraint cannot be applied to {1}");

    /// <summary>Empty value range.</summary>
    public static readonly DiagnosticDescriptor EmptyRange = D("ASN3002", DiagnosticCategory.Constraint, E, "Empty range", "Range {0} is empty");

    /// <summary>The constraint admits no values.</summary>
    public static readonly DiagnosticDescriptor EmptyConstraint = D("ASN3003", DiagnosticCategory.Constraint, E, "Unsatisfiable constraint", "The constraint on {0} permits no values");

    /// <summary>Constraint operand of wrong type.</summary>
    public static readonly DiagnosticDescriptor InvalidConstraintValue = D("ASN3004", DiagnosticCategory.Constraint, E, "Invalid constraint value", "Invalid value in constraint: {0}");

    /// <summary>Constraint not within the parent type.</summary>
    public static readonly DiagnosticDescriptor ConstraintOutsideParent = D("ASN3005", DiagnosticCategory.Constraint, W, "Constraint outside parent", "Constraint {0} lies (partly) outside the parent type's permitted values {1}");

    /// <summary>Unknown component in WITH COMPONENTS.</summary>
    public static readonly DiagnosticDescriptor UnknownComponentInConstraint = D("ASN3006", DiagnosticCategory.Constraint, E, "Unknown component in constraint", "'{0}' is not a component of {1}");

    /// <summary>Bad PATTERN regular expression.</summary>
    public static readonly DiagnosticDescriptor InvalidPattern = D("ASN3007", DiagnosticCategory.Constraint, E, "Invalid pattern", "Invalid PATTERN: {0}");

    /// <summary>Invalid presence constraint.</summary>
    public static readonly DiagnosticDescriptor InvalidPresenceConstraint = D("ASN3008", DiagnosticCategory.Constraint, E, "Invalid presence constraint", "{0}");

    // ---- ASN4xxx: unsupported ----

    /// <summary>Valid ASN.1 that is not implemented.</summary>
    public static readonly DiagnosticDescriptor UnsupportedFeature = D("ASN4001", DiagnosticCategory.Unsupported, W, "Unsupported feature", "{0} is not supported yet{1}");

    /// <summary>Constraint that is kept as metadata but not checked at run time.</summary>
    public static readonly DiagnosticDescriptor ConstraintNotEnforced = D("ASN4004", DiagnosticCategory.Unsupported, I, "Constraint not enforced", "{0} constraint is recorded but not enforced at run time");

    // ---- ASN5xxx: style / maintainability ----

    /// <summary>Type reference naming convention.</summary>
    public static readonly DiagnosticDescriptor TypeNaming = D("ASN5001", DiagnosticCategory.Style, W, "Type naming convention", "Type '{0}' does not follow the {1} naming convention");

    /// <summary>Identifier naming convention.</summary>
    public static readonly DiagnosticDescriptor IdentifierNaming = D("ASN5002", DiagnosticCategory.Style, W, "Identifier naming convention", "Identifier '{0}' does not follow the {1} naming convention");

    /// <summary>Unused type or value assignment.</summary>
    public static readonly DiagnosticDescriptor UnusedDefinition = D("ASN5003", DiagnosticCategory.Style, I, "Unused definition", "'{0}' is neither referenced nor exported");

    /// <summary>Unused import.</summary>
    public static readonly DiagnosticDescriptor UnusedImport = D("ASN5004", DiagnosticCategory.Style, W, "Unused import", "Imported symbol '{0}' is never used");

    /// <summary>Alias that adds nothing.</summary>
    public static readonly DiagnosticDescriptor RedundantAlias = D("ASN5005", DiagnosticCategory.Style, I, "Redundant alias", "'{0}' is a plain alias of '{1}' without tags or constraints");

    /// <summary>Names that differ only by case or hyphenation.</summary>
    public static readonly DiagnosticDescriptor ConfusingNames = D("ASN5006", DiagnosticCategory.Style, W, "Confusing names", "'{0}' differs from '{1}' only by letter case or hyphens");

    /// <summary>Names that collide after mapping to C#.</summary>
    public static readonly DiagnosticDescriptor CSharpNameCollision = D("ASN5007", DiagnosticCategory.CodeGeneration, W, "C# name collision", "'{0}' and '{1}' map to the same C# name '{2}'; the generator will disambiguate");

    /// <summary>Deeply nested anonymous types.</summary>
    public static readonly DiagnosticDescriptor ExcessiveNesting = D("ASN5008", DiagnosticCategory.Style, I, "Excessive nesting", "'{0}' nests anonymous structured types {1} levels deep; consider named type assignments");

    /// <summary>Structured type without extension marker.</summary>
    public static readonly DiagnosticDescriptor NotExtensible = D("ASN5009", DiagnosticCategory.Interoperability, I, "Not extensible", "{0} '{1}' has no extension marker; adding components later breaks PER/OER compatibility");

    /// <summary>Extension marker with suspicious usage.</summary>
    public static readonly DiagnosticDescriptor SuspiciousExtension = D("ASN5010", DiagnosticCategory.Interoperability, W, "Suspicious extensibility", "{0}");

    /// <summary>Unconstrained INTEGER inside packed encodings.</summary>
    public static readonly DiagnosticDescriptor UnconstrainedInteger = D("ASN5011", DiagnosticCategory.Interoperability, I, "Unconstrained INTEGER", "'{0}' is an unconstrained INTEGER; PER/OER encode it with a length prefix and implementations may limit its size");

    /// <summary>ENUMERATED field whose wire form is its index.</summary>
    public static readonly DiagnosticDescriptor EnumeratedIndexEncoding = D("ASN5012", DiagnosticCategory.Interoperability, I, "ENUMERATED index encoding", "ENUMERATED '{0}' is encoded by index in PER; reordering items or crossing a power of two changes the wire format (consider an extension marker)");

    /// <summary>Upper bound that triggers PER fragmentation.</summary>
    public static readonly DiagnosticDescriptor FragmentationRisk = D("ASN5013", DiagnosticCategory.Interoperability, I, "PER fragmentation", "'{0}' has no SIZE upper bound below 64K; PER encodes values of 16K units or more with fragmentation (X.691 11.9.3.8)");

    /// <summary>Reserved C# keyword names.</summary>
    public static readonly DiagnosticDescriptor CSharpKeyword = D("ASN5015", DiagnosticCategory.CodeGeneration, I, "C# keyword", "'{0}' is a C# keyword and will be renamed to '{1}' in generated code");

    /// <summary>Module without explicit tag default.</summary>
    public static readonly DiagnosticDescriptor ImplicitTagDefault = D("ASN5017", DiagnosticCategory.Style, I, "Default tagging", "Module '{0}' has no tag default and therefore uses EXPLICIT TAGS");

    /// <summary>Unused value assignment.</summary>
    public static readonly DiagnosticDescriptor UnusedValue = D("ASN5020", DiagnosticCategory.Style, I, "Unused value", "Value '{0}' is neither referenced nor exported");

    /// <summary>Constraint that is wider than its parent or otherwise redundant.</summary>
    public static readonly DiagnosticDescriptor RedundantConstraint = D("ASN5014", DiagnosticCategory.Style, I, "Redundant constraint", "{0}");

    // ---- ASN6xxx: encoding-rule interoperability ----

    /// <summary>A type cannot be encoded by a selected encoding rule.</summary>
    public static readonly DiagnosticDescriptor EncodingRuleIncompatible = D("ASN6001", DiagnosticCategory.Interoperability, W, "Encoding rule incompatibility", "'{0}' cannot be encoded with {1}: {2}");

    // ---- ASN7xxx: code generation ----

    /// <summary>Generator renamed an identifier.</summary>
    public static readonly DiagnosticDescriptor IdentifierRenamed = D("ASN7001", DiagnosticCategory.CodeGeneration, I, "Identifier renamed", "'{0}' is generated as '{1}' ({2})");

    /// <summary>Type cannot be generated.</summary>
    public static readonly DiagnosticDescriptor GenerationUnsupported = D("ASN7002", DiagnosticCategory.CodeGeneration, W, "Generation unsupported", "'{0}' cannot be generated: {1}");

    /// <summary>An INTEGER value does not fit the generated (C# or TypeScript) type of its INTEGER type.</summary>
    public static readonly DiagnosticDescriptor ValueOutsideType = D("ASN7003", DiagnosticCategory.CodeGeneration, W, "Value outside its generated type", "the value {0} does not fit {1}, the {2} type of {3}; {4}");

    private static readonly Lazy<ImmutableArray<DiagnosticDescriptor>> s_all = new(() =>
        [.. typeof(DiagnosticCatalog)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(DiagnosticDescriptor))
            .Select(f => (DiagnosticDescriptor)f.GetValue(null)!)
            .OrderBy(d => d.Id, System.StringComparer.Ordinal)]);

    /// <summary>All known descriptors ordered by identifier.</summary>
    public static ImmutableArray<DiagnosticDescriptor> All => s_all.Value;

    /// <summary>Looks up a descriptor by identifier.</summary>
    public static DiagnosticDescriptor? Find(string id) => All.FirstOrDefault(d => d.Id == id);

    /// <summary>Returns descriptors by category.</summary>
    public static IEnumerable<DiagnosticDescriptor> ByCategory(DiagnosticCategory category) => All.Where(d => d.Category == category);
}
