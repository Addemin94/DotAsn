using Asn1.Dynamic;
using Asn1.Runtime.Per;
using Xunit;

namespace Asn1.InteroperabilityTests;

/// <summary>
/// The worked examples of ITU-T X.691 (02/2021) Annex A: the published ALIGNED and UNALIGNED encodings of the
/// PersonnelRecord (A.1 without constraints, A.2 with constraints, A.3 with extension markers) and of Ax (A.4,
/// extension addition groups). These are authoritative known answers independent of any implementation.
/// </summary>
public class X691AnnexATests
{
    private const string Smith = """
        {"name":{"givenName":"John","initial":"P","familyName":"Smith"},"title":"Director","number":51,"dateOfHire":"19710917",
         "nameOfSpouse":{"givenName":"Mary","initial":"T","familyName":"Smith"},
         "children":[{"name":{"givenName":"Ralph","initial":"T","familyName":"Smith"},"dateOfBirth":"19571111"},
                     {"name":{"givenName":"Susan","initial":"B","familyName":"Jones"},"dateOfBirth":"19590717"}]}
        """;

    private const string SmithWithSex = """
        {"name":{"givenName":"John","initial":"P","familyName":"Smith"},"title":"Director","number":51,"dateOfHire":"19710917",
         "nameOfSpouse":{"givenName":"Mary","initial":"T","familyName":"Smith"},
         "children":[{"name":{"givenName":"Ralph","initial":"T","familyName":"Smith"},"dateOfBirth":"19571111"},
                     {"name":{"givenName":"Susan","initial":"B","familyName":"Jones"},"dateOfBirth":"19590717","sex":"female"}]}
        """;

    public static TheoryData<string, string, string, bool, string> Examples() => new()
    {
        { "A.1.3", "x691_a1", Smith, true, "80044A6F686E015005536D6974680133084469726563746F72083139373130393137044D617279015405536D697468020552616C7068015405536D69746808313935373131313105537573616E0142054A6F6E6573083139353930373137" },
        { "A.1.4", "x691_a1", Smith, false, "824ADFA3700D005A7B74F4D0026611134F2CB8FA6FE410C5CB762C1CB16E09370F2F20350169EDD3D340102D2C3B386801A80B4F6E9E9A0218B96ADD8B162C4169F5E787700C20595BF765E610C5CB572C1BB16E" },
        { "A.2.3", "x691_a2", Smith, true, "864A6F686E5010536D6974680133084469726563746F72197109170C4D6172795410536D697468021052616C70685410536D6974681957111110537573616E42104A6F6E657319590717" },
        { "A.2.4", "x691_a2", Smith, false, "865D51D2888A5125F180998444D3CB2E3E9BF90CB8848B867396E8A88A5125F181089B93D71AA2294497C632AE222222985CE521885D54C170CAC838B8" },
        { "A.3.3", "x691_a3", SmithWithSex, true, "40C04A6F686E5008536D697468000033084469726563746F720019710917034D6172795408536D697468010052616C70685408536D69746800195711118200537573616E42084A6F6E65730019590717010140" },
        { "A.3.4", "x691_a3", SmithWithSex, false, "40CBAA3A5108A5125F180330889A7965C7D37F20CB8848B819CE5BA2A114A24BE30113727AE3542294497C619571111822985CE521842EAA60B832B20E2E020280" },
        { "A.4.3", "x691_a4", """{"a":253,"b":true,"c":{"e":true},"g":"123","h":true}""", true, "9E000180010291A4" },
        { "A.4.4", "x691_a4", """{"a":253,"b":true,"c":{"e":true},"g":"123","h":true}""", false, "9E000600040A4690" },
    };

    [Theory]
    [MemberData(nameof(Examples))]
    public void AnnexAEncodingsAreReproduced(string clause, string spec, string json, bool aligned, string expected)
    {
        var file = new VectorSuite.VectorFile([$"test-vectors/corpus/asn1tools/{spec}.asn"], []);
        var typeName = spec == "x691_a4" ? "X691-A4.Ax" : $"X691-A{spec[^1]}.PersonnelRecord";
        var type = VectorSuite.Descriptor(file, typeName);
        var rule = aligned ? PerEncodingRule.Aligned : PerEncodingRule.Unaligned;
        var value = DynamicJson.Parse(type, json);
        Assert.True(expected == Convert.ToHexString(rule.Encode(type, value)), clause);
        var decoded = rule.Decode(type, Convert.FromHexString(expected));
        Assert.Equal(VectorSuite.Canonical(json), VectorSuite.Canonical(DynamicJson.Serialize(type, decoded)));
    }
}
