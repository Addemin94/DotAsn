using System.Collections.Immutable;
using Asn1.Diagnostics;
using Asn1.Syntax;

namespace Asn1.Semantics.Binding;

internal sealed partial class Binder
{
    private const string BuiltinClassesText = """
        BuiltinClasses DEFINITIONS ::= BEGIN
        TYPEIDENTIFIER ::= CLASS { &id OBJECT IDENTIFIER UNIQUE, &Type } WITH SYNTAX { &Type IDENTIFIED BY &id }
        ABSTRACTSYNTAX ::= CLASS { &id OBJECT IDENTIFIER UNIQUE, &Type, &property BIT STRING { handles-invalid-encodings(0) } DEFAULT {} }
            WITH SYNTAX { &Type IDENTIFIED BY &id [HAS PROPERTY &property] }
        END
        """;

    private ModuleSymbol? _builtinModule;
    private readonly Dictionary<string, ObjectClass> _builtinClasses = new(StringComparer.Ordinal);
    private readonly HashSet<AssignmentSymbol> _objectsInProgress = new(ReferenceEqualityComparer.Instance);

    private ObjectClass? BuiltinClass(string name)
    {
        if (_builtinClasses.TryGetValue(name, out var cls))
        {
            return cls;
        }

        if (_builtinModule is null)
        {
            var bag = new DiagnosticBag();
            var unit = Parser.ParseCompilationUnit(new SourceText("<builtin>", BuiltinClassesText), bag);
            _builtinModule = new ModuleSymbol(unit.Modules[0]);
            foreach (var a in unit.Modules[0].Assignments)
            {
                _builtinModule.TryAdd(new ClassAssignmentSymbol(_builtinModule, a));
            }
        }

        var symbol = (ClassAssignmentSymbol?)_builtinModule.FindLocal(name == "TYPE-IDENTIFIER" ? "TYPEIDENTIFIER" : "ABSTRACTSYNTAX");
        cls = symbol is null ? null : EnsureClass(symbol);
        if (cls is not null)
        {
            var renamed = new ObjectClass(name, cls.Location) { Fields = cls.Fields, WithSyntax = cls.WithSyntax };
            _builtinClasses[name] = renamed;
            return renamed;
        }

        return null;
    }

    private ObjectClass? EnsureClass(ClassAssignmentSymbol c)
    {
        if (c.Class is not null)
        {
            return c.Class;
        }

        if (!_objectsInProgress.Add(c))
        {
            Report(DiagnosticCatalog.IllegalRecursion, c.Location, $"class '{c.Name}' is defined in terms of itself");
            return null;
        }

        try
        {
            var scope = ModuleScope(c.Module);
            switch (c.Syntax)
            {
                case ClassAssignmentSyntax { Class: BuiltinClassSyntax b }:
                    return c.Class = BuiltinClass(b.Name);
                case ClassAssignmentSyntax { Class: ObjectClassDefinitionSyntax def }:
                    {
                        var cls = new ObjectClass(c.Name, c.Location) { WithSyntax = def.WithSyntax };
                        c.Class = cls;
                        var fields = ImmutableArray.CreateBuilder<ClassField>();
                        var names = new HashSet<string>(StringComparer.Ordinal);
                        foreach (var f in def.Fields)
                        {
                            if (!names.Add(f.Name.Text))
                            {
                                Report(DiagnosticCatalog.DuplicateComponent, f.Name.Location, f.Name.Text, "CLASS");
                                continue;
                            }

                            fields.Add(ResolveFieldSpec(f, scope));
                        }

                        cls.Fields = fields.ToImmutable();
                        return cls;
                    }

                case TypeAssignmentSyntax { Type: TypeReferenceSyntax r }:
                    return c.Class = ResolveClassReference(r, scope);
                default:
                    return null;
            }
        }
        finally
        {
            _objectsInProgress.Remove(c);
        }
    }

