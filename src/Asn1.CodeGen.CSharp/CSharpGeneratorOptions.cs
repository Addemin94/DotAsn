namespace Asn1.CodeGen.CSharp;

/// <summary>
/// How INTEGER types without finite bounds are mapped. Bounded INTEGERs always use the smallest CLR type that holds
/// every value of their effective constraint: byte, ushort, uint or ulong for non-negative ranges, sbyte, short, int or
/// long otherwise, and BigInteger beyond 64 bits.
/// </summary>
public enum IntegerMapping
{
    /// <summary>Unbounded or extensible INTEGERs use <see cref="System.Numerics.BigInteger"/> (no precision loss).</summary>
    Precise,

    /// <summary>Unbounded or extensible INTEGERs use <see cref="long"/>; larger values are decoding errors (never truncated).</summary>
    Int64,
}

/// <summary>Layout of generated files.</summary>
public enum OutputLayout
{
    /// <summary>One file per ASN.1 module.</summary>
    FilePerModule,

    /// <summary>One file per top-level generated type plus one per module class.</summary>
    FilePerType,
}

/// <summary>Options for <see cref="CSharpGenerator"/>.</summary>
public sealed record CSharpGeneratorOptions
{
    /// <summary>Root namespace. Defaults to the PascalCase name of the first module.</summary>
    public string? Namespace { get; init; }

    /// <summary>Place each module in its own sub-namespace (default: only when several modules are generated).</summary>
    public bool? NamespacePerModule { get; init; }

    /// <summary>File layout.</summary>
    public OutputLayout Layout { get; init; } = OutputLayout.FilePerModule;

    /// <summary>INTEGER mapping policy.</summary>
    public IntegerMapping IntegerMapping { get; init; } = IntegerMapping.Precise;

    /// <summary>Emit XML documentation from ASN.1 comments.</summary>
    public bool GenerateDocumentation { get; init; } = true;

    /// <summary>Emit <c>internal</c> instead of <c>public</c> types.</summary>
    public bool InternalTypes { get; init; }

    /// <summary>Restrict generation to these modules (null: all).</summary>
    public IReadOnlySet<string>? Modules { get; init; }
}
