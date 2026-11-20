using System.Text.Json;
using System.Text.RegularExpressions;
using Asn1.CodeGen.Pawn;
using Asn1.Diagnostics;
using Asn1.Semantics;
using Xunit;

namespace Asn1.CodeGenTests;

/// <summary>
/// Tests of the Pawn generator's output. The generated encoders and decoders are compiled with pawncc and run against
/// the OER vectors by <c>scripts/pawn_vectors.py</c>, which needs a Pawn 4 toolchain.
/// </summary>
public partial class PawnGeneratorTests
{
    private static readonly string[] s_specs =
    [
        "samples/telemetry/telemetry.asn",
        "samples/basic/basic.asn",
        "samples/recursive-types/tree.asn",
        "samples/constraints/constraints.asn",
        "samples/extensibility/extensibility.asn",
        "samples/protocol-messages/sensor-protocol.asn",
        "test-vectors/corpus/asn1tools/all_types.asn",
        "test-vectors/corpus/asn1tools/zforce.asn",
        "test-vectors/corpus/dotasn/primitives.asn",
    ];

    public static TheoryData<string> Specs() => [.. s_specs];

    private static PawnGenerationResult Generate(string asn) =>
        PawnGenerator.Generate(Asn1Compilation.Create([new SourceText("test.asn", asn)]));

    private static string Module(PawnGenerationResult result) =>
        Assert.Single(result.Files, f => f.Path is not (PawnGenerator.CoreFileName or PawnGenerator.ManifestFileName)).Content;

    [GeneratedRegex(@"^const (\w+) =|^stock bool:(\w+)\(", RegexOptions.Multiline)]
    private static partial Regex Symbols();

    [Theory]
    [MemberData(nameof(Specs))]
    public void GeneratesEveryModuleDeterministicallyWithinPawnNameLimits(string spec)
    {
        var compilation = Asn1Compilation.Create([new SourceText(spec, File.ReadAllText(Path.Combine(GeneratedAssembly.Root, spec)))]);
        var first = PawnGenerator.Generate(compilation);
        var second = PawnGenerator.Generate(compilation);

        Assert.Equal(first.Files.Select(f => f.Content), second.Files.Select(f => f.Content));
        Assert.Single(first.Files, f => f.Path == PawnGenerator.CoreFileName);
        Assert.Single(first.Files, f => f.Path == PawnGenerator.ManifestFileName);
        Assert.Equal(compilation.Modules.Length + 2, first.Files.Length);
        Assert.All(first.Files, f => Assert.DoesNotContain("\r", f.Content, StringComparison.Ordinal));

        var symbols = first.Files.Where(f => f.Path.EndsWith(".inc", StringComparison.Ordinal) && f.Path != PawnGenerator.CoreFileName)
            .SelectMany(f => Symbols().Matches(f.Content))
            .Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value)
            .ToList();
        Assert.All(symbols, s => Assert.True(s.Length <= 31, $"'{s}' is longer than Pawn's 31 characters"));
        Assert.Equal(symbols.Count, symbols.Distinct(StringComparer.Ordinal).Count());

