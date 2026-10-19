using System.Numerics;

namespace Asn1.Runtime.Primitives;

/// <summary>OBJECT IDENTIFIER and RELATIVE-OID content octets (X.690 8.19 and 8.20), shared by BER/DER/CER/PER/OER.</summary>
public static class ObjectIdentifierCodec
{
    /// <summary>Encodes OBJECT IDENTIFIER content octets.</summary>
    public static byte[] Encode(ObjectIdentifier oid)
    {
        var output = new List<byte>(oid.Arcs.Length + 4);
        WriteSubidentifier(output, (oid.Arcs[0] * 40) + oid.Arcs[1]);
        for (var i = 2; i < oid.Arcs.Length; i++)
        {
            WriteSubidentifier(output, oid.Arcs[i]);
        }

        return [.. output];
    }

    /// <summary>Encodes RELATIVE-OID content octets.</summary>
    public static byte[] Encode(RelativeObjectIdentifier oid)
    {
        var output = new List<byte>(oid.Arcs.Length + 4);
        foreach (var arc in oid.Arcs)
        {
            WriteSubidentifier(output, arc);
        }

        return [.. output];
    }

    private static void WriteSubidentifier(List<byte> output, BigInteger value)
    {
        var groups = new Stack<byte>();
        do
        {
            groups.Push((byte)(int)(value & 0x7F));
            value >>= 7;
        }
        while (value > 0);

        while (groups.Count > 0)
        {
            var g = groups.Pop();
            output.Add(groups.Count > 0 ? (byte)(g | 0x80) : g);
        }
    }

    /// <summary>Decodes OBJECT IDENTIFIER content octets; returns null and an error message on malformed input.</summary>
    public static ObjectIdentifier? Decode(ReadOnlySpan<byte> content, out string? error, int maxArcs = 1024)
    {
        var subs = ReadSubidentifiers(content, out error, maxArcs);
        if (subs is null)
        {
            return null;
        }

        if (subs.Count == 0)
        {
            error = "an OBJECT IDENTIFIER must have at least one subidentifier";
            return null;
        }

        var first = subs[0];
        BigInteger arc0 = first < 40 ? 0 : first < 80 ? 1 : 2;
        var arcs = new List<BigInteger>(subs.Count + 1) { arc0, first - (arc0 * 40) };
        arcs.AddRange(subs.Skip(1));
        return new ObjectIdentifier(arcs);
    }

    /// <summary>Decodes RELATIVE-OID content octets.</summary>
    public static RelativeObjectIdentifier? DecodeRelative(ReadOnlySpan<byte> content, out string? error, int maxArcs = 1024)
    {
        var subs = ReadSubidentifiers(content, out error, maxArcs);
        if (subs is null)
        {
            return null;
        }

        if (subs.Count == 0)
        {
            error = "a RELATIVE-OID must have at least one subidentifier";
            return null;
        }

        return new RelativeObjectIdentifier(subs);
    }

    private static List<BigInteger>? ReadSubidentifiers(ReadOnlySpan<byte> content, out string? error, int maxArcs)
    {
        error = null;
        var result = new List<BigInteger>();
        var i = 0;
        while (i < content.Length)
        {
            if (content[i] == 0x80)
            {
                error = "subidentifier has a leading 0x80 octet (X.690 8.19.2)";
                return null;
            }

            BigInteger value = 0;
            var octets = 0;
            while (true)
            {
                if (i >= content.Length)
                {
                    error = "truncated subidentifier";
                    return null;
                }

                var b = content[i++];
                value = (value << 7) | (b & 0x7F);
                if (++octets > 1024)
                {
                    error = "subidentifier too long";
                    return null;
                }

                if ((b & 0x80) == 0)
                {
                    break;
                }
            }

            result.Add(value);
            if (result.Count > maxArcs)
            {
                error = $"more than {maxArcs} arcs";
                return null;
            }
        }

        return result;
    }
}
