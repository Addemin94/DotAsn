using System.Text.Json.Nodes;
using Asn1.Runtime;
using Asn1.Runtime.Jer;
using Asn1.Runtime.Oer;
using Asn1.Runtime.Per;
using Asn1.Runtime.Xer;
using Xunit;

namespace Asn1.CodeGenTests;

/// <summary>
/// Generated types must decode the asn1tools UPER/APER/OER vectors and re-encode them bit-exactly, and decode the JER
/// and XER vectors and re-encode them to text that decodes to an equal value (JER and BASIC-XER texts are not unique).
/// </summary>
public class GeneratedPerOerVectorTests
{
    // Types where asn1tools deviates from X.691/X.680 (see ADR 0003); not comparable byte for byte.
    private static readonly HashSet<string> s_deviations =
    [
        "ModuleTagsExplicit.S8", "ModuleTagsExplicit.S9", "ModuleTagsImplicit.S8", "ModuleTagsImplicit.S9",
    ];

    public static TheoryData<string, string> Files()
    {
        var data = new TheoryData<string, string>();
        foreach (var codec in new[] { "uper", "per", "oer", "jer", "xer" })
        {
            foreach (var f in Directory.GetFiles(Path.Combine(GeneratedAssembly.Root, "test-vectors", codec), "*.json").Order())
            {
                var name = Path.GetFileNameWithoutExtension(f)!;
                // zforce: EXTENSIBILITY IMPLIED on ENUMERATED (PER only); x691_a2/a3: fixed-size Name.initial (OER only).
                if (name != "asn1tools_zforce" && !(codec == "oer" && name is "asn1tools_x691_a2" or "asn1tools_x691_a3"))
                {
                    data.Add(codec, name);
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void GeneratedTypesRoundTripVectors(string codec, string name)
    {
        Asn1EncodingRule rule = codec switch
        {
            "uper" => PerEncodingRule.Unaligned,
            "per" => PerEncodingRule.Aligned,
            "jer" => JerEncodingRule.Instance,
            "xer" => XerEncodingRule.Basic,
            _ => OerEncodingRule.Basic,
        };
        var json = JsonNode.Parse(File.ReadAllText(Path.Combine(GeneratedAssembly.Root, "test-vectors", codec, name + ".json")))!;
        var assembly = GeneratedAssembly.For([.. json["specs"]!.AsArray().Select(s => s!.GetValue<string>())]);
        var failures = new List<string>();
        foreach (var c in json["cases"]!.AsArray())
        {
            var type = c!["type"]!.GetValue<string>();
            if ((s_deviations.Contains(type) && codec is "uper" or "per") ||
                (codec == "oer" && name == "telemetry" && type is "Telemetry-Protocol.Measurement" or "Telemetry-Protocol.MeasurementBatch" or "Telemetry-Protocol.Payload" or "Telemetry-Protocol.Message") || (name == "derived_zforce_not_implied" && type is "ProtocolMessage.ProtocolMessage" or "ProtocolMessage.Notification"))
            {
                continue;
            }

            var hex = c["hex"]!.GetValue<string>();
            hex = hex.Length == 0 && codec is "uper" or "per" ? "00" : hex;
            var descriptor = assembly.Descriptor(type);
            try
            {
                var value = descriptor.DecodeBoxed(rule, Convert.FromHexString(hex));
                var again = Convert.ToHexString(descriptor.EncodeBoxed(rule, value));
                if (codec == "jer")
                {
                    if (!Asn1ValueEquality.Equal(value, descriptor.DecodeBoxed(rule, Convert.FromHexString(again))))
                    {
                        failures.Add($"{type}: re-encoded {codec} decodes to a different value");
                    }
                }
                else if (codec == "xer")
                {
                    // The XER encoder sorts SET OF elements (X.693 9.7), so compare the stable canonical text instead.
                    if (Convert.ToHexString(descriptor.EncodeBoxed(rule, descriptor.DecodeBoxed(rule, Convert.FromHexString(again)))) != again)
                    {
                        failures.Add($"{type}: re-encoded {codec} is not stable");
                    }
                }
                else if (again != hex)
                {
                    failures.Add($"{type}: {again} != {hex}");
                }
            }
            catch (Asn1Exception e)
            {
                failures.Add($"{type}: {e.Message}");
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(10)));
    }
}
