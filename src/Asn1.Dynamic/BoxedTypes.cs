using System.Collections.Immutable;
using Asn1.Constraints;
using Asn1.Runtime;

namespace Asn1.Dynamic;

/// <summary>Non-generic access to the wrapped descriptor of a boxing adapter.</summary>
internal interface IBoxedType
{
    Asn1Type InnerType { get; }
}

/// <summary>Adapts a typed descriptor to <c>Asn1Type&lt;object?&gt;</c> for the dynamic value model.</summary>
internal class BoxedType<T> : Asn1Type<object?>, IBoxedType, IAsn1ValidationAdapter
{
    public BoxedType(Asn1Type<T> inner)
        : base(inner.Name, inner.Kind, inner.Tags, inner.Constraints)
    {
        Inner = inner;
    }

    public Asn1Type<T> Inner { get; }

    public Asn1Type InnerType => Inner;

    public override Type ClrType => typeof(T);

    public override void Encode(Asn1Writer writer, object? value)
    {
        if (value is not T typed)
        {
            throw writer.Error($"expected a {DynamicValueNames.Describe(typeof(T))} for {Name} but got {DynamicValueNames.Describe(value?.GetType())}");
        }

        Inner.Encode(writer, typed);
    }

    public override object? Decode(Asn1Reader reader) => Inner.Decode(reader);

    public override Asn1Type<object?> WithTags(ImmutableArray<Asn1TagStep> tags) => DynamicTypeFactory.Box(Inner.WithTags(tags));

    public override Asn1Type<object?> WithConstraints(ImmutableArray<Asn1Constraint> additional, string? name = null) => DynamicTypeFactory.Box(Inner.WithConstraints(additional, name));

    public override ConstraintSubject ToConstraintSubject(object? value) => value is T t ? Inner.ToConstraintSubject(t) : ConstraintSubject.Opaque;

    public override ConstraintCheck CheckConstraints(object? value) => value is T t ? Inner.CheckConstraints(t) : ConstraintCheck.Violation;

    /// <summary>Validates with the typed descriptor so that components, alternatives and elements are checked too.</summary>
    public void ValidateAdapted(object? value, Asn1Validator validator)
    {
        if (value is T typed)
        {
            validator.Validate(Inner, typed);
        }
        else
        {
            validator.Report(null, $"expected a {DynamicValueNames.Describe(typeof(T))} for {Name} but got {DynamicValueNames.Describe(value?.GetType())}");
        }
    }
}

/// <summary>Boxed adapter that also exposes structural information (SEQUENCE/SET/CHOICE).</summary>
internal sealed class BoxedStructuredType<T>(Asn1Type<T> inner) : BoxedType<T>(inner), IStructuredTypeInfo
{
    public IReadOnlyList<Asn1ComponentInfo> ComponentInfos => ((IStructuredTypeInfo)Inner).ComponentInfos;

    public bool IsExtensible => ((IStructuredTypeInfo)Inner).IsExtensible;
}

/// <summary>Boxed adapter that also exposes collection information.</summary>
internal sealed class BoxedCollectionType<T>(Asn1Type<T> inner) : BoxedType<T>(inner), ICollectionTypeInfo
{
    public Asn1Type ElementTypeInfo => ((ICollectionTypeInfo)Inner).ElementTypeInfo;

    public string? ElementName => ((ICollectionTypeInfo)Inner).ElementName;
}

/// <summary>Human-readable names of dynamic value CLR types.</summary>
internal static class DynamicValueNames
{
    public static string Describe(Type? t) => t switch
    {
        null => "null",
        _ when t == typeof(System.Numerics.BigInteger) => "integer",
        _ when t == typeof(bool) => "boolean",
        _ when t == typeof(string) => "string",
        _ when t == typeof(byte[]) => "octet string",
        _ when t == typeof(Asn1Record) => "record",
        _ when t == typeof(Asn1Choice) => "choice",
        _ when t == typeof(List<object?>) => "list",
        _ => t.Name,
    };
}
