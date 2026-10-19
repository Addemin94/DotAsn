using Asn1.Dynamic;
using Asn1.Runtime;
using Asn1.Runtime.Per;
using Xunit;

namespace Asn1.RuntimeTests;

public class PerTests
{
    private static readonly PerEncodingRule Uper = PerEncodingRule.Unaligned;
    private static readonly PerEncodingRule Aper = PerEncodingRule.Aligned;

    [Theory]
    [InlineData("INTEGER (0..255)", "255", "FF", "FF")]
    [InlineData("INTEGER (0..7)", "5", "A0", "A0")]
    [InlineData("INTEGER (1..1)", "1", "00", "00")]
    [InlineData("INTEGER (0..65535)", "256", "0100", "0100")]
    [InlineData("INTEGER (0..4294967295)", "256", "00000100", "400100")]
    [InlineData("INTEGER (-10..MAX)", "-10", "0100", "0100")]
    [InlineData("INTEGER", "-1", "01FF", "01FF")]
    [InlineData("INTEGER (0..10, ...)", "5", "28", "28")]
    [InlineData("INTEGER (0..10, ..., 100)", "100", "80B200", "800164")]
    [InlineData("BOOLEAN", "true", "80", "80")]
    [InlineData("OCTET STRING (SIZE (2))", "\"ABCD\"", "ABCD", "ABCD")]
    [InlineData("OCTET STRING (SIZE (0..5))", "\"AB\"", "3560", "20AB")]
    [InlineData("IA5String (SIZE (1..4))", "\"ab\"", "70E2", "406162")]
    [InlineData("NumericString (SIZE (3))", "\"123\"", "2340", "2340")]
    [InlineData("BIT STRING (SIZE (4))", "{\"hex\":\"B0\",\"length\":4}", "B0", "B0")]
    [InlineData("SEQUENCE SIZE (0..3) OF BOOLEAN", "[true, false, true]", "E8", "E8")]
    public void KnownAnswers(string type, string json, string uper, string aper)
    {
        var schema = new Schema("T ::= " + type);
        Assert.Equal(uper, schema.Encode(Uper, "T", json));
        Assert.Equal(aper, schema.Encode(Aper, "T", json));
        Assert.Equal(VectorCanonical(json), VectorCanonical(schema.Decode(Uper, "T", uper)));
        Assert.Equal(VectorCanonical(json), VectorCanonical(schema.Decode(Aper, "T", aper)));
    }

    private static string VectorCanonical(string json) => System.Text.Json.Nodes.JsonNode.Parse(json)!.ToJsonString();

    [Theory]
    [InlineData(127, "7F")]
    [InlineData(128, "8080")]
    [InlineData(16383, "BFFF")]
    [InlineData(16384, "C1")]
    [InlineData(65536, "C4")]
    [InlineData(70000, "C4")]
    public void LongOctetStringsUseFragmentation(int length, string prefix)
    {
        var schema = new Schema("T ::= OCTET STRING");
        var data = Enumerable.Range(0, length).Select(i => (byte)i).ToArray();
        var type = schema["T"];
        var encoded = Uper.Encode(type, data);
        Assert.StartsWith(prefix, Convert.ToHexString(encoded.AsSpan(0, 2)), StringComparison.Ordinal);
        Assert.Equal(data, (byte[])Uper.Decode(type, encoded)!);
        Assert.Equal(data, (byte[])Aper.Decode(type, Aper.Encode(type, data))!);
        if (length % 16384 == 0)
        {
            // A length that is an exact multiple of 16K ends with a zero-length fragment (X.691 11.9.3.8.3 note).
            Assert.Equal(0, encoded[^1]);
        }
    }

    [Fact]
    public void LargeCollectionsAndBitStringsRoundTrip()
    {
        var schema = new Schema("L ::= SEQUENCE OF INTEGER (0..3)\nB ::= BIT STRING");
        var list = Enumerable.Range(0, 40000).Select(i => (object?)new System.Numerics.BigInteger(i % 4)).ToList();
        Assert.Equal(40000, ((List<object?>)Uper.Decode(schema["L"], Uper.Encode(schema["L"], list))!).Count);
        var bits = new Asn1BitString(Enumerable.Range(0, 5000).Select(i => (byte)i).ToArray(), 39999);
        Assert.Equal(bits, Aper.Decode(schema["B"], Aper.Encode(schema["B"], bits)));
    }

    [Fact]
    public void OlderDecoderSkipsUnknownSequenceAdditions()
    {
        var v1 = new Schema("S ::= SEQUENCE { a INTEGER (0..15), ... }");
        var v2 = new Schema("S ::= SEQUENCE { a INTEGER (0..15), ..., b IA5String, [[ c BOOLEAN, d INTEGER OPTIONAL ]] }");
        foreach (var rule in new[] { Uper, Aper })
        {
            var hex = v2.Encode(rule, "S", "{\"a\":3,\"b\":\"hi\",\"c\":true,\"d\":-5}");
            Assert.Equal("{\"a\":3}", v1.Decode(rule, "S", hex));
            Assert.Equal("{\"a\":3,\"b\":\"hi\",\"c\":true,\"d\":-5}", v2.Decode(rule, "S", hex));
        }
    }

