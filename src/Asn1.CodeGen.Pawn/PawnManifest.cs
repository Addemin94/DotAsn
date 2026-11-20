using System.Text;
using System.Text.Json;
using Asn1.Semantics;

namespace Asn1.CodeGen.Pawn;

/// <summary>
/// Writes the JSON description of the generated value layouts, so tools can fill and read a layout without parsing
/// Pawn: cell offsets, string and list bounds, alternatives and enumeration values.
/// </summary>
internal static class PawnManifest
{
    public static string Write(IEnumerable<ModuleSymbol> modules, IReadOnlyDictionary<TypeAssignmentSymbol, PawnTypeEntry?> entries, string version)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject();
            json.WriteString("generator", "DotAsn " + version);
            json.WriteString("encoding", "COER");
            json.WriteStartObject("types");
            foreach (var module in modules)
            {
                foreach (var assignment in module.TypeAssignments)
                {
                    if (entries.GetValueOrDefault(assignment) is not { } entry)
                    {
                        continue;
                    }

                    json.WriteStartObject(entry.AsnName);
                    json.WriteString("prefix", entry.Prefix);
                    json.WriteString("file", PawnGenerator.FileName(module));
                    json.WriteString("encode", entry.EncodeName);
                    json.WriteString("decode", entry.DecodeName);
                    json.WriteNumber("cells", entry.Layout!.Cells);
                    json.WritePropertyName("layout");
                    Layout(json, entry.Layout!);
                    json.WriteEndObject();
                }
            }

            json.WriteEndObject();
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }

    private static void Layout(Utf8JsonWriter json, PawnLayout layout)
    {
        json.WriteStartObject();
        json.WriteNumber("cells", layout.Cells);
        switch (layout)
        {
            case BooleanLayout:
                json.WriteString("kind", "boolean");
                break;
            case IntegerLayout integer:
                json.WriteString("kind", "integer");
                json.WriteBoolean("signed", integer.IsSigned);
                break;
            case EnumeratedLayout enumerated:
                json.WriteString("kind", "enumerated");
                json.WriteStartObject("items");
                foreach (var item in enumerated.Items)
                {
                    json.WriteNumber(item.Name, item.Value);
                }

                json.WriteEndObject();
                break;
            case NullLayout:
                json.WriteString("kind", "null");
                break;
            case StringLayout text:
                json.WriteString("kind", text.Kind switch
                {
                    StringKind.Octets => "octets",
                    StringKind.Bits => "bits",
                    StringKind.Utf8Characters => "utf8",
                    _ => "characters",
                });
                json.WriteNumber("minimum", text.Minimum);
                json.WriteNumber("maximum", text.Maximum);
                json.WriteBoolean("fixed", text.IsFixed);
                json.WriteNumber("dataOffset", text.DataOffset);
                break;
            case SequenceLayout sequence:
                json.WriteString("kind", "sequence");
                json.WriteBoolean("extensible", sequence.IsExtensible);
                json.WriteStartArray("fields");
                foreach (var field in sequence.Fields)
                {
                    json.WriteStartObject();
                    json.WriteString("name", field.Name);
                    json.WriteString("presence", field.Presence.ToString().ToLowerInvariant());
                    json.WriteNumber("presenceOffset", field.PresenceOffset);
                    json.WriteNumber("offset", field.ValueOffset);
                    if (field.DefaultValue is { } fallback)
                    {
                        json.WriteNumber("default", fallback);
                    }

                    json.WritePropertyName("layout");
                    Layout(json, field.Layout);
                    json.WriteEndObject();
                }

                json.WriteEndArray();
                break;
            case ChoiceLayout choice:
                json.WriteString("kind", "choice");
                json.WriteStartArray("alternatives");
                foreach (var alternative in choice.Alternatives)
                {
                    json.WriteStartObject();
                    json.WriteString("name", alternative.Name);
                    json.WriteNumber("index", alternative.Index);
                    json.WritePropertyName("layout");
                    Layout(json, alternative.Layout);
                    json.WriteEndObject();
                }

                json.WriteEndArray();
                break;
            case SequenceOfLayout list:
                json.WriteString("kind", "sequenceOf");
                json.WriteNumber("minimum", list.Minimum);
                json.WriteNumber("maximum", list.Maximum);
                json.WritePropertyName("element");
                Layout(json, list.Element);
                break;
            case ReferenceLayout reference:
                json.WriteString("kind", "reference");
                json.WriteString("type", reference.Target.AsnName);
                break;
        }

        json.WriteEndObject();
    }
}
