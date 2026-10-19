using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Asn1.Dynamic;
using Asn1.Runtime;

namespace Asn1.Wasm;

/// <summary>
/// The WebAssembly exports of the DotAsn runtime. Strings are passed as UTF-8 (pointer, length) pairs in memory
/// obtained from <see cref="Alloc"/>. Every call returns 0 on success and 1 on failure; the result (encoded bytes,
/// JSON text, or a JSON error object) stays available through <see cref="ResultPointer"/>/<see cref="ResultLength"/>
/// until the next call. The module is single-threaded, like the JavaScript that calls it.
/// </summary>
public static unsafe class Exports
{
    private static readonly List<Asn1Schema?> s_schemas = [null];
    private static byte* s_result;
    private static int s_resultLength;

    /// <summary>Allocates <paramref name="size"/> bytes of module memory (for arguments).</summary>
    [UnmanagedCallersOnly(EntryPoint = "dotasn_alloc")]
    public static byte* Alloc(int size) => (byte*)NativeMemory.Alloc((nuint)Math.Max(size, 1));

    /// <summary>Frees memory obtained from <see cref="Alloc"/>.</summary>
    [UnmanagedCallersOnly(EntryPoint = "dotasn_free")]
    public static void Free(byte* pointer) => NativeMemory.Free(pointer);

    /// <summary>The result of the last call.</summary>
    [UnmanagedCallersOnly(EntryPoint = "dotasn_result_ptr")]
    public static byte* ResultPointer() => s_result;

    /// <summary>The length of the result of the last call.</summary>
    [UnmanagedCallersOnly(EntryPoint = "dotasn_result_len")]
    public static int ResultLength() => s_resultLength;

    /// <summary>Loads a DotAsn schema (JSON). The result is the schema handle as text, e.g. "1".</summary>
    [UnmanagedCallersOnly(EntryPoint = "dotasn_load_schema")]
    public static int LoadSchema(byte* json, int length) => Run(() =>
    {
        s_schemas.Add(Asn1Schema.Parse(new ReadOnlySpan<byte>(json, length)));
        return Encoding.UTF8.GetBytes((s_schemas.Count - 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
    });

    /// <summary>Releases a schema handle.</summary>
    [UnmanagedCallersOnly(EntryPoint = "dotasn_unload_schema")]
    public static void UnloadSchema(int handle)
    {
        if (handle > 0 && handle < s_schemas.Count)
        {
            s_schemas[handle] = null;
        }
    }

    /// <summary>The qualified type names of a schema as a JSON array.</summary>
    [UnmanagedCallersOnly(EntryPoint = "dotasn_type_names")]
    public static int TypeNames(int handle) => Run(() =>
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartArray();
            foreach (var name in Schema(handle).TypeNames.Order(StringComparer.Ordinal))
            {
                w.WriteStringValue(name);
            }

            w.WriteEndArray();
        }

        return ms.ToArray();
    });

    /// <summary>
    /// Encodes a value given in the JSON value notation. <paramref name="rule"/> is BER, CER, DER, APER, UPER, OER,
    /// COER, JER, XER or CXER. The result is the encoding.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "dotasn_encode")]
    public static int Encode(int handle, byte* type, int typeLength, byte* rule, int ruleLength, byte* json, int jsonLength) => Run(() =>
    {
        var encodingRule = Rule(Text(rule, ruleLength));
        var descriptor = Schema(handle).GetType(Text(type, typeLength));
        return encodingRule.Encode(descriptor, DynamicJson.Parse(descriptor, Text(json, jsonLength)));
    });

    /// <summary>
    /// Decodes an encoding. <paramref name="options"/> is an optional JSON object with rejectTrailingData, maxDepth,
    /// maxCollectionCount, maxStringLength and validateConstraints. The result is the value in the JSON value notation.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "dotasn_decode")]
    public static int Decode(int handle, byte* type, int typeLength, byte* rule, int ruleLength, byte* data, int dataLength, byte* options, int optionsLength) => Run(() =>
    {
        var encodingRule = Rule(Text(rule, ruleLength));
        var descriptor = Schema(handle).GetType(Text(type, typeLength));
        var bytes = new ReadOnlySpan<byte>(data, dataLength).ToArray();
        var value = encodingRule.Decode(descriptor, bytes, DecodingOptions(options, optionsLength));
        return Encoding.UTF8.GetBytes(DynamicJson.Serialize(descriptor, value, indented: false));
    });

