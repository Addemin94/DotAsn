using Asn1.Dynamic;
using Asn1.Runtime;
using Asn1.Runtime.Ber;
using Asn1.Runtime.Jer;
using Asn1.Runtime.Per;
using Asn1.Semantics;
using Xunit;

namespace Asn1.RuntimeTests;

/// <summary>
/// The DotAsn schema (<see cref="Asn1Schema"/>, <see cref="Asn1SchemaWriter"/>): what the WebAssembly runtime loads
/// instead of compiling. Encoding equivalence over all vectors is covered by SchemaVectorTests.
/// </summary>
public class SchemaFormatTests
{
    private const string Spec = """
        T DEFINITIONS AUTOMATIC TAGS ::= BEGIN
          Small ::= INTEGER (0..255)
          Color ::= ENUMERATED { red, green, ..., blue }
          Flags ::= BIT STRING { a(0), b(1) } (SIZE (2..8))
          Name ::= IA5String (FROM ("a".."z") ^ SIZE (1..8)) (PATTERN "[a-z]+")
          Point ::= SEQUENCE { x Small, y Small DEFAULT 5, label Name OPTIONAL, ..., [[ z Small ]] }
          Shape ::= CHOICE { point Point, color Color, ... }
          Points ::= SEQUENCE (SIZE (0..4)) OF Point
          Tree ::= SEQUENCE { value Small, children SEQUENCE OF Tree }
          Ext ::= EXTERNAL
          Alias ::= Point
        END
        """;

    private static Asn1Compilation Compile()
    {
        var compilation = Asn1Compilation.FromText(Spec);
        Assert.DoesNotContain(compilation.Diagnostics, d => d.IsError);
        return compilation;
    }

    private static Asn1Schema Load() => Asn1Schema.Parse(Asn1SchemaWriter.Write(Compile()));

    [Theory]
    [InlineData("Point", """{"x":1,"label":"ab","z":3}""")]
    [InlineData("Shape", """{"point":{"x":1,"y":9}}""")]
    [InlineData("Shape", """{"color":"blue"}""")]
    [InlineData("Points", """[{"x":1},{"x":2,"y":3}]""")]
    [InlineData("Tree", """{"value":1,"children":[{"value":2,"children":[]}]}""")]
    [InlineData("Flags", """{"hex":"C0","length":2}""")]
    [InlineData("Alias", """{"x":4}""")]
    public void SchemaDescriptorsEncodeLikeCompiledOnes(string type, string json)
    {
        var compilation = Compile();
        var compiled = new DynamicTypeFactory().Create(compilation.FindType(type)!);
        var loaded = Asn1Schema.Parse(Asn1SchemaWriter.Write(compilation)).GetType(type);
        foreach (var rule in new Asn1EncodingRule[] { BerEncodingRule.Instance, DerEncodingRule.Instance, PerEncodingRule.Unaligned, PerEncodingRule.Aligned, JerEncodingRule.Instance })
        {
            var expected = rule.Encode(compiled, DynamicJson.Parse(compiled, json));
            var actual = rule.Encode(loaded, DynamicJson.Parse(loaded, json));
            Assert.Equal(Convert.ToHexString(expected), Convert.ToHexString(actual));
            Assert.Equal(DynamicJson.Serialize(compiled, rule.Decode(compiled, expected)), DynamicJson.Serialize(loaded, rule.Decode(loaded, actual)));
        }
    }

    [Fact]
    public void ConstraintsSurviveTheSchema()
    {
        var schema = Load();
        Assert.Throws<Asn1ConstraintViolationException>(() => PerEncodingRule.Unaligned.Encode(schema.GetType("Small"), DynamicJson.Parse(schema.GetType("Small"), "300")));
        Assert.NotEmpty(Asn1Validator.ValidateValue(schema.GetType("Name"), DynamicJson.Parse(schema.GetType("Name"), "\"ABC\"")));
        Assert.Empty(Asn1Validator.ValidateValue(schema.GetType("Name"), DynamicJson.Parse(schema.GetType("Name"), "\"abc\"")));
        Assert.Equal("{\"x\":1,\"y\":5}", DynamicJson.Serialize(schema.GetType("Point"), PerEncodingRule.Unaligned.Decode(schema.GetType("Point"), Convert.FromHexString("0020")), indented: false));
    }

