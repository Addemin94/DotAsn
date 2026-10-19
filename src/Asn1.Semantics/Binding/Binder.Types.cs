using System.Collections.Immutable;
using System.Numerics;
using Asn1.Diagnostics;
using Asn1.Syntax;

namespace Asn1.Semantics.Binding;

internal sealed partial class Binder
{
    private AsnType? _integerType;

    /// <summary>A synthetic unconstrained INTEGER used for tag numbers, SIZE contexts and similar.</summary>
    private AsnType IntegerType
    {
        get
        {
            if (_integerType is null)
            {
                _integerType = new AsnType(TypeKind.Integer, SourceLocation.None) { Tags = [new TagInfo(Asn1Tag.Integer, false)] };
                _nodeStates[_integerType] = new NodeState(null, ModuleScope(_moduleList[0])) { StructureDone = true, ConstraintsDone = true };
            }

            return _integerType;
        }
    }

    private AsnType ErrorNode(SourceLocation location, Scope scope)
    {
        var node = NewNode(TypeKind.Error, location, null, scope);
        var st = State(node);
        st.StructureDone = true;
        st.ConstraintsDone = true;
        return node;
    }

    // ------------------------------------------------------------------ assignment headers

    private AsnType EnsureTypeHeader(TypeAssignmentSymbol a)
    {
        if (a.Type is not null)
        {
            return a.Type;
        }

        var scope = ModuleScope(a.Module);
        if (!_inProgress.Add(a))
        {
            Report(DiagnosticCatalog.IllegalRecursion, a.Location, $"'{a.Name}' is defined directly in terms of itself");
            return a.Type = ErrorNode(a.Location, scope);
        }

        try
        {
            AsnType node;
            if (a.Syntax is SetAssignmentSyntax set)
            {
                node = ResolveHeader(set.Governor, scope);
                State(node).PendingConstraints.Add((set.Elements, scope, set.Elements.Location));
            }
            else
            {
                node = ResolveHeader(a.TypeSyntax!, scope);
            }

            node.Assignment = a;
            node.Documentation ??= a.Documentation;
            a.Type ??= node;
            return a.Type;
        }
        finally
        {
            _inProgress.Remove(a);
        }
    }

    // ------------------------------------------------------------------ headers

    /// <summary>
    /// Creates the node for a type occurrence, computing its kind and effective tags. Structural details are
    /// queued so that recursive references only ever need the header of the referenced type.
    /// </summary>
    private AsnType ResolveHeader(TypeSyntax syntax, Scope scope)
    {
        switch (syntax)
        {
            case ConstrainedTypeSyntax c:
                {
                    var node = ResolveHeader(c.Type, scope);
                    foreach (var constraint in c.Constraints)
                    {
                        State(node).PendingConstraints.Add((constraint.Spec, scope, constraint.Location));
                    }

                    return node;
                }

            case TaggedTypeSyntax t:
                {
                    var node = ResolveHeader(t.Type, scope);
                    var isDummy = t.Type is TypeReferenceSyntax { Module: null } r && scope.Parameters.ContainsKey(r.Name.Text);
                    ApplyTag(node, t, scope, isDummy);
                    return node;
                }

            case BuiltinTypeSyntax b:
                return Builtin(b, scope);

            case IntegerTypeSyntax or BitStringTypeSyntax or EnumeratedTypeSyntax or StructuredTypeSyntax or CollectionTypeSyntax:
                return Constructed(syntax, scope);

            case TypeReferenceSyntax r:
                return ResolveReferenceHeader(r, scope);

            case SelectionTypeSyntax s:
                return ResolveSelection(s, scope);

            case FieldTypeSyntax f:
                return ResolveFieldType(f, scope);

            case InstanceOfTypeSyntax io:
                return ResolveInstanceOf(io, scope);

            case AnyTypeSyntax any:
                {
                    var node = NewNode(TypeKind.OpenType, any.Location, any, scope);
                    node._openType = new OpenTypeInfo { DefinedBy = any.DefinedBy?.Text };
                    State(node).StructureDone = true;
                    return node;
                }

            default:
                return ErrorNode(syntax.Location, scope);
        }
    }

