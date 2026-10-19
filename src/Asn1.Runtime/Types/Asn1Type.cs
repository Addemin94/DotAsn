using System.Collections.Immutable;
using Asn1.Constraints;

namespace Asn1.Runtime;

/// <summary>Kinds of runtime type descriptors.</summary>
public enum Asn1TypeKind
{
    /// <summary>BOOLEAN</summary>
    Boolean,

    /// <summary>INTEGER</summary>
    Integer,

    /// <summary>ENUMERATED</summary>
    Enumerated,

    /// <summary>REAL</summary>
    Real,

    /// <summary>BIT STRING</summary>
    BitString,

    /// <summary>OCTET STRING</summary>
    OctetString,

    /// <summary>NULL</summary>
    Null,

    /// <summary>OBJECT IDENTIFIER</summary>
    ObjectIdentifier,

    /// <summary>RELATIVE-OID</summary>
    RelativeOid,

    /// <summary>Restricted character string.</summary>
    CharacterString,

    /// <summary>UTCTime, GeneralizedTime and the X.680 time types.</summary>
    Time,

    /// <summary>SEQUENCE</summary>
    Sequence,

    /// <summary>SET</summary>
    Set,

    /// <summary>CHOICE</summary>
    Choice,

    /// <summary>SEQUENCE OF</summary>
    SequenceOf,

    /// <summary>SET OF</summary>
    SetOf,

    /// <summary>Open type.</summary>
    OpenType,
}

/// <summary>One step of an effective tag list.</summary>
/// <param name="Tag">The tag.</param>
/// <param name="IsExplicit">True if the tag wraps the remaining encoding (EXPLICIT); the innermost tag is not explicit.</param>
public readonly record struct Asn1TagStep(Asn1Tag Tag, bool IsExplicit)
{
    /// <inheritdoc />
    public override string ToString() => IsExplicit ? Tag + " EXPLICIT" : Tag.ToString();
}

/// <summary>A per-encoding-rule cache slot for data derived from a descriptor (e.g. PER effective bounds).</summary>
/// <typeparam name="TData">The cached data type.</typeparam>
public sealed class Asn1RuleDataSlot<TData>
    where TData : class
{
    /// <summary>Creates a slot.</summary>
    public Asn1RuleDataSlot() => Index = RuleDataSlots.Next();

    internal int Index { get; }
}

/// <summary>Allocates slot indices shared by all <see cref="Asn1RuleDataSlot{TData}"/> instantiations.</summary>
internal static class RuleDataSlots
{
    // A static field of the generic slot class would give every TData its own counter and make slots collide.
    private static int s_next;

    public static int Next() => Interlocked.Increment(ref s_next) - 1;
}

/// <summary>
/// Non-generic base of runtime type descriptors. A descriptor carries everything an encoding rule needs:
/// kind, effective tags, constraints and structure. Descriptors are immutable and thread-safe.
/// </summary>
public abstract class Asn1Type
{
    private object?[] _ruleData = new object?[8];

    /// <summary>Creates a descriptor.</summary>
    protected Asn1Type(string name, Asn1TypeKind kind, ImmutableArray<Asn1TagStep> tags, ImmutableArray<Asn1Constraint> constraints)
    {
        Name = name;
        Kind = kind;
        Tags = tags.IsDefault ? [] : tags;
        Constraints = constraints.IsDefault ? [] : constraints;
    }

    /// <summary>The ASN.1 name used in diagnostics (e.g. <c>Module.Type</c> or a builtin keyword).</summary>
    public string Name { get; }

    /// <summary>The kind.</summary>
    public Asn1TypeKind Kind { get; }

    /// <summary>Effective tags, outermost first. Empty for untagged CHOICE and open types.</summary>
    public ImmutableArray<Asn1TagStep> Tags { get; }

    /// <summary>Serially applied constraints.</summary>
    public ImmutableArray<Asn1Constraint> Constraints { get; }

    /// <summary>The outermost tag, or null for untagged CHOICE and open types.</summary>
    public Asn1Tag? OutermostTag => Tags.IsEmpty ? null : Tags[0].Tag;

    /// <summary>True when the final applied constraint is extensible.</summary>
    public bool HasExtensibleConstraint => !Constraints.IsEmpty && Constraints[^1].IsExtensible;

    /// <summary>The CLR type of values.</summary>
    public abstract Type ClrType { get; }

    /// <summary>Encodes a boxed value (used for open types and the dynamic value model).</summary>
    public abstract void EncodeBoxed(Asn1Writer writer, object? value);

    /// <summary>Decodes a boxed value (used for open types and the dynamic value model).</summary>
    public abstract object? DecodeBoxed(Asn1Reader reader);

    /// <summary>Encodes a boxed value with a rule (for callers that do not know the CLR type statically).</summary>
    public abstract byte[] EncodeBoxed(Asn1EncodingRule rule, object? value, Asn1EncodingOptions? options = null);

    /// <summary>Decodes a complete encoding into a boxed value.</summary>
    public abstract object? DecodeBoxed(Asn1EncodingRule rule, ReadOnlyMemory<byte> data, Asn1DecodingOptions? options = null);

