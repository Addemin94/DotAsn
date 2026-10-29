using System.Collections.Immutable;
using Asn1.Constraints;

namespace Asn1.Runtime;

/// <summary>Non-generic description of a component or alternative, for generic tools (TLV inspectors, PER analysis).</summary>
/// <param name="Name">Identifier.</param>
/// <param name="Type">Descriptor.</param>
/// <param name="Presence">Presence.</param>
/// <param name="ExtensionAdditionIndex">Extension addition index or -1.</param>
/// <param name="IsInRootPart2">True after the second extension marker.</param>
/// <param name="IsInExtensionGroup">True inside [[ ]].</param>
public sealed record Asn1ComponentInfo(string Name, Asn1Type Type, Asn1Presence Presence, int ExtensionAdditionIndex, bool IsInRootPart2, bool IsInExtensionGroup)
{
    /// <summary>True if the component is an extension addition.</summary>
    public bool IsExtensionAddition => ExtensionAdditionIndex >= 0;

    /// <summary>True for OPTIONAL and DEFAULT components.</summary>
    public bool IsOptional => Presence != Asn1Presence.Required;
}

/// <summary>Implemented by SEQUENCE, SET and CHOICE descriptors.</summary>
public interface IStructuredTypeInfo
{
    /// <summary>Components or alternatives in textual order.</summary>
    IReadOnlyList<Asn1ComponentInfo> ComponentInfos { get; }

    /// <summary>True if the type has an extension marker.</summary>
    bool IsExtensible { get; }
}

/// <summary>Implemented by SEQUENCE OF and SET OF descriptors.</summary>
public interface ICollectionTypeInfo
{
    /// <summary>The element descriptor.</summary>
    Asn1Type ElementTypeInfo { get; }

    /// <summary>The element identifier, if named.</summary>
    string? ElementName { get; }
}

/// <summary>Presence of a component.</summary>
public enum Asn1Presence
{
    /// <summary>Mandatory.</summary>
    Required,

    /// <summary>OPTIONAL</summary>
    Optional,

    /// <summary>DEFAULT</summary>
    Default,
}

/// <summary>A component of a SEQUENCE/SET described independently of the CLR field type.</summary>
/// <typeparam name="TOwner">The owning CLR type.</typeparam>
public abstract class SequenceComponent<TOwner>
{
    /// <summary>Creates a component.</summary>
    protected SequenceComponent(string name, Asn1Presence presence, int extensionAdditionIndex, bool isInRootPart2, bool isInExtensionGroup)
    {
        Name = name;
        Presence = presence;
        ExtensionAdditionIndex = extensionAdditionIndex;
        IsInRootPart2 = isInRootPart2;
        IsInExtensionGroup = isInExtensionGroup;
    }

    /// <summary>The identifier.</summary>
    public string Name { get; }

    /// <summary>Presence.</summary>
    public Asn1Presence Presence { get; }

    /// <summary>True for OPTIONAL and DEFAULT components.</summary>
    public bool IsOptional => Presence != Asn1Presence.Required;

    /// <summary>Index of the extension addition (shared by members of one [[ ]] group), or -1 for root components.</summary>
    public int ExtensionAdditionIndex { get; }

    /// <summary>True if the component is an extension addition.</summary>
    public bool IsExtensionAddition => ExtensionAdditionIndex >= 0;

    /// <summary>True if the component is a member of an extension addition group.</summary>
    public bool IsInExtensionGroup { get; }

    /// <summary>True if the component follows the second extension marker.</summary>
    public bool IsInRootPart2 { get; }

    /// <summary>The component's descriptor.</summary>
    public abstract Asn1Type Type { get; }

    /// <summary>True if the component is present in <paramref name="owner"/> (always true for mandatory and DEFAULT components).</summary>
    public abstract bool IsPresent(TOwner owner);

    /// <summary>True if a DEFAULT component holds its default value.</summary>
    public abstract bool HasDefaultValue(TOwner owner);

