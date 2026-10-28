using System.Collections.Immutable;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Asn1.Constraints;
using Asn1.Diagnostics;
using Asn1.Semantics;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Asn1.CodeGen.CSharp.Cs;
using static Asn1.CodeGen.CSharp.Literals;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Asn1.CodeGen.CSharp;

/// <summary>A generated source file.</summary>
/// <param name="Path">Relative output path.</param>
/// <param name="Content">File content.</param>
public sealed record GeneratedFile(string Path, string Content);

/// <summary>The result of C# generation.</summary>
/// <param name="Files">Generated files in deterministic order.</param>
/// <param name="Diagnostics">Generation diagnostics (renames, unsupported constructs).</param>
/// <param name="Manifest">The JSON manifest describing the generation.</param>
public sealed record CSharpGenerationResult(ImmutableArray<GeneratedFile> Files, ImmutableArray<Diagnostic> Diagnostics, string Manifest);

/// <summary>
/// Generates C# from a resolved compilation: one class per SEQUENCE/SET/CHOICE definition, one enum per ENUMERATED
/// definition, static classes for aliases, and a module class with descriptors and value constants. All encoding
/// behaviour lives in the runtime; generated code only describes types.
/// </summary>
public sealed partial class CSharpGenerator
{
    /// <summary>The generator version recorded in generated files.</summary>
    public static readonly string Version = typeof(CSharpGenerator).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    private readonly Asn1Compilation _compilation;
    private readonly CSharpGeneratorOptions _options;
    private readonly List<Diagnostic> _diagnostics = [];
    private readonly Dictionary<AsnType, GenType> _types = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<AsnType, IntegerMappingInfo> _integerClr = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<AsnComponent, ExpressionSyntax?> _defaults = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<TypeAssignmentSymbol, (ModuleContext Module, string Property)> _assignmentProperties = new(ReferenceEqualityComparer.Instance);
    private readonly List<ModuleContext> _modules = [];
    private readonly HashSet<TypeAssignmentSymbol> _skipped = new(ReferenceEqualityComparer.Instance);

    private CSharpGenerator(Asn1Compilation compilation, CSharpGeneratorOptions options)
    {
        _compilation = compilation;
        _options = options;
    }

    private SyntaxKind Access => _options.InternalTypes ? SyntaxKind.InternalKeyword : SyntaxKind.PublicKeyword;

    /// <summary>Generates C# for a compilation.</summary>
    /// <param name="compilation">The compilation (should be free of errors).</param>
    /// <param name="options">Options.</param>
    /// <param name="inputs">Input files to record in the manifest (path and content).</param>
    public static CSharpGenerationResult Generate(Asn1Compilation compilation, CSharpGeneratorOptions? options = null, IEnumerable<(string Path, string Content)>? inputs = null)
    {
        var g = new CSharpGenerator(compilation, options ?? new CSharpGeneratorOptions());
        g.Plan();
        var files = g.Emit();
        var manifest = g.Manifest(files, inputs ?? compilation.SyntaxTrees.Select(t => (t.Source.Path, t.Source.Text)));
        return new CSharpGenerationResult(files, [.. DiagnosticOrdering.Sort(g._diagnostics)], manifest);
    }

    // ------------------------------------------------------------------ planning

