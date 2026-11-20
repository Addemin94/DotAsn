using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using System.Text;
using Asn1.Diagnostics;
using Asn1.Runtime;
using Asn1.Semantics;

namespace Asn1.CodeGen.Pawn;

/// <summary>Options for <see cref="PawnGenerator"/>.</summary>
public sealed record PawnGeneratorOptions
{
    /// <summary>Restrict generation to these modules (null: all).</summary>
    public IReadOnlySet<string>? Modules { get; init; }

    /// <summary>Also emit <see cref="PawnGenerator.ManifestFileName"/>, a JSON description of every value layout.</summary>
    public bool GenerateLayoutManifest { get; init; } = true;
}

/// <summary>A generated file.</summary>
/// <param name="Path">Relative output path.</param>
/// <param name="Content">File content.</param>
public sealed record PawnFile(string Path, string Content);

/// <summary>The result of Pawn generation.</summary>
/// <param name="Files">The OER core, one include file per ASN.1 module and the optional layout manifest.</param>
/// <param name="Diagnostics">Renames and unsupported constructs.</param>
public sealed record PawnGenerationResult(ImmutableArray<PawnFile> Files, ImmutableArray<Diagnostic> Diagnostics);

/// <summary>
/// Generates Pawn for scripts on small devices: for every supported type a value layout in a cell array, described by
/// constants, and a canonical OER (COER) encoder and decoder over a packed byte array.
/// </summary>
/// <remarks>
/// Pawn has no structures, so a value lives in a cell array at named cell offsets. Optional and DEFAULT components have
/// a presence cell, strings and SEQUENCE OF a length or count cell, CHOICE an index cell. Strings are stored as packed
/// bytes, four to a cell with the first in the most significant byte. The supported subset is what fits 32-bit cells
/// and a fixed layout: bounded strings and SEQUENCE OF, integers within 32 bits, root components and alternatives.
/// </remarks>
public sealed class PawnGenerator
{
    /// <summary>The generator version recorded in generated files.</summary>
    public static readonly string Version = typeof(PawnGenerator).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>The OER core every generated file includes.</summary>
    public const string CoreFileName = "dotasn_oer.inc";

    /// <summary>The JSON description of the generated value layouts.</summary>
    public const string ManifestFileName = "dotasn-pawn-layout.json";

    /// <summary>Pawn truncates symbols to this many characters.</summary>
    private const int MaxNameLength = 31;

    private static readonly HashSet<string> s_reserved = new(StringComparer.Ordinal)
    {
        "assert", "break", "case", "const", "continue", "default", "defined", "do", "else", "enum", "exit", "for",
        "forward", "goto", "if", "native", "new", "operator", "public", "return", "sizeof", "sleep", "state", "static",
        "stock", "switch", "tagof", "while", "true", "false", "cellmin", "cellmax",
    };

    private readonly Asn1Compilation _compilation;
    private readonly PawnGeneratorOptions _options;
    private readonly List<Diagnostic> _diagnostics = [];
    private readonly HashSet<string> _names = new(StringComparer.Ordinal);
    private readonly Dictionary<TypeAssignmentSymbol, PawnTypeEntry?> _entries = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<TypeAssignmentSymbol> _building = new(ReferenceEqualityComparer.Instance);

    private PawnGenerator(Asn1Compilation compilation, PawnGeneratorOptions options)
    {
        _compilation = compilation;
        _options = options;
    }

