using System.Buffers;

namespace Asn1.Runtime.Ber;

/// <summary>Base class for the X.690 encoding rules.</summary>
public abstract class X690EncodingRule : Asn1EncodingRule
{
    private protected X690EncodingRule(BerProfile profile) => Profile = profile;

    /// <summary>The X.690 profile.</summary>
    public BerProfile Profile { get; }

    /// <summary>BER only: use the indefinite length form for constructed encodings.</summary>
    public bool UseIndefiniteLength { get; init; }

    /// <summary>BER only: encode DEFAULT components even when they hold their default value.</summary>
    public bool EncodeDefaultValues { get; init; }

    /// <inheritdoc />
    public override void Encode<T>(Asn1Type<T> type, T value, IBufferWriter<byte> output, Asn1EncodingOptions? options = null)
    {
        options ??= Asn1EncodingOptions.Default;
        var tlv = new TlvWriter(256, options.MaxEncodedSize);
        try
        {
            var writer = new BerWriter(tlv, Profile, options, UseIndefiniteLength, EncodeDefaultValues);
            type.Encode(writer, value);
            output.Write(tlv.WrittenSpan);
        }
        finally
        {
            tlv.Release();
        }
    }

    /// <inheritdoc />
    public override T Decode<T>(Asn1Type<T> type, ReadOnlyMemory<byte> data, Asn1DecodingOptions? options, out int bytesConsumed)
    {
        options ??= Asn1DecodingOptions.Default;
        CheckInputSize(data, options);
        var reader = new BerReader(data, Profile, options);
        var value = type.Decode(reader);
        bytesConsumed = reader.Position;
        if (options.RejectTrailingData && reader.Position != data.Length)
        {
            throw new Asn1DecodeException(Asn1DecodeError.TrailingData, $"{data.Length - reader.Position} octet(s) follow the encoded value", reader.Position * 8L);
        }

        return value;
    }
}

/// <summary>Basic Encoding Rules (X.690 clause 8). Encodes definite-length, primitive forms by default; decodes every valid BER form.</summary>
public sealed class BerEncodingRule : X690EncodingRule
{
    /// <summary>The default BER rule.</summary>
    public static readonly BerEncodingRule Instance = new();

    /// <summary>Creates a BER rule.</summary>
    public BerEncodingRule() : base(BerProfile.Ber)
    {
    }

    /// <inheritdoc />
    public override string Name => "BER";
}

/// <summary>
/// Distinguished Encoding Rules (X.690 clause 10 and 11): canonical encoding and strict decoding that rejects
/// every BER-only form (indefinite and non-minimal lengths, constructed strings, non-0xFF TRUE, encoded DEFAULT
/// values, unsorted SET/SET OF, non-zero unused bits, non-canonical times and REALs).
/// </summary>
public sealed class DerEncodingRule : X690EncodingRule
{
    /// <summary>The DER rule.</summary>
    public static readonly DerEncodingRule Instance = new();

    /// <summary>Creates a DER rule.</summary>
    public DerEncodingRule() : base(BerProfile.Der)
    {
    }

    /// <inheritdoc />
    public override string Name => "DER";
}

/// <summary>
/// Canonical Encoding Rules (X.690 clause 9 and 11): indefinite-length constructed encodings, strings
/// fragmented into 1000-octet segments, plus the shared canonical restrictions.
/// </summary>
public sealed class CerEncodingRule : X690EncodingRule
{
    /// <summary>The CER rule.</summary>
    public static readonly CerEncodingRule Instance = new();

    /// <summary>Creates a CER rule.</summary>
    public CerEncodingRule() : base(BerProfile.Cer)
    {
    }

    /// <inheritdoc />
    public override string Name => "CER";
}
