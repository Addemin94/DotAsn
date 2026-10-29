using System.Text;

namespace Asn1.Runtime;

/// <summary>
/// Tracks the component path during encoding and decoding so errors can name the failing value
/// (e.g. <c>Message.reports[2].data</c>). The string is only built when an error is reported.
/// </summary>
public sealed class Asn1Path
{
    // A stack of (name, -1) for components and (null, index) for collection elements.
    private (string? Name, int Index)[] _segments = new (string?, int)[8];
    private int _count;

    /// <summary>The current depth.</summary>
    public int Depth => _count;

    /// <summary>Enters a named component.</summary>
    public void Push(string name) => Add((name, -1));

    /// <summary>Enters a collection element.</summary>
    public void PushIndex(int index) => Add((null, index));

    /// <summary>Leaves the current segment.</summary>
    public void Pop()
    {
        if (_count == 0)
        {
            throw new InvalidOperationException("Pop without a matching Push");
        }

        _count--;
    }

    /// <summary>Removes all segments.</summary>
    internal void Clear() => _count = 0;

    private void Add((string?, int) segment)
    {
        if (_count == _segments.Length)
        {
            Array.Resize(ref _segments, _count * 2);
        }

        _segments[_count++] = segment;
    }

    /// <inheritdoc />
    public override string ToString()
    {
        var sb = new StringBuilder();
        foreach (var (name, index) in _segments.AsSpan(0, _count))
        {
            if (name is null)
            {
                sb.Append('[').Append(index).Append(']');
            }
            else
            {
                if (sb.Length > 0)
                {
                    sb.Append('.');
                }

                sb.Append(name);
            }
        }

        return sb.ToString();
    }
}
