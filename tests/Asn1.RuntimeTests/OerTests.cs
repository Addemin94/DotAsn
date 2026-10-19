using Asn1.Dynamic;
using Asn1.Runtime;
using Asn1.Runtime.Oer;
using Xunit;

namespace Asn1.RuntimeTests;

public class OerTests
{
    private static readonly OerEncodingRule Oer = OerEncodingRule.Basic;
    private static readonly OerEncodingRule Coer = OerEncodingRule.Canonical;

    [Theory]
    [InlineData("INTEGER (0..255)", "255", "FF")] // 10.3 a
    [InlineData("INTEGER (0..256)", "256", "0100")] // 10.3 b
    [InlineData("INTEGER (0..65536)", "1", "00000001")] // 10.3 c
    [InlineData("INTEGER (0..4294967296)", "1", "0000000000000001")] // 10.3 d
    [InlineData("INTEGER (0..18446744073709551616)", "1", "0101")] // 10.3 e
    [InlineData("INTEGER (0..MAX)", "256", "020100")] // 10.3 e
    [InlineData("INTEGER (-1..127)", "-1", "FF")] // 10.4 a
    [InlineData("INTEGER (-129..0)", "-129", "FF7F")] // 10.4 b
    [InlineData("INTEGER (-1..2147483648)", "-1", "FFFFFFFFFFFFFFFF")] // 10.4 d
    [InlineData("INTEGER", "128", "020080")] // 10.4 e
    [InlineData("INTEGER (-5..MAX)", "-1", "01FF")] // 10.4 e (no upper bound)
    [InlineData("INTEGER (0..10, ...)", "5", "0105")] // 8.2.2 g: extensible constraints are not OER-visible
    [InlineData("INTEGER (0..10, ...) (0..5)", "5", "05")] // 8.2.3: serial application removes earlier extensibility
    [InlineData("BOOLEAN", "true", "FF")] // 9, 31.3
    [InlineData("ENUMERATED { a(0), b(200) }", "\"b\"", "8200C8")] // 11.4: signed, so 200 needs two octets
    [InlineData("ENUMERATED { a(-1), b(5) }", "\"a\"", "81FF")]
    [InlineData("ENUMERATED { a(0), b(127) }", "\"b\"", "7F")] // 11.3
    [InlineData("REAL", "1", "03800001")] // 12.4: DER contents octets
    [InlineData("OCTET STRING (SIZE (2))", "\"ABCD\"", "ABCD")] // 14.1
    [InlineData("OCTET STRING (SIZE (1..2, ...))", "\"AB\"", "01AB")] // 14.2
    [InlineData("BIT STRING (SIZE (4))", "{\"hex\":\"B0\",\"length\":4}", "B0")] // 13.2
    [InlineData("BIT STRING", "{\"hex\":\"B0\",\"length\":4}", "0204B0")] // 13.3
    [InlineData("BIT STRING", "{\"hex\":\"\",\"length\":0}", "0100")] // 13.3.4
    [InlineData("IA5String (SIZE (2))", "\"ab\"", "6162")] // 27.2
    [InlineData("BMPString (SIZE (1))", "\"a\"", "0061")] // 27.4 c
    [InlineData("UniversalString", "\"a\"", "0400000061")] // 27.4 d
    [InlineData("UTF8String (SIZE (2))", "\"ab\"", "026162")] // 27.1: UTF8String is not known-multiplier
    [InlineData("OBJECT IDENTIFIER", "\"1.2.3\"", "022A03")] // 21
    [InlineData("SEQUENCE OF BOOLEAN", "[true,false]", "0102FF00")] // 17.2
    [InlineData("SEQUENCE { a BOOLEAN OPTIONAL, b BOOLEAN }", "{\"b\":true}", "00FF")] // 16.2
    [InlineData("SEQUENCE { a BOOLEAN OPTIONAL, b BOOLEAN }", "{\"a\":false,\"b\":true}", "8000FF")]
    [InlineData("SEQUENCE { a BOOLEAN, ... }", "{\"a\":true}", "00FF")] // 16.2.2: extension bit 0
    [InlineData("SEQUENCE { a BOOLEAN, ..., b BOOLEAN OPTIONAL }", "{\"a\":true,\"b\":true}", "80FF02078001FF")] // 16.4, 16.5.1
    [InlineData("SEQUENCE { a INTEGER DEFAULT 5 }", "{\"a\":5}", "00")] // 31.9 (also chosen by BASIC-OER)
    [InlineData("SEQUENCE { a BOOLEAN, ..., [[ b INTEGER (0..255) OPTIONAL ]] }", "{\"a\":true,\"b\":5}", "80FF020780028005")] // 16.5.2: group preamble
    [InlineData("CHOICE { a BOOLEAN, b INTEGER }", "{\"b\":5}", "810105")] // 20.1, 8.7.2.2: [1] context
    [InlineData("CHOICE { a BOOLEAN, ..., c BOOLEAN }", "{\"c\":true}", "8101FF")] // 20.2: open type
    [InlineData("CHOICE { a [100] BOOLEAN }", "{\"a\":true}", "BF64FF")] // 8.7.2.3
    [InlineData("CHOICE { a [APPLICATION 1] BOOLEAN }", "{\"a\":false}", "4100")] // 8.7.2.1
    public void KnownAnswers(string type, string json, string expected)
    {
        var schema = new Schema("T ::= " + type);
        Assert.Equal(expected, schema.Encode(Oer, "T", json));
        Assert.Equal(expected, schema.Encode(Coer, "T", json));
        var decoded = schema.Decode(Coer, "T", expected);
        Assert.Equal(expected, schema.Encode(Oer, "T", decoded));
    }

