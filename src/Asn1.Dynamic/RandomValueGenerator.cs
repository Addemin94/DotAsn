using System.Numerics;
using Asn1.Constraints;
using Asn1.Runtime;
using Asn1.Semantics;

namespace Asn1.Dynamic;

/// <summary>Options for <see cref="RandomValueGenerator"/>.</summary>
public sealed record RandomValueOptions
{
    /// <summary>Maximum structural depth; deeper optional components are omitted and finite alternatives preferred.</summary>
    public int MaxDepth { get; init; } = 8;

    /// <summary>Maximum element count for unbounded collections.</summary>
    public int MaxUnboundedCount { get; init; } = 3;

    /// <summary>Maximum length for unbounded strings.</summary>
    public int MaxUnboundedLength { get; init; } = 12;

    /// <summary>Probability of including an OPTIONAL/DEFAULT component.</summary>
    public double OptionalProbability { get; init; } = 0.5;

    /// <summary>Probability of including extension additions and extension alternatives/items.</summary>
    public double ExtensionProbability { get; init; } = 0.3;

    /// <summary>Generate the REAL special values (infinity, NaN, minus zero).</summary>
    public bool IncludeSpecialReals { get; init; } = true;

    /// <summary>Characters used for UTF8String/BMPString/UniversalString values.</summary>
    public string WideCharacterSample { get; init; } = "Aaz09 ~éß€Ωж中";
}

/// <summary>
/// Generates random values of resolved types that satisfy their PER-visible constraints (value ranges, sizes,
/// permitted alphabets). Used for property-based tests, fuzzing seeds and interoperability vector generation.
/// </summary>
public sealed class RandomValueGenerator(Random random, RandomValueOptions? options = null)
{
    private readonly RandomValueOptions _options = options ?? new RandomValueOptions();

    /// <summary>Generates a value of <paramref name="type"/> in the dynamic value model.</summary>
    public object? Generate(AsnType type) => Gen(type, 0);

    private bool Chance(double p) => random.NextDouble() < p;

