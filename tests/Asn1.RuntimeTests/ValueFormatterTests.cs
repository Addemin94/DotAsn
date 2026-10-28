using System.Globalization;
using System.Numerics;
using Asn1.Runtime;
using Xunit;

namespace Asn1.RuntimeTests;

/// <summary>The ASN.1 value notation of <see cref="Asn1ValueFormatter"/> (used by generated ToString methods).</summary>
public class ValueFormatterTests
{
    public static TheoryData<string, object> Integers() => new()
    {
        { "-5", (sbyte)-5 },
        { "255", (byte)255 },
        { "-300", (short)-300 },
        { "65535", (ushort)65535 },
        { "-70000", -70000 },
        { "4294967295", uint.MaxValue },
        { "-9223372036854775808", long.MinValue },
        { "18446744073709551615", ulong.MaxValue },
        { "-18446744073709551616", -(BigInteger)ulong.MaxValue - 1 },
    };

    [Theory]
    [MemberData(nameof(Integers))]
    public void IntegersOfEveryClrTypeIgnoreTheCurrentCulture(string expected, object value)
    {
        // A culture whose minus sign is not '-' (as sv-SE's U+2212 is with ICU).
        var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        culture.NumberFormat.NegativeSign = "−";
        var saved = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = culture;
        try
        {
            Assert.Equal(expected, Format(value));
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    private static string Format(object value) => value switch
    {
        sbyte v => Asn1ValueFormatter.Format(new IntegerType<sbyte>(), v),
        byte v => Asn1ValueFormatter.Format(new IntegerType<byte>(), v),
        short v => Asn1ValueFormatter.Format(new IntegerType<short>(), v),
        ushort v => Asn1ValueFormatter.Format(new IntegerType<ushort>(), v),
        int v => Asn1ValueFormatter.Format(new IntegerType<int>(), v),
        uint v => Asn1ValueFormatter.Format(new IntegerType<uint>(), v),
        long v => Asn1ValueFormatter.Format(new IntegerType<long>(), v),
        ulong v => Asn1ValueFormatter.Format(new IntegerType<ulong>(), v),
        BigInteger v => Asn1ValueFormatter.Format(new IntegerType<BigInteger>(), v),
        _ => throw new ArgumentException(value.GetType().Name),
    };
}
