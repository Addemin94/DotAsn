using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Asn1.Diagnostics;

/// <summary>Output format for diagnostic reports.</summary>
public enum DiagnosticOutputFormat
{
    /// <summary>Human-readable text, compiler style.</summary>
    Text,

    /// <summary>A JSON document with a diagnostics array.</summary>
    Json,

    /// <summary>SARIF 2.1.0 log.</summary>
    Sarif,
}

/// <summary>Writes diagnostics in text, JSON or SARIF form.</summary>
public static class DiagnosticFormatter
{
    /// <summary>Writes diagnostics in the requested format.</summary>
    public static void Write(TextWriter writer, IReadOnlyList<Diagnostic> diagnostics, DiagnosticOutputFormat format, string toolVersion, bool showSource = true)
    {
        switch (format)
        {
            case DiagnosticOutputFormat.Json:
                writer.Write(ToJson(diagnostics));
                writer.WriteLine();
                break;
            case DiagnosticOutputFormat.Sarif:
                writer.Write(ToSarif(diagnostics, toolVersion));
                writer.WriteLine();
                break;
            default:
                WriteText(writer, diagnostics, showSource);
                break;
        }
    }

    /// <summary>Writes compiler-style text with optional source excerpts.</summary>
    public static void WriteText(TextWriter writer, IEnumerable<Diagnostic> diagnostics, bool showSource)
    {
        foreach (var d in diagnostics)
        {
            writer.WriteLine(d.ToString());
            if (showSource && !d.Location.IsNone && d.Location.Source.Text.Length > 0)
            {
                var start = d.Location.Start;
                var line = d.Location.Source.GetLineText(start.Line - 1).Replace('\t', ' ');
                writer.WriteLine($"    {line}");
                var width = Math.Max(1, Math.Min(d.Location.Span.Length, Math.Max(1, line.Length - start.Column + 1)));
                writer.WriteLine("    " + new string(' ', Math.Max(0, start.Column - 1)) + new string('^', width));
            }

            foreach (var r in d.RelatedLocations)
            {
                writer.WriteLine($"    note: {r.Message} at {r.Location}");
            }

            if (d.Fix is not null)
            {
                writer.WriteLine($"    fix: {d.Fix.Description}");
            }
        }
    }

