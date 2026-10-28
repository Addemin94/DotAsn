using System.Collections.Immutable;
using System.Numerics;
using Asn1.Constraints;

namespace Asn1.Runtime;

/// <summary>A named number or named bit carried as metadata.</summary>
/// <param name="Name">Identifier.</param>
/// <param name="Value">Number or bit position.</param>
public readonly record struct Asn1NamedNumber(string Name, long Value)
{
    /// <summary>
    /// True if a named number or bit position can be carried (it fits in 64 bits). X.680 allows any INTEGER value for
    /// named numbers; larger ones are left out of the metadata, and BIT STRING types with larger positions are
    /// unsupported (their presence decides whether the type has named bits, which affects DER).
    /// </summary>
    public static bool CanHold(BigInteger value) => value >= long.MinValue && value <= long.MaxValue;
}

/// <summary>Helpers shared by primitive descriptors.</summary>
internal static class Tagging
{
    public static ImmutableArray<Asn1TagStep> OrUniversal(ImmutableArray<Asn1TagStep> tags, int universal) =>
        tags.IsDefault ? [new Asn1TagStep(Asn1Tag.Universal(universal), false)] : tags;

    public static ImmutableArray<Asn1Constraint> Append(ImmutableArray<Asn1Constraint> existing, ImmutableArray<Asn1Constraint> additional) =>
        existing.IsDefaultOrEmpty ? (additional.IsDefault ? [] : additional) : additional.IsDefaultOrEmpty ? existing : existing.AddRange(additional);
}

/// <summary>BOOLEAN.</summary>
public sealed class BooleanType : Asn1Type<bool>
{
    /// <summary>The plain BOOLEAN type.</summary>
    public static readonly BooleanType Instance = new();

    /// <summary>Creates a BOOLEAN descriptor.</summary>
    public BooleanType(string name = "BOOLEAN", ImmutableArray<Asn1TagStep> tags = default, ImmutableArray<Asn1Constraint> constraints = default)
        : base(name, Asn1TypeKind.Boolean, Tagging.OrUniversal(tags, UniversalTagNumber.Boolean), constraints)
    {
    }

    /// <inheritdoc />
    public override void Encode(Asn1Writer writer, bool value)
    {
        ValidateForEncoding(writer, value);
        writer.WriteBoolean(this, value);
    }

    /// <inheritdoc />
    public override bool Decode(Asn1Reader reader) => ValidateDecoded(reader, reader.ReadBoolean(this));

    /// <inheritdoc />
    public override Asn1Type<bool> WithTags(ImmutableArray<Asn1TagStep> tags) => new BooleanType(Name, tags, Constraints);

    /// <inheritdoc />
    public override Asn1Type<bool> WithConstraints(ImmutableArray<Asn1Constraint> additional, string? name = null) => new BooleanType(name ?? Name, Tags, Tagging.Append(Constraints, additional));

    /// <inheritdoc />
    public override ConstraintSubject ToConstraintSubject(bool value) => ConstraintSubject.FromBoolean(value);
}

/// <summary>NULL.</summary>
public sealed class NullType : Asn1Type<Asn1Null>
{
    /// <summary>The plain NULL type.</summary>
    public static readonly NullType Instance = new();

    /// <summary>Creates a NULL descriptor.</summary>
    public NullType(string name = "NULL", ImmutableArray<Asn1TagStep> tags = default, ImmutableArray<Asn1Constraint> constraints = default)
        : base(name, Asn1TypeKind.Null, Tagging.OrUniversal(tags, UniversalTagNumber.Null), constraints)
    {
    }

    /// <inheritdoc />
    public override void Encode(Asn1Writer writer, Asn1Null value) => writer.WriteNull(this);

    /// <inheritdoc />
    public override Asn1Null Decode(Asn1Reader reader)
    {
        reader.ReadNull(this);
        return Asn1Null.Value;
    }

    /// <inheritdoc />
    public override Asn1Type<Asn1Null> WithTags(ImmutableArray<Asn1TagStep> tags) => new NullType(Name, tags, Constraints);

