using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using Asn1.Diagnostics;

namespace Asn1.Semantics;

/// <summary>A resolved ASN.1 value.</summary>
public abstract class AsnValue
{
    /// <summary>Creates a value.</summary>
    protected AsnValue(SourceLocation location) => Location = location;

    /// <summary>Source location.</summary>
    public SourceLocation Location { get; }

    /// <summary>
    /// Converts to the canonical runtime representation used by constraints and generated code:
    /// BigInteger, bool, double, string, byte[], Asn1BitString, ObjectIdentifier, RelativeObjectIdentifier, Asn1Null.
    /// Returns null for structured values.
    /// </summary>
    public abstract object? ToRuntimeObject();

    /// <summary>ASN.1 value notation.</summary>
    public abstract override string ToString();
}

/// <summary>BOOLEAN value.</summary>
public sealed class BooleanValue(bool value, SourceLocation location) : AsnValue(location)
{
    /// <summary>The value.</summary>
    public bool Value { get; } = value;

    /// <inheritdoc />
    public override object? ToRuntimeObject() => Value;

    /// <inheritdoc />
    public override string ToString() => Value ? "TRUE" : "FALSE";
}

/// <summary>INTEGER value.</summary>
public sealed class IntegerValue(BigInteger value, SourceLocation location, string? name = null) : AsnValue(location)
{
    /// <summary>The value.</summary>
    public BigInteger Value { get; } = value;

    /// <summary>The named number used to express it, if any.</summary>
    public string? Name { get; } = name;

    /// <inheritdoc />
    public override object? ToRuntimeObject() => Value;

    /// <inheritdoc />
    public override string ToString() => Name ?? Value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>ENUMERATED value.</summary>
public sealed class EnumeratedValue(string name, BigInteger number, SourceLocation location) : AsnValue(location)
{
    /// <summary>The identifier.</summary>
    public string Name { get; } = name;

    /// <summary>The numeric value.</summary>
    public BigInteger Number { get; } = number;

    /// <inheritdoc />
    public override object? ToRuntimeObject() => Number;

    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>REAL value.</summary>
public sealed class RealValue(double value, SourceLocation location) : AsnValue(location)
{
    /// <summary>The value.</summary>
    public double Value { get; } = value;

    /// <inheritdoc />
    public override object? ToRuntimeObject() => Value;

    /// <inheritdoc />
    public override string ToString() => double.IsPositiveInfinity(Value) ? "PLUS-INFINITY" : double.IsNegativeInfinity(Value) ? "MINUS-INFINITY" : double.IsNaN(Value) ? "NOT-A-NUMBER" : Value.ToString("R", CultureInfo.InvariantCulture);
}

/// <summary>NULL value.</summary>
public sealed class NullValue(SourceLocation location) : AsnValue(location)
{
    /// <inheritdoc />
    public override object? ToRuntimeObject() => Asn1Null.Value;

    /// <inheritdoc />
    public override string ToString() => "NULL";
}

/// <summary>BIT STRING value.</summary>
public sealed class BitStringValue(Asn1BitString value, SourceLocation location, ImmutableArray<string> namedBits = default) : AsnValue(location)
{
    /// <summary>The value.</summary>
    public Asn1BitString Value { get; } = value;

    /// <summary>Named bits used to express it (empty when given as a bstring/hstring).</summary>
    public ImmutableArray<string> NamedBits { get; } = namedBits.IsDefault ? [] : namedBits;

    /// <inheritdoc />
    public override object? ToRuntimeObject() => Value;

    /// <inheritdoc />
    public override string ToString() => NamedBits.Length > 0 ? "{ " + string.Join(", ", NamedBits) + " }" : Value.ToString();
}

/// <summary>OCTET STRING value.</summary>
public sealed class OctetStringValue(byte[] value, SourceLocation location) : AsnValue(location)
{
    /// <summary>The value.</summary>
    public ImmutableArray<byte> Value { get; } = [.. value];

    /// <inheritdoc />
    public override object? ToRuntimeObject() => Value.ToArray();

