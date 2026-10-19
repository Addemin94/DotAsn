using System.Text;
using System.Text.Json.Nodes;
using Asn1.Runtime;
using Asn1.Runtime.Jer;
using Xunit;

namespace Asn1.RuntimeTests;

public class JerTests
{
    private static readonly JerEncodingRule Jer = JerEncodingRule.Instance;

    private const string Definitions = """
        MyInteger ::= INTEGER (0..1500)
        MyEnumerated ::= ENUMERATED { red, yellow, green }
        MyReal ::= REAL (0 | WITH COMPONENTS { mantissa (-999999999999..999999999999), base (10), exponent (-100..100) })
        MyBitString1 ::= BIT STRING (SIZE (10))
        MyBitString2 ::= BIT STRING (SIZE (10), ...)
        MyOctetString ::= OCTET STRING (SIZE (4))
        MySequence1 ::= SEQUENCE { a INTEGER OPTIONAL, b BOOLEAN, c UTF8String }
        MySequence2 ::= SEQUENCE { x MyReal, y MySequence1, ... }
        MySequenceOf1 ::= SEQUENCE (SIZE (1..16)) OF INTEGER
        MySequenceOf2 ::= SEQUENCE OF MySequence1
        MyChoice ::= CHOICE { a MySequence1, b UniversalString }
        B ::= BOOLEAN
        I ::= INTEGER
        R ::= REAL
        BS ::= BIT STRING
        OS ::= OCTET STRING
        N ::= NULL
        O ::= OBJECT IDENTIFIER
        VS ::= VisibleString
        IS ::= IA5String
        BMP ::= BMPString
        U8 ::= UTF8String
        US ::= UniversalString
        PS ::= PrintableString
        T ::= TIME
        """;

    private static readonly Schema s_schema = new(Definitions);

    private static string Canonical(string json) => Canon(JsonNode.Parse(json));

