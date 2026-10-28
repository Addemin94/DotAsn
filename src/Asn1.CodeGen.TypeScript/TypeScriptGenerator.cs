using System.Collections.Immutable;
using System.Numerics;
using System.Text;
using Asn1.Constraints;
using Asn1.Diagnostics;
using Asn1.Semantics;

namespace Asn1.CodeGen.TypeScript;

/// <summary>Options for <see cref="TypeScriptGenerator"/>.</summary>
public sealed record TypeScriptGeneratorOptions
{
    /// <summary>The module specifier of the runtime package imported by generated code.</summary>
    public string RuntimeModule { get; init; } = "@dotasn/runtime";

    /// <summary>
    /// The extension of relative imports between generated modules: ".js" (default, for tsc and bundlers), ".ts"
    /// (for runtimes that execute TypeScript directly) or "" (extensionless).
    /// </summary>
    public string ImportExtension { get; init; } = ".js";

    /// <summary>Emit documentation comments from ASN.1 comments and notation.</summary>
    public bool GenerateDocumentation { get; init; } = true;

    /// <summary>Restrict generation to these modules (null: all).</summary>
    public IReadOnlySet<string>? Modules { get; init; }

    /// <summary>
    /// Also emit <see cref="TypeScriptGenerator.SchemaFileName"/>, which exports the DotAsn schema of the generated
    /// modules for the WebAssembly runtime (<c>@dotasn/wasm</c>).
    /// </summary>
    public bool GenerateSchema { get; init; } = true;
}

/// <summary>A generated TypeScript file.</summary>
/// <param name="Path">Relative output path.</param>
/// <param name="Content">File content.</param>
public sealed record TypeScriptFile(string Path, string Content);

/// <summary>The result of TypeScript generation.</summary>
/// <param name="Files">One file per ASN.1 module, in deterministic order.</param>
/// <param name="Diagnostics">Renames and unsupported constructs.</param>
public sealed record TypeScriptGenerationResult(ImmutableArray<TypeScriptFile> Files, ImmutableArray<Diagnostic> Diagnostics);

/// <summary>
/// Generates TypeScript: for every type an interface or union describing its values and a descriptor constant (the
/// shape of the values, for the value-notation conversion of <c>@dotasn/runtime</c>), and the DotAsn schema with which
/// <c>@dotasn/wasm</c> encodes and decodes them.
/// </summary>
public sealed class TypeScriptGenerator
{
    /// <summary>The generator version recorded in generated files.</summary>
    public static readonly string Version = typeof(TypeScriptGenerator).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    private const string Rt = "$";
    private static readonly BigInteger MaxSafe = (BigInteger.One << 53) - 1;

    private static readonly HashSet<string> s_reserved = new(StringComparer.Ordinal)
    {
        "break", "case", "catch", "class", "const", "continue", "debugger", "default", "delete", "do", "else", "enum",
        "export", "extends", "false", "finally", "for", "function", "if", "import", "in", "instanceof", "new", "null",
        "return", "super", "switch", "this", "throw", "true", "try", "typeof", "var", "void", "while", "with", "yield",
        "let", "static", "implements", "interface", "package", "private", "protected", "public", "await", "as", "async",
        "undefined", "NaN", "Infinity", "Uint8Array", "$",
    };

    private readonly Asn1Compilation _compilation;
    private readonly TypeScriptGeneratorOptions _options;
    private readonly List<Diagnostic> _diagnostics = [];
    private readonly List<ModuleContext> _modules = [];
    private readonly Dictionary<AsnType, Named> _named = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<TypeAssignmentSymbol, Named> _assignments = new(ReferenceEqualityComparer.Instance);
    private ModuleContext? _current;

    private TypeScriptGenerator(Asn1Compilation compilation, TypeScriptGeneratorOptions options)
    {
        _compilation = compilation;
        _options = options;
    }

