using Asn1.Runtime.Xer;
using Xunit;

namespace Asn1.InteroperabilityTests;

/// <summary>
/// Known-answer tests against BASIC-XER encodings produced by asn1tools, compared as normalised XML trees. DotAsn
/// always writes the CANONICAL-XER form, which differs from asn1tools only in sender's options: SET components in
/// canonical tag order (X.693 9.6) and DEFAULT values written out (9.5). Those vectors are checked by decoding
/// asn1tools' text to the expected value and by round-tripping DotAsn's text.
/// </summary>
public class XerVectorTests
{
    public static TheoryData<string> XerFiles() => [.. VectorSuite.Files("xer")];

    [Theory]
    [MemberData(nameof(XerFiles))]
    public void XerMatchesAsn1Tools(string name) => X690VectorTests.Run("xer", name, XerEncodingRule.Basic, exactEncoding: false);
}