    private AsnType Builtin(BuiltinTypeSyntax b, Scope scope)
    {
        (TypeKind kind, int tag, CharacterStringKind sk) = b.Kind switch
        {
            BuiltinTypeKind.Boolean => (TypeKind.Boolean, UniversalTagNumber.Boolean, default(CharacterStringKind)),
            BuiltinTypeKind.Null => (TypeKind.Null, UniversalTagNumber.Null, default),
            BuiltinTypeKind.Real => (TypeKind.Real, UniversalTagNumber.Real, default),
            BuiltinTypeKind.OctetString => (TypeKind.OctetString, UniversalTagNumber.OctetString, default),
            BuiltinTypeKind.ObjectIdentifier => (TypeKind.ObjectIdentifier, UniversalTagNumber.ObjectIdentifier, default),
            BuiltinTypeKind.RelativeOid => (TypeKind.RelativeOid, UniversalTagNumber.RelativeOid, default),
            BuiltinTypeKind.OidIri => (TypeKind.OidIri, UniversalTagNumber.OidIri, default),
            BuiltinTypeKind.RelativeOidIri => (TypeKind.RelativeOidIri, UniversalTagNumber.RelativeOidIri, default),
            BuiltinTypeKind.External => (TypeKind.External, UniversalTagNumber.External, default),
            BuiltinTypeKind.EmbeddedPdv => (TypeKind.EmbeddedPdv, UniversalTagNumber.EmbeddedPdv, default),
            BuiltinTypeKind.CharacterString => (TypeKind.UnrestrictedCharacterString, UniversalTagNumber.CharacterString, default),
            BuiltinTypeKind.Utf8String => (TypeKind.CharacterString, UniversalTagNumber.Utf8String, CharacterStringKind.Utf8),
            BuiltinTypeKind.NumericString => (TypeKind.CharacterString, UniversalTagNumber.NumericString, CharacterStringKind.Numeric),
            BuiltinTypeKind.PrintableString => (TypeKind.CharacterString, UniversalTagNumber.PrintableString, CharacterStringKind.Printable),
            BuiltinTypeKind.TeletexString => (TypeKind.CharacterString, UniversalTagNumber.TeletexString, CharacterStringKind.Teletex),
            BuiltinTypeKind.VideotexString => (TypeKind.CharacterString, UniversalTagNumber.VideotexString, CharacterStringKind.Videotex),
            BuiltinTypeKind.IA5String => (TypeKind.CharacterString, UniversalTagNumber.IA5String, CharacterStringKind.IA5),
            BuiltinTypeKind.GraphicString => (TypeKind.CharacterString, UniversalTagNumber.GraphicString, CharacterStringKind.Graphic),
            BuiltinTypeKind.VisibleString => (TypeKind.CharacterString, UniversalTagNumber.VisibleString, CharacterStringKind.Visible),
            BuiltinTypeKind.GeneralString => (TypeKind.CharacterString, UniversalTagNumber.GeneralString, CharacterStringKind.General),
            BuiltinTypeKind.UniversalString => (TypeKind.CharacterString, UniversalTagNumber.UniversalString, CharacterStringKind.Universal),
            BuiltinTypeKind.BmpString => (TypeKind.CharacterString, UniversalTagNumber.BmpString, CharacterStringKind.Bmp),
            BuiltinTypeKind.ObjectDescriptor => (TypeKind.CharacterString, UniversalTagNumber.ObjectDescriptor, CharacterStringKind.ObjectDescriptor),
            BuiltinTypeKind.UtcTime => (TypeKind.UtcTime, UniversalTagNumber.UtcTime, default),
            BuiltinTypeKind.GeneralizedTime => (TypeKind.GeneralizedTime, UniversalTagNumber.GeneralizedTime, default),
            BuiltinTypeKind.Date => (TypeKind.Date, UniversalTagNumber.Date, default),
            BuiltinTypeKind.TimeOfDay => (TypeKind.TimeOfDay, UniversalTagNumber.TimeOfDay, default),
            BuiltinTypeKind.DateTime => (TypeKind.DateTime, UniversalTagNumber.DateTime, default),
            BuiltinTypeKind.Duration => (TypeKind.Duration, UniversalTagNumber.Duration, default),
            BuiltinTypeKind.Time => (TypeKind.Time, UniversalTagNumber.Time, default),
            _ => (TypeKind.Error, 0, default),
        };
        var node = NewNode(kind, b.Location, b, scope);
        node._stringKind = sk;
        node.Tags = [new TagInfo(Asn1Tag.Universal(tag), false)];
        State(node).StructureDone = true;
        return node;
    }