    /// <summary>Encodes the component value of <paramref name="owner"/>.</summary>
    public abstract void Encode(Asn1Writer writer, TOwner owner);

    /// <summary>Decodes the component and stores it in <paramref name="owner"/>.</summary>
    public abstract void Decode(Asn1Reader reader, TOwner owner);

    /// <summary>Stores the DEFAULT value into <paramref name="owner"/> (no-op for components without a default).</summary>
    public abstract void ApplyDefault(TOwner owner);

    /// <summary>Gets the component value boxed (for validation and diagnostics); null when absent.</summary>
    public abstract object? GetBoxed(TOwner owner);

    /// <summary>Creates a constraint subject for the component value, or null when absent.</summary>
    public abstract ConstraintSubject? GetConstraintSubject(TOwner owner);

    /// <summary>Validates the component value (recursively), reporting violations to <paramref name="validator"/>.</summary>
    public abstract void Validate(TOwner owner, Asn1Validator validator);
}

/// <summary>A SEQUENCE/SET component with typed access.</summary>
/// <typeparam name="TOwner">The owning CLR type.</typeparam>
/// <typeparam name="TField">The component's CLR type.</typeparam>
public sealed class SequenceComponent<TOwner, TField> : SequenceComponent<TOwner>
{
    private readonly Func<Asn1Type<TField>> _type;
    private readonly Func<TOwner, TField> _get;
    private readonly Action<TOwner, TField> _set;
    private readonly Func<TOwner, bool>? _isPresent;
    private readonly Action<TOwner>? _applyDefault;
    private readonly Func<TOwner, bool>? _isDefault;
    private Asn1Type<TField>? _resolved;

    /// <summary>Creates a component accessor.</summary>
    /// <param name="name">Component identifier.</param>
    /// <param name="type">Lazily supplied descriptor (supports recursive types).</param>
    /// <param name="get">Gets the value (only called when present).</param>
    /// <param name="set">Stores a decoded value.</param>
    /// <param name="presence">Presence.</param>
    /// <param name="isPresent">For OPTIONAL components: presence test.</param>
    /// <param name="isDefault">For DEFAULT components: tests whether the value equals the default.</param>
    /// <param name="applyDefault">For DEFAULT components: stores the default value.</param>
    /// <param name="extensionAdditionIndex">Extension addition index or -1.</param>
    /// <param name="isInRootPart2">True after the second extension marker.</param>
    /// <param name="isInExtensionGroup">True inside [[ ]].</param>
    public SequenceComponent(
        string name,
        Func<Asn1Type<TField>> type,
        Func<TOwner, TField> get,
        Action<TOwner, TField> set,
        Asn1Presence presence = Asn1Presence.Required,
        Func<TOwner, bool>? isPresent = null,
        Func<TOwner, bool>? isDefault = null,
        Action<TOwner>? applyDefault = null,
        int extensionAdditionIndex = -1,
        bool isInRootPart2 = false,
        bool isInExtensionGroup = false)
        : base(name, presence, extensionAdditionIndex, isInRootPart2, isInExtensionGroup)
    {
        _type = type;
        _get = get;
        _set = set;
        _isPresent = isPresent;
        _isDefault = isDefault;
        _applyDefault = applyDefault;
    }

    /// <summary>The typed descriptor.</summary>
    public Asn1Type<TField> FieldType => _resolved ??= _type();

    /// <inheritdoc />
    public override Asn1Type Type => FieldType;

    /// <inheritdoc />
    public override bool IsPresent(TOwner owner) => _isPresent is null || _isPresent(owner);

    /// <inheritdoc />
    public override bool HasDefaultValue(TOwner owner) => _isDefault is not null && _isDefault(owner);

    /// <summary>Gets the typed value.</summary>
    public TField GetValue(TOwner owner) => _get(owner);

    /// <inheritdoc />
    public override void Encode(Asn1Writer writer, TOwner owner)
    {
        writer.Path.Push(Name);
        FieldType.Encode(writer, _get(owner));
        writer.Path.Pop();
    }