    private ClassField ResolveFieldSpec(FieldSpecSyntax f, Scope scope)
    {
        switch (f.Kind)
        {
            case FieldSpecKind.Type:
                return new ClassField(f.Name.Text, ClassFieldKind.Type, f);
            case FieldSpecKind.VariableTypeValue:
                return new ClassField(f.Name.Text, ClassFieldKind.VariableTypeValue, f) { TypeFieldReference = f.GovernorFieldPath.FirstOrDefault()?.Text };
            case FieldSpecKind.VariableTypeValueSet:
                return new ClassField(f.Name.Text, ClassFieldKind.VariableTypeValueSet, f) { TypeFieldReference = f.GovernorFieldPath.FirstOrDefault()?.Text };
            case FieldSpecKind.ValueOrObject:
            case FieldSpecKind.ValueSetOrObjectSet:
                {
                    var isSet = f.Kind == FieldSpecKind.ValueSetOrObjectSet;
                    if (f.Governor is TypeReferenceSyntax gr && NamesClass(scope.Module, gr))
                    {
                        return new ClassField(f.Name.Text, isSet ? ClassFieldKind.ObjectSet : ClassFieldKind.Object, f) { FieldClass = ResolveClassReference(gr, scope) };
                    }

                    var type = ResolveHeader(f.Governor!, scope);
                    return new ClassField(f.Name.Text, isSet ? ClassFieldKind.FixedTypeValueSet : ClassFieldKind.FixedTypeValue, f) { FixedType = type };
                }

            default:
                throw new InvalidOperationException();
        }
    }

    private ObjectClass? ResolveClassReference(TypeReferenceSyntax r, Scope scope)
    {
        if (r.Module is null && r.Name.Text is "TYPE-IDENTIFIER" or "ABSTRACT-SYNTAX")
        {
            return BuiltinClass(r.Name.Text);
        }

        var lookup = LookupName(scope, r.Module, r.Name);
        switch (lookup.Symbol)
        {
            case ClassAssignmentSymbol c:
                if (c.IsParameterized)
                {
                    Report(DiagnosticCatalog.UnsupportedFeature, r.Location, "Parameterized information object classes", string.Empty);
                    return null;
                }

                return EnsureClass(c);
            case null:
                if (lookup.Parameter is not null)
                {
                    Report(DiagnosticCatalog.UnsupportedFeature, r.Location, "Class-valued parameters", string.Empty);
                }

                return null;
            default:
                Report(DiagnosticCatalog.WrongReferenceKind, r.Location, r.ToString(), lookup.Symbol.KindName, "information object class");
                return null;
        }
    }

    // ------------------------------------------------------------------ objects

    private InformationObject? EnsureObject(ObjectAssignmentSymbol o)
    {
        if (o.Object is not null)
        {
            return o.Object;
        }

        if (!_objectsInProgress.Add(o))
        {
            Report(DiagnosticCatalog.IllegalRecursion, o.Location, $"object '{o.Name}' is defined in terms of itself");
            return null;
        }

        try
        {
            var syntax = (ValueAssignmentSyntax)o.Syntax;
            var scope = ModuleScope(o.Module);
            var cls = ResolveClassReference((TypeReferenceSyntax)syntax.Type, scope);
            if (cls is null)
            {
                return null;
            }

            return o.Object = ResolveObject(syntax.Value, cls, scope, o.Name);
        }
        finally
        {
            _objectsInProgress.Remove(o);
        }
    }

    private InformationObject? ResolveObject(ValueSyntax v, ObjectClass cls, Scope scope, string? name)
    {
        switch (v)
        {
            case ValueReferenceSyntax r:
                {
                    var lookup = LookupName(scope, r.Module, r.Name);
                    if (lookup.Parameter is { } binding && binding.Actual.AsValue is { } actual)
                    {
                        return ResolveObject(actual, cls, binding.CallerScope, name);
                    }

                    switch (lookup.Symbol)
                    {
                        case ObjectAssignmentSymbol oa:
                            return EnsureObject(oa);
                        case null:
                            return null;
                        default:
                            Report(DiagnosticCatalog.WrongReferenceKind, r.Location, r.ToString(), lookup.Symbol.KindName, "information object");
                            return null;
                    }
                }

            case BracedValueSyntax braced:
                return DefineObject(braced, cls, scope, name);
            default:
                Report(DiagnosticCatalog.InformationObjectError, v.Location, $"expected an object of class {cls.Name}");
                return null;
        }
    }

