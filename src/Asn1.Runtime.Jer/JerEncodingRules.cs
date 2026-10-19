using System.Buffers;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Asn1.Runtime.Jer;

/// <summary>
/// X.697 JavaScript Object Notation Encoding Rules without encoding instructions. <see cref="Instance"/> writes
/// compact JSON; <see cref="Indented"/> writes the same JSON text with indentation (both are valid JER).
/// </summary>
public sealed class JerEncodingRule : Asn1EncodingRule
{
    /// <summary>JER with compact output.</summary>
    public static readonly JerEncodingRule Instance = new(false);

    /// <summary>JER with indented output.</summary>
    public static readonly JerEncodingRule Indented = new(true);

    private readonly bool _indented;

    private JerEncodingRule(bool indented) => _indented = indented;

    /// <inheritdoc />
    public override string Name => "JER";

    /// <inheritdoc />
    public override bool IsBinary => false;

    /// <inheritdoc />
    public override void Encode<T>(Asn1Type<T> type, T value, IBufferWriter<byte> output, Asn1EncodingOptions? options = null)
    {
        options ??= Asn1EncodingOptions.Default;
        var buffer = new ArrayBufferWriter<byte>();
        using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = _indented, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            type.Encode(new JerWriter(json, options), value);
        }

        if (buffer.WrittenCount > options.MaxEncodedSize)
        {
            throw new Asn1EncodeException($"the encoding of {buffer.WrittenCount} octets exceeds the limit of {options.MaxEncodedSize}");
        }

        // 7.6.2: the JSON text in UTF-8.
        output.Write(buffer.WrittenSpan);
    }

    /// <inheritdoc />
    public override T Decode<T>(Asn1Type<T> type, ReadOnlyMemory<byte> data, Asn1DecodingOptions? options, out int bytesConsumed)
    {
        options ??= Asn1DecodingOptions.Default;
        CheckInputSize(data, options);
        if (!System.Text.Unicode.Utf8.IsValid(data.Span))
        {
            // 7.6.2: a complete JER encoding is UTF-8 (JsonDocument would only detect this lazily).
            throw new Asn1DecodeException(Asn1DecodeError.Malformed, "the encoding is not valid UTF-8 (X.697 7.6.2)");
        }

        var reader = new Utf8JsonReader(data.Span, new JsonReaderOptions { MaxDepth = options.MaxDepth + 1, CommentHandling = JsonCommentHandling.Disallow });
        JsonDocument document;
        try
        {
            document = JsonDocument.ParseValue(ref reader);
        }
        catch (JsonException e)
        {
            var depth = e.Message.Contains("depth", StringComparison.OrdinalIgnoreCase);
            throw new Asn1DecodeException(depth ? Asn1DecodeError.LimitExceeded : Asn1DecodeError.Malformed, $"invalid JSON: {e.Message}");
        }

        using (document)
        {
            bytesConsumed = (int)reader.BytesConsumed;
            while (bytesConsumed < data.Length && data.Span[bytesConsumed] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
            {
                bytesConsumed++;
            }

            if (options.RejectTrailingData && bytesConsumed != data.Length)
            {
                throw new Asn1DecodeException(Asn1DecodeError.TrailingData, $"{data.Length - bytesConsumed} octet(s) follow the JSON value", bytesConsumed * 8L);
            }

            var jer = new JerReader(document.RootElement, options);
            jer.TrackAllocation(data.Length);
            return type.Decode(jer);
        }
    }
}
