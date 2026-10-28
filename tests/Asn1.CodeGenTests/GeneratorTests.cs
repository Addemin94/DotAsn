using Asn1.CodeGen.CSharp;
using Asn1.Diagnostics;
using Asn1.Semantics;
using Xunit;

namespace Asn1.CodeGenTests;

public class GeneratorTests
{
    private static readonly string[] s_specs =
    [
        "samples/telemetry/telemetry.asn",
        "tests/Asn1.CodeGenTests/Specs/integers.asn",
        "tests/Asn1.CodeGenTests/Specs/integer-edge-cases.asn",
        "tests/Asn1.CodeGenTests/Specs/integer-constants.asn",
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

    [Fact]
    public void IntegersUseTheSmallestExactClrType()
    {
        // Every limit of every width must round-trip through the generated property, for PER (constrained whole
        // numbers), OER (fixed-size integers) and DER.
        const string usage = """
            using Asn1.Runtime;
            using Asn1.Runtime.Ber;
            using Asn1.Runtime.Oer;
            using Asn1.Runtime.Per;
            using Integers;

            public static class Usage
            {
                public static string Run()
                {
                    var values = new[]
                    {
                        new Ranges { U8 = byte.MaxValue, U16 = ushort.MaxValue, U32 = uint.MaxValue, U64 = ulong.MaxValue, S8 = sbyte.MinValue, S16 = short.MinValue, S32 = int.MinValue, S64 = long.MinValue, Mixed = -1 },
                        new Ranges { S8 = sbyte.MaxValue, S16 = short.MaxValue, S32 = int.MaxValue, S64 = long.MaxValue, Mixed = 255, Level = 7, Offset = -100, Port = IntegersAsn1.DefaultPort },
                    };
                    foreach (var v in values)
                    {
                        foreach (var rule in new Asn1EncodingRule[] { PerEncodingRule.Unaligned, PerEncodingRule.Aligned, OerEncodingRule.Canonical, DerEncodingRule.Instance })
                        {
                            if (!Asn1Serializer.Decode<Ranges>(Asn1Serializer.Encode(v, rule), rule).Equals(v)) throw new System.Exception(rule.Name);
                        }
                    }

                    return "ok";
                }
            }
            """;
        var generated = GeneratedAssembly.For(["tests/Asn1.CodeGenTests/Specs/integers.asn"], extraSource: usage);
        var ranges = generated.Assembly.GetType("Integers.Ranges")!;
        Type Property(string name) => ranges.GetProperty(name)!.PropertyType;

        Assert.Equal(typeof(byte), Property("U8"));
        Assert.Equal(typeof(ushort), Property("U16"));
        Assert.Equal(typeof(uint), Property("U32"));
        Assert.Equal(typeof(ulong), Property("U64"));
        Assert.Equal(typeof(sbyte), Property("S8"));
        Assert.Equal(typeof(short), Property("S16"));
        Assert.Equal(typeof(int), Property("S32"));
        Assert.Equal(typeof(long), Property("S64"));
        Assert.Equal(typeof(short), Property("Mixed"));
        Assert.Equal(typeof(System.Numerics.BigInteger), Property("Huge"));
        Assert.Equal(typeof(System.Numerics.BigInteger), Property("Open"));
        Assert.Equal(typeof(System.Numerics.BigInteger), Property("Ext"));
        Assert.Equal(typeof(byte), Property("Level"));
        Assert.Equal(typeof(sbyte), Property("Offset"));
        Assert.Equal(typeof(ushort?), Property("Port"));

        // DEFAULT values, named numbers and value assignments have the type of their INTEGER type.
        var instance = Activator.CreateInstance(ranges)!;
        Assert.Equal((byte)3, ranges.GetProperty("Level")!.GetValue(instance));
        Assert.Equal((sbyte)-5, ranges.GetProperty("Offset")!.GetValue(instance));
        var module = generated.Assembly.GetType("Integers.IntegersAsn1")!;
        Assert.Equal((ushort)8080, module.GetField("DefaultPort")!.GetValue(null));
        Assert.Equal((sbyte)-128, module.GetField("Minimum")!.GetValue(null));
        Assert.Equal((byte)9, generated.Assembly.GetType("Integers.Priority")!.GetField("High")!.GetValue(null));

        Assert.Equal("ok", (string)generated.Assembly.GetType("Usage")!.GetMethod("Run")!.Invoke(null, null)!);
    }

    [Fact]
    public void IntegerValuesOutsideTheirTypeStillCompile()
    {
        // GeneratedAssembly fails on any compiler warning or error in the generated code.
        var generated = GeneratedAssembly.For(["tests/Asn1.CodeGenTests/Specs/integer-edge-cases.asn"]);
        object? Constant(string type, string name) => generated.Assembly.GetType("IntegerEdgeCases." + type)!.GetField(name)!.GetValue(null);

        // Named numbers outside the range get a type that holds them; the others keep the INTEGER type's type.
        Assert.Equal(-1, Constant("A", "Unknown"));
        Assert.Equal((byte)255, Constant("A", "Max"));
        Assert.Equal(-1, Constant("B", "Unknown"));
        Assert.Equal(5_000_000_000L, Constant("B", "Big"));

        // Names that only hide an inherited member are kept (CS0108 is disabled in generated code); the Asn1Type
        // property is a real conflict, so that constant is renamed.
        Assert.Equal((byte)1, Constant("Methods", "ToString"));
        Assert.Equal((byte)2, Constant("Methods", "Equals"));
        Assert.Equal(0, Constant("Flags", "GetType"));
        Assert.Equal(1, Constant("Flags", "Asn1TypeValue"));

        Assert.Equal(3_000_000_000L, Constant("Wide", "Far"));
        Assert.NotNull(generated.Assembly.GetType("IntegerEdgeCases.Members")!.GetProperty("ReferenceEquals"));
        Assert.Null(generated.Assembly.GetType("IntegerEdgeCases.Huge"));

        // A value outside its type keeps its value in a wider type; nested ones cannot be generated. Both are reported.
        var module = generated.Assembly.GetType("IntegerEdgeCases.IntegerEdgeCasesAsn1")!;
        Assert.Equal(70000, module.GetField("P")!.GetValue(null));
        Assert.Equal((ushort)8080, module.GetField("Ok")!.GetValue(null));
        Assert.NotNull(module.GetProperty("Good"));
        Assert.Null(module.GetProperty("Nested"));
        Assert.Null(module.GetProperty("Nested2"));

        Assert.Equal(
            [
                "the value 70000 does not fit ushort, the C# type of Port; 'p' is generated as int",
                "the value 300 does not fit byte, the C# type of INTEGER; 'nested' is not generated",
                "the value -1 does not fit uint, the C# type of INTEGER; 'nested2' is not generated",
            ],
            generated.Generation.Diagnostics.Where(d => d.Descriptor == DiagnosticCatalog.ValueOutsideType).Select(d => d.Message));
        Assert.Equal(
            ["'Integer-Edge-Cases.Huge' cannot be generated: named bit positions must fit in 64 bits"],
            generated.Generation.Diagnostics.Where(d => d.Descriptor == DiagnosticCatalog.GenerationUnsupported).Select(d => d.Message));
    }

    [Theory]
    [InlineData(IntegerMapping.Precise)]
    [InlineData(IntegerMapping.Int64)]
    public void ConstantsOfUnboundedTypesAreTypedByValue(IntegerMapping mapping)
    {
        var generated = GeneratedAssembly.For(["tests/Asn1.CodeGenTests/Specs/integer-constants.asn"], new CSharpGeneratorOptions { IntegerMapping = mapping });
        var module = generated.Assembly.GetType("IntegerConstants.IntegerConstantsAsn1")!;
        object? Constant(Type type, string name) => type.GetField(name)!.GetValue(null);

        // Compile-time constants of the smallest of int, long and ulong, as for every INTEGER without bounds.
        Assert.Equal(150, Constant(module, "MaxAge"));
        Assert.True(module.GetField("MaxAge")!.IsLiteral);
        Assert.Equal(10_000_000_000_000_000_000UL, Constant(module, "Big"));
        Assert.Equal(System.Numerics.BigInteger.Parse("18446744073709551616", System.Globalization.CultureInfo.InvariantCulture), Constant(module, "Huge"));
        Assert.Equal(0, Constant(generated.Assembly.GetType("IntegerConstants.Code")!, "Ok"));
        Assert.Equal(ulong.MaxValue, Constant(generated.Assembly.GetType("IntegerConstants.Top")!, "TopValue"));
        Assert.Contains(generated.Generation.Diagnostics, d => d.Descriptor == DiagnosticCatalog.IdentifierRenamed && d.Message.StartsWith("'top' is generated as 'TopValue'", StringComparison.Ordinal));

        // Under Int64, values beyond long are reported (once each): constants keep a type that holds them, and the DEFAULT
        // is generated as OPTIONAL.
        var reports = generated.Generation.Diagnostics.Where(d => d.Descriptor == DiagnosticCatalog.ValueOutsideType).Select(d => d.Message).ToList();
        var a = generated.Assembly.GetType("IntegerConstants.S")!.GetProperty("A")!.PropertyType;
        if (mapping == IntegerMapping.Int64)
        {
            const string Long = "does not fit long (unbounded and extensible INTEGERs are long with IntegerMapping.Int64), the C# type of INTEGER;";
            Assert.Equal(
                [
                    $"the value 18446744073709551616 {Long} 'huge' is generated as BigInteger",
                    $"the value 10000000000000000000 {Long} 'big' is generated as ulong",
                    $"the value 10000000000000000000 {Long} the DEFAULT of 'a' is generated as OPTIONAL",
                ],
                reports);
            Assert.Equal(typeof(long?), a);
        }
        else
        {
            Assert.Empty(reports);
            Assert.Equal(typeof(System.Numerics.BigInteger), a);
        }
    }

    [Fact]
    public void GeneratedValuesCanBeUsedWithoutTheirStaticType()
    {
        const string usage = """
            using System.Collections.Generic;
            using Asn1.Runtime;
            using Asn1.Runtime.Per;
            using TelemetryProtocol;

            public static class Usage
            {
                // A non-generic method returning values of different generated types.
                private static IAsn1Value Pick(int i, Heartbeat heartbeat) => i switch
                {
                    0 => heartbeat,
                    1 => new Message { MessageType = MessageType.Heartbeat, Payload = new Payload.Heartbeat(heartbeat) },
                    _ => new Payload.Heartbeat(heartbeat),
                };

                public static string Run()
                {
                    var heartbeat = new Heartbeat { SequenceNumber = 7, UptimeSeconds = 4_000_000_000 };
                    var results = new List<string>();
                    for (var i = 0; i < 3; i++)
                    {
                        IAsn1Value value = Pick(i, heartbeat);
                        results.Add(value.Asn1Type.Name + "=" + System.Convert.ToHexString(Asn1Serializer.Encode(value, PerEncodingRule.Unaligned)));
                    }

                    // The generic overloads are still chosen for statically typed values.
                    var typed = Asn1Serializer.Encode(heartbeat, PerEncodingRule.Unaligned);
                    if (System.Convert.ToHexString(typed) != "07EE6B2800") throw new System.Exception("generic");

                    // The buffer-writer and stream overloads produce the same bytes.
                    IAsn1Value boxed = heartbeat;
                    var writer = new System.Buffers.ArrayBufferWriter<byte>();
                    Asn1Serializer.Encode(boxed, writer, PerEncodingRule.Unaligned);
                    using var stream = new System.IO.MemoryStream();
                    Asn1Serializer.EncodeAsync(boxed, stream, PerEncodingRule.Unaligned).GetAwaiter().GetResult();
                    if (System.Convert.ToHexString(writer.WrittenSpan) != "07EE6B2800" || System.Convert.ToHexString(stream.ToArray()) != "07EE6B2800") throw new System.Exception("writers");

                    // keys: SEQUENCE (SIZE (0..16)) OF VisibleString (SIZE (1..16)); an empty key violates it.
                    IAsn1Value invalid = new ConfigurationQuery { RequestId = 1, Keys = [""] };
                    results.Add(Asn1Serializer.Validate(invalid).Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    return string.Join("|", results);
                }
            }
            """;
        var generated = GeneratedAssembly.For(["samples/telemetry/telemetry.asn"], extraSource: usage);
        var parts = ((string)generated.Assembly.GetType("Usage")!.GetMethod("Run")!.Invoke(null, null)!).Split('|');

        Assert.Equal("Telemetry-Protocol.Heartbeat=07EE6B2800", parts[0]);
        Assert.StartsWith("Telemetry-Protocol.Message=", parts[1], StringComparison.Ordinal);

        // A CHOICE alternative reports and encodes as its CHOICE type.
        Assert.StartsWith("Telemetry-Protocol.Payload=", parts[2], StringComparison.Ordinal);
        Assert.Equal("1", parts[3]);
    }
}
