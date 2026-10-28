using System.Collections.Immutable;
using System.Numerics;
using Asn1.Runtime;
using Asn1.Semantics;

namespace Asn1.Dynamic;

/// <summary>
/// Builds runtime descriptors (<c>Asn1Type&lt;object?&gt;</c>) from resolved semantic types. Values use the
/// dynamic model: BigInteger, bool, double, string (also ENUMERATED identifiers and times), byte[], Asn1BitString,
/// ObjectIdentifier, RelativeObjectIdentifier, Asn1Null, <see cref="Asn1Record"/>, List&lt;object?&gt;,
/// <see cref="Asn1Choice"/> and <see cref="Asn1OpenValue"/>.
/// </summary>
public sealed class DynamicTypeFactory
{
    private readonly Dictionary<AsnType, Asn1Type<object?>> _cache = new(ReferenceEqualityComparer.Instance);
    private readonly Lock _lock = new();

    /// <summary>
    /// Creates (or returns the cached) descriptor for a resolved type. Thread-safe: descriptors resolve their
    /// components lazily through this factory, possibly from several threads encoding or decoding concurrently.
    /// </summary>
    public Asn1Type<object?> Create(AsnType type)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(type, out var cached))
            {
                return cached;
            }

            var created = Build(type);
            _cache[type] = created;
            return created;
        }
    }

    /// <summary>Creates the descriptor of a type assignment.</summary>
    public Asn1Type<object?> Create(TypeAssignmentSymbol assignment) =>
        Create(assignment.Type ?? throw new InvalidOperationException($"'{assignment.Name}' is parameterized and cannot be used directly"));

    internal static Asn1Type<object?> Box<T>(Asn1Type<T> inner) => inner switch
    {
        IStructuredTypeInfo => new BoxedStructuredType<T>(inner),
        ICollectionTypeInfo => new BoxedCollectionType<T>(inner),
        _ => new BoxedType<T>(inner),
    };

    private static string NameOf(AsnType t) =>
        t.Assignment is { } a ? a.QualifiedName : t.Reference is { } r ? r.QualifiedName : t.Definition.Assignment?.QualifiedName ?? AsnType.KindName(t.Kind, t.StringKind);

    private Asn1Type<object?> Build(AsnType t)
    {
        var name = NameOf(t);
        var tags = t.Tags.Select(x => new Asn1TagStep(x.Tag, x.IsExplicit)).ToImmutableArray();
        var constraints = t.Constraints;
        var def = t.Definition;
        switch (t.Kind)
        {
            case TypeKind.Boolean:
                return Box(new BooleanType(name, tags, constraints));
            case TypeKind.Integer:
                return Box(new IntegerType<BigInteger>(name, tags, constraints, NamedNumbers(def)));
            case TypeKind.Enumerated:
                {
                    if (def.EnumItems.Any(i => i.Value < long.MinValue || i.Value > long.MaxValue))
                    {
                        throw new Asn1UnsupportedException($"enumeration values of '{name}' must fit in 64 bits");
                    }

                    var info = new EnumerationInfo(def.EnumItems.Select(i => (i.Name, (long)i.Value, i.IsExtension)), def.IsExtensible);
                    var byValue = def.EnumItems.ToDictionary(i => (long)i.Value, i => (object?)i.Name);
                    return Box(new EnumeratedType<object?>(
                        name,
                        info,
                        v => v is string s && info.FindByName(s) is { } item ? item.Value : long.MinValue,
                        v => byValue[v],
                        item => "#unknown-extension-" + item.Index,
                        tags,
                        constraints));
                }

            case TypeKind.Real:
                return Box(new RealType(name, tags, constraints));
            case TypeKind.BitString:
                if (def.NamedNumbers.Any(n => !Asn1NamedNumber.CanHold(n.Value)))
                {
                    throw new Asn1UnsupportedException($"named bit positions of '{name}' must fit in 64 bits");
                }

                return Box(new BitStringType(name, tags, constraints, NamedNumbers(def)));
            case TypeKind.OctetString:
                return Box(new OctetStringType(name, tags, constraints));
            case TypeKind.Null:
                return Box(new NullType(name, tags, constraints));
            case TypeKind.ObjectIdentifier:
                return Box(new ObjectIdentifierType(name, tags, constraints));
            case TypeKind.RelativeOid:
                return Box(new RelativeOidType(name, tags, constraints));
            case TypeKind.CharacterString:
                return Box(new CharacterStringType(t.StringKind, name, tags, constraints));
            case TypeKind.UtcTime:
            case TypeKind.GeneralizedTime:
            case TypeKind.Date:
            case TypeKind.TimeOfDay:
            case TypeKind.DateTime:
            case TypeKind.Duration:
            case TypeKind.Time:
                return Box(new TimeType(TimeKindOf(t.Kind), name, tags, constraints));
            case TypeKind.Sequence:
            case TypeKind.Set:
                {
                    var components = def.Components;
                    var seq = new SequenceType<Asn1Record>(
                        name,
                        t.Kind == TypeKind.Set,
                        () => new Asn1Record(),
                        () => components.Select(c => MakeComponent(c)),
                        def.IsExtensible,
                        tags,
                        constraints);
                    return Box(seq);
                }

            case TypeKind.Choice:
                {
                    var alternatives = def.Components;
                    var indexByName = alternatives.Select((a, i) => (a.Name, i)).ToDictionary(x => x.Name, x => x.i, StringComparer.Ordinal);
                    var choice = new ChoiceType<Asn1Choice>(
                        name,
                        () => alternatives.Select(a => (ChoiceAlternative<Asn1Choice>)new ChoiceAlternative<Asn1Choice, object?>(
                            a.Name,
                            () => Create(a.Type),
                            c => c.Value,
                            v => new Asn1Choice(a.Name, v),
                            a.ExtensionAdditionIndex,
                            a.IsInExtensionGroup)),
                        c => c.Alternative is not null && indexByName.TryGetValue(c.Alternative, out var i) ? i : -1,
                        def.IsExtensible,
                        (index, raw) => new Asn1Choice(null, raw, index),
                        c => c.Alternative is null && c.Value is Asn1OpenValue raw ? (c.UnknownIndex, raw) : null,
                        tags,
                        constraints);
                    return Box(choice);
                }

            case TypeKind.SequenceOf:
            case TypeKind.SetOf:
                {
                    var element = def.ElementType!;
                    var coll = new CollectionType<object?>(name, t.Kind == TypeKind.SetOf, () => Create(element), def.ElementName, tags, constraints);
                    return Box(coll);
                }

            case TypeKind.OpenType:
                return Box(new OpenType(name, tags, constraints));
            default:
                throw new Asn1UnsupportedException($"{AsnType.KindName(t.Kind)} ('{name}') is not supported by the dynamic runtime");
        }
    }

    /// <summary>Named numbers and bits as descriptor metadata (see <see cref="Asn1NamedNumber.CanHold"/>).</summary>
    private static ImmutableArray<Asn1NamedNumber> NamedNumbers(AsnType def) =>
        [.. def.NamedNumbers.Where(n => Asn1NamedNumber.CanHold(n.Value)).Select(n => new Asn1NamedNumber(n.Name, (long)n.Value))];

    private SequenceComponent<Asn1Record> MakeComponent(AsnComponent c)
    {
        var presence = c.Presence switch
        {
            Presence.Optional => Asn1Presence.Optional,
            Presence.Default => Asn1Presence.Default,
            _ => Asn1Presence.Required,
        };
        var defaultValue = c.DefaultValue is { } dv and not ErrorValue ? DynamicValueConverter.FromSemanticValue(dv, c.Type) : null;
        return new SequenceComponent<Asn1Record, object?>(
            c.Name,
            () => Create(c.Type),
            r => r.TryGetValue(c.Name, out var v) ? v : defaultValue,
            (r, v) => r[c.Name] = v,
            presence,
            isPresent: r => r.Contains(c.Name) || (presence == Asn1Presence.Default && defaultValue is not null),
            isDefault: presence == Asn1Presence.Default && defaultValue is not null ? r => !r.TryGetValue(c.Name, out var v) || DynamicEquality.ValuesEqual(v, defaultValue) : null,
            applyDefault: defaultValue is null ? null : r => r[c.Name] = defaultValue,
            c.ExtensionAdditionIndex,
            c.IsInRootPart2,
            c.IsInExtensionGroup);
    }

    private static TimeKind TimeKindOf(TypeKind k) => k switch
    {
        TypeKind.UtcTime => TimeKind.UtcTime,
        TypeKind.GeneralizedTime => TimeKind.GeneralizedTime,
        TypeKind.Date => TimeKind.Date,
        TypeKind.TimeOfDay => TimeKind.TimeOfDay,
        TypeKind.DateTime => TimeKind.DateTime,
        TypeKind.Duration => TimeKind.Duration,
        _ => TimeKind.Time,
    };
}
