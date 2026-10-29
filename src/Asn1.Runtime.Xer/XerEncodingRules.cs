using System.Buffers;
using System.Text;

namespace Asn1.Runtime.Xer;

/// <summary>
/// X.693 XML Encoding Rules. <see cref="Basic"/> (BASIC-XER) and <see cref="Canonical"/> (CANONICAL-XER) both encode
/// the canonical form. The BASIC-XER decoder accepts every BASIC-XER alternative (prolog, white-space, character
/// references, members in any order); the CANONICAL-XER decoder additionally requires the input to be exactly the
/// canonical encoding of the decoded value (X.693 7.4). EXTENDED-XER is not implemented.
/// </summary>
public sealed class XerEncodingRule : Asn1EncodingRule
{
    /// <summary>BASIC-XER (X.693 clause 8).</summary>
    public static readonly XerEncodingRule Basic = new(false);

    /// <summary>CANONICAL-XER (X.693 clause 9).</summary>
    public static readonly XerEncodingRule Canonical = new(true);

    private XerEncodingRule(bool canonical) => IsCanonical = canonical;

    /// <summary>True for CANONICAL-XER.</summary>
    public bool IsCanonical { get; }

    /// <inheritdoc />
    public override string Name => IsCanonical ? "CXER" : "XER";

    /// <inheritdoc />
    public override bool IsBinary => false;

    /// <inheritdoc />
    public override void Encode<T>(Asn1Type<T> type, T value, IBufferWriter<byte> output, Asn1EncodingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        options ??= Asn1EncodingOptions.Default;
        var writer = XerWriter.Rent(IsCanonical, options);

        // X.693 8.1, 9.1.1: an empty prolog and the XMLTypedValue of the type.
        var name = XerTypeInfo.Get(type).TypeName;
        writer.Open(name);
        type.Encode(writer, value);
        writer.Close(name);

        // The text is copied once into a pooled buffer and transcoded from there, without a string or a byte array.
        var text = writer.Output;
        var chars = ArrayPool<char>.Shared.Rent(text.Length);
        try
        {
            text.CopyTo(0, chars, text.Length);
            var source = chars.AsSpan(0, text.Length);
            var count = Encoding.UTF8.GetByteCount(source);
            if (count > options.MaxEncodedSize)
            {
                throw new Asn1EncodeException($"the encoding of {count} octets exceeds the limit of {options.MaxEncodedSize}");
            }

            var written = Encoding.UTF8.GetBytes(source, output.GetSpan(count));
            output.Advance(written);
        }
        finally
        {
            ArrayPool<char>.Shared.Return(chars);
        }

        writer.Return();
    }

    /// <inheritdoc />
    public override T Decode<T>(Asn1Type<T> type, ReadOnlyMemory<byte> data, Asn1DecodingOptions? options, out int bytesConsumed)
    {
        options ??= Asn1DecodingOptions.Default;
        CheckInputSize(data, options);
        var span = data.Span;
        if (span.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            span = span[3..];
        }

        if (!System.Text.Unicode.Utf8.IsValid(span))
        {
            throw new Asn1DecodeException(Asn1DecodeError.Malformed, "the encoding is not valid UTF-8 (X.693 8.1.3)");
        }

        var root = XerDocument.Parse(Encoding.UTF8.GetString(span), options);
        var expected = XerTypeInfo.Get(type).TypeName;
        if (root.Name != expected)
        {
            throw new Asn1DecodeException(Asn1DecodeError.Malformed, $"the document element is <{root.Name}>, expected <{expected}> (X.693 8.3.1)");
        }

        var reader = new XerReader(root, Name, options);
        reader.TrackAllocation(data.Length);
        var value = type.Decode(reader);
        bytesConsumed = data.Length;
        if (IsCanonical && !reader.SawUnknownExtension)
        {
            // X.693 7.4: CANONICAL-XER has exactly one encoding per value, so the input must equal its re-encoding.
            byte[] canonical;
            try
            {
                canonical = Encode(type, value, new Asn1EncodingOptions { MaxEncodedSize = Math.Max(data.Length, 1) * 2L + 1024 });
            }
            catch (Asn1Exception e)
            {
                throw new Asn1DecodeException(Asn1DecodeError.NonCanonical, $"the value has no CANONICAL-XER encoding: {e.Message}");
            }

            // Compared after XML end-of-line normalisation, which CANONICAL-XER cannot avoid (X.680 12.15.3 note 2).
            if (!canonical.AsSpan().SequenceEqual(data.Span) && XerSyntax.NormalizeLineEnds(Encoding.UTF8.GetString(span)) != Encoding.UTF8.GetString(canonical))
            {
                throw new Asn1DecodeException(Asn1DecodeError.NonCanonical, "the input is not the CANONICAL-XER encoding of the decoded value (X.693 clause 9)");
            }
        }

        return value;
    }
}
