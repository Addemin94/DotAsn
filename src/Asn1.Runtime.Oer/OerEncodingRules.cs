using System.Buffers;
using Asn1.Runtime.Buffers;

namespace Asn1.Runtime.Oer;

/// <summary>X.696 Octet Encoding Rules. Use <see cref="Basic"/> (BASIC-OER) or <see cref="Canonical"/> (CANONICAL-OER).</summary>
public sealed class OerEncodingRule : Asn1EncodingRule
{
    /// <summary>BASIC-OER. The encoder always produces the canonical form; the decoder accepts every BASIC-OER form.</summary>
    public static readonly OerEncodingRule Basic = new(false);

    /// <summary>CANONICAL-OER (X.696 clause 31). The decoder rejects every non-canonical encoding.</summary>
    public static readonly OerEncodingRule Canonical = new(true);

    private OerEncodingRule(bool canonical) => IsCanonical = canonical;

    /// <summary>True for CANONICAL-OER.</summary>
    public bool IsCanonical { get; }

    /// <inheritdoc />
    public override string Name => IsCanonical ? "COER" : "OER";

    /// <inheritdoc />
    public override void Encode<T>(Asn1Type<T> type, T value, IBufferWriter<byte> output, Asn1EncodingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        options ??= Asn1EncodingOptions.Default;
        var writer = OerWriter.Rent(IsCanonical, options);
        type.Encode(writer, value);
        output.Write(writer.Output.WrittenSpan);
        writer.Return();
    }

    /// <inheritdoc />
    public override byte[] Encode<T>(Asn1Type<T> type, T value, Asn1EncodingOptions? options = null)
    {
        options ??= Asn1EncodingOptions.Default;
        var writer = OerWriter.Rent(IsCanonical, options);
        type.Encode(writer, value);
        var result = writer.Output.ToArray();
        writer.Return();
        return result;
    }

    /// <inheritdoc />
    public override T Decode<T>(Asn1Type<T> type, ReadOnlyMemory<byte> data, Asn1DecodingOptions? options, out int bytesConsumed)
    {
        options ??= Asn1DecodingOptions.Default;
        CheckInputSize(data, options);
        var reader = OerReader.Rent(data, IsCanonical, options);
        var bits = reader.Input;
        var value = type.Decode(reader);
        bytesConsumed = (int)(bits.BitPosition / 8);
        if (options.RejectTrailingData && bytesConsumed != data.Length)
        {
            throw new Asn1DecodeException(Asn1DecodeError.TrailingData, $"{data.Length - bytesConsumed} octet(s) follow the encoded value", bits.BitPosition);
        }

        reader.Return();
        return value;
    }
}
