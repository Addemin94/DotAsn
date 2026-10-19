using Asn1.Diagnostics;
using Asn1.Semantics;
using Xunit;

namespace Asn1.SemanticTests;

/// <summary>
/// Compiles large third-party specifications (3GPP RRC/S1AP/LPP, ETSI ITS, OMA ULP, IEEE 1609.2, CEN DSRC) fetched by
/// scripts/fetch-extended-corpus.sh. Skipped when the corpus has not been fetched.
/// </summary>
public class ExtendedCorpusTests
{
    private static readonly string s_root = FindRoot();

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Asn1Toolchain.sln")))
            {
                return Path.Combine(dir.FullName, ".cache", "asn1tools", "tests", "files");
            }
        }

        return string.Empty;
    }

    public static TheoryData<string[]> Groups() => new()
    {
        new[] { "3gpp/rrc_8_6_0.asn" },
        new[] { "3gpp/rrc_14_4_0.asn" },
        new[] { "3gpp/s1ap_14_4_0.asn" },
        new[] { "3gpp/lpp_14_3_0.asn" },
        new[] { "etsi/cam_pdu_descriptions_1_3_2.asn", "etsi/its_container_1_2_1.asn" },
        new[] { "oma/ulp.asn" },
        new[] { "ieee/ieee1609_2.asn" },
        new[] { "cen/dsrc.asn", "cen/electronic_registration_identification_vehicle_data_module.asn", "etsi/its_container_1_2_1.asn" },
    };

    [Theory]
    [MemberData(nameof(Groups))]
    public void CompilesWithoutErrors(string[] files)
    {
        Assert.SkipUnless(Directory.Exists(s_root), "extended corpus not fetched (run scripts/fetch-extended-corpus.sh)");
        var compilation = Asn1Compilation.Create(files.Select(f => new SourceText(f, File.ReadAllText(Path.Combine(s_root, f)))));
        var errors = compilation.Diagnostics.Where(d => d.IsError && d.Id != "ASN2004").Select(d => d.ToString()).ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors.Take(20)));
    }
}
