using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;

namespace Asn1.Diagnostics;

/// <summary>Severity of a diagnostic.</summary>
public enum DiagnosticSeverity
{
    /// <summary>Suppressed or disabled; never reported.</summary>
    Hidden = 0,

    /// <summary>Informational message.</summary>
    Info = 1,

    /// <summary>Potential problem that does not prevent processing.</summary>
    Warning = 2,

    /// <summary>Problem that makes the input invalid or unusable.</summary>
    Error = 3,
}

/// <summary>
/// Broad classification of a diagnostic. Categories let tooling distinguish a genuine
/// standards violation from a style preference or an unsupported implementation feature.
/// </summary>
public enum DiagnosticCategory
{
    /// <summary>Lexical or grammatical error (X.680 notation violation).</summary>
    Syntax,

    /// <summary>Semantic standards violation (resolution, tagging, typing).</summary>
    Semantic,

    /// <summary>Invalid or unsatisfiable constraint.</summary>
    Constraint,

    /// <summary>Valid ASN.1 that this implementation does not support yet.</summary>
    Unsupported,

    /// <summary>Style or maintainability preference.</summary>
    Style,

    /// <summary>Interoperability or encoding-rule compatibility risk.</summary>
    Interoperability,

    /// <summary>Code generation concern.</summary>
    CodeGeneration,

    /// <summary>Failure inside the toolchain itself (a bug).</summary>
    Internal,
}

/// <summary>Describes a kind of diagnostic with a stable identifier.</summary>
public sealed class DiagnosticDescriptor
{
    /// <summary>Creates a descriptor.</summary>
    public DiagnosticDescriptor(string id, string title, string messageFormat, DiagnosticCategory category, DiagnosticSeverity defaultSeverity, string? description = null)
    {
        Id = id;
        Title = title;
        MessageFormat = messageFormat;
        Category = category;
        DefaultSeverity = defaultSeverity;
        Description = description ?? title;
    }

    /// <summary>Stable identifier such as <c>ASN1001</c>.</summary>
    public string Id { get; }

    /// <summary>Short title.</summary>
    public string Title { get; }

    /// <summary>Composite format string for the message.</summary>
    public string MessageFormat { get; }

    /// <summary>Category.</summary>
    public DiagnosticCategory Category { get; }

    /// <summary>Severity used unless overridden by configuration.</summary>
    public DiagnosticSeverity DefaultSeverity { get; }

    /// <summary>Longer description for documentation.</summary>
    public string Description { get; }

    /// <inheritdoc />
    public override string ToString() => Id;
}

/// <summary>A suggested textual correction.</summary>
/// <param name="Description">Human-readable description of the fix.</param>
/// <param name="Location">Range to replace.</param>
/// <param name="Replacement">Replacement text.</param>
public sealed record SuggestedFix(string Description, SourceLocation Location, string Replacement);

/// <summary>A secondary location related to a diagnostic.</summary>
/// <param name="Location">The related location.</param>
/// <param name="Message">Explanation of the relation.</param>
public sealed record RelatedLocation(SourceLocation Location, string Message);

/// <summary>A single reported problem.</summary>
public sealed class Diagnostic
{
    /// <summary>Creates a diagnostic.</summary>
    public Diagnostic(
        DiagnosticDescriptor descriptor,
        SourceLocation location,
        DiagnosticSeverity severity,
        string message,
        ImmutableArray<RelatedLocation> relatedLocations = default,
        SuggestedFix? fix = null)
    {
        Descriptor = descriptor;
        Location = location;
        Severity = severity;
        Message = message;
        RelatedLocations = relatedLocations.IsDefault ? [] : relatedLocations;
        Fix = fix;
    }

    /// <summary>The descriptor.</summary>
    public DiagnosticDescriptor Descriptor { get; }

    /// <summary>Stable identifier.</summary>
    public string Id => Descriptor.Id;

    /// <summary>Effective severity.</summary>
    public DiagnosticSeverity Severity { get; }

    /// <summary>Formatted message.</summary>
    public string Message { get; }

    /// <summary>Primary location.</summary>
    public SourceLocation Location { get; }

    /// <summary>Related locations.</summary>
    public ImmutableArray<RelatedLocation> RelatedLocations { get; }

    /// <summary>Optional suggested fix.</summary>
    public SuggestedFix? Fix { get; }

    /// <summary>Category from the descriptor.</summary>
    public DiagnosticCategory Category => Descriptor.Category;

    /// <summary>True for error severity.</summary>
    public bool IsError => Severity == DiagnosticSeverity.Error;

