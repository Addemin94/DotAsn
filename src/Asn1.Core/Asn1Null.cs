namespace Asn1;

/// <summary>The single value of the ASN.1 NULL type.</summary>
public readonly record struct Asn1Null
{
    /// <summary>The NULL value.</summary>
    public static readonly Asn1Null Value;

    /// <inheritdoc />
    public override string ToString() => "NULL";
}
