namespace Asn1.Runtime;

/// <summary>Options for encoding.</summary>
public sealed record Asn1EncodingOptions
{
    /// <summary>Default options.</summary>
    public static readonly Asn1EncodingOptions Default = new();

    /// <summary>Validate values against their constraints before encoding (default true).</summary>
    public bool ValidateConstraints { get; init; } = true;

    /// <summary>
    /// Permit values outside the root and additions of an extensible constraint (values of a later version).
    /// Off by default: senders should only produce values known to their version.
    /// </summary>
    public bool AllowValuesOutsideExtensibleConstraints { get; init; }

    /// <summary>Maximum nesting depth of structured values.</summary>
    public int MaxDepth { get; init; } = 256;

    /// <summary>Maximum size of the produced encoding in bytes.</summary>
    public long MaxEncodedSize { get; init; } = 256L * 1024 * 1024;

    /// <summary>Cancellation for long-running operations.</summary>
    public CancellationToken CancellationToken { get; init; }
}

/// <summary>Options and resource limits for decoding untrusted input.</summary>
public sealed record Asn1DecodingOptions
{
    /// <summary>Default options.</summary>
    public static readonly Asn1DecodingOptions Default = new();

    /// <summary>Validate decoded values against all their constraints (default true).</summary>
    public bool ValidateConstraints { get; init; } = true;

    /// <summary>Fail if bytes remain after the top-level value (default true).</summary>
    public bool RejectTrailingData { get; init; } = true;

    /// <summary>Maximum nesting depth (tags, structures, collections).</summary>
    public int MaxDepth { get; init; } = 128;

    /// <summary>Maximum accepted input size in bytes.</summary>
    public long MaxInputSize { get; init; } = 64L * 1024 * 1024;

    /// <summary>Maximum number of elements in one SEQUENCE OF / SET OF.</summary>
    public int MaxCollectionCount { get; init; } = 1_000_000;

    /// <summary>Maximum length of a single string, octet string or bit string in bytes.</summary>
    public int MaxStringLength { get; init; } = 16 * 1024 * 1024;

    /// <summary>Maximum total bytes allocated for decoded string, octet and bit string contents.</summary>
    public long MaxTotalAllocation { get; init; } = 256L * 1024 * 1024;

    /// <summary>Maximum number of bytes of an unconstrained INTEGER.</summary>
    public int MaxIntegerLength { get; init; } = 1024;

    /// <summary>Cancellation for long-running operations.</summary>
    public CancellationToken CancellationToken { get; init; }
}
