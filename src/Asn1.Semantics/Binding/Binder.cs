using System.Collections.Immutable;
using Asn1.Diagnostics;
using Asn1.Syntax;

namespace Asn1.Semantics.Binding;

/// <summary>Name resolution scope: a module plus the dummy references of a parameterized assignment being instantiated.</summary>
internal sealed class Scope
{
    public Scope(ModuleSymbol module, ImmutableDictionary<string, ParamBinding>? parameters = null, int depth = 0)
    {
        Module = module;
        Parameters = parameters ?? ImmutableDictionary<string, ParamBinding>.Empty;
        Depth = depth;
    }

    public ModuleSymbol Module { get; }

    public ImmutableDictionary<string, ParamBinding> Parameters { get; }

    public int Depth { get; }
}

/// <summary>Binding of a dummy reference to an actual parameter.</summary>
internal sealed class ParamBinding
{
    public ParamBinding(ParameterSyntax formal, ActualParameterSyntax actual, Scope callerScope)
    {
        Formal = formal;
        Actual = actual;
        CallerScope = callerScope;
    }

    public ParameterSyntax Formal { get; }

    public ActualParameterSyntax Actual { get; }

    public Scope CallerScope { get; }

    /// <summary>The governor resolved in the definition scope (set lazily).</summary>
    public object? Governor { get; set; }

    public Scope? DefinitionScope { get; set; }

    public AsnValue? CachedValue { get; set; }
}

/// <summary>Aborts binding after a resource limit was reported.</summary>
internal sealed class ResourceLimitAbortException : Exception
{
}

/// <summary>Per-node binder bookkeeping.</summary>
internal sealed class NodeState
{
    public NodeState(TypeSyntax? syntax, Scope scope)
    {
        Syntax = syntax;
        Scope = scope;
    }

    public TypeSyntax? Syntax { get; }

    public Scope Scope { get; }

    public List<(ConstraintSpecSyntax Spec, Scope Scope, SourceLocation Location)> PendingConstraints { get; } = [];

    public bool StructureQueued { get; set; }

    public bool StructureDone { get; set; }

    public bool StructureInProgress { get; set; }

    public bool ConstraintsDone { get; set; }

    public bool ConstraintsInProgress { get; set; }

    public Action? StructureWork { get; set; }
}

/// <summary>The semantic binder. Resolves modules, symbols, types, values, constraints and information objects.</summary>
internal sealed partial class Binder
{
    private readonly DiagnosticBag _diagnostics;
    private readonly Asn1CompilerOptions _options;
    private readonly Dictionary<string, ModuleSymbol> _modules = new(StringComparer.Ordinal);
    private readonly List<ModuleSymbol> _moduleList = [];
    private readonly List<AsnType> _allNodes = [];
    private readonly Dictionary<AsnType, NodeState> _nodeStates = new(ReferenceEqualityComparer.Instance);
    private readonly Queue<AsnType> _structureQueue = new();
    private readonly HashSet<AssignmentSymbol> _inProgress = new(ReferenceEqualityComparer.Instance);
    private bool _nodeLimitReported;

    public Binder(DiagnosticBag diagnostics, Asn1CompilerOptions options)
    {
        _diagnostics = diagnostics;
        _options = options;
    }

    public ImmutableArray<ModuleSymbol> Modules => [.. _moduleList];

    public ImmutableArray<AsnType> AllNodes => [.. _allNodes];

    private void Report(DiagnosticDescriptor d, SourceLocation location, params object?[] args) => _diagnostics.Report(d, location, args);

    public ImmutableArray<ModuleSymbol> Bind(ImmutableArray<CompilationUnitSyntax> trees)
    {
        CreateModules(trees);
        CreateAssignments();
        ClassifyAssignments();
        BindImportsAndExports();
        ResolveModuleOids();
        ResolveAll();
        RunChecks();
        return Modules;
    }

    // ------------------------------------------------------------------ declaration phase

