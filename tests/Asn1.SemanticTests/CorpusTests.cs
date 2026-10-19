using Asn1.Diagnostics;
using Asn1.Semantics;
using Xunit;

namespace Asn1.SemanticTests;

/// <summary>Compiles the sample protocols and the third-party corpus and pins the expected diagnostics.</summary>
public class CorpusTests
{
    private static Asn1Compilation CompileFiles(params string[] paths) =>
        Asn1Compilation.Create(paths.Select(p => new SourceText(p, File.ReadAllText(Path.Combine(AppContext.BaseDirectory, p)))));

    [Theory]
    [InlineData("samples/telemetry/telemetry.asn")]
    [InlineData("samples/basic/basic.asn")]
    [InlineData("samples/recursive-types/tree.asn")]
    [InlineData("samples/constraints/constraints.asn")]
    [InlineData("samples/extensibility/extensibility.asn")]
    [InlineData("samples/protocol-messages/sensor-protocol.asn")]
    [InlineData("corpus/asn1tools/x691_a1.asn")]
    [InlineData("corpus/asn1tools/x691_a2.asn")]
    [InlineData("corpus/asn1tools/x691_a3.asn")]
    [InlineData("corpus/asn1tools/x691_a4.asn")]
    [InlineData("corpus/asn1tools/module_tags_automatic.asn")]
    [InlineData("corpus/asn1tools/module_tags_explicit.asn")]
    [InlineData("corpus/asn1tools/module_tags_implicit.asn")]
    [InlineData("corpus/asn1tools/parameterization.asn")]
    [InlineData("corpus/asn1tools/information_object.asn")]
    [InlineData("corpus/asn1tools/enumerated.asn")]
    [InlineData("corpus/asn1tools/named_numbers.asn")]
    [InlineData("corpus/asn1tools/time_types.asn")]
    [InlineData("corpus/asn1tools/with_components.asn")]
    [InlineData("corpus/asn1tools/zforce.asn")]
    [InlineData("corpus/asn1tools/x683.asn")]
    [InlineData("corpus/ietf/rfc5280.asn")]
    [InlineData("corpus/ietf/rfc5084.asn")]
    public void CompilesWithoutErrors(string path)
    {
        var c = CompileFiles(path);
        var errors = c.Diagnostics.Where(d => d.IsError).Select(d => d.ToString()).ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors));
    }

    [Fact]
    public void AllTypesReportsOnlyGenuineStandardsViolations()
    {
        // asn1tools' all_types.asn deliberately contains notation that X.680 forbids: SIZE on INTEGER, extension
        // additions whose tags collide with root components in an EXPLICIT TAGS module, an undefined type 'A',
        // a Quadruple beyond U+10FFFF, and OPTIONAL presence constraints on mandatory components.
        var c = CompileFiles("corpus/asn1tools/all_types.asn");
        var ids = c.Diagnostics.Where(d => d.IsError).Select(d => d.Id).Distinct().Order().ToList();
        Assert.Equal(["ASN2001", "ASN2010", "ASN2014", "ASN3001", "ASN3008"], ids);
    }
}