    private void Plan()
    {
        var modules = _compilation.Modules.Where(m => _options.Modules is null || _options.Modules.Contains(m.Name)).ToList();
        if (modules.Count == 0)
        {
            return;
        }

        var root = _options.Namespace ?? Naming.Pascal(modules[0].Name);
        var perModule = _options.NamespacePerModule ?? modules.Count > 1;
        var scopes = new Dictionary<string, NameScope>(StringComparer.Ordinal);
        foreach (var m in modules)
        {
            var ns = perModule ? root + "." + Naming.Pascal(m.Name) : root;
            var scope = scopes.TryGetValue(ns, out var s) ? s : scopes[ns] = new NameScope();
            var ctx = new ModuleContext(m, ns, scope, scope.Allocate(Naming.Pascal(m.Name) + "Asn1", "Types"));
            _modules.Add(ctx);
        }

        // Definitions that their own type assignment will own: an occurrence that merely references such a type
        // (possibly textually earlier, e.g. a component "b C1" before "C1 ::= CHOICE ...") must not claim it.
        foreach (var ta in _modules.SelectMany(c => c.Module.TypeAssignments))
        {
            if (!ta.IsParameterized && ta.Type is { } t && IsGenerated(t.Definition) && (!t.IsReference || t.Target!.Assignment is null))
            {
                _assignmentOwned.Add(t.Definition);
            }
        }

        // Pass 1: classes for definitions owned by type assignments; aliases otherwise.
        foreach (var ctx in _modules)
        {
            foreach (var ta in ctx.Module.TypeAssignments)
            {
                if (ta.IsParameterized || ta.Type is null)
                {
                    continue;
                }

                if (Unsupported(ta.Type, new HashSet<AsnType>(ReferenceEqualityComparer.Instance)) is { } reason)
                {
                    _skipped.Add(ta);
                    _diagnostics.Add(Diagnostic.Create(DiagnosticCatalog.GenerationUnsupported, ta.Location, ta.QualifiedName, reason));
                    continue;
                }

                var property = ctx.ClassScope.Allocate(Naming.Pascal(ta.Name));
                ReportRename(ta.Name, property, ta.Location, "module class member");
                var def = ta.Type.Definition;
                var owns = IsGenerated(def) && !_types.ContainsKey(def) && (!ta.Type.IsReference || ta.Type.Target!.Assignment is null);
                string? alias = null;
                if (owns)
                {
                    var name = ctx.TopScope.Allocate(Naming.Pascal(ta.Name), "Type");
                    ReportRename(ta.Name, name, ta.Location, "type name");
                    var gt = Register(def, name, null, ctx, ta);
                    ctx.TopTypes.Add(gt);
                    if (gt.Kind == GenKind.Enum)
                    {
                        gt.EnumDescriptorProperty = property;
                    }
                }
                else
                {
                    alias = ctx.TopScope.Allocate(Naming.Pascal(ta.Name), "Type");
                }

                ctx.Assignments.Add((ta, property, alias));
                _assignmentProperties[ta] = (ctx, property);
            }

            foreach (var va in ctx.Module.ValueAssignments)
            {
                if (va.Value is not null and not ErrorValue && va.Type is not null && !va.IsParameterized)
                {
                    ctx.Values.Add((va, ctx.ClassScope.Allocate(Naming.Pascal(va.Name))));
                }
            }
        }

        // Pass 2: nested classes for inline definitions and parameterized instances.
        foreach (var ctx in _modules)
        {
            foreach (var (ta, _, alias) in ctx.Assignments)
            {
                if (alias is not null)
                {
                    VisitOccurrence(ta.Type!, null, ctx, Naming.Pascal(ta.Name), 0);
                }
            }
        }
    }

    private readonly HashSet<AsnType> _assignmentOwned = new(ReferenceEqualityComparer.Instance);

    private static bool IsGenerated(AsnType def) => def.Kind is TypeKind.Sequence or TypeKind.Set or TypeKind.Choice or TypeKind.Enumerated;

    private GenType Register(AsnType def, string name, GenType? parent, ModuleContext ctx, TypeAssignmentSymbol? ta)
    {
        var gt = new GenType(def, name, parent, ctx, ta);
        _types[def] = gt;
        parent?.Nested.Add(gt);
        AllocateMembers(gt);
        foreach (var c in def.Components)
        {
            VisitOccurrence(c.Type, gt, ctx, gt.Members[c] + (gt.Kind == GenKind.Choice ? "Value" : "Type"), 0);
        }

        return gt;
    }

    private void AllocateMembers(GenType gt)
    {
        gt.Scope.Reserve(gt.Name);
        foreach (var r in new[] { "Asn1Type", "Equals", "GetHashCode", "ToString", "GetType", "MemberwiseClone", "Finalize" })
        {
            gt.Scope.Reserve(r);
        }

        switch (gt.Kind)
        {
            case GenKind.Enum:
                {
                    var scope = new NameScope(["Asn1UnknownExtension"]);
                    foreach (var item in gt.Definition.EnumItems)
                    {
                        gt.EnumMembers[item.Name] = scope.Allocate(Naming.Pascal(item.Name), "Item");
                    }

                    break;
                }

            case GenKind.Choice:
                gt.Scope.Reserve("Kind");
                gt.Scope.Reserve("AlternativeKind");
                if (gt.Definition.IsExtensible)
                {
                    gt.Scope.Reserve("UnknownExtension");
                }

                foreach (var c in gt.Definition.Components)
                {
                    gt.Members[c] = gt.Scope.Allocate(Naming.Pascal(c.Name), "Alternative");
                }

                break;
            default:
                foreach (var c in gt.Definition.Components)
                {
                    var name = gt.Scope.Allocate(Naming.Pascal(c.Name));
                    ReportRename(c.Name, name, c.Location, "property");
                    gt.Members[c] = name;
                }

                break;
        }
    }

