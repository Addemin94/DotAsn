using Asn1.Diagnostics;
using Asn1.Semantics;

namespace Asn1.Linter;

/// <summary>A lint rule.</summary>
public abstract class LintRule
{
    /// <summary>The diagnostics this rule can report.</summary>
    public abstract IReadOnlyList<DiagnosticDescriptor> Descriptors { get; }

    /// <summary>Analyzes the compilation and reports diagnostics through <paramref name="context"/>.</summary>
    public abstract void Analyze(LintContext context);
}

/// <summary>State shared by rules during one lint run.</summary>
public sealed class LintContext
{
    private readonly List<Diagnostic> _diagnostics = [];
    private Dictionary<ModuleSymbol, HashSet<string>>? _references;

    internal LintContext(Asn1Compilation compilation, LintConfiguration configuration)
    {
        Compilation = compilation;
        Configuration = configuration;
    }

    /// <summary>The compilation (possibly with errors).</summary>
    public Asn1Compilation Compilation { get; }

    /// <summary>The configuration.</summary>
    public LintConfiguration Configuration { get; }

    internal IReadOnlyList<Diagnostic> Diagnostics => _diagnostics;

    /// <summary>Reports a diagnostic.</summary>
    public void Report(DiagnosticDescriptor descriptor, SourceLocation location, params object?[] args) =>
        _diagnostics.Add(Diagnostic.Create(descriptor, location, args));

    /// <summary>Reports a diagnostic with a suggested fix.</summary>
    public void Report(Diagnostic diagnostic) => _diagnostics.Add(diagnostic);

    /// <summary>
    /// Names referenced inside a module's assignments (an over-approximation computed from tokens), excluding
    /// the names being defined by each assignment.
    /// </summary>
    public IReadOnlySet<string> ReferencedNames(ModuleSymbol module)
    {
        _references ??= ComputeReferences();
        return _references.TryGetValue(module, out var set) ? set : new HashSet<string>();
    }

    private Dictionary<ModuleSymbol, HashSet<string>> ComputeReferences()
    {
        var result = new Dictionary<ModuleSymbol, HashSet<string>>(ReferenceEqualityComparer.Instance);
        foreach (var tree in Compilation.SyntaxTrees)
        {
            var tokens = tree.Lex.Tokens;
            foreach (var m in tree.Modules)
            {
                var module = Compilation.GetModule(m.Name.Text);
                if (module is null || !ReferenceEquals(module.Syntax, m))
                {
                    continue;
                }

                var set = new HashSet<string>(StringComparer.Ordinal);
                var assignments = m.Assignments.OrderBy(a => a.Span.Start).ToList();
                var ai = 0;
                foreach (var t in tokens)
                {
                    if (t.Span.Start < m.Span.Start || t.Span.End > m.Span.End)
                    {
                        continue;
                    }

                    while (ai < assignments.Count && t.Span.Start >= assignments[ai].Span.End)
                    {
                        ai++;
                    }

                    if (ai >= assignments.Count)
                    {
                        break;
                    }

                    var a = assignments[ai];
                    if (t.Span.Start >= a.Span.Start && t.IsName && t.Span.Start != a.Name.Span.Start)
                    {
                        set.Add(t.Text);
                    }
                }

                // EXPORTS of the module itself do not count as uses, but references from other modules do.
                result[module] = set;
            }
        }

        foreach (var module in Compilation.Modules)
        {
            foreach (var other in Compilation.Modules.Where(o => !ReferenceEquals(o, module)))
            {
                foreach (var imp in other.Imports.Where(i => i.FromModule == module.Name))
                {
                    if (result.TryGetValue(module, out var s))
                    {
                        s.Add(imp.Name);
                    }
                }
            }
        }

        return result;
    }
}