    /// <inheritdoc />
    public override Asn1Type<Asn1Null> WithConstraints(ImmutableArray<Asn1Constraint> additional, string? name = null) => new NullType(name ?? Name, Tags, Tagging.Append(Constraints, additional));

    /// <inheritdoc />
    public override ConstraintSubject ToConstraintSubject(Asn1Null value) => ConstraintSubject.FromNull();
}

/// <summary>
/// INTEGER mapped to a CLR integer type: any of <see cref="sbyte"/>, <see cref="byte"/>, <see cref="short"/>,
/// <see cref="ushort"/>, <see cref="int"/>, <see cref="uint"/>, <see cref="long"/>, <see cref="ulong"/> or
/// <see cref="BigInteger"/>. Values that do not fit the CLR type are reported as decoding errors, never truncated.
/// </summary>
/// <typeparam name="T">The CLR integer type.</typeparam>
public sealed class IntegerType<T> : Asn1Type<T>
    where T : struct, IBinaryInteger<T>
{
    private static readonly bool s_fitsInt64 = typeof(T) == typeof(int) || typeof(T) == typeof(long) || typeof(T) == typeof(short) ||
        typeof(T) == typeof(sbyte) || typeof(T) == typeof(byte) || typeof(T) == typeof(ushort) || typeof(T) == typeof(uint);

    private readonly IntegerConstraintCheck? _fast;

    /// <summary>Creates an INTEGER descriptor.</summary>
    public IntegerType(string name = "INTEGER", ImmutableArray<Asn1TagStep> tags = default, ImmutableArray<Asn1Constraint> constraints = default, ImmutableArray<Asn1NamedNumber> namedNumbers = default)
        : base(name, Asn1TypeKind.Integer, Tagging.OrUniversal(tags, UniversalTagNumber.Integer), constraints)
    {
        NamedNumbers = namedNumbers.IsDefault ? [] : namedNumbers;
        _fast = IntegerConstraintCheck.TryCreate(Constraints);
    }

    /// <summary>Named numbers (metadata).</summary>
    public ImmutableArray<Asn1NamedNumber> NamedNumbers { get; }

    /// <inheritdoc />
    public override void Encode(Asn1Writer writer, T value)
    {
        ValidateForEncoding(writer, value);
        if (s_fitsInt64)
        {
            writer.WriteInteger(this, long.CreateTruncating(value));
        }
        else
        {
            writer.WriteInteger(this, BigInteger.CreateTruncating(value));
        }
    }

    /// <inheritdoc />
    public override T Decode(Asn1Reader reader)
    {
        var big = reader.ReadInteger(this);
        T value;
        try
        {
            value = T.CreateChecked(big);
        }
        catch (OverflowException)
        {
            throw reader.Error(Asn1DecodeError.Overflow, $"INTEGER value {big} does not fit the CLR type {typeof(T).Name} of {Name}");
        }

        return ValidateDecoded(reader, value);
    }

    /// <inheritdoc />
    public override ConstraintCheck CheckConstraints(T value) =>
        _fast is not null ? _fast.Check(BigInteger.CreateTruncating(value)) : base.CheckConstraints(value);

    /// <inheritdoc />
    public override Asn1Type<T> WithTags(ImmutableArray<Asn1TagStep> tags) => new IntegerType<T>(Name, tags, Constraints, NamedNumbers);

    /// <inheritdoc />
    public override Asn1Type<T> WithConstraints(ImmutableArray<Asn1Constraint> additional, string? name = null) => new IntegerType<T>(name ?? Name, Tags, Tagging.Append(Constraints, additional), NamedNumbers);

    /// <inheritdoc />
    public override ConstraintSubject ToConstraintSubject(T value) => ConstraintSubject.FromInteger(BigInteger.CreateTruncating(value));
}

