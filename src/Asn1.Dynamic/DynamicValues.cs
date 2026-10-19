using System.Collections;

namespace Asn1.Dynamic;

/// <summary>A dynamic SEQUENCE/SET value: present components in insertion order.</summary>
public sealed class Asn1Record : IEnumerable<KeyValuePair<string, object?>>, IEquatable<Asn1Record>
{
    private readonly List<KeyValuePair<string, object?>> _items = [];

    /// <summary>Number of present components.</summary>
    public int Count => _items.Count;

    /// <summary>Gets or sets a component value; setting appends or replaces.</summary>
    public object? this[string name]
    {
        get => TryGetValue(name, out var v) ? v : throw new KeyNotFoundException(name);
        set
        {
            var i = _items.FindIndex(p => p.Key == name);
            if (i >= 0)
            {
                _items[i] = new(name, value);
            }
            else
            {
                _items.Add(new(name, value));
            }
        }
    }

    /// <summary>True if the component is present.</summary>
    public bool Contains(string name) => _items.Exists(p => p.Key == name);

    /// <summary>Gets a component value.</summary>
    public bool TryGetValue(string name, out object? value)
    {
        foreach (var p in _items)
        {
            if (p.Key == name)
            {
                value = p.Value;
                return true;
            }
        }

        value = null;
        return false;
    }

    /// <summary>Removes a component.</summary>
    public void Remove(string name) => _items.RemoveAll(p => p.Key == name);

    /// <inheritdoc />
    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <inheritdoc />
    public bool Equals(Asn1Record? other)
    {
        if (other is null || other.Count != Count)
        {
            return false;
        }

        foreach (var p in _items)
        {
            if (!other.TryGetValue(p.Key, out var v) || !DynamicEquality.ValuesEqual(p.Value, v))
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as Asn1Record);

    /// <inheritdoc />
    public override int GetHashCode() => Count;
}

/// <summary>A dynamic CHOICE value.</summary>
/// <param name="Alternative">The chosen alternative's identifier (null for an unknown extension).</param>
/// <param name="Value">The alternative's value (an <see cref="Runtime.Asn1OpenValue"/> for unknown extensions).</param>
/// <param name="UnknownIndex">For unknown extensions: the extension index (needed to re-encode with PER), or -1.</param>
public sealed record Asn1Choice(string? Alternative, object? Value, int UnknownIndex = -1)
{
    /// <inheritdoc />
    public bool Equals(Asn1Choice? other) => other is not null && other.Alternative == Alternative && DynamicEquality.ValuesEqual(Value, other.Value);

    /// <inheritdoc />
    public override int GetHashCode() => Alternative?.GetHashCode(StringComparison.Ordinal) ?? 0;
}

/// <summary>Structural equality for dynamic values.</summary>
public static class DynamicEquality
{
    /// <summary>Compares two dynamic values structurally.</summary>
    public static bool ValuesEqual(object? a, object? b) => (a, b) switch
    {
        (null, null) => true,
        (byte[] x, byte[] y) => x.AsSpan().SequenceEqual(y),
        (double x, double y) => x.Equals(y),
        (System.Collections.IList x, System.Collections.IList y) => ListEqual(x, y),
        _ => Equals(a, b),
    };

    private static bool ListEqual(System.Collections.IList x, System.Collections.IList y)
    {
        if (x.Count != y.Count)
        {
            return false;
        }

        for (var i = 0; i < x.Count; i++)
        {
            if (!ValuesEqual(x[i], y[i]))
            {
                return false;
            }
        }

        return true;
    }
}
