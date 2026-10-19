using System.Collections;

namespace Asn1.Runtime;

/// <summary>Structural equality used by generated types (byte arrays and lists compare by content).</summary>
public static class Asn1ValueEquality
{
    /// <summary>Compares two values structurally.</summary>
    public static bool Equal<T>(T a, T b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }

        return (object)a switch
        {
            byte[] x => b is byte[] y && x.AsSpan().SequenceEqual(y),
            double x => b is double y && x.Equals(y),
            IList x when b is IList y => ListEqual(x, y),
            _ => EqualityComparer<T>.Default.Equals(a, b),
        };
    }

    private static bool ListEqual(IList x, IList y)
    {
        if (x.Count != y.Count)
        {
            return false;
        }

        for (var i = 0; i < x.Count; i++)
        {
            if (!Equal(x[i], y[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A structural hash code consistent with <see cref="Equal{T}"/>.</summary>
    public static int Hash<T>(T value)
    {
        switch (value)
        {
            case null:
                return 0;
            case byte[] bytes:
                {
                    var h = new HashCode();
                    h.AddBytes(bytes);
                    return h.ToHashCode();
                }

            case IList list:
                {
                    var h = new HashCode();
                    h.Add(list.Count);
                    foreach (var item in list)
                    {
                        h.Add(Hash(item));
                    }

                    return h.ToHashCode();
                }

            default:
                return EqualityComparer<T>.Default.GetHashCode(value);
        }
    }
}
