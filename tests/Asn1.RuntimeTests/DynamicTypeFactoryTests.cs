using System.Numerics;
using Asn1.Runtime;
using Asn1.Runtime.Per;
using Xunit;

namespace Asn1.RuntimeTests;

/// <summary>Descriptors built at run time from the semantic model (Asn1.Dynamic), as the CLI and the studio use them.</summary>
public class DynamicTypeFactoryTests
{
    [Fact]
    public void NamedNumbersBeyond64BitsAreLeftOutOfTheMetadata()
    {
        // X.680 allows any named number; Asn1NamedNumber holds a long, so the large one is not part of the metadata.
        var schema = new Schema("""
            Big ::= INTEGER { small(1), top(18446744073709551615) } (0..18446744073709551615)
            Bits ::= BIT STRING { a(0) }
            """);

        // The boxing adapter is internal; its Inner property holds the typed descriptor.
        var boxed = schema["Big"];
        var type = (IntegerType<BigInteger>)boxed.GetType().GetProperty("Inner")!.GetValue(boxed)!;
        Assert.Equal(["small"], type.NamedNumbers.Select(n => n.Name));
        Assert.Equal("FFFFFFFFFFFFFFFF", schema.Encode(PerEncodingRule.Unaligned, "Big", "18446744073709551615"));
        Assert.Equal("0180", schema.Encode(PerEncodingRule.Unaligned, "Bits", """{"hex":"80","length":1}"""));
    }

    [Fact]
    public void NamedBitsBeyond64BitsAreUnsupported()
    {
        // Whether a BIT STRING has named bits changes DER (X.690 11.2.2), so such bits cannot simply be left out.
        var schema = new Schema("Huge ::= BIT STRING { huge(9223372036854775808) }");
        var e = Assert.Throws<Asn1UnsupportedException>(() => schema["Huge"]);
        Assert.Contains("must fit in 64 bits", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnumerationValuesBeyond64BitsAreUnsupported()
    {
        var schema = new Schema("En ::= ENUMERATED { a(1), b(100000000000000000000) }");
        var e = Assert.Throws<Asn1UnsupportedException>(() => schema["En"]);
        Assert.Contains("must fit in 64 bits", e.Message, StringComparison.Ordinal);
    }
}