    private object? Gen(AsnType t, int depth)
    {
        var def = t.Definition;
        switch (t.Kind)
        {
            case TypeKind.Boolean:
                return random.Next(2) == 1;
            case TypeKind.Null:
                return Asn1Null.Value;
            case TypeKind.Integer:
                return PickInteger(IntegerRootSet(t.Constraints) ?? IntegerSet.All);
            case TypeKind.Enumerated:
                {
                    var allowed = def.EnumItems.Where(i => !i.IsExtension || Chance(_options.ExtensionProbability)).Where(i => Satisfies(t, ConstraintSubject.FromInteger(i.Value))).ToList();
                    if (allowed.Count == 0)
                    {
                        allowed = [.. def.EnumItems.Where(i => !i.IsExtension)];
                    }

                    return allowed[random.Next(allowed.Count)].Name;
                }

            case TypeKind.Real:
                {
                    double[] specials = _options.IncludeSpecialReals ? [0.0, -0.0, double.PositiveInfinity, double.NegativeInfinity, 1.0, -1.5, 0.1, 3.14159, 1e300, -2.5e-10, 123456789.0] : [0.0, 1.0, -1.5, 0.1, 3.14159, 1e300, -2.5e-10, 123456789.0];
                    var value = Chance(0.5) ? specials[random.Next(specials.Length)] : (random.NextDouble() - 0.5) * Math.Pow(10, random.Next(-20, 20));
                    return Satisfies(t, ConstraintSubject.FromReal(value)) ? value : 0.0;
                }

            case TypeKind.BitString:
                {
                    var length = PickSize(t, 64);
                    var bytes = new byte[(length + 7) / 8];
                    random.NextBytes(bytes);
                    var bits = new Asn1BitString(bytes, length);
                    if (def.NamedNumbers.Length > 0 && length > 0 && !bits[length - 1])
                    {
                        // Named-bit values are canonical without trailing zeros; set the last bit for unambiguous vectors.
                        bytes[(length - 1) / 8] |= (byte)(0x80 >> (int)((length - 1) % 8));
                        bits = new Asn1BitString(bytes, length);
                    }

                    return bits;
                }

            case TypeKind.OctetString:
                {
                    var length = PickSize(t, _options.MaxUnboundedLength);
                    var bytes = new byte[length];
                    random.NextBytes(bytes);
                    return bytes;
                }

            case TypeKind.ObjectIdentifier:
                {
                    var arcs = new List<BigInteger> { random.Next(3) };
                    arcs.Add(arcs[0] < 2 ? random.Next(40) : random.Next(1000));
                    for (var i = random.Next(0, 6); i > 0; i--)
                    {
                        arcs.Add(Chance(0.2) ? (BigInteger)random.NextInt64(0, long.MaxValue) : random.Next(0, 70000));
                    }

                    return new ObjectIdentifier(arcs);
                }

            case TypeKind.RelativeOid:
                return new RelativeObjectIdentifier(Enumerable.Range(0, random.Next(1, 5)).Select(_ => (BigInteger)random.Next(0, 100000)));
            case TypeKind.CharacterString:
                return GenString(t);
            case TypeKind.UtcTime:
                return $"{random.Next(0, 100):D2}{random.Next(1, 13):D2}{random.Next(1, 29):D2}{random.Next(0, 24):D2}{random.Next(0, 60):D2}{random.Next(0, 60):D2}Z";
            case TypeKind.GeneralizedTime:
                return $"{random.Next(1950, 2100):D4}{random.Next(1, 13):D2}{random.Next(1, 29):D2}{random.Next(0, 24):D2}{random.Next(0, 60):D2}{random.Next(0, 60):D2}Z";
            case TypeKind.Sequence:
            case TypeKind.Set:
                {
                    var record = new Asn1Record();
                    var includeExtensions = depth < _options.MaxDepth && Chance(_options.ExtensionProbability);
                    var groupsIncluded = new Dictionary<int, bool>();
                    foreach (var c in def.Components)
                    {
                        bool include;
                        if (c.IsExtensionAddition)
                        {
                            if (!includeExtensions)
                            {
                                continue;
                            }

                            if (c.IsInExtensionGroup)
                            {
                                if (!groupsIncluded.TryGetValue(c.ExtensionAdditionIndex, out var g))
                                {
                                    g = groupsIncluded[c.ExtensionAdditionIndex] = Chance(0.6);
                                }

                                include = g && (!c.IsOptional || Chance(_options.OptionalProbability));
                            }
                            else
                            {
                                include = Chance(0.6);
                            }

                            // Additions must be present in order up to the last one included (version semantics).
                        }
                        else
                        {
                            include = !c.IsOptional || (depth < _options.MaxDepth && Chance(_options.OptionalProbability));
                        }

                        if (include)
                        {
                            record[c.Name] = Gen(c.Type, depth + 1);
                        }
                    }

                    return record;
                }

            case TypeKind.Choice:
                {
                    var candidates = def.Components.Where(c => !c.IsExtensionAddition || Chance(_options.ExtensionProbability)).ToList();
                    if (depth >= _options.MaxDepth)
                    {
                        var finite = candidates.Where(c => !(c.Type.Definition.IsStructured || c.Type.Definition.IsCollection)).ToList();
                        if (finite.Count > 0)
                        {
                            candidates = finite;
                        }
                    }

                    var alt = candidates.Count > 0 ? candidates[random.Next(candidates.Count)] : def.Components[0];
                    return new Asn1Choice(alt.Name, Gen(alt.Type, depth + 1));
                }

            case TypeKind.SequenceOf:
            case TypeKind.SetOf:
                {
                    var count = PickSize(t, depth >= _options.MaxDepth ? 0 : _options.MaxUnboundedCount, preferSmall: true);
                    var list = new List<object?>();
                    for (var i = 0; i < count; i++)
                    {
                        list.Add(Gen(def.ElementType!, depth + 1));
                    }

                    return list;
                }

            default:
                throw new NotSupportedException($"cannot generate values of {AsnType.KindName(t.Kind)}");
        }
    }

    private static bool Satisfies(AsnType t, ConstraintSubject subject) =>
        t.Constraints.IsDefaultOrEmpty || ConstraintEvaluator.CheckSerial(t.Constraints, subject) is ConstraintCheck.InRoot;

    /// <summary>The integer root set of serially applied constraints (each constraint's root only), or null if not representable.</summary>
    internal static IntegerSet? IntegerRootSet(IEnumerable<Asn1Constraint> constraints)
    {
        var acc = IntegerSet.All;
        foreach (var c in constraints)
        {
            var s = ConstraintSets.IntegerRoot(c);
            if (s is null)
            {
                return null;
            }

            acc = acc.Intersect(s);
        }

        return acc;
    }

