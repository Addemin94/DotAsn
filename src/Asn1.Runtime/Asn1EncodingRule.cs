using System.Buffers;

namespace Asn1.Runtime;

/// <summary>
/// An encoding rule (BER, DER, UPER, ...). Implementations live in their own assemblies and create
/// rule-specific writers and readers. Instances are stateless and thread-safe.
/// </summary>
public abstract class Asn1EncodingRule
{
    /// <summary>The rule name as used on the command line (e.g. "DER", "UPER").</summary>
    public abstract string Name { get; }

    /// <summary>True for binary rules; false for XML/JSON text rules.</summary>
    public virtual bool IsBinary => true;

    /// <summary>Encodes a value into a new byte array.</summary>
    public byte[] Encode<T>(Asn1Type<T> type, T value, Asn1EncodingOptions? options = null)
    {
        var buffer = new ArrayBufferWriter<byte>();
        Encode(type, value, buffer, options);
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Encodes a value into a buffer writer.</summary>
    public abstract void Encode<T>(Asn1Type<T> type, T value, IBufferWriter<byte> output, Asn1EncodingOptions? options = null);

    /// <summary>Decodes a complete encoding (rejecting trailing data unless disabled in the options).</summary>
    public T Decode<T>(Asn1Type<T> type, ReadOnlyMemory<byte> data, Asn1DecodingOptions? options = null) =>
        Decode(type, data, options, out _);

    /// <summary>Decodes a value from the start of <paramref name="data"/>, reporting the number of bytes consumed.</summary>
    public abstract T Decode<T>(Asn1Type<T> type, ReadOnlyMemory<byte> data, Asn1DecodingOptions? options, out int bytesConsumed);

    /// <summary>Validates the input size against the decoding limits.</summary>
    protected static void CheckInputSize(ReadOnlyMemory<byte> data, Asn1DecodingOptions options)
    {
        if (data.Length > options.MaxInputSize)
        {
            throw new Asn1DecodeException(Asn1DecodeError.LimitExceeded, $"Input of {data.Length} bytes exceeds the maximum of {options.MaxInputSize}");
        }
    }

    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>Convenience entry points for encoding and decoding generated types.</summary>
public static class Asn1Serializer
{
    /// <summary>Encodes a generated value.</summary>
    public static byte[] Encode<T>(T value, Asn1EncodingRule rule, Asn1EncodingOptions? options = null)
        where T : IAsn1Value<T> => rule.Encode(T.Asn1Type, value, options);

    /// <summary>Encodes a generated value whose static type is not known (see <see cref="IAsn1Value"/>).</summary>
    public static byte[] Encode(IAsn1Value value, Asn1EncodingRule rule, Asn1EncodingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Asn1Type.EncodeBoxed(rule, value, options);
    }

    /// <summary>Encodes a value with an explicit descriptor.</summary>
    public static byte[] Encode<T>(T value, Asn1Type<T> type, Asn1EncodingRule rule, Asn1EncodingOptions? options = null) =>
        rule.Encode(type, value, options);

    /// <summary>Encodes a generated value into a buffer writer.</summary>
    public static void Encode<T>(T value, IBufferWriter<byte> output, Asn1EncodingRule rule, Asn1EncodingOptions? options = null)
        where T : IAsn1Value<T> => rule.Encode(T.Asn1Type, value, output, options);

    /// <summary>Encodes a generated value whose static type is not known into a buffer writer.</summary>
    public static void Encode(IAsn1Value value, IBufferWriter<byte> output, Asn1EncodingRule rule, Asn1EncodingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        value.Asn1Type.EncodeBoxed(rule, value, output, options);
    }

    /// <summary>Encodes a generated value whose static type is not known to a stream.</summary>
    public static async Task EncodeAsync(IAsn1Value value, Stream stream, Asn1EncodingRule rule, Asn1EncodingOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var bytes = Encode(value, rule, options);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Encodes a generated value to a stream.</summary>
    public static async Task EncodeAsync<T>(T value, Stream stream, Asn1EncodingRule rule, Asn1EncodingOptions? options = null, CancellationToken cancellationToken = default)
        where T : IAsn1Value<T>
    {
        var bytes = rule.Encode(T.Asn1Type, value, options);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Decodes a generated type.</summary>
    public static T Decode<T>(ReadOnlyMemory<byte> data, Asn1EncodingRule rule, Asn1DecodingOptions? options = null)
        where T : IAsn1Value<T> => rule.Decode(T.Asn1Type, data, options);

    /// <summary>Decodes with an explicit descriptor.</summary>
    public static T Decode<T>(ReadOnlyMemory<byte> data, Asn1Type<T> type, Asn1EncodingRule rule, Asn1DecodingOptions? options = null) =>
        rule.Decode(type, data, options);

    /// <summary>Tries to decode, returning the error instead of throwing.</summary>
    public static bool TryDecode<T>(ReadOnlyMemory<byte> data, Asn1Type<T> type, Asn1EncodingRule rule, out T? value, out Asn1Exception? error, Asn1DecodingOptions? options = null)
    {
        try
        {
            value = rule.Decode(type, data, options);
            error = null;
            return true;
        }
        catch (Asn1Exception e)
        {
            value = default;
            error = e;
            return false;
        }
    }

    /// <summary>Reads a stream (bounded by <see cref="Asn1DecodingOptions.MaxInputSize"/>) and decodes it.</summary>
    public static async Task<T> DecodeAsync<T>(Stream stream, Asn1Type<T> type, Asn1EncodingRule rule, Asn1DecodingOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= Asn1DecodingOptions.Default;
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (ms.Length + read > options.MaxInputSize)
            {
                throw new Asn1DecodeException(Asn1DecodeError.LimitExceeded, $"Input exceeds the maximum of {options.MaxInputSize} bytes");
            }

            ms.Write(buffer, 0, read);
        }

        return rule.Decode(type, ms.GetBuffer().AsMemory(0, (int)ms.Length), options);
    }

    /// <summary>Validates a generated value against its constraints without encoding it.</summary>
    public static IReadOnlyList<Asn1ValidationError> Validate<T>(T value)
        where T : IAsn1Value<T> => Asn1Validator.ValidateValue(T.Asn1Type, value);

    /// <summary>Validates a generated value whose static type is not known against its constraints.</summary>
    public static IReadOnlyList<Asn1ValidationError> Validate(IAsn1Value value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Asn1Type.ValidateBoxed(value);
    }
}
