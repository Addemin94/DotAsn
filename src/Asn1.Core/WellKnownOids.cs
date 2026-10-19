using System.Numerics;

namespace Asn1;

/// <summary>Well-known object identifier arc names (X.660 Annexes A-C).</summary>
public static class WellKnownOids
{
    private static readonly Dictionary<string, int> s_roots = new(StringComparer.Ordinal)
    {
        ["itu-t"] = 0,
        ["ccitt"] = 0,
        ["itu-r"] = 0,
        ["iso"] = 1,
        ["joint-iso-itu-t"] = 2,
        ["joint-iso-ccitt"] = 2,
    };

    private static readonly Dictionary<string, int> s_itu = new(StringComparer.Ordinal)
    {
        ["recommendation"] = 0,
        ["question"] = 1,
        ["administration"] = 2,
        ["network-operator"] = 3,
        ["identified-organization"] = 4,
        ["r-recommendation"] = 5,
        ["data"] = 9,
    };

    private static readonly Dictionary<string, int> s_iso = new(StringComparer.Ordinal)
    {
        ["standard"] = 0,
        ["registration-authority"] = 1,
        ["member-body"] = 2,
        ["identified-organization"] = 3,
    };

    /// <summary>Returns the arc number of a name-only component at the position after <paramref name="previous"/>.</summary>
    public static BigInteger? Lookup(IReadOnlyList<BigInteger> previous, string name)
    {
        if (previous.Count == 0)
        {
            return s_roots.TryGetValue(name, out var r) ? r : null;
        }

        if (previous.Count == 1)
        {
            if (previous[0] == 0 && s_itu.TryGetValue(name, out var i))
            {
                return i;
            }

            if (previous[0] == 1 && s_iso.TryGetValue(name, out var s))
            {
                return s;
            }
        }

        return null;
    }
}