    /// <inheritdoc />
    public override void Decode(Asn1Reader reader, TOwner owner)
    {
        reader.Path.Push(Name);
        _set(owner, FieldType.Decode(reader));
        reader.Path.Pop();
    }

    /// <inheritdoc />
    public override void ApplyDefault(TOwner owner) => _applyDefault?.Invoke(owner);

    /// <inheritdoc />
    public override object? GetBoxed(TOwner owner) => IsPresent(owner) ? _get(owner) : null;

    /// <inheritdoc />
    public override ConstraintSubject? GetConstraintSubject(TOwner owner) => IsPresent(owner) ? FieldType.ToConstraintSubject(_get(owner)) : null;

    /// <inheritdoc />
    public override void Validate(TOwner owner, Asn1Validator validator)
    {
        if (!IsPresent(owner))
        {
            if (Presence == Asn1Presence.Required && !IsExtensionAddition)
            {
                validator.Report(Name, "mandatory component is absent");
            }

            return;
        }

        validator.Path.Push(Name);
        validator.Validate(FieldType, _get(owner));
        validator.Path.Pop();
    }
}

/// <summary>SEQUENCE or SET.</summary>
/// <typeparam name="T">The CLR type.</typeparam>
public sealed class SequenceType<T> : Asn1Type<T>, IStructuredTypeInfo, IFormatDispatch
{
    void IFormatDispatch.AppendFormatted(System.Text.StringBuilder sb, object? value, int depth) => Asn1ValueFormatter.AppendTyped(sb, this, value, depth);

    private readonly Lazy<ImmutableArray<SequenceComponent<T>>> _components;
    private readonly Func<T> _factory;

    /// <summary>Creates a SEQUENCE/SET descriptor.</summary>
    /// <param name="name">ASN.1 name.</param>
    /// <param name="isSet">True for SET.</param>
    /// <param name="factory">Creates an empty value (with DEFAULT values pre-populated).</param>
    /// <param name="components">Lazily supplied components in textual order (supports recursion).</param>
    /// <param name="isExtensible">True if the type has an extension marker.</param>
    /// <param name="tags">Effective tags (default UNIVERSAL 16/17).</param>
    /// <param name="constraints">Constraints.</param>
    public SequenceType(string name, bool isSet, Func<T> factory, Func<IEnumerable<SequenceComponent<T>>> components, bool isExtensible = false, ImmutableArray<Asn1TagStep> tags = default, ImmutableArray<Asn1Constraint> constraints = default)
        : this(name, isSet, factory, new Lazy<ImmutableArray<SequenceComponent<T>>>(() => [.. components()]), isExtensible, tags, constraints)
    {
    }

    private SequenceType(string name, bool isSet, Func<T> factory, Lazy<ImmutableArray<SequenceComponent<T>>> components, bool isExtensible, ImmutableArray<Asn1TagStep> tags, ImmutableArray<Asn1Constraint> constraints)
        : base(name, isSet ? Asn1TypeKind.Set : Asn1TypeKind.Sequence, Tagging.OrUniversal(tags, isSet ? UniversalTagNumber.Set : UniversalTagNumber.Sequence), constraints)
    {
        _factory = factory;
        _components = components;
        IsExtensible = isExtensible;
    }

    /// <summary>Components in textual order.</summary>
    public ImmutableArray<SequenceComponent<T>> Components => _components.Value;

    /// <inheritdoc />
    public IReadOnlyList<Asn1ComponentInfo> ComponentInfos => _infos ??= [.. Components.Select(c => new Asn1ComponentInfo(c.Name, c.Type, c.Presence, c.ExtensionAdditionIndex, c.IsInRootPart2, c.IsInExtensionGroup))];

    private ImmutableArray<Asn1ComponentInfo>? _infos;

    /// <summary>True if the type has an extension marker.</summary>
    public bool IsExtensible { get; }

