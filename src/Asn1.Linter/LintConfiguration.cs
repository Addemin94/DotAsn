using System.Text.Json;
using Asn1.Diagnostics;

namespace Asn1.Linter;

/// <summary>Naming convention for type references and identifiers.</summary>
public enum NamingStyle
{
    /// <summary>No check.</summary>
    Any,

    /// <summary>UpperCamelCase / lowerCamelCase, no hyphens.</summary>
    Camel,

    /// <summary>Camel case words optionally joined by single hyphens (the common ASN.1 style).</summary>
    CamelWithHyphens,
}

/// <summary>Linter configuration (loaded from <c>asn1lint.json</c> or set programmatically).</summary>
public sealed record LintConfiguration
{
    /// <summary>Default configuration.</summary>
    public static readonly LintConfiguration Default = new();

    /// <summary>Severity overrides by diagnostic id; <see cref="DiagnosticSeverity.Hidden"/> disables a rule.</summary>
    public IReadOnlyDictionary<string, DiagnosticSeverity> Severities { get; init; } = new Dictionary<string, DiagnosticSeverity>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Rules that are off unless enabled through <see cref="Severities"/>.</summary>
    public IReadOnlySet<string> DisabledByDefault { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ASN5009", "ASN5017" };

    /// <summary>Naming style for type references.</summary>
    public NamingStyle TypeNaming { get; init; } = NamingStyle.CamelWithHyphens;

    /// <summary>Naming style for identifiers (components, items, values).</summary>
    public NamingStyle IdentifierNaming { get; init; } = NamingStyle.CamelWithHyphens;

    /// <summary>Encoding rules the specification targets (enables interoperability rules), e.g. "uper", "der".</summary>
    public IReadOnlySet<string> EncodingRules { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Maximum nesting depth of anonymous structured types before ASN5008 is reported.</summary>
    public int MaxAnonymousNesting { get; init; } = 3;

    /// <summary>Report unreferenced definitions even in modules that export everything.</summary>
    public bool ReportUnreferencedWhenExportingAll { get; init; }

    /// <summary>True if any PER variant is targeted.</summary>
    public bool TargetsPer => EncodingRules.Any(r => r.ToUpperInvariant() is "UPER" or "APER" or "PER");

    /// <summary>The effective severity of a diagnostic id, or null for the descriptor default.</summary>
    public DiagnosticSeverity? SeverityOf(string id) =>
        Severities.TryGetValue(id, out var s) ? s : DisabledByDefault.Contains(id) ? DiagnosticSeverity.Hidden : null;

    /// <summary>
    /// Loads a configuration file:
    /// <code>{ "rules": { "ASN5001": "off" | "info" | "warning" | "error" }, "typeNaming": "camelWithHyphens",
    ///   "identifierNaming": "camel", "encodingRules": ["uper"], "maxAnonymousNesting": 3 }</code>
    /// </summary>
    public static LintConfiguration Load(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var root = doc.RootElement;
        var config = new LintConfiguration();
        if (root.TryGetProperty("rules", out var rules))
        {
            var map = new Dictionary<string, DiagnosticSeverity>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in rules.EnumerateObject())
            {
                map[p.Name] = (p.Value.GetString() ?? string.Empty).ToLowerInvariant() switch
                {
                    "off" or "none" or "hidden" => DiagnosticSeverity.Hidden,
                    "info" or "note" or "suggestion" => DiagnosticSeverity.Info,
                    "warning" or "warn" => DiagnosticSeverity.Warning,
                    "error" => DiagnosticSeverity.Error,
                    var other => throw new FormatException($"invalid severity '{other}' for {p.Name}"),
                };
            }

            config = config with { Severities = map };
        }

        NamingStyle Style(string name) => name.ToLowerInvariant() switch
        {
            "any" or "off" => NamingStyle.Any,
            "camel" or "pascal" => NamingStyle.Camel,
            "camelwithhyphens" or "camel-with-hyphens" or "hyphens" => NamingStyle.CamelWithHyphens,
            _ => throw new FormatException($"invalid naming style '{name}'"),
        };

        if (root.TryGetProperty("typeNaming", out var tn))
        {
            config = config with { TypeNaming = Style(tn.GetString() ?? "any") };
        }

        if (root.TryGetProperty("identifierNaming", out var idn))
        {
            config = config with { IdentifierNaming = Style(idn.GetString() ?? "any") };
        }

        if (root.TryGetProperty("encodingRules", out var er))
        {
            config = config with { EncodingRules = er.EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToHashSet(StringComparer.OrdinalIgnoreCase) };
        }

        if (root.TryGetProperty("maxAnonymousNesting", out var mn))
        {
            config = config with { MaxAnonymousNesting = mn.GetInt32() };
        }

        if (root.TryGetProperty("reportUnreferencedWhenExportingAll", out var ru))
        {
            config = config with { ReportUnreferencedWhenExportingAll = ru.GetBoolean() };
        }

        return config;
    }
}