/// <summary>Precomputed interval-based checking of integer constraints.</summary>
internal sealed class IntegerConstraintCheck
{
    private readonly IntegerSet _nonFinal;
    private readonly IntegerSet _root;
    private readonly IntegerSet? _additions;
    private readonly bool _extensible;

    private IntegerConstraintCheck(IntegerSet nonFinal, IntegerSet root, IntegerSet? additions, bool extensible)
    {
        _nonFinal = nonFinal;
        _root = root;
        _additions = additions;
        _extensible = extensible;
    }

    public static IntegerConstraintCheck? TryCreate(ImmutableArray<Asn1Constraint> constraints)
    {
        if (constraints.IsEmpty)
        {
            return null;
        }

        var nonFinal = ConstraintSets.IntegerSerial(constraints.Take(constraints.Length - 1));
        var last = constraints[^1];
        var root = ConstraintSets.IntegerRoot(last);
        IntegerSet? additions = null;
        if (last.Additions is not null)
        {
            additions = ConstraintSets.IntegerElement(last.Additions);
            if (additions is null)
            {
                return null;
            }
        }

        return nonFinal is null || root is null ? null : new IntegerConstraintCheck(nonFinal, root, additions, last.IsExtensible);
    }

    public ConstraintCheck Check(BigInteger value)
    {
        if (!_nonFinal.Contains(value))
        {
            return ConstraintCheck.Violation;
        }

        if (_root.Contains(value))
        {
            return ConstraintCheck.InRoot;
        }

        if (!_extensible)
        {
            return ConstraintCheck.Violation;
        }

        return _additions is not null && _additions.Contains(value) ? ConstraintCheck.InAdditions : ConstraintCheck.OutsideExtensible;
    }
}

/// <summary>An item of an enumeration.</summary>
/// <param name="Name">Identifier, or null for an unknown extension received from a later version.</param>
/// <param name="Value">The numeric value (undefined for unknown extensions).</param>
/// <param name="IsExtension">True for additional enumerations.</param>
/// <param name="Index">Index among root items sorted by value, or among additions in definition order.</param>
public sealed record EnumItemInfo(string? Name, long Value, bool IsExtension, int Index)
{
    /// <summary>True for an extension item unknown to this version.</summary>
    public bool IsUnknown => Name is null;
}

/// <summary>The enumeration of an ENUMERATED type.</summary>
public sealed class EnumerationInfo
{
    private readonly Dictionary<long, EnumItemInfo> _byValue = [];
    private readonly Dictionary<string, EnumItemInfo> _byName = new(StringComparer.Ordinal);

    /// <summary>Creates an enumeration from items in definition order.</summary>
    public EnumerationInfo(IEnumerable<(string Name, long Value, bool IsExtension)> items, bool isExtensible)
    {
        var list = items.ToList();
        var root = list.Where(i => !i.IsExtension).OrderBy(i => i.Value).Select((i, idx) => new EnumItemInfo(i.Name, i.Value, false, idx)).ToImmutableArray();
        var additions = list.Where(i => i.IsExtension).Select((i, idx) => new EnumItemInfo(i.Name, i.Value, true, idx)).ToImmutableArray();
        Root = root;
        Additions = additions;
        IsExtensible = isExtensible || additions.Length > 0;
        foreach (var item in root.Concat(additions))
        {
            _byValue[item.Value] = item;
            _byName[item.Name!] = item;
        }
    }

    /// <summary>Root items sorted by value (their PER/OER index order).</summary>
    public ImmutableArray<EnumItemInfo> Root { get; }

    /// <summary>Additional items in definition order.</summary>
    public ImmutableArray<EnumItemInfo> Additions { get; }

    /// <summary>True if the enumeration has an extension marker.</summary>
    public bool IsExtensible { get; }

    /// <summary>Finds an item by value.</summary>
    public EnumItemInfo? FindByValue(long value) => _byValue.GetValueOrDefault(value);

    /// <summary>Finds an item by name.</summary>
    public EnumItemInfo? FindByName(string name) => _byName.GetValueOrDefault(name);