    /// <summary>Creates an empty value.</summary>
    public T CreateInstance() => _factory();

    /// <inheritdoc />
    public override void Encode(Asn1Writer writer, T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        ValidateForEncoding(writer, value);
        writer.EnterNested();
        writer.WriteSequence(this, value);
        writer.ExitNested();
    }

    /// <inheritdoc />
    public override T Decode(Asn1Reader reader)
    {
        reader.EnterNested();
        var value = reader.ReadSequence(this);
        reader.ExitNested();
        return ValidateDecoded(reader, value);
    }

    /// <inheritdoc />
    public override Asn1Type<T> WithTags(ImmutableArray<Asn1TagStep> tags) => new SequenceType<T>(Name, Kind == Asn1TypeKind.Set, _factory, _components, IsExtensible, tags, Constraints);

    /// <inheritdoc />
    public override Asn1Type<T> WithConstraints(ImmutableArray<Asn1Constraint> additional, string? name = null) => new SequenceType<T>(name ?? Name, Kind == Asn1TypeKind.Set, _factory, _components, IsExtensible, Tags, Tagging.Append(Constraints, additional));

    /// <inheritdoc />
    public override ConstraintSubject ToConstraintSubject(T value) =>
        ConstraintSubject.FromStructure(name => Components.FirstOrDefault(c => c.Name == name)?.GetConstraintSubject(value));
}

/// <summary>An alternative of a CHOICE.</summary>
/// <typeparam name="T">The CHOICE CLR type.</typeparam>
public abstract class ChoiceAlternative<T>
{
    /// <summary>Creates an alternative.</summary>
    protected ChoiceAlternative(string name, int extensionAdditionIndex, bool isInExtensionGroup)
    {
        Name = name;
        ExtensionAdditionIndex = extensionAdditionIndex;
        IsInExtensionGroup = isInExtensionGroup;
    }

    /// <summary>The identifier.</summary>
    public string Name { get; }

    /// <summary>Extension addition index, or -1 for root alternatives.</summary>
    public int ExtensionAdditionIndex { get; }

    /// <summary>True if the alternative is an extension addition.</summary>
    public bool IsExtensionAddition => ExtensionAdditionIndex >= 0;

    /// <summary>True if the alternative is in an extension addition group.</summary>
    public bool IsInExtensionGroup { get; }

    /// <summary>The alternative's descriptor.</summary>
    public abstract Asn1Type Type { get; }

    /// <summary>Encodes the alternative's value held in <paramref name="choice"/>.</summary>
    public abstract void Encode(Asn1Writer writer, T choice);

    /// <summary>Decodes the alternative's value and wraps it.</summary>
    public abstract T Decode(Asn1Reader reader);

    /// <summary>Creates a constraint subject for the alternative's value.</summary>
    public abstract ConstraintSubject GetConstraintSubject(T choice);

    /// <summary>Gets the alternative's value boxed (for formatting).</summary>
    public abstract object? GetBoxed(T choice);

    /// <summary>Validates the alternative's value.</summary>
    public abstract void Validate(T choice, Asn1Validator validator);
}

/// <summary>A CHOICE alternative with typed access.</summary>
/// <typeparam name="T">The CHOICE CLR type.</typeparam>
/// <typeparam name="TValue">The alternative's CLR type.</typeparam>
public sealed class ChoiceAlternative<T, TValue> : ChoiceAlternative<T>
{
    private readonly Func<Asn1Type<TValue>> _type;
    private readonly Func<T, TValue> _get;
    private readonly Func<TValue, T> _create;
    private Asn1Type<TValue>? _resolved;

