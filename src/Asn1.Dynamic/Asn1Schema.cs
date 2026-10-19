using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using Asn1.Constraints;
using Asn1.Runtime;

namespace Asn1.Dynamic;

/// <summary>
/// A compiled description of the types of an ASN.1 compilation (a "DotAsn schema", JSON written by
/// <see cref="Asn1SchemaWriter"/> and <c>asn1 generate-schema</c>). Loading a schema yields the same descriptors as
/// <see cref="DynamicTypeFactory"/> without the compiler: tags, the complete constraint model, components, DEFAULT
/// values and extensibility. Values use the dynamic model and the JSON value notation (<see cref="DynamicJson"/>).
/// </summary>
/// <remarks>The format is documented in docs/schema-format.md. Loading is thread-safe.</remarks>
public sealed class Asn1Schema
{
    /// <summary>The value of the "format" member.</summary>
    public const string FormatName = "dotasn-schema";

    /// <summary>The schema format version this implementation reads and writes.</summary>
    public const int FormatVersion = 1;

    private readonly JsonElement[] _nodes;
    private readonly Asn1Type<object?>?[] _types;
    private readonly Dictionary<string, int> _names;
    private readonly Lock _lock = new();

    private Asn1Schema(JsonElement[] nodes, Dictionary<string, int> names)
    {
        _nodes = nodes;
        _types = new Asn1Type<object?>?[nodes.Length];
        _names = names;
    }

    /// <summary>The qualified names (Module.Type) of the type assignments in the schema.</summary>
    public IReadOnlyCollection<string> TypeNames => _names.Keys;

    /// <summary>Parses a schema from JSON text.</summary>
    /// <exception cref="Asn1SchemaException">The text is not a valid schema of a supported version.</exception>
    public static Asn1Schema Parse(string json) => Parse(System.Text.Encoding.UTF8.GetBytes(json));

    /// <summary>Parses a schema from UTF-8 JSON.</summary>
    /// <exception cref="Asn1SchemaException">The text is not a valid schema of a supported version.</exception>
    public static Asn1Schema Parse(ReadOnlySpan<byte> utf8)
    {
        JsonElement root;
        try
        {
            var reader = new Utf8JsonReader(utf8, new JsonReaderOptions { MaxDepth = 1024 });
            using var doc = JsonDocument.ParseValue(ref reader);
            root = doc.RootElement.Clone();
        }
        catch (JsonException e)
        {
            throw new Asn1SchemaException($"invalid schema JSON: {e.Message}");
        }

        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("format", out var format) || format.ValueKind != JsonValueKind.String || format.GetString() != FormatName)
        {
            throw new Asn1SchemaException($"not a DotAsn schema (expected \"format\": \"{FormatName}\")");
        }

        if (!root.TryGetProperty("version", out var version) || !version.TryGetInt32(out var v) || v != FormatVersion)
        {
            throw new Asn1SchemaException($"unsupported schema version (this runtime reads version {FormatVersion}); regenerate the schema with the matching DotAsn version");
        }

        if (!root.TryGetProperty("types", out var types) || types.ValueKind != JsonValueKind.Array ||
            !root.TryGetProperty("names", out var names) || names.ValueKind != JsonValueKind.Object)
        {
            throw new Asn1SchemaException("a schema needs \"types\" (array) and \"names\" (object)");
        }

