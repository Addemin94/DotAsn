using System.Text;
using Asn1.Diagnostics;
using Xunit;

namespace Asn1.UnitTests;

/// <summary>Keeps generated documentation in sync with the code (set UPDATE_DOCS=1 to regenerate).</summary>
public class DocumentationTests
{
    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Asn1Toolchain.sln")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("repository root not found");
    }

    public static string DiagnosticsMarkdown()
    {
        var sb = new StringBuilder();
        sb.Append("# Diagnostics\n\n");
        sb.Append("Every diagnostic has a stable identifier. Ranges: `ASN0xxx` driver/internal, `ASN1xxx` syntax, `ASN2xxx` semantics, ");
        sb.Append("`ASN3xxx` constraints, `ASN4xxx` unsupported features, `ASN5xxx` lint style/maintainability, `ASN6xxx` encoding-rule ");
        sb.Append("interoperability, `ASN7xxx` code generation. This file is generated from `DiagnosticCatalog` by `DocumentationTests`.\n\n");
        sb.Append("| Id | Category | Default severity | Title | Message |\n|---|---|---|---|---|\n");
        foreach (var d in DiagnosticCatalog.All)
        {
            sb.Append($"| {d.Id} | {d.Category} | {d.DefaultSeverity} | {d.Title} | {d.MessageFormat.Replace("|", "\\|", StringComparison.Ordinal)} |\n");
        }

        return sb.ToString();
    }

    [Fact]
    public void DiagnosticsDocumentIsUpToDate()
    {
        var path = Path.Combine(RepositoryRoot(), "docs", "diagnostics.md");
        var expected = DiagnosticsMarkdown();
        if (Environment.GetEnvironmentVariable("UPDATE_DOCS") == "1")
        {
            File.WriteAllText(path, expected);
        }

        Assert.True(File.Exists(path) && File.ReadAllText(path) == expected, "docs/diagnostics.md is stale; run the tests with UPDATE_DOCS=1");
    }
}