    private void VisitOccurrence(AsnType occ, GenType? owner, ModuleContext ctx, string baseName, int depth)
    {
        if (depth > 64)
        {
            return;
        }

        var def = occ.Definition;
        if (def.IsCollection && def.ElementType is { } element)
        {
            VisitOccurrence(element, owner, ctx, baseName + "Item", depth + 1);
            return;
        }

        if (!IsGenerated(def) || _types.ContainsKey(def) || _assignmentOwned.Contains(def))
        {
            return;
        }

        var scope = owner?.Scope ?? ctx.TopScope;
        var name = scope.Allocate(baseName, "Type");
        var gt = Register(def, name, owner, ctx, null);
        if (owner is null)
        {
            ctx.TopTypes.Add(gt);
        }

        if (gt.Kind == GenKind.Enum)
        {
            gt.EnumDescriptorProperty = ctx.ClassScope.Allocate((owner?.Name ?? string.Empty) + name);
            ctx.NestedEnums.Add(gt);
        }
    }

    private static string? Unsupported(AsnType type, HashSet<AsnType> visited)
    {
        var def = type.Definition;
        if (!visited.Add(def))
        {
            return null;
        }

        switch (type.Kind)
        {
            case TypeKind.Error:
                return "the type contains errors";
            case TypeKind.External or TypeKind.EmbeddedPdv or TypeKind.UnrestrictedCharacterString or TypeKind.OidIri or TypeKind.RelativeOidIri:
                return $"{AsnType.KindName(type.Kind)} is not supported by the runtime yet";
            case TypeKind.Enumerated when def.EnumItems.Any(i => !Fits(i.Value, "long")):
                return "enumeration values must fit in 64 bits";
            // The runtime's Asn1NamedNumber holds a long (Asn1NamedNumber.CanHold).
            case TypeKind.BitString when def.NamedNumbers.Any(n => !Fits(n.Value, "long")):
                return "named bit positions must fit in 64 bits";
        }

        foreach (var c in def.Components)
        {
            if (Unsupported(c.Type, visited) is { } r)
            {
                return $"component '{c.Name}': {r}";
            }
        }

        if (def.IsCollection && def.ElementType is { } e)
        {
            // A collection whose element is (transitively, through collections only) itself has no C# type.
            var probe = e.Definition;
            for (var i = 0; i < 64 && probe.IsCollection; i++)
            {
                if (ReferenceEquals(probe, def))
                {
                    return "a SEQUENCE OF that contains itself without an intervening structure cannot be represented";
                }

                probe = probe.ElementType!.Definition;
            }

            return Unsupported(e, visited);
        }

        return null;
    }

    private void ReportRename(string asnName, string csName, SourceLocation location, string what)
    {
        if (!string.Equals(Naming.Pascal(asnName), csName, StringComparison.Ordinal))
        {
            _diagnostics.Add(Diagnostic.Create(DiagnosticCatalog.IdentifierRenamed, location, asnName, csName, "collision with another " + what));
        }
    }

    // ------------------------------------------------------------------ type mapping

    /// <summary>The node whose descriptor an occurrence derives from (null for definitions).</summary>
    private static AsnType? BaseNode(AsnType occ) => occ.Target;

    /// <summary>The CLR type of values of a type occurrence.</summary>
    private TypeSyntax Clr(AsnType occ)
    {
        if (BaseNode(occ) is { } b)
        {
            return Clr(b);
        }

        return occ.Kind switch
        {
            TypeKind.Boolean => BoolType,
            TypeKind.Integer => IntegerType(IntegerInfo(occ).Keyword),
            TypeKind.Real => DoubleType,
            TypeKind.Null => Core("Asn1Null"),
            TypeKind.BitString => Core("Asn1BitString"),
            TypeKind.OctetString => ByteArray,
            TypeKind.ObjectIdentifier => Core("ObjectIdentifier"),
            TypeKind.RelativeOid => Core("RelativeObjectIdentifier"),
            TypeKind.CharacterString or TypeKind.UtcTime or TypeKind.GeneralizedTime or TypeKind.Date or TypeKind.TimeOfDay or TypeKind.DateTime or TypeKind.Duration or TypeKind.Time => StringType,
            TypeKind.OpenType => Runtime("Asn1OpenValue"),
            TypeKind.SequenceOf or TypeKind.SetOf => Global("System.Collections.Generic.List", Clr(occ.ElementType!)),
            TypeKind.Sequence or TypeKind.Set or TypeKind.Choice or TypeKind.Enumerated => _types[occ].TypeName,
            _ => ObjectType,
        };
    }

