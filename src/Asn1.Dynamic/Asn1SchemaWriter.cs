using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Asn1.Constraints;
using Asn1.Runtime;
using Asn1.Semantics;

namespace Asn1.Dynamic;

/// <summary>
/// Writes the <see cref="Asn1Schema"/> of a compilation: every type assignment of the selected modules and the types
/// they reference, with tags, constraints, components and DEFAULT values. A schema lets runtimes without the
/// compiler (such as the WebAssembly runtime of <c>@dotasn/wasm</c>) build the same descriptors as
/// <see cref="DynamicTypeFactory"/>.
/// </summary>
public static class Asn1SchemaWriter
{
    /// <summary>Writes the schema of the compilation (or of the named modules and what they reference) as compact JSON.</summary>
    public static string Write(Asn1Compilation compilation, IReadOnlySet<string>? modules = null, bool indented = false)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        var state = new State();
        var names = new List<(string Name, int Index)>();
        foreach (var module in compilation.Modules.Where(m => modules is null || modules.Contains(m.Name)))
        {
            foreach (var assignment in module.TypeAssignments)
            {
                if (assignment.Type is { } type)
                {
                    names.Add((assignment.QualifiedName, state.Index(type)));
                }
            }
        }

        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = indented, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteString("format", Asn1Schema.FormatName);
            w.WriteNumber("version", Asn1Schema.FormatVersion);
            w.WriteStartObject("names");
            foreach (var (name, index) in names)
            {
                w.WriteNumber(name, index);
            }

            w.WriteEndObject();
            w.WriteStartArray("types");