    /// <summary>Creates an alternative.</summary>
    /// <param name="name">Identifier.</param>
    /// <param name="type">Lazily supplied descriptor.</param>
    /// <param name="get">Extracts the alternative's value from a CHOICE value of this alternative.</param>
    /// <param name="create">Wraps a value in the CHOICE type.</param>
    /// <param name="extensionAdditionIndex">Extension addition index or -1.</param>
    /// <param name="isInExtensionGroup">True inside [[ ]].</param>
    public ChoiceAlternative(string name, Func<Asn1Type<TValue>> type, Func<T, TValue> get, Func<TValue, T> create, int extensionAdditionIndex = -1, bool isInExtensionGroup = false)
        : base(name, extensionAdditionIndex, isInExtensionGroup)
    {
        _type = type;
        _get = get;
        _create = create;
    }

    /// <summary>The typed descriptor.</summary>
    public Asn1Type<TValue> ValueType => _resolved ??= _type();

    /// <inheritdoc />
    public override Asn1Type Type => ValueType;

    /// <inheritdoc />
    public override void Encode(Asn1Writer writer, T choice)
    {
        writer.Path.Push(Name);
        ValueType.Encode(writer, _get(choice));
        writer.Path.Pop();
    }

    /// <inheritdoc />
    public override T Decode(Asn1Reader reader)
    {
        reader.Path.Push(Name);
        var value = _create(ValueType.Decode(reader));
        reader.Path.Pop();
        return value;
    }

    /// <inheritdoc />
    public override ConstraintSubject GetConstraintSubject(T choice) => ValueType.ToConstraintSubject(_get(choice));

    /// <inheritdoc />
    public override object? GetBoxed(T choice) => _get(choice);

    /// <inheritdoc />
    public override void Validate(T choice, Asn1Validator validator)
    {
        validator.Path.Push(Name);
        validator.Validate(ValueType, _get(choice));
        validator.Path.Pop();
    }
}

/// <summary>CHOICE.</summary>
/// <typeparam name="T">The CLR type (normally an abstract generated class with one subclass per alternative).</typeparam>
public sealed class ChoiceType<T> : Asn1Type<T>, IStructuredTypeInfo, IFormatDispatch
{
    void IFormatDispatch.AppendFormatted(System.Text.StringBuilder sb, object? value, int depth) => Asn1ValueFormatter.AppendTyped(sb, this, value, depth);

    private readonly Lazy<ImmutableArray<ChoiceAlternative<T>>> _alternatives;
    private readonly Func<T, int> _selector;
    private readonly Func<int, Asn1OpenValue, T>? _unknown;
    private readonly Func<T, (int Index, Asn1OpenValue Value)?>? _unknownSelector;

    /// <summary>Creates a CHOICE descriptor.</summary>
    /// <param name="name">ASN.1 name.</param>
    /// <param name="alternatives">Lazily supplied alternatives in textual order.</param>
    /// <param name="selector">Returns the index (into <see cref="Alternatives"/>) of the chosen alternative, or -1 for an unknown extension.</param>
    /// <param name="isExtensible">True if the CHOICE has an extension marker.</param>
    /// <param name="unknownExtension">Wraps an unknown extension alternative received from a later version; when null such input is rejected.</param>
    /// <param name="unknownSelector">Extracts the raw unknown extension from a value (for re-encoding).</param>
    /// <param name="tags">Effective tags (normally empty).</param>
    /// <param name="constraints">Constraints.</param>
    public ChoiceType(string name, Func<IEnumerable<ChoiceAlternative<T>>> alternatives, Func<T, int> selector, bool isExtensible = false, Func<int, Asn1OpenValue, T>? unknownExtension = null, Func<T, (int Index, Asn1OpenValue Value)?>? unknownSelector = null, ImmutableArray<Asn1TagStep> tags = default, ImmutableArray<Asn1Constraint> constraints = default)
        : this(name, new Lazy<ImmutableArray<ChoiceAlternative<T>>>(() => [.. alternatives()]), selector, isExtensible, unknownExtension, unknownSelector, tags.IsDefault ? [] : tags, constraints)
    {
    }

