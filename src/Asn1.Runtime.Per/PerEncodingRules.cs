using System.Buffers;
using Asn1.Runtime.Buffers;

namespace Asn1.Runtime.Per;

/// <summary>X.691 BASIC-PER. Use <see cref="Aligned"/> (APER) or <see cref="Unaligned"/> (UPER).</summary>
public sealed class PerEncodingRule : Asn1EncodingRule
{
    /// <summary>BASIC-PER, ALIGNED variant.</summary>
    public static readonly PerEncodingRule Aligned = new(true);

    /// <summary>BASIC-PER, UNALIGNED variant.</summary>
    public static readonly PerEncodingRule Unaligned = new(false);

    private PerEncodingRule(bool aligned) => IsAligned = aligned;

    /// <summary>True for the ALIGNED variant.</summary>
    public bool IsAligned { get; }

    /// <inheritdoc />
    public override string Name => IsAligned ? "APER" : "UPER";

    /// <inheritdoc />
    public override void Encode<T>(Asn1Type<T> type, T value, IBufferWriter<byte> output, Asn1EncodingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        options ??= Asn1EncodingOptions.Default;
        var writer = PerWriter.Rent(IsAligned, Name, options);
        type.Encode(writer, value);

        // X.691 11.1: an empty encoding becomes one zero octet; otherwise pad to an octet boundary.
        output.Write(writer.Output.BitPosition == 0 ? EmptyEncoding(options) : writer.Output.WrittenSpan);
        writer.Return();
    }

    /// <inheritdoc />
    public override byte[] Encode<T>(Asn1Type<T> type, T value, Asn1EncodingOptions? options = null) => Encode(type, value, out _, options);

    /// <summary>Encodes a value and also returns the exact number of significant bits (before padding).</summary>
    public byte[] Encode<T>(Asn1Type<T> type, T value, out long bitLength, Asn1EncodingOptions? options = null)
    {
        options ??= Asn1EncodingOptions.Default;
        var writer = PerWriter.Rent(IsAligned, Name, options);
        type.Encode(writer, value);
        bitLength = writer.Output.BitPosition;
        var result = bitLength == 0 ? EmptyEncoding(options) : writer.Output.ToArray();
        writer.Return();
        return result;
    }

    private static byte[] EmptyEncoding(Asn1EncodingOptions options) => options.EmptyPerEncodingAsZeroOctet ? [0] : [];

    /// <inheritdoc />
    public override T Decode<T>(Asn1Type<T> type, ReadOnlyMemory<byte> data, Asn1DecodingOptions? options, out int bytesConsumed)
    {
        options ??= Asn1DecodingOptions.Default;
        CheckInputSize(data, options);
        var reader = PerReader.Rent(data, IsAligned, Name, options);
        var bits = reader.Input;
        var value = type.Decode(reader);

        // Empty input only gets here for a value of no bits (anything else is truncated): X.691 11.1.3 sends that as one
        // zero octet, but asn1scc and asn1tools send no octets, so both are accepted.
        bytesConsumed = data.Length == 0 ? 0 : Math.Max(1, (int)((bits.BitPosition + 7) / 8));

        if (options.RejectTrailingData && bytesConsumed != data.Length)
        {
            throw new Asn1DecodeException(Asn1DecodeError.TrailingData, $"{data.Length - bytesConsumed} octet(s) follow the encoded value", bits.BitPosition);
        }

        reader.Return();
        return value;
    }
}
