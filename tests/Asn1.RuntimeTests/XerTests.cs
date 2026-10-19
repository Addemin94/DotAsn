using System.Text;
using Asn1.Runtime;
using Asn1.Runtime.Xer;
using Xunit;

namespace Asn1.RuntimeTests;

public class XerTests
{
    private static readonly XerEncodingRule Xer = XerEncodingRule.Basic;
    private static readonly XerEncodingRule Cxer = XerEncodingRule.Canonical;

    private const string Definitions = """
        B ::= BOOLEAN
        I ::= INTEGER
        R ::= REAL
        E ::= ENUMERATED { red, yellow, green }
        BS ::= BIT STRING
        NB ::= BIT STRING { a(0), b(1), c(2) }
        OS ::= OCTET STRING
        N ::= NULL
        O ::= OBJECT IDENTIFIER
        RO ::= RELATIVE-OID
        U8 ::= UTF8String
        GT ::= GeneralizedTime
        S ::= SEQUENCE { a INTEGER OPTIONAL, b BOOLEAN, c UTF8String, d INTEGER DEFAULT 7 }
        St ::= SET { x [1] INTEGER, y [0] BOOLEAN }
        SO ::= SEQUENCE OF INTEGER
        SB ::= SEQUENCE OF BOOLEAN
        SE ::= SEQUENCE OF E
        SN ::= SEQUENCE OF NULL
        SR ::= SEQUENCE OF S
        SI ::= SEQUENCE OF item INTEGER
        SetOfI ::= SET OF INTEGER
        C ::= CHOICE { a INTEGER, b UTF8String }
        SC ::= SEQUENCE OF C
        XMLThing ::= INTEGER
        """;

    private static readonly Schema s_schema = new(Definitions);

    private static string EncodeText(XerEncodingRule rule, string type, string value) => Encoding.UTF8.GetString(rule.Encode(s_schema[type], s_schema.Value(type, value)));

    private static string DecodeText(XerEncodingRule rule, string type, string xml) =>
        Asn1.Dynamic.DynamicJson.Serialize(s_schema[type], rule.Decode(s_schema[type], Encoding.UTF8.GetBytes(xml)), indented: false);

