using System.Buffers;

namespace Asn1.Runtime.Ber;

/// <summary>
/// A growable byte buffer that writes TLV headers. Constructed definite-length values are written by
/// reserving one length octet and back-patching it (shifting the contents when a long-form length is needed).
/// </summary>
internal sealed class TlvWriter
{
    private byte[] _buffer;
    private int _position;
    private long _maxBytes;

    public TlvWriter(int capacity, long maxBytes)
    {
        _buffer = ArrayPool<byte>.Shared.Rent(Math.Max(64, capacity));
        _maxBytes = maxBytes;
    }

    public int Position => _position;

    /// <summary>The size of the buffer in octets.</summary>
    public int Capacity => _buffer.Length;

    /// <summary>Starts another encoding in the same buffer.</summary>
    public void Reset(long maxBytes)
    {
        _position = 0;
        _maxBytes = maxBytes;
    }

    public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _position);

    public void Release()
    {
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = [];
    }

    private void Ensure(int additional)
    {
        var needed = (long)_position + additional;
        if (needed > _maxBytes)
        {
            throw new Asn1EncodeException($"Encoding exceeds the maximum size of {_maxBytes} bytes");
        }

        if (needed > _buffer.Length)
        {
            var size = Math.Max(needed, (long)_buffer.Length * 2);
            var next = ArrayPool<byte>.Shared.Rent((int)Math.Min(size, Array.MaxLength));
            _buffer.AsSpan(0, _position).CopyTo(next);
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = next;
        }
    }

    public void WriteByte(byte b)
    {
        Ensure(1);
        _buffer[_position++] = b;
    }

    public void WriteBytes(ReadOnlySpan<byte> bytes)
    {
        Ensure(bytes.Length);
        bytes.CopyTo(_buffer.AsSpan(_position));
        _position += bytes.Length;
    }

    public Span<byte> GetSpan(int length)
    {
        Ensure(length);
        var span = _buffer.AsSpan(_position, length);
        _position += length;
        return span;
    }

    /// <summary>Writes identifier octets (X.690 8.1.2).</summary>
    public void WriteIdentifier(Asn1Tag tag, bool constructed)
    {
        var first = (byte)(((int)tag.Class << 6) | (constructed ? 0x20 : 0));
        if (tag.Number < 31)
        {
            WriteByte((byte)(first | tag.Number));
            return;
        }

        WriteByte((byte)(first | 0x1F));
        Span<byte> tmp = stackalloc byte[5];
        var n = 0;
        var v = (uint)tag.Number;
        do
        {
            tmp[n++] = (byte)(v & 0x7F);
            v >>= 7;
        }
        while (v != 0);

        for (var i = n - 1; i >= 0; i--)
        {
            WriteByte((byte)(tmp[i] | (i > 0 ? 0x80 : 0)));
        }
    }

    /// <summary>Writes a definite length in minimal form (X.690 8.1.3, 10.1).</summary>
    public void WriteLength(int length)
    {
        if (length < 0x80)
        {
            WriteByte((byte)length);
            return;
        }

        var n = LengthOfLength(length);
        WriteByte((byte)(0x80 | n));
        for (var i = n - 1; i >= 0; i--)
        {
            WriteByte((byte)(length >> (8 * i)));
        }
    }

    private static int LengthOfLength(int length) => length <= 0xFF ? 1 : length <= 0xFFFF ? 2 : length <= 0xFFFFFF ? 3 : 4;

    /// <summary>Writes the identifier and reserves a length octet; returns the mark for <see cref="EndDefinite"/>.</summary>
    public int BeginDefinite(Asn1Tag tag)
    {
        WriteIdentifier(tag, constructed: true);
        WriteByte(0);
        return _position;
    }

    /// <summary>Back-patches the length reserved by <see cref="BeginDefinite"/>.</summary>
    public void EndDefinite(int mark)
    {
        var length = _position - mark;
        if (length < 0x80)
        {
            _buffer[mark - 1] = (byte)length;
            return;
        }

        var n = LengthOfLength(length);
        Ensure(n);
        _buffer.AsSpan(mark, length).CopyTo(_buffer.AsSpan(mark + n));
        _buffer[mark - 1] = (byte)(0x80 | n);
        for (var i = 0; i < n; i++)
        {
            _buffer[mark + i] = (byte)(length >> (8 * (n - 1 - i)));
        }

        _position += n;
    }

    /// <summary>Writes the identifier and the indefinite-length octet.</summary>
    public void BeginIndefinite(Asn1Tag tag)
    {
        WriteIdentifier(tag, constructed: true);
        WriteByte(0x80);
    }

    /// <summary>Writes end-of-contents octets.</summary>
    public void EndIndefinite()
    {
        WriteByte(0);
        WriteByte(0);
    }

    /// <summary>Truncates the buffer (used when encoding into scratch regions).</summary>
    public void Truncate(int position) => _position = position;
}
