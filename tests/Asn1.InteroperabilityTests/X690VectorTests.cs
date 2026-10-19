using Asn1.Dynamic;
using Asn1.Runtime;
using Asn1.Runtime.Ber;
using Asn1.Runtime.Oer;
using Asn1.Runtime.Per;
using Xunit;

namespace Asn1.InteroperabilityTests;

/// <summary>Known-answer tests against BER/DER encodings produced by asn1tools.</summary>
public class X690VectorTests
{
    public static TheoryData<string> DerFiles() => [.. VectorSuite.Files("der")];

    public static TheoryData<string> BerFiles() => [.. VectorSuite.Files("ber")];

    [Theory]
    [MemberData(nameof(DerFiles))]
    public void DerMatchesAsn1Tools(string name) => Run("der", name, DerEncodingRule.Instance, exactEncoding: true);

    [Theory]
    [MemberData(nameof(BerFiles))]
    public void BerInteroperatesWithAsn1Tools(string name) => Run("ber", name, BerEncodingRule.Instance, exactEncoding: false);

    /// <summary>
    /// Documented deviations of the reference implementation from the standards (test-vectors/deviations.json, shared
    /// with the WebAssembly runtime tests). Each is verified against the ITU-T text; the affected vectors are checked by
    /// round trip of our own encoding instead of byte equality. Keys are (codec, file, type); "*" matches every type.
    /// </summary>
    internal static readonly Dictionary<(string Codec, string File, string Type), string> KnownDeviations = LoadDeviations();