    private ChoiceType(string name, Lazy<ImmutableArray<ChoiceAlternative<T>>> alternatives, Func<T, int> selector, bool isExtensible, Func<int, Asn1OpenValue, T>? unknownExtension, Func<T, (int Index, Asn1OpenValue Value)?>? unknownSelector, ImmutableArray<Asn1TagStep> tags, ImmutableArray<Asn1Constraint> constraints)
        : base(name, Asn1TypeKind.Choice, tags, constraints)
    {
        _alternatives = alternatives;
        _selector = selector;
        IsExtensible = isExtensible;
        _unknown = unknownExtension;
        _unknownSelector = unknownSelector;
    }

    /// <summary>Alternatives in textual order.</summary>
    public ImmutableArray<ChoiceAlternative<T>> Alternatives => _alternatives.Value;

    /// <inheritdoc />
    public IReadOnlyList<Asn1ComponentInfo> ComponentInfos => _infos ??= [.. Alternatives.Select(a => new Asn1ComponentInfo(a.Name, a.Type, Asn1Presence.Required, a.ExtensionAdditionIndex, false, a.IsInExtensionGroup))];

    private ImmutableArray<Asn1ComponentInfo>? _infos;

    /// <summary>True if the CHOICE is extensible.</summary>
    public bool IsExtensible { get; }

    /// <summary>True if unknown extension alternatives can be represented.</summary>
    public bool SupportsUnknownExtensions => _unknown is not null;

    /// <summary>Returns the alternative index of a value, or -1 for an unknown extension.</summary>
    public int GetIndex(T value) => _selector(value);

    /// <summary>For an unknown extension value: its extension index and raw encoding.</summary>
    public (int Index, Asn1OpenValue Value)? GetUnknown(T value) => _unknownSelector?.Invoke(value);

    /// <summary>Wraps an unknown extension alternative.</summary>
    public T CreateUnknown(int extensionIndex, Asn1OpenValue raw) =>
        _unknown is not null ? _unknown(extensionIndex, raw) : throw new Asn1UnsupportedException($"{Name} cannot represent unknown extension alternatives");

    /// <inheritdoc />
    public override void Encode(Asn1Writer writer, T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        ValidateForEncoding(writer, value);
        writer.EnterNested();
        writer.WriteChoice(this, value);
        writer.ExitNested();
    }

    /// <inheritdoc />
    public override T Decode(Asn1Reader reader)
    {
        reader.EnterNested();
        var value = reader.ReadChoice(this);
        reader.ExitNested();
        return ValidateDecoded(reader, value);
    }

    /// <inheritdoc />
    public override Asn1Type<T> WithTags(ImmutableArray<Asn1TagStep> tags) => new ChoiceType<T>(Name, _alternatives, _selector, IsExtensible, _unknown, _unknownSelector, tags, Constraints);

    /// <inheritdoc />
    public override Asn1Type<T> WithConstraints(ImmutableArray<Asn1Constraint> additional, string? name = null) => new ChoiceType<T>(name ?? Name, _alternatives, _selector, IsExtensible, _unknown, _unknownSelector, Tags, Tagging.Append(Constraints, additional));

    /// <inheritdoc />
    public override ConstraintSubject ToConstraintSubject(T value)
    {
        var index = _selector(value);
        return ConstraintSubject.FromStructure(name => index >= 0 && Alternatives[index].Name == name ? Alternatives[index].GetConstraintSubject(value) : null);
    }
}

/// <summary>SEQUENCE OF or SET OF.</summary>
/// <typeparam name="TElement">The element CLR type.</typeparam>
public sealed class CollectionType<TElement> : Asn1Type<List<TElement>>, ICollectionValidator, ICollectionTypeInfo
{
    private readonly Func<Asn1Type<TElement>> _element;
    private Asn1Type<TElement>? _resolved;
    private readonly IntegerConstraintCheck? _sizeOnly;

