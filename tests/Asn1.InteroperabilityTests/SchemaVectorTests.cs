using Asn1.Runtime;
using Asn1.Runtime.Ber;
using Asn1.Runtime.Jer;
using Asn1.Runtime.Oer;
using Asn1.Runtime.Per;
using Asn1.Runtime.Xer;
using Xunit;

namespace Asn1.InteroperabilityTests;

/// <summary>
/// Runs every asn1tools vector through descriptors loaded from a DotAsn schema (<see cref="Asn1.Dynamic.Asn1Schema"/>)
/// instead of the compiler: the path of the WebAssembly runtime. Results must be identical to the compiler path.
/// </summary>
public class SchemaVectorTests
{
    public static TheoryData<string, string> Files()
    {
        var data = new TheoryData<string, string>();
        foreach (var codec in new[] { "ber", "der", "uper", "per", "oer", "coer", "jer", "xer" })
        {
            foreach (var name in VectorSuite.Files(codec == "coer" ? "oer" : codec))
            {
                data.Add(codec, name);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void SchemaDescriptorsMatchAsn1Tools(string codec, string name)
    {
        (string Dir, Asn1EncodingRule Rule, bool Exact) suite = codec switch
        {
            "ber" => ("ber", BerEncodingRule.Instance, false),
            "der" => ("der", DerEncodingRule.Instance, true),
            "uper" => ("uper", PerEncodingRule.Unaligned, true),
            "per" => ("per", PerEncodingRule.Aligned, true),
            "oer" => ("oer", OerEncodingRule.Basic, true),
            "coer" => ("oer", OerEncodingRule.Canonical, true),
            "jer" => ("jer", JerEncodingRule.Instance, false),
            _ => ("xer", XerEncodingRule.Basic, false),
        };
        X690VectorTests.Run(suite.Dir, name, suite.Rule, suite.Exact, viaSchema: true);
    }
}
