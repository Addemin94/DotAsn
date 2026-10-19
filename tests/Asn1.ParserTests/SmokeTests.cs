using Asn1.Diagnostics;
using Asn1.Syntax;
using Xunit;

namespace Asn1.ParserTests;

public class SmokeTests
{
    [Theory]
    [InlineData("samples/telemetry/telemetry.asn")]
    public void SamplesParseWithoutErrors(string path)
    {
        var bag = new DiagnosticBag();
        var unit = Parser.ParseCompilationUnit(new SourceText(path, File.ReadAllText(Path.Combine(AppContext.BaseDirectory, path))), bag);
        Assert.Empty(bag.ToImmutable().Select(d => d.ToString()));
        Assert.Single(unit.Modules);
        Assert.NotEmpty(unit.Modules[0].Assignments);
    }
}