    private sealed class ModuleContext(ModuleSymbol module, string fileName)
    {
        public ModuleSymbol Module { get; } = module;

        public string FileName { get; } = fileName;

        public HashSet<string> Scope { get; } = new(StringComparer.Ordinal);

        /// <summary>Exports in emission order.</summary>
        public List<Entry> Entries { get; } = [];

        /// <summary>Names imported from other generated modules, by file name.</summary>
        public SortedDictionary<string, SortedSet<string>> Imports { get; } = new(StringComparer.Ordinal);

        public string Allocate(string preferred, string suffix)
        {
            var name = s_reserved.Contains(preferred) ? preferred + "_" : preferred;
            if (Scope.Add(name))
            {
                return name;
            }

            var candidate = name + suffix;
            for (var i = 2; !Scope.Add(candidate); i++)
            {
                candidate = name + suffix + i;
            }

            return candidate;
        }
    }

    private sealed record Named(ModuleContext Module, string Name);

    private abstract record Entry(string Name);

    private sealed record TypeEntry(string Name, AsnType Node, TypeAssignmentSymbol? Assignment, string AsnName) : Entry(Name);

    private sealed record ValueEntry(string Name, ValueAssignmentSymbol Value) : Entry(Name);

    /// <summary>Generates TypeScript for a compilation (which should be free of errors).</summary>
    public static TypeScriptGenerationResult Generate(Asn1Compilation compilation, TypeScriptGeneratorOptions? options = null)
    {
        var g = new TypeScriptGenerator(compilation, options ?? new TypeScriptGeneratorOptions());
        g.Plan();
        var files = g._modules.Select(g.EmitModule).ToList();
        if (g._options.GenerateSchema && g._modules.Count > 0)
        {
            files.Add(EmitSchema(compilation, g._options));
        }

        return new TypeScriptGenerationResult([.. files], [.. DiagnosticOrdering.Sort(g._diagnostics)]);
    }

    /// <summary>The file that exports the DotAsn schema (a name no ASN.1 module can produce).</summary>
    public const string SchemaFileName = "dotasn-schema.ts";

    private static TypeScriptFile EmitSchema(Asn1Compilation compilation, TypeScriptGeneratorOptions options)
    {
        var json = Dynamic.Asn1SchemaWriter.Write(compilation, options.Modules);
        var file = new TsSourceFile(
        [
            new TsComment(
            [
                "<auto-generated>",
                $"    Generated by DotAsn {Version}: the DotAsn schema of the generated modules.",
                "    Do not edit: changes are lost when the code is regenerated.",
                "</auto-generated>",
            ]),
            new TsConst(
                "schema",
                new TsTypeName("string"),
                new TsString(json),
                options.GenerateDocumentation ? "The DotAsn schema (JSON) for the WebAssembly runtime: `await loadDotAsn(schema)` from @dotasn/wasm." : null),
        ]);
        return new TypeScriptFile(SchemaFileName, TsPrinter.Print(file));
    }

    // ------------------------------------------------------------------ planning

    private void Plan()
    {
        foreach (var m in _compilation.Modules.Where(m => _options.Modules is null || _options.Modules.Contains(m.Name)))
        {
            _modules.Add(new ModuleContext(m, Pascal(m.Name)));
        }

        // Pass 1: every type assignment gets an export; definitions belong to their own assignment.
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
                    _diagnostics.Add(Diagnostic.Create(DiagnosticCatalog.GenerationUnsupported, ta.Location, ta.QualifiedName, reason));
                    continue;
                }

                var name = ctx.Allocate(Pascal(ta.Name), "Type");
                ReportRename(ta.Name, Pascal(ta.Name), name, ta.Location);
                var named = new Named(ctx, name);
                _assignments[ta] = named;
                if (!ta.Type.IsReference)
                {
                    _named[ta.Type] = named;
                }