    private static string Canon(JsonNode? node) => node switch
    {
        null => "null",
        JsonObject o => "{" + string.Join(",", o.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => System.Text.Json.JsonSerializer.Serialize(p.Key) + ":" + Canon(p.Value))) + "}",
        JsonArray a => "[" + string.Join(",", a.Select(Canon)) + "]",
        _ => node.ToJsonString(),
    };

    private static string EncodeText(string type, string value) => Encoding.UTF8.GetString(Jer.Encode(s_schema[type], s_schema.Value(type, value)));

    private static string DecodeText(string type, string jer) =>
        Asn1.Dynamic.DynamicJson.Serialize(s_schema[type], Jer.Decode(s_schema[type], Encoding.UTF8.GetBytes(jer)), indented: false);

    /// <summary>X.697 (02/2021) A.4: the published encodings (base-2 REAL values).</summary>
    [Theory]
    [InlineData("B", "true", "true")]
    [InlineData("I", "100", "100")]
    [InlineData("MyInteger", "100", "100")]
    [InlineData("MyEnumerated", "\"red\"", "\"red\"")]
    [InlineData("R", "14", "14")]
    [InlineData("R", "\"NOT-A-NUMBER\"", "\"NaN\"")] // DotAsn value notation spells NaN as NOT-A-NUMBER
    [InlineData("MyBitString1", "{\"hex\":\"5540\",\"length\":10}", "\"5540\"")]
    [InlineData("BS", "{\"hex\":\"5540\",\"length\":10}", "{ \"length\" : 10, \"value\" : \"5540\" }")]
    [InlineData("MyBitString2", "{\"hex\":\"5540\",\"length\":10}", "{ \"length\" : 10, \"value\" : \"5540\" }")]
    [InlineData("OS", "\"EABC001E\"", "\"EABC001E\"")]
    [InlineData("MyOctetString", "\"EABC001E\"", "\"EABC001E\"")]
    [InlineData("N", "null", "null")]
    [InlineData("MySequence1", "{\"a\":123,\"b\":true,\"c\":\"Hello\"}", "{ \"a\" : 123, \"b\" : true, \"c\" : \"Hello\" }")]
    [InlineData("MySequence1", "{\"b\":true,\"c\":\"Hello\"}", "{ \"b\" : true, \"c\" : \"Hello\" }")]
    [InlineData("MySequence2", "{\"x\":-3.1415,\"y\":{\"b\":true,\"c\":\"Hello\"}}", "{ \"x\" : -3.1415, \"y\" : { \"b\" : true, \"c\" : \"Hello\" }}")]
    [InlineData("MySequenceOf1", "[1,2,3]", "[ 1, 2, 3 ]")]
    [InlineData("MySequenceOf2", "[{\"b\":true,\"c\":\"one\"},{\"a\":99,\"b\":false,\"c\":\"two\"}]", "[{ \"b\" : true, \"c\" : \"one\" }, { \"a\" : 99, \"b\" : false, \"c\" : \"two\" }]")]
    [InlineData("MyChoice", "{\"b\":\"mouse\"}", "{ \"b\" : \"mouse\" }")]
    [InlineData("O", "\"1.0.8571.1\"", "\"1.0.8571.1\"")]
    [InlineData("VS", "\"ABCDEabcde12345(/)\"", "\"ABCDEabcde12345(/)\"")]
    [InlineData("IS", "\"ABCDEabcde12345(/)\"", "\"ABCDEabcde12345(/)\"")]
    [InlineData("BMP", "\"ABCDEabcde12345(/)\"", "\"ABCDEabcde12345(/)\"")]
    [InlineData("U8", "\"ABCDEabcde12345(/)\"", "\"ABCDEabcde12345(/)\"")]
    [InlineData("US", "\"ABCDEabcde12345(/)\"", "\"ABCDEabcde12345(/)\"")]
    [InlineData("PS", "\"ABCDEabcde12345(/)\"", "\"ABCDEabcde12345(/)\"")]
    [InlineData("T", "\"2014-12-31T23:59:59\"", "\"2014-12-31T23:59:59\"")]
    public void AnnexAExamples(string type, string value, string published)
    {
        Assert.Equal(Canonical(published), Canonical(EncodeText(type, value)));
        Assert.Equal(Canonical(value), Canonical(DecodeText(type, published)));
    }

    /// <summary>X.697 A.4: base-10 REAL values are published as base10Value objects or numbers; both decode.</summary>
    [Theory]
    [InlineData("R", "{ \"base10Value\" : 14 }", "14")]
    [InlineData("MyReal", "14.56", "14.56")]
    [InlineData("MyReal", "0.145600e2", "14.56")]
    public void Base10RealsDecode(string type, string jer, string value) => Assert.Equal(Canonical(value), Canonical(DecodeText(type, jer)));

    [Theory]
    [InlineData("R", "\"-0\"")]
    [InlineData("R", "\"INF\"")]
    [InlineData("R", "\"-INF\"")]
    public void SpecialRealsUseStrings(string type, string jer) => Assert.Equal(jer, EncodeText(type, DecodeText(type, jer)));

    [Theory]
    [InlineData("MySequence1", "{\"c\":\"x\",\"b\":false,\"a\":null}", "{\"b\":false,\"c\":\"x\"}")] // 27.3.3/27.3.4
    [InlineData("MySequence1", "{\"\\u0062\":true,\"c\":\"\\u00e9\"}", "{\"b\":true,\"c\":\"é\"}")] // 7.6.3: escapes
    [InlineData("OS", "\"eabc001e\"", "\"EABC001E\"")] // 25.3: lower-case hexadecimal
    [InlineData("I", " 123456789012345678901234567890 ", "\"123456789012345678901234567890\"")]
    public void DecoderAcceptsSenderOptions(string type, string jer, string value) => Assert.Equal(Canonical(value), Canonical(DecodeText(type, jer)));

    [Theory]
    [InlineData("I", "1.0")] // 21: no fractional part
    [InlineData("I", "1e3")] // 21: no exponent
    [InlineData("I", "\"1\"")]
    [InlineData("B", "1")]
    [InlineData("MyEnumerated", "\"blue\"")]
    [InlineData("MySequence1", "{\"b\":true}")] // c is mandatory
    [InlineData("MySequence1", "{\"b\":true,\"c\":\"x\",\"z\":1}")] // not extensible
    [InlineData("MySequence1", "{\"b\":true,\"b\":false,\"c\":\"x\"}")] // duplicate member
    [InlineData("MyChoice", "{\"a\":{\"b\":true,\"c\":\"x\"},\"b\":\"y\"}")] // 31.3: exactly one member
    [InlineData("MyBitString1", "\"55\"")] // fixed size 10 needs two octets
    [InlineData("BS", "{\"value\":\"55\",\"length\":9}")]
    [InlineData("OS", "\"ABC\"")]
    [InlineData("VS", "\"é\"")]
    [InlineData("N", "{}")]
    [InlineData("I", "1 2")] // trailing data
    [InlineData("I", "")]
    [InlineData("MySequenceOf1", "[1,2")]
    public void InvalidInputIsRejected(string type, string jer) =>
        Assert.Throws<Asn1DecodeException>(() => Jer.Decode(s_schema[type], Encoding.UTF8.GetBytes(jer)));

    [Fact]
    public void InvalidUtf8IsRejected()
    {
        // Found by fuzzing: invalid UTF-8 inside a JSON string must be a decode error, not InvalidOperationException.
        var e = Assert.Throws<Asn1DecodeException>(() => Jer.Decode(s_schema["VS"], new byte[] { 0x22, 0xC3, 0x28, 0x22 }));
        Assert.Equal(Asn1DecodeError.Malformed, e.Error);
    }

    [Fact]
    public void UnknownExtensionsAreSkippedOrRelayed()
    {
        var v1 = new Schema("S ::= SEQUENCE { a BOOLEAN, ... }\nC ::= CHOICE { a BOOLEAN, ... }");
        Assert.Equal("{\"a\":true}", v1.Decode(Jer, "S", Convert.ToHexString(Encoding.UTF8.GetBytes("{\"a\":true,\"b\":\"x\"}"))));
        var raw = Encoding.UTF8.GetBytes("{\"b\":{\"x\":[1,2]}}");
        var decoded = Jer.Decode(v1["C"], raw);
        Assert.Null(Assert.IsType<Asn1.Dynamic.Asn1Choice>(decoded).Alternative);
        Assert.Equal("{\"b\":{\"x\":[1,2]}}", Encoding.UTF8.GetString(Jer.Encode(v1["C"], decoded)));
    }

    [Fact]
    public void NonJsonStringTypesUseHexadecimalOctets()
    {
        // 38.2: TeletexString and friends are encoded as the OCTET STRING of their X.690 8.23.5 octets.
        var schema = new Schema("G ::= GeneralString");
        Assert.Equal("\"6162\"", Encoding.UTF8.GetString(Jer.Encode(schema["G"], schema.Value("G", "\"ab\""))));
    }

    [Fact]
    public void ContentsConstrainedContainingFormIsReportedAsUnsupported()
    {
        var schema = new Schema("O ::= OCTET STRING (CONTAINING INTEGER)");
        Assert.Throws<Asn1UnsupportedException>(() => Jer.Decode(schema["O"], Encoding.UTF8.GetBytes("{\"containing\":5}")));
    }

    [Fact]
    public void IndentedOutputIsEquivalent()
    {
        var value = s_schema.Value("MySequence1", "{\"a\":1,\"b\":true,\"c\":\"x\"}");
        var indented = Encoding.UTF8.GetString(JerEncodingRule.Indented.Encode(s_schema["MySequence1"], value));
        Assert.Contains("\n", indented, StringComparison.Ordinal);
        Assert.Equal(Canonical(EncodeText("MySequence1", "{\"a\":1,\"b\":true,\"c\":\"x\"}")), Canonical(indented));
    }
}