    [Fact]
    public void UntaggedChoiceAlternativeRepeatsTheTag()
    {
        // 20.1 note 3: the outermost tag of an untagged CHOICE alternative is that of its own chosen alternative.
        var schema = new Schema("C ::= CHOICE { x [1] BOOLEAN, y CHOICE { z [2] BOOLEAN } }", "EXPLICIT TAGS");
        Assert.Equal("8282FF", schema.Encode(Oer, "C", "{\"y\":{\"z\":true}}"));
        Assert.Equal("{\"y\":{\"z\":true}}", schema.Decode(Oer, "C", "8282FF"));
        Assert.Equal("{\"x\":true}", schema.Decode(Oer, "C", "81FF"));
    }

    [Fact]
    public void SetComponentsUseCanonicalTagOrder()
    {
        // 18.2: [APPLICATION 1] precedes [0] regardless of textual order.
        var schema = new Schema("S ::= SET { a [0] BOOLEAN, b [APPLICATION 1] INTEGER (0..255) }");
        Assert.Equal("07FF", schema.Encode(Oer, "S", "{\"a\":true,\"b\":7}"));
    }

    [Fact]
    public void CanonicalSetOfIsSorted()
    {
        var schema = new Schema("S ::= SET OF INTEGER (0..255)");
        Assert.Equal("0103010203", schema.Encode(Coer, "S", "[3,1,2]"));
        Assert.Equal("0103030102", schema.Encode(Oer, "S", "[3,1,2]"));
    }

    [Theory]
    [InlineData("BOOLEAN", "01", "true")] // 31.3
    [InlineData("INTEGER", "020001", "1")] // 31.4
    [InlineData("INTEGER (0..MAX)", "020001", "1")] // 31.4
    [InlineData("OCTET STRING", "8101AB", "\"AB\"")] // 31.2: long form for a short length
    [InlineData("OCTET STRING", "820001AB", "\"AB\"")] // 31.2: non-minimal long form
    [InlineData("ENUMERATED { a, b }", "8101", "\"b\"")] // 31.5
    [InlineData("SEQUENCE OF BOOLEAN", "020001FF", "[true]")] // 31.7
    [InlineData("SET OF INTEGER (0..255)", "01020201", "[2,1]")] // 31.8
    [InlineData("SEQUENCE { a INTEGER DEFAULT 5 }", "800105", "{\"a\":5}")] // 31.9
    [InlineData("BIT STRING { x(0), y(1) }", "020680", "{\"hex\":\"80\",\"length\":2}")] // 31.6
    public void CanonicalDecoderRejectsBasicOnlyForms(string type, string hex, string expected)
    {
        var schema = new Schema("T ::= " + type);
        Assert.Equal(expected, schema.Decode(Oer, "T", hex));
        Assert.Equal(Asn1DecodeError.NonCanonical, Assert.Throws<Asn1DecodeException>(() => schema.Decode(Coer, "T", hex)).Error);
    }

    [Fact]
    public void UnknownExtensionsAreSkippedOrRelayed()
    {
        var v1 = new Schema("S ::= SEQUENCE { a BOOLEAN, ... }\nC ::= CHOICE { a BOOLEAN, ... }\nE ::= ENUMERATED { a, ... }");
        var v2 = new Schema("S ::= SEQUENCE { a BOOLEAN, ..., b IA5String OPTIONAL }\nC ::= CHOICE { a BOOLEAN, ..., b IA5String }\nE ::= ENUMERATED { a, ..., b }");
        Assert.Equal("{\"a\":true}", v1.Decode(Coer, "S", v2.Encode(Oer, "S", "{\"a\":true,\"b\":\"x\"}")));

        var hex = v2.Encode(Oer, "C", "{\"b\":\"new\"}");
        var decoded = Oer.Decode(v1["C"], Convert.FromHexString(hex));
        Assert.Null(Assert.IsType<Asn1Choice>(decoded).Alternative);
        Assert.Equal(hex, Convert.ToHexString(Oer.Encode(v1["C"], decoded)));

        // An unknown value of an extensible enumeration decodes to the unknown-extension placeholder.
        Assert.StartsWith("\"#unknown-extension", v1.Decode(Oer, "E", "01"), StringComparison.Ordinal);
        Assert.Throws<Asn1DecodeException>(() => new Schema("E ::= ENUMERATED { a }").Decode(Oer, "E", "01"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("80")]
    [InlineData("0005AB")]
    [InlineData("0081")]
    [InlineData("00FF")]
    public void TruncatedInputFailsCleanly(string hex)
    {
        var schema = new Schema("S ::= SEQUENCE { a INTEGER (0..255), b OCTET STRING (SIZE (1..10)), c BOOLEAN }");
        Assert.Throws<Asn1DecodeException>(() => schema.Decode(Oer, "S", hex));
    }

    [Fact]
    public void ValuesOutsideTheVisibleRangeAreRejected()
    {
        var schema = new Schema("T ::= INTEGER (0..10)");
        Assert.Throws<Asn1ConstraintViolationException>(() => schema.Encode(Oer, "T", "11"));
        Assert.Equal(Asn1DecodeError.ConstraintViolation, Assert.Throws<Asn1DecodeException>(() => schema.Decode(Oer, "T", "0B")).Error);
    }

    [Fact]
    public void RealWithComponentConstraintIsReportedAsUnsupported()
    {
        var schema = new Schema("R ::= REAL (WITH COMPONENTS { mantissa (-16777215..16777215), base (2), exponent (-125..128) })");
        Assert.Throws<Asn1UnsupportedException>(() => schema.Encode(Oer, "R", "1"));
    }
}