    private static Dictionary<(string Codec, string File, string Type), string> LoadDeviations()
    {
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(VectorSuite.Root, "test-vectors", "deviations.json")))!;
        return json["deviations"]!.AsArray().ToDictionary(
            d => (d!["codec"]!.GetValue<string>(), d["file"]!.GetValue<string>(), d["type"]!.GetValue<string>()),
            d => d!["reason"]!.GetValue<string>());
    }

    internal static bool IsDeviation(string codec, string file, string type) =>
        KnownDeviations.ContainsKey((codec, file, type)) || KnownDeviations.ContainsKey((codec, file, "*"));

    /// <summary>True if the type (transitively) contains SET or SET OF, whose DER ordering asn1tools does not apply (X.690 10.3, 11.6).</summary>
    internal static bool ContainsSet(Asn1Type type, HashSet<Asn1Type>? seen = null)
    {
        seen ??= new HashSet<Asn1Type>(ReferenceEqualityComparer.Instance);
        if (!seen.Add(type))
        {
            return false;
        }

        if (type.Kind is Asn1TypeKind.Set or Asn1TypeKind.SetOf)
        {
            return true;
        }

        return type switch
        {
            IStructuredTypeInfo s => s.ComponentInfos.Any(c => ContainsSet(c.Type, seen)),
            ICollectionTypeInfo c => ContainsSet(c.ElementTypeInfo, seen),
            _ => false,
        };
    }

    private static bool SameText(Asn1EncodingRule rule, string hexA, string hexB)
    {
        static string Json(string hex) => VectorSuite.Canonical(System.Text.Json.Nodes.JsonNode.Parse(Convert.FromHexString(hex)));
        static string Xml(string hex) => System.Xml.Linq.XElement.Parse(System.Text.Encoding.UTF8.GetString(Convert.FromHexString(hex)), System.Xml.Linq.LoadOptions.PreserveWhitespace).ToString(System.Xml.Linq.SaveOptions.DisableFormatting);
        return rule.Name == "JER" ? Json(hexA) == Json(hexB) : Xml(hexA) == Xml(hexB);
    }

    internal static void Run(string codec, string name, Asn1EncodingRule rule, bool exactEncoding, bool viaSchema = false)
    {
        var file = VectorSuite.Load(codec, name);
        var failures = new List<string>();
        var exact = 0;
        foreach (var c in file.Cases)
        {
            if (IsDeviation(codec, name, c.Type))
            {
                // Not comparable with the reference; our own encoding must still round-trip to the same value.
                try
                {
                    var t = viaSchema ? VectorSuite.SchemaDescriptor(file, c.Type) : VectorSuite.Descriptor(file, c.Type);
                    var v = DynamicJson.Parse(t, c.ValueJson);
                    // Compare re-encodings rather than JSON: an absent DEFAULT component decodes as its explicit default.
                    var bytes = Convert.ToHexString(rule.Encode(t, v));
                    var decoded = rule.Decode(t, Convert.FromHexString(bytes));
                    var again = Convert.ToHexString(rule.Encode(t, decoded));
                    if (bytes != again)
                    {
                        failures.Add($"{c.Type}: round trip changed the value: {bytes} re-encoded as {again} ({DynamicJson.Serialize(t, decoded)})");
                    }
                }
                catch (Exception e) when (e is Asn1Exception or Asn1JsonException)
                {
                    failures.Add($"{c.Type} (deviation case): {e.Message}");
                }

                continue;
            }

            try
            {
                var type = viaSchema ? VectorSuite.SchemaDescriptor(file, c.Type) : VectorSuite.Descriptor(file, c.Type);
                if ((exactEncoding && (codec == "der" || rule == OerEncodingRule.Canonical) || codec == "xer") && ContainsSet(type))
                {
                    // asn1tools leaves SET/SET OF in textual order (X.690 10.3/11.6, X.696 31.8, X.693 9.6/9.7): compare values, and
                    // require that our encoding is canonical (accepted by the strict decoder) and decodes to the same value.
                    // Abstract SET OF values are unordered, so compare the canonical encodings of both values.
                    var lenient = codec switch
                    {
                        "der" => (Asn1EncodingRule)BerEncodingRule.Instance,
                        "xer" => rule,
                        _ => OerEncodingRule.Basic,
                    };
                    var theirs = lenient.Decode(type, Convert.FromHexString(c.Hex));
                    var canonicalTheirs = Convert.ToHexString(rule.Encode(type, theirs));
                    var oursBytes = rule.Encode(type, DynamicJson.Parse(type, c.ValueJson));
                    rule.Decode(type, oursBytes);
                    if (canonicalTheirs != Convert.ToHexString(oursBytes))
                    {
                        failures.Add($"{c.Type}: canonical DER differs: {canonicalTheirs} vs {Convert.ToHexString(oursBytes)}");
                    }

                    continue;
                }

                var value = DynamicJson.Parse(type, c.ValueJson);
                var encoded = Convert.ToHexString(rule.Encode(type, value));
                if (!rule.IsBinary && encoded != c.Hex && SameText(rule, encoded, c.Hex))
                {
                    // JER/XER: white space, escapes and (JER) member order are sender's options (X.697 27.3.3, 7.6.3; X.693 8.1.4).
                    exact++;
                }
                else if (encoded == c.Hex || (c.Hex.Length == 0 && encoded == "00" && rule is PerEncodingRule))
                {
                    // X.691 11.1.3.1: an empty PER encoding is replaced by one zero octet (asn1tools emits nothing).
                    exact++;
                }
                else if (exactEncoding)
                {
                    failures.Add($"{c.Type} {c.ValueJson}: encoded {encoded}, expected {c.Hex}");
                    continue;
                }

                // Decode the reference encoding and compare with asn1tools' own decoding.
                var decoded = rule.Decode(type, Convert.FromHexString(c.Hex.Length == 0 && rule is PerEncodingRule ? "00" : c.Hex));
                // Our decoding must equal asn1tools' own decoding (which adds DEFAULT values) or the original value
                // (asn1tools decodes some values wrongly, e.g. OBJECT IDENTIFIER 2.x with x >= 40, X.690 8.19.4).
                var expected = VectorSuite.Canonical(c.Decoded ?? c.Value);
                var actual = VectorSuite.Canonical(DynamicJson.Serialize(type, decoded));
                if (expected != actual && VectorSuite.Canonical(c.Value) == actual)
                {
                    expected = actual;
                }

                if (expected != actual)
                {
                    failures.Add($"{c.Type}: decoded {actual}, expected {expected}");
                    continue;
                }

                // Our own encoding must decode to the same value.
                var roundTrip = VectorSuite.Canonical(DynamicJson.Serialize(type, rule.Decode(type, Convert.FromHexString(encoded))));
                if (roundTrip != expected)
                {
                    failures.Add($"{c.Type}: round trip {roundTrip}, expected {expected}");
                }
            }
            catch (Exception e) when (e is Asn1Exception or Asn1JsonException or InvalidOperationException or NotSupportedException or FormatException)
            {
                failures.Add($"{c.Type} {c.ValueJson}: {e.GetType().Name}: {e.Message}");
            }
        }

        Assert.True(failures.Count == 0, $"{failures.Count}/{file.Cases.Count} failed ({exact} exact):\n" + string.Join("\n", failures.Take(15)));
    }
}