    /// <summary>Generates Pawn for a compilation (which should be free of errors).</summary>
    /// <param name="compilation">The compilation to generate from.</param>
    /// <param name="options">Generation options.</param>
    /// <returns>The generated files and diagnostics.</returns>
    public static PawnGenerationResult Generate(Asn1Compilation compilation, PawnGeneratorOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        var g = new PawnGenerator(compilation, options ?? new PawnGeneratorOptions());
        var modules = compilation.Modules.Where(m => g._options.Modules is null || g._options.Modules.Contains(m.Name)).ToList();
        foreach (var module in modules)
        {
            foreach (var assignment in module.TypeAssignments)
            {
                g.Entry(assignment);
            }
        }

        var files = new List<PawnFile> { new(CoreFileName, Core()) };
        files.AddRange(modules.Select(g.EmitModule));
        if (g._options.GenerateLayoutManifest)
        {
            files.Add(new PawnFile(ManifestFileName, PawnManifest.Write(modules, g._entries, Version)));
        }

        return new PawnGenerationResult([.. files], [.. DiagnosticOrdering.Sort(g._diagnostics)]);
    }

    /// <summary>The OER core library, the same for every generation.</summary>
    /// <returns>The content of <see cref="CoreFileName"/>.</returns>
    public static string Core()
    {
        using var stream = typeof(PawnGenerator).Assembly.GetManifestResourceStream(CoreFileName)
            ?? throw new InvalidOperationException($"missing resource {CoreFileName}");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    internal static string FileName(ModuleSymbol module) => Sanitize(module.Name) + ".inc";

    // ------------------------------------------------------------------ naming

    private static string Sanitize(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            builder.Append(char.IsAsciiLetterOrDigit(c) ? c : '_');
        }

        return builder.ToString();
    }

    /// <summary>A unique Pawn symbol, shortened with a hash of the full name when Pawn would truncate it.</summary>
    private string Allocate(string preferred, SourceLocation location)
    {
        var name = Sanitize(preferred);
        if (s_reserved.Contains(name))
        {
            name += "_";
        }

        if (name.Length > MaxNameLength)
        {
            var hash = Fnv1a(name).ToString("x4", CultureInfo.InvariantCulture)[..4];
            name = name[..(MaxNameLength - 5)] + "_" + hash;
        }

        var candidate = name;
        for (var i = 2; !_names.Add(candidate); i++)
        {
            var suffix = i.ToString(CultureInfo.InvariantCulture);
            candidate = (name.Length + suffix.Length > MaxNameLength ? name[..(MaxNameLength - suffix.Length)] : name) + suffix;
        }

        if (candidate != Sanitize(preferred))
        {
            _diagnostics.Add(Diagnostic.Create(DiagnosticCatalog.IdentifierRenamed, location, preferred, candidate, "Pawn keeps 31 characters of a symbol"));
        }

        return candidate;
    }

    private static uint Fnv1a(string text)
    {
        var hash = 2166136261u;
        foreach (var c in text)
        {
            hash = (hash ^ c) * 16777619u;
        }

        return (hash >> 16) ^ (hash & 0xFFFF);
    }

    // ------------------------------------------------------------------ layouts

    private sealed class UnsupportedException(string reason) : Exception(reason);

    private PawnTypeEntry? Entry(TypeAssignmentSymbol assignment)
    {
        if (_entries.TryGetValue(assignment, out var known))
        {
            return known;
        }

        if (!_building.Add(assignment))
        {
            throw new UnsupportedException($"'{assignment.Name}' is recursive, which has no fixed layout");
        }

        PawnTypeEntry? entry = null;
        try
        {
            if (assignment.Type is null || assignment.IsValueSet)
            {
                return null;
            }

            var layout = Build(assignment.Type, topLevel: true);
            var prefix = Allocate(Prefix(assignment.Name, layout), assignment.Location);
            if (prefix != Sanitize(assignment.Name))
            {
                _diagnostics.Add(Diagnostic.Create(DiagnosticCatalog.IdentifierRenamed, assignment.Location, assignment.Name, prefix, "a shorter prefix keeps the type's constants within Pawn's 31 characters"));
            }

            var candidate = new PawnTypeEntry(assignment, prefix) { Layout = layout };
            candidate.EncodeName = Allocate(prefix + "_encode", assignment.Location);
            candidate.DecodeName = Allocate(prefix + "_decode", assignment.Location);
            candidate.CellsName = Allocate(prefix + "_CELLS", assignment.Location);
            entry = candidate;
        }
        catch (UnsupportedException e)
        {
            _diagnostics.Add(Diagnostic.Create(DiagnosticCatalog.GenerationUnsupported, assignment.Location, assignment.QualifiedName, e.Message));
        }
        finally
        {
            _building.Remove(assignment);
        }

        _entries[assignment] = entry;
        return entry;
    }