    private AsnType Constructed(TypeSyntax syntax, Scope scope)
    {
        var (kind, tag) = syntax switch
        {
            IntegerTypeSyntax => (TypeKind.Integer, (int?)UniversalTagNumber.Integer),
            BitStringTypeSyntax => (TypeKind.BitString, UniversalTagNumber.BitString),
            EnumeratedTypeSyntax => (TypeKind.Enumerated, UniversalTagNumber.Enumerated),
            StructuredTypeSyntax { Kind: StructuredKind.Sequence } => (TypeKind.Sequence, UniversalTagNumber.Sequence),
            StructuredTypeSyntax { Kind: StructuredKind.Set } => (TypeKind.Set, UniversalTagNumber.Set),
            StructuredTypeSyntax { Kind: StructuredKind.Choice } => (TypeKind.Choice, null),
            CollectionTypeSyntax { IsSet: false } => (TypeKind.SequenceOf, UniversalTagNumber.Sequence),
            CollectionTypeSyntax { IsSet: true } => (TypeKind.SetOf, UniversalTagNumber.Set),
            _ => (TypeKind.Error, null),
        };
        var node = NewNode(kind, syntax.Location, syntax, scope);
        node.Tags = tag is null ? [] : [new TagInfo(Asn1Tag.Universal(tag.Value), false)];
        if (syntax is CollectionTypeSyntax { Constraint: { } sc })
        {
            State(node).PendingConstraints.Add((sc.Spec, scope, sc.Location));
        }

        // Extensibility is a header property: references to recursive types need it before the structure is resolved.
        node._isExtensible = syntax switch
        {
            StructuredTypeSyntax s => s.Items.OfType<ExtensionMarkerSyntax>().Any() || scope.Module.ExtensibilityImplied,
            EnumeratedTypeSyntax e => e.IsExtensible || scope.Module.ExtensibilityImplied,
            _ => false,
        };

        QueueStructure(node);
        return node;
    }

    private void QueueStructure(AsnType node)
    {
        var st = State(node);
        if (!st.StructureQueued)
        {
            st.StructureQueued = true;
            _structureQueue.Enqueue(node);
        }
    }

    private AsnType ResolveReferenceHeader(TypeReferenceSyntax r, Scope scope)
    {
        if (r.Module is null && r.Name.Text is "TYPE-IDENTIFIER" or "ABSTRACT-SYNTAX")
        {
            Report(DiagnosticCatalog.WrongReferenceKind, r.Location, r.Name.Text, "information object class", "type");
            return ErrorNode(r.Location, scope);
        }

        var lookup = LookupName(scope, r.Module, r.Name);
        if (lookup.Parameter is { } binding)
        {
            return ResolveParameterAsType(binding, r, scope);
        }

        switch (lookup.Symbol)
        {
            case TypeAssignmentSymbol ta when ta.IsParameterized:
                {
                    if (r.ActualParameters is not { } actuals)
                    {
                        Report(DiagnosticCatalog.MissingActualParameters, r.Location, ta.Name);
                        return ErrorNode(r.Location, scope);
                    }

                    var key = new InstanceKey(ta, [.. actuals.Select(a => ActualIdentity(a, scope, 0))]);
                    if (!_instances.TryGetValue(key, out var instance))
                    {
                        instance = Instantiate(ta, actuals, scope, r.Location);
                        _instances[key] = instance;
                    }

                    var node = NewNode(instance.Kind, r.Location, r, scope);
                    node.Reference = ta;
                    node.Target = instance;
                    node.Tags = instance.Tags;
                    State(node).StructureDone = true;
                    return node;
                }

            case TypeAssignmentSymbol ta:
                {
                    if (r.ActualParameters is not null)
                    {
                        Report(DiagnosticCatalog.ParameterCountMismatch, r.Location, ta.Name, 0, r.ActualParameters.Value.Length);
                    }

                    var target = EnsureTypeHeader(ta);
                    var node = NewNode(target.Kind, r.Location, r, scope);
                    node.Reference = ta;
                    node.Target = target;
                    node.Tags = target.Tags;
                    State(node).StructureDone = true;
                    return node;
                }

            case null:
                return ErrorNode(r.Location, scope);

            default:
                Report(DiagnosticCatalog.WrongReferenceKind, r.Location, r.ToString(), lookup.Symbol.KindName, "type");
                return ErrorNode(r.Location, scope);
        }
    }

    private readonly Dictionary<InstanceKey, AsnType> _instances = [];

    /// <summary>Identifies a parameterized instantiation so that recursive and repeated uses share one instance.</summary>
    private sealed class InstanceKey(TypeAssignmentSymbol assignment, ImmutableArray<object> actuals) : IEquatable<InstanceKey>
    {
        public bool Equals(InstanceKey? other) =>
            other is not null && ReferenceEquals(assignment, other.Assignment) && actuals.SequenceEqual(other.Actuals);

        public TypeAssignmentSymbol Assignment => assignment;

        public ImmutableArray<object> Actuals => actuals;