        using var manifest = JsonDocument.Parse(first.Files.Single(f => f.Path == PawnGenerator.ManifestFileName).Content);
        Assert.Equal("COER", manifest.RootElement.GetProperty("encoding").GetString());
    }

    [Fact]
    public void LaysOutASequenceWithPresenceCellsAndBoundedContent()
    {
        var module = Module(Generate("""
            M DEFINITIONS AUTOMATIC TAGS ::= BEGIN
            Report ::= SEQUENCE {
                id      INTEGER (0..255),
                label   IA5String (SIZE (0..10)) OPTIONAL,
                samples SEQUENCE (SIZE (1..4)) OF INTEGER (-100..100)
            }
            END
            """));

        Assert.Contains("const Report_CELLS = 11;", module, StringComparison.Ordinal);
        Assert.Contains("const Report_id = 0;", module, StringComparison.Ordinal);
        Assert.Contains("const Report_label_PRESENT = 1;", module, StringComparison.Ordinal);
        Assert.Contains("const Report_label_LENGTH = 2;", module, StringComparison.Ordinal);
        Assert.Contains("const Report_label = 3;", module, StringComparison.Ordinal);
        Assert.Contains("const Report_label_MAX = 10;", module, StringComparison.Ordinal);
        Assert.Contains("const Report_samples_COUNT = 6;", module, StringComparison.Ordinal);
        Assert.Contains("const Report_samples = 7;", module, StringComparison.Ordinal);
        Assert.Contains("stock bool:Report_encode(const value[], buffer{}, &position, capacity, base = 0)", module, StringComparison.Ordinal);
        Assert.Contains("stock bool:Report_decode(value[], const buffer{}, &position, length, base = 0)", module, StringComparison.Ordinal);
    }

    [Fact]
    public void ChoosesTheOerIntegerFormFromTheRange()
    {
        var module = Module(Generate("""
            M DEFINITIONS AUTOMATIC TAGS ::= BEGIN
            Byte ::= INTEGER (0..255)
            Word ::= INTEGER (0..4294967295)
            Small ::= INTEGER (-1000..10000)
            Open ::= INTEGER
            END
            """));

        Assert.Contains("oer_put_unsigned_in(buffer, position, capacity, value[base], 1, 0, 255)", module, StringComparison.Ordinal);
        Assert.Contains("oer_put_uint(buffer, position, capacity, value[base], 4)", module, StringComparison.Ordinal);
        Assert.Contains("oer_put_signed_in(buffer, position, capacity, value[base], 2, -1000, 10000)", module, StringComparison.Ordinal);
        Assert.Contains("oer_put_varint(buffer, position, capacity, value[base])", module, StringComparison.Ordinal);
    }

    [Fact]
    public void LeavesOutADefaultValueAndRejectsItWhenDecoding()
    {
        var module = Module(Generate("""
            M DEFINITIONS AUTOMATIC TAGS ::= BEGIN
            Config ::= SEQUENCE { interval INTEGER (10..86400) DEFAULT 900 }
            END
            """));

        Assert.Contains("(value[base] != 0) && (value[base + 1] != 900)", module, StringComparison.Ordinal);
        Assert.Contains("if (value[base + 1] == 900) { return false; }", module, StringComparison.Ordinal);
        Assert.Contains("value[base + 1] = 900;", module, StringComparison.Ordinal);
    }

    [Fact]
    public void ReportsTypesOutsideThePawnSubset()
    {
        var result = Generate("""
            M DEFINITIONS AUTOMATIC TAGS ::= BEGIN
            Blob ::= OCTET STRING
            Huge ::= INTEGER (0..18446744073709551615)
            Tree ::= SEQUENCE { children SEQUENCE (SIZE (0..2)) OF Tree }
            Uses ::= SEQUENCE { blob Blob }
            Fine ::= BOOLEAN
            END
            """);

        var unsupported = result.Diagnostics.Where(d => d.Descriptor == DiagnosticCatalog.GenerationUnsupported).Select(d => d.Message).ToList();
        Assert.Contains(unsupported, m => m.Contains("'M.Blob'", StringComparison.Ordinal) && m.Contains("upper bound", StringComparison.Ordinal));
        Assert.Contains(unsupported, m => m.Contains("'M.Huge'", StringComparison.Ordinal) && m.Contains("32-bit cell", StringComparison.Ordinal));
        Assert.Contains(unsupported, m => m.Contains("'M.Tree'", StringComparison.Ordinal) && m.Contains("recursive", StringComparison.Ordinal));
        Assert.Contains(unsupported, m => m.Contains("'M.Uses'", StringComparison.Ordinal) && m.Contains("not generated", StringComparison.Ordinal));
        Assert.Contains("stock bool:Fine_encode(", Module(result), StringComparison.Ordinal);
    }

    [Fact]
    public void ShortensSymbolsThatPawnWouldTruncate()
    {
        var result = Generate("""
            M DEFINITIONS AUTOMATIC TAGS ::= BEGIN
            NetworkInterfaceConfigurationValue ::= SEQUENCE {
                interfaceNumber        INTEGER (1..65535),
                maximumTransferOctets  INTEGER (0..4294967295)
            }
            END
            """);

        var module = Module(result);
        Assert.Contains("stock bool:NICV_encode(", module, StringComparison.Ordinal);
        Assert.Contains(result.Diagnostics, d => d.Descriptor == DiagnosticCatalog.IdentifierRenamed);
        Assert.All(Symbols().Matches(module), m => Assert.True(m.Value.Length - "const  =".Length <= 31 || m.Value.StartsWith("stock", StringComparison.Ordinal)));
    }

    [Fact]
    public void ReferencesAGeneratedTypeByCallingItsFunctions()
    {
        var module = Module(Generate("""
            M DEFINITIONS AUTOMATIC TAGS ::= BEGIN
            Position ::= SEQUENCE { latitude INTEGER (-900000000..900000000), longitude INTEGER (-1800000000..1800000000) }
            Fix ::= SEQUENCE { time INTEGER (0..4294967295), position Position }
            END
            """));

        Assert.Contains("if (!Position_encode(value, buffer, position, capacity, base + 1)) { return false; }", module, StringComparison.Ordinal);
        Assert.Contains("if (!Position_decode(value, buffer, position, length, base + 1)) { return false; }", module, StringComparison.Ordinal);
    }

    [Fact]
    public void ShipsTheOerCore()
    {
        var core = PawnGenerator.Core();
        Assert.Contains("#define _dotasn_oer_included", core, StringComparison.Ordinal);
        Assert.Contains("stock bool:oer_put_length(", core, StringComparison.Ordinal);
        Assert.Contains("stock bool:oer_skip_extensions(", core, StringComparison.Ordinal);
    }
}
