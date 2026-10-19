using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;

namespace Asn1;

/// <summary>An immutable OBJECT IDENTIFIER value (X.680 32) with arbitrary-precision arcs.</summary>
public sealed class ObjectIdentifier : IEquatable<ObjectIdentifier>, IComparable<ObjectIdentifier>
{
    private string? _text;

    /// <summary>Creates an object identifier from arcs, validating X.660 first/second arc rules.</summary>
    public ObjectIdentifier(IEnumerable<BigInteger> arcs)
    {
        Arcs = [.. arcs];
        Validate(Arcs);
    }

    /// <summary>Creates an object identifier from arcs.</summary>
    public ObjectIdentifier(params long[] arcs) : this(arcs.Select(a => (BigInteger)a))
    {
    }

    /// <summary>The arcs.</summary>
    public ImmutableArray<BigInteger> Arcs { get; }

    /// <summary>Parses dotted-decimal notation such as <c>1.2.840.113549</c>.</summary>
    public static ObjectIdentifier Parse(string text)
    {
        if (!TryParse(text, out var oid, out var error))
        {
            throw new FormatException(error);
        }

        return oid!;
    }

    /// <summary>Tries to parse dotted-decimal notation.</summary>
    public static bool TryParse(string text, out ObjectIdentifier? oid, out string? error)
    {
        oid = null;
        error = null;
        var parts = text.Split('.');
        var arcs = new List<BigInteger>(parts.Length);
        foreach (var p in parts)
        {
            if (p.Length == 0 || !p.All(char.IsAsciiDigit) || (p.Length > 1 && p[0] == '0'))
            {
                error = $"'{text}' is not a valid dotted object identifier";
                return false;
            }

            arcs.Add(BigInteger.Parse(p, NumberStyles.None, CultureInfo.InvariantCulture));
        }

        try
        {
            oid = new ObjectIdentifier(arcs);
            return true;
        }
        catch (ArgumentException e)
        {
            error = e.Message;
            return false;
        }
    }

    private static void Validate(ImmutableArray<BigInteger> arcs)
    {
        if (arcs.Length < 2)
        {
            throw new ArgumentException("An OBJECT IDENTIFIER requires at least two arcs.");
        }

        if (arcs[0] < 0 || arcs[0] > 2)
        {
            throw new ArgumentException($"The first arc must be 0, 1 or 2 but was {arcs[0]}.");
        }

        if (arcs[0] < 2 && (arcs[1] < 0 || arcs[1] > 39))
        {
            throw new ArgumentException($"The second arc under {arcs[0]} must be in 0..39 but was {arcs[1]}.");
        }

        if (arcs.Any(a => a.Sign < 0))
        {
            throw new ArgumentException("Arcs must be non-negative.");
        }
    }

    /// <inheritdoc />
    public bool Equals(ObjectIdentifier? other) => other is not null && Arcs.SequenceEqual(other.Arcs);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as ObjectIdentifier);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var h = new HashCode();
        foreach (var a in Arcs)
        {
            h.Add(a);
        }

        return h.ToHashCode();
    }

    /// <inheritdoc />
    public int CompareTo(ObjectIdentifier? other)
    {
        if (other is null)
        {
            return 1;
        }

        for (var i = 0; i < Math.Min(Arcs.Length, other.Arcs.Length); i++)
        {
            var c = Arcs[i].CompareTo(other.Arcs[i]);
            if (c != 0)
            {
                return c;
            }
        }

        return Arcs.Length.CompareTo(other.Arcs.Length);
    }

    /// <summary>Equality operator.</summary>
    public static bool operator ==(ObjectIdentifier? a, ObjectIdentifier? b) => a is null ? b is null : a.Equals(b);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(ObjectIdentifier? a, ObjectIdentifier? b) => !(a == b);

    /// <summary>Dotted-decimal form.</summary>
    public override string ToString() => _text ??= string.Join('.', Arcs.Select(a => a.ToString(CultureInfo.InvariantCulture)));
}

/// <summary>An immutable RELATIVE-OID value (X.680 33).</summary>
public sealed class RelativeObjectIdentifier : IEquatable<RelativeObjectIdentifier>
{
    /// <summary>Creates a relative OID.</summary>
    public RelativeObjectIdentifier(IEnumerable<BigInteger> arcs)
    {
        Arcs = [.. arcs];
        if (Arcs.Length == 0 || Arcs.Any(a => a.Sign < 0))
        {
            throw new ArgumentException("A RELATIVE-OID requires at least one non-negative arc.");
        }
    }

    /// <summary>Creates a relative OID.</summary>
    public RelativeObjectIdentifier(params long[] arcs) : this(arcs.Select(a => (BigInteger)a))
    {
    }

    /// <summary>The arcs.</summary>
    public ImmutableArray<BigInteger> Arcs { get; }

    /// <summary>Parses dotted-decimal notation.</summary>
    public static RelativeObjectIdentifier Parse(string text) =>
        new(text.Split('.').Select(p => p.Length > 0 && p.All(char.IsAsciiDigit) ? BigInteger.Parse(p, NumberStyles.None, CultureInfo.InvariantCulture) : throw new FormatException($"'{text}' is not a valid RELATIVE-OID")));

    /// <inheritdoc />
    public bool Equals(RelativeObjectIdentifier? other) => other is not null && Arcs.SequenceEqual(other.Arcs);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as RelativeObjectIdentifier);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var h = new HashCode();
        foreach (var a in Arcs)
        {
            h.Add(a);
        }

        return h.ToHashCode();
    }

    /// <summary>Equality operator.</summary>
    public static bool operator ==(RelativeObjectIdentifier? a, RelativeObjectIdentifier? b) => a is null ? b is null : a.Equals(b);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(RelativeObjectIdentifier? a, RelativeObjectIdentifier? b) => !(a == b);

    /// <inheritdoc />
    public override string ToString() => string.Join('.', Arcs.Select(a => a.ToString(CultureInfo.InvariantCulture)));
}
