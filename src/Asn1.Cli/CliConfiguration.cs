using System.Text.Json;
using Asn1.Linter;

namespace Asn1.Cli;

/// <summary>Generator defaults from the configuration file.</summary>
internal sealed record GenerateSettings(string? Namespace, string? Output, string? Layout, string? IntegerMapping, bool Internal);

/// <summary>
/// The <c>asn1.json</c> configuration: <c>{ "lint": { ...asn1lint options... }, "generate": { "namespace": "...",
/// "output": "...", "layout": "per-module", "integerMapping": "precise", "internal": false } }</c>.
/// </summary>
internal sealed record CliConfiguration(LintConfiguration Lint, GenerateSettings Generate)
{
    public static CliConfiguration Load(string? explicitPath)
    {
        var path = explicitPath ?? Find();
        if (path is null)
        {
            return new CliConfiguration(LintConfiguration.Default, new GenerateSettings(null, null, null, null, false));
        }

        if (!File.Exists(path))
        {
            throw new UsageException($"configuration file '{path}' does not exist");
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var root = doc.RootElement;
            var lint = root.TryGetProperty("lint", out var l) ? LintConfiguration.Load(l.GetRawText()) : LintConfiguration.Default;
            var g = root.TryGetProperty("generate", out var gen) ? gen : default;
            string? S(string name) => g.ValueKind == JsonValueKind.Object && g.TryGetProperty(name, out var v) ? v.GetString() : null;
            var isInternal = g.ValueKind == JsonValueKind.Object && g.TryGetProperty("internal", out var i) && i.GetBoolean();
            return new CliConfiguration(lint, new GenerateSettings(S("namespace"), S("output"), S("layout"), S("integerMapping"), isInternal));
        }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException)
        {
            throw new UsageException($"invalid configuration file '{path}': {e.Message}");
        }
    }

    private static string? Find()
    {
        for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "asn1.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