    /// <summary>The CLR keyword of an INTEGER occurrence and whether its range is bounded.</summary>
    private sealed record IntegerMappingInfo(string Keyword, bool Bounded);

    /// <summary>
    /// The CLR type of an INTEGER occurrence (computed once per occurrence): the smallest integer type that holds every
    /// value of the effective constraint, unsigned (byte, ushort, uint, ulong) for non-negative ranges and signed
    /// (sbyte, short, int, long) otherwise; "BigInteger" (or long, see <see cref="IntegerMapping"/>) when the range is
    /// unbounded, extensible or wider than 64 bits.
    /// </summary>
    private IntegerMappingInfo IntegerInfo(AsnType occ)
    {
        if (!_integerClr.TryGetValue(occ, out var info))
        {
            _integerClr[occ] = info = Bounds(occ) is var (lo, hi)
                ? new(SmallestIntegerKeyword(lo, hi), true)
                : new(_options.IntegerMapping == IntegerMapping.Int64 ? "long" : "BigInteger", false);
        }

        return info;
    }

    /// <summary>The bounds of an INTEGER occurrence's effective constraint, or null if it is unbounded or extensible.</summary>
    private static (BigInteger Lo, BigInteger Hi)? Bounds(AsnType occ)
    {
        var set = ConstraintSets.IntegerSerial(occ.Constraints);
        var extensible = !occ.Constraints.IsDefaultOrEmpty && occ.Constraints[^1].IsExtensible;
        return set is null || extensible || set.LowerBound is not { } lo || set.UpperBound is not { } hi ? null : (lo, hi);
    }

    /// <summary>The definition of an INTEGER occurrence (references followed), whose constraint decides its CLR type.</summary>
    private AsnType IntegerDefinition(AsnType occ)
    {
        while (BaseNode(occ) is { } b)
        {
            occ = b;
        }

        return occ;
    }

    /// <summary>
    /// Reports ASN7003 for an INTEGER value that does not fit the C# type of its type, with what the generator does
    /// instead (<paramref name="consequence"/>).
    /// </summary>
    private void ReportValueOutsideType(IntegerValue value, AsnType type, string consequence)
    {
        var info = IntegerInfo(IntegerDefinition(type));
        var mapping = info.Bounded ? string.Empty : " (unbounded and extensible INTEGERs are long with IntegerMapping.Int64)";
        var keyword = info.Keyword;
        _diagnostics.Add(Diagnostic.Create(DiagnosticCatalog.ValueOutsideType, value.Location, value.Value, keyword + mapping, "C#", type.DisplayName, consequence));
    }

    private static TypeSyntax IntegerType(string keyword) => keyword == "BigInteger" ? BigIntegerType : IntegerKeywordType(keyword);

    /// <summary>The CLR integer keyword of an INTEGER occurrence (as <see cref="Clr"/> maps it), or null for other types.</summary>
    private string? IntegerKeyword(AsnType occ)
    {
        occ = IntegerDefinition(occ);
        return occ.Kind == TypeKind.Integer ? IntegerInfo(occ).Keyword : null;
    }

    private static bool IsValueType(AsnType occ) => occ.Kind is TypeKind.Boolean or TypeKind.Integer or TypeKind.Real or TypeKind.Null or TypeKind.Enumerated;

    private static ExpressionSyntax MandatoryInitializer(AsnType occ) => occ.Kind switch
    {
        TypeKind.CharacterString or TypeKind.UtcTime or TypeKind.GeneralizedTime or TypeKind.Date or TypeKind.TimeOfDay or TypeKind.DateTime or TypeKind.Duration or TypeKind.Time => Member(StringType, "Empty"),
        TypeKind.OctetString => EmptyCollection,
        TypeKind.BitString => Member(Core("Asn1BitString"), "Empty"),
        TypeKind.SequenceOf or TypeKind.SetOf => EmptyCollection,
        TypeKind.Sequence or TypeKind.Set => ImplicitObjectCreationExpression(),
        _ => NullForgiving,
    };