    /// <summary>Validates a value given in the JSON value notation. The result is a JSON array of {path, message}.</summary>
    [UnmanagedCallersOnly(EntryPoint = "dotasn_validate")]
    public static int Validate(int handle, byte* type, int typeLength, byte* json, int jsonLength) => Run(() =>
    {
        var descriptor = Schema(handle).GetType(Text(type, typeLength));
        var errors = Asn1Validator.ValidateValue(descriptor, DynamicJson.Parse(descriptor, Text(json, jsonLength)));
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartArray();
            foreach (var e in errors)
            {
                w.WriteStartObject();
                w.WriteString("path", e.Path);
                w.WriteString("message", e.Message);
                w.WriteEndObject();
            }

            w.WriteEndArray();
        }

        return ms.ToArray();
    });

    // ------------------------------------------------------------------ helpers

    private static Asn1Schema Schema(int handle) =>
        handle > 0 && handle < s_schemas.Count && s_schemas[handle] is { } schema ? schema : throw new ArgumentException($"invalid schema handle {handle}");

    private static Asn1EncodingRule Rule(string name) =>
        Asn1EncodingRules.All.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException($"unknown encoding rule '{name}' (expected one of {string.Join(", ", Asn1EncodingRules.All.Select(r => r.Name))})");

    private static string Text(byte* p, int length) => Encoding.UTF8.GetString(p, length);

    private static Asn1DecodingOptions DecodingOptions(byte* p, int length)
    {
        if (length == 0)
        {
            return Asn1DecodingOptions.Default;
        }

        using var doc = JsonDocument.Parse(new ReadOnlyMemory<byte>(new ReadOnlySpan<byte>(p, length).ToArray()));
        var o = doc.RootElement;
        var options = Asn1DecodingOptions.Default;
        if (o.TryGetProperty("rejectTrailingData", out var trailing))
        {
            options = options with { RejectTrailingData = trailing.GetBoolean() };
        }

        if (o.TryGetProperty("validateConstraints", out var validate))
        {
            options = options with { ValidateConstraints = validate.GetBoolean() };
        }

        if (o.TryGetProperty("maxDepth", out var depth))
        {
            options = options with { MaxDepth = depth.GetInt32() };
        }

        if (o.TryGetProperty("maxCollectionCount", out var count))
        {
            options = options with { MaxCollectionCount = count.GetInt32() };
        }

        if (o.TryGetProperty("maxStringLength", out var strings))
        {
            options = options with { MaxStringLength = strings.GetInt32() };
        }

        return options;
    }

    /// <summary>Runs a call, storing its result or a JSON error object; exceptions never cross the boundary.</summary>
    private static int Run(Func<byte[]> call)
    {
        byte[] result;
        int status;
        try
        {
            result = call();
            status = 0;
        }
        catch (Exception e)
        {
            result = Error(e);
            status = 1;
        }

        NativeMemory.Free(s_result);
        s_result = (byte*)NativeMemory.Alloc((nuint)Math.Max(result.Length, 1));
        result.CopyTo(new Span<byte>(s_result, result.Length));
        s_resultLength = result.Length;
        return status;
    }

    /// <summary>{"type", "message", "kind"?, "path"?, "bitOffset"?}: the error categories of the JavaScript API.</summary>
    private static byte[] Error(Exception e)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            switch (e)
            {
                case Asn1DecodeException d:
                    w.WriteString("type", "decode");
                    w.WriteString("message", d.Reason);
                    w.WriteString("kind", d.Error.ToString());
                    if (d.BitOffset >= 0)
                    {
                        w.WriteNumber("bitOffset", d.BitOffset);
                    }

                    break;
                case Asn1EncodeException x:
                    w.WriteString("type", "encode");
                    w.WriteString("message", x.Reason);
                    break;
                case Asn1ConstraintViolationException c:
                    w.WriteString("type", "constraint");
                    w.WriteString("message", c.Message);
                    break;
                case Asn1UnsupportedException:
                    w.WriteString("type", "unsupported");
                    w.WriteString("message", e.Message);
                    break;
                case Asn1JsonException j:
                    w.WriteString("type", "notation");
                    w.WriteString("message", e.Message);
                    w.WriteString("path", j.JsonPath);
                    break;
                case Asn1SchemaException:
                    w.WriteString("type", "schema");
                    w.WriteString("message", e.Message);
                    break;
                case Asn1Exception a:
                    w.WriteString("type", "asn1");
                    w.WriteString("message", a.Message);
                    break;
                case KeyNotFoundException or ArgumentException:
                    w.WriteString("type", "argument");
                    w.WriteString("message", e.Message);
                    break;
                default:
                    w.WriteString("type", "internal");
                    w.WriteString("message", $"{e.GetType().Name}: {e.Message}");
                    break;
            }

            if (e is Asn1Exception { Path: { Length: > 0 } path } && e is not Asn1JsonException)
            {
                w.WriteString("path", path);
            }

            w.WriteEndObject();
        }

        return ms.ToArray();
    }
}
