using System.Collections.Immutable;
using System.Numerics;

namespace Asn1.Constraints;

/// <summary>A closed integer interval; null bounds are unbounded (MIN/MAX).</summary>
/// <param name="Min">Inclusive lower bound or null.</param>
/// <param name="Max">Inclusive upper bound or null.</param>
public readonly record struct IntegerInterval(BigInteger? Min, BigInteger? Max)
{
    /// <summary>True if the interval contains <paramref name="value"/>.</summary>
    public bool Contains(BigInteger value) => (Min is null || value >= Min) && (Max is null || value <= Max);

    /// <inheritdoc />
    public override string ToString() => $"{(Min?.ToString() ?? "MIN")}..{(Max?.ToString() ?? "MAX")}";
}

/// <summary>An immutable set of integers represented as sorted, disjoint, non-adjacent closed intervals.</summary>
public sealed class IntegerSet : IEquatable<IntegerSet>
{
    /// <summary>All integers.</summary>
    public static readonly IntegerSet All = new([new IntegerInterval(null, null)]);

    /// <summary>The empty set.</summary>
    public static readonly IntegerSet Empty = new([]);

    // Marks a set whose bounds do not all fit in 64 bits.
    private static readonly long[] s_notLong = new long[1];

    // The bounds as 64-bit pairs (min, max) for allocation-free membership tests, computed on first use: most sets are
    // intermediate results of constraint arithmetic and are never tested for membership.
    private long[]? _longBounds;

    private IntegerSet(ImmutableArray<IntegerInterval> intervals) => Intervals = intervals;

    /// <summary>The normalized intervals.</summary>
    public ImmutableArray<IntegerInterval> Intervals { get; }

    /// <summary>True if empty.</summary>
    public bool IsEmpty => Intervals.Length == 0;

    /// <summary>Smallest member, or null if unbounded below (or empty).</summary>
    public BigInteger? LowerBound => IsEmpty ? null : Intervals[0].Min;

    /// <summary>Largest member, or null if unbounded above (or empty).</summary>
    public BigInteger? UpperBound => IsEmpty ? null : Intervals[^1].Max;

    /// <summary>True when the set has finite lower and upper bounds.</summary>
    public bool IsBounded => !IsEmpty && LowerBound is not null && UpperBound is not null;

    /// <summary>Creates a set holding one value.</summary>
    public static IntegerSet Single(BigInteger value) => new([new IntegerInterval(value, value)]);

    /// <summary>Creates a set from a range; null bounds are unbounded.</summary>
    public static IntegerSet Range(BigInteger? min, BigInteger? max) =>
        min is not null && max is not null && min > max ? Empty : new([new IntegerInterval(min, max)]);

