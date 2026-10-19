using System.Collections.Immutable;
using System.Numerics;
using Asn1.Constraints;
using Asn1.Diagnostics;
using Asn1.Syntax;

namespace Asn1.Semantics.Binding;

internal enum ConstraintContext
{
    Value,
    Size,
    Alphabet,
}

internal sealed partial class Binder
{
    private AsnType EnsureConstraints(AsnType node)
    {
        var st = State(node);
        if (st.ConstraintsDone)
        {
            return node;
        }

        if (st.ConstraintsInProgress)
        {
            Report(DiagnosticCatalog.IllegalRecursion, node.Location, $"the constraint of '{node.DisplayName}' depends on itself");
            return node;
        }

        st.ConstraintsInProgress = true;
        try
        {
            var inherited = node.Target is not null ? EnsureConstraints(node.Target).Constraints : [];
            var own = ImmutableArray.CreateBuilder<Asn1Constraint>();
            foreach (var (spec, scope, _) in st.PendingConstraints)
            {
                var c = ResolveConstraintSpec(spec, node, scope, ConstraintContext.Value);
                if (c is not null)
                {
                    own.Add(c);
                }
            }

            node.OwnConstraints = own.ToImmutable();
            node.Constraints = inherited.IsDefaultOrEmpty ? node.OwnConstraints : inherited.AddRange(node.OwnConstraints);
        }
        finally
        {
            st.ConstraintsInProgress = false;
            st.ConstraintsDone = true;
        }

        return node;
    }

    private Asn1Constraint? ResolveConstraintSpec(ConstraintSpecSyntax spec, AsnType parent, Scope scope, ConstraintContext ctx)
    {
        switch (spec)
        {
            case ElementSetSpecsSyntax es:
                {
                    var root = es.Root is null ? null : ResolveElementSet(es.Root, parent, scope, ctx);
                    var additions = es.Additions is null ? null : ResolveElementSet(es.Additions, parent, scope, ctx);
                    return new Asn1Constraint(root, es.IsExtensible, additions);
                }

            case TableConstraintSyntax t:
                ApplyTableConstraint(parent, t, scope);
                return new Asn1Constraint(new OpaqueElement("table", t.Source.GetText(t.Span)));

            case UserDefinedConstraintSyntax u:
                return new Asn1Constraint(new OpaqueElement("user-defined", u.Source.GetText(u.Span)));

            case ContentsConstraintSyntax c:
                if (parent.Kind is not (TypeKind.BitString or TypeKind.OctetString or TypeKind.Error))
                {
                    Report(DiagnosticCatalog.ConstraintNotApplicable, c.Location, "CONTAINING", parent.DisplayName);
                    return null;
                }

                if (c.ContainedType is not null)
                {
                    parent.ContainedType = ResolveHeader(c.ContainedType, scope);
                    DrainStructureQueue();
                }

                return new Asn1Constraint(new OpaqueElement("contents", c.Source.GetText(c.Span)));
            default:
                return null;
        }
    }

    private Asn1Constraint? ResolveConstraint(ConstraintSyntax c, AsnType parent, Scope scope, ConstraintContext ctx) =>
        ResolveConstraintSpec(c.Spec, parent, scope, ctx);

    private AsnType ValueTypeFor(ConstraintContext ctx, AsnType parent) => ctx == ConstraintContext.Size ? IntegerType : parent;

