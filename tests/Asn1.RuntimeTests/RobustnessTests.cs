using Asn1.Runtime;
using Asn1.Runtime.Ber;
using Xunit;

namespace Asn1.RuntimeTests;

/// <summary>Malformed, truncated and hostile inputs must fail with structured errors and bounded resources.</summary>
public class RobustnessTests
{
    private static readonly BerEncodingRule Ber = BerEncodingRule.Instance;

    [Theory]
    [InlineData("")]
    [InlineData("30")]
    [InlineData("3003")]
    [InlineData("300302")]
    [InlineData("30030201")]
    [InlineData("3084FFFFFFFF")]
    [InlineData("308480000000")]
    [InlineData("30FF")]
    [InlineData("1F")]
    [InlineData("1F80")]
    [InlineData("30800201")]
    public void TruncatedAndMalformedInputFailsCleanly(string hex)
    {
        var schema = new Schema("S ::= SEQUENCE { a INTEGER }");
        var e = Assert.Throws<Asn1DecodeException>(() => schema.Decode(Ber, "S", hex));
        Assert.NotEqual(Asn1DecodeError.Unsupported, e.Error);
    }

    [Fact]
    public void HugeDeclaredLengthDoesNotAllocate()
    {
        // OCTET STRING claiming 2 GB with 1 byte of content.
        var e = Assert.Throws<Asn1DecodeException>(() => Ber.Decode(OctetStringType.Instance, Convert.FromHexString("04847FFFFFFF00")));
        Assert.Equal(Asn1DecodeError.InvalidLength, e.Error);
    }

    [Fact]
    public void DeepNestingHitsTheDepthLimit()
    {
        var schema = new Schema("R ::= SEQUENCE { next R OPTIONAL }");
        var depth = 1000;
        // R's component 'next' is IMPLICIT [0] under automatic tagging, so each level is A0 80 ... 00 00.
        var bytes = new List<byte> { 0x30, 0x80 };
        for (var i = 0; i < depth; i++)
        {
            bytes.AddRange([0xA0, 0x80]);
        }

        for (var i = 0; i <= depth; i++)
        {
            bytes.AddRange([0x00, 0x00]);
        }

        var e = Assert.Throws<Asn1DecodeException>(() => Ber.Decode(schema["R"], bytes.ToArray()));
        Assert.Equal(Asn1DecodeError.LimitExceeded, e.Error);
    }

    [Fact]
    public void CollectionCountLimitIsEnforced()
    {
        var schema = new Schema("L ::= SEQUENCE OF NULL");
        var content = Enumerable.Repeat(new byte[] { 0x05, 0x00 }, 50).SelectMany(x => x).ToArray();
        var data = new byte[] { 0x30, 0x64 }.Concat(content).ToArray();
        var options = new Asn1DecodingOptions { MaxCollectionCount = 10 };
        Assert.Equal(Asn1DecodeError.LimitExceeded, Assert.Throws<Asn1DecodeException>(() => Ber.Decode(schema["L"], data, options)).Error);
        Assert.Equal(50, ((List<object?>)Ber.Decode(schema["L"], data)!).Count);
    }

    [Fact]
    public void TrailingDataIsRejectedByDefault()
    {
        var data = Convert.FromHexString("05000500");
        Assert.Equal(Asn1DecodeError.TrailingData, Assert.Throws<Asn1DecodeException>(() => Ber.Decode(NullType.Instance, data)).Error);
        Ber.Decode(NullType.Instance, data, new Asn1DecodingOptions { RejectTrailingData = false }, out var consumed);
        Assert.Equal(2, consumed);
    }

    [Fact]
    public void ConstraintViolationsAreRejectedOnEncodeAndDecode()
    {
        var schema = new Schema("S ::= SEQUENCE { a INTEGER (0..10), s IA5String (SIZE (1..3)) }");
        Assert.Throws<Asn1ConstraintViolationException>(() => schema.Encode(Ber, "S", "{\"a\":11,\"s\":\"x\"}"));
        Assert.Throws<Asn1ConstraintViolationException>(() => schema.Encode(Ber, "S", "{\"a\":1,\"s\":\"xxxx\"}"));
        var e = Assert.Throws<Asn1DecodeException>(() => schema.Decode(Ber, "S", "300680010B810178"));
        Assert.Equal(Asn1DecodeError.ConstraintViolation, e.Error);
        Assert.Contains("a", e.Path);
    }

    [Fact]
    public void IntegersOutsideTheClrTypeAreReportedNotTruncated()
    {
        var e = Assert.Throws<Asn1DecodeException>(() => Ber.Decode(new IntegerType<int>(), Convert.FromHexString("02050100000000")));
        Assert.Equal(Asn1DecodeError.Overflow, e.Error);
    }

    [Fact]
    public void RandomGarbageNeverEscapesAsAnUnexpectedException()
    {
        var schema = new Schema("""
            M ::= SEQUENCE { a INTEGER, b OCTET STRING OPTIONAL, c CHOICE { x BOOLEAN, y SEQUENCE OF IA5String }, d BIT STRING OPTIONAL, ... }
            """);
        var type = schema["M"];
        var random = new Random(1234);
        for (var i = 0; i < 20000; i++)
        {
            var data = new byte[random.Next(0, 40)];
            random.NextBytes(data);
            if (data.Length > 0)
            {
                data[0] = 0x30;
            }

            try
            {
                Ber.Decode(type, data);
            }
            catch (Asn1Exception)
            {
            }
        }
    }
}