        public override bool Equals(object? obj) => Equals(obj as InstanceKey);

        public override int GetHashCode() => actuals.Aggregate(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(assignment), (h, a) => HashCode.Combine(h, a));
    }

    /// <summary>A stable identity for an actual parameter: follows dummy references and resolves plain references to symbols.</summary>
    private object ActualIdentity(ActualParameterSyntax actual, Scope scope, int depth)
    {
        if (depth < 64)
        {
            if (actual.AsType is TypeReferenceSyntax { Module: null, ActualParameters: null } tr && scope.Parameters.TryGetValue(tr.Name.Text, out var tb))
            {
                return ActualIdentity(tb.Actual, tb.CallerScope, depth + 1);
            }

            if (actual.AsValue is ValueReferenceSyntax { Module: null, ActualParameters: null } vr && scope.Parameters.TryGetValue(vr.Name.Text, out var vb))
            {
                return ActualIdentity(vb.Actual, vb.CallerScope, depth + 1);
            }

            if (actual.AsType is TypeReferenceSyntax { ActualParameters: null } r && LookupName(scope, r.Module, r.Name, report: false).Symbol is { } ts)
            {
                return ts;
            }

            if (actual.AsValue is ValueReferenceSyntax { ActualParameters: null } v && LookupName(scope, v.Module, v.Name, report: false).Symbol is { } vs)
            {
                return vs;
            }
        }

        return (actual.Source, actual.Span.Start, actual.Span.Length, scope.Parameters.IsEmpty ? (object)scope.Module : scope);
    }

    private AsnType Instantiate(TypeAssignmentSymbol ta, ImmutableArray<ActualParameterSyntax> actuals, Scope callerScope, SourceLocation location)
    {
        var formals = ta.Syntax.Parameters;
        if (formals.Length != actuals.Length)
        {
            Report(DiagnosticCatalog.ParameterCountMismatch, location, ta.Name, formals.Length, actuals.Length);
            return ErrorNode(location, callerScope);
        }

        if (callerScope.Depth >= _options.MaxInstantiationDepth)
        {
            Report(DiagnosticCatalog.ResourceLimitExceeded, location, $"parameterized instantiation of '{ta.Name}' nests deeper than {_options.MaxInstantiationDepth} levels");
            return ErrorNode(location, callerScope);
        }

        var bindings = ImmutableDictionary.CreateBuilder<string, ParamBinding>(StringComparer.Ordinal);
        var list = new List<ParamBinding>();
        for (var i = 0; i < formals.Length; i++)
        {
            var b = new ParamBinding(formals[i], actuals[i], callerScope);
            bindings[formals[i].Name.Text] = b;
            list.Add(b);
        }

        var defScope = new Scope(ta.Module, bindings.ToImmutable(), callerScope.Depth + 1);
        foreach (var b in list)
        {
            b.DefinitionScope = defScope;
        }

        AsnType node;
        if (ta.Syntax is SetAssignmentSyntax set)
        {
            node = ResolveHeader(set.Governor, defScope);
            State(node).PendingConstraints.Add((set.Elements, defScope, set.Elements.Location));
        }
        else
        {
            node = ResolveHeader(ta.TypeSyntax!, defScope);
        }

        node.InstanceOf = ta;
        node.Documentation ??= ta.Documentation;
        return node;
    }

    private AsnType ResolveParameterAsType(ParamBinding binding, TypeReferenceSyntax r, Scope scope)
    {
        var actual = binding.Actual;
        if (binding.Formal.Governor is { } governorSyntax && char.IsUpper(binding.Formal.Name.Text[0]))
        {
            // Value set parameter: governor type constrained by the actual set.
            var governor = ResolveHeader(governorSyntax, binding.DefinitionScope ?? scope);
            if (actual.AsValue is BracedValueSyntax braced)
            {
                var specs = ParseBracedElementSet(braced);
                if (specs is not null)
                {
                    State(governor).PendingConstraints.Add((specs, binding.CallerScope, braced.Location));
                }

                return governor;
            }

            if (actual.AsType is not null)
            {
                var node = ResolveHeader(actual.AsType, binding.CallerScope);
                return node;
            }
        }

        if (actual.AsType is null)
        {
            Report(DiagnosticCatalog.WrongReferenceKind, actual.Location, actual.Source.GetText(actual.Span), "value", "type");
            return ErrorNode(r.Location, scope);
        }

        return ResolveHeader(actual.AsType, binding.CallerScope);
    }

