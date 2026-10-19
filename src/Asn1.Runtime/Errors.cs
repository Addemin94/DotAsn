namespace Asn1.Runtime;

/// <summary>Base class of all runtime ASN.1 errors.</summary>
public class Asn1Exception : Exception
{
    /// <summary>Creates an exception.</summary>
    public Asn1Exception()
    {
    }

    /// <summary>Creates an exception.</summary>
    public Asn1Exception(string message) : base(message)
    {
    }

    /// <summary>Creates an exception.</summary>
    public Asn1Exception(string message, Exception inner) : base(message, inner)
    {
    }

    /// <summary>The component path where the error occurred (e.g. <c>Message.reports[2].data</c>), if known.</summary>
    public string? Path { get; init; }
}

/// <summary>Classification of decoding failures.</summary>
public enum Asn1DecodeError
{
    /// <summary>The input ended prematurely.</summary>
    Truncated,

    /// <summary>An unexpected or malformed tag.</summary>
    InvalidTag,

    /// <summary>A malformed or out-of-range length.</summary>
    InvalidLength,

    /// <summary>Valid in a relaxed rule set but not canonical for the selected one (e.g. BER-only forms under DER).</summary>
    NonCanonical,

    /// <summary>The contents are malformed.</summary>
    Malformed,

    /// <summary>A decoded value violates its constraints.</summary>
    ConstraintViolation,

    /// <summary>A configured resource limit was exceeded.</summary>
    LimitExceeded,

    /// <summary>Bytes remain after the top-level value.</summary>
    TrailingData,

    /// <summary>The value does not fit the generated CLR type.</summary>
    Overflow,

    /// <summary>A construct is not supported by this implementation.</summary>
    Unsupported,
}

/// <summary>Thrown when decoding fails. Never thrown for out-of-bounds access: readers validate bounds first.</summary>
public sealed class Asn1DecodeException : Asn1Exception
{
    /// <summary>Creates a decode exception.</summary>
    public Asn1DecodeException(Asn1DecodeError error, string message, long bitOffset = -1, string? path = null)
        : base(Compose(message, bitOffset, path))
    {
        Error = error;
        BitOffset = bitOffset;
        Path = path;
        Reason = message;
    }

    /// <summary>The failure class.</summary>
    public Asn1DecodeError Error { get; }

    /// <summary>The bit offset in the input where the error was detected, or -1.</summary>
    public long BitOffset { get; }

    /// <summary>The message without position information.</summary>
    public string Reason { get; }

    private static string Compose(string message, long bitOffset, string? path)
    {
        var where = bitOffset < 0 ? string.Empty : bitOffset % 8 == 0 ? $" at byte {bitOffset / 8}" : $" at bit {bitOffset} (byte {bitOffset / 8}, bit {bitOffset % 8})";
        return message + where + (string.IsNullOrEmpty(path) ? string.Empty : $" in {path}");
    }
}

/// <summary>Thrown when a value cannot be encoded (constraint violations, invalid values, unsupported constructs).</summary>
public sealed class Asn1EncodeException : Asn1Exception
{
    /// <summary>Creates an encode exception.</summary>
    public Asn1EncodeException(string message, string? path = null) : base(path is null ? message : $"{message} in {path}")
    {
        Path = path;
        Reason = message;
    }

    /// <summary>The message without the path.</summary>
    public string Reason { get; }
}

/// <summary>Thrown when a value violates a constraint during validation or encoding.</summary>
public sealed class Asn1ConstraintViolationException : Asn1Exception
{
    /// <summary>Creates a constraint violation.</summary>
    public Asn1ConstraintViolationException(string typeName, string constraint, string? path = null)
        : base($"Value violates constraint {constraint} of {typeName}" + (path is null ? string.Empty : $" in {path}"))
    {
        TypeName = typeName;
        Constraint = constraint;
        Path = path;
    }

    /// <summary>The constrained type.</summary>
    public string TypeName { get; }

    /// <summary>The violated constraint notation.</summary>
    public string Constraint { get; }
}

/// <summary>Thrown when an encoding rule does not support a type or feature.</summary>
public sealed class Asn1UnsupportedException : Asn1Exception
{
    /// <summary>Creates an unsupported-feature exception.</summary>
    public Asn1UnsupportedException(string message) : base(message)
    {
    }
}