        var nodes = types.EnumerateArray().ToArray();
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var n in names.EnumerateObject())
        {
            if (!n.Value.TryGetInt32(out var index) || index < 0 || index >= nodes.Length)
            {
                throw new Asn1SchemaException($"type '{n.Name}' refers to a missing node");
            }

            map[n.Name] = index;
        }

        return new Asn1Schema(nodes, map);
    }

    /// <summary>Returns the descriptor of a type assignment (qualified "Module.Type", or "Type" if unambiguous).</summary>
    /// <exception cref="KeyNotFoundException">No such type.</exception>
    /// <exception cref="Asn1UnsupportedException">The type cannot be encoded by the dynamic runtime.</exception>
    public Asn1Type<object?> GetType(string name) =>
        TryGetType(name, out var type) ? type : throw new KeyNotFoundException($"the schema has no type '{name}'");

    /// <summary>Looks up the descriptor of a type assignment (qualified "Module.Type", or "Type" if unambiguous).</summary>
    public bool TryGetType(string name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Asn1Type<object?>? type)
    {
        if (!_names.TryGetValue(name, out var index))
        {
            var matches = _names.Where(n => n.Key.EndsWith("." + name, StringComparison.Ordinal)).Take(2).ToList();
            if (matches.Count != 1)
            {
                type = null;
                return false;
            }

            index = matches[0].Value;
        }

        type = Node(index);
        return true;
    }

    private Asn1Type<object?> Node(int index)
    {
        if (index < 0 || index >= _nodes.Length)
        {
            throw new Asn1SchemaException($"reference to missing node {index}");
        }

        lock (_lock)
        {
            return _types[index] ??= Build(_nodes[index]);
        }
    }

    // ------------------------------------------------------------------ descriptors

    private Asn1Type<object?> Build(JsonElement n)
    {
        var kind = Str(n, "kind");
        var name = Str(n, "name");
        var tags = n.TryGetProperty("tags", out var t) ? [.. t.EnumerateArray().Select(Tag)] : ImmutableArray<Asn1TagStep>.Empty;
        var constraints = n.TryGetProperty("constraints", out var c) ? [.. c.EnumerateArray().Select(Constraint)] : ImmutableArray<Asn1Constraint>.Empty;
        switch (kind)
        {
            case "boolean":
                return DynamicTypeFactory.Box(new BooleanType(name, tags, constraints));
            case "integer":
                return DynamicTypeFactory.Box(new IntegerType<BigInteger>(name, tags, constraints, NamedNumbers(n)));
            case "enumerated":
                {
                    var items = n.GetProperty("items").EnumerateArray().Select(i => (i[0].GetString()!, i[1].GetInt64(), i[2].GetBoolean())).ToList();
                    var info = new EnumerationInfo(items, Bool(n, "ext"));
                    var byValue = items.ToDictionary(i => i.Item2, i => (object?)i.Item1);
                    return DynamicTypeFactory.Box(new EnumeratedType<object?>(
                        name,
                        info,
                        v => v is string s && info.FindByName(s) is { } item ? item.Value : long.MinValue,
                        v => byValue[v],
                        item => "#unknown-extension-" + item.Index,
                        tags,
                        constraints));
                }

            case "real":
                return DynamicTypeFactory.Box(new RealType(name, tags, constraints));
            case "bitString":
                return DynamicTypeFactory.Box(new BitStringType(name, tags, constraints, NamedNumbers(n)));
            case "octetString":
                return DynamicTypeFactory.Box(new OctetStringType(name, tags, constraints));
            case "null":
                return DynamicTypeFactory.Box(new NullType(name, tags, constraints));
            case "objectIdentifier":
                return DynamicTypeFactory.Box(new ObjectIdentifierType(name, tags, constraints));
            case "relativeOid":
                return DynamicTypeFactory.Box(new RelativeOidType(name, tags, constraints));
            case "string":
                return DynamicTypeFactory.Box(new CharacterStringType(StringKind(Str(n, "stringKind")), name, tags, constraints));
            case "time":
                return DynamicTypeFactory.Box(new TimeType(TimeKindOf(Str(n, "timeKind")), name, tags, constraints));
            case "sequence":
            case "set":
                {
                    var components = n.GetProperty("components").EnumerateArray().ToArray();
                    return DynamicTypeFactory.Box(new SequenceType<Asn1Record>(
                        name,
                        kind == "set",
                        () => new Asn1Record(),
                        () => components.Select(Component),
                        Bool(n, "ext"),
                        tags,
                        constraints));
                }

            case "choice":
                {
                    var alternatives = n.GetProperty("alternatives").EnumerateArray().ToArray();
                    var indexByName = alternatives.Select((a, i) => (Name: Str(a, "name"), i)).ToDictionary(x => x.Name, x => x.i, StringComparer.Ordinal);
                    return DynamicTypeFactory.Box(new ChoiceType<Asn1Choice>(
                        name,
                        () => alternatives.Select(a =>
                        {
                            var altName = Str(a, "name");
                            var typeIndex = a.GetProperty("type").GetInt32();
                            return (ChoiceAlternative<Asn1Choice>)new ChoiceAlternative<Asn1Choice, object?>(
                                altName,
                                () => Node(typeIndex),
                                ch => ch.Value,
                                v => new Asn1Choice(altName, v),
                                Int(a, "addition", -1),
                                Bool(a, "group"));
                        }),
                        ch => ch.Alternative is not null && indexByName.TryGetValue(ch.Alternative, out var i) ? i : -1,
                        Bool(n, "ext"),
                        (index, raw) => new Asn1Choice(null, raw, index),
                        ch => ch.Alternative is null && ch.Value is Asn1OpenValue raw ? (ch.UnknownIndex, raw) : null,
                        tags,
                        constraints));
                }

            case "sequenceOf":
            case "setOf":
                {
                    var element = n.GetProperty("element").GetInt32();
                    var elementName = n.TryGetProperty("elementName", out var en) ? en.GetString() : null;
                    return DynamicTypeFactory.Box(new CollectionType<object?>(name, kind == "setOf", () => Node(element), elementName, tags, constraints));
                }

            case "open":
                return DynamicTypeFactory.Box(new OpenType(name, tags, constraints));
            case "unsupported":
                throw new Asn1UnsupportedException($"'{name}' cannot be used: {(n.TryGetProperty("reason", out var r) ? r.GetString() : "unsupported")}");
            default:
                throw new Asn1SchemaException($"unknown type kind '{kind}' of '{name}'");
        }
    }

    private SequenceComponent<Asn1Record> Component(JsonElement c)
    {
        var name = Str(c, "name");
        var typeIndex = c.GetProperty("type").GetInt32();
        var presence = (c.TryGetProperty("presence", out var p) ? p.GetString() : null) switch
        {
            "optional" => Asn1Presence.Optional,
            "default" => Asn1Presence.Default,
            _ => Asn1Presence.Required,
        };
        var defaultValue = presence == Asn1Presence.Default && c.TryGetProperty("default", out var d)
            ? DynamicJson.Read(Node(typeIndex), d, "default of " + name)
            : null;
        return new SequenceComponent<Asn1Record, object?>(
            name,
            () => Node(typeIndex),
            r => r.TryGetValue(name, out var v) ? v : defaultValue,
            (r, v) => r[name] = v,
            presence,
            isPresent: r => r.Contains(name) || (presence == Asn1Presence.Default && defaultValue is not null),
            isDefault: presence == Asn1Presence.Default && defaultValue is not null ? r => !r.TryGetValue(name, out var v) || DynamicEquality.ValuesEqual(v, defaultValue) : null,
            applyDefault: defaultValue is null ? null : r => r[name] = defaultValue,
            Int(c, "addition", -1),
            Bool(c, "part2"),
            Bool(c, "group"));
    }

    private static ImmutableArray<Asn1NamedNumber> NamedNumbers(JsonElement n) =>
        n.TryGetProperty("named", out var named) ? [.. named.EnumerateArray().Select(x => new Asn1NamedNumber(x[0].GetString()!, x[1].GetInt64()))] : [];

    private static Asn1TagStep Tag(JsonElement t) => new(new Asn1Tag((TagClass)t[0].GetInt32(), t[1].GetInt32()), t[2].GetBoolean());

    // ------------------------------------------------------------------ constraints

    internal static Asn1Constraint Constraint(JsonElement c) =>
        new(c.TryGetProperty("root", out var root) ? Element(root) : null, Bool(c, "ext"), c.TryGetProperty("add", out var add) ? Element(add) : null);

    private static ConstraintElement Element(JsonElement e)
    {
        var op = e[0].GetString();
        return op switch
        {
            "all" => AllValuesElement.Instance,
            "single" => new SingleValueElement(Value(e[1])!),
            "range" => new ValueRangeElement(Value(e[1]), Value(e[2]), e[3].GetBoolean(), e[4].GetBoolean()),
            "size" => new SizeElement(Constraint(e[1])),
            "from" => new PermittedAlphabetElement(Constraint(e[1])),
            "pattern" => new PatternElement(e[1].GetString()!),
            "withComponent" => new InnerTypeElement(Constraint(e[1])),
            "withComponents" => new InnerComponentsElement(e[1].GetBoolean(), [.. e[2].EnumerateArray().Select(x => new ComponentConstraint(
                x[0].GetString()!,
                x[1].ValueKind == JsonValueKind.Null ? null : Constraint(x[1]),
                x[2].GetString() switch
                {
                    "present" => ComponentPresenceConstraint.Present,
                    "absent" => ComponentPresenceConstraint.Absent,
                    "optional" => ComponentPresenceConstraint.Optional,
                    _ => ComponentPresenceConstraint.None,
                }))]),
            "union" => new UnionElement([.. e[1].EnumerateArray().Select(Element)]),
            "intersection" => new IntersectionElement([.. e[1].EnumerateArray().Select(Element)]),
            "except" => new ExceptElement(Element(e[1]), Element(e[2])),
            "nested" => new NestedElement(Constraint(e[1]), e[2].GetBoolean()),
            "opaque" => new OpaqueElement(e[1].GetString()!, e[2].GetString()!),
            _ => throw new Asn1SchemaException($"unknown constraint element '{op}'"),
        };
    }

    /// <summary>A constraint value: <c>null</c> (MIN/MAX) or a single-member object naming its representation.</summary>
    private static object? Value(JsonElement v)
    {
        if (v.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        var p = v.EnumerateObject().First();
        var x = p.Value;
        return p.Name switch
        {
            "int" => BigInteger.Parse(x.GetString()!, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture),
            "bool" => x.GetBoolean(),
            "real" => x.ValueKind == JsonValueKind.Number ? x.GetDouble() : x.GetString() switch
            {
                "INF" => double.PositiveInfinity,
                "-INF" => double.NegativeInfinity,
                "-0" => -0.0,
                _ => double.NaN,
            },
            "null" => Asn1Null.Value,
            "str" => x.GetString()!,
            "bytes" => Convert.FromHexString(x.GetString()!),
            "bits" => new Asn1BitString(Convert.FromHexString(x[0].GetString()!), x[1].GetInt64()),
            "oid" => ObjectIdentifier.Parse(x.GetString()!),
            "relOid" => RelativeObjectIdentifier.Parse(x.GetString()!),
            _ => throw new Asn1SchemaException($"unknown constraint value representation '{p.Name}'"),
        };
    }

    // ------------------------------------------------------------------ helpers

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : throw new Asn1SchemaException($"missing \"{name}\"");

    private static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static int Int(JsonElement e, string name, int fallback) => e.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : fallback;

    internal static string StringKindName(CharacterStringKind kind) => kind switch
    {
        CharacterStringKind.Utf8 => "utf8",
        CharacterStringKind.Numeric => "numeric",
        CharacterStringKind.Printable => "printable",
        CharacterStringKind.Teletex => "teletex",
        CharacterStringKind.Videotex => "videotex",
        CharacterStringKind.IA5 => "ia5",
        CharacterStringKind.Graphic => "graphic",
        CharacterStringKind.Visible => "visible",
        CharacterStringKind.General => "general",
        CharacterStringKind.Universal => "universal",
        CharacterStringKind.Bmp => "bmp",
        CharacterStringKind.ObjectDescriptor => "objectDescriptor",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static CharacterStringKind StringKind(string name) =>
        Enum.GetValues<CharacterStringKind>().FirstOrDefault(k => StringKindName(k) == name, (CharacterStringKind)(-1)) is var k && (int)k >= 0
            ? k
            : throw new Asn1SchemaException($"unknown string kind '{name}'");

    internal static string TimeKindName(TimeKind kind) => kind switch
    {
        TimeKind.UtcTime => "utcTime",
        TimeKind.GeneralizedTime => "generalizedTime",
        TimeKind.Date => "date",
        TimeKind.TimeOfDay => "timeOfDay",
        TimeKind.DateTime => "dateTime",
        TimeKind.Duration => "duration",
        _ => "time",
    };

    private static TimeKind TimeKindOf(string name) => name switch
    {
        "utcTime" => TimeKind.UtcTime,
        "generalizedTime" => TimeKind.GeneralizedTime,
        "date" => TimeKind.Date,
        "timeOfDay" => TimeKind.TimeOfDay,
        "dateTime" => TimeKind.DateTime,
        "duration" => TimeKind.Duration,
        "time" => TimeKind.Time,
        _ => throw new Asn1SchemaException($"unknown time kind '{name}'"),
    };
}

/// <summary>A DotAsn schema is malformed or of an unsupported version.</summary>
public sealed class Asn1SchemaException(string message) : Exception(message);