    /// <summary>Returns the root item with the given index, or null.</summary>
    public EnumItemInfo? RootAt(long index) => index >= 0 && index < Root.Length ? Root[(int)index] : null;

    /// <summary>Returns the addition with the given index, or an unknown-extension placeholder.</summary>
    public EnumItemInfo AdditionAt(long index) => index >= 0 && index < Additions.Length ? Additions[(int)index] : new EnumItemInfo(null, 0, true, (int)Math.Min(index, int.MaxValue));
}

/// <summary>ENUMERATED mapped to a CLR type (normally a generated enum).</summary>
/// <typeparam name="T">The CLR type.</typeparam>
public sealed class EnumeratedType<T> : Asn1Type<T>, IFormatDispatch
{
    void IFormatDispatch.AppendFormatted(System.Text.StringBuilder sb, object? value, int depth) => Asn1ValueFormatter.AppendTyped(sb, this, value, depth);

    private readonly Func<T, long> _toValue;
    private readonly Func<long, T> _fromValue;
    private readonly Func<EnumItemInfo, T>? _unknown;

    /// <summary>Creates an ENUMERATED descriptor.</summary>
    /// <param name="name">ASN.1 name.</param>
    /// <param name="enumeration">The enumeration.</param>
    /// <param name="toValue">Maps a CLR value to its enumeration value.</param>
    /// <param name="fromValue">Maps an enumeration value to the CLR value.</param>
    /// <param name="unknownExtension">Produces the CLR value for an unknown extension item; when null, unknown items are decoding errors.</param>
    /// <param name="tags">Effective tags (default UNIVERSAL 10).</param>
    /// <param name="constraints">Constraints.</param>
    public EnumeratedType(string name, EnumerationInfo enumeration, Func<T, long> toValue, Func<long, T> fromValue, Func<EnumItemInfo, T>? unknownExtension = null, ImmutableArray<Asn1TagStep> tags = default, ImmutableArray<Asn1Constraint> constraints = default)
        : base(name, Asn1TypeKind.Enumerated, Tagging.OrUniversal(tags, UniversalTagNumber.Enumerated), constraints)
    {
        Enumeration = enumeration;
        _toValue = toValue;
        _fromValue = fromValue;
        _unknown = unknownExtension;
    }

    /// <summary>The enumeration.</summary>
    public EnumerationInfo Enumeration { get; }

    /// <summary>Maps a CLR value to its enumeration item, or null if the value is not an item.</summary>
    public EnumItemInfo? ItemOf(T value) => Enumeration.FindByValue(_toValue(value));

    /// <summary>Maps an item to the CLR value.</summary>
    public T ValueOf(EnumItemInfo item) => item.IsUnknown
        ? (_unknown is not null ? _unknown(item) : throw new Asn1UnsupportedException($"Unknown extension item #{item.Index} of {Name} cannot be represented"))
        : _fromValue(item.Value);

    /// <inheritdoc />
    public override void Encode(Asn1Writer writer, T value)
    {
        var item = ItemOf(value) ?? throw writer.Error($"{value} is not a value of the enumeration {Name}");
        ValidateForEncoding(writer, value);
        writer.WriteEnumerated(this, Enumeration, item);
    }

    /// <inheritdoc />
    public override T Decode(Asn1Reader reader)
    {
        var item = reader.ReadEnumerated(this, Enumeration);
        if (item.IsUnknown && _unknown is null)
        {
            throw reader.Error(Asn1DecodeError.Unsupported, $"Unknown extension item #{item.Index} of {Name} cannot be represented");
        }

        return ValidateDecoded(reader, ValueOf(item));
    }

    /// <inheritdoc />
    public override Asn1Type<T> WithTags(ImmutableArray<Asn1TagStep> tags) => new EnumeratedType<T>(Name, Enumeration, _toValue, _fromValue, _unknown, tags, Constraints);

    /// <inheritdoc />
    public override Asn1Type<T> WithConstraints(ImmutableArray<Asn1Constraint> additional, string? name = null) => new EnumeratedType<T>(name ?? Name, Enumeration, _toValue, _fromValue, _unknown, Tags, Tagging.Append(Constraints, additional));

