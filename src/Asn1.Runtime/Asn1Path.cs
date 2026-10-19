using System.Text;

namespace Asn1.Runtime;

/// <summary>
/// Tracks the component path during encoding and decoding so errors can name the failing value
/// (e.g. <c>Message.reports[2].data</c>). The string is only built when an error is reported.
/// </summary>
public sealed class Asn1Path
{
    private readonly List<(string? Name, int Index)> _segments = [];

    /// <summary>The current depth.</summary>
    public int Depth => _segments.Count;

    /// <summary>Enters a named component.</summary>
    public void Push(string name) => _segments.Add((name, -1));

    /// <summary>Enters a collection element.</summary>
    public void PushIndex(int index) => _segments.Add((null, index));

    /// <summary>Leaves the current segment.</summary>
    public void Pop() => _segments.RemoveAt(_segments.Count - 1);

    /// <inheritdoc />
    public override string ToString()
    {
        var sb = new StringBuilder();
        foreach (var (name, index) in _segments)
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
