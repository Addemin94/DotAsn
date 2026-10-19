using Asn1.Runtime.Per;
using Xunit;

namespace Asn1.InteroperabilityTests;

/// <summary>Known-answer tests against UPER and APER encodings produced by asn1tools (bit-exact in both directions).</summary>
public class PerVectorTests
{
    public static TheoryData<string> UperFiles() => [.. VectorSuite.Files("uper")];

    public static TheoryData<string> AperFiles() => [.. VectorSuite.Files("per")];

    [Theory]
    [MemberData(nameof(UperFiles))]
    public void UperMatchesAsn1Tools(string name) => X690VectorTests.Run("uper", name, PerEncodingRule.Unaligned, exactEncoding: true);

    [Theory]
    [MemberData(nameof(AperFiles))]
    public void AperMatchesAsn1Tools(string name) => X690VectorTests.Run("per", name, PerEncodingRule.Aligned, exactEncoding: true);
}
