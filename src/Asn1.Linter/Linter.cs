using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Asn1.Diagnostics;
using Asn1.Semantics;
using Asn1.Syntax;

namespace Asn1.Linter;

/// <summary>Runs lint rules and applies configuration and inline suppressions.</summary>
/// <remarks>
/// Suppressions are ASN.1 comments: <c>-- asn1-lint-disable ASN5001,ASN5002</c> (rest of the line or, alone on a line,
/// the next line), <c>-- asn1-lint-disable-next-line ASN5001</c>, and <c>-- asn1-lint-disable-file ASN5001</c>.
/// Without ids, all non-error diagnostics are suppressed. Errors (standards violations) cannot be suppressed.
/// </remarks>
public static partial class Asn1Linter
{
    [GeneratedRegex(@"^asn1-lint-disable(?<kind>-next-line|-file)?(?:\s+(?<ids>[A-Za-z0-9, ]+))?\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex SuppressionRegex();

    /// <summary>Lints a compilation. The result contains the compilation diagnostics followed by lint diagnostics, after overrides and suppressions.</summary>
    public static ImmutableArray<Diagnostic> Lint(Asn1Compilation compilation, LintConfiguration? configuration = null, IEnumerable<LintRule>? rules = null)
    {
        configuration ??= LintConfiguration.Default;
        var context = new LintContext(compilation, configuration);
        foreach (var rule in rules ?? BuiltInRules.All)
        {
            try
            {
                rule.Analyze(context);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                context.Report(DiagnosticCatalog.InternalError, SourceLocation.None, $"lint rule {rule.GetType().Name} failed: {ex.Message}");
            }
        }

        var suppressions = Suppressions(compilation);
        var output = new List<Diagnostic>();
        foreach (var d in compilation.Diagnostics.Concat(context.Diagnostics))
        {
            var severity = configuration.SeverityOf(d.Id) ?? d.Severity;
            if (d.Severity == DiagnosticSeverity.Error && d.Category != DiagnosticCategory.Style)
            {
                // Standards violations keep their severity.
                severity = DiagnosticSeverity.Error;
            }

            if (severity == DiagnosticSeverity.Hidden || (severity != DiagnosticSeverity.Error && IsSuppressed(d, suppressions)))
            {
                continue;
            }

            output.Add(severity == d.Severity ? d : d.WithSeverity(severity));
        }

        return [.. DiagnosticOrdering.Sort(output)];
    }

    private sealed record Suppression(string Path, int Line, bool WholeFile, IReadOnlySet<string>? Ids);

    private static List<Suppression> Suppressions(Asn1Compilation compilation)
    {
        var list = new List<Suppression>();
        foreach (var tree in compilation.SyntaxTrees)
        {
            var tokenLines = tree.Lex.Tokens.Select(t => t.Line).ToHashSet();
            foreach (var c in tree.Lex.Comments)
            {
                var m = SuppressionRegex().Match(c.Text);
                if (!m.Success)
                {
                    continue;
                }

                var ids = m.Groups["ids"].Success ? m.Groups["ids"].Value.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase) : null;
                var kind = m.Groups["kind"].Value;
                var line = kind switch
                {
                    "-next-line" => c.Line + 1,
                    "-file" => -1,
                    _ => c.IsTrailing || tokenLines.Contains(c.Line) ? c.Line : c.Line + 1,
                };
                list.Add(new Suppression(tree.Source.Path, line + 1, kind == "-file", ids));
            }
        }

        return list;
    }

    private static bool IsSuppressed(Diagnostic d, List<Suppression> suppressions) =>
        !d.Location.IsNone && suppressions.Any(s => s.Path == d.Location.Path && (s.WholeFile || s.Line == d.Location.Start.Line) && (s.Ids is null || s.Ids.Contains(d.Id)));
}