    [Fact]
    public void UnknownChoiceAlternativeIsPreservedForRelay()
    {
        var v1 = new Schema("C ::= CHOICE { a INTEGER (0..3), ... }");
        var v2 = new Schema("C ::= CHOICE { a INTEGER (0..3), ..., b IA5String }");
        var hex = v2.Encode(Uper, "C", "{\"b\":\"new\"}");
        var decoded = Uper.Decode(v1["C"], Convert.FromHexString(hex));
        var choice = Assert.IsType<Asn1Choice>(decoded);
        Assert.Null(choice.Alternative);
        Assert.Equal(hex, Convert.ToHexString(Uper.Encode(v1["C"], decoded)));
    }

    [Fact]
    public void ExtensibleEnumerationsUseNormallySmallIndices()
    {
        var schema = new Schema("E ::= ENUMERATED { a, b, ..., c, d }");
        Assert.Equal("40", schema.Encode(Uper, "E", "\"b\""));
        Assert.Equal("81", schema.Encode(Uper, "E", "\"d\""));
        Assert.Equal("\"d\"", schema.Decode(Uper, "E", "81"));
    }

    [Fact]
    public void CanonicalChoiceOrderUsesTags()
    {
        // X.691 23.2: BOOLEAN [UNIVERSAL 1] sorts before INTEGER [UNIVERSAL 2], so b has index 0.
        var schema = new Schema("C ::= CHOICE { a INTEGER (0..1), b BOOLEAN }", "EXPLICIT TAGS");
        Assert.Equal("40", schema.Encode(Uper, "C", "{\"b\":true}"));
        Assert.Equal("C0", schema.Encode(Uper, "C", "{\"a\":1}"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("80")]
    [InlineData("FF")]
    public void TruncatedInputFailsCleanly(string hex)
    {
        var schema = new Schema("S ::= SEQUENCE { a INTEGER (0..255), b OCTET STRING (SIZE (1..10)), c BOOLEAN }");
        Assert.Throws<Asn1DecodeException>(() => schema.Decode(Uper, "S", hex));
    }

    [Fact]
    public void OutOfRangeValuesAreRejected()
    {
        var schema = new Schema("S ::= SEQUENCE { a INTEGER (0..255) }");
        Assert.Throws<Asn1ConstraintViolationException>(() => schema.Encode(Uper, "S", "{\"a\":300}"));

        // A 3-bit field for range 0..4 holding 7 is invalid input.
        var narrow = new Schema("T ::= INTEGER (0..4)");
        Assert.Equal(Asn1DecodeError.Malformed, Assert.Throws<Asn1DecodeException>(() => narrow.Decode(Uper, "T", "E0")).Error);
    }

    [Fact]
    public void RandomInputNeverCausesUnexpectedExceptions()
    {
        var schema = new Schema("""
            M ::= SEQUENCE { a INTEGER, b OCTET STRING OPTIONAL, c CHOICE { x BOOLEAN, y SEQUENCE OF IA5String, ... },
                             d BIT STRING (SIZE (0..100, ...)) OPTIONAL, e UTF8String, ..., f INTEGER (0..7) }
            """);
        var type = schema["M"];
        var random = new Random(77);
        foreach (var rule in new[] { Uper, Aper })
        {
            for (var i = 0; i < 20000; i++)
            {
                var data = new byte[random.Next(1, 40)];
                random.NextBytes(data);
                try
                {
                    rule.Decode(type, data);
                }
                catch (Asn1Exception)
                {
                }
            }
        }
    }

    [Fact]
    public void RoundTripsRandomValuesOfTheSampleProtocols()
    {
        foreach (var file in new[] { "telemetry/telemetry.asn", "extensibility/extensibility.asn", "basic/basic.asn" })
        {
            var path = Path.Combine(AppContext.BaseDirectory, "samples", file);
            var compilation = Asn1.Semantics.Asn1Compilation.Create([new Asn1.Diagnostics.SourceText(path, File.ReadAllText(path))]);
            var factory = new DynamicTypeFactory();
            var generator = new RandomValueGenerator(new Random(5));
            foreach (var ta in compilation.TypeAssignments)
            {
                var type = factory.Create(ta);
                for (var i = 0; i < 25; i++)
                {
                    var value = generator.Generate(ta.Type!);
                    foreach (var rule in new Asn1EncodingRule[] { Uper, Aper, Asn1.Runtime.Ber.DerEncodingRule.Instance })
                    {
                        // Decoders fill in DEFAULT values, so compare the canonical re-encodings.
                        var encoded = rule.Encode(type, value);
                        Assert.Equal(Convert.ToHexString(encoded), Convert.ToHexString(rule.Encode(type, rule.Decode(type, encoded))));
                    }
                }
            }
        }
    }
}

public class RuleDataTests
{
    [Fact]
    public void RuleDataSlotsOfDifferentTypesDoNotCollide()
    {
        var a = new Asn1RuleDataSlot<string>();
        var b = new Asn1RuleDataSlot<List<int>>();
        var type = new IntegerType<int>();
        Assert.Equal("x", type.GetRuleData(a, _ => "x"));
        Assert.Equal([1], type.GetRuleData(b, _ => new List<int> { 1 }));
        Assert.Equal("x", type.GetRuleData(a, _ => "y"));
    }
}