    private InformationObject? DefineObject(BracedValueSyntax braced, ObjectClass cls, Scope scope, string? name)
    {
        var obj = new InformationObject(cls, name, braced.Location);
        var settings = new Dictionary<string, FieldSetting>(StringComparer.Ordinal);
        var deferred = new List<(ClassField Field, ValueSyntax Syntax)>();
        var parser = Parser.ForTokenRange(braced.Lex, braced.OpenBrace + 1, braced.CloseBrace, _diagnostics, _options.Parser);
        var useDefaultSyntax = cls.WithSyntax is null || parser.CurrentToken.Kind is TokenKind.TypeFieldReference or TokenKind.ValueFieldReference;

        bool ParseSetting(ClassField field)
        {
            var setting = new FieldSetting(field);
            switch (field.Kind)
            {
                case ClassFieldKind.Type:
                    {
                        var ts = parser.TryRun(p => p.ParseType());
                        if (ts is null)
                        {
                            return false;
                        }

                        setting.Type = ResolveHeader(ts, scope);
                        break;
                    }

                case ClassFieldKind.FixedTypeValue:
                    {
                        var vs = parser.TryRun(p => p.ParseValue());
                        if (vs is null)
                        {
                            return false;
                        }

                        setting.ValueType = field.FixedType;
                        setting.Value = ResolveValue(vs, field.FixedType!, scope);
                        break;
                    }

                case ClassFieldKind.VariableTypeValue:
                    {
                        var vs = parser.TryRun(p => p.ParseValue());
                        if (vs is null)
                        {
                            return false;
                        }

                        deferred.Add((field, vs));
                        break;
                    }

                case ClassFieldKind.FixedTypeValueSet:
                case ClassFieldKind.VariableTypeValueSet:
                    {
                        if (parser.CurrentToken.Kind == TokenKind.LeftBrace)
                        {
                            var specs = parser.TryRun(p => p.ParseBracedElementSetSpecs());
                            if (specs is null)
                            {
                                return false;
                            }

                            if (field.FixedType is not null)
                            {
                                var vsType = CopyNode(field.FixedType, specs.Location, null, scope);
                                State(vsType).PendingConstraints.Add((specs, scope, specs.Location));
                                setting.ValueSet = vsType;
                            }
                        }
                        else
                        {
                            var ts = parser.TryRun(p => p.ParseType());
                            if (ts is null)
                            {
                                return false;
                            }

                            setting.ValueSet = ResolveHeader(ts, scope);
                        }

                        break;
                    }

                case ClassFieldKind.Object:
                    {
                        var vs = parser.TryRun(p => p.ParseValue());
                        if (vs is null)
                        {
                            return false;
                        }

                        setting.Object = field.FieldClass is null ? null : ResolveObject(vs, field.FieldClass, scope, null);
                        break;
                    }

                case ClassFieldKind.ObjectSet:
                    {
                        ElementSetSpecsSyntax? specs;
                        if (parser.CurrentToken.Kind == TokenKind.LeftBrace)
                        {
                            specs = parser.TryRun(p => p.ParseBracedElementSetSpecs());
                        }
                        else
                        {
                            var ts = parser.TryRun(p => p.ParseType());
                            specs = ts is null ? null : new ElementSetSpecsSyntax(ts.Source, ts.Span, new ContainedSubtypeSyntax(ts.Source, ts.Span, ts, false), false, null);
                        }

                        if (specs is null)
                        {
                            return false;
                        }

                        setting.ObjectSet = field.FieldClass is null ? null : ResolveObjectSet(specs, field.FieldClass, scope, null);
                        break;
                    }
            }

            if (!settings.TryAdd(field.Name, setting))
            {
                Report(DiagnosticCatalog.InformationObjectError, braced.Location, $"field {field.Name} is set more than once");
            }

            return true;
        }

        if (useDefaultSyntax)
        {
            while (!parser.IsAtEnd)
            {
                var ft = parser.Next();
                var field = cls.FindField(ft.Text);
                if (field is null)
                {
                    Report(DiagnosticCatalog.InformationObjectError, new SourceLocation(braced.Source, ft.Span), $"'{ft.Text}' is not a field of class {cls.Name}");
                    return null;
                }

                if (!ParseSetting(field))
                {
                    return null;
                }

                if (!parser.IsAtEnd && !parser.ExpectToken(TokenKind.Comma, "','"))
                {
                    return null;
                }
            }
        }
        else
        {
            if (!MatchSyntax(cls.WithSyntax!.Value, parser, cls, ParseSetting, braced) || !parser.IsAtEnd)
            {
                if (!parser.IsAtEnd)
                {
                    parser.ReportUnexpected($"end of object definition for class {cls.Name}");
                }

                return null;
            }
        }

        foreach (var (field, vs) in deferred)
        {
            var typeSetting = field.TypeFieldReference is null ? null : settings.GetValueOrDefault(field.TypeFieldReference);
            var setting = settings[field.Name];
            if (typeSetting?.Type is { } t)
            {
                setting.ValueType = t;
                setting.Value = ResolveValue(vs, t, scope);
            }
        }

        foreach (var f in cls.Fields)
        {
            if (!settings.ContainsKey(f.Name) && !f.IsOptional && !f.HasDefault)
            {
                Report(DiagnosticCatalog.InformationObjectError, braced.Location, $"mandatory field {f.Name} of class {cls.Name} is not set");
            }
        }

        obj.Settings = settings.ToImmutableDictionary(StringComparer.Ordinal);
        return obj;
    }