    private void CreateModules(ImmutableArray<CompilationUnitSyntax> trees)
    {
        foreach (var tree in trees)
        {
            foreach (var m in tree.Modules)
            {
                var symbol = new ModuleSymbol(m);
                if (_modules.TryGetValue(symbol.Name, out var existing))
                {
                    _diagnostics.Add(Diagnostic.Create(DiagnosticCatalog.DuplicateModule, m.Name.Location, [new RelatedLocation(existing.Location, "previous definition")], symbol.Name));
                    continue;
                }

                _modules.Add(symbol.Name, symbol);
                _moduleList.Add(symbol);
            }
        }
    }

    private void CreateAssignments()
    {
        foreach (var module in _moduleList)
        {
            foreach (var a in module.Syntax.Assignments)
            {
                AssignmentSymbol symbol = a switch
                {
                    TypeAssignmentSyntax => new TypeAssignmentSymbol(module, a, isValueSet: false),
                    SetAssignmentSyntax => new TypeAssignmentSymbol(module, a, isValueSet: true),
                    ValueAssignmentSyntax v => new ValueAssignmentSymbol(module, v),
                    ClassAssignmentSyntax => new ClassAssignmentSymbol(module, a),
                    MacroAssignmentSyntax => new MacroAssignmentSymbol(module, a),
                    _ => throw new InvalidOperationException("unknown assignment syntax"),
                };
                AddSymbol(module, symbol);
            }
        }
    }

    private void AddSymbol(ModuleSymbol module, AssignmentSymbol symbol)
    {
        if (!module.TryAdd(symbol))
        {
            var existing = module.FindLocal(symbol.Name)!;
            _diagnostics.Add(Diagnostic.Create(DiagnosticCatalog.DuplicateDefinition, symbol.Location, [new RelatedLocation(existing.Location, "previous definition")], symbol.Name, module.Name));
        }
    }

    /// <summary>
    /// Re-classifies assignments whose kind depends on name resolution: class aliases, information objects
    /// (value assignments governed by a class) and object sets (set assignments governed by a class).
    /// </summary>
    private void ClassifyAssignments()
    {
        bool changed;
        var guard = 0;
        do
        {
            changed = false;
            foreach (var module in _moduleList)
            {
                foreach (var symbol in module.Assignments.ToList())
                {
                    AssignmentSymbol? replacement = null;
                    switch (symbol)
                    {
                        case TypeAssignmentSymbol { IsValueSet: false, Syntax: TypeAssignmentSyntax { Type: TypeReferenceSyntax { ActualParameters: null } r } } when NamesClass(module, r):
                            replacement = new ClassAssignmentSymbol(module, symbol.Syntax);
                            break;
                        case TypeAssignmentSymbol { IsValueSet: true, Syntax: SetAssignmentSyntax s } when s.Governor is TypeReferenceSyntax gr && NamesClass(module, gr):
                            replacement = new ObjectSetAssignmentSymbol(module, s);
                            break;
                        case ValueAssignmentSymbol { Syntax: ValueAssignmentSyntax v } when v.Type is TypeReferenceSyntax vr && NamesClass(module, vr):
                            replacement = new ObjectAssignmentSymbol(module, v);
                            break;
                    }

                    if (replacement is not null)
                    {
                        module.Replace(symbol, replacement);
                        changed = true;
                    }
                }
            }
        }
        while (changed && ++guard < 16);
    }

    private bool NamesClass(ModuleSymbol module, TypeReferenceSyntax reference)
    {
        if (reference.Module is null && reference.Name.Text is "TYPE-IDENTIFIER" or "ABSTRACT-SYNTAX")
        {
            return true;
        }

        var symbol = reference.Module is not null
            ? (_modules.GetValueOrDefault(reference.Module.Text)?.FindLocal(reference.Name.Text))
            : ResolveName(module, reference.Name.Text, new HashSet<string>(StringComparer.Ordinal));
        return symbol is ClassAssignmentSymbol;
    }

    // ------------------------------------------------------------------ imports / exports

