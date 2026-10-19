using System.Collections.Immutable;
using Asn1.Diagnostics;
using Asn1.Syntax;

namespace Asn1.Semantics;

/// <summary>A resolved module.</summary>
public sealed class ModuleSymbol
{
    private readonly Dictionary<string, AssignmentSymbol> _assignments = new(StringComparer.Ordinal);
    private readonly List<AssignmentSymbol> _ordered = [];

    internal ModuleSymbol(ModuleDefinitionSyntax syntax)
    {
        Syntax = syntax;
        Name = syntax.Name.Text;
    }

    /// <summary>The module reference.</summary>
    public string Name { get; }

    /// <summary>The syntax.</summary>
    public ModuleDefinitionSyntax Syntax { get; }

    /// <summary>The module's definitive object identifier, if resolvable.</summary>
    public ObjectIdentifier? Oid { get; internal set; }

    /// <summary>Tag default (Unspecified means EXPLICIT).</summary>
    public TagDefault TagDefault => Syntax.TagDefault;

    /// <summary>True for EXTENSIBILITY IMPLIED.</summary>
    public bool ExtensibilityImplied => Syntax.ExtensibilityImplied;

    /// <summary>Source location of the module name.</summary>
    public SourceLocation Location => Syntax.Name.Location;

    /// <summary>Assignments in source order.</summary>
    public IReadOnlyList<AssignmentSymbol> Assignments => _ordered;

    /// <summary>Imported symbols.</summary>
    public ImmutableArray<ImportedSymbol> Imports { get; internal set; } = [];

    /// <summary>True when everything is exported (no EXPORTS clause, or EXPORTS ALL).</summary>
    public bool ExportsAll => Syntax.Exports is null || Syntax.Exports.All;

    /// <summary>Explicitly exported names.</summary>
    public ImmutableHashSet<string> ExportedNames { get; internal set; } = [];

    /// <summary>True if <paramref name="name"/> is visible to importing modules.</summary>
    public bool IsExported(string name) => ExportsAll || ExportedNames.Contains(name);

    /// <summary>Finds a local assignment.</summary>
    public AssignmentSymbol? FindLocal(string name) => _assignments.GetValueOrDefault(name);

    /// <summary>Type assignments in source order.</summary>
    public IEnumerable<TypeAssignmentSymbol> TypeAssignments => _ordered.OfType<TypeAssignmentSymbol>();

    /// <summary>Value assignments in source order.</summary>
    public IEnumerable<ValueAssignmentSymbol> ValueAssignments => _ordered.OfType<ValueAssignmentSymbol>();

    internal void Replace(AssignmentSymbol old, AssignmentSymbol replacement)
    {
        _assignments[old.Name] = replacement;
        var index = _ordered.IndexOf(old);
        if (index >= 0)
        {
            _ordered[index] = replacement;
        }
    }

    internal bool TryAdd(AssignmentSymbol symbol)
    {
        if (!_assignments.TryAdd(symbol.Name, symbol))
        {
            return false;
        }

        _ordered.Add(symbol);
        return true;
    }

    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>An imported symbol.</summary>
/// <param name="Name">The symbol name.</param>
/// <param name="FromModule">The module named in the import.</param>
/// <param name="Target">The resolved symbol in the source module, or null when unresolved.</param>
/// <param name="Syntax">The import syntax.</param>
public sealed record ImportedSymbol(string Name, string FromModule, AssignmentSymbol? Target, SymbolSyntax Syntax);

/// <summary>Base class of assignments.</summary>
public abstract class AssignmentSymbol
{
    internal AssignmentSymbol(ModuleSymbol module, AssignmentSyntax syntax)
    {
        Module = module;
        Syntax = syntax;
        Name = syntax.Name.Text;
    }

    /// <summary>The defined name.</summary>
    public string Name { get; }

    /// <summary>The defining module.</summary>
    public ModuleSymbol Module { get; }

    /// <summary>The syntax.</summary>
    public AssignmentSyntax Syntax { get; }

    /// <summary>Location of the name.</summary>
    public SourceLocation Location => Syntax.Name.Location;

    /// <summary>Attached documentation comments.</summary>
    public string? Documentation => Syntax.Documentation;

    /// <summary>True for parameterized assignments.</summary>
    public bool IsParameterized => Syntax.IsParameterized;

    /// <summary>Qualified name <c>Module.Name</c>.</summary>
    public string QualifiedName => Module.Name + "." + Name;

    /// <summary>A short noun for diagnostics ("type", "value", ...).</summary>
    public abstract string KindName { get; }

    /// <inheritdoc />
    public override string ToString() => QualifiedName;
}

/// <summary>A type assignment, or a value set assignment (which defines a constrained type).</summary>
public sealed class TypeAssignmentSymbol : AssignmentSymbol
{
    internal TypeAssignmentSymbol(ModuleSymbol module, AssignmentSyntax syntax, bool isValueSet) : base(module, syntax) => IsValueSet = isValueSet;

    /// <summary>True for a value set assignment.</summary>
    public bool IsValueSet { get; }

    /// <summary>The resolved type. Null for parameterized assignments (which are instantiated per use).</summary>
    public AsnType? Type { get; internal set; }

    /// <summary>The type syntax.</summary>
    public TypeSyntax? TypeSyntax => Syntax switch
    {
        TypeAssignmentSyntax t => t.Type,
        SetAssignmentSyntax s => s.Governor,
        _ => null,
    };

    /// <inheritdoc />
    public override string KindName => IsValueSet ? "value set" : "type";
}

/// <summary>A value assignment.</summary>
public sealed class ValueAssignmentSymbol : AssignmentSymbol
{
    internal ValueAssignmentSymbol(ModuleSymbol module, ValueAssignmentSyntax syntax) : base(module, syntax)
    {
    }

    /// <summary>The governing type.</summary>
    public AsnType? Type { get; internal set; }

    /// <summary>The resolved value.</summary>
    public AsnValue? Value { get; internal set; }

    /// <inheritdoc />
    public override string KindName => "value";
}

/// <summary>An information object class assignment.</summary>
public sealed class ClassAssignmentSymbol : AssignmentSymbol
{
    internal ClassAssignmentSymbol(ModuleSymbol module, AssignmentSyntax syntax) : base(module, syntax)
    {
    }

    /// <summary>The resolved class.</summary>
    public ObjectClass? Class { get; internal set; }

    /// <inheritdoc />
    public override string KindName => "information object class";
}

/// <summary>An information object assignment.</summary>
public sealed class ObjectAssignmentSymbol : AssignmentSymbol
{
    internal ObjectAssignmentSymbol(ModuleSymbol module, ValueAssignmentSyntax syntax) : base(module, syntax)
    {
    }

    /// <summary>The resolved object.</summary>
    public InformationObject? Object { get; internal set; }

    /// <inheritdoc />
    public override string KindName => "information object";
}

/// <summary>An information object set assignment.</summary>
public sealed class ObjectSetAssignmentSymbol : AssignmentSymbol
{
    internal ObjectSetAssignmentSymbol(ModuleSymbol module, SetAssignmentSyntax syntax) : base(module, syntax)
    {
    }

    /// <summary>The resolved object set.</summary>
    public ObjectSet? ObjectSet { get; internal set; }

    /// <inheritdoc />
    public override string KindName => "information object set";
}

/// <summary>A skipped X.208 macro definition.</summary>
public sealed class MacroAssignmentSymbol : AssignmentSymbol
{
    internal MacroAssignmentSymbol(ModuleSymbol module, AssignmentSyntax syntax) : base(module, syntax)
    {
    }

    /// <inheritdoc />
    public override string KindName => "macro";
}
