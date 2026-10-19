using Asn1.Diagnostics;
using Asn1.Semantics;
using Xunit;

namespace Asn1.SemanticTests;

internal static class TestHelpers
{
    public static Asn1Compilation Compile(string text, params string[] more)
    {
        var sources = new List<SourceText> { new("test.asn", text) };
        for (var i = 0; i < more.Length; i++)
        {
            sources.Add(new SourceText($"test{i + 2}.asn", more[i]));
        }

        return Asn1Compilation.Create(sources);
    }

    public static Asn1Compilation CompileClean(string text, params string[] more)
    {
        var c = Compile(text, more);
        var errors = c.Diagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning).Select(d => d.ToString()).ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors));
        return c;
    }

    public static string Module(string body, string tags = "AUTOMATIC TAGS", string name = "M") =>
        $"{name} DEFINITIONS {tags} ::= BEGIN\n{body}\nEND\n";

    public static AsnType Type(this Asn1Compilation c, string name) => c.FindType(name)?.Type ?? throw new InvalidOperationException($"type {name} not found");

    public static AsnComponent Component(this AsnType t, string name) => t.Components.Single(c => c.Name == name);

    public static void AssertDiagnostic(this Asn1Compilation c, string id, int? line = null)
    {
        Assert.Contains(c.Diagnostics, d => d.Id == id && (line is null || d.Location.Start.Line == line));
    }

    public static string Tags(this AsnType t) => string.Join(" ", t.Tags.Select(x => x.ToString()));
}