    /// <summary>Creates a diagnostic using the descriptor's default severity.</summary>
    public static Diagnostic Create(DiagnosticDescriptor descriptor, SourceLocation location, params object?[] args) =>
        new(descriptor, location, descriptor.DefaultSeverity, Format(descriptor, args));

    /// <summary>Creates a diagnostic with related locations.</summary>
    public static Diagnostic Create(DiagnosticDescriptor descriptor, SourceLocation location, IEnumerable<RelatedLocation> related, params object?[] args) =>
        new(descriptor, location, descriptor.DefaultSeverity, Format(descriptor, args), [.. related]);

    /// <summary>Returns a copy with another severity.</summary>
    public Diagnostic WithSeverity(DiagnosticSeverity severity) => new(Descriptor, Location, severity, Message, RelatedLocations, Fix);

    /// <summary>Returns a copy with a suggested fix.</summary>
    public Diagnostic WithFix(SuggestedFix fix) => new(Descriptor, Location, Severity, Message, RelatedLocations, fix);

    private static string Format(DiagnosticDescriptor descriptor, object?[] args) =>
        args.Length == 0 ? descriptor.MessageFormat : string.Format(CultureInfo.InvariantCulture, descriptor.MessageFormat, args);

    /// <inheritdoc />
    public override string ToString()
    {
        var sev = Severity switch
        {
            DiagnosticSeverity.Error => "error",
            DiagnosticSeverity.Warning => "warning",
            DiagnosticSeverity.Info => "info",
            _ => "hidden",
        };
        return Location.IsNone
            ? $"{sev} {Id}: {Message}"
            : $"{Location.Path}({Location.Start.Line},{Location.Start.Column}): {sev} {Id}: {Message}";
    }
}

/// <summary>A thread-safe accumulating collection of diagnostics.</summary>
public sealed class DiagnosticBag
{
    private readonly List<Diagnostic> _items = [];
    private readonly Lock _lock = new();

    /// <summary>Number of diagnostics.</summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _items.Count;
            }
        }
    }

    /// <summary>True if any error was reported.</summary>
    public bool HasErrors
    {
        get
        {
            lock (_lock)
            {
                return _items.Exists(d => d.IsError);
            }
        }
    }

    /// <summary>Adds a diagnostic.</summary>
    public void Add(Diagnostic diagnostic)
    {
        lock (_lock)
        {
            _items.Add(diagnostic);
        }
    }

    /// <summary>Adds a diagnostic created from a descriptor.</summary>
    public Diagnostic Report(DiagnosticDescriptor descriptor, SourceLocation location, params object?[] args)
    {
        var d = Diagnostic.Create(descriptor, location, args);
        Add(d);
        return d;
    }

    /// <summary>Adds many diagnostics.</summary>
    public void AddRange(IEnumerable<Diagnostic> diagnostics)
    {
        lock (_lock)
        {
            _items.AddRange(diagnostics);
        }
    }

    /// <summary>Returns a stable sorted snapshot (by path, position, id).</summary>
    public ImmutableArray<Diagnostic> ToImmutableSorted()
    {
        lock (_lock)
        {
            return [.. DiagnosticOrdering.Sort(_items)];
        }
    }

    /// <summary>Returns an unsorted snapshot in insertion order.</summary>
    public ImmutableArray<Diagnostic> ToImmutable()
    {
        lock (_lock)
        {
            return [.. _items];
        }
    }
}

/// <summary>Deterministic diagnostic ordering helpers.</summary>
public static class DiagnosticOrdering
{
    /// <summary>Sorts diagnostics by path, start offset, id and message, removing exact duplicates.</summary>
    public static IEnumerable<Diagnostic> Sort(IEnumerable<Diagnostic> diagnostics)
    {
        var seen = new HashSet<(string, int, int, string, string)>();
        var list = new List<Diagnostic>();
        foreach (var d in diagnostics)
        {
            if (seen.Add((d.Location.Path, d.Location.Span.Start, d.Location.Span.Length, d.Id, d.Message)))
            {
                list.Add(d);
            }
        }

        list.Sort(static (a, b) =>
        {
            var c = string.CompareOrdinal(a.Location.Path, b.Location.Path);
            if (c != 0)
            {
                return c;
            }

            c = a.Location.Span.Start.CompareTo(b.Location.Span.Start);
            if (c != 0)
            {
                return c;
            }

            c = string.CompareOrdinal(a.Id, b.Id);
            return c != 0 ? c : string.CompareOrdinal(a.Message, b.Message);
        });
        return list;
    }
}
