using Asn1.Semantics;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Asn1.CodeGen.CSharp.Cs;

namespace Asn1.CodeGen.CSharp;

internal enum GenKind
{
    Sequence,
    Set,
    Choice,
    Enum,
}

/// <summary>A generated class or enum, mapped 1:1 to a structured or enumerated definition.</summary>
internal sealed class GenType
{
    public GenType(AsnType definition, string name, GenType? parent, ModuleContext module, TypeAssignmentSymbol? assignment)
    {
        Definition = definition;
        Name = name;
        Parent = parent;
        Module = module;
        Assignment = assignment;
        Kind = definition.Kind switch
        {
            TypeKind.Sequence => GenKind.Sequence,
            TypeKind.Set => GenKind.Set,
            TypeKind.Choice => GenKind.Choice,
            _ => GenKind.Enum,
        };
    }

    public AsnType Definition { get; }

    public string Name { get; }

    public GenType? Parent { get; }

    public ModuleContext Module { get; }

    public TypeAssignmentSymbol? Assignment { get; }

    public GenKind Kind { get; }

    public List<GenType> Nested { get; } = [];

    public NameScope Scope { get; } = new();

    /// <summary>Property names (SEQUENCE/SET), alternative class names (CHOICE).</summary>
    public Dictionary<AsnComponent, string> Members { get; } = new(ReferenceEqualityComparer.Instance);

    /// <summary>Enum member names by item identifier.</summary>
    public Dictionary<string, string> EnumMembers { get; } = new(StringComparer.Ordinal);

    /// <summary>For enums: the module class property holding the descriptor.</summary>
    public string? EnumDescriptorProperty { get; set; }

    /// <summary>The fully qualified (<c>global::</c>) type name.</summary>
    public NameSyntax TypeName => Parent is null ? Global(Module.Namespace + "." + Name) : Dot(Parent.TypeName, Name);

    /// <summary>The qualified name for documentation text.</summary>
    public string DisplayName => Parent is null ? $"{Module.Namespace}.{Name}" : $"{Parent.DisplayName}.{Name}";

    public string AsnName => Assignment?.QualifiedName ?? (Parent is null ? Name : $"{Parent.AsnName}.{Name}");

    /// <summary>The expression that yields the type's descriptor.</summary>
    public ExpressionSyntax DescriptorExpression => Kind == GenKind.Enum
        ? Member(Module.ClassTypeName, EnumDescriptorProperty!)
        : Member(TypeName, "Asn1Type");

    public bool IsExtensibleChoice => Kind == GenKind.Choice && Definition.IsExtensible;
}

/// <summary>Per-module generation state.</summary>
internal sealed class ModuleContext
{
    public ModuleContext(ModuleSymbol module, string ns, NameScope topScope, string className)
    {
        Module = module;
        Namespace = ns;
        TopScope = topScope;
        ClassName = className;
    }

    public ModuleSymbol Module { get; }

    public string Namespace { get; }

    public NameScope TopScope { get; }

    public string ClassName { get; }

    /// <summary>The fully qualified name of the module class.</summary>
    public NameSyntax ClassTypeName => Global(Namespace + "." + ClassName);

    public NameScope ClassScope { get; } = new(["Equals", "GetHashCode", "ToString", "GetType", "ReferenceEquals"]);

    public List<GenType> TopTypes { get; } = [];

    /// <summary>Type assignments in order with their module class property name and alias class name (null when a class is generated).</summary>
    public List<(TypeAssignmentSymbol Assignment, string Property, string? AliasClass)> Assignments { get; } = [];

    public List<(ValueAssignmentSymbol Value, string Name)> Values { get; } = [];

    public List<GenType> NestedEnums { get; } = [];
}