                ctx.Entries.Add(new TypeEntry(name, ta.Type, ta, ta.QualifiedName));
            }
        }

        // Pass 2: inline SEQUENCE/SET/CHOICE/ENUMERATED definitions and parameterized instances get derived names.
        foreach (var ctx in _modules)
        {
            foreach (var entry in ctx.Entries.OfType<TypeEntry>().ToList())
            {
                VisitMembers(entry.Node, entry.Name, ctx, 0, entry.AsnName);
            }

            foreach (var va in ctx.Module.ValueAssignments)
            {
                if (va.Value is not null and not ErrorValue && va.Type is not null && !va.IsParameterized && Unsupported(va.Type, new HashSet<AsnType>(ReferenceEqualityComparer.Instance)) is null)
                {
                    var valueName = ctx.Allocate(Camel(va.Name), "Value");
                    ReportRename(va.Name, Camel(va.Name), valueName, va.Location);
                    ctx.Entries.Add(new ValueEntry(valueName, va));
                }
            }
        }
    }

    /// <summary>
    /// Names inline types. Their ASN.1 name is their path from the type assignment (<c>Module.Type.component</c>,
    /// <c>[]</c> for SEQUENCE OF elements), the name under which <see cref="Dynamic.Asn1SchemaWriter"/> registers them, so
    /// that their descriptors can be encoded with @dotasn/wasm on their own.
    /// </summary>
    private void Visit(AsnType occ, string preferredName, ModuleContext ctx, int depth, string path)
    {
        if (depth > 64)
        {
            return;
        }

        if (occ.Target is { } target)
        {
            if (!_named.ContainsKey(target) && target.Assignment is null)
            {
                Visit(target, preferredName, ctx, depth + 1, path);
            }

            return;
        }

        if (occ.Kind is TypeKind.Sequence or TypeKind.Set or TypeKind.Choice or TypeKind.Enumerated && !_named.ContainsKey(occ))
        {
            var name = ctx.Allocate(preferredName, "Type");
            _named[occ] = new Named(ctx, name);
            ctx.Entries.Add(new TypeEntry(name, occ, null, path));
            VisitMembers(occ, name, ctx, depth, path);
            return;
        }

        if (occ.IsCollection && occ.ElementType is { } element)
        {
            Visit(element, preferredName + "Item", ctx, depth + 1, path + "[]");
        }
    }

    private void VisitMembers(AsnType node, string ownerName, ModuleContext ctx, int depth, string path)
    {
        if (node.Target is not null)
        {
            Visit(node, ownerName, ctx, depth + 1, path);
            return;
        }

        foreach (var c in node.Components)
        {
            Visit(c.Type, ownerName + Pascal(c.Name), ctx, depth + 1, path + "." + c.Name);
        }

        if (node.IsCollection && node.ElementType is { } element)
        {
            Visit(element, ownerName + "Item", ctx, depth + 1, path + "[]");
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
                return $"{AsnType.KindName(type.Kind)} is not supported by the generated TypeScript and the WebAssembly runtime";
            case TypeKind.Enumerated or TypeKind.BitString when Dynamic.DynamicTypeFactory.UnsupportedReason(type) is { } reason:
                // The limits of the WebAssembly runtime, which loads the schema (as for the dynamic runtime).
                return reason;
            case TypeKind.BitString when def.NamedNumbers.Any(n => BigInteger.Abs(n.Value) > MaxSafe):
                // Named bit positions are JavaScript numbers in the descriptors.
                return "named bit positions must fit in ±(2^53 - 1)";
        }

        foreach (var c in def.Components)
        {
            if (Unsupported(c.Type, visited) is { } r)
            {
                return $"component '{c.Name}': {r}";
            }
        }

        return def.IsCollection && def.ElementType is { } e ? Unsupported(e, visited) : null;
    }

    private void ReportRename(string asnName, string preferred, string name, SourceLocation location)
    {
        if (!string.Equals(preferred, name, StringComparison.Ordinal))
        {
            _diagnostics.Add(Diagnostic.Create(DiagnosticCatalog.IdentifierRenamed, location, asnName, name, "reserved word or collision"));
        }
    }

    // ------------------------------------------------------------------ emission

    private TypeScriptFile EmitModule(ModuleContext ctx)
    {
        _current = ctx;
        var body = new List<TsStatement>();
        foreach (var entry in ctx.Entries)
        {
            switch (entry)
            {
                case TypeEntry t when t.Node.IsReference:
                    {
                        // An alias of another type: the same values under its own name.
                        body.Add(new TsTypeAlias(t.Name, TypeOf(t.Node), Doc(t.Assignment?.Documentation, $"ASN.1: {t.AsnName} ::= {Notation(t.Node)}")));
                        body.Add(new TsConst(t.Name, DescriptorType(t.Name), Obj(
                        [
                            new("kind", new TsString("ref")),
                            new("name", new TsString(t.AsnName)),
                            new("target", new TsArrow([], BaseOf(t.Node))),
                        ]), null));
                        break;
                    }

                case TypeEntry t:
                    body.Add(TypeDeclaration(t));
                    body.Add(new TsConst(t.Name, DescriptorType(t.Name), Literal(t.Node, t.AsnName), null));
                    break;
                case ValueEntry v when ValueExpr(v.Value.Value!, v.Value.Type!, out var outside) is not { } && outside is var (value, ofType):
                    // An INTEGER beyond ±(2^53 - 1) whose type is generated as number (it violates its constraint).
                    _diagnostics.Add(Diagnostic.Create(DiagnosticCatalog.ValueOutsideType, value.Location, value.Value, "number", "TypeScript", ofType.DisplayName, $"'{v.Value.Name}' is not generated"));
                    break;
                case ValueEntry v when ValueExpr(v.Value.Value!, v.Value.Type!) is { } expr:
                    body.Add(new TsConst(v.Name, TypeOf(v.Value.Type!), expr, Doc(v.Value.Documentation, $"ASN.1: {v.Value.Name} {Notation(v.Value.Type!)} ::= {v.Value.Value}")));
                    break;
                case ValueEntry v:
                    _diagnostics.Add(Diagnostic.Create(DiagnosticCatalog.GenerationUnsupported, v.Value.Location, v.Value.Name, "the value notation cannot be represented in TypeScript"));
                    break;
            }
        }

        var statements = new List<TsStatement>
        {
            new TsComment(
            [
                "<auto-generated>",
                $"    Generated by DotAsn {Version} from ASN.1 module {ctx.Module.Name}.",
                "    Do not edit: changes are lost when the code is regenerated.",
                "</auto-generated>",
            ]),
            new TsNamespaceImport(Rt, _options.RuntimeModule),
        };
        foreach (var (file, names) in ctx.Imports)
        {
            statements.Add(new TsNamedImport([.. names], "./" + file + _options.ImportExtension));
        }

        statements.AddRange(body);
        _current = null;
        return new TypeScriptFile(ctx.FileName + ".ts", TsPrinter.Print(new TsSourceFile([.. statements])));
    }

    private static TsType DescriptorType(string name) => new TsGenericType(Rt + ".Asn1Type", [new TsTypeName(name)]);

    private string? Doc(string? documentation, string notation)
    {
        if (!_options.GenerateDocumentation)
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(documentation) ? notation : documentation.Trim() + "\n\n" + notation;
    }

    private TsStatement TypeDeclaration(TypeEntry t)
    {
        var def = t.Node;
        var doc = Doc(t.Assignment?.Documentation ?? def.Documentation, $"ASN.1: {t.AsnName} ::= {AsnType.KindName(def.Kind)}{(def.IsExtensible ? " (extensible)" : string.Empty)}");
        switch (def.Kind)
        {
            case TypeKind.Sequence or TypeKind.Set:
                return new TsInterface(t.Name, [.. def.Components.Select(c => new TsProperty(
                    c.Name,
                    TypeOf(c.Type),
                    c.IsOptional || c.IsExtensionAddition,
                    Doc(c.Documentation, ComponentNotation(c))))], doc);
            case TypeKind.Choice:
                {
                    var members = def.Components.Select(c => (TsType)new TsObjectType([new TsProperty(c.Name, TypeOf(c.Type), false, null)])).ToList();
                    if (def.IsExtensible)
                    {
                        members.Add(new TsTypeName(Rt + ".UnknownAlternative"));
                    }

                    return new TsTypeAlias(t.Name, new TsUnionType([.. members]), doc);
                }

            case TypeKind.Enumerated:
                {
                    var members = def.EnumItems.Select(i => (TsType)new TsLiteralType(i.Name)).ToList();
                    if (def.IsExtensible || def.EnumItems.Any(i => i.IsExtension))
                    {
                        members.Add(new TsTypeName(Rt + ".UnknownEnumItem"));
                    }

                    return new TsTypeAlias(t.Name, members.Count == 1 ? members[0] : new TsUnionType([.. members]), doc);
                }

            default:
                // A named primitive or collection type: its structural type (not its own name).
                return new TsTypeAlias(t.Name, TypeOf(def, structural: true), Doc(t.Assignment?.Documentation, $"ASN.1: {t.AsnName} ::= {Notation(def)}"));
        }
    }

    private static string ComponentNotation(AsnComponent c) => $"ASN.1: {c.Name} {Notation(c.Type)}" + c.Presence switch
    {
        Presence.Optional => " OPTIONAL",
        Presence.Default => " DEFAULT " + c.DefaultValue,
        _ => string.Empty,
    } + (c.IsExtensionAddition ? " (extension addition)" : string.Empty);

    private static string Notation(AsnType t)
    {
        var name = t.IsReference ? t.Reference?.Name ?? t.DisplayName : AsnType.KindName(t.Kind, t.StringKind);
        return name + string.Concat((t.IsReference ? t.OwnConstraints : t.Constraints).Select(c => " " + c));
    }

    private static string AsnName(AsnType t) => t.Assignment?.QualifiedName ?? AsnType.KindName(t.Kind, t.StringKind);

    /// <summary>References a named export, importing it when it belongs to another module.</summary>
    private string Use(Named named)
    {
        if (named.Module != _current)
        {
            if (!_current!.Imports.TryGetValue(named.Module.FileName, out var names))
            {
                _current.Imports[named.Module.FileName] = names = new SortedSet<string>(StringComparer.Ordinal);
            }

            names.Add(named.Name);
        }

        return named.Name;
    }

    // ------------------------------------------------------------------ value types

    /// <summary>The TypeScript type of the values of a type occurrence.</summary>
    private TsType TypeOf(AsnType occ, bool structural = false)
    {
        if (!structural && _named.TryGetValue(occ, out var named))
        {
            return new TsTypeName(Use(named));
        }

        if (occ.Target is { } target)
        {
            return occ.Reference is { IsParameterized: false } r && ReferenceEquals(target, r.Type) && _assignments.TryGetValue(r, out var a)
                ? new TsTypeName(Use(a))
                : TypeOf(target);
        }

        return occ.Kind switch
        {
            TypeKind.Boolean => new TsTypeName("boolean"),
            TypeKind.Integer => new TsTypeName(IsBig(occ) ? "bigint" : "number"),
            TypeKind.Real => new TsTypeName("number"),
            TypeKind.BitString => new TsTypeName(Rt + ".BitString"),
            TypeKind.OctetString => new TsTypeName("Uint8Array"),
            TypeKind.Null => new TsTypeName("null"),
            TypeKind.OpenType => new TsTypeName(Rt + ".OpenValue"),
            TypeKind.SequenceOf or TypeKind.SetOf => new TsArrayType(TypeOf(occ.ElementType!)),
            TypeKind.ObjectIdentifier or TypeKind.RelativeOid or TypeKind.CharacterString or TypeKind.UtcTime or TypeKind.GeneralizedTime
                or TypeKind.Date or TypeKind.TimeOfDay or TypeKind.DateTime or TypeKind.Duration or TypeKind.Time => new TsTypeName("string"),
            _ => new TsTypeName("unknown"),
        };
    }

    /// <summary>INTEGER values are bigint unless the definition's bounds are within ±(2^53 − 1).</summary>
    private static bool IsBig(AsnType occ)
    {
        while (occ.Target is { } t)
        {
            occ = t;
        }

        var set = ConstraintSets.IntegerSerial(occ.Constraints);
        var extensible = !occ.Constraints.IsDefaultOrEmpty && occ.Constraints[^1].IsExtensible;
        return set is null || extensible || set.LowerBound is not { } lo || set.UpperBound is not { } hi || lo < -MaxSafe || hi > MaxSafe;
    }

    // ------------------------------------------------------------------ descriptors

    /// <summary>The descriptor expression of a type occurrence used inside another descriptor.</summary>
    private TsExpression Desc(AsnType occ) =>
        _named.TryGetValue(occ, out var named) ? new TsIdentifier(Use(named))
        : occ.Target is not null ? BaseOf(occ)
        : Literal(occ, AsnName(occ));

    /// <summary>The descriptor a reference derives from (the named assignment, or the target's descriptor).</summary>
    private TsExpression BaseOf(AsnType occ) =>
        occ.Reference is { IsParameterized: false } r && ReferenceEquals(occ.Target, r.Type) && _assignments.TryGetValue(r, out var a)
            ? new TsIdentifier(Use(a))
            : Desc(occ.Target!);

    private static TsObject Obj(IEnumerable<TsObjectMember> members) => new([.. members]);

    /// <summary>A descriptor literal for a definition: what the value notation needs to know about its values.</summary>
    private TsExpression Literal(AsnType def, string name)
    {
        var m = new List<TsObjectMember>
        {
            new("kind", new TsString(KindOf(def))),
            new("name", new TsString(name)),
        };
        switch (def.Kind)
        {
            case TypeKind.Integer:
                m.Add(new("big", new TsBoolean(IsBig(def))));
                break;
            case TypeKind.Enumerated:
                m.Add(new("items", new TsArray([.. def.EnumItems.Select(i => (TsExpression)new TsString(i.Name))])));
                break;
            case TypeKind.BitString when !def.NamedNumbers.IsDefaultOrEmpty:
                m.Add(new("named", Obj(def.NamedNumbers.Select(n => new TsObjectMember(n.Name, TsNumber.Integer(n.Value))))));
                break;
            case TypeKind.Sequence or TypeKind.Set:
                m.Add(new("components", new TsArray([.. def.Components.Select(c => (TsExpression)Obj(
                [
                    new("name", new TsString(c.Name)),
                    new("type", new TsArrow([], Desc(c.Type))),
                    .. c.IsOptional || c.IsExtensionAddition ? [new TsObjectMember("optional", new TsBoolean(true))] : ImmutableArray<TsObjectMember>.Empty,
                ]))])));
                break;
            case TypeKind.Choice:
                m.Add(new("alternatives", new TsArray([.. def.Components.Select(c => (TsExpression)Obj(
                [
                    new("name", new TsString(c.Name)),
                    new("type", new TsArrow([], Desc(c.Type))),
                ]))])));
                break;
            case TypeKind.SequenceOf or TypeKind.SetOf:
                m.Add(new("element", new TsArrow([], Desc(def.ElementType!))));
                break;
        }

        return Obj(m);
    }

    private static string KindOf(AsnType def) => def.Kind switch
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
        _ => throw new NotSupportedException(AsnType.KindName(def.Kind)),
    };

    // ------------------------------------------------------------------ values

    /// <summary>A TypeScript expression for a value of a type occurrence, or null when it cannot be represented.</summary>
    private TsExpression? ValueExpr(AsnValue value, AsnType type) => ValueExpr(value, type, out _);

    /// <summary>
    /// A TypeScript expression for a value, or null; <paramref name="outside"/> is the first INTEGER (with its type) that
    /// a <c>number</c> cannot hold exactly, which is one reason for null.
    /// </summary>
    private TsExpression? ValueExpr(AsnValue value, AsnType type, out (IntegerValue Value, AsnType Type)? outside)
    {
        outside = null;
        var def = type.Definition;
        switch (value)
        {
            case IntegerValue i when def.Kind == TypeKind.Real:
                return TsNumber.Float((double)i.Value);
            case IntegerValue i when IsBig(type):
                return new TsBigInt(i.Value);
            case IntegerValue i when BigInteger.Abs(i.Value) > MaxSafe:
                outside = (i, type);
                return null;
            case IntegerValue i:
                return TsNumber.Integer(i.Value);
            case BooleanValue b:
                return new TsBoolean(b.Value);
            case RealValue r:
                return TsNumber.Float(r.Value);
            case StringValue s:
                return new TsString(s.Value);
            case OctetStringValue o:
                return new TsCall(new TsIdentifier("Uint8Array.of"), [.. o.Value.Select(b => (TsExpression)TsNumber.Integer(b))]);
            case BitStringValue bs:
                return new TsCall(new TsIdentifier(Rt + ".bitsFromBinary"), [new TsString(bs.Value.ToBinaryString())]);
            case ObjectIdentifierValue oid:
                return new TsString(oid.Value.ToString());
            case RelativeOidValue rel:
                return new TsString(rel.Value.ToString());
            case NullValue:
                return TsNull.Value;
            case EnumeratedValue e:
                return new TsString(e.Name);
            case SequenceValue sv:
                {
                    var members = new List<TsObjectMember>();
                    foreach (var nv in sv.Components)
                    {
                        if (def.Components.FirstOrDefault(c => c.Name == nv.Name) is not { } comp || ValueExpr(nv.Value, comp.Type, out outside) is not { } inner)
                        {
                            return null;
                        }

                        members.Add(new(nv.Name, inner));
                    }

                    return Obj(members);
                }

            case ChoiceValue cv:
                {
                    var alt = def.Components.FirstOrDefault(c => c.Name == cv.Alternative);
                    return alt is not null && ValueExpr(cv.Value, alt.Type, out outside) is { } inner ? Obj([new(cv.Alternative, inner)]) : null;
                }

            case SequenceOfValue so when def.ElementType is { } et:
                {
                    var items = new List<TsExpression>();
                    foreach (var item in so.Items)
                    {
                        if (ValueExpr(item, et, out outside) is not { } expr)
                        {
                            return null;
                        }

                        items.Add(expr);
                    }

                    return new TsArray([.. items]);
                }

            default:
                return null;
        }
    }

    // ------------------------------------------------------------------ naming

    /// <summary>Converts an ASN.1 reference to PascalCase.</summary>
    public static string Pascal(string name)
    {
        var sb = new StringBuilder(name.Length);
        var upper = true;
        foreach (var c in name)
        {
            if (c is '-' or '_' or '&' or '.')
            {
                upper = true;
                continue;
            }

            sb.Append(upper ? char.ToUpperInvariant(c) : c);
            upper = false;
        }

        var result = sb.ToString();
        return result.Length == 0 || !char.IsLetter(result[0]) ? "N" + result : result;
    }

    /// <summary>Converts an ASN.1 value reference to camelCase.</summary>
    public static string Camel(string name)
    {
        var p = Pascal(name);
        return char.ToLowerInvariant(p[0]) + p[1..];
    }
}