    /// <summary>CANONICAL-XER encodings derived clause by clause from X.693 and the X.680 XML value notation.</summary>
    [Theory]
    [InlineData("B", "true", "<B><true/></B>")] // 8.3.5
    [InlineData("I", "-42", "<I>-42</I>")] // 8.3.6
    [InlineData("I", "\"123456789012345678901234567890\"", "<I>123456789012345678901234567890</I>")]
    [InlineData("E", "\"yellow\"", "<E><yellow/></E>")] // 8.3.7
    [InlineData("R", "0", "<R>0</R>")] // 9.2.1
    [InlineData("R", "\"-0\"", "<R>-0</R>")] // X.680 21.8: minus zero
    [InlineData("R", "1", "<R>1.0E0</R>")] // 9.2.3/9.2.4
    [InlineData("R", "-0.5", "<R>-5.0E-1</R>")]
    [InlineData("R", "123.45", "<R>1.2345E2</R>")]
    [InlineData("R", "1e20", "<R>1.0E20</R>")]
    [InlineData("R", "\"PLUS-INFINITY\"", "<R><PLUS-INFINITY/></R>")] // 8.3.8
    [InlineData("R", "\"NOT-A-NUMBER\"", "<R><NOT-A-NUMBER/></R>")]
    [InlineData("BS", "{\"hex\":\"B0\",\"length\":5}", "<BS>10110</BS>")] // 9.3.1
    [InlineData("BS", "{\"hex\":\"\",\"length\":0}", "<BS/>")] // 9.1.4
    [InlineData("NB", "{\"hex\":\"40\",\"length\":8}", "<NB>01</NB>")] // 9.3.2
    [InlineData("OS", "\"0aff\"", "<OS>0AFF</OS>")] // 9.4
    [InlineData("N", "null", "<N/>")] // 9.1.4
    [InlineData("O", "\"1.2.840.113549\"", "<O>1.2.840.113549</O>")] // 9.8
    [InlineData("RO", "\"5.6\"", "<RO>5.6</RO>")] // 9.9
    [InlineData("U8", "\"a<b&c>\"", "<U8>a&lt;b&amp;c&gt;</U8>")] // X.680 12.15.4
    [InlineData("U8", "\"\\u0000\\u0007\\t\"", "<U8><nul/><bel/>\t</U8>")] // X.680 12.15.5
    [InlineData("U8", "\"\"", "<U8/>")]
    [InlineData("GT", "\"19920722132100.3Z\"", "<GT>19920722132100.3Z</GT>")] // 9.10
    [InlineData("S", "{\"b\":false,\"c\":\"x\"}", "<S><b><false/></b><c>x</c><d>7</d></S>")] // 9.5: DEFAULT present
    [InlineData("St", "{\"x\":1,\"y\":true}", "<St><y><true/></y><x>1</x></St>")] // 9.6: canonical order
    [InlineData("SO", "[1,2]", "<SO><INTEGER>1</INTEGER><INTEGER>2</INTEGER></SO>")] // X.680 26.10
    [InlineData("SB", "[true,false]", "<SB><true/><false/></SB>")] // X.680 26.6
    [InlineData("SE", "[\"red\",\"green\"]", "<SE><red/><green/></SE>")] // X.680 26.7
    [InlineData("SN", "[null,null]", "<SN><NULL/><NULL/></SN>")] // X.680 26.4
    [InlineData("SI", "[3]", "<SI><item>3</item></SI>")] // X.680 26.9
    [InlineData("SR", "[{\"b\":true,\"c\":\"\",\"d\":1}]", "<SR><S><b><true/></b><c/><d>1</d></S></SR>")]
    [InlineData("SO", "[]", "<SO/>")]
    [InlineData("SetOfI", "[10,9,1]", "<SetOfI><INTEGER>10</INTEGER><INTEGER>1</INTEGER><INTEGER>9</INTEGER></SetOfI>")] // 9.7
    [InlineData("C", "{\"b\":\"hi\"}", "<C><b>hi</b></C>")]
    [InlineData("SC", "[{\"a\":1},{\"b\":\"x\"}]", "<SC><a>1</a><b>x</b></SC>")] // X.680 Table 5: CHOICE uses XMLValueList
    [InlineData("XMLThing", "5", "<_XMLThing>5</_XMLThing>")] // X.680 14.2
    public void CanonicalEncodings(string type, string value, string xml)
    {
        Assert.Equal(xml, EncodeText(Cxer, type, value));
        Assert.Equal(xml, EncodeText(Xer, type, value));
        var decoded = DecodeText(Cxer, type, xml);
        Assert.Equal(xml, EncodeText(Cxer, type, decoded));
    }

    [Theory]
    [InlineData("B", "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<B> <true /> </B>", "true")] // 8.2, 8.1.4
    [InlineData("I", "<I>\n  42\n</I>", "42")]
    [InlineData("R", "<R>12.5e-1</R>", "1.25")]
    [InlineData("BS", "<BS>1 0 1</BS>", "{\"hex\":\"A0\",\"length\":3}")] // X.680 12.11
    [InlineData("OS", "<OS>ab cd</OS>", "\"ABCD\"")] // X.680 12.13
    [InlineData("O", "<O>iso.member-body(2).840</O>", "\"1.2.840\"")] // X.680 32.3 name forms
    [InlineData("U8", "<U8>&#233;&#xEE;<![CDATA[<x>]]></U8>", "\"éî<x>\"")] // X.680 12.15.8
    [InlineData("S", "<S><c>x</c><b><true/></b></S>", "{\"b\":true,\"c\":\"x\",\"d\":7}")] // DEFAULT absent
    [InlineData("St", "<St><x>1</x><y><false/></y></St>", "{\"x\":1,\"y\":false}")]
    [InlineData("SetOfI", "<SetOfI><INTEGER>2</INTEGER><INTEGER>1</INTEGER></SetOfI>", "[2,1]")] // 9.7
    public void BasicDecoderAcceptsEncoderOptions(string type, string xml, string value)
    {
        Assert.Equal(Canon(System.Text.Json.Nodes.JsonNode.Parse(value)), Canon(System.Text.Json.Nodes.JsonNode.Parse(DecodeText(Xer, type, xml))));
        Assert.Equal(Asn1DecodeError.NonCanonical, Assert.Throws<Asn1DecodeException>(() => DecodeText(Cxer, type, xml)).Error);
    }