    /// <summary>The type's name, or its capital initials when its longest constant would exceed Pawn's limit.</summary>
    private static string Prefix(string name, PawnLayout layout)
    {
        var sanitized = Sanitize(name);
        var longest = PawnConstantsEmitter.Collect(layout).Select(c => c.Suffix.Length).Append("_encode".Length).Max();
        if (sanitized.Length + longest <= MaxNameLength)
        {
            return sanitized;
        }

        var initials = new string([.. sanitized.Where((c, i) => i == 0 || char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c))]);
        return initials.Length >= 2 ? initials : sanitized[..Math.Max(2, MaxNameLength - longest)];
    }

    private PawnLayout Build(AsnType type, bool topLevel = false)
    {
        if (!topLevel && type.Reference is { } reference && type.OwnConstraints.IsEmpty && reference.Type is not null)
        {
            var target = Entry(reference) ?? throw new UnsupportedException($"it uses '{reference.Name}', which is not generated");
            return new ReferenceLayout(target);
        }

        return type.Kind switch
        {
            TypeKind.Boolean => new BooleanLayout(),
            TypeKind.Integer => BuildInteger(type),
            TypeKind.Enumerated => BuildEnumerated(type),
            TypeKind.Null => new NullLayout(),
            TypeKind.OctetString => BuildString(type, StringKind.Octets),
            TypeKind.BitString => BuildString(type, StringKind.Bits),
            TypeKind.CharacterString => BuildCharacterString(type),
            TypeKind.Sequence => BuildSequence(type),
            TypeKind.Choice => BuildChoice(type),
            TypeKind.SequenceOf => BuildSequenceOf(type),
            var kind => throw new UnsupportedException($"{kind} is not supported in Pawn"),
        };
    }

    private static IntegerLayout BuildInteger(AsnType type)
    {
        var values = NonExtensibleConstraints.Integer(type.Constraints);
        var lower = values.LowerBound;
        var upper = values.UpperBound;
        IntegerLayout layout;
        if (lower is { } l && l.Sign >= 0)
        {
            var octets = upper is not { } u ? 0 : u <= byte.MaxValue ? 1 : u <= ushort.MaxValue ? 2 : u <= uint.MaxValue ? 4 : u <= ulong.MaxValue ? 8 : 0;
            layout = new IntegerLayout(octets > 0 ? IntegerForm.UnsignedFixed : IntegerForm.UnsignedVariable, octets, lower, upper);
        }
        else
        {
            var octets = lower is { } sl && upper is { } su
                ? (sl >= sbyte.MinValue && su <= sbyte.MaxValue ? 1 : sl >= short.MinValue && su <= short.MaxValue ? 2 : sl >= int.MinValue && su <= int.MaxValue ? 4 : sl >= long.MinValue && su <= long.MaxValue ? 8 : 0)
                : 0;
            layout = new IntegerLayout(octets > 0 ? IntegerForm.SignedFixed : IntegerForm.SignedVariable, octets, lower, upper);
        }

        var fitsCell = layout.IsSigned
            ? (lower is null || lower >= int.MinValue) && (upper is null || upper <= int.MaxValue)
            : upper is null || upper <= uint.MaxValue;
        if (layout.Octets == 8 || !fitsCell)
        {
            throw new UnsupportedException($"the INTEGER range {values} does not fit a 32-bit cell");
        }

        return layout;
    }

    private static EnumeratedLayout BuildEnumerated(AsnType type)
    {
        var items = new List<EnumeratedItem>();
        foreach (var item in type.EnumItems)
        {
            if (item.Value < int.MinValue || item.Value > int.MaxValue)
            {
                throw new UnsupportedException($"the ENUMERATED value {item.Value} does not fit a 32-bit cell");
            }

            items.Add(new EnumeratedItem(item.Name, (long)item.Value));
        }

        return new EnumeratedLayout(items, type.IsExtensible);
    }

    private static (long Minimum, long Maximum, bool IsFixed) Sizes(AsnType type)
    {
        var sizes = NonExtensibleConstraints.Size(type.Constraints);
        if (sizes.UpperBound is not { } upper)
        {
            throw new UnsupportedException("a size without an upper bound has no fixed layout");
        }

        if (upper > 65535)
        {
            throw new UnsupportedException($"a size of up to {upper} is too large for a script's memory");
        }

        var lower = sizes.LowerBound ?? BigInteger.Zero;
        return ((long)lower, (long)upper, lower == upper);
    }

    private static StringLayout BuildString(AsnType type, StringKind kind)
    {
        if (type.ContainedType is not null)
        {
            throw new UnsupportedException("contents constraints are not supported in Pawn");
        }

        var (minimum, maximum, isFixed) = Sizes(type);
        return new StringLayout(kind, minimum, maximum, isFixed);
    }

    private static StringLayout BuildCharacterString(AsnType type)
    {
        switch (type.StringKind)
        {
            case CharacterStringKind.IA5 or CharacterStringKind.Visible or CharacterStringKind.Numeric or CharacterStringKind.Printable:
                {
                    var (minimum, maximum, isFixed) = Sizes(type);
                    return new StringLayout(StringKind.KnownMultiplierCharacters, minimum, maximum, isFixed);
                }

            case CharacterStringKind.Utf8:
                {
                    var (_, maximum, _) = Sizes(type);
                    return new StringLayout(StringKind.Utf8Characters, 0, maximum * 4, isFixed: false);
                }

            default:
                throw new UnsupportedException($"{type.StringKind} strings are not supported in Pawn");
        }
    }

    private SequenceLayout BuildSequence(AsnType type)
    {
        var roots = type.Components.Where(c => c.ExtensionAdditionIndex < 0 && !c.IsInRootPart2)
            .Concat(type.Components.Where(c => c.ExtensionAdditionIndex < 0 && c.IsInRootPart2));
        var fields = new List<SequenceField>();
        var offset = 0;
        foreach (var component in roots)
        {
            var presence = component.Presence switch
            {
                Presence.Optional => FieldPresence.Optional,
                Presence.Default => FieldPresence.Default,
                _ => FieldPresence.Required,
            };
            var field = new SequenceField(component.Name, presence, Build(component.Type), DefaultNumber(component.DefaultValue));
            if (field.HasPresenceCell)
            {
                field.PresenceOffset = offset++;
            }

            field.ValueOffset = offset;
            offset += field.Layout.Cells;
            fields.Add(field);
        }

        var layout = new SequenceLayout(fields, type.IsExtensible);
        if (layout.PreambleBits > 32)
        {
            throw new UnsupportedException("more than 32 OPTIONAL and DEFAULT components are not supported in Pawn");
        }

        return layout;
    }

    private static long? DefaultNumber(AsnValue? value) => value switch
    {
        IntegerValue i when i.Value >= int.MinValue && i.Value <= uint.MaxValue => (long)i.Value,
        BooleanValue b => b.Value ? 1 : 0,
        EnumeratedValue e when e.Number >= int.MinValue && e.Number <= int.MaxValue => (long)e.Number,
        _ => null,
    };

    private ChoiceLayout BuildChoice(AsnType type)
    {
        var alternatives = new List<ChoiceAlternative>();
        foreach (var component in type.Components.Where(c => c.ExtensionAdditionIndex < 0))
        {
            if (component.Type.Tags.IsEmpty)
            {
                throw new UnsupportedException($"the untagged CHOICE alternative '{component.Name}' is not supported in Pawn");
            }

            var tag = component.Type.Tags[0].Tag;
            if (tag.Number >= 63)
            {
                throw new UnsupportedException($"the tag number {tag.Number} of '{component.Name}' needs more than one octet");
            }

            var octet = (byte)(((byte)tag.Class << 6) | tag.Number);
            alternatives.Add(new ChoiceAlternative(component.Name, alternatives.Count, octet, Build(component.Type)));
        }

        if (alternatives.Count == 0)
        {
            throw new UnsupportedException("a CHOICE needs at least one root alternative");
        }

        return new ChoiceLayout(alternatives);
    }

    private SequenceOfLayout BuildSequenceOf(AsnType type)
    {
        var (minimum, maximum, _) = Sizes(type);
        var element = Build(type.ElementType ?? throw new UnsupportedException("SEQUENCE OF without an element type"));
        var layout = new SequenceOfLayout(minimum, maximum, element);
        if (layout.Cells > 65535)
        {
            throw new UnsupportedException($"{layout.Cells} cells are too many for a script's memory");
        }

        return layout;
    }

    // ------------------------------------------------------------------ emission

    private PawnFile EmitModule(ModuleSymbol module)
    {
        var w = new PawnWriter();
        w.Line($"/* Generated by DotAsn {Version} from the ASN.1 module {module.Name}. Do not edit. */");
        w.Line("/* Canonical OER (COER) encoders and decoders over value layouts, see " + CoreFileName + ". */");
        w.Line();
        var guard = "_" + Sanitize(module.Name) + "_included";
        w.Line($"#if defined {guard}");
        w.Line("    #endinput");
        w.Line("#endif");
        w.Line($"#define {guard}");
        w.Line();
        w.Line($"#include \"{CoreFileName}\"");
        foreach (var other in ReferencedModules(module))
        {
            w.Line($"#include \"{FileName(other)}\"");
        }

        foreach (var assignment in module.TypeAssignments)
        {
            if (_entries.GetValueOrDefault(assignment) is not { } entry)
            {
                continue;
            }

            w.Line();
            w.Line($"/* ---- {assignment.Name} */");
            w.Line();
            new PawnConstantsEmitter(w, Allocate).Emit(entry);
            w.Line();
            new PawnCodeEmitter(w).EmitEncode(entry);
            w.Line();
            new PawnCodeEmitter(w).EmitDecode(entry);
        }

        return new PawnFile(FileName(module), w.ToString());
    }

    private IEnumerable<ModuleSymbol> ReferencedModules(ModuleSymbol module)
    {
        var referenced = new SortedDictionary<string, ModuleSymbol>(StringComparer.Ordinal);
        foreach (var assignment in module.TypeAssignments)
        {
            if (_entries.GetValueOrDefault(assignment)?.Layout is { } layout)
            {
                Collect(layout);
            }
        }

        return referenced.Values;

        void Collect(PawnLayout layout)
        {
            switch (layout)
            {
                case ReferenceLayout r:
                    if (r.Target.Assignment.Module != module)
                    {
                        referenced[r.Target.Assignment.Module.Name] = r.Target.Assignment.Module;
                    }

                    break;
                case SequenceLayout s:
                    foreach (var f in s.Fields)
                    {
                        Collect(f.Layout);
                    }

                    break;
                case ChoiceLayout c:
                    foreach (var a in c.Alternatives)
                    {
                        Collect(a.Layout);
                    }

                    break;
                case SequenceOfLayout so:
                    Collect(so.Element);
                    break;
            }
        }
    }

    internal static string Literal(BigInteger value)
    {
        if (value == int.MinValue)
        {
            return "cellmin";
        }

        if (value >= int.MinValue && value <= int.MaxValue)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        return "0x" + ((uint)value).ToString("X8", CultureInfo.InvariantCulture);
    }

    internal static string Literal(long value) => Literal(new BigInteger(value));
}
