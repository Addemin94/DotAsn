using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asn1.Diagnostics;
using Asn1.Dynamic;
using Asn1.Runtime;
using Asn1.Semantics;

namespace Asn1.InteroperabilityTests;

/// <summary>Loads asn1tools-generated vector files and compiles their specifications once.</summary>
public static class VectorSuite
{
    private static readonly ConcurrentDictionary<string, (Asn1Compilation Compilation, DynamicTypeFactory Factory)> s_compilations = new();

    public static string Root { get; } = typeof(VectorSuite).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == "RepositoryRoot").Value!;

    public static IEnumerable<string> Files(string codec)
    {
        var dir = Path.Combine(Root, "test-vectors", codec);
        return Directory.Exists(dir) ? Directory.GetFiles(dir, "*.json").Select(Path.GetFileNameWithoutExtension).Order().Select(f => f!) : [];
    }

    public sealed record Case(string Type, JsonNode Value, string Hex, JsonNode? Decoded);

    public sealed record VectorFile(IReadOnlyList<string> Specs, IReadOnlyList<Case> Cases);

    public static VectorFile Load(string codec, string name)
    {
        var json = JsonNode.Parse(File.ReadAllText(Path.Combine(Root, "test-vectors", codec, name + ".json")))!;
        var specs = json["specs"]!.AsArray().Select(s => s!.GetValue<string>()).ToList();
        var cases = json["cases"]!.AsArray().Select(c => new Case(c!["type"]!.GetValue<string>(), c["value"]!, c["hex"]!.GetValue<string>(), c["decoded"])).ToList();
        return new VectorFile(specs, cases);
    }

    public static (Asn1Compilation Compilation, DynamicTypeFactory Factory) Compile(IReadOnlyList<string> specs) =>
        s_compilations.GetOrAdd(string.Join("|", specs), _ =>
        {
            var c = Asn1Compilation.Create(specs.Select(s => new SourceText(s, File.ReadAllText(Path.Combine(Root, s)))));
            return (c, new DynamicTypeFactory());
        });

    public static Asn1Type<object?> Descriptor(VectorFile file, string qualifiedType)
    {
        var (compilation, factory) = Compile(file.Specs);
        var ta = compilation.FindType(qualifiedType) ?? throw new InvalidOperationException($"type {qualifiedType} not found");
        lock (factory)
        {
            return factory.Create(ta);
        }
    }

    public static string Canonical(JsonNode? node) => node switch
    {
        null => "null",
        JsonObject o => "{" + string.Join(",", o.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => JsonSerializer.Serialize(p.Key) + ":" + Canonical(p.Value))) + "}",
        JsonArray a => "[" + string.Join(",", a.Select(Canonical)) + "]",
        JsonValue v when v.TryGetValue<double>(out var d) && !v.TryGetValue<long>(out _) => d.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        _ => node.ToJsonString(),
    };

    public static string Canonical(string json) => Canonical(JsonNode.Parse(json));
}
