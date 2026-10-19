using Asn1.Runtime.Oer;
using Xunit;

namespace Asn1.InteroperabilityTests;

/// <summary>
/// Known-answer tests against BASIC-OER encodings produced by asn1tools (byte-exact in both directions). The same
/// vectors are run through CANONICAL-OER: our encoder emits the canonical form for both profiles, and asn1tools'
/// encodings must also pass the strict COER decoder.
/// </summary>
public class OerVectorTests
{
    public static TheoryData<string> OerFiles() => [.. VectorSuite.Files("oer")];

    [Theory]
    [MemberData(nameof(OerFiles))]
    public void OerMatchesAsn1Tools(string name) => X690VectorTests.Run("oer", name, OerEncodingRule.Basic, exactEncoding: true);

    [Theory]
    [MemberData(nameof(OerFiles))]
    public void CoerMatchesAsn1Tools(string name) => X690VectorTests.Run("oer", name, OerEncodingRule.Canonical, exactEncoding: true);
}
