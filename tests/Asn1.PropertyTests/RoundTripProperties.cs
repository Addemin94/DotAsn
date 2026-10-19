using System.Collections.Immutable;
using System.Numerics;
using Asn1.Constraints;
using Asn1.Diagnostics;
using Asn1.Dynamic;
using Asn1.Runtime;
using Asn1.Semantics;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

namespace Asn1.PropertyTests;

/// <summary>Round-trip properties over all implemented encoding rules.</summary>
public class RoundTripProperties
{
    private static readonly Asn1EncodingRule[] s_rules = [.. Asn1EncodingRules.All];

    private static bool RoundTrips<T>(Asn1Type<T> type, T value, Func<T, T, bool> equal)
    {
        foreach (var rule in s_rules)
        {
            var decoded = rule.Decode(type, rule.Encode(type, value));
            if (!equal(value, decoded))
            {
                return false;
            }
        }

        return true;
    }

    [Property(MaxTest = 300)]
    public bool ConstrainedIntegers(long a, long b, long v, bool extensible)
    {
        var lo = Math.Min(a, b);
        var hi = Math.Max(a, b);
        var value = lo + (long)((ulong)(v - long.MinValue) % ((ulong)(hi - lo) + 1UL));
        var constraint = new Asn1Constraint(new ValueRangeElement((BigInteger)lo, (BigInteger)hi), extensible);
        var type = new IntegerType<long>("I", default, [constraint]);
        return RoundTrips(type, value, (x, y) => x == y);
    }

    [Property(MaxTest = 300)]
    public bool UnconstrainedBigIntegers(byte[] bytes, bool negative)
    {
        var v = new BigInteger(bytes.Take(40).ToArray(), isUnsigned: true) * (negative ? -1 : 1);
        return RoundTrips(new IntegerType<BigInteger>(), v, (x, y) => x == y);
    }

    [Property(MaxTest = 200)]
    public bool SemiConstrainedIntegers(int lower, uint offset)
    {
        var type = new IntegerType<long>("I", default, [new Asn1Constraint(new ValueRangeElement((BigInteger)lower, null))]);
        return RoundTrips(type, (long)lower + offset, (x, y) => x == y);
    }

    [Property(MaxTest = 200)]
    public bool OctetStringsWithSizeConstraints(byte[] data, byte slack)
    {
        data ??= [];
        var sizes = new Asn1Constraint(new SizeElement(new Asn1Constraint(new ValueRangeElement((BigInteger)0, (BigInteger)(data.Length + slack)))));
        var type = new OctetStringType("O", default, [sizes]);
        return RoundTrips(type, data, (x, y) => x.AsSpan().SequenceEqual(y)) && RoundTrips(OctetStringType.Instance, data, (x, y) => x.AsSpan().SequenceEqual(y));
    }

    [Property(MaxTest = 200)]
    public bool BitStrings(byte[] data, byte trim)
    {
        data ??= [];
        var length = Math.Max(0, (data.Length * 8L) - (trim % 8));
        var bits = new Asn1BitString(data, length);
        return RoundTrips(BitStringType.Instance, bits, (x, y) => x.Equals(y));
    }

    [Property(MaxTest = 200)]
    public bool VisibleStrings(NonNull<string> raw, byte maxExtra)
    {
        var value = new string(raw.Get.Where(c => c is >= ' ' and <= '~').ToArray());
        var sizes = new Asn1Constraint(new SizeElement(new Asn1Constraint(new ValueRangeElement((BigInteger)0, (BigInteger)(value.Length + maxExtra)))));
        var type = new CharacterStringType(CharacterStringKind.Visible, null, default, [sizes]);
        return RoundTrips(type, value, string.Equals);
    }

    [Property(MaxTest = 200)]
    public bool Utf8Strings(NonNull<string> raw)
    {
        // U+FFFE and U+FFFF cannot appear in XML (X.680 12.15.1); CANONICAL-XER cannot preserve CARRIAGE RETURN
        // (X.680 12.15.3 note 2, X.693 9.1.3), so it is compared after XML end-of-line normalisation.
        var value = CharacterRepertoire.FromCodePoints(CharacterRepertoire.CodePoints(raw.Get).Where(cp => cp >= 0 && cp is not (0xFFFE or 0xFFFF)));
        var type = new CharacterStringType(CharacterStringKind.Utf8);
        return s_rules.All(rule =>
        {
            var decoded = rule.Decode(type, rule.Encode(type, value));
            return rule.Name == "CXER" ? decoded == value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n') : decoded == value;
        });
    }

    [Property(MaxTest = 300)]
    public bool Reals(double value) => RoundTrips(RealType.Instance, value, (x, y) => BitConverter.DoubleToInt64Bits(x) == BitConverter.DoubleToInt64Bits(y) || (double.IsNaN(x) && double.IsNaN(y)));

    [Property(MaxTest = 200)]
    public bool ObjectIdentifiers(byte first, byte second, ulong[] rest)
    {
        var arc0 = first % 3;
        var arcs = new List<BigInteger> { arc0, arc0 < 2 ? second % 40 : second };
        arcs.AddRange((rest ?? []).Take(10).Select(r => (BigInteger)r));
        var oid = new ObjectIdentifier(arcs);
        return RoundTrips(ObjectIdentifierType.Instance, oid, (x, y) => x.Equals(y));
    }

    private static readonly Lazy<(Asn1Compilation Compilation, DynamicTypeFactory Factory)> s_schemas = new(() =>
    {
        var files = new[] { "samples/telemetry/telemetry.asn", "samples/extensibility/extensibility.asn", "corpus/x691_a1.asn", "corpus/x691_a2.asn", "corpus/x691_a3.asn", "corpus/x691_a4.asn" };
        var compilation = Asn1Compilation.Create(files.Select(f => new SourceText(f, File.ReadAllText(Path.Combine(AppContext.BaseDirectory, f)))));
        return (compilation, new DynamicTypeFactory());
    });

    [Property(MaxTest = 150)]
    public bool RandomSchemaValuesRoundTripCanonically(int seed)
    {
        var (compilation, factory) = s_schemas.Value;
        var random = new Random(seed);
        var types = compilation.TypeAssignments.Where(t => t.Type is not null).ToList();
        var ta = types[random.Next(types.Count)];
        var generator = new RandomValueGenerator(random);
        var value = generator.Generate(ta.Type!);
        Asn1Type<object?> type;
        lock (factory)
        {
            type = factory.Create(ta);
        }

        foreach (var rule in s_rules)
        {
            var encoded = rule.Encode(type, value);
            var again = rule.Encode(type, rule.Decode(type, encoded));
            if (!encoded.AsSpan().SequenceEqual(again))
            {
                return false;
            }
        }

        return true;
    }
}