    /// <inheritdoc />
    public override string ToString() => "'" + Convert.ToHexString(Value.AsSpan()) + "'H";
}

/// <summary>Character string or time value (string-based).</summary>
public sealed class StringValue(string value, SourceLocation location) : AsnValue(location)
{
    /// <summary>The value.</summary>
    public string Value { get; } = value;

    /// <inheritdoc />
    public override object? ToRuntimeObject() => Value;

    /// <inheritdoc />
    public override string ToString() => "\"" + Value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}

/// <summary>OBJECT IDENTIFIER value.</summary>
public sealed class ObjectIdentifierValue(ObjectIdentifier value, SourceLocation location) : AsnValue(location)
{
    /// <summary>The value.</summary>
    public ObjectIdentifier Value { get; } = value;

    /// <inheritdoc />
    public override object? ToRuntimeObject() => Value;

    /// <inheritdoc />
    public override string ToString() => "{ " + string.Join(' ', Value.Arcs) + " }";
}

/// <summary>RELATIVE-OID value.</summary>
public sealed class RelativeOidValue(RelativeObjectIdentifier value, SourceLocation location) : AsnValue(location)
{
    /// <summary>The value.</summary>
    public RelativeObjectIdentifier Value { get; } = value;

    /// <inheritdoc />
    public override object? ToRuntimeObject() => Value;

    /// <inheritdoc />
    public override string ToString() => "{ " + string.Join(' ', Value.Arcs) + " }";
}

/// <summary>A named component value inside a SEQUENCE/SET value.</summary>
/// <param name="Name">Component identifier.</param>
/// <param name="Value">Value.</param>
public sealed record NamedValue(string Name, AsnValue Value);

/// <summary>SEQUENCE or SET value.</summary>
public sealed class SequenceValue(ImmutableArray<NamedValue> components, SourceLocation location) : AsnValue(location)
{
    /// <summary>Present component values in textual order.</summary>
    public ImmutableArray<NamedValue> Components { get; } = components;

    /// <summary>Gets a component value by name.</summary>
    public AsnValue? this[string name] => Components.FirstOrDefault(c => c.Name == name)?.Value;

    /// <inheritdoc />
    public override object? ToRuntimeObject() => null;

    /// <inheritdoc />
    public override string ToString() => Components.Length == 0 ? "{}" : "{ " + string.Join(", ", Components.Select(c => c.Name + " " + c.Value)) + " }";
}

/// <summary>SEQUENCE OF or SET OF value.</summary>
public sealed class SequenceOfValue(ImmutableArray<AsnValue> items, SourceLocation location) : AsnValue(location)
{
    /// <summary>Elements.</summary>
    public ImmutableArray<AsnValue> Items { get; } = items;

    /// <inheritdoc />
    public override object? ToRuntimeObject() => null;

    /// <inheritdoc />
    public override string ToString() => Items.Length == 0 ? "{}" : "{ " + string.Join(", ", Items) + " }";
}

/// <summary>CHOICE value.</summary>
public sealed class ChoiceValue(string alternative, AsnValue value, SourceLocation location) : AsnValue(location)
{
    /// <summary>The chosen alternative.</summary>
    public string Alternative { get; } = alternative;

    /// <summary>The alternative's value.</summary>
    public AsnValue Value { get; } = value;

    /// <inheritdoc />
    public override object? ToRuntimeObject() => null;

    /// <inheritdoc />
    public override string ToString() => $"{Alternative} : {Value}";
}

/// <summary>A value of an open type, carrying its actual type.</summary>
public sealed class OpenTypeValue(AsnType type, AsnValue value, SourceLocation location) : AsnValue(location)
{
    /// <summary>The actual type.</summary>
    public AsnType Type { get; } = type;

    /// <summary>The value.</summary>
    public AsnValue Value { get; } = value;

    /// <inheritdoc />
    public override object? ToRuntimeObject() => null;

    /// <inheritdoc />
    public override string ToString() => $"{Type.DisplayName} : {Value}";
}

/// <summary>Placeholder for a value that failed to resolve.</summary>
public sealed class ErrorValue(SourceLocation location) : AsnValue(location)
{
    /// <inheritdoc />
    public override object? ToRuntimeObject() => null;

    /// <inheritdoc />
    public override string ToString() => "<error>";
}