    /// <summary>Re-parses a braced value as an element set (value sets, object sets).</summary>
    private ElementSetSpecsSyntax? ParseBracedElementSet(BracedValueSyntax braced)
    {
        var parser = Parser.ForTokenRange(braced.Lex, braced.OpenBrace, braced.CloseBrace + 1, _diagnostics, _options.Parser);
        var result = parser.TryRun(p => p.ParseBracedElementSetSpecs());
        if (result is not null && !parser.IsAtEnd)
        {
            parser.ReportUnexpected("'}'");
        }

        return result;
    }

    // ------------------------------------------------------------------ tagging

    private void ApplyTag(AsnType node, TaggedTypeSyntax t, Scope scope, bool innerIsDummy)
    {
        var number = ResolveTagNumber(t.Number, scope);
        var cls = t.TagClass switch
        {
            TagClassSyntax.Universal => TagClass.Universal,
            TagClassSyntax.Application => TagClass.Application,
            TagClassSyntax.Private => TagClass.Private,
            _ => TagClass.ContextSpecific,
        };
        var tag = new Asn1Tag(cls, number);
        var isExplicit = t.Mode switch
        {
            TaggingMode.Explicit => true,
            TaggingMode.Implicit => false,
            _ => scope.Module.TagDefault is TagDefault.Explicit or TagDefault.Unspecified,
        };
        ApplyTag(node, tag, isExplicit, t.Mode == TaggingMode.Implicit, innerIsDummy, t.Location);
    }

    private void ApplyTag(AsnType node, Asn1Tag tag, bool isExplicit, bool implicitWritten, bool innerIsDummy, SourceLocation location)
    {
        if (node.Kind == TypeKind.Error)
        {
            return;
        }

        var untagged = node.Tags.IsDefaultOrEmpty;
        if (!isExplicit && (untagged || innerIsDummy))
        {
            if (implicitWritten)
            {
                Report(DiagnosticCatalog.ImplicitTagOnChoice, location, innerIsDummy ? "a dummy reference" : node.Kind == TypeKind.Choice ? "an untagged CHOICE" : "an open type");
            }

            isExplicit = true;
        }

        if (isExplicit)
        {
            node.Tags = node.Tags.IsDefaultOrEmpty ? [new TagInfo(tag, true)] : node.Tags.Insert(0, new TagInfo(tag, true));
        }
        else
        {
            node.Tags = node.Tags.SetItem(0, new TagInfo(tag, node.Tags[0].IsExplicit));
        }
    }

    private int ResolveTagNumber(ValueSyntax value, Scope scope)
    {
        var v = ResolveValue(value, IntegerType, scope);
        if (v is IntegerValue iv && iv.Value >= 0 && iv.Value <= int.MaxValue)
        {
            return (int)iv.Value;
        }

        if (v is not ErrorValue)
        {
            Report(DiagnosticCatalog.InvalidTagNumber, value.Location, v.ToString());
        }

        return 0;
    }

    // ------------------------------------------------------------------ selection, fields, instance of

    private AsnType ResolveSelection(SelectionTypeSyntax s, Scope scope)
    {
        var choice = ResolveHeader(s.ChoiceType, scope);
        if (choice.Kind != TypeKind.Choice)
        {
            if (choice.Kind != TypeKind.Error)
            {
                Report(DiagnosticCatalog.InvalidSelection, s.Location, $"'{choice.DisplayName}' is not a CHOICE type");
            }

            return ErrorNode(s.Location, scope);
        }

        EnsureStructureNow(choice.Definition);
        var alt = choice.Components.FirstOrDefault(c => c.Name == s.Alternative.Text);
        if (alt is null)
        {
            Report(DiagnosticCatalog.InvalidSelection, s.Alternative.Location, $"'{s.Alternative.Text}' is not an alternative of '{choice.DisplayName}'");
            return ErrorNode(s.Location, scope);
        }

        return CopyNode(alt.Type, s.Location, s, scope);
    }

    /// <summary>Creates a fresh occurrence node that shares the definition (and inherits the constraints) of <paramref name="original"/>.</summary>
    private AsnType CopyNode(AsnType original, SourceLocation location, TypeSyntax? syntax, Scope scope)
    {
        var node = NewNode(original.Kind, location, syntax, scope);
        node.Reference = original.Reference;
        node.Target = original;
        node.Tags = original.Tags;
        State(node).StructureDone = true;
        return node;
    }

    private void EnsureStructureNow(AsnType node)
    {
        EnsureStructure(node.Definition);
    }

    // ------------------------------------------------------------------ structures