    private bool MatchSyntax(ImmutableArray<SyntaxTokenSpecSyntax> items, Parser parser, ObjectClass cls, Func<ClassField, bool> parseSetting, BracedValueSyntax braced)
    {
        foreach (var item in items)
        {
            if (item.IsGroup)
            {
                var first = item.OptionalGroup.FirstOrDefault();
                if (first?.Literal is not null && !LiteralMatches(parser.CurrentToken, first.Literal))
                {
                    continue;
                }

                if (first?.Literal is null && parser.IsAtEnd)
                {
                    continue;
                }

                if (!MatchSyntax(item.OptionalGroup, parser, cls, parseSetting, braced))
                {
                    return false;
                }

                continue;
            }

            if (item.Literal is not null)
            {
                if (!LiteralMatches(parser.CurrentToken, item.Literal))
                {
                    parser.ReportUnexpected($"'{item.Literal}'");
                    return false;
                }

                parser.Next();
                continue;
            }

            var field = cls.FindField(item.Field!.Text);
            if (field is null)
            {
                Report(DiagnosticCatalog.InformationObjectError, item.Location, $"WITH SYNTAX refers to unknown field {item.Field.Text}");
                return false;
            }

            if (!parseSetting(field))
            {
                return false;
            }
        }

        return true;
    }

    private static bool LiteralMatches(Token token, string literal) =>
        literal == "," ? token.Kind == TokenKind.Comma : token.Kind is TokenKind.TypeReference or TokenKind.Keyword && token.Text == literal;

    // ------------------------------------------------------------------ object sets

    private ObjectSet? EnsureObjectSet(ObjectSetAssignmentSymbol os)
    {
        if (os.ObjectSet is not null)
        {
            return os.ObjectSet;
        }

        if (!_objectsInProgress.Add(os))
        {
            Report(DiagnosticCatalog.IllegalRecursion, os.Location, $"object set '{os.Name}' is defined in terms of itself");
            return null;
        }

        try
        {
            var syntax = (SetAssignmentSyntax)os.Syntax;
            var scope = ModuleScope(os.Module);
            var cls = ResolveClassReference((TypeReferenceSyntax)syntax.Governor, scope);
            return cls is null ? null : os.ObjectSet = ResolveObjectSet(syntax.Elements, cls, scope, os.Name);
        }
        finally
        {
            _objectsInProgress.Remove(os);
        }
    }