    // ------------------------------------------------------------------ descriptors

    /// <summary>The expression that yields the descriptor of a type occurrence.</summary>
    private ExpressionSyntax Desc(AsnType occ)
    {
        if (occ.Target is null)
        {
            return DefinitionDesc(occ);
        }

        ExpressionSyntax baseExpr;
        AsnType baseNode;
        if (occ.Reference is { IsParameterized: false } r && ReferenceEquals(occ.Target, r.Type) && _assignmentProperties.TryGetValue(r, out var prop))
        {
            baseExpr = Member(prop.Module.ClassTypeName, prop.Property);
            baseNode = r.Type!;
        }
        else
        {
            baseNode = occ.Target;
            baseExpr = Desc(baseNode);
        }

        if (!occ.Tags.SequenceEqual(baseNode.Tags))
        {
            baseExpr = Call(Member(baseExpr, "WithTags"), TagsOf(occ));
        }

        if (!occ.OwnConstraints.IsDefaultOrEmpty)
        {
            baseExpr = Call(Member(baseExpr, "WithConstraints"), ConstraintList(occ.OwnConstraints));
        }

        return baseExpr;
    }

    private static ExpressionSyntax TagsOf(AsnType t) => Tags(t.Tags.Select(x => (x.Tag, x.IsExplicit)));

    private static string AsnName(AsnType t) => t.Assignment?.QualifiedName ?? AsnType.KindName(t.Kind, t.StringKind);

    private ExpressionSyntax DefinitionDesc(AsnType def)
    {
        var name = Str(AsnName(def));
        var tags = TagsOf(def);
        var cons = ConstraintList(def.Constraints);
        // Named numbers beyond 64 bits are left out of the metadata (see Asn1NamedNumber.CanHold); the alias class still
        // has constants for all of them.
        ExpressionSyntax NamedNumbers() => Collection(def.NamedNumbers.Where(n => Fits(n.Value, "long")).Select(n => New(Runtime("Asn1NamedNumber"), Str(n.Name), Int64((long)n.Value))));
        return def.Kind switch
        {
            TypeKind.Sequence or TypeKind.Set or TypeKind.Choice or TypeKind.Enumerated => _types[def].DescriptorExpression,
            TypeKind.Boolean => New(Runtime("BooleanType"), name, tags, cons),
            TypeKind.Integer => New(Runtime("IntegerType", IntegerType(IntegerInfo(def).Keyword)), name, tags, cons, NamedNumbers()),
            TypeKind.Real => New(Runtime("RealType"), name, tags, cons),
            TypeKind.BitString => New(Runtime("BitStringType"), name, tags, cons, NamedNumbers()),
            TypeKind.OctetString => New(Runtime("OctetStringType"), name, tags, cons),
            TypeKind.Null => New(Runtime("NullType"), name, tags, cons),
            TypeKind.ObjectIdentifier => New(Runtime("ObjectIdentifierType"), name, tags, cons),
            TypeKind.RelativeOid => New(Runtime("RelativeOidType"), name, tags, cons),
            TypeKind.CharacterString => New(Runtime("CharacterStringType"), Member(Core("CharacterStringKind"), def.StringKind.ToString()), name, tags, cons),
            TypeKind.UtcTime or TypeKind.GeneralizedTime or TypeKind.Date or TypeKind.TimeOfDay or TypeKind.DateTime or TypeKind.Duration or TypeKind.Time =>
                New(Runtime("TimeType"), Member(Runtime("TimeKind"), TimeKind(def.Kind)), name, tags, cons),
            TypeKind.OpenType => New(Runtime("OpenType"), name, tags, cons),
            TypeKind.SequenceOf or TypeKind.SetOf => New(
                Runtime("CollectionType", Clr(def.ElementType!)),
                name,
                Bool(def.Kind == TypeKind.SetOf),
                Lambda(Desc(def.ElementType!)),
                def.ElementName is null ? Null : Str(def.ElementName),
                tags,
                cons),
            _ => throw new NotSupportedException(AsnType.KindName(def.Kind)),
        };
    }