    /// <summary>Creates a collection descriptor.</summary>
    /// <param name="name">ASN.1 name.</param>
    /// <param name="isSet">True for SET OF.</param>
    /// <param name="element">Lazily supplied element descriptor.</param>
    /// <param name="elementName">Element identifier for <c>SEQUENCE OF name Type</c> (used by XER/JER).</param>
    /// <param name="tags">Effective tags (default UNIVERSAL 16/17).</param>
    /// <param name="constraints">Constraints.</param>
    public CollectionType(string name, bool isSet, Func<Asn1Type<TElement>> element, string? elementName = null, ImmutableArray<Asn1TagStep> tags = default, ImmutableArray<Asn1Constraint> constraints = default)
        : base(name, isSet ? Asn1TypeKind.SetOf : Asn1TypeKind.SequenceOf, Tagging.OrUniversal(tags, isSet ? UniversalTagNumber.Set : UniversalTagNumber.Sequence), constraints)
    {
        _element = element;
        ElementName = elementName;
        _sizeOnly = IntegerConstraintCheck.TryCreateSize(Constraints);
    }

    /// <summary>The element descriptor.</summary>
    public Asn1Type<TElement> ElementType => _resolved ??= _element();

    /// <summary>The element identifier, if named.</summary>
    public string? ElementName { get; }

    /// <inheritdoc />
    public Asn1Type ElementTypeInfo => ElementType;

    /// <summary>True for SET OF.</summary>
    public bool IsSet => Kind == Asn1TypeKind.SetOf;

    /// <summary>Encodes one element, maintaining the error path.</summary>
    public void EncodeElement(Asn1Writer writer, int index, TElement element)
    {
        writer.Path.PushIndex(index);
        ElementType.Encode(writer, element);
        writer.Path.Pop();
    }

    /// <summary>Decodes one element, maintaining the error path.</summary>
    public TElement DecodeElement(Asn1Reader reader, int index)
    {
        reader.Path.PushIndex(index);
        var e = ElementType.Decode(reader);
        reader.Path.Pop();
        return e;
    }

    /// <inheritdoc />
    public override void Encode(Asn1Writer writer, List<TElement> value)
    {
        ArgumentNullException.ThrowIfNull(value);
        ValidateForEncoding(writer, value);
        writer.EnterNested();
        writer.WriteCollection(this, value);
        writer.ExitNested();
    }

    /// <inheritdoc />
    public override List<TElement> Decode(Asn1Reader reader)
    {
        reader.EnterNested();
        var value = reader.ReadCollection(this);
        reader.ExitNested();
        return ValidateDecoded(reader, value);
    }

    /// <inheritdoc />
    public override ConstraintCheck CheckConstraints(List<TElement> value) =>
        _sizeOnly is not null ? _sizeOnly.Check(value.Count) : base.CheckConstraints(value);

    /// <inheritdoc />
    public override Asn1Type<List<TElement>> WithTags(ImmutableArray<Asn1TagStep> tags) => new CollectionType<TElement>(Name, IsSet, _element, ElementName, tags, Constraints);

    /// <inheritdoc />
    public override Asn1Type<List<TElement>> WithConstraints(ImmutableArray<Asn1Constraint> additional, string? name = null) => new CollectionType<TElement>(name ?? Name, IsSet, _element, ElementName, Tags, Tagging.Append(Constraints, additional));

    /// <inheritdoc />
    public override ConstraintSubject ToConstraintSubject(List<TElement> value) =>
        ConstraintSubject.FromCollection(value.Count, () => value.Select(e => ElementType.ToConstraintSubject(e)));

    void ICollectionValidator.ValidateElements(object list, Asn1Validator validator)
    {
        var items = (List<TElement>)list;
        for (var i = 0; i < items.Count; i++)
        {
            validator.Path.PushIndex(i);
            validator.Validate(ElementType, items[i]);
            validator.Path.Pop();
        }
    }
}

/// <summary>
/// A value of an open type: either a typed value (for encoding) or raw contents produced by a decoder.
/// Raw contents can be decoded later with <see cref="DecodeAs{T}"/> once the actual type is known.
/// </summary>
public sealed class Asn1OpenValue
{
    private Asn1OpenValue(Asn1Type? type, object? value, ReadOnlyMemory<byte> encoded, string? encodingRule, long bitLength)
    {
        Type = type;
        Value = value;
        Encoded = encoded;
        EncodingRule = encodingRule;
        BitLength = bitLength;
    }