    private ObjectSet ResolveObjectSet(ElementSetSpecsSyntax specs, ObjectClass cls, Scope scope, string? name)
    {
        var set = new ObjectSet(cls, name, specs.Location) { IsExtensible = specs.IsExtensible };
        var objects = new List<InformationObject>();
        if (specs.Root is not null)
        {
            CollectObjects(specs.Root, cls, scope, objects);
        }

        if (specs.Additions is not null)
        {
            CollectObjects(specs.Additions, cls, scope, objects);
        }

        set.Objects = [.. objects.Distinct()];
        return set;
    }

    private void CollectObjects(ElementSetSyntax e, ObjectClass cls, Scope scope, List<InformationObject> objects)
    {
        switch (e)
        {
            case UnionSyntax u:
                foreach (var i in u.Items)
                {
                    CollectObjects(i, cls, scope, objects);
                }

                break;
            case NestedElementSetSyntax n:
                if (n.Inner.Root is not null)
                {
                    CollectObjects(n.Inner.Root, cls, scope, objects);
                }

                if (n.Inner.Additions is not null)
                {
                    CollectObjects(n.Inner.Additions, cls, scope, objects);
                }

                break;
            case SingleValueSyntax sv:
                if (ResolveObject(sv.Value, cls, scope, null) is { } obj)
                {
                    objects.Add(obj);
                }

                break;
            case ContainedSubtypeSyntax { Type: TypeReferenceSyntax r }:
                {
                    var lookup = LookupName(scope, r.Module, r.Name);
                    if (lookup.Parameter is { } binding)
                    {
                        var actual = binding.Actual.AsValue as BracedValueSyntax;
                        var inner = actual is null ? null : ParseBracedElementSet(actual);
                        if (inner is not null)
                        {
                            var nested = ResolveObjectSet(inner, cls, binding.CallerScope, null);
                            objects.AddRange(nested.Objects);
                        }
                        else if (binding.Actual.AsType is TypeReferenceSyntax ar)
                        {
                            CollectObjects(new ContainedSubtypeSyntax(ar.Source, ar.Span, ar, false), cls, binding.CallerScope, objects);
                        }

                        break;
                    }

                    if (lookup.Symbol is ObjectSetAssignmentSymbol os)
                    {
                        if (os.IsParameterized)
                        {
                            Report(DiagnosticCatalog.UnsupportedFeature, r.Location, "Parameterized object sets", string.Empty);
                        }
                        else if (EnsureObjectSet(os) is { } s)
                        {
                            objects.AddRange(s.Objects);
                        }
                    }
                    else if (lookup.Symbol is not null)
                    {
                        Report(DiagnosticCatalog.WrongReferenceKind, r.Location, r.ToString(), lookup.Symbol.KindName, "information object set");
                    }

                    break;
                }

            case IntersectionSyntax or ExceptSyntax or AllExceptSyntax:
                Report(DiagnosticCatalog.UnsupportedFeature, e.Location, "Intersections and exclusions of object sets", string.Empty);
                break;
            default:
                Report(DiagnosticCatalog.InformationObjectError, e.Location, "unsupported object set element");
                break;
        }
    }

    // ------------------------------------------------------------------ field types and values