    [Fact]
    public void TypesAreFoundByQualifiedOrUniqueName()
    {
        var schema = Load();
        Assert.Contains("T.Point", schema.TypeNames);
        Assert.Same(schema.GetType("T.Point"), schema.GetType("Point"));
        Assert.False(schema.TryGetType("Missing", out _));
        Assert.Throws<KeyNotFoundException>(() => schema.GetType("Missing"));
    }

    [Fact]
    public void UnsupportedTypesFailOnUse()
    {
        var schema = Load();
        var e = Assert.Throws<Asn1UnsupportedException>(() => schema.GetType("Ext"));
        Assert.Contains("Ext", e.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[]", "not a DotAsn schema")]
    [InlineData("""{"format":"dotasn-schema","version":2,"names":{},"types":[]}""", "unsupported schema version")]
    [InlineData("""{"format":"dotasn-schema","version":1,"names":{"T.A":3},"types":[]}""", "missing node")]
    [InlineData("{", "invalid schema JSON")]
    public void InvalidSchemasAreRejected(string json, string message)
    {
        var e = Assert.Throws<Asn1SchemaException>(() => Asn1Schema.Parse(json));
        Assert.Contains(message, e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WritingIsDeterministic() => Assert.Equal(Asn1SchemaWriter.Write(Compile()), Asn1SchemaWriter.Write(Compile()));

    [Fact]
    public void UnknownAlternativesRoundTripThroughTheNotation()
    {
        var v2 = new Schema("C ::= CHOICE { a INTEGER (0..7), ..., b BOOLEAN, c UTF8String }");
        var v1 = new Schema("C ::= CHOICE { a INTEGER (0..7), ... }");
        foreach (var rule in new Asn1EncodingRule[] { PerEncodingRule.Unaligned, PerEncodingRule.Aligned, BerEncodingRule.Instance })
        {
            var original = v2.Encode(rule, "C", """{"c":"hi"}""");
            var unknown = v1.Decode(rule, "C", original);
            Assert.Contains("$unknownExtension", unknown, StringComparison.Ordinal);

            // The notation carries everything needed to relay the alternative with the same rule.
            Assert.Equal(original, v1.Encode(rule, "C", unknown));
        }
    }

    [Fact]
    public void ValuesBeyond64BitsFollowTheDynamicRuntimeLimits()
    {
        var compilation = Asn1Compilation.FromText("""
            T DEFINITIONS ::= BEGIN
              Big ::= INTEGER { small(1), top(18446744073709551615) } (0..18446744073709551615)
              Huge ::= BIT STRING { huge(9223372036854775808) }
              En ::= ENUMERATED { a(1), b(100000000000000000000) }
            END
            """);
        var schema = Asn1Schema.Parse(Asn1SchemaWriter.Write(compilation));

        // INTEGER named numbers are metadata: the large one is left out, the type works.
        // (0..2^64-1) is a 64-bit constrained whole number in UPER.
        Assert.Equal("0000000000000001", Convert.ToHexString(PerEncodingRule.Unaligned.Encode(schema.GetType("Big"), DynamicJson.Parse(schema.GetType("Big"), "1"))));

        // Types that the runtime cannot represent are written as unsupported and fail on use.
        Assert.Contains("must fit in 64 bits", Assert.Throws<Asn1UnsupportedException>(() => schema.GetType("Huge")).Message, StringComparison.Ordinal);
        Assert.Contains("must fit in 64 bits", Assert.Throws<Asn1UnsupportedException>(() => schema.GetType("En")).Message, StringComparison.Ordinal);
    }
}