    private static string TimeKind(TypeKind k) => k switch
    {
        TypeKind.UtcTime => "UtcTime",
        TypeKind.GeneralizedTime => "GeneralizedTime",
        TypeKind.Date => "Date",
        TypeKind.TimeOfDay => "TimeOfDay",
        TypeKind.DateTime => "DateTime",
        TypeKind.Duration => "Duration",
        _ => "Time",
    };

    // ------------------------------------------------------------------ values

    /// <summary>An expression for a value of a type occurrence, or null if it cannot be expressed.</summary>
    private ExpressionSyntax? ValueExpr(AsnValue value, AsnType type) => ValueExpr(value, type, out _);

    /// <summary>
    /// An expression for a value of a type occurrence, or null if it cannot be expressed; <paramref name="outside"/> is
    /// the first INTEGER (with its type) that does not fit the C# type of its type, which is one reason for null.
    /// </summary>
    private ExpressionSyntax? ValueExpr(AsnValue value, AsnType type, out (IntegerValue Value, AsnType Type)? outside)
    {
        outside = null;
        var def = type.Definition;
        switch (value)
        {
            case IntegerValue i when type.Kind == TypeKind.Real:
                return Float64((double)i.Value);
            case IntegerValue i:
                {
                    var literal = TryInteger(i.Value, IntegerKeyword(type) ?? "BigInteger");
                    outside = literal is null ? (i, type) : null;
                    return literal;
                }
            case BooleanValue b:
                return Bool(b.Value);
            case RealValue r:
                return Float64(r.Value);
            case StringValue s:
                return Str(s.Value);
            case OctetStringValue o:
                return NewByteArray(o.Value.AsSpan());
            case BitStringValue bs:
                return Bits(bs.Value);
            case ObjectIdentifierValue oid:
                return Literals.Object(oid.Value);
            case RelativeOidValue rel:
                return Literals.Object(rel.Value);
            case NullValue:
                return Member(Core("Asn1Null"), "Value");
            case EnumeratedValue e when _types.TryGetValue(def, out var en):
                return Member(en.TypeName, en.EnumMembers[e.Name]);
            case SequenceValue sv when _types.TryGetValue(def, out var cls):
                {
                    var members = new List<(string, ExpressionSyntax)>();
                    foreach (var nv in sv.Components)
                    {
                        var comp = def.Components.First(c => c.Name == nv.Name);
                        if (ValueExpr(nv.Value, comp.Type, out outside) is not { } inner)
                        {
                            return null;
                        }

                        members.Add((cls.Members[comp], inner));
                    }

                    return NewWithInitializer(cls.TypeName, members);
                }

            case ChoiceValue cv when _types.TryGetValue(def, out var ch):
                {
                    var alt = def.Components.First(c => c.Name == cv.Alternative);
                    return ValueExpr(cv.Value, alt.Type, out outside) is { } inner ? New(Dot(ch.TypeName, ch.Members[alt]), inner) : null;
                }

            case SequenceOfValue so when def.ElementType is { } et:
                {
                    var items = new List<ExpressionSyntax>();
                    foreach (var item in so.Items)
                    {
                        if (ValueExpr(item, et, out outside) is not { } expr)
                        {
                            return null;
                        }

                        items.Add(expr);
                    }

                    return NewWithItems(Clr(type), items);
                }

            default:
                return null;
        }
    }

    // ------------------------------------------------------------------ manifest

    private string Manifest(ImmutableArray<GeneratedFile> files, IEnumerable<(string Path, string Content)> inputs)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("generator", "DotAsn C# generator");
            w.WriteString("version", Version);
            w.WriteStartArray("inputs");
            foreach (var (path, content) in inputs.OrderBy(i => i.Path, StringComparer.Ordinal))
            {
                w.WriteStartObject();
                w.WriteString("path", path.Replace('\\', '/'));
                w.WriteString("sha256", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant());
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteStartObject("options");
            w.WriteString("namespace", _modules.FirstOrDefault()?.Namespace);
            w.WriteString("layout", _options.Layout.ToString());
            w.WriteString("integerMapping", _options.IntegerMapping.ToString());
            w.WriteBoolean("documentation", _options.GenerateDocumentation);
            w.WriteBoolean("internalTypes", _options.InternalTypes);
            w.WriteEndObject();
            w.WriteStartArray("files");
            foreach (var f in files)
            {
                w.WriteStartObject();
                w.WriteString("path", f.Path);
                w.WriteString("sha256", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(f.Content))).ToLowerInvariant());
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(ms.ToArray()) + "\n";
    }
}