    private void BindImportsAndExports()
    {
        // Exports first: import resolution consults the export lists of other modules.
        foreach (var module in _moduleList)
        {
            if (module.Syntax.Exports is { All: false } exports)
            {
                module.ExportedNames = exports.Symbols.Select(e => e.Name.Text).ToImmutableHashSet(StringComparer.Ordinal);
            }
        }

        foreach (var module in _moduleList)
        {
            var imports = ImmutableArray.CreateBuilder<ImportedSymbol>();
            foreach (var group in module.Syntax.Imports)
            {
                var source = _modules.GetValueOrDefault(group.ModuleName.Text);
                if (source is null)
                {
                    Report(DiagnosticCatalog.ImportedModuleNotFound, group.ModuleName.Location, group.ModuleName.Text, module.Name);
                }

                foreach (var sym in group.Symbols)
                {
                    AssignmentSymbol? target = null;
                    if (source is not null)
                    {
                        target = ResolveExported(source, sym.Name.Text, new HashSet<string>(StringComparer.Ordinal));
                        if (target is null)
                        {
                            Report(DiagnosticCatalog.ImportedSymbolNotFound, sym.Name.Location, sym.Name.Text, source.Name);
                        }
                    }

                    if (module.FindLocal(sym.Name.Text) is { } local)
                    {
                        _diagnostics.Add(Diagnostic.Create(DiagnosticCatalog.DuplicateDefinition, local.Location, [new RelatedLocation(sym.Name.Location, "imported here")], sym.Name.Text, module.Name));
                    }

                    imports.Add(new ImportedSymbol(sym.Name.Text, group.ModuleName.Text, target, sym));
                }
            }

            module.Imports = imports.ToImmutable();

            if (module.Syntax.Exports is { All: false } exports)
            {
                foreach (var e in exports.Symbols)
                {
                    if (module.FindLocal(e.Name.Text) is null && module.Syntax.Imports.SelectMany(g => g.Symbols).All(s => s.Name.Text != e.Name.Text))
                    {
                        Report(DiagnosticCatalog.ExportedSymbolNotDefined, e.Name.Location, e.Name.Text, module.Name);
                    }
                }
            }
        }
    }

    /// <summary>Resolves a name exported by <paramref name="module"/>, following re-exported imports.</summary>
    private AssignmentSymbol? ResolveExported(ModuleSymbol module, string name, HashSet<string> visited)
    {
        if (!module.IsExported(name) || !visited.Add(module.Name + "." + name))
        {
            return null;
        }

        return ResolveName(module, name, visited);
    }

    /// <summary>Resolves a name visible in <paramref name="module"/>: local definitions first, then imports.</summary>
    private AssignmentSymbol? ResolveName(ModuleSymbol module, string name, HashSet<string> visited)
    {
        if (module.FindLocal(name) is { } local)
        {
            return local;
        }

        foreach (var group in module.Syntax.Imports)
        {
            if (group.Symbols.Any(s => s.Name.Text == name) && _modules.TryGetValue(group.ModuleName.Text, out var source))
            {
                return ResolveExported(source, name, visited);
            }
        }

        return null;
    }

    // ------------------------------------------------------------------ lookup

    internal readonly record struct Lookup(AssignmentSymbol? Symbol, ParamBinding? Parameter, bool Reported)
    {
        public bool Found => Symbol is not null || Parameter is not null;
    }

