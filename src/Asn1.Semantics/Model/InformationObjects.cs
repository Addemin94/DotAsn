using System.Collections.Immutable;
using Asn1.Diagnostics;
using Asn1.Syntax;

namespace Asn1.Semantics;

/// <summary>Kinds of information object class fields (X.681 9).</summary>
public enum ClassFieldKind
{
    /// <summary>Type field <c>&amp;Type</c>.</summary>
    Type,

    /// <summary>Fixed-type value field <c>&amp;value Type</c>.</summary>
    FixedTypeValue,

    /// <summary>Variable-type value field <c>&amp;value &amp;Type</c>.</summary>
    VariableTypeValue,

    /// <summary>Fixed-type value set field <c>&amp;Values Type</c>.</summary>
    FixedTypeValueSet,

    /// <summary>Variable-type value set field <c>&amp;Values &amp;Type</c>.</summary>
    VariableTypeValueSet,

    /// <summary>Object field <c>&amp;object CLASS</c>.</summary>
    Object,

    /// <summary>Object set field <c>&amp;Objects CLASS</c>.</summary>
    ObjectSet,
}

/// <summary>A field of an information object class.</summary>
public sealed class ClassField
{
    internal ClassField(string name, ClassFieldKind kind, FieldSpecSyntax syntax)
    {
        Name = name;
        Kind = kind;
        Syntax = syntax;
    }

    /// <summary>Name including the ampersand.</summary>
    public string Name { get; }

    /// <summary>Kind.</summary>
    public ClassFieldKind Kind { get; }

    /// <summary>The fixed type for value and value set fields.</summary>
    public AsnType? FixedType { get; internal set; }

    /// <summary>The class for object and object set fields.</summary>
    public ObjectClass? FieldClass { get; internal set; }

    /// <summary>The referenced type field for variable-type fields.</summary>
    public string? TypeFieldReference { get; internal set; }

    /// <summary>UNIQUE.</summary>
    public bool IsUnique => Syntax.IsUnique;

    /// <summary>OPTIONAL.</summary>
    public bool IsOptional => Syntax.IsOptional;

    /// <summary>True when a DEFAULT setting exists.</summary>
    public bool HasDefault => Syntax.DefaultSetting is not null;

    /// <summary>The syntax.</summary>
    public FieldSpecSyntax Syntax { get; }
}

/// <summary>An information object class (X.681).</summary>
public sealed class ObjectClass
{
    internal ObjectClass(string name, SourceLocation location)
    {
        Name = name;
        Location = location;
    }

    /// <summary>The class name.</summary>
    public string Name { get; }

    /// <summary>Location.</summary>
    public SourceLocation Location { get; }

    /// <summary>Fields in definition order.</summary>
    public ImmutableArray<ClassField> Fields { get; internal set; } = [];

    /// <summary>User-defined syntax, if any.</summary>
    public ImmutableArray<SyntaxTokenSpecSyntax>? WithSyntax { get; internal set; }

    /// <summary>Finds a field by name (with ampersand).</summary>
    public ClassField? FindField(string name) => Fields.FirstOrDefault(f => f.Name == name);

    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>A setting of one field of an information object.</summary>
public sealed class FieldSetting
{
    internal FieldSetting(ClassField field)
    {
        Field = field;
    }

    /// <summary>The field.</summary>
    public ClassField Field { get; }

    /// <summary>The type (type fields).</summary>
    public AsnType? Type { get; internal set; }

    /// <summary>The value (value fields).</summary>
    public AsnValue? Value { get; internal set; }

    /// <summary>The type of the value (value fields).</summary>
    public AsnType? ValueType { get; internal set; }

    /// <summary>The object (object fields).</summary>
    public InformationObject? Object { get; internal set; }

    /// <summary>The object set (object set fields).</summary>
    public ObjectSet? ObjectSet { get; internal set; }

    /// <summary>The value set type (value set fields).</summary>
    public AsnType? ValueSet { get; internal set; }
}

/// <summary>An information object.</summary>
public sealed class InformationObject
{
    internal InformationObject(ObjectClass @class, string? name, SourceLocation location)
    {
        Class = @class;
        Name = name;
        Location = location;
    }

    /// <summary>The class.</summary>
    public ObjectClass Class { get; }

    /// <summary>The assigned name, if any.</summary>
    public string? Name { get; }

    /// <summary>Location.</summary>
    public SourceLocation Location { get; }

    /// <summary>Field settings by field name.</summary>
    public ImmutableDictionary<string, FieldSetting> Settings { get; internal set; } = ImmutableDictionary<string, FieldSetting>.Empty;

    /// <summary>Gets a setting.</summary>
    public FieldSetting? this[string field] => Settings.GetValueOrDefault(field);

    /// <inheritdoc />
    public override string ToString() => Name ?? "{object of " + Class.Name + "}";
}

/// <summary>An information object set.</summary>
public sealed class ObjectSet
{
    internal ObjectSet(ObjectClass @class, string? name, SourceLocation location)
    {
        Class = @class;
        Name = name;
        Location = location;
    }

    /// <summary>The class.</summary>
    public ObjectClass Class { get; }

    /// <summary>The assigned name, if any.</summary>
    public string? Name { get; }

    /// <summary>Location.</summary>
    public SourceLocation Location { get; }

    /// <summary>Objects in the root and additions.</summary>
    public ImmutableArray<InformationObject> Objects { get; internal set; } = [];

    /// <summary>True if the set is extensible.</summary>
    public bool IsExtensible { get; internal set; }

    /// <inheritdoc />
    public override string ToString() => Name ?? "{object set of " + Class.Name + "}";
}