    private void EnsureStructure(AsnType node)
    {
        if (!_nodeStates.TryGetValue(node, out var st) || st.StructureDone)
        {
            return;
        }

        if (st.StructureInProgress)
        {
            Report(DiagnosticCatalog.IllegalRecursion, node.Location, $"'{node.DisplayName}' includes itself through COMPONENTS OF or a selection type");
            return;
        }

        st.StructureInProgress = true;
        try
        {
            switch (st.Syntax)
            {
                case IntegerTypeSyntax i:
                    node.SetNamedNumbers(ResolveNamedNumbers(i.NamedNumbers, st.Scope, bits: false));
                    break;
                case BitStringTypeSyntax b:
                    node.SetNamedNumbers(ResolveNamedNumbers(b.NamedBits, st.Scope, bits: true));
                    break;
                case EnumeratedTypeSyntax e:
                    ResolveEnumerated(node, e, st.Scope);
                    break;
                case StructuredTypeSyntax s:
                    ResolveComponents(node, s, st.Scope);
                    break;
                case CollectionTypeSyntax c:
                    node._elementType = ResolveHeader(c.ElementType, st.Scope);
                    node._elementName = c.ElementName?.Text;
                    break;
            }

            st.StructureWork?.Invoke();
        }
        finally
        {
            st.StructureInProgress = false;
            st.StructureDone = true;
            node.IsComplete = true;
        }
    }

    private ImmutableArray<NamedNumber> ResolveNamedNumbers(ImmutableArray<NamedNumberSyntax> items, Scope scope, bool bits)
    {
        var result = ImmutableArray.CreateBuilder<NamedNumber>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var values = new Dictionary<BigInteger, string>();
        foreach (var item in items)
        {
            var v = item.Value is null ? null : ResolveValue(item.Value, IntegerType, scope) as IntegerValue;
            if (v is null)
            {
                continue;
            }

            if (bits && v.Value < 0)
            {
                Report(DiagnosticCatalog.ValueTypeMismatch, item.Value!.Location, "named bit", "bit numbers must be non-negative");
                continue;
            }

            if (!names.Add(item.Name.Text))
            {
                Report(DiagnosticCatalog.DuplicateNamedNumber, item.Name.Location, $"identifier '{item.Name.Text}'");
                continue;
            }

            if (!values.TryAdd(v.Value, item.Name.Text))
            {
                Report(DiagnosticCatalog.DuplicateNamedNumber, item.Value!.Location, $"value {v.Value} ('{item.Name.Text}' and '{values[v.Value]}')");
                continue;
            }

            result.Add(new NamedNumber(item.Name.Text, v.Value, item.Name.Location, item.Documentation));
        }

        return result.ToImmutable();
    }

    private void ResolveEnumerated(AsnType node, EnumeratedTypeSyntax e, Scope scope)
    {
        var items = ImmutableArray.CreateBuilder<EnumItem>();
        var names = new Dictionary<string, SourceLocation>(StringComparer.Ordinal);
        var used = new Dictionary<BigInteger, string>();

        BigInteger? Explicit(NamedNumberSyntax item) =>
            item.Value is null ? null : (ResolveValue(item.Value, IntegerType, scope) as IntegerValue)?.Value;

        void CheckName(NamedNumberSyntax item)
        {
            if (!names.TryAdd(item.Name.Text, item.Name.Location))
            {
                _diagnostics.Add(Diagnostic.Create(DiagnosticCatalog.DuplicateEnumIdentifier, item.Name.Location, [new RelatedLocation(names[item.Name.Text], "previous definition")], item.Name.Text));
            }
        }

        // Root: explicit numbers first, then successive integers from 0 skipping used values (X.680 20.3).
        var rootExplicit = new Dictionary<NamedNumberSyntax, BigInteger>(ReferenceEqualityComparer.Instance);
        foreach (var item in e.RootItems)
        {
            if (Explicit(item) is { } v)
            {
                rootExplicit[item] = v;
                if (!used.TryAdd(v, item.Name.Text))
                {
                    Report(DiagnosticCatalog.DuplicateEnumValue, item.Value!.Location, v, item.Name.Text, used[v]);
                }
            }
        }

        BigInteger next = 0;
        foreach (var item in e.RootItems)
        {
            CheckName(item);
            if (!rootExplicit.TryGetValue(item, out var value))
            {
                while (used.ContainsKey(next))
                {
                    next++;
                }

                value = next;
                used[value] = item.Name.Text;
            }

            items.Add(new EnumItem(item.Name.Text, value, false, item.Name.Location, item.Documentation));
        }

        // Additions: each value must exceed all previous additions; implicit values are the least integer
        // not used in the root that is greater than all previous additions (X.680 20.4).
        BigInteger? lastAddition = null;
        foreach (var item in e.AdditionItems)
        {
            CheckName(item);
            BigInteger value;
            if (Explicit(item) is { } v)
            {
                value = v;
                if (lastAddition is not null && value <= lastAddition)
                {
                    Report(DiagnosticCatalog.InvalidExtension, item.Value!.Location, $"the value {value} of additional enumeration '{item.Name.Text}' must be greater than all previous additional values");
                }
            }
            else
            {
                value = lastAddition is null ? 0 : lastAddition.Value + 1;
                while (used.ContainsKey(value))
                {
                    value++;
                }
            }

            if (!used.TryAdd(value, item.Name.Text))
            {
                Report(DiagnosticCatalog.DuplicateEnumValue, (item.Value ?? (SyntaxNode)item.Name).Location, value, item.Name.Text, used[value]);
            }

            lastAddition = value;
            items.Add(new EnumItem(item.Name.Text, value, true, item.Name.Location, item.Documentation));
        }

        node.SetEnumItems(items.ToImmutable());
    }