    /// <summary>Validates a boxed value.</summary>
    public abstract IReadOnlyList<Asn1ValidationError> ValidateBoxed(object? value);

    /// <summary>Gets or computes rule-specific data derived from this descriptor.</summary>
    public TData GetRuleData<TData>(Asn1RuleDataSlot<TData> slot, Func<Asn1Type, TData> factory)
        where TData : class
    {
        var data = _ruleData;
        if (slot.Index < data.Length && Volatile.Read(ref data[slot.Index]) is TData cached)
        {
            return cached;
        }

        var value = factory(this);
        lock (this)
        {
            if (slot.Index >= _ruleData.Length)
            {
                Array.Resize(ref _ruleData, Math.Max(slot.Index + 1, _ruleData.Length * 2));
            }

            _ruleData[slot.Index] ??= value;
            return (TData)_ruleData[slot.Index]!;
        }
    }

    /// <summary>Renders the tags and name.</summary>
    public override string ToString() => (Tags.IsEmpty ? string.Empty : string.Join(" ", Tags) + " ") + Name;
}

/// <summary>A typed descriptor for values of CLR type <typeparamref name="T"/>.</summary>
/// <typeparam name="T">The CLR value type.</typeparam>
public abstract class Asn1Type<T> : Asn1Type
{
    /// <summary>Creates a descriptor.</summary>
    protected Asn1Type(string name, Asn1TypeKind kind, ImmutableArray<Asn1TagStep> tags, ImmutableArray<Asn1Constraint> constraints)
        : base(name, kind, tags, constraints)
    {
    }

    /// <inheritdoc />
    public override Type ClrType => typeof(T);

    /// <summary>Encodes a value by dispatching to the writer method for this kind. Validates constraints when enabled.</summary>
    public abstract void Encode(Asn1Writer writer, T value);

    /// <summary>Decodes a value by dispatching to the reader method for this kind.</summary>
    public abstract T Decode(Asn1Reader reader);

    /// <summary>Returns a descriptor with other effective tags (used for tagged references).</summary>
    public abstract Asn1Type<T> WithTags(ImmutableArray<Asn1TagStep> tags);

    /// <summary>Returns a descriptor with additional serially applied constraints.</summary>
    public abstract Asn1Type<T> WithConstraints(ImmutableArray<Asn1Constraint> additional, string? name = null);

    /// <summary>Creates a constraint subject for a value, used by the generic validator.</summary>
    public abstract ConstraintSubject ToConstraintSubject(T value);

    /// <summary>Checks this type's own constraints (not those of nested components).</summary>
    public virtual ConstraintCheck CheckConstraints(T value) =>
        Constraints.IsEmpty ? ConstraintCheck.InRoot : ConstraintEvaluator.CheckSerial(Constraints, ToConstraintSubject(value));

    /// <inheritdoc />
    public override void EncodeBoxed(Asn1Writer writer, object? value) => Encode(writer, (T)value!);

    /// <inheritdoc />
    public override object? DecodeBoxed(Asn1Reader reader) => Decode(reader);

    /// <inheritdoc />
    public override byte[] EncodeBoxed(Asn1EncodingRule rule, object? value, Asn1EncodingOptions? options = null) => rule.Encode(this, (T)value!, options);

    /// <inheritdoc />
    public override object? DecodeBoxed(Asn1EncodingRule rule, ReadOnlyMemory<byte> data, Asn1DecodingOptions? options = null) => rule.Decode(this, data, options);

    /// <inheritdoc />
    public override IReadOnlyList<Asn1ValidationError> ValidateBoxed(object? value) => Asn1Validator.ValidateValue(this, (T)value!);

    /// <summary>Validates a value before it is written; throws on violation.</summary>
    protected void ValidateForEncoding(Asn1Writer writer, T value)
    {
        if (!writer.Options.ValidateConstraints || Constraints.IsEmpty)
        {
            return;
        }

        var check = CheckConstraints(value);
        if (check == ConstraintCheck.Violation || (check == ConstraintCheck.OutsideExtensible && !writer.Options.AllowValuesOutsideExtensibleConstraints))
        {
            throw new Asn1ConstraintViolationException(Name, string.Concat(Constraints.Select(c => c.ToString())), writer.Path.ToString());
        }
    }

    /// <summary>Validates a decoded value; throws on violation (values of later versions in extensible constraints are accepted).</summary>
    protected T ValidateDecoded(Asn1Reader reader, T value)
    {
        if (!reader.Options.ValidateConstraints || Constraints.IsEmpty)
        {
            return value;
        }

        if (CheckConstraints(value) == ConstraintCheck.Violation)
        {
            throw new Asn1DecodeException(Asn1DecodeError.ConstraintViolation, $"Value violates constraint {string.Concat(Constraints.Select(c => c.ToString()))} of {Name}", reader.BitPosition, reader.Path.ToString());
        }

        return value;
    }
}

/// <summary>Implemented by generated types to expose their descriptor.</summary>
/// <typeparam name="TSelf">The implementing type.</typeparam>
public interface IAsn1Value<TSelf>
    where TSelf : IAsn1Value<TSelf>
{
    /// <summary>The descriptor of the type.</summary>
    static abstract Asn1Type<TSelf> Asn1Type { get; }
}
