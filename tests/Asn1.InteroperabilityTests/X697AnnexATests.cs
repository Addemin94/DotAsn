using System.Text;
using System.Text.Json.Nodes;
using Asn1.Dynamic;
using Asn1.Runtime.Jer;
using Xunit;

namespace Asn1.InteroperabilityTests;

/// <summary>
/// The worked example of ITU-T X.697 (02/2021) Annex A.3: the JER representation of John Smith's PersonnelRecord.
/// JSON texts are compared as canonicalised trees (member order and white space are sender's options).
/// </summary>
public class X697AnnexATests
{
    private const string Smith = """
        {"name":{"givenName":"John","initial":"P","familyName":"Smith"},"title":"Director","number":51,"dateOfHire":"19710917",
         "nameOfSpouse":{"givenName":"Mary","initial":"T","familyName":"Smith"},
         "children":[{"name":{"givenName":"Ralph","initial":"T","familyName":"Smith"},"dateOfBirth":"19571111"},
                     {"name":{"givenName":"Susan","initial":"B","familyName":"Jones"},"dateOfBirth":"19590717"}]}
        """;

    // X.697 A.3, verbatim except that the published text omits the quotation marks around the second "name".
    private const string Published = """
        {
          "name" : { "givenName" : "John", "initial" : "P", "familyName" : "Smith" },
          "title" : "Director",
          "number" : 51,
          "dateOfHire" : "19710917",
          "nameOfSpouse" : { "givenName" : "Mary", "initial" : "T", "familyName" : "Smith" },
          "children" : [
            { "name" : { "givenName" : "Ralph", "initial" : "T", "familyName" : "Smith" }, "dateOfBirth": "19571111" },
            { "name" : { "givenName" : "Susan", "initial" : "B", "familyName" : "Jones" }, "dateOfBirth" : "19590717" }
          ]
        }
        """;

    [Fact]
    public void PersonnelRecordRepresentationIsReproduced()
    {
        var file = new VectorSuite.VectorFile(["test-vectors/corpus/asn1tools/x691_a1.asn"], []);
        var type = VectorSuite.Descriptor(file, "X691-A1.PersonnelRecord");
        var encoded = Encoding.UTF8.GetString(JerEncodingRule.Instance.Encode(type, DynamicJson.Parse(type, Smith)));
        Assert.Equal(VectorSuite.Canonical(JsonNode.Parse(Published)), VectorSuite.Canonical(JsonNode.Parse(encoded)));
        var decoded = JerEncodingRule.Instance.Decode(type, Encoding.UTF8.GetBytes(Published));
        Assert.Equal(VectorSuite.Canonical(JsonNode.Parse(Smith)), VectorSuite.Canonical(DynamicJson.Serialize(type, decoded)));
    }
}