    private void ResolveComponents(AsnType node, StructuredTypeSyntax s, Scope scope)
    {
        var components = new List<AsnComponent>();
        var markers = 0;
        var additionIndex = 0;
        var anyTagged = false;
        var kindName = s.Kind == StructuredKind.Choice ? "CHOICE" : s.Kind == StructuredKind.Set ? "SET" : "SEQUENCE";

        AsnComponent MakeComponent(NamedComponentSyntax nc)
        {
            if (nc.Type is TaggedTypeSyntax)
            {
                anyTagged = true;
            }

            var type = ResolveHeader(nc.Type, scope);
            if (type.Documentation is null && !type.IsReference)
            {
                type.Documentation = nc.Documentation;
            }

            var comp = new AsnComponent(nc.Name.Text, type, nc.Presence switch
            {
                ComponentPresence.Optional => Presence.Optional,
                ComponentPresence.Default => Presence.Default,
                _ => Presence.Required,
            }, nc.Name.Location, nc.Documentation);
            if (nc.DefaultValue is not null)
            {
                _pendingDefaults.Add((comp, nc.DefaultValue, scope));
            }

            return comp;
        }

        foreach (var item in s.Items)
        {
            switch (item)
            {
                case ExtensionMarkerSyntax marker:
                    markers++;
                    if (markers > 2)
                    {
                        Report(DiagnosticCatalog.InvalidExtension, marker.Location, $"a {kindName} may contain at most two extension markers");
                    }
                    else if (markers == 2 && s.Kind == StructuredKind.Choice)
                    {
                        // CHOICE permits a closing marker but no second root part.
                    }

                    break;
                case NamedComponentSyntax nc:
                    {
                        var comp = MakeComponent(nc);
                        if (markers == 1)
                        {
                            comp.ExtensionAdditionIndex = additionIndex++;
                        }
                        else if (markers >= 2)
                        {
                            if (s.Kind == StructuredKind.Choice)
                            {
                                Report(DiagnosticCatalog.InvalidExtension, nc.Location, "a CHOICE cannot have alternatives after a second extension marker");
                            }

                            comp.IsInRootPart2 = true;
                        }

                        components.Add(comp);
                        break;
                    }

                case ExtensionGroupSyntax g:
                    {
                        if (markers != 1)
                        {
                            Report(DiagnosticCatalog.InvalidExtension, g.Location, "extension addition groups [[ ]] are only allowed among extension additions");
                        }

                        BigInteger? version = null;
                        if (g.Version is not null && ResolveValue(g.Version, IntegerType, scope) is IntegerValue vv)
                        {
                            version = vv.Value;
                            if (vv.Value < 2)
                            {
                                Report(DiagnosticCatalog.InvalidExtension, g.Version.Location, "a version number must be 2 or greater");
                            }
                        }

                        var index = markers == 1 ? additionIndex++ : -1;
                        foreach (var gi in g.Items)
                        {
                            if (gi is NamedComponentSyntax gnc)
                            {
                                var comp = MakeComponent(gnc);
                                comp.ExtensionAdditionIndex = index;
                                comp.IsInExtensionGroup = index >= 0;
                                comp.GroupVersion = version;
                                components.Add(comp);
                            }
                            else if (gi is ComponentsOfSyntax gco)
                            {
                                foreach (var copied in ExpandComponentsOf(gco, node, scope))
                                {
                                    copied.ExtensionAdditionIndex = index;
                                    copied.IsInExtensionGroup = index >= 0;
                                    copied.GroupVersion = version;
                                    components.Add(copied);
                                }
                            }
                        }

                        break;
                    }

                case ComponentsOfSyntax co:
                    foreach (var copied in ExpandComponentsOf(co, node, scope))
                    {
                        if (markers == 1)
                        {
                            copied.ExtensionAdditionIndex = additionIndex++;
                        }
                        else if (markers >= 2)
                        {
                            copied.IsInRootPart2 = true;
                        }

                        components.Add(copied);
                    }

                    break;
            }
        }

        if (s.Kind == StructuredKind.Choice && components.Count == 0)
        {
            Report(DiagnosticCatalog.EmptyChoice, s.Location);
        }

        // Duplicate identifiers.
        var seen = new Dictionary<string, AsnComponent>(StringComparer.Ordinal);
        foreach (var c in components)
        {
            if (!seen.TryAdd(c.Name, c))
            {
                _diagnostics.Add(Diagnostic.Create(DiagnosticCatalog.DuplicateComponent, c.Location, [new RelatedLocation(seen[c.Name].Location, "previous definition")], c.Name, kindName));
            }
        }

        // Automatic tagging (X.680 25.3, 29.3): decided before COMPONENTS OF expansion, applied after it.
        if (scope.Module.TagDefault == TagDefault.Automatic && !anyTagged)
        {
            var ordered = components.Where(c => !c.IsExtensionAddition).Concat(components.Where(c => c.IsExtensionAddition)).ToList();
            for (var i = 0; i < ordered.Count; i++)
            {
                var c = ordered[i];
                var isDummy = c.Type.Syntax is TypeReferenceSyntax { Module: null } r && scope.Parameters.ContainsKey(r.Name.Text);
                if (c.FromComponentsOf)
                {
                    // Copied components get a fresh node so the copy can be re-tagged.
                    c.Type = CopyNode(c.Type, c.Location, c.Type.Syntax, scope);
                }

                ApplyTag(c.Type, Asn1Tag.Context(i), isExplicit: false, implicitWritten: false, innerIsDummy: isDummy, c.Location);
            }
        }

        node.SetComponents([.. components]);
    }

