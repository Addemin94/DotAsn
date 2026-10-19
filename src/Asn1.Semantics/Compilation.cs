using System.Collections.Immutable;
using Asn1.Diagnostics;
using Asn1.Semantics.Binding;
using Asn1.Syntax;

namespace Asn1.Semantics;

/// <summary>Options for <see cref="Asn1Compilation"/>.</summary>
public sealed record Asn1CompilerOptions
{
    /// <summary>Default options.</summary>
    public static readonly Asn1CompilerOptions Default = new();

    /// <summary>Parser limits.</summary>
    public ParserOptions Parser { get; init; } = ParserOptions.Default;

    /// <summary>Maximum nesting of parameterized type instantiations.</summary>
    public int MaxInstantiationDepth { get; init; } = 24;

    /// <summary>Maximum number of resolved type nodes (guards against definitions that explode in size).</summary>
    public int MaxTypeNodes { get; init; } = 2_000_000;
}

/// <summary>
/// The result of compiling a set of ASN.1 sources: syntax trees, resolved modules and diagnostics.
/// </summary>
public sealed class Asn1Compilation
{
    private readonly Dictionary<string, ModuleSymbol> _modulesByName;

    private Asn1Compilation(ImmutableArray<CompilationUnitSyntax> trees, ImmutableArray<ModuleSymbol> modules, ImmutableArray<Diagnostic> diagnostics, ImmutableArray<AsnType> allTypes)
    {
        SyntaxTrees = trees;
        Modules = modules;
        Diagnostics = diagnostics;
        AllTypeNodes = allTypes;
        _modulesByName = modules.GroupBy(m => m.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
    }

    /// <summary>Parsed files.</summary>
    public ImmutableArray<CompilationUnitSyntax> SyntaxTrees { get; }

    /// <summary>Resolved modules in input order.</summary>
    public ImmutableArray<ModuleSymbol> Modules { get; }

    /// <summary>All diagnostics, deterministically ordered.</summary>
    public ImmutableArray<Diagnostic> Diagnostics { get; }

    /// <summary>Every resolved type node (for analysis tools such as the linter).</summary>
    public ImmutableArray<AsnType> AllTypeNodes { get; }

    /// <summary>True if any error diagnostic was produced.</summary>
    public bool HasErrors => Diagnostics.Any(d => d.IsError);

    /// <summary>Compiles source texts.</summary>
    public static Asn1Compilation Create(IEnumerable<SourceText> sources, Asn1CompilerOptions? options = null)
    {
        options ??= Asn1CompilerOptions.Default;
        var bag = new DiagnosticBag();
        var trees = ImmutableArray.CreateBuilder<CompilationUnitSyntax>();
        foreach (var source in sources)
        {
            trees.Add(Parser.ParseCompilationUnit(source, bag, options.Parser));
        }

        var binder = new Binder(bag, options);
        ImmutableArray<ModuleSymbol> modules;
        try
        {
            modules = binder.Bind(trees.ToImmutable());
        }
        catch (ResourceLimitAbortException)
        {
            // Already reported as ASN0003.
            modules = binder.Modules;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var frame = ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim() ?? string.Empty;
            bag.Report(DiagnosticCatalog.InternalError, SourceLocation.None, ex.GetType().Name + ": " + ex.Message + " " + frame);
            modules = binder.Modules;
        }

        return new Asn1Compilation(trees.ToImmutable(), modules, bag.ToImmutableSorted(), binder.AllNodes);
    }

    /// <summary>Compiles a single in-memory source.</summary>
    public static Asn1Compilation FromText(string text, string path = "<memory>", Asn1CompilerOptions? options = null) =>
        Create([new SourceText(path, text)], options);

    /// <summary>Finds a module by name.</summary>
    public ModuleSymbol? GetModule(string name) => _modulesByName.GetValueOrDefault(name);

    /// <summary>
    /// Finds a type assignment by <c>Module.Type</c> or by unqualified name (which must be unique across modules).
    /// </summary>
    public TypeAssignmentSymbol? FindType(string name)
    {
        var dot = name.IndexOf('.', StringComparison.Ordinal);
        if (dot > 0)
        {
            return GetModule(name[..dot])?.FindLocal(name[(dot + 1)..]) as TypeAssignmentSymbol;
        }

        var matches = Modules.Select(m => m.FindLocal(name)).OfType<TypeAssignmentSymbol>().ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    /// <summary>All type assignments across modules.</summary>
    public IEnumerable<TypeAssignmentSymbol> TypeAssignments => Modules.SelectMany(m => m.TypeAssignments);
}
