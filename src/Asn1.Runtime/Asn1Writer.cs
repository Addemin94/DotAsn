using System.Numerics;

namespace Asn1.Runtime;

/// <summary>
/// The encoding-rule contract for producing encodings. Each encoding rule derives a writer; descriptors
/// dispatch to the method for their kind so that encoders work on typed values without reflection.
/// </summary>
public abstract class Asn1Writer
{
    private int _depth;

    /// <summary>Creates a writer.</summary>
    protected Asn1Writer(Asn1EncodingOptions options)
    {
        Options = options;
        Path = new();
    }

    /// <summary>Creates a nested writer that shares the component path and depth of <paramref name="parent"/> (used for open types).</summary>
    protected Asn1Writer(Asn1Writer parent)
    {
        Options = parent.Options;
        Path = parent.Path;
        _depth = parent._depth;
    }

    /// <summary>Encoding options.</summary>
    public Asn1EncodingOptions Options { get; }

    /// <summary>The current component path (for error messages).</summary>
    public Asn1Path Path { get; }

    /// <summary>Prepares a writer for another encoding: clears the path and depth (which an aborted encoding may have left).</summary>
    protected void ResetForReuse()
    {
        _depth = 0;
        Path.Clear();
    }

    /// <summary>Gives a reused nested writer the current depth of <paramref name="parent"/>, which shares its path.</summary>
    protected void ContinueFrom(Asn1Writer parent)
    {
        ArgumentNullException.ThrowIfNull(parent);
        _depth = parent._depth;
    }

    /// <summary>Writes a BOOLEAN.</summary>
    public abstract void WriteBoolean(BooleanType type, bool value);

    /// <summary>Writes an INTEGER.</summary>
    public abstract void WriteInteger(Asn1Type type, BigInteger value);

    /// <summary>Writes an INTEGER that fits in 64 bits. Rules may override this for speed.</summary>
    public virtual void WriteInteger(Asn1Type type, long value) => WriteInteger(type, (BigInteger)value);

    /// <summary>Writes an ENUMERATED value.</summary>
    public abstract void WriteEnumerated(Asn1Type type, EnumerationInfo enumeration, EnumItemInfo item);

    /// <summary>Writes a REAL.</summary>
    public abstract void WriteReal(RealType type, double value);

    /// <summary>Writes a BIT STRING.</summary>
    public abstract void WriteBitString(BitStringType type, Asn1BitString value);

    /// <summary>Writes an OCTET STRING.</summary>
    public abstract void WriteOctetString(OctetStringType type, ReadOnlySpan<byte> value);

    /// <summary>Writes NULL.</summary>
    public abstract void WriteNull(NullType type);

    /// <summary>Writes an OBJECT IDENTIFIER.</summary>
    public abstract void WriteObjectIdentifier(ObjectIdentifierType type, ObjectIdentifier value);

    /// <summary>Writes a RELATIVE-OID.</summary>
    public abstract void WriteRelativeObjectIdentifier(RelativeOidType type, RelativeObjectIdentifier value);

    /// <summary>Writes a restricted character string.</summary>
    public abstract void WriteCharacterString(CharacterStringType type, string value);

    /// <summary>Writes a time value (UTCTime, GeneralizedTime, DATE, ...), already validated for its syntax.</summary>
    public abstract void WriteTime(TimeType type, string value);

    /// <summary>Writes a SEQUENCE or SET.</summary>
    public abstract void WriteSequence<T>(SequenceType<T> type, T value);

    /// <summary>Writes a SEQUENCE OF or SET OF.</summary>
    public abstract void WriteCollection<TElement>(CollectionType<TElement> type, IReadOnlyList<TElement> value);

    /// <summary>Writes a CHOICE.</summary>
    public abstract void WriteChoice<T>(ChoiceType<T> type, T value);

    /// <summary>Writes an open type value.</summary>
    public abstract void WriteOpenType(OpenType type, Asn1OpenValue value);

    /// <summary>Enters a nested structure, enforcing the depth limit.</summary>
    public void EnterNested()
    {
        if (++_depth > Options.MaxDepth)
        {
            throw new Asn1EncodeException($"Nesting exceeds the maximum depth of {Options.MaxDepth}", Path.ToString());
        }

        Options.CancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>Leaves a nested structure.</summary>
    public void ExitNested() => _depth--;

    /// <summary>Creates an encode exception carrying the current path.</summary>
    public Asn1EncodeException Error(string message) => new(message, Path.ToString());
}

/// <summary>The encoding-rule contract for decoding. Implementations validate all bounds before reading.</summary>
public abstract class Asn1Reader
{
    private int _depth;
    private long _allocated;

    /// <summary>Creates a reader.</summary>
    protected Asn1Reader(Asn1DecodingOptions options)
    {
        Options = options;
        Path = new();
    }

    /// <summary>Creates a nested reader that shares the path, depth and allocation accounting of <paramref name="parent"/>.</summary>
    protected Asn1Reader(Asn1Reader parent)
    {
        Options = parent.Options;
        Path = parent.Path;
        _depth = parent._depth;
        _parent = parent;
    }