    private AsnType ResolveFieldType(FieldTypeSyntax f, Scope scope)
    {
        if (f.ClassReference is not null)
        {
            ObjectClass? cls;
            var r = f.ClassReference;
            var isBuiltin = r.Module is null && r.Name.Text is "TYPE-IDENTIFIER" or "ABSTRACT-SYNTAX";
            var lookup = isBuiltin ? default : LookupName(scope, r.Module, r.Name);
            if (lookup.Symbol is ObjectSetAssignmentSymbol os)
            {
                cls = EnsureObjectSet(os)?.Class;
            }
            else
            {
                cls = isBuiltin || lookup.Symbol is ClassAssignmentSymbol ? ResolveClassReference(r, scope) : null;
                if (!isBuiltin && lookup.Symbol is not null and not ClassAssignmentSymbol)
                {
                    Report(DiagnosticCatalog.WrongReferenceKind, r.Location, r.ToString(), lookup.Symbol.KindName, "information object class");
                }
            }

            if (cls is null)
            {
                return ErrorNode(f.Location, scope);
            }

            var field = FollowFieldPath(cls, f.FieldPath, f.Location);
            if (field is null)
            {
                return ErrorNode(f.Location, scope);
            }

            switch (field.Value.Field.Kind)
            {
                case ClassFieldKind.Type:
                case ClassFieldKind.VariableTypeValue:
                case ClassFieldKind.VariableTypeValueSet:
                    {
                        var node = NewNode(TypeKind.OpenType, f.Location, f, scope);
                        node._openType = new OpenTypeInfo { Class = field.Value.Class, FieldName = field.Value.Field.Name };
                        node.FieldOrigin = new ClassFieldReference(field.Value.Class, field.Value.Field);
                        State(node).StructureDone = true;
                        return node;
                    }

                case ClassFieldKind.FixedTypeValue:
                case ClassFieldKind.FixedTypeValueSet:
                    {
                        var node = CopyNode(field.Value.Field.FixedType!, f.Location, f, scope);
                        node.FieldOrigin = new ClassFieldReference(field.Value.Class, field.Value.Field);
                        return node;
                    }

                default:
                    Report(DiagnosticCatalog.InformationObjectError, f.Location, $"{field.Value.Field.Name} is an object or object set field, not a type");
                    return ErrorNode(f.Location, scope);
            }
        }

        // object.&Field: information from an object.
        var objLookup = LookupName(scope, null, f.ObjectReference!);
        if (objLookup.Symbol is ObjectAssignmentSymbol oa && EnsureObject(oa) is { } obj)
        {
            var setting = obj[f.FieldPath[0].Text];
            if (setting?.Type is { } t)
            {
                return CopyNode(t, f.Location, f, scope);
            }

            Report(DiagnosticCatalog.InformationObjectError, f.Location, $"object '{f.ObjectReference!.Text}' does not set a type in {f.FieldPath[0].Text}");
        }
        else if (objLookup.Symbol is not null)
        {
            Report(DiagnosticCatalog.WrongReferenceKind, f.Location, f.ObjectReference!.Text, objLookup.Symbol.KindName, "information object");
        }

        return ErrorNode(f.Location, scope);
    }

    private (ObjectClass Class, ClassField Field)? FollowFieldPath(ObjectClass cls, ImmutableArray<NameSyntax> path, SourceLocation location)
    {
        var current = cls;
        for (var i = 0; i < path.Length; i++)
        {
            var field = current.FindField(path[i].Text);
            if (field is null)
            {
                Report(DiagnosticCatalog.InformationObjectError, path[i].Location, $"'{path[i].Text}' is not a field of class {current.Name}");
                return null;
            }

            if (i == path.Length - 1)
            {
                return (current, field);
            }

            if (field.FieldClass is null)
            {
                Report(DiagnosticCatalog.InformationObjectError, path[i].Location, $"'{path[i].Text}' is not an object or object set field");
                return null;
            }

            current = field.FieldClass;
        }

        return null;
    }

    private AsnValue ResolveFieldValue(FieldValueSyntax f, AsnType type, Scope scope)
    {
        var lookup = LookupName(scope, f.Module, f.ObjectReference);
        if (lookup.Symbol is ObjectAssignmentSymbol oa && EnsureObject(oa) is { } obj && obj[f.FieldPath[0].Text] is { Value: { } v })
        {
            return v;
        }

        if (lookup.Symbol is not null)
        {
            Report(DiagnosticCatalog.InformationObjectError, f.Location, $"cannot obtain a value from '{f.ObjectReference.Text}.{f.FieldPath[0].Text}'");
        }

        return new ErrorValue(f.Location);
    }

    private void ApplyTableConstraint(AsnType node, TableConstraintSyntax t, Scope scope)
    {
        var cls = node.FieldOrigin?.Class ?? node.OpenType?.Class;
        if (cls is null)
        {
            if (node.Kind != TypeKind.Error)
            {
                Report(DiagnosticCatalog.InformationObjectError, t.Location, "a table constraint requires an information object class field type");
            }

            return;
        }

        var set = ResolveObjectSet(t.ObjectSet, cls, scope, null);
        var at = t.AtNotations.FirstOrDefault();
        node.TableConstraint = new TableConstraintInfo(set, at is null ? [] : [.. at.Path.Select(p => p.Text)], at?.Level ?? 0);
    }
}