    /// <inheritdoc />
    public override ConstraintSubject ToConstraintSubject(T value) => ConstraintSubject.FromInteger(_toValue(value));
}

/// <summary>REAL mapped to <see cref="double"/>.</summary>
public sealed class RealType : Asn1Type<double>
{
    /// <summary>The plain REAL type.</summary>
    public static readonly RealType Instance = new();

    /// <summary>Creates a REAL descriptor.</summary>
    public RealType(string name = "REAL", ImmutableArray<Asn1TagStep> tags = default, ImmutableArray<Asn1Constraint> constraints = default)
        : base(name, Asn1TypeKind.Real, Tagging.OrUniversal(tags, UniversalTagNumber.Real), constraints)
    {
    }

    /// <inheritdoc />
    public override void Encode(Asn1Writer writer, double value)
    {
        ValidateForEncoding(writer, value);
        writer.WriteReal(this, value);
    }

    /// <inheritdoc />
    public override double Decode(Asn1Reader reader) => ValidateDecoded(reader, reader.ReadReal(this));

    /// <inheritdoc />
    public override Asn1Type<double> WithTags(ImmutableArray<Asn1TagStep> tags) => new RealType(Name, tags, Constraints);

    /// <inheritdoc />
    public override Asn1Type<double> WithConstraints(ImmutableArray<Asn1Constraint> additional, string? name = null) => new RealType(name ?? Name, Tags, Tagging.Append(Constraints, additional));

    /// <inheritdoc />
    public override ConstraintSubject ToConstraintSubject(double value) => ConstraintSubject.FromReal(value);
}

/// <summary>BIT STRING mapped to <see cref="Asn1BitString"/>.</summary>
public sealed class BitStringType : Asn1Type<Asn1BitString>
{
    /// <summary>The plain BIT STRING type.</summary>
    public static readonly BitStringType Instance = new();

    /// <summary>Creates a BIT STRING descriptor.</summary>
    public BitStringType(string name = "BIT STRING", ImmutableArray<Asn1TagStep> tags = default, ImmutableArray<Asn1Constraint> constraints = default, ImmutableArray<Asn1NamedNumber> namedBits = default)
        : base(name, Asn1TypeKind.BitString, Tagging.OrUniversal(tags, UniversalTagNumber.BitString), constraints)
    {
        NamedBits = namedBits.IsDefault ? [] : namedBits;
    }

    /// <summary>Named bits (affect canonical trailing-zero handling in DER/CER/PER).</summary>
    public ImmutableArray<Asn1NamedNumber> NamedBits { get; }

    /// <summary>True when the type has a named bit list.</summary>
    public bool HasNamedBits => !NamedBits.IsEmpty;

    /// <inheritdoc />
    public override void Encode(Asn1Writer writer, Asn1BitString value)
    {
        ArgumentNullException.ThrowIfNull(value);
        ValidateForEncoding(writer, value);
        writer.WriteBitString(this, value);
    }

    /// <inheritdoc />
    public override Asn1BitString Decode(Asn1Reader reader) => ValidateDecoded(reader, reader.ReadBitString(this));

    /// <inheritdoc />
    public override ConstraintCheck CheckConstraints(Asn1BitString value)
    {
        var check = base.CheckConstraints(value);

        // X.680 22.7: for named bit lists, trailing zero bits are not significant for SIZE constraints.
        return check == ConstraintCheck.Violation && HasNamedBits ? base.CheckConstraints(value.TrimTrailingZeros()) : check;
    }

    /// <inheritdoc />
    public override Asn1Type<Asn1BitString> WithTags(ImmutableArray<Asn1TagStep> tags) => new BitStringType(Name, tags, Constraints, NamedBits);

    /// <inheritdoc />
    public override Asn1Type<Asn1BitString> WithConstraints(ImmutableArray<Asn1Constraint> additional, string? name = null) => new BitStringType(name ?? Name, Tags, Tagging.Append(Constraints, additional), NamedBits);

