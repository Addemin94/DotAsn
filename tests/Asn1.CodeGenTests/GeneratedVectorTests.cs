using System.Text.Json.Nodes;
using Asn1.Runtime;
using Asn1.Runtime.Ber;
using Xunit;

namespace Asn1.CodeGenTests;

/// <summary>Runs the asn1tools DER vectors through generated types: decode then re-encode must reproduce the bytes.</summary>
public class GeneratedVectorTests
{
    public static TheoryData<string> DerFiles() =>
        [.. Directory.GetFiles(Path.Combine(GeneratedAssembly.Root, "test-vectors", "der"), "*.json").Select(f => Path.GetFileNameWithoutExtension(f)!).Order()];

    [Theory]
    [MemberData(nameof(DerFiles))]
    public void GeneratedTypesRoundTripDerVectors(string name)
    {
        var json = JsonNode.Parse(File.ReadAllText(Path.Combine(GeneratedAssembly.Root, "test-vectors", "der", name + ".json")))!;
        var specs = json["specs"]!.AsArray().Select(s => s!.GetValue<string>()).ToList();
        var assembly = GeneratedAssembly.For(specs);
        var failures = new List<string>();
        var count = 0;
        foreach (var c in json["cases"]!.AsArray())
        {
            var type = c!["type"]!.GetValue<string>();
            var hex = c["hex"]!.GetValue<string>();
            if (type == "X691-A4.Ax")
            {
                // asn1tools numbers automatic tags of root part 2 incorrectly (X.680 25.10); see ADR 0003.
                continue;
            }

            var descriptor = assembly.Descriptor(type);
            try
            {
                // Values that asn1tools encodes non-canonically (SET ordering) are first decoded with BER.
                var value = descriptor.DecodeBoxed(BerEncodingRule.Instance, Convert.FromHexString(hex));
                var der = Convert.ToHexString(descriptor.EncodeBoxed(DerEncodingRule.Instance, value));
                var again = descriptor.DecodeBoxed(DerEncodingRule.Instance, Convert.FromHexString(der));
                if (Unordered(type))
                {
                    // SET OF values are unordered: the canonical encoding must be stable instead.
                    if (Convert.ToHexString(descriptor.EncodeBoxed(DerEncodingRule.Instance, again)) != der)
                    {
                        failures.Add($"{type}: canonical DER is not stable");
                    }
                }
                else if (!Asn1ValueEquality.Equal(value, again))
                {
                    failures.Add($"{type}: value changed after a DER round trip: {value} vs {again}");
                }
                else if (der != hex && !Unordered(type))
                {
                    failures.Add($"{type}: {der} != {hex}");
                }

                Assert.NotNull(value);
                count++;
            }
            catch (Asn1Exception e)
            {
                failures.Add($"{type}: {e.Message}");
            }
        }

        Assert.True(failures.Count == 0, $"{failures.Count} of {count} failed:\n" + string.Join("\n", failures.Take(10)));
    }

    // Types for which asn1tools does not apply DER SET/SET OF ordering or X.680 25.10 tag numbering (see ADR 0003).
    private static bool Unordered(string type) => type.EndsWith("PersonnelRecord", StringComparison.Ordinal) || type.EndsWith(".S9", StringComparison.Ordinal) || false;
}