    private BigInteger PickInteger(IntegerSet set)
    {
        if (set.IsEmpty)
        {
            return 0;
        }

        var interval = set.Intervals[random.Next(set.Intervals.Length)];
        var lo = interval.Min ?? (interval.Max is { } mx ? mx - RandomMagnitude() : -RandomMagnitude());
        var hi = interval.Max ?? (lo + RandomMagnitude());
        if (lo > hi)
        {
            (lo, hi) = (hi, lo);
        }

        // Favour boundary values.
        switch (random.Next(6))
        {
            case 0:
                return lo;
            case 1:
                return hi;
            case 2 when lo <= 0 && hi >= 0:
                return 0;
        }

        var range = hi - lo;
        if (range.IsZero)
        {
            return lo;
        }

        var bytes = range.ToByteArray(isUnsigned: true);
        var buffer = new byte[bytes.Length + 1];
        random.NextBytes(buffer);
        buffer[^1] = 0;
        return lo + (new BigInteger(buffer) % (range + 1));
    }

    private BigInteger RandomMagnitude() => random.Next(5) switch
    {
        0 => 100,
        1 => 70000,
        2 => BigInteger.Pow(2, 40),
        3 => BigInteger.Pow(2, 64) + 5,
        _ => BigInteger.Pow(2, 100),
    };

    private long PickSize(AsnType t, int unboundedMax, bool preferSmall = false)
    {
        IntegerSet? sizes = IntegerSet.Range(0, null);
        foreach (var c in t.Constraints)
        {
            if (c.Root is SizeElement s && ConstraintSets.IntegerRoot(s.Constraint) is { } set)
            {
                sizes = sizes.Intersect(set);
            }
            else if (c.Root is IntersectionElement x)
            {
                foreach (var se in x.Items.OfType<SizeElement>())
                {
                    if (ConstraintSets.IntegerRoot(se.Constraint) is { } set2)
                    {
                        sizes = sizes.Intersect(set2);
                    }
                }
            }
        }

        if (sizes.IsEmpty)
        {
            return 0;
        }

        var interval = sizes.Intervals[random.Next(sizes.Intervals.Length)];
        var lo = (long)(interval.Min ?? 0);
        var hi = interval.Max is { } m ? (long)BigInteger.Min(m, lo + Math.Max(unboundedMax, preferSmall ? 4 : 300)) : lo + unboundedMax;
        if (preferSmall && hi - lo > 6)
        {
            hi = Chance(0.8) ? lo + 6 : hi;
        }

        return random.Next(3) switch
        {
            0 => lo,
            1 => hi,
            _ => random.NextInt64(lo, hi + 1),
        };
    }

    private string GenString(AsnType t)
    {
        var kind = t.StringKind;
        var candidates = (CharacterRepertoire.SmallAlphabet(kind)?.ToList() ?? (kind is CharacterStringKind.Teletex or CharacterStringKind.Videotex or CharacterStringKind.Graphic or CharacterStringKind.General or CharacterStringKind.ObjectDescriptor
            ? [.. Enumerable.Range(32, 95)]
            : CharacterRepertoire.CodePoints(_options.WideCharacterSample).Where(cp => CharacterRepertoire.IsValid(kind, cp)).ToList()))
            .Where(cp => CharacterAllowed(t, cp)).ToList();
        var length = PickSize(t, _options.MaxUnboundedLength);
        if (candidates.Count == 0)
        {
            return string.Empty;
        }

        var cps = new int[length];
        for (var i = 0; i < length; i++)
        {
            cps[i] = candidates[random.Next(candidates.Count)];
        }

        return CharacterRepertoire.FromCodePoints(cps);
    }

    private static bool CharacterAllowed(AsnType t, int codePoint)
    {
        // A single-character string satisfies a permitted alphabet constraint iff the character is permitted.
        var s = char.ConvertFromUtf32(codePoint);
        foreach (var c in t.Constraints)
        {
            if (!AlphabetOk(c.Root, s))
            {
                return false;
            }
        }

        return true;
    }

    private static bool AlphabetOk(ConstraintElement? e, string ch) => e switch
    {
        PermittedAlphabetElement pa => ConstraintEvaluator.Satisfies(pa.Constraint, ConstraintSubject.FromString(ch)) || ConstraintEvaluator.Evaluate(pa, ConstraintSubject.FromString(ch)),
        IntersectionElement x => x.Items.All(i => AlphabetOk(i, ch)),
        _ => true,
    };
}
