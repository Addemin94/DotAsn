using System;
using System.Collections.Generic;

namespace Asn1.Diagnostics;

/// <summary>
/// Immutable source file contents with a precomputed line map, used to translate
/// character offsets into one-based line/column positions.
/// </summary>
public sealed class SourceText
{
    private readonly int[] _lineStarts;

    /// <summary>Creates a source text.</summary>
    /// <param name="path">The file path, or a synthetic name such as <c>&lt;memory&gt;</c>.</param>
    /// <param name="text">The full text.</param>
    public SourceText(string path, string text)
    {
        Path = path ?? throw new ArgumentNullException(nameof(path));
        Text = text ?? throw new ArgumentNullException(nameof(text));
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                starts.Add(i + 1);
            }
            else if (c == '\n' || c == '\u000B' || c == '\u000C')
            {
                starts.Add(i + 1);
            }
        }

        _lineStarts = starts.ToArray();
    }

    /// <summary>The file path or synthetic name.</summary>
    public string Path { get; }

    /// <summary>The complete text.</summary>
    public string Text { get; }

    /// <summary>Number of lines in the text.</summary>
    public int LineCount => _lineStarts.Length;

    /// <summary>Gets the zero-based line index that contains <paramref name="offset"/>.</summary>
    public int GetLineIndex(int offset)
    {
        offset = Math.Clamp(offset, 0, Text.Length);
        var index = Array.BinarySearch(_lineStarts, offset);
        return index >= 0 ? index : ~index - 1;
    }

    /// <summary>Converts a character offset to a one-based line and column.</summary>
    public LinePosition GetLinePosition(int offset)
    {
        var line = GetLineIndex(offset);
        return new LinePosition(line + 1, Math.Clamp(offset, 0, Text.Length) - _lineStarts[line] + 1);
    }

    /// <summary>Returns the text of a zero-based line without its line terminator.</summary>
    public string GetLineText(int lineIndex)
    {
        if (lineIndex < 0 || lineIndex >= _lineStarts.Length)
        {
            return string.Empty;
        }

        var start = _lineStarts[lineIndex];
        var end = lineIndex + 1 < _lineStarts.Length ? _lineStarts[lineIndex + 1] : Text.Length;
        while (end > start && (Text[end - 1] == '\n' || Text[end - 1] == '\r'))
        {
            end--;
        }

        return Text[start..end];
    }

    /// <summary>Returns the substring covered by <paramref name="span"/>.</summary>
    public string GetText(TextSpan span) => Text.Substring(span.Start, Math.Min(span.Length, Text.Length - span.Start));
}

/// <summary>A one-based line and column position.</summary>
/// <param name="Line">One-based line number.</param>
/// <param name="Column">One-based column number.</param>
public readonly record struct LinePosition(int Line, int Column)
{
    /// <inheritdoc />
    public override string ToString() => $"{Line}:{Column}";
}

/// <summary>A half-open character range <c>[Start, Start + Length)</c>.</summary>
/// <param name="Start">Zero-based start offset.</param>
/// <param name="Length">Length in characters.</param>
public readonly record struct TextSpan(int Start, int Length)
{
    /// <summary>Exclusive end offset.</summary>
    public int End => Start + Length;

    /// <summary>Creates a span from inclusive start and exclusive end offsets.</summary>
    public static TextSpan FromBounds(int start, int end) => new(start, Math.Max(0, end - start));

    /// <summary>Returns the smallest span covering both spans.</summary>
    public TextSpan Union(TextSpan other) => FromBounds(Math.Min(Start, other.Start), Math.Max(End, other.End));
}

/// <summary>A span within a specific source text.</summary>
public sealed class SourceLocation : IEquatable<SourceLocation>
{
    /// <summary>A location that refers to no source (for example, command-line input).</summary>
    public static readonly SourceLocation None = new(new SourceText("<none>", string.Empty), default);

    /// <summary>Creates a location.</summary>
    public SourceLocation(SourceText source, TextSpan span)
    {
        Source = source;
        Span = span;
    }

    /// <summary>The source text.</summary>
    public SourceText Source { get; }

    /// <summary>The covered span.</summary>
    public TextSpan Span { get; }

    /// <summary>The file path.</summary>
    public string Path => Source.Path;

    /// <summary>One-based start position.</summary>
    public LinePosition Start => Source.GetLinePosition(Span.Start);

    /// <summary>One-based end position (exclusive).</summary>
    public LinePosition End => Source.GetLinePosition(Span.End);

    /// <summary>True when this is <see cref="None"/>.</summary>
    public bool IsNone => ReferenceEquals(this, None);

    /// <inheritdoc />
    public bool Equals(SourceLocation? other) =>
        other is not null && ReferenceEquals(Source, other.Source) && Span == other.Span;

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as SourceLocation);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Source.Path, Span);

    /// <inheritdoc />
    public override string ToString() => IsNone ? "<none>" : $"{Path}({Start.Line},{Start.Column})";
}
