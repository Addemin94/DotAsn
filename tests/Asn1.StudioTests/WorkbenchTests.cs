using System.Text;
using Asn1.Diagnostics;
using Asn1.Studio.Engine;
using Xunit;

namespace Asn1.StudioTests;

public class WorkbenchTests
{
    private const string Heartbeat = "Telemetry-Protocol.Heartbeat";
    private const string HeartbeatValue = """{"sequenceNumber": 7, "uptimeSeconds": 4000000000}""";

    private static Workbench Telemetry()
    {
        var bench = new Workbench();
        var result = bench.Compile(Samples.All.Single(s => s.Name == "telemetry/telemetry.asn").Text);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        return bench;
    }

    [Fact]
    public void SamplesAreEmbedded()
    {
        var expected = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "../../../../../samples"), "*.asn", SearchOption.AllDirectories).Length;
        Assert.Equal(expected, Samples.All.Count);
        Assert.All(Samples.All, s => Assert.True(new Workbench().Compile(s.Text).Success, s.Name));
    }

    [Fact]
    public void CompileListsTypesAndDefinitions()
    {
        var bench = Telemetry();
        Assert.Contains(Heartbeat, bench.Types);
        Assert.Contains("uptimeSeconds   INTEGER (0..4294967295)", bench.Definition(Heartbeat), StringComparison.Ordinal);
        Assert.Contains("Components:", bench.Describe(Heartbeat), StringComparison.Ordinal);
        Assert.Contains("Heartbeat", bench.Model(), StringComparison.Ordinal);
    }

    [Fact]
    public void ErrorsHaveLocationsAndKeepThePreviousSchema()
    {
        var bench = Telemetry();
        var result = bench.Compile("M DEFINITIONS ::= BEGIN\n  A ::= SEQUENCE { a INTEGER, }\nEND");
        Assert.False(result.Success);
        var error = result.Diagnostics.First(d => d.Severity == DiagnosticSeverity.Error);
        Assert.Equal(2, error.Line);
        Assert.Contains(Heartbeat, bench.Types);
    }

    [Fact]
    public void LintingUsesTheTargetEncodingRules()
    {
        var bench = new Workbench();
        // ASN5011 (unconstrained INTEGER) is a PER interoperability rule: reported only when PER is targeted.
        var text = "M DEFINITIONS AUTOMATIC TAGS ::= BEGIN\n  A ::= SEQUENCE { n INTEGER }\nEND";
        Assert.Contains(bench.Compile(text, lint: true, ["uper"]).Diagnostics, d => d.Id == "ASN5011");
        Assert.DoesNotContain(bench.Compile(text, lint: true, ["der"]).Diagnostics, d => d.Id == "ASN5011");
        Assert.DoesNotContain(bench.Compile(text, lint: false, ["uper"]).Diagnostics, d => d.Id == "ASN5011");
    }

    [Theory]
    [InlineData("UPER", "07EE6B2800", 40L)] // X.691: 8 + 32 bits
    [InlineData("APER", "07C0EE6B2800", 48L)] // X.691 11.5.7.4: range > 64K, 2-bit length "11", padding, 4 octets
    [InlineData("DER", "300A800107810500EE6B2800", null)] // X.690: [0] 7, [1] 00 EE 6B 28 00
    [InlineData("OER", "07EE6B2800", null)] // X.696 10.3: 1-octet and 4-octet unsigned
    public void EncodesKnownAnswers(string rule, string hex, long? bits)
    {
        var output = Telemetry().Encode(Heartbeat, HeartbeatValue, rule);
        Assert.Null(output.Error);
        Assert.Equal(hex, Convert.ToHexString(output.Bytes!));
        Assert.Equal(bits, output.BitLength);
    }

    [Fact]
    public void TextRulesExposeTheText()
    {
        var bench = Telemetry();
        Assert.Equal("""{"sequenceNumber":7,"uptimeSeconds":4000000000}""", bench.Encode(Heartbeat, HeartbeatValue, "JER").Text);
        Assert.Equal("<Heartbeat><sequenceNumber>7</sequenceNumber><uptimeSeconds>4000000000</uptimeSeconds></Heartbeat>", bench.Encode(Heartbeat, HeartbeatValue, "CXER").Text);
    }

    [Fact]
    public void ExampleValuesOfEveryTypeRoundTripThroughEveryRule()
    {
        var bench = Telemetry();
        foreach (var type in bench.Types)
        {
            for (var seed = 1; seed <= 5; seed++)
            {
                var json = bench.Example(type, seed);
                Assert.Equal(json, bench.Example(type, seed));
                Assert.Empty(bench.Validate(type, json));
                foreach (var encoded in bench.EncodeAll(type, json))
                {
                    Assert.True(encoded.Error is null, $"{type} {encoded.Rule}: {encoded.Error}");
                    var decoded = bench.Decode(type, encoded.Rule, encoded.Bytes!);
                    Assert.True(decoded.Error is null, $"{type} {encoded.Rule}: {decoded.Error}");
                    Assert.Equal(encoded.Bytes!.Length, decoded.BytesConsumed);

                    // Decoders fill in DEFAULT values, so compare re-encodings rather than JSON text.
                    Assert.Equal(Convert.ToHexString(encoded.Bytes), Convert.ToHexString(bench.Encode(type, decoded.Json!, encoded.Rule).Bytes!));
                }
            }
        }
    }

    [Fact]
    public void ProblemsAreReportedNotThrown()
    {
        var bench = Telemetry();
        Assert.NotEmpty(bench.Validate(Heartbeat, """{"sequenceNumber": 300, "uptimeSeconds": 1}"""));
        Assert.NotNull(bench.Encode(Heartbeat, """{"sequenceNumber": 300, "uptimeSeconds": 1}""", "UPER").Error);
        Assert.NotNull(bench.Encode(Heartbeat, "{not json", "UPER").Error);
        Assert.NotNull(bench.Decode(Heartbeat, "UPER", [0x07]).Error);
        Assert.NotNull(bench.Decode(Heartbeat, "UPER", Convert.FromHexString("07EE6B280000")).Error);

        var trailing = bench.Decode(Heartbeat, "UPER", Convert.FromHexString("07EE6B280000"), allowTrailingData: true);
        Assert.Null(trailing.Error);
        Assert.Equal((5, 6), (trailing.BytesConsumed, trailing.TotalBytes));
    }

    [Fact]
    public void DecodedValuesAreCheckedAgainstConstraints()
    {
        // Measurement.humidity is INTEGER (0..100); BER carries 101 without complaint, the check reports it.
        var bench = Telemetry();
        var ber = bench.Encode("Telemetry-Protocol.Measurement", """{"timestamp": 1, "humidity": 100, "batteryMv": 1}""", "BER").Bytes!;
        var hex = Convert.ToHexString(ber).Replace("820164", "820165", StringComparison.Ordinal);
        var decoded = bench.Decode("Telemetry-Protocol.Measurement", "BER", Convert.FromHexString(hex));
        Assert.Null(decoded.Error);
        Assert.NotEmpty(decoded.Violations);
    }

    [Fact]
    public void TranscodesBetweenRules()
    {
        var bench = Telemetry();
        var xer = bench.Transcode(Heartbeat, "UPER", Convert.FromHexString("07EE6B2800"), "XER");
        Assert.Equal("<Heartbeat><sequenceNumber>7</sequenceNumber><uptimeSeconds>4000000000</uptimeSeconds></Heartbeat>", xer.Text);
        Assert.NotNull(bench.Transcode(Heartbeat, "UPER", [0x07], "XER").Error);
    }

    [Theory]
    [InlineData("07 EE 6B 28 00", InputFormat.Hex)]
    [InlineData("0x07, 0xEE:6b-28\n00", InputFormat.Hex)]
    [InlineData("B+5rKAA=", InputFormat.Base64)]
    public void ParsesEncodedInput(string text, InputFormat format) =>
        Assert.Equal("07EE6B2800", Convert.ToHexString(Workbench.ParseInput(text, format)));

    [Fact]
    public void RejectsMalformedInput()
    {
        Assert.Throws<FormatException>(() => Workbench.ParseInput("07E", InputFormat.Hex));
        Assert.Throws<FormatException>(() => Workbench.ParseInput("07 zz", InputFormat.Hex));
        Assert.Equal("é", Encoding.UTF8.GetString(Workbench.ParseInput("é", InputFormat.Text)));
    }

    [Fact]
    public void FormatsHexSixteenOctetsPerLine() =>
        Assert.Equal("00 01 02 03 04 05 06 07 08 09 0A 0B 0C 0D 0E 0F\n10", Workbench.FormatHex(Enumerable.Range(0, 17).Select(i => (byte)i).ToArray()));

    [Fact]
    public void InspectsTlvsWithoutASchema()
    {
        var dump = Workbench.Inspect(Convert.FromHexString("300A800107810500EE6B2800"));
        Assert.DoesNotContain("error", dump, StringComparison.Ordinal);
        Assert.Contains("EE 6B 28 00", dump.ToUpperInvariant().Replace("EE6B2800", "EE 6B 28 00", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public void GeneratesCSharp()
    {
        var code = Telemetry().GenerateCSharp("Demo");
        Assert.Contains("namespace Demo;", code, StringComparison.Ordinal);
        Assert.Contains("public sealed partial class Heartbeat", code, StringComparison.Ordinal);
    }

    [Fact]
    public void HighlightsWithTheDotAsnLexer()
    {
        const string text = "M DEFINITIONS ::= BEGIN T ::= INTEGER END";
        var triples = Workbench.Highlight(text);
        Assert.Equal(0, triples.Length % 3);
        var spans = Enumerable.Range(0, triples.Length / 3)
            .Select(i => (text.Substring(triples[i * 3], triples[(i * 3) + 1]), (Asn1.Syntax.SyntaxClass)triples[(i * 3) + 2]))
            .ToList();
        Assert.Contains(("DEFINITIONS", Asn1.Syntax.SyntaxClass.Keyword), spans);
        Assert.Contains(("INTEGER", Asn1.Syntax.SyntaxClass.BuiltinType), spans);
        Assert.Contains(("T", Asn1.Syntax.SyntaxClass.TypeReference), spans);
    }

    [Fact]
    public void DiagnosticsCarryTheirTextOffsets()
    {
        const string text = "M DEFINITIONS ::= BEGIN\n  A ::= SEQUENCE { a Missing }\nEND";
        var result = new Workbench().Compile(text);
        var error = Assert.Single(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Equal("Missing", text[error.From..error.To]);
        Assert.Equal(2, error.Line);
    }

    [Fact]
    public void HighlightsGeneratedCSharpWithRoslyn()
    {
        var code = Telemetry().GenerateCSharp("Protocol.Test");
        var triples = CSharpHighlighter.Highlight(code);
        var spans = Enumerable.Range(0, triples.Length / 3)
            .Select(i => (Start: triples[i * 3], Length: triples[(i * 3) + 1], Class: (Asn1.Syntax.SyntaxClass)triples[(i * 3) + 2]))
            .ToList();
        var texts = spans.Select(s => (code.Substring(s.Start, s.Length), s.Class)).ToList();

        Assert.Contains(("namespace", Asn1.Syntax.SyntaxClass.Keyword), texts);
        Assert.Contains(("sealed", Asn1.Syntax.SyntaxClass.Keyword), texts);
        Assert.Contains(("int", Asn1.Syntax.SyntaxClass.BuiltinType), texts);
        Assert.Contains(("Heartbeat", Asn1.Syntax.SyntaxClass.TypeReference), texts);
        Assert.Contains(texts, t => t.Class == Asn1.Syntax.SyntaxClass.Comment && t.Item1.StartsWith("// <auto-generated>", StringComparison.Ordinal));
        Assert.Contains(texts, t => t.Class == Asn1.Syntax.SyntaxClass.Comment && t.Item1.StartsWith("/// <summary>", StringComparison.Ordinal));
        Assert.Contains(texts, t => t.Class == Asn1.Syntax.SyntaxClass.String);

        // The editor needs ordered, non-overlapping spans.
        for (var i = 1; i < spans.Count; i++)
        {
            Assert.True(spans[i].Start >= spans[i - 1].Start + spans[i - 1].Length, $"span {i} at {spans[i].Start} overlaps its predecessor");
        }
    }
}
