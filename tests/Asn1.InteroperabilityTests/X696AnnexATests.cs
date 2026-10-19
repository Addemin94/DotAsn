using Asn1.Dynamic;
using Asn1.Runtime.Oer;
using Xunit;

namespace Asn1.InteroperabilityTests;

/// <summary>
/// The worked example of ITU-T X.696 (02/2021) Annex A: the published 95-octet encoding of John Smith's
/// PersonnelRecord, which X.696 A.3 states is identical in BASIC-OER and CANONICAL-OER.
/// </summary>
public class X696AnnexATests
{
    private const string Smith = """
        {"name":{"givenName":"John","initial":"P","familyName":"Smith"},"title":"Director","number":51,"dateOfHire":"19710917",
         "nameOfSpouse":{"givenName":"Mary","initial":"T","familyName":"Smith"},
         "children":[{"name":{"givenName":"Ralph","initial":"T","familyName":"Smith"},"dateOfBirth":"19571111"},
                     {"name":{"givenName":"Susan","initial":"B","familyName":"Jones"},"dateOfBirth":"19590717"}]}
        """;

    private const string Expected =
        "80044A6F686E015005536D6974680133084469726563746F72083139373130393137044D617279015405536D697468" +
        "01020552616C7068015405536D69746808313935373131313105537573616E0142054A6F6E6573083139353930373137";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PersonnelRecordEncodingIsReproduced(bool canonical)
    {
        var file = new VectorSuite.VectorFile(["test-vectors/corpus/asn1tools/x691_a1.asn"], []);
        var type = VectorSuite.Descriptor(file, "X691-A1.PersonnelRecord");
        var rule = canonical ? OerEncodingRule.Canonical : OerEncodingRule.Basic;
        var value = DynamicJson.Parse(type, Smith);
        var encoded = rule.Encode(type, value);
        Assert.Equal(95, encoded.Length);
        Assert.Equal(Expected, Convert.ToHexString(encoded));
        var decoded = rule.Decode(type, Convert.FromHexString(Expected));
        Assert.Equal(VectorSuite.Canonical(Smith), VectorSuite.Canonical(DynamicJson.Serialize(type, decoded)));
    }
}
