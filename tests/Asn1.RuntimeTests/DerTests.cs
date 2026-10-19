using System.Numerics;
using Asn1.Runtime;
using Asn1.Runtime.Ber;
using Xunit;

namespace Asn1.RuntimeTests;

public class DerTests
{
    private static readonly DerEncodingRule Der = DerEncodingRule.Instance;
    private static readonly BerEncodingRule Ber = BerEncodingRule.Instance;

    private static string Hex(byte[] b) => Convert.ToHexString(b);

    [Theory]
    [InlineData(0, "020100")]
    [InlineData(127, "02017F")]
    [InlineData(128, "02020080")]
    [InlineData(256, "02020100")]
    [InlineData(-128, "020180")]
    [InlineData(-129, "0202FF7F")]
    [InlineData(long.MaxValue, "02087FFFFFFFFFFFFFFF")]
    [InlineData(long.MinValue, "02088000000000000000")]
    public void IntegersUseMinimalTwosComplement(long value, string hex)
    {
        var type = new IntegerType<long>();
        Assert.Equal(hex, Hex(Der.Encode(type, value)));
        Assert.Equal(value, Der.Decode(type, Convert.FromHexString(hex)));
    }

    [Fact]
    public void BigIntegersRoundTrip()
    {
        var type = new IntegerType<BigInteger>();
        var v = BigInteger.Parse("-123456789012345678901234567890", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(v, Der.Decode(type, Der.Encode(type, v)));
    }

    [Theory]
    [InlineData("02020001")]
    [InlineData("0202FF80")]
    [InlineData("0200")]
    public void NonMinimalOrEmptyIntegersAreRejectedEvenInBer(string hex)
    {
        var e = Assert.Throws<Asn1DecodeException>(() => Ber.Decode(new IntegerType<long>(), Convert.FromHexString(hex)));
        Assert.Equal(Asn1DecodeError.Malformed, e.Error);
    }

    [Fact]
    public void BooleanTrueMustBeFFInDer()
    {
        Assert.Equal("0101FF", Hex(Der.Encode(BooleanType.Instance, true)));
        Assert.True(Ber.Decode(BooleanType.Instance, Convert.FromHexString("010101")));
        Assert.Equal(Asn1DecodeError.NonCanonical, Assert.Throws<Asn1DecodeException>(() => Der.Decode(BooleanType.Instance, Convert.FromHexString("010101"))).Error);
    }

    [Fact]
    public void ObjectIdentifierExamplesFromX690()
    {
        // X.690 8.19.5: { 2 999 3 } is encoded as 06 03 88 37 03.
        Assert.Equal("0603883703", Hex(Der.Encode(ObjectIdentifierType.Instance, new ObjectIdentifier(2, 999, 3))));
        Assert.Equal("06062A864886F70D", Hex(Der.Encode(ObjectIdentifierType.Instance, ObjectIdentifier.Parse("1.2.840.113549"))));
        Assert.Equal("2.999.3", Der.Decode(ObjectIdentifierType.Instance, Convert.FromHexString("0603883703")).ToString());
        Assert.Throws<Asn1DecodeException>(() => Der.Decode(ObjectIdentifierType.Instance, Convert.FromHexString("0603808837")));
        Assert.Throws<Asn1DecodeException>(() => Der.Decode(ObjectIdentifierType.Instance, Convert.FromHexString("06022A86")));
    }

    [Fact]
    public void BitStringExampleFromX690()
    {
        // X.690 8.6.4.2: '0A3B5F291CD'H.
        var value = Asn1BitString.FromHex("0A3B5F291CD");
        Assert.Equal("0307040A3B5F291CD0", Hex(Der.Encode(BitStringType.Instance, value)));

        // The constructed BER form of the same value (X.690 8.6.4.2 example) is accepted by BER and rejected by DER.
        var constructed = Convert.FromHexString("23800303000A3B0305045F291CD00000");
        Assert.Equal(value, Ber.Decode(BitStringType.Instance, constructed));
        Assert.Equal(Asn1DecodeError.NonCanonical, Assert.Throws<Asn1DecodeException>(() => Der.Decode(BitStringType.Instance, constructed)).Error);
    }

    [Fact]
    public void UnusedBitsMustBeZeroInDer()
    {
        var bits = Convert.FromHexString("030204F1");
        Assert.Equal(4, Ber.Decode(BitStringType.Instance, bits).Length);
        Assert.Equal(Asn1DecodeError.NonCanonical, Assert.Throws<Asn1DecodeException>(() => Der.Decode(BitStringType.Instance, bits)).Error);
    }

    [Fact]
    public void NamedBitStringsDropTrailingZerosInDer()
    {
        var schema = new Schema("B ::= BIT STRING { a(0), b(1), c(7) }");
        Assert.Equal("03020780", schema.Encode(Der, "B", "\"'10000000'B\""));
        Assert.Equal("030100", schema.Encode(Der, "B", "\"'0000'B\""));
        Assert.Equal(Asn1DecodeError.NonCanonical, Assert.Throws<Asn1DecodeException>(() => schema.Decode(Der, "B", "03020680")).Error);
    }

    [Fact]
    public void LengthsMustBeMinimalAndDefinite()
    {
        var octets = OctetStringType.Instance;
        Assert.Equal("0481800" + new string('0', 255), Hex(Der.Encode(octets, new byte[128])));
        var nonMinimal = Convert.FromHexString("0481020102");
        Assert.Equal([1, 2], Ber.Decode(octets, nonMinimal));
        Assert.Equal(Asn1DecodeError.NonCanonical, Assert.Throws<Asn1DecodeException>(() => Der.Decode(octets, nonMinimal)).Error);

        var schema = new Schema("S ::= SEQUENCE { a INTEGER }");
        Assert.Equal("{\"a\":5}", schema.Decode(Ber, "S", "3080800105" + "0000"));
        Assert.Equal(Asn1DecodeError.NonCanonical, Assert.Throws<Asn1DecodeException>(() => schema.Decode(Der, "S", "30808001050000")).Error);
    }

    [Fact]
    public void HighTagNumbersUseTheMultiOctetForm()
    {
        var schema = new Schema("A ::= [APPLICATION 31] IMPLICIT INTEGER\nB ::= [1000] IMPLICIT BOOLEAN", "EXPLICIT TAGS");
        Assert.Equal("5F1F0105", schema.Encode(Der, "A", "5"));
        Assert.Equal("9F876801FF", schema.Encode(Der, "B", "true"));
        Assert.Equal("true", schema.Decode(Der, "B", "9F876801FF"));
        // Tag 5 in the high-tag form is invalid (X.690 8.1.2.4).
        Assert.Throws<Asn1DecodeException>(() => schema.Decode(Ber, "A", "5F050105"));
    }

    [Fact]
    public void DerSetComponentsAreSortedByTag()
    {
        var schema = new Schema("S ::= SET { z [2] INTEGER, a [0] INTEGER, m [1] INTEGER }", "IMPLICIT TAGS");
        var hex = schema.Encode(Der, "S", "{\"z\":3,\"a\":1,\"m\":2}");
        Assert.Equal("3109800101810102820103", hex);
        Assert.Equal(Asn1DecodeError.NonCanonical, Assert.Throws<Asn1DecodeException>(() => schema.Decode(Der, "S", "3109820103800101810102")).Error);
        Assert.Equal("{\"z\":3,\"a\":1,\"m\":2}", schema.Decode(Ber, "S", "3109820103800101810102"));
    }

    [Fact]
    public void CerOrdersSetsByCanonicalTagOfUntaggedChoices()
    {
        // X.690 9.3 example: the order is always e, b, a.
        var schema = new Schema("""
            A ::= SET { a [3] INTEGER, b [1] CHOICE { c [2] INTEGER, d [4] INTEGER },
                        e CHOICE { f CHOICE { g [5] INTEGER, h [6] INTEGER }, i CHOICE { j [0] INTEGER } } }
            """, "IMPLICIT TAGS");
        var cer = CerEncodingRule.Instance;
        var hex = schema.Encode(cer, "A", "{\"a\":1,\"b\":{\"c\":2},\"e\":{\"f\":{\"g\":5}}}");
        Assert.Equal("3180" + "850105" + "A180820102" + "0000" + "830101" + "0000", hex);
        Assert.Equal("{\"e\":{\"f\":{\"g\":5}},\"b\":{\"c\":2},\"a\":1}", schema.Decode(cer, "A", hex));
    }

    [Fact]
    public void SetOfElementsAreSortedByEncoding()
    {
        var schema = new Schema("S ::= SET OF INTEGER");
        Assert.Equal("310A02010102017F020200FF", schema.Encode(Der, "S", "[255, 1, 127]"));
        Assert.Equal(Asn1DecodeError.NonCanonical, Assert.Throws<Asn1DecodeException>(() => schema.Decode(Der, "S", "310A020200FF02010102017F")).Error);
    }

    [Fact]
    public void DefaultValuesAreOmittedAndRejectedWhenEncoded()
    {
        var schema = new Schema("S ::= SEQUENCE { a INTEGER DEFAULT 3, b BOOLEAN }");
        Assert.Equal("30038101FF", schema.Encode(Der, "S", "{\"a\":3,\"b\":true}"));
        Assert.Equal(Asn1DecodeError.NonCanonical, Assert.Throws<Asn1DecodeException>(() => schema.Decode(Der, "S", "30068001038101FF")).Error);
        Assert.Equal("{\"a\":3,\"b\":true}", schema.Decode(Ber, "S", "30068001038101FF"));
    }

    [Fact]
    public void RealValuesUseCanonicalBinaryForm()
    {
        Assert.Equal("0900", Hex(Der.Encode(RealType.Instance, 0.0)));
        Assert.Equal("0903800001", Hex(Der.Encode(RealType.Instance, 1.0)));
        Assert.Equal("090380FF01", Hex(Der.Encode(RealType.Instance, 0.5)));
        Assert.Equal("090140", Hex(Der.Encode(RealType.Instance, double.PositiveInfinity)));
        Assert.Equal("090143", Hex(Der.Encode(RealType.Instance, -0.0)));
        foreach (var v in new[] { 3.14159, -2.5e-300, 1e308, double.Epsilon, 123456789.0 })
        {
            Assert.Equal(v, Der.Decode(RealType.Instance, Der.Encode(RealType.Instance, v)));
        }

        // Base 16 encodings are valid BER but not DER.
        Assert.Equal(1.0, Ber.Decode(RealType.Instance, Convert.FromHexString("0903A00001")));
        Assert.Throws<Asn1DecodeException>(() => Der.Decode(RealType.Instance, Convert.FromHexString("0903A00001")));
        // Decimal NR3.
        Assert.Equal(-1.5, Ber.Decode(RealType.Instance, Convert.FromHexString("0908032D31352E452D31")));
    }

    [Fact]
    public void TimesMustBeCanonicalInDer()
    {
        var utc = new TimeType(TimeKind.UtcTime);
        Assert.Equal("170D3932303532313030303030305A", Hex(Der.Encode(utc, "920521000000Z")));
        Assert.Throws<Asn1EncodeException>(() => Der.Encode(utc, "9207221321Z"));
        Assert.Equal("9207221321Z", Ber.Decode(utc, Ber.Encode(utc, "9207221321Z")));
        var gt = new TimeType(TimeKind.GeneralizedTime);
        Assert.Throws<Asn1EncodeException>(() => Der.Encode(gt, "19920622123421.0Z"));
        Assert.Equal("19920722132100.3Z", Der.Decode(gt, Der.Encode(gt, "19920722132100.3Z")));
    }

    [Fact]
    public void ExtensibleSequenceSkipsUnknownAdditions()
    {
        var v1 = new Schema("S ::= SEQUENCE { a INTEGER, ... }");
        var v2 = new Schema("S ::= SEQUENCE { a INTEGER, ..., b BOOLEAN, c OCTET STRING }");
        var hex = v2.Encode(Der, "S", "{\"a\":1,\"b\":true,\"c\":\"AABB\"}");
        Assert.Equal("{\"a\":1}", v1.Decode(Der, "S", hex));
        var closed = new Schema("S ::= SEQUENCE { a INTEGER }");
        Assert.Throws<Asn1DecodeException>(() => closed.Decode(Der, "S", hex));
    }

    [Fact]
    public void ExplicitAndImplicitTagsAreEncoded()
    {
        var schema = new Schema("A ::= [5] EXPLICIT INTEGER\nB ::= [5] IMPLICIT INTEGER\nC ::= [0] CHOICE { x [1] INTEGER }", "EXPLICIT TAGS");
        Assert.Equal("A503020107", schema.Encode(Der, "A", "7"));
        Assert.Equal("850107", schema.Encode(Der, "B", "7"));
        // x [1] INTEGER is itself EXPLICIT in an EXPLICIT TAGS module.
        Assert.Equal("A005A103020107", schema.Encode(Der, "C", "{\"x\":7}"));
    }
}