    private readonly List<(AsnComponent Component, ValueSyntax Value, Scope Scope)> _pendingDefaults = [];

    private IEnumerable<AsnComponent> ExpandComponentsOf(ComponentsOfSyntax co, AsnType owner, Scope scope)
    {
        var included = ResolveHeader(co.Type, scope);
        if (included.Kind == TypeKind.Error)
        {
            yield break;
        }

        if (included.Kind != owner.Kind)
        {
            Report(DiagnosticCatalog.InvalidComponentsOf, co.Type.Location, AsnType.KindName(owner.Kind), included.DisplayName, AsnType.KindName(included.Kind));
            yield break;
        }

        EnsureStructureNow(included);
        foreach (var c in included.Components.Where(c => !c.IsExtensionAddition))
        {
            var copy = new AsnComponent(c.Name, c.Type, c.Presence, c.Location, c.Documentation)
            {
                FromComponentsOf = true,
                DefaultValue = c.DefaultValue,
            };
            var pending = _pendingDefaults.FirstOrDefault(p => ReferenceEquals(p.Component, c));
            if (pending.Component is not null)
            {
                _pendingDefaults.Add((copy, pending.Value, pending.Scope));
            }

            yield return copy;
        }
    }

    private AsnType ResolveInstanceOf(InstanceOfTypeSyntax io, Scope scope)
    {
        // X.681 Annex C: INSTANCE OF C is SEQUENCE { type-id C.&id, value [0] EXPLICIT C.&Type } tagged [UNIVERSAL 8].
        var node = NewNode(TypeKind.Sequence, io.Location, io, scope);
        node.Tags = [new TagInfo(Asn1Tag.Universal(UniversalTagNumber.External), false)];
        var cls = ResolveClassReference(io.ClassReference, scope);
        var typeId = NewNode(TypeKind.ObjectIdentifier, io.Location, null, scope);
        typeId.Tags = [new TagInfo(Asn1Tag.ObjectIdentifier, false)];
        State(typeId).StructureDone = true;
        var value = NewNode(TypeKind.OpenType, io.Location, null, scope);
        value._openType = new OpenTypeInfo { Class = cls, FieldName = "&Type", RelatedComponentPath = ["type-id"] };
        value.Tags = [new TagInfo(Asn1Tag.Context(0), true)];
        State(value).StructureDone = true;
        node.SetComponents([
            new AsnComponent("type-id", typeId, Presence.Required, io.Location, null),
            new AsnComponent("value", value, Presence.Required, io.Location, null),
        ]);
        State(node).StructureDone = true;
        node.IsComplete = true;
        return node;
    }
}
