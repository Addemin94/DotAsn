using Asn1.Runtime.Jer;
using Xunit;

namespace Asn1.InteroperabilityTests;

/// <summary>
/// Known-answer tests against JER encodings produced by asn1tools. JSON texts are compared as canonicalised JSON
/// trees, because member order, white space and escapes are sender's options (X.697 27.3.3, 7.6.3). The only
/// remaining differences are DEFAULT components, which asn1tools writes when the input value has them and DotAsn
/// omits (both are valid JER); those vectors are checked by decoding asn1tools' text to the expected value and by
/// round-tripping DotAsn's text.
/// </summary>
public class JerVectorTests
{
    public static TheoryData<string> JerFiles() => [.. VectorSuite.Files("jer")];

    [Theory]
    [MemberData(nameof(JerFiles))]
    public void JerMatchesAsn1Tools(string name) => X690VectorTests.Run("jer", name, JerEncodingRule.Instance, exactEncoding: false);
}
