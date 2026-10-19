namespace Asn1;

/// <summary>ASN.1 tag class (X.680 8.1).</summary>
public enum TagClass : byte
{
    /// <summary>UNIVERSAL</summary>
    Universal = 0,

    /// <summary>APPLICATION</summary>
    Application = 1,

    /// <summary>Context-specific.</summary>
    ContextSpecific = 2,

    /// <summary>PRIVATE</summary>
    Private = 3,
}

/// <summary>An ASN.1 tag: a class and a non-negative number.</summary>
/// <param name="Class">Tag class.</param>
/// <param name="Number">Tag number.</param>
public readonly record struct Asn1Tag(TagClass Class, int Number) : IComparable<Asn1Tag>
{
    /// <summary>UNIVERSAL 1 BOOLEAN.</summary>
    public static readonly Asn1Tag Boolean = Universal(UniversalTagNumber.Boolean);

    /// <summary>UNIVERSAL 2 INTEGER.</summary>
    public static readonly Asn1Tag Integer = Universal(UniversalTagNumber.Integer);

    /// <summary>UNIVERSAL 3 BIT STRING.</summary>
    public static readonly Asn1Tag BitString = Universal(UniversalTagNumber.BitString);

    /// <summary>UNIVERSAL 4 OCTET STRING.</summary>
    public static readonly Asn1Tag OctetString = Universal(UniversalTagNumber.OctetString);

    /// <summary>UNIVERSAL 5 NULL.</summary>
    public static readonly Asn1Tag Null = Universal(UniversalTagNumber.Null);

    /// <summary>UNIVERSAL 6 OBJECT IDENTIFIER.</summary>
    public static readonly Asn1Tag ObjectIdentifier = Universal(UniversalTagNumber.ObjectIdentifier);

    /// <summary>UNIVERSAL 9 REAL.</summary>
    public static readonly Asn1Tag Real = Universal(UniversalTagNumber.Real);

    /// <summary>UNIVERSAL 10 ENUMERATED.</summary>
    public static readonly Asn1Tag Enumerated = Universal(UniversalTagNumber.Enumerated);

    /// <summary>UNIVERSAL 16 SEQUENCE / SEQUENCE OF.</summary>
    public static readonly Asn1Tag Sequence = Universal(UniversalTagNumber.Sequence);

    /// <summary>UNIVERSAL 17 SET / SET OF.</summary>
    public static readonly Asn1Tag Set = Universal(UniversalTagNumber.Set);

    /// <summary>Creates a UNIVERSAL tag.</summary>
    public static Asn1Tag Universal(int number) => new(TagClass.Universal, number);

    /// <summary>Creates an APPLICATION tag.</summary>
    public static Asn1Tag Application(int number) => new(TagClass.Application, number);

    /// <summary>Creates a context-specific tag.</summary>
    public static Asn1Tag Context(int number) => new(TagClass.ContextSpecific, number);

    /// <summary>Creates a PRIVATE tag.</summary>
    public static Asn1Tag Private(int number) => new(TagClass.Private, number);

    /// <summary>
    /// Canonical tag order (X.680 8.6): UNIVERSAL, APPLICATION, context-specific, PRIVATE, then ascending number.
    /// </summary>
    public int CompareTo(Asn1Tag other)
    {
        var c = ((byte)Class).CompareTo((byte)other.Class);
        return c != 0 ? c : Number.CompareTo(other.Number);
    }

    /// <inheritdoc />
    public override string ToString() => Class switch
    {
        TagClass.Universal => $"[UNIVERSAL {Number}]",
        TagClass.Application => $"[APPLICATION {Number}]",
        TagClass.Private => $"[PRIVATE {Number}]",
        _ => $"[{Number}]",
    };
}

/// <summary>Universal tag numbers (X.680 8.4 Table 1).</summary>
public static class UniversalTagNumber
{
    /// <summary>End-of-contents (reserved for BER).</summary>
    public const int EndOfContents = 0;

    /// <summary>BOOLEAN</summary>
    public const int Boolean = 1;

    /// <summary>INTEGER</summary>
    public const int Integer = 2;

    /// <summary>BIT STRING</summary>
    public const int BitString = 3;

    /// <summary>OCTET STRING</summary>
    public const int OctetString = 4;

    /// <summary>NULL</summary>
    public const int Null = 5;

    /// <summary>OBJECT IDENTIFIER</summary>
    public const int ObjectIdentifier = 6;

    /// <summary>ObjectDescriptor</summary>
    public const int ObjectDescriptor = 7;

    /// <summary>EXTERNAL and INSTANCE OF</summary>
    public const int External = 8;

    /// <summary>REAL</summary>
    public const int Real = 9;

    /// <summary>ENUMERATED</summary>
    public const int Enumerated = 10;

    /// <summary>EMBEDDED PDV</summary>
    public const int EmbeddedPdv = 11;

    /// <summary>UTF8String</summary>
    public const int Utf8String = 12;

    /// <summary>RELATIVE-OID</summary>
    public const int RelativeOid = 13;

    /// <summary>TIME</summary>
    public const int Time = 14;

    /// <summary>SEQUENCE and SEQUENCE OF</summary>
    public const int Sequence = 16;

    /// <summary>SET and SET OF</summary>
    public const int Set = 17;

    /// <summary>NumericString</summary>
    public const int NumericString = 18;

    /// <summary>PrintableString</summary>
    public const int PrintableString = 19;

    /// <summary>TeletexString / T61String</summary>
    public const int TeletexString = 20;

    /// <summary>VideotexString</summary>
    public const int VideotexString = 21;

    /// <summary>IA5String</summary>
    public const int IA5String = 22;

    /// <summary>UTCTime</summary>
    public const int UtcTime = 23;

    /// <summary>GeneralizedTime</summary>
    public const int GeneralizedTime = 24;

    /// <summary>GraphicString</summary>
    public const int GraphicString = 25;

    /// <summary>VisibleString / ISO646String</summary>
    public const int VisibleString = 26;

    /// <summary>GeneralString</summary>
    public const int GeneralString = 27;

    /// <summary>UniversalString</summary>
    public const int UniversalString = 28;

    /// <summary>CHARACTER STRING</summary>
    public const int CharacterString = 29;

    /// <summary>BMPString</summary>
    public const int BmpString = 30;

    /// <summary>DATE</summary>
    public const int Date = 31;

    /// <summary>TIME-OF-DAY</summary>
    public const int TimeOfDay = 32;

    /// <summary>DATE-TIME</summary>
    public const int DateTime = 33;

    /// <summary>DURATION</summary>
    public const int Duration = 34;

    /// <summary>OID-IRI</summary>
    public const int OidIri = 35;

    /// <summary>RELATIVE-OID-IRI</summary>
    public const int RelativeOidIri = 36;
}