    /// <summary>The actual type of a typed value.</summary>
    public Asn1Type? Type { get; }

    /// <summary>The typed value (boxed).</summary>
    public object? Value { get; }

    /// <summary>The raw encoding (for decoded values).</summary>
    public ReadOnlyMemory<byte> Encoded { get; }

    /// <summary>The encoding rule that produced <see cref="Encoded"/>.</summary>
    public string? EncodingRule { get; }

    /// <summary>Length in bits of <see cref="Encoded"/> (normally a multiple of 8).</summary>
    public long BitLength { get; }

    /// <summary>True if this value holds raw encoded contents.</summary>
    public bool IsRaw => Type is null;

    /// <summary>Wraps a typed value.</summary>
    public static Asn1OpenValue Create<T>(Asn1Type<T> type, T value) => new(type, value, default, null, 0);

    /// <summary>Wraps raw encoded contents.</summary>
    public static Asn1OpenValue FromEncoded(ReadOnlyMemory<byte> encoded, string encodingRule, long bitLength = -1) =>
        new(null, null, encoded, encodingRule, bitLength < 0 ? encoded.Length * 8L : bitLength);

    /// <summary>Decodes raw contents as <paramref name="type"/> using <paramref name="rule"/>.</summary>
    public T DecodeAs<T>(Asn1Type<T> type, Asn1EncodingRule rule, Asn1DecodingOptions? options = null)
    {
        if (!IsRaw)
        {
            return (T)Value!;
        }

        if (EncodingRule is not null && !string.Equals(EncodingRule, rule.Name, StringComparison.Ordinal))
        {
            throw new Asn1UnsupportedException($"The open type value was encoded with {EncodingRule}, not {rule.Name}");
        }

        return rule.Decode(type, Encoded, options);
    }

    /// <inheritdoc />
    public override string ToString() => IsRaw ? $"{EncodingRule}:{Convert.ToHexString(Encoded.Span)}" : $"{Type!.Name}:{Value}";
}

/// <summary>An open type (CLASS.&amp;Type, ANY).</summary>
public sealed class OpenType : Asn1Type<Asn1OpenValue>
{
    /// <summary>The plain open type.</summary>
    public static readonly OpenType Instance = new();

    /// <summary>Creates an open type descriptor.</summary>
    public OpenType(string name = "open type", ImmutableArray<Asn1TagStep> tags = default, ImmutableArray<Asn1Constraint> constraints = default)
        : base(name, Asn1TypeKind.OpenType, tags.IsDefault ? [] : tags, constraints)
    {
    }

    /// <inheritdoc />
    public override void Encode(Asn1Writer writer, Asn1OpenValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        writer.EnterNested();
        writer.WriteOpenType(this, value);
        writer.ExitNested();
    }

    /// <inheritdoc />
    public override Asn1OpenValue Decode(Asn1Reader reader)
    {
        reader.EnterNested();
        var v = reader.ReadOpenType(this);
        reader.ExitNested();
        return v;
    }

    /// <inheritdoc />
    public override Asn1Type<Asn1OpenValue> WithTags(ImmutableArray<Asn1TagStep> tags) => new OpenType(Name, tags, Constraints);

    /// <inheritdoc />
    public override Asn1Type<Asn1OpenValue> WithConstraints(ImmutableArray<Asn1Constraint> additional, string? name = null) => new OpenType(name ?? Name, Tags, Tagging.Append(Constraints, additional));

    /// <inheritdoc />
    public override ConstraintSubject ToConstraintSubject(Asn1OpenValue value) => ConstraintSubject.Opaque;

    /// <inheritdoc />
    public override ConstraintCheck CheckConstraints(Asn1OpenValue value) => ConstraintCheck.InRoot;
}