    private static string Canon(System.Text.Json.Nodes.JsonNode? node) => node switch
    {
        null => "null",
        System.Text.Json.Nodes.JsonObject o => "{" + string.Join(",", o.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + ":" + Canon(p.Value))) + "}",
        System.Text.Json.Nodes.JsonArray a => "[" + string.Join(",", a.Select(Canon)) + "]",
        _ => node.ToJsonString(),
    };

    [Theory]
    [InlineData("B", "<B>true</B>")] // 8.3.5: EmptyElementBoolean only
    [InlineData("B", "<X><true/></X>")] // wrong document element
    [InlineData("I", "<I>007</I>")] // leading zeros
    [InlineData("I", "<I>1.5</I>")]
    [InlineData("E", "<E><blue/></E>")]
    [InlineData("BS", "<BS>102</BS>")]
    [InlineData("OS", "<OS>ABC</OS>")]
    [InlineData("N", "<N>x</N>")]
    [InlineData("S", "<S><b><true/></b></S>")] // c missing
    [InlineData("S", "<S><b><true/></b><c>x</c><z/></S>")] // not extensible
    [InlineData("S", "<S><b><true/></b><b><true/></b><c>x</c></S>")] // duplicate
    [InlineData("SO", "<SO><REAL>1</REAL></SO>")] // wrong item name
    [InlineData("C", "<C><a>1</a><b>x</b></C>")]
    [InlineData("I", "<I a=\"1\">1</I>")] // attributes
    [InlineData("I", "<!-- c --><I>1</I>")] // comments (8.1.2)
    [InlineData("I", "<!DOCTYPE I [<!ENTITY x \"1\">]><I>&x;</I>")] // DTDs prohibited
    [InlineData("I", "<I>1</I><I>2</I>")]
    [InlineData("I", "<I>1")]
    [InlineData("U8", "<U8><foo/></U8>")]
    public void InvalidInputIsRejected(string type, string xml) =>
        Assert.Throws<Asn1DecodeException>(() => Xer.Decode(s_schema[type], Encoding.UTF8.GetBytes(xml)));

    [Fact]
    public void CanonicalEncodingRequiresCanonicalTimes()
    {
        Assert.Throws<Asn1EncodeException>(() => EncodeText(Cxer, "GT", "\"19920722132100.30Z\""));
        Assert.Equal("<GT>19920722132100.30Z</GT>", EncodeText(Xer, "GT", "\"19920722132100.30Z\""));
    }

    [Fact]
    public void UnknownExtensionsAreSkippedOrRelayed()
    {
        var v1 = new Schema("S ::= SEQUENCE { a BOOLEAN, ... }\nC ::= CHOICE { a BOOLEAN, ... }\nE ::= ENUMERATED { a, ... }");
        Assert.Equal("{\"a\":true}", Asn1.Dynamic.DynamicJson.Serialize(v1["S"], Cxer.Decode(v1["S"], Encoding.UTF8.GetBytes("<S><a><true/></a><b>x</b></S>")), indented: false));
        var decoded = Xer.Decode(v1["C"], Encoding.UTF8.GetBytes("<C><b><x>1</x></b></C>"));
        Assert.Null(Assert.IsType<Asn1.Dynamic.Asn1Choice>(decoded).Alternative);
        Assert.Equal("<C><b><x>1</x></b></C>", Encoding.UTF8.GetString(Xer.Encode(v1["C"], decoded)));
        Assert.StartsWith("\"#unknown-extension", Asn1.Dynamic.DynamicJson.Serialize(v1["E"], Xer.Decode(v1["E"], Encoding.UTF8.GetBytes("<E><b/></E>")), indented: false), StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidUtf8IsRejected() =>
        Assert.Equal(Asn1DecodeError.Malformed, Assert.Throws<Asn1DecodeException>(() => Xer.Decode(s_schema["U8"], "<U8>Ã(</U8>"u8.ToArray().Select(b => b == 0xC3 ? (byte)0xC3 : b).Concat(new byte[] { 0xC3 }).ToArray())).Error);
}