    /// <inheritdoc />
    public override ConstraintSubject ToConstraintSubject(Asn1BitString value) => ConstraintSubject.FromBits(value);
}

/// <summary>OCTET STRING mapped to <see cref="byte"/>[].</summary>
public sealed class OctetStringType : Asn1Type<byte[]>
{
    /// <summary>The plain OCTET STRING type.</summary>
    public static readonly OctetStringType Instance = new();

    /// <summary>Creates an OCTET STRING descriptor.</summary>
    public OctetStringType(string name = "OCTET STRING", ImmutableArray<Asn1TagStep> tags = default, ImmutableArray<Asn1Constraint> constraints = default)
        : base(name, Asn1TypeKind.OctetString, Tagging.OrUniversal(tags, UniversalTagNumber.OctetString), constraints)
    {
    }

    /// <inheritdoc />
    public override void Encode(Asn1Writer writer, byte[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        ValidateForEncoding(writer, value);
        writer.WriteOctetString(this, value);
    }

    /// <inheritdoc />
    public override byte[] Decode(Asn1Reader reader) => ValidateDecoded(reader, reader.ReadOctetString(this));

    /// <inheritdoc />
    public override Asn1Type<byte[]> WithTags(ImmutableArray<Asn1TagStep> tags) => new OctetStringType(Name, tags, Constraints);

    /// <inheritdoc />
    public override Asn1Type<byte[]> WithConstraints(ImmutableArray<Asn1Constraint> additional, string? name = null) => new OctetStringType(name ?? Name, Tags, Tagging.Append(Constraints, additional));

    /// <inheritdoc />
    public override ConstraintSubject ToConstraintSubject(byte[] value) => ConstraintSubject.FromOctets(value);
}

/// <summary>OBJECT IDENTIFIER.</summary>
public sealed class ObjectIdentifierType : Asn1Type<ObjectIdentifier>
{
    /// <summary>The plain OBJECT IDENTIFIER type.</summary>
    public static readonly ObjectIdentifierType Instance = new();

    /// <summary>Creates an OBJECT IDENTIFIER descriptor.</summary>
    public ObjectIdentifierType(string name = "OBJECT IDENTIFIER", ImmutableArray<Asn1TagStep> tags = default, ImmutableArray<Asn1Constraint> constraints = default)
        : base(name, Asn1TypeKind.ObjectIdentifier, Tagging.OrUniversal(tags, UniversalTagNumber.ObjectIdentifier), constraints)
    {
    }

    /// <inheritdoc />
    public override void Encode(Asn1Writer writer, ObjectIdentifier value)
    {
        ArgumentNullException.ThrowIfNull(value);
        ValidateForEncoding(writer, value);
        writer.WriteObjectIdentifier(this, value);
    }

    /// <inheritdoc />
    public override ObjectIdentifier Decode(Asn1Reader reader) => ValidateDecoded(reader, reader.ReadObjectIdentifier(this));

    /// <inheritdoc />
    public override Asn1Type<ObjectIdentifier> WithTags(ImmutableArray<Asn1TagStep> tags) => new ObjectIdentifierType(Name, tags, Constraints);

    /// <inheritdoc />
    public override Asn1Type<ObjectIdentifier> WithConstraints(ImmutableArray<Asn1Constraint> additional, string? name = null) => new ObjectIdentifierType(name ?? Name, Tags, Tagging.Append(Constraints, additional));

    /// <inheritdoc />
    public override ConstraintSubject ToConstraintSubject(ObjectIdentifier value) => ConstraintSubject.FromObjectIdentifier(value);
}

/// <summary>RELATIVE-OID.</summary>
public sealed class RelativeOidType : Asn1Type<RelativeObjectIdentifier>
{
    /// <summary>The plain RELATIVE-OID type.</summary>
    public static readonly RelativeOidType Instance = new();