    /// <summary>True if the set contains <paramref name="value"/>.</summary>
    public bool Contains(BigInteger value)
    {
        if (value.GetBitLength() < 64 && GetLongBounds() is { } bounds)
        {
            return Contains(bounds, (long)value);
        }

        foreach (var i in Intervals)
        {
            if (i.Contains(value))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True if the set contains <paramref name="value"/>.</summary>
    public bool Contains(long value) => GetLongBounds() is { } bounds ? Contains(bounds, value) : Contains((BigInteger)value);

    private static bool Contains(long[] bounds, long value)
    {
        for (var i = 0; i < bounds.Length; i += 2)
        {
            if (value >= bounds[i] && value <= bounds[i + 1])
            {
                return true;
            }
        }

        return false;
    }

    private long[]? GetLongBounds()
    {
        var bounds = Volatile.Read(ref _longBounds);
        if (bounds is null)
        {
            // Concurrent first uses compute the same array; either result may be kept.
            bounds = LongBounds(Intervals) ?? s_notLong;
            Volatile.Write(ref _longBounds, bounds);
        }

        return ReferenceEquals(bounds, s_notLong) ? null : bounds;
    }

    private static long[]? LongBounds(ImmutableArray<IntegerInterval> intervals)
    {
        var bounds = new long[intervals.Length * 2];
        for (var i = 0; i < intervals.Length; i++)
        {
            if (!TryLong(intervals[i].Min, long.MinValue, out bounds[2 * i]) || !TryLong(intervals[i].Max, long.MaxValue, out bounds[(2 * i) + 1]))
            {
                return null;
            }
        }

        return bounds;
    }

    private static bool TryLong(BigInteger? bound, long unbounded, out long value)
    {
        if (bound is not { } b)
        {
            value = unbounded;
            return true;
        }

        // GetBitLength excludes the sign bit, so values of up to 63 bits fit (long.MinValue included).
        if (b.GetBitLength() < 64)
        {
            value = (long)b;
            return true;
        }

        value = 0;
        return false;
    }

    /// <summary>Union.</summary>
    public IntegerSet Union(IntegerSet other) => Normalize(Intervals.Concat(other.Intervals));

    /// <summary>Intersection.</summary>
    public IntegerSet Intersect(IntegerSet other)
    {
        var result = new List<IntegerInterval>();
        foreach (var a in Intervals)
        {
            foreach (var b in other.Intervals)
            {
                var min = MaxLower(a.Min, b.Min);
                var max = MinUpper(a.Max, b.Max);
                if (min is null || max is null || min <= max)
                {
                    result.Add(new IntegerInterval(min, max));
                }
            }
        }

        return Normalize(result);
    }

    /// <summary>Complement relative to all integers.</summary>
    public IntegerSet Complement()
    {
        if (IsEmpty)
        {
            return All;
        }

        var result = new List<IntegerInterval>();
        BigInteger? cursor = null;
        var first = true;
        foreach (var i in Intervals)
        {
            if (first)
            {
                if (i.Min is not null)
                {
                    result.Add(new IntegerInterval(null, i.Min - 1));
                }

                first = false;
            }
            else if (cursor is not null && i.Min is not null)
            {
                result.Add(new IntegerInterval(cursor + 1, i.Min - 1));
            }

            cursor = i.Max;
            if (cursor is null)
            {
                return Normalize(result);
            }
        }

        result.Add(new IntegerInterval(cursor + 1, null));
        return Normalize(result);
    }

    /// <summary>Set difference.</summary>
    public IntegerSet Except(IntegerSet other) => Intersect(other.Complement());

    private static BigInteger? MaxLower(BigInteger? a, BigInteger? b) => a is null ? b : b is null ? a : BigInteger.Max(a.Value, b.Value);

    private static BigInteger? MinUpper(BigInteger? a, BigInteger? b) => a is null ? b : b is null ? a : BigInteger.Min(a.Value, b.Value);

    private static IntegerSet Normalize(IEnumerable<IntegerInterval> intervals)
    {
        var sorted = intervals.OrderBy(i => i.Min is null ? 0 : 1).ThenBy(i => i.Min ?? BigInteger.Zero).ToList();
        var result = new List<IntegerInterval>();
        foreach (var i in sorted)
        {
            if (result.Count == 0)
            {
                result.Add(i);
                continue;
            }

            var last = result[^1];
            if (last.Max is null || i.Min is null || i.Min <= last.Max + 1)
            {
                BigInteger? max = last.Max is null || i.Max is null ? null : BigInteger.Max(last.Max.Value, i.Max.Value);
                result[^1] = new IntegerInterval(last.Min, max);
            }
            else
            {
                result.Add(i);
            }
        }

        return new IntegerSet([.. result]);
    }

    /// <inheritdoc />
    public bool Equals(IntegerSet? other) => other is not null && Intervals.SequenceEqual(other.Intervals);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as IntegerSet);

    /// <inheritdoc />
    public override int GetHashCode() => Intervals.Aggregate(0, (h, i) => HashCode.Combine(h, i));

    /// <inheritdoc />
    public override string ToString() => IsEmpty ? "{}" : string.Join(" | ", Intervals);
}
