using System.Text;
using Asn1.Dynamic;
using Asn1.Runtime;
using Asn1.Runtime.Xer;
using Xunit;

namespace Asn1.InteroperabilityTests;

/// <summary>
/// The worked example of ITU-T X.693 (02/2021) Annex A: the BASIC-XER (A.3) and CANONICAL-XER (A.4) representations of
/// John Smith's PersonnelRecord.
/// </summary>
public class X693AnnexATests
{
    private const string Smith = """
        {"name":{"givenName":"John","initial":"P","familyName":"Smith"},"title":"Director","number":51,"dateOfHire":"19710917",
         "nameOfSpouse":{"givenName":"Mary","initial":"T","familyName":"Smith"},
         "children":[{"name":{"givenName":"Ralph","initial":"T","familyName":"Smith"},"dateOfBirth":"19571111"},
                     {"name":{"givenName":"Susan","initial":"B","familyName":"Jones"},"dateOfBirth":"19590717"}]}
        """;

    private const string Basic = """
        <PersonnelRecord>
             <name>
                   <givenName>John</givenName>
                   <initial>P</initial>
                   <familyName>Smith</familyName>
             </name>
             <title>Director</title>
             <number>51</number>
             <dateOfHire>19710917</dateOfHire>
             <nameOfSpouse>
                   <givenName>Mary</givenName>
                   <initial>T</initial>
                   <familyName>Smith</familyName>
             </nameOfSpouse>
             <children>
                   <ChildInformation>
                         <name>
                              <givenName>Ralph</givenName>
                              <initial>T</initial>
                              <familyName>Smith</familyName>
                         </name>
                         <dateOfBirth>19571111</dateOfBirth>
                   </ChildInformation>
                   <ChildInformation>
                         <name>
                              <givenName>Susan</givenName>
                              <initial>B</initial>
                              <familyName>Jones</familyName>
                         </name>
                         <dateOfBirth>19590717</dateOfBirth>
                   </ChildInformation>
             </children>
        </PersonnelRecord>
        """;

    private const string Canonical =
        "<PersonnelRecord><name><givenName>John</givenName><initial>P</initial><familyName>Smith</familyName></name>" +
        "<number>51</number><title>Director</title><dateOfHire>19710917</dateOfHire><nameOfSpouse><givenName>Mary</givenName>" +
        "<initial>T</initial><familyName>Smith</familyName></nameOfSpouse><children><ChildInformation><name><givenName>Ralph</givenName>" +
        "<initial>T</initial><familyName>Smith</familyName></name><dateOfBirth>19571111</dateOfBirth></ChildInformation><ChildInformation>" +
        "<name><givenName>Susan</givenName><initial>B</initial><familyName>Jones</familyName></name><dateOfBirth>19590717</dateOfBirth>" +
        "</ChildInformation></children></PersonnelRecord>";

    private static Asn1Type<object?> Type() =>
        VectorSuite.Descriptor(new VectorSuite.VectorFile(["test-vectors/corpus/asn1tools/x691_a1.asn"], []), "X691-A1.PersonnelRecord");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CanonicalRepresentationIsReproduced(bool canonical)
    {
        var type = Type();
        var rule = canonical ? XerEncodingRule.Canonical : XerEncodingRule.Basic;
        Assert.Equal(Canonical, Encoding.UTF8.GetString(rule.Encode(type, DynamicJson.Parse(type, Smith))));
        var decoded = rule.Decode(type, Encoding.UTF8.GetBytes(Canonical));
        Assert.Equal(VectorSuite.Canonical(System.Text.Json.Nodes.JsonNode.Parse(Smith)), VectorSuite.Canonical(DynamicJson.Serialize(type, decoded)));
    }

    [Fact]
    public void BasicRepresentationIsDecoded()
    {
        var type = Type();
        var decoded = XerEncodingRule.Basic.Decode(type, Encoding.UTF8.GetBytes(Basic));
        Assert.Equal(VectorSuite.Canonical(System.Text.Json.Nodes.JsonNode.Parse(Smith)), VectorSuite.Canonical(DynamicJson.Serialize(type, decoded)));

        // White-space and the textual SET order are BASIC-XER options that CANONICAL-XER does not allow.
        var e = Assert.Throws<Asn1DecodeException>(() => XerEncodingRule.Canonical.Decode(type, Encoding.UTF8.GetBytes(Basic)));
        Assert.Equal(Asn1DecodeError.NonCanonical, e.Error);
    }
}