    /// <summary>Formats a one-line summary such as "2 error(s), 1 warning(s)".</summary>
    public static string Summary(IReadOnlyCollection<Diagnostic> diagnostics)
    {
        var errors = diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error);
        var warnings = diagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning);
        var infos = diagnostics.Count(d => d.Severity == DiagnosticSeverity.Info);
        return $"{errors} error(s), {warnings} warning(s), {infos} info message(s)";
    }

    /// <summary>Serializes diagnostics to a JSON document.</summary>
    public static string ToJson(IEnumerable<Diagnostic> diagnostics)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteStartArray("diagnostics");
            foreach (var d in diagnostics)
            {
                w.WriteStartObject();
                w.WriteString("id", d.Id);
                w.WriteString("severity", SeverityName(d.Severity));
                w.WriteString("category", d.Category.ToString());
                w.WriteString("message", d.Message);
                WriteJsonLocation(w, "location", d.Location);
                if (d.RelatedLocations.Length > 0)
                {
                    w.WriteStartArray("related");
                    foreach (var r in d.RelatedLocations)
                    {
                        w.WriteStartObject();
                        w.WriteString("message", r.Message);
                        WriteJsonLocation(w, "location", r.Location);
                        w.WriteEndObject();
                    }

                    w.WriteEndArray();
                }

                if (d.Fix is not null)
                {
                    w.WriteStartObject("fix");
                    w.WriteString("description", d.Fix.Description);
                    w.WriteString("replacement", d.Fix.Replacement);
                    WriteJsonLocation(w, "location", d.Fix.Location);
                    w.WriteEndObject();
                }

                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteJsonLocation(Utf8JsonWriter w, string name, SourceLocation loc)
    {
        if (loc.IsNone)
        {
            w.WriteNull(name);
            return;
        }

        w.WriteStartObject(name);
        w.WriteString("path", loc.Path);
        w.WriteNumber("startLine", loc.Start.Line);
        w.WriteNumber("startColumn", loc.Start.Column);
        w.WriteNumber("endLine", loc.End.Line);
        w.WriteNumber("endColumn", loc.End.Column);
        w.WriteNumber("offset", loc.Span.Start);
        w.WriteNumber("length", loc.Span.Length);
        w.WriteEndObject();
    }

    /// <summary>Serializes diagnostics to a SARIF 2.1.0 log.</summary>
    public static string ToSarif(IReadOnlyList<Diagnostic> diagnostics, string toolVersion)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("$schema", "https://json.schemastore.org/sarif-2.1.0.json");
            w.WriteString("version", "2.1.0");
            w.WriteStartArray("runs");
            w.WriteStartObject();
            w.WriteStartObject("tool");
            w.WriteStartObject("driver");
            w.WriteString("name", "asn1");
            w.WriteString("informationUri", "https://www.itu.int/rec/T-REC-X.680");
            w.WriteString("version", toolVersion);
            w.WriteStartArray("rules");
            var ruleIds = diagnostics.Select(d => d.Descriptor).DistinctBy(d => d.Id).OrderBy(d => d.Id, StringComparer.Ordinal).ToList();
            foreach (var rule in ruleIds)
            {
                w.WriteStartObject();
                w.WriteString("id", rule.Id);
                w.WriteString("name", ToPascal(rule.Title));
                w.WriteStartObject("shortDescription");
                w.WriteString("text", rule.Title);
                w.WriteEndObject();
                w.WriteStartObject("fullDescription");
                w.WriteString("text", rule.Description);
                w.WriteEndObject();
                w.WriteStartObject("defaultConfiguration");
                w.WriteString("level", SarifLevel(rule.DefaultSeverity));
                w.WriteEndObject();
                w.WriteStartObject("properties");
                w.WriteString("category", rule.Category.ToString());
                w.WriteEndObject();
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndObject();
            w.WriteStartArray("results");
            foreach (var d in diagnostics)
            {
                w.WriteStartObject();
                w.WriteString("ruleId", d.Id);
                w.WriteNumber("ruleIndex", ruleIds.FindIndex(r => r.Id == d.Id));
                w.WriteString("level", SarifLevel(d.Severity));
                w.WriteStartObject("message");
                w.WriteString("text", d.Message);
                w.WriteEndObject();
                if (!d.Location.IsNone)
                {
                    w.WriteStartArray("locations");
                    WriteSarifLocation(w, d.Location, null);
                    w.WriteEndArray();
                }

                if (d.RelatedLocations.Length > 0)
                {
                    w.WriteStartArray("relatedLocations");
                    foreach (var r in d.RelatedLocations.Where(r => !r.Location.IsNone))
                    {
                        WriteSarifLocation(w, r.Location, r.Message);
                    }

                    w.WriteEndArray();
                }

                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteSarifLocation(Utf8JsonWriter w, SourceLocation loc, string? message)
    {
        w.WriteStartObject();
        w.WriteStartObject("physicalLocation");
        w.WriteStartObject("artifactLocation");
        w.WriteString("uri", loc.Path.Replace('\\', '/'));
        w.WriteEndObject();
        w.WriteStartObject("region");
        w.WriteNumber("startLine", loc.Start.Line);
        w.WriteNumber("startColumn", loc.Start.Column);
        w.WriteNumber("endLine", loc.End.Line);
        w.WriteNumber("endColumn", loc.End.Column);
        w.WriteEndObject();
        w.WriteEndObject();
        if (message is not null)
        {
            w.WriteStartObject("message");
            w.WriteString("text", message);
            w.WriteEndObject();
        }

        w.WriteEndObject();
    }

    private static string SeverityName(DiagnosticSeverity s) => s switch
    {
        DiagnosticSeverity.Error => "error",
        DiagnosticSeverity.Warning => "warning",
        DiagnosticSeverity.Info => "info",
        _ => "hidden",
    };

    private static string SarifLevel(DiagnosticSeverity s) => s switch
    {
        DiagnosticSeverity.Error => "error",
        DiagnosticSeverity.Warning => "warning",
        DiagnosticSeverity.Info => "note",
        _ => "none",
    };

    private static string ToPascal(string title)
    {
        var sb = new StringBuilder();
        var upper = true;
        foreach (var c in title)
        {
            if (!char.IsLetterOrDigit(c))
            {
                upper = true;
                continue;
            }

            sb.Append(upper ? char.ToUpperInvariant(c) : c);
            upper = false;
        }

        return sb.ToString();
    }
}