    /// <summary>Creates a RELATIVE-OID descriptor.</summary>
    public RelativeOidType(string name = "RELATIVE-OID", ImmutableArray<Asn1TagStep> tags = default, ImmutableArray<Asn1Constraint> constraints = default)
        : base(name, Asn1TypeKind.RelativeOid, Tagging.OrUniversal(tags, UniversalTagNumber.RelativeOid), constraints)
    {
    }

    /// <inheritdoc />
    public override void Encode(Asn1Writer writer, RelativeObjectIdentifier value)
    {
        ArgumentNullException.ThrowIfNull(value);
        ValidateForEncoding(writer, value);
        writer.WriteRelativeObjectIdentifier(this, value);
    }

    /// <inheritdoc />
    public override RelativeObjectIdentifier Decode(Asn1Reader reader) => ValidateDecoded(reader, reader.ReadRelativeObjectIdentifier(this));

    /// <inheritdoc />
    public override Asn1Type<RelativeObjectIdentifier> WithTags(ImmutableArray<Asn1TagStep> tags) => new RelativeOidType(Name, tags, Constraints);

    /// <inheritdoc />
    public override Asn1Type<RelativeObjectIdentifier> WithConstraints(ImmutableArray<Asn1Constraint> additional, string? name = null) => new RelativeOidType(name ?? Name, Tags, Tagging.Append(Constraints, additional));

    /// <inheritdoc />
    public override ConstraintSubject ToConstraintSubject(RelativeObjectIdentifier value) => ConstraintSubject.FromObjectIdentifier(value);
}

/// <summary>A restricted character string mapped to <see cref="string"/>.</summary>
public sealed class CharacterStringType : Asn1Type<string>
{
    private readonly IntegerConstraintCheck? _sizeOnly;

    /// <summary>Creates a character string descriptor.</summary>
    public CharacterStringType(CharacterStringKind stringKind, string? name = null, ImmutableArray<Asn1TagStep> tags = default, ImmutableArray<Asn1Constraint> constraints = default)
        : base(name ?? CharacterRepertoire.Keyword(stringKind), Asn1TypeKind.CharacterString, Tagging.OrUniversal(tags, CharacterRepertoire.UniversalTag(stringKind)), constraints)
    {
        StringKind = stringKind;
        if (Constraints.All(c => c.Root is SizeElement && (c.Additions is null or SizeElement)))
        {
            _sizeOnly = IntegerConstraintCheck.TryCreate([.. Constraints.Select(c => new Asn1Constraint(((SizeElement)c.Root!).Constraint.Root, c.IsExtensible, (c.Additions as SizeElement)?.Constraint.Root))]);
        }
    }

    /// <summary>The string kind.</summary>
    public CharacterStringKind StringKind { get; }

    /// <inheritdoc />
    public override void Encode(Asn1Writer writer, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (CharacterRepertoire.Validate(StringKind, value) is { } error)
        {
            throw writer.Error($"Invalid {CharacterRepertoire.Keyword(StringKind)} value: {error}");
        }

        ValidateForEncoding(writer, value);
        writer.WriteCharacterString(this, value);
    }

    /// <inheritdoc />
    public override string Decode(Asn1Reader reader)
    {
        var value = reader.ReadCharacterString(this);
        if (CharacterRepertoire.Validate(StringKind, value) is { } error)
        {
            throw reader.Error(Asn1DecodeError.Malformed, $"Invalid {CharacterRepertoire.Keyword(StringKind)} value: {error}");
        }

        return ValidateDecoded(reader, value);
    }

    /// <inheritdoc />
    public override ConstraintCheck CheckConstraints(string value) =>
        _sizeOnly is not null ? _sizeOnly.Check(CharacterRepertoire.CountCharacters(value)) : base.CheckConstraints(value);

    /// <inheritdoc />
    public override Asn1Type<string> WithTags(ImmutableArray<Asn1TagStep> tags) => new CharacterStringType(StringKind, Name, tags, Constraints);