    private ConstraintElement ResolveElementSet(ElementSetSyntax e, AsnType parent, Scope scope, ConstraintContext ctx)
    {
        switch (e)
        {
            case UnionSyntax u:
                return new UnionElement([.. u.Items.Select(i => ResolveElementSet(i, parent, scope, ctx))]);
            case IntersectionSyntax x:
                return new IntersectionElement([.. x.Items.Select(i => ResolveElementSet(i, parent, scope, ctx))]);
            case ExceptSyntax ex:
                return new ExceptElement(ResolveElementSet(ex.Included, parent, scope, ctx), ResolveElementSet(ex.Excluded, parent, scope, ctx));
            case AllExceptSyntax ae:
                return new ExceptElement(AllValuesElement.Instance, ResolveElementSet(ae.Excluded, parent, scope, ctx));
            case NestedElementSetSyntax n:
                {
                    var inner = ResolveConstraintSpec(n.Inner, parent, scope, ctx);
                    return inner is null ? AllValuesElement.Instance : new NestedElement(inner);
                }

            case SingleValueSyntax sv:
                return ResolveSingleValue(sv, parent, scope, ctx);

            case ValueRangeSyntax r:
                return ResolveRange(r, parent, scope, ctx);

            case SizeConstraintSyntax s:
                {
                    if (ctx != ConstraintContext.Value || parent.Kind is not (TypeKind.BitString or TypeKind.OctetString or TypeKind.CharacterString or TypeKind.SequenceOf or TypeKind.SetOf or TypeKind.UnrestrictedCharacterString or TypeKind.Error))
                    {
                        Report(DiagnosticCatalog.ConstraintNotApplicable, s.Location, "SIZE", parent.DisplayName);
                        return AllValuesElement.Instance;
                    }

                    var inner = ResolveConstraint(s.Constraint, IntegerType, scope, ConstraintContext.Size);
                    if (inner is not null)
                    {
                        CheckNonNegativeSizes(inner, s.Constraint.Location);
                    }

                    return inner is null ? AllValuesElement.Instance : new SizeElement(inner);
                }

            case PermittedAlphabetSyntax pa:
                {
                    if (ctx == ConstraintContext.Size || parent.Kind is not (TypeKind.CharacterString or TypeKind.Error))
                    {
                        Report(DiagnosticCatalog.ConstraintNotApplicable, pa.Location, "FROM", parent.DisplayName);
                        return AllValuesElement.Instance;
                    }

                    var inner = ResolveConstraint(pa.Constraint, parent, scope, ConstraintContext.Alphabet);
                    return inner is null ? AllValuesElement.Instance : new PermittedAlphabetElement(inner);
                }

            case PatternConstraintSyntax p:
                {
                    if (parent.Kind is not (TypeKind.CharacterString or TypeKind.Error))
                    {
                        Report(DiagnosticCatalog.ConstraintNotApplicable, p.Location, "PATTERN", parent.DisplayName);
                        return AllValuesElement.Instance;
                    }

                    var value = ResolveValue(p.Pattern, StringPatternType(parent), scope);
                    if (value is not StringValue sv)
                    {
                        return AllValuesElement.Instance;
                    }

                    try
                    {
                        _ = new System.Text.RegularExpressions.Regex(Asn1RegexTranslator.Translate(sv.Value), System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1));
                    }
                    catch (ArgumentException ex)
                    {
                        Report(DiagnosticCatalog.InvalidPattern, p.Location, ex.Message);
                        return AllValuesElement.Instance;
                    }

                    return new PatternElement(sv.Value);
                }

            case PropertySettingsSyntax ps:
                return new OpaqueElement("settings", "SETTINGS \"" + ps.Settings + "\"");

            case SingleTypeConstraintSyntax it:
                {
                    if (parent.Kind is not (TypeKind.SequenceOf or TypeKind.SetOf or TypeKind.Error))
                    {
                        Report(DiagnosticCatalog.ConstraintNotApplicable, it.Location, "WITH COMPONENT", parent.DisplayName);
                        return AllValuesElement.Instance;
                    }

                    if (parent.Kind == TypeKind.Error)
                    {
                        return AllValuesElement.Instance;
                    }

                    EnsureStructureNow(parent);
                    var element = parent.ElementType!;
                    var inner = ResolveConstraint(it.Constraint, element, scope, ConstraintContext.Value);
                    return inner is null ? AllValuesElement.Instance : new InnerTypeElement(inner);
                }

            case MultipleTypeConstraintsSyntax mc:
                return ResolveInnerComponents(mc, parent, scope);

            case ContainedSubtypeSyntax cs:
                return ResolveContainedSubtype(cs, parent, scope, ctx);

            default:
                return AllValuesElement.Instance;
        }
    }

    private AsnType StringPatternType(AsnType parent) => parent;

    private void CheckNonNegativeSizes(Asn1Constraint c, SourceLocation location)
    {
        var set = ConstraintSets.IntegerRoot(c);
        if (set is not null && set.LowerBound is { } lb && lb < 0)
        {
            Report(DiagnosticCatalog.InvalidConstraintValue, location, "sizes must be non-negative");
        }
    }

    private ConstraintElement ResolveSingleValue(SingleValueSyntax sv, AsnType parent, Scope scope, ConstraintContext ctx)
    {
        if (parent.Kind == TypeKind.Error)
        {
            return AllValuesElement.Instance;
        }

        var valueType = ValueTypeFor(ctx, parent);
        if (parent.Kind == TypeKind.OpenType)
        {
            return new OpaqueElement("value", sv.Source.GetText(sv.Span));
        }

        var value = ResolveValue(sv.Value, valueType, scope);
        var obj = value.ToRuntimeObject();
        if (obj is null)
        {
            return value is ErrorValue ? AllValuesElement.Instance : new OpaqueElement("value", value.ToString());
        }

        return new SingleValueElement(obj);
    }

    private ConstraintElement ResolveRange(ValueRangeSyntax r, AsnType parent, Scope scope, ConstraintContext ctx)
    {
        if (parent.Kind == TypeKind.Error)
        {
            return AllValuesElement.Instance;
        }

        var applicable = ctx switch
        {
            ConstraintContext.Size => true,
            ConstraintContext.Alphabet => parent.Kind == TypeKind.CharacterString,
            _ => parent.Kind is TypeKind.Integer or TypeKind.Real or TypeKind.Date or TypeKind.TimeOfDay or TypeKind.DateTime or TypeKind.Duration or TypeKind.Time,
        };
        if (!applicable)
        {
            Report(DiagnosticCatalog.ConstraintNotApplicable, r.Location, "value range", parent.DisplayName);
            return AllValuesElement.Instance;
        }

        var valueType = ValueTypeFor(ctx, parent);
        object? Bound(RangeEndpointSyntax ep)
        {
            if (ep.Value is null)
            {
                return null;
            }

            var v = ResolveValue(ep.Value, valueType, scope);
            if (ctx == ConstraintContext.Alphabet && v is StringValue s && CharacterRepertoire.CountCharacters(s.Value) != 1)
            {
                Report(DiagnosticCatalog.InvalidConstraintValue, ep.Location, "permitted alphabet range endpoints must be single characters");
            }

            return v.ToRuntimeObject();
        }

        var lower = Bound(r.Lower);
        var upper = Bound(r.Upper);
        if (lower is BigInteger lo && upper is BigInteger hi)
        {
            var effLo = r.Lower.IsOpen ? lo + 1 : lo;
            var effHi = r.Upper.IsOpen ? hi - 1 : hi;
            if (effLo > effHi)
            {
                Report(DiagnosticCatalog.EmptyRange, r.Location, r.Source.GetText(r.Span));
            }
        }
        else if (lower is double dl && upper is double dh && dl > dh)
        {
            Report(DiagnosticCatalog.EmptyRange, r.Location, r.Source.GetText(r.Span));
        }

        return new ValueRangeElement(lower, upper, r.Lower.IsOpen, r.Upper.IsOpen);
    }

    private ConstraintElement ResolveInnerComponents(MultipleTypeConstraintsSyntax mc, AsnType parent, Scope scope)
    {
        if (parent.Kind is TypeKind.Real or TypeKind.UnrestrictedCharacterString or TypeKind.External or TypeKind.EmbeddedPdv)
        {
            // REAL (X.680 21.5), EXTERNAL, EMBEDDED PDV and CHARACTER STRING are constrained through their associated
            // SEQUENCE types. Such constraints are recorded as metadata.
            if (parent.Kind == TypeKind.Real)
            {
                foreach (var nc in mc.Constraints.Where(c => c.Name.Text is not ("mantissa" or "base" or "exponent")))
                {
                    Report(DiagnosticCatalog.UnknownComponentInConstraint, nc.Name.Location, nc.Name.Text, "REAL");
                }
            }

            return new OpaqueElement("associated-type", mc.Source.GetText(mc.Span));
        }

        if (parent.Kind is not (TypeKind.Sequence or TypeKind.Set or TypeKind.Choice))
        {
            if (parent.Kind != TypeKind.Error)
            {
                Report(DiagnosticCatalog.ConstraintNotApplicable, mc.Location, "WITH COMPONENTS", parent.DisplayName);
            }

            return AllValuesElement.Instance;
        }

        EnsureStructureNow(parent);
        var list = ImmutableArray.CreateBuilder<ComponentConstraint>();
        var mentioned = new HashSet<string>(StringComparer.Ordinal);
        foreach (var nc in mc.Constraints)
        {
            var comp = parent.Components.FirstOrDefault(c => c.Name == nc.Name.Text);
            if (comp is null)
            {
                Report(DiagnosticCatalog.UnknownComponentInConstraint, nc.Name.Location, nc.Name.Text, parent.DisplayName);
                continue;
            }

            if (!mentioned.Add(comp.Name))
            {
                Report(DiagnosticCatalog.DuplicateComponent, nc.Name.Location, comp.Name, "WITH COMPONENTS");
            }

            if (parent.Kind != TypeKind.Choice && nc.Presence is PresenceConstraint.Absent or PresenceConstraint.Optional && !comp.IsOptional)
            {
                Report(DiagnosticCatalog.InvalidPresenceConstraint, nc.Location, $"'{comp.Name}' is mandatory and cannot be constrained {nc.Presence.ToString().ToUpperInvariant()}");
            }

            var value = nc.ValueConstraint is null ? null : ResolveConstraint(nc.ValueConstraint, comp.Type, scope, ConstraintContext.Value);
            list.Add(new ComponentConstraint(comp.Name, value, nc.Presence switch
            {
                PresenceConstraint.Present => ComponentPresenceConstraint.Present,
                PresenceConstraint.Absent => ComponentPresenceConstraint.Absent,
                PresenceConstraint.Optional => ComponentPresenceConstraint.Optional,
                _ => ComponentPresenceConstraint.None,
            }));
        }

        if (!mc.IsPartial)
        {
            // Full specification (X.680 51.8.5): unmentioned optional components / alternatives are absent.
            foreach (var c in parent.Components)
            {
                if (!mentioned.Contains(c.Name) && (parent.Kind == TypeKind.Choice || c.IsOptional))
                {
                    list.Add(new ComponentConstraint(c.Name, null, ComponentPresenceConstraint.Absent));
                }
            }
        }

        return new InnerComponentsElement(mc.IsPartial, list.ToImmutable());
    }

    private ConstraintElement ResolveContainedSubtype(ContainedSubtypeSyntax cs, AsnType parent, Scope scope, ConstraintContext ctx)
    {
        if (parent.Kind == TypeKind.Error)
        {
            return AllValuesElement.Instance;
        }

        var contained = ResolveHeader(cs.Type, scope);
        DrainStructureQueue();
        if (contained.Kind == TypeKind.Error)
        {
            return AllValuesElement.Instance;
        }

        if (parent.Kind == TypeKind.OpenType)
        {
            return new OpaqueElement("type", contained.DisplayName);
        }

        var expectedKind = ctx == ConstraintContext.Size ? TypeKind.Integer : parent.Kind;
        if (contained.Kind != expectedKind && !(contained.IsStringLike && parent.IsStringLike))
        {
            Report(DiagnosticCatalog.ConstraintNotApplicable, cs.Location, $"contained subtype '{contained.DisplayName}'", parent.DisplayName);
            return AllValuesElement.Instance;
        }

        if (ctx == ConstraintContext.Alphabet)
        {
            // FROM(StringType): the alphabet is the set of characters of the contained type's permitted alphabet.
            EnsureConstraints(contained);
            var alphabets = contained.Constraints.SelectMany(c => c.Root is PermittedAlphabetElement pa ? [pa.Constraint] : Array.Empty<Asn1Constraint>()).ToList();
            return alphabets.Count == 0 ? AllValuesElement.Instance : new IntersectionElement([.. alphabets.Select(a => (ConstraintElement)new NestedElement(a, isContainedSubtype: true))]);
        }

        EnsureConstraints(contained);
        if (contained.Constraints.IsDefaultOrEmpty)
        {
            return AllValuesElement.Instance;
        }

        var elements = contained.Constraints.Select(c => (ConstraintElement)new NestedElement(c, isContainedSubtype: true)).ToImmutableArray();
        return elements.Length == 1 ? elements[0] : new IntersectionElement(elements);
    }
}
