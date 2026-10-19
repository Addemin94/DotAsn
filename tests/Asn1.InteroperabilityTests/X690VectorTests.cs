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
    /// Documented deviations of the reference implementation from the standards. Each is verified against the
    /// ITU-T text; the affected vectors are checked by value equivalence instead of byte equality, or skipped.
    /// </summary>
    internal static readonly Dictionary<(string Codec, string File, string Type), string> KnownDeviations = new()
    {
        // asn1tools numbers automatic tags in textual order; X.680 (2021) 25.10 c/d numbers both root parts first,
        // then the extension additions. Every tag-based encoding of these types therefore differs.
        [("ber", "asn1tools_x691_a4", "X691-A4.Ax")] = "X.680 25.10: automatic tags of root part 2 precede extension additions",
        [("der", "asn1tools_x691_a4", "X691-A4.Ax")] = "X.680 25.10: automatic tags of root part 2 precede extension additions",

        // asn1tools indexes CHOICE alternatives in textual order; X.691 23.2 uses the canonical tag order
        // (CHOICE { a INTEGER, b BOOLEAN } has b = 0 because UNIVERSAL 1 < UNIVERSAL 2).
        [("uper", "asn1tools_module_tags_explicit", "ModuleTagsExplicit.S8")] = "X.691 23.2 canonical CHOICE index order",
        [("uper", "asn1tools_module_tags_explicit", "ModuleTagsExplicit.S9")] = "X.691 23.2 canonical CHOICE index order",
        [("per", "asn1tools_module_tags_explicit", "ModuleTagsExplicit.S8")] = "X.691 23.2 canonical CHOICE index order",
        [("per", "asn1tools_module_tags_explicit", "ModuleTagsExplicit.S9")] = "X.691 23.2 canonical CHOICE index order",
        [("uper", "asn1tools_module_tags_implicit", "ModuleTagsImplicit.S8")] = "X.691 23.2 canonical CHOICE index order",
        [("uper", "asn1tools_module_tags_implicit", "ModuleTagsImplicit.S9")] = "X.691 23.2 canonical CHOICE index order",
        [("per", "asn1tools_module_tags_implicit", "ModuleTagsImplicit.S8")] = "X.691 23.2 canonical CHOICE index order",
        [("per", "asn1tools_module_tags_implicit", "ModuleTagsImplicit.S9")] = "X.691 23.2 canonical CHOICE index order",

        // zforce's notificationMessage CHOICE has APPLICATION and context tags out of textual order (X.691 23.2).
        [("uper", "derived_zforce_not_implied", "ProtocolMessage.ProtocolMessage")] = "X.691 23.2 canonical CHOICE index order",
        [("uper", "derived_zforce_not_implied", "ProtocolMessage.Notification")] = "X.691 23.2 canonical CHOICE index order",
        [("per", "derived_zforce_not_implied", "ProtocolMessage.ProtocolMessage")] = "X.691 23.2 canonical CHOICE index order",
        [("per", "derived_zforce_not_implied", "ProtocolMessage.Notification")] = "X.691 23.2 canonical CHOICE index order",

        // asn1tools ignores EXTENSIBILITY IMPLIED for ENUMERATED; X.680 13.4 inserts the marker into every type that permits one.
        [("uper", "asn1tools_zforce", "*")] = "X.680 13.4 EXTENSIBILITY IMPLIED applies to ENUMERATED",
        [("per", "asn1tools_zforce", "*")] = "X.680 13.4 EXTENSIBILITY IMPLIED applies to ENUMERATED",

        // Name.initial is NameString (SIZE(1)): the effective size constraint is fixed, so X.696 27.2 omits the length
        // determinant (the same analysis reproduces the published X.691 A.2/A.3 PER encodings); asn1tools emits one.
        // Measurement has the extension addition group [[ pressurePa ... OPTIONAL ]]. X.696 16.5.2 encodes a group as a
        // sequence in an open type, so it starts with a presence bitmap for its OPTIONAL components; asn1tools' OER
        // omits that preamble (its UPER/APER encodings include the PER equivalent and match DotAsn exactly).
        [("oer", "telemetry", "Telemetry-Protocol.Measurement")] = "X.696 16.5.2 extension addition group preamble",
        [("oer", "telemetry", "Telemetry-Protocol.MeasurementBatch")] = "X.696 16.5.2 extension addition group preamble",
        [("oer", "telemetry", "Telemetry-Protocol.Payload")] = "X.696 16.5.2 extension addition group preamble",
        [("oer", "telemetry", "Telemetry-Protocol.Message")] = "X.696 16.5.2 extension addition group preamble",
        [("oer", "asn1tools_x691_a2", "*")] = "X.696 27.2 fixed-size known-multiplier string has no length determinant",
        [("oer", "asn1tools_x691_a3", "*")] = "X.696 27.2 fixed-size known-multiplier string has no length determinant",
    };

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

    internal static void Run(string codec, string name, Asn1EncodingRule rule, bool exactEncoding)
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
                    var t = VectorSuite.Descriptor(file, c.Type);
                    var v = DynamicJson.Parse(t, c.Value.ToJsonString());
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
                var type = VectorSuite.Descriptor(file, c.Type);
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
                    var oursBytes = rule.Encode(type, DynamicJson.Parse(type, c.Value.ToJsonString()));
                    rule.Decode(type, oursBytes);
                    if (canonicalTheirs != Convert.ToHexString(oursBytes))
                    {
                        failures.Add($"{c.Type}: canonical DER differs: {canonicalTheirs} vs {Convert.ToHexString(oursBytes)}");
                    }

                    continue;
                }

                var value = DynamicJson.Parse(type, c.Value.ToJsonString());
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
                    failures.Add($"{c.Type} {c.Value.ToJsonString()}: encoded {encoded}, expected {c.Hex}");
                    continue;
                }

                // Decode the reference encoding and compare with asn1tools' own decoding.
                var decoded = rule.Decode(type, Convert.FromHexString(c.Hex.Length == 0 && rule is PerEncodingRule ? "00" : c.Hex));
                var expected = VectorSuite.Canonical(c.Decoded ?? c.Value);
                var actual = VectorSuite.Canonical(DynamicJson.Serialize(type, decoded));
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
                failures.Add($"{c.Type} {c.Value.ToJsonString()}: {e.GetType().Name}: {e.Message}");
            }
        }

        Assert.True(failures.Count == 0, $"{failures.Count}/{file.Cases.Count} failed ({exact} exact):\n" + string.Join("\n", failures.Take(15)));
    }
}