    /// <inheritdoc />
    public override Asn1Type<string> WithConstraints(ImmutableArray<Asn1Constraint> additional, string? name = null) => new CharacterStringType(StringKind, name ?? Name, Tags, Tagging.Append(Constraints, additional));

    /// <inheritdoc />
    public override ConstraintSubject ToConstraintSubject(string value) => ConstraintSubject.FromString(value);
}

/// <summary>The useful and X.680 time types.</summary>
public enum TimeKind
{
    /// <summary>UTCTime</summary>
    UtcTime,

    /// <summary>GeneralizedTime</summary>
    GeneralizedTime,

    /// <summary>DATE</summary>
    Date,

    /// <summary>TIME-OF-DAY</summary>
    TimeOfDay,

    /// <summary>DATE-TIME</summary>
    DateTime,

    /// <summary>DURATION</summary>
    Duration,

    /// <summary>TIME</summary>
    Time,
}

/// <summary>A time type mapped to its canonical string notation.</summary>
public sealed class TimeType : Asn1Type<string>
{
    /// <summary>Creates a time descriptor.</summary>
    public TimeType(TimeKind timeKind, string? name = null, ImmutableArray<Asn1TagStep> tags = default, ImmutableArray<Asn1Constraint> constraints = default)
        : base(name ?? Keyword(timeKind), Asn1TypeKind.Time, Tagging.OrUniversal(tags, UniversalTag(timeKind)), constraints)
    {
        TimeKind = timeKind;
    }

    /// <summary>The time kind.</summary>
    public TimeKind TimeKind { get; }

    /// <summary>Universal tag number of a time kind.</summary>
    public static int UniversalTag(TimeKind kind) => kind switch
    {
        TimeKind.UtcTime => UniversalTagNumber.UtcTime,
        TimeKind.GeneralizedTime => UniversalTagNumber.GeneralizedTime,
        TimeKind.Date => UniversalTagNumber.Date,
        TimeKind.TimeOfDay => UniversalTagNumber.TimeOfDay,
        TimeKind.DateTime => UniversalTagNumber.DateTime,
        TimeKind.Duration => UniversalTagNumber.Duration,
        _ => UniversalTagNumber.Time,
    };

    /// <summary>ASN.1 keyword of a time kind.</summary>
    public static string Keyword(TimeKind kind) => kind switch
    {
        TimeKind.UtcTime => "UTCTime",
        TimeKind.GeneralizedTime => "GeneralizedTime",
        TimeKind.Date => "DATE",
        TimeKind.TimeOfDay => "TIME-OF-DAY",
        TimeKind.DateTime => "DATE-TIME",
        TimeKind.Duration => "DURATION",
        _ => "TIME",
    };

    /// <inheritdoc />
    public override void Encode(Asn1Writer writer, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (TimeSyntax.Validate(TimeKind, value) is { } error)
        {
            throw writer.Error($"Invalid {Keyword(TimeKind)} value '{value}': {error}");
        }

        ValidateForEncoding(writer, value);
        writer.WriteTime(this, value);
    }

    /// <inheritdoc />
    public override string Decode(Asn1Reader reader)
    {
        var value = reader.ReadTime(this);
        if (TimeSyntax.Validate(TimeKind, value) is { } error)
        {
            throw reader.Error(Asn1DecodeError.Malformed, $"Invalid {Keyword(TimeKind)} value '{value}': {error}");
        }

        return ValidateDecoded(reader, value);
    }

    /// <inheritdoc />
    public override Asn1Type<string> WithTags(ImmutableArray<Asn1TagStep> tags) => new TimeType(TimeKind, Name, tags, Constraints);

    /// <inheritdoc />
    public override Asn1Type<string> WithConstraints(ImmutableArray<Asn1Constraint> additional, string? name = null) => new TimeType(TimeKind, name ?? Name, Tags, Tagging.Append(Constraints, additional));

    /// <inheritdoc />
    public override ConstraintSubject ToConstraintSubject(string value) => ConstraintSubject.FromString(value);
}