            // Writing a node may discover further types, so the list grows while it is written.
            for (var i = 0; i < state.Types.Count; i++)
            {
                WriteNode(w, state, state.Types[i]);
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private sealed class State
    {
        private readonly Dictionary<AsnType, int> _indices = new(ReferenceEqualityComparer.Instance);

        public List<AsnType> Types { get; } = [];

        public DynamicTypeFactory Factory { get; } = new();

        public int Index(AsnType type)
        {
            if (!_indices.TryGetValue(type, out var index))
            {
                index = Types.Count;
                Types.Add(type);
                _indices[type] = index;
            }

            return index;
        }
    }

    private static string NameOf(AsnType t) =>
        t.Assignment is { } a ? a.QualifiedName : t.Reference is { } r ? r.QualifiedName : t.Definition.Assignment?.QualifiedName ?? AsnType.KindName(t.Kind, t.StringKind);

    private static void WriteNode(Utf8JsonWriter w, State state, AsnType t)
    {
        var def = t.Definition;
        w.WriteStartObject();
        var kind = KindOf(t.Kind);

        // The same limits as DynamicTypeFactory: Asn1NamedNumber and enumeration items hold 64-bit values.
        var reason = kind is null ? $"{AsnType.KindName(t.Kind)} is not supported by the dynamic runtime"
            : t.Kind == TypeKind.Enumerated && def.EnumItems.Any(i => !Asn1NamedNumber.CanHold(i.Value)) ? "enumeration values must fit in 64 bits"
            : t.Kind == TypeKind.BitString && def.NamedNumbers.Any(n => !Asn1NamedNumber.CanHold(n.Value)) ? "named bit positions must fit in 64 bits"
            : null;
        w.WriteString("kind", reason is null ? kind : "unsupported");
        w.WriteString("name", NameOf(t));
        if (reason is not null)
        {
            w.WriteString("reason", reason);
            w.WriteEndObject();
            return;
        }

        if (!t.Tags.IsDefaultOrEmpty)
        {
            w.WriteStartArray("tags");
            foreach (var tag in t.Tags)
            {
                w.WriteStartArray();
                w.WriteNumberValue((int)tag.Tag.Class);
                w.WriteNumberValue(tag.Tag.Number);
                w.WriteBooleanValue(tag.IsExplicit);
                w.WriteEndArray();
            }

            w.WriteEndArray();
        }

        if (!t.Constraints.IsDefaultOrEmpty)
        {
            w.WriteStartArray("constraints");
            foreach (var c in t.Constraints)
            {
                WriteConstraint(w, c);
            }

            w.WriteEndArray();
        }

        switch (t.Kind)
        {
            case TypeKind.Integer or TypeKind.BitString when !def.NamedNumbers.IsDefaultOrEmpty:
                // INTEGER named numbers beyond 64 bits are left out of the metadata (Asn1NamedNumber.CanHold).
                w.WriteStartArray("named");
                foreach (var n in def.NamedNumbers.Where(n => Asn1NamedNumber.CanHold(n.Value)))
                {
                    w.WriteStartArray();
                    w.WriteStringValue(n.Name);
                    w.WriteNumberValue((long)n.Value);
                    w.WriteEndArray();
                }

                w.WriteEndArray();
                break;
            case TypeKind.Enumerated:
                w.WriteBoolean("ext", def.IsExtensible);
                w.WriteStartArray("items");
                foreach (var i in def.EnumItems)
                {
                    w.WriteStartArray();
                    w.WriteStringValue(i.Name);
                    w.WriteNumberValue((long)i.Value);
                    w.WriteBooleanValue(i.IsExtension);
                    w.WriteEndArray();
                }

                w.WriteEndArray();
                break;
            case TypeKind.CharacterString:
                w.WriteString("stringKind", Asn1Schema.StringKindName(t.StringKind));
                break;
            case TypeKind.UtcTime or TypeKind.GeneralizedTime or TypeKind.Date or TypeKind.TimeOfDay or TypeKind.DateTime or TypeKind.Duration or TypeKind.Time:
                w.WriteString("timeKind", Asn1Schema.TimeKindName(TimeKindOf(t.Kind)));
                break;
            case TypeKind.Sequence or TypeKind.Set:
                w.WriteBoolean("ext", def.IsExtensible);
                w.WriteStartArray("components");
                foreach (var c in def.Components)
                {
                    WriteComponent(w, state, c);
                }

                w.WriteEndArray();
                break;
            case TypeKind.Choice:
                w.WriteBoolean("ext", def.IsExtensible);
                w.WriteStartArray("alternatives");
                foreach (var a in def.Components)
                {
                    w.WriteStartObject();
                    w.WriteString("name", a.Name);
                    w.WriteNumber("type", state.Index(a.Type));
                    WriteAdditionFlags(w, a);
                    w.WriteEndObject();
                }

                w.WriteEndArray();
                break;
            case TypeKind.SequenceOf or TypeKind.SetOf:
                w.WriteNumber("element", state.Index(def.ElementType!));
                if (def.ElementName is { } elementName)
                {
                    w.WriteString("elementName", elementName);
                }

                break;
        }

        w.WriteEndObject();
    }

    private static void WriteComponent(Utf8JsonWriter w, State state, AsnComponent c)
    {
        w.WriteStartObject();
        w.WriteString("name", c.Name);
        w.WriteNumber("type", state.Index(c.Type));
        if (c.Presence != Presence.Required)
        {
            w.WriteString("presence", c.Presence == Presence.Default ? "default" : "optional");
        }

        if (c.Presence == Presence.Default && c.DefaultValue is { } dv and not ErrorValue)
        {
            // The DEFAULT value in the JSON value notation of the component's type.
            try
            {
                var descriptor = state.Factory.Create(c.Type);
                var value = DynamicValueConverter.FromSemanticValue(dv, c.Type);
                if (value is not null)
                {
                    w.WritePropertyName("default");
                    DynamicJson.Write(w, descriptor, value);
                }
            }
            catch (Asn1UnsupportedException)
            {
                // A default of a type that the dynamic runtime cannot represent: the component is written without it.
            }
        }

        WriteAdditionFlags(w, c);
        if (c.IsInRootPart2)
        {
            w.WriteBoolean("part2", true);
        }

        w.WriteEndObject();
    }

    private static void WriteAdditionFlags(Utf8JsonWriter w, AsnComponent c)
    {
        if (c.ExtensionAdditionIndex >= 0)
        {
            w.WriteNumber("addition", c.ExtensionAdditionIndex);
        }

        if (c.IsInExtensionGroup)
        {
            w.WriteBoolean("group", true);
        }
    }

    // ------------------------------------------------------------------ constraints

    private static void WriteConstraint(Utf8JsonWriter w, Asn1Constraint c)
    {
        w.WriteStartObject();
        if (c.Root is { } root)
        {
            w.WritePropertyName("root");
            WriteElement(w, root);
        }

        if (c.IsExtensible)
        {
            w.WriteBoolean("ext", true);
        }

        if (c.Additions is { } additions)
        {
            w.WritePropertyName("add");
            WriteElement(w, additions);
        }

        w.WriteEndObject();
    }

    private static void WriteElement(Utf8JsonWriter w, ConstraintElement e)
    {
        w.WriteStartArray();
        switch (e)
        {
            case AllValuesElement:
                w.WriteStringValue("all");
                break;
            case SingleValueElement s:
                w.WriteStringValue("single");
                WriteValue(w, s.Value);
                break;
            case ValueRangeElement r:
                w.WriteStringValue("range");
                WriteValue(w, r.Lower);
                WriteValue(w, r.Upper);
                w.WriteBooleanValue(r.LowerOpen);
                w.WriteBooleanValue(r.UpperOpen);
                break;
            case SizeElement s:
                w.WriteStringValue("size");
                WriteConstraint(w, s.Constraint);
                break;
            case PermittedAlphabetElement a:
                w.WriteStringValue("from");
                WriteConstraint(w, a.Constraint);
                break;
            case PatternElement p:
                w.WriteStringValue("pattern");
                w.WriteStringValue(p.Pattern);
                break;
            case InnerTypeElement i:
                w.WriteStringValue("withComponent");
                WriteConstraint(w, i.Constraint);
                break;
            case InnerComponentsElement ic:
                w.WriteStringValue("withComponents");
                w.WriteBooleanValue(ic.IsPartial);
                w.WriteStartArray();
                foreach (var cc in ic.Components)
                {
                    w.WriteStartArray();
                    w.WriteStringValue(cc.Name);
                    if (cc.Value is { } value)
                    {
                        WriteConstraint(w, value);
                    }
                    else
                    {
                        w.WriteNullValue();
                    }

                    w.WriteStringValue(cc.Presence switch
                    {
                        ComponentPresenceConstraint.Present => "present",
                        ComponentPresenceConstraint.Absent => "absent",
                        ComponentPresenceConstraint.Optional => "optional",
                        _ => "none",
                    });
                    w.WriteEndArray();
                }

                w.WriteEndArray();
                break;
            case UnionElement u:
                w.WriteStringValue("union");
                WriteElements(w, u.Items);
                break;
            case IntersectionElement x:
                w.WriteStringValue("intersection");
                WriteElements(w, x.Items);
                break;
            case ExceptElement x:
                w.WriteStringValue("except");
                WriteElement(w, x.Included);
                WriteElement(w, x.Excluded);
                break;
            case NestedElement n:
                w.WriteStringValue("nested");
                WriteConstraint(w, n.Inner);
                w.WriteBooleanValue(n.IsContainedSubtype);
                break;
            case OpaqueElement o:
                w.WriteStringValue("opaque");
                w.WriteStringValue(o.Kind);
                w.WriteStringValue(o.Notation);
                break;
            default:
                // An element this format does not know is kept as an opaque (non-evaluated) constraint.
                w.WriteStringValue("opaque");
                w.WriteStringValue("unknown");
                w.WriteStringValue(e.ToString());
                break;
        }

        w.WriteEndArray();
    }

    private static void WriteElements(Utf8JsonWriter w, IEnumerable<ConstraintElement> items)
    {
        w.WriteStartArray();
        foreach (var item in items)
        {
            WriteElement(w, item);
        }

        w.WriteEndArray();
    }

    private static void WriteValue(Utf8JsonWriter w, object? value)
    {
        if (value is null)
        {
            w.WriteNullValue();
            return;
        }

        w.WriteStartObject();
        switch (value)
        {
            case BigInteger i:
                w.WriteString("int", i.ToString(CultureInfo.InvariantCulture));
                break;
            case bool b:
                w.WriteBoolean("bool", b);
                break;
            case double d when double.IsNaN(d):
                w.WriteString("real", "NaN");
                break;
            case double d when double.IsInfinity(d):
                w.WriteString("real", d > 0 ? "INF" : "-INF");
                break;
            case double d when d == 0 && double.IsNegative(d):
                w.WriteString("real", "-0");
                break;
            case double d:
                w.WriteNumber("real", d);
                break;
            case Asn1Null:
                w.WriteNull("null");
                break;
            case string s:
                w.WriteString("str", s);
                break;
            case byte[] bytes:
                w.WriteString("bytes", Convert.ToHexString(bytes));
                break;
            case Asn1BitString bits:
                w.WriteStartArray("bits");
                w.WriteStringValue(Convert.ToHexString(bits.Bytes));
                w.WriteNumberValue(bits.Length);
                w.WriteEndArray();
                break;
            case ObjectIdentifier oid:
                w.WriteString("oid", oid.ToString());
                break;
            case RelativeObjectIdentifier rel:
                w.WriteString("relOid", rel.ToString());
                break;
            default:
                throw new InvalidOperationException($"constraint value of type {value.GetType().Name} cannot be written to a schema");
        }

        w.WriteEndObject();
    }

    private static string? KindOf(TypeKind kind) => kind switch
    {
        TypeKind.Boolean => "boolean",
        TypeKind.Integer => "integer",
        TypeKind.Enumerated => "enumerated",
        TypeKind.Real => "real",
        TypeKind.BitString => "bitString",
        TypeKind.OctetString => "octetString",
        TypeKind.Null => "null",
        TypeKind.ObjectIdentifier => "objectIdentifier",
        TypeKind.RelativeOid => "relativeOid",
        TypeKind.CharacterString => "string",
        TypeKind.UtcTime or TypeKind.GeneralizedTime or TypeKind.Date or TypeKind.TimeOfDay or TypeKind.DateTime or TypeKind.Duration or TypeKind.Time => "time",
        TypeKind.Sequence => "sequence",
        TypeKind.Set => "set",
        TypeKind.Choice => "choice",
        TypeKind.SequenceOf => "sequenceOf",
        TypeKind.SetOf => "setOf",
        TypeKind.OpenType => "open",
        _ => null,
    };

    private static TimeKind TimeKindOf(TypeKind k) => k switch
    {
        TypeKind.UtcTime => TimeKind.UtcTime,
        TypeKind.GeneralizedTime => TimeKind.GeneralizedTime,
        TypeKind.Date => TimeKind.Date,
        TypeKind.TimeOfDay => TimeKind.TimeOfDay,
        TypeKind.DateTime => TimeKind.DateTime,
        TypeKind.Duration => TimeKind.Duration,
        _ => TimeKind.Time,
    };
}
