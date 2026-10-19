using Asn1.CodeGen.CSharp;
using Asn1.Semantics;
using Xunit;

namespace Asn1.CodeGenTests;

public class GeneratorTests
{
    private static readonly string[] s_specs =
    [
        "samples/telemetry/telemetry.asn",
        "samples/basic/basic.asn",
        "samples/recursive-types/tree.asn",
        "samples/constraints/constraints.asn",
        "samples/extensibility/extensibility.asn",
        "samples/protocol-messages/sensor-protocol.asn",
        "test-vectors/corpus/asn1tools/information_object.asn",
        "test-vectors/corpus/asn1tools/parameterization.asn",
        "test-vectors/corpus/asn1tools/x683.asn",
        "test-vectors/corpus/asn1tools/zforce.asn",
        "test-vectors/corpus/asn1tools/time_types.asn",
        "test-vectors/corpus/ietf/rfc5280.asn",
    ];

    public static TheoryData<string> Specs() => [.. s_specs];

    [Theory]
    [MemberData(nameof(Specs))]
    public void GeneratedCodeCompilesWithoutWarnings(string spec)
    {
        var generated = GeneratedAssembly.For([spec]);
        Assert.NotEmpty(generated.Assembly.GetTypes());
    }

    [Fact]
    public void GenerationIsDeterministic()
    {
        var text = File.ReadAllText(Path.Combine(GeneratedAssembly.Root, "samples/telemetry/telemetry.asn"));
        var a = CSharpGenerator.Generate(Asn1Compilation.FromText(text, "a.asn"));
        var b = CSharpGenerator.Generate(Asn1Compilation.FromText(text, "a.asn"));
        Assert.Equal(a.Files.Select(f => f.Content), b.Files.Select(f => f.Content));
        Assert.Equal(a.Manifest, b.Manifest);
    }

    [Fact]
    public void ReferencedTypesAreOwnedByTheirAssignment()
    {
        // Regression: a component referencing a later assignment ("b C" before "C ::= CHOICE") used to turn C into
        // a nested class S.BType, and aliases reused the referenced descriptor's name (visible as XER element names).
        var text = """
            M DEFINITIONS AUTOMATIC TAGS ::= BEGIN
            S ::= SEQUENCE { b C }
            C ::= CHOICE { a INTEGER }
            D ::= C
            END
            """;
        var source = string.Concat(CSharpGenerator.Generate(Asn1Compilation.FromText(text), new CSharpGeneratorOptions { Namespace = "Test" }).Files.Select(f => f.Content));
        Assert.Contains("public abstract partial class C ", source, StringComparison.Ordinal);
        Assert.DoesNotContain("BType", source, StringComparison.Ordinal);
        Assert.Contains(".WithConstraints([], \"M.D\")", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CSharpKeywordsAndCollisionsAreHandled()
    {
        var text = """
            M DEFINITIONS AUTOMATIC TAGS ::= BEGIN
            Class ::= SEQUENCE { class INTEGER, equals BOOLEAN, asn1Type NULL, class-x INTEGER, classX BOOLEAN }
            Choice ::= CHOICE { kind INTEGER, choice BOOLEAN, alternativeKind NULL, ... }
            Nested ::= SEQUENCE { inner SEQUENCE { a INTEGER }, innerType BOOLEAN, list SEQUENCE OF SEQUENCE { b INTEGER } }
            END
            """;
        var compilation = Asn1Compilation.FromText(text);
        var result = CSharpGenerator.Generate(compilation, new CSharpGeneratorOptions { Namespace = "Test" });
        var path = Path.Combine(Path.GetTempPath(), "dotasn-" + Guid.NewGuid().ToString("N") + ".asn");
        Assert.Contains(result.Diagnostics, d => d.Id == "ASN7001");
        File.WriteAllText(path, text);
        try
        {
            var assembly = GeneratedAssembly.For([path]);
            Assert.NotNull(assembly.Assembly.GetType("M.Class"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void UsageFromHandWrittenCode()
    {
        const string usage = """
            using Asn1.Runtime;
            using Asn1.Runtime.Ber;
            using TelemetryProtocol;

            public static class Usage
            {
                public static string Run()
                {
                    var heartbeat = new Heartbeat { SequenceNumber = 7, UptimeSeconds = 4_000_000_000 };
                    var der = Asn1Serializer.Encode(heartbeat, DerEncodingRule.Instance);
                    var back = Asn1Serializer.Decode<Heartbeat>(der, DerEncodingRule.Instance);
                    if (!back.Equals(heartbeat)) throw new System.Exception("round trip");

                    // The README example: a CHOICE alternative is a nested class of the CHOICE type.
                    var message = new Message { MessageType = MessageType.Heartbeat, Payload = new Payload.Heartbeat(heartbeat) };
                    if (Asn1Serializer.Validate(message).Count != 0) throw new System.Exception("validation");
                    if (!Asn1Serializer.Decode<Message>(Asn1Serializer.Encode(message, DerEncodingRule.Instance), DerEncodingRule.Instance).Equals(message)) throw new System.Exception("message");

                    var query = new ConfigurationQuery { RequestId = 1, Keys = ["ab"] };
                    var bytes = Asn1Serializer.Encode(query, DerEncodingRule.Instance);
                    var enumBytes = Asn1Serializer.Encode(MessageType.TimeSyncRequest, MessageType.Asn1Type, DerEncodingRule.Instance);
                    return System.Convert.ToHexString(der) + "|" + System.Convert.ToHexString(bytes) + "|" + System.Convert.ToHexString(enumBytes) + "|" + back;
                }
            }
            """;
        var generated = GeneratedAssembly.For(["samples/telemetry/telemetry.asn"], extraSource: usage);
        var result = (string)generated.Assembly.GetType("Usage")!.GetMethod("Run")!.Invoke(null, null)!;
        var parts = result.Split('|');
        Assert.Equal("300A800107810500EE6B2800", parts[0]);
        Assert.Equal("3009800101A1041A026162", parts[1]);
        Assert.Equal("0A0102", parts[2]);
        Assert.Equal("{ sequenceNumber 7, uptimeSeconds 4000000000 }", parts[3]);
    }
}