    /// <summary>Looks up a (possibly module-qualified) reference, reporting failures.</summary>
    private Lookup LookupName(Scope scope, NameSyntax? moduleName, NameSyntax name, bool report = true)
    {
        if (moduleName is not null)
        {
            if (!_modules.TryGetValue(moduleName.Text, out var m))
            {
                if (report)
                {
                    Report(DiagnosticCatalog.UndefinedReference, moduleName.Location, moduleName.Text, " (unknown module)");
                }

                return new Lookup(null, null, report);
            }

            var target = m == scope.Module ? m.FindLocal(name.Text) : ResolveExported(m, name.Text, new HashSet<string>(StringComparer.Ordinal));
            if (target is null && report)
            {
                Report(DiagnosticCatalog.UndefinedReference, name.Location, moduleName.Text + "." + name.Text, string.Empty);
            }

            return new Lookup(target, null, report);
        }

        if (scope.Parameters.TryGetValue(name.Text, out var binding))
        {
            return new Lookup(null, binding, false);
        }

        if (scope.Module.FindLocal(name.Text) is { } local)
        {
            return new Lookup(local, null, false);
        }

        var candidates = scope.Module.Imports.Where(i => i.Name == name.Text).ToList();
        var resolved = candidates.Where(c => c.Target is not null).Select(c => c.Target!).Distinct().ToList();
        if (resolved.Count == 1)
        {
            return new Lookup(resolved[0], null, false);
        }

        if (resolved.Count > 1)
        {
            if (report)
            {
                Report(DiagnosticCatalog.AmbiguousReference, name.Location, name.Text, string.Join(", ", candidates.Select(c => c.FromModule)));
            }

            return new Lookup(resolved[0], null, report);
        }

        if (candidates.Count > 0)
        {
            // Import failed and was already reported.
            return new Lookup(null, null, true);
        }

        if (report)
        {
            Report(DiagnosticCatalog.UndefinedReference, name.Location, name.Text, string.Empty);
        }

        return new Lookup(null, null, report);
    }

    private Scope ModuleScope(ModuleSymbol module)
    {
        return _scopes.TryGetValue(module, out var s) ? s : _scopes[module] = new Scope(module);
    }

    private readonly Dictionary<ModuleSymbol, Scope> _scopes = new(ReferenceEqualityComparer.Instance);

    // ------------------------------------------------------------------ resolution driver

    private void ResolveAll()
    {
        foreach (var module in _moduleList)
        {
            foreach (var symbol in module.Assignments)
            {
                switch (symbol)
                {
                    case TypeAssignmentSymbol t when !t.IsParameterized:
                        EnsureTypeHeader(t);
                        break;
                    case ClassAssignmentSymbol c when !c.IsParameterized:
                        EnsureClass(c);
                        break;
                }
            }
        }

        DrainStructureQueue();

        foreach (var module in _moduleList)
        {
            foreach (var symbol in module.Assignments)
            {
                switch (symbol)
                {
                    case ValueAssignmentSymbol v when !v.IsParameterized:
                        EnsureValue(v);
                        break;
                    case ObjectAssignmentSymbol o when !o.IsParameterized:
                        EnsureObject(o);
                        break;
                    case ObjectSetAssignmentSymbol os when !os.IsParameterized:
                        EnsureObjectSet(os);
                        break;
                }
            }
        }

        DrainStructureQueue();

        // Constraints and DEFAULT values need complete structures; resolve them for every node.
        for (var i = 0; i < _allNodes.Count; i++)
        {
            EnsureConstraints(_allNodes[i]);
            DrainStructureQueue();
        }

        for (var i = 0; i < _allNodes.Count; i++)
        {
            ResolveDefaults(_allNodes[i]);
        }

        // Defaults may have created further nodes.
        for (var i = 0; i < _allNodes.Count; i++)
        {
            EnsureConstraints(_allNodes[i]);
            DrainStructureQueue();
        }
    }

    private void DrainStructureQueue()
    {
        while (_structureQueue.Count > 0)
        {
            var node = _structureQueue.Dequeue();
            EnsureStructure(node);
        }
    }

    private AsnType NewNode(TypeKind kind, SourceLocation location, TypeSyntax? syntax, Scope scope)
    {
        var node = new AsnType(kind, location) { Syntax = syntax };
        _allNodes.Add(node);
        _nodeStates[node] = new NodeState(syntax, scope);
        if (_allNodes.Count > _options.MaxTypeNodes && !_nodeLimitReported)
        {
            _nodeLimitReported = true;
            Report(DiagnosticCatalog.ResourceLimitExceeded, location, $"more than {_options.MaxTypeNodes} type nodes");
            throw new ResourceLimitAbortException();
        }

        return node;
    }

    private NodeState State(AsnType node) => _nodeStates[node];
}