    private readonly Asn1Reader? _parent;

    /// <summary>Decoding options.</summary>
    public Asn1DecodingOptions Options { get; }

    /// <summary>The current component path.</summary>
    public Asn1Path Path { get; }

    /// <summary>Prepares a reader for another decoding: clears the path, depth and allocation count.</summary>
    protected void ResetForReuse()
    {
        _depth = 0;
        _allocated = 0;
        Path.Clear();
    }

    /// <summary>
    /// Gives a reused nested reader the current depth of <paramref name="parent"/>. Its allocations are still accounted
    /// to the reader it was created for, which belongs to the same decoding.
    /// </summary>
    protected void ContinueFrom(Asn1Reader parent)
    {
        ArgumentNullException.ThrowIfNull(parent);
        _depth = parent._depth;
    }

    /// <summary>The current position in bits from the start of the input (for error messages).</summary>
    public abstract long BitPosition { get; }

    /// <summary>Reads a BOOLEAN.</summary>
    public abstract bool ReadBoolean(BooleanType type);

    /// <summary>Reads an INTEGER.</summary>
    public abstract BigInteger ReadInteger(Asn1Type type);

    /// <summary>
    /// Reads an INTEGER into <paramref name="value"/> when it fits in 64 bits; otherwise returns false with the value in
    /// <paramref name="large"/>. Rules may override this to avoid <see cref="BigInteger"/> arithmetic.
    /// </summary>
    public virtual bool TryReadInt64(Asn1Type type, out long value, out BigInteger large)
    {
        large = ReadInteger(type);
        return Primitives.IntegerCodec.TryToInt64(large, out value);
    }

    /// <summary>Reads an ENUMERATED value; returns the item, or an unknown extension item.</summary>
    public abstract EnumItemInfo ReadEnumerated(Asn1Type type, EnumerationInfo enumeration);

    /// <summary>Reads a REAL.</summary>
    public abstract double ReadReal(RealType type);

    /// <summary>Reads a BIT STRING.</summary>
    public abstract Asn1BitString ReadBitString(BitStringType type);

    /// <summary>Reads an OCTET STRING.</summary>
    public abstract byte[] ReadOctetString(OctetStringType type);

    /// <summary>Reads NULL.</summary>
    public abstract void ReadNull(NullType type);

    /// <summary>Reads an OBJECT IDENTIFIER.</summary>
    public abstract ObjectIdentifier ReadObjectIdentifier(ObjectIdentifierType type);

    /// <summary>Reads a RELATIVE-OID.</summary>
    public abstract RelativeObjectIdentifier ReadRelativeObjectIdentifier(RelativeOidType type);

    /// <summary>Reads a restricted character string.</summary>
    public abstract string ReadCharacterString(CharacterStringType type);

    /// <summary>Reads a time value.</summary>
    public abstract string ReadTime(TimeType type);

    /// <summary>Reads a SEQUENCE or SET.</summary>
    public abstract T ReadSequence<T>(SequenceType<T> type);

    /// <summary>Reads a SEQUENCE OF or SET OF.</summary>
    public abstract List<TElement> ReadCollection<TElement>(CollectionType<TElement> type);

    /// <summary>Reads a CHOICE.</summary>
    public abstract T ReadChoice<T>(ChoiceType<T> type);

    /// <summary>Reads an open type value (as raw encoding).</summary>
    public abstract Asn1OpenValue ReadOpenType(OpenType type);

    /// <summary>Enters a nested structure, enforcing the depth limit.</summary>
    public void EnterNested()
    {
        if (++_depth > Options.MaxDepth)
        {
            throw Error(Asn1DecodeError.LimitExceeded, $"Nesting exceeds the maximum depth of {Options.MaxDepth}");
        }

        Options.CancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>Leaves a nested structure.</summary>
    public void ExitNested() => _depth--;

    /// <summary>Accounts for an allocation of decoded content and enforces size limits.</summary>
    public void TrackAllocation(long bytes)
    {
        if (bytes > Options.MaxStringLength)
        {
            throw Error(Asn1DecodeError.LimitExceeded, $"A value of {bytes} bytes exceeds the maximum of {Options.MaxStringLength}");
        }

        if (_parent is not null)
        {
            _parent.TrackAllocation(bytes);
            return;
        }

        _allocated += bytes;
        if (_allocated > Options.MaxTotalAllocation)
        {
            throw Error(Asn1DecodeError.LimitExceeded, $"Decoded content exceeds the total allocation limit of {Options.MaxTotalAllocation} bytes");
        }
    }

    /// <summary>Checks a collection element count against the limit.</summary>
    public void CheckCollectionCount(long count)
    {
        if (count > Options.MaxCollectionCount)
        {
            throw Error(Asn1DecodeError.LimitExceeded, $"A collection of {count} elements exceeds the maximum of {Options.MaxCollectionCount}");
        }
    }

    /// <summary>Creates a decode exception at the current position and path.</summary>
    public Asn1DecodeException Error(Asn1DecodeError error, string message) => new(error, message, BitPosition, Path.ToString());
}
