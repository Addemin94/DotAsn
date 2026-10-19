using System.Text.Json.Nodes;
using Asn1.Runtime;

namespace Asn1.CodeGenTests;

/// <summary>
/// The documented deviations of asn1tools from the standards (test-vectors/deviations.json, shared with the
/// interoperability tests and the WebAssembly runtime tests). Such vectors are not comparable byte for byte.
/// </summary>
internal static class VectorDeviations
{
    private static readonly HashSet<(string Codec, string File, string Type)> s_entries = Load();

    private static HashSet<(string, string, string)> Load()
    {
        var json = JsonNode.Parse(File.ReadAllText(Path.Combine(GeneratedAssembly.Root, "test-vectors", "deviations.json")))!;
        return [.. json["deviations"]!.AsArray().Select(d => (d!["codec"]!.GetValue<string>(), d["file"]!.GetValue<string>(), d["type"]!.GetValue<string>()))];
    }

    public static bool Contains(string codec, string file, string type) =>
        s_entries.Contains((codec, file, type)) || s_entries.Contains((codec, file, "*"));

    /// <summary>True if a type (transitively) contains SET or SET OF, whose canonical orders asn1tools does not apply.</summary>
    public static bool ContainsSet(Asn1Type type, HashSet<Asn1Type>? seen = null)
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
}
