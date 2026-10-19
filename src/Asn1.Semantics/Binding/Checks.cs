using Asn1.Constraints;
using Asn1.Diagnostics;

namespace Asn1.Semantics.Binding;

internal sealed partial class Binder
{
    private void RunChecks()
    {
        var definitions = _allNodes.Where(n => n.Target is null && n.IsStructured).Distinct().ToList();
        foreach (var def in definitions)
        {
            CheckTags(def);
        }

        CheckFiniteness();
        CheckDefaults();
        CheckImportOids();
    }

    // ------------------------------------------------------------------ tag distinctness (X.680 25.5, 27.3, 29.2)

    /// <summary>The set of tags a component type can start with (untagged CHOICE expands to its alternatives).</summary>
    private static HashSet<Asn1Tag>? PossibleTags(AsnType type, int depth = 0)
    {
        if (type.Kind == TypeKind.Error || depth > 64)
        {
            return null;
        }

        if (!type.Tags.IsDefaultOrEmpty)
        {
            return [type.Tags[0].Tag];
        }

        if (type.Kind == TypeKind.Choice)
        {
            var set = new HashSet<Asn1Tag>();
            foreach (var alt in type.Components)
            {
                var inner = PossibleTags(alt.Type, depth + 1);
                if (inner is null)
                {
                    return null;
                }

                set.UnionWith(inner);
            }

            return set;
        }

        // Open types can start with any tag.
        return null;
    }

    private void CheckTags(AsnType def)
    {
        var comps = def.Components;
        if (comps.IsDefaultOrEmpty)
        {
            return;
        }

        var kindName = AsnType.KindName(def.Kind);
        var tags = comps.Select(c => PossibleTags(c.Type)).ToList();

        void Conflict(int a, int b, Asn1Tag tag)
        {
            _diagnostics.Add(Diagnostic.Create(DiagnosticCatalog.AmbiguousTags, comps[b].Location, [new RelatedLocation(comps[a].Location, "conflicting component")], tag, comps[b].Name, comps[a].Name, def.Assignment?.Name ?? kindName));
        }

        if (def.Kind is TypeKind.Set or TypeKind.Choice)
        {
            for (var i = 0; i < comps.Length; i++)
            {
                for (var j = i + 1; j < comps.Length; j++)
                {
                    if (tags[i] is { } ti && tags[j] is { } tj && ti.Overlaps(tj))
                    {
                        Conflict(i, j, ti.Intersect(tj).First());
                    }
                }
            }

            return;
        }

        // SEQUENCE: each OPTIONAL/DEFAULT component must be distinguishable from the following components up to
        // and including the next mandatory one; extension additions must be distinguishable from root part 2.
        for (var i = 0; i < comps.Length; i++)
        {
            if (!comps[i].IsOptional && !comps[i].IsExtensionAddition)
            {
                continue;
            }

            for (var j = i + 1; j < comps.Length; j++)
            {
                if (tags[i] is { } ti && tags[j] is { } tj && ti.Overlaps(tj))
                {
                    Conflict(i, j, ti.Intersect(tj).First());
                    break;
                }

                if (!comps[j].IsOptional && !comps[j].IsExtensionAddition)
                {
                    break;
                }
            }
        }
    }

    // ------------------------------------------------------------------ finiteness of recursive types

    /// <summary>
    /// Reports recursive definitions that have no finite value (for example a SEQUENCE with a mandatory
    /// component of its own type). Valid recursion goes through OPTIONAL components, CHOICE alternatives
    /// with a finite alternative, or collections that may be empty.
    /// </summary>
    private void CheckFiniteness()
    {
        var definitions = _allNodes.Where(n => n.Target is null && (n.IsStructured || n.IsCollection)).ToList();
        var finite = new HashSet<AsnType>(ReferenceEqualityComparer.Instance);
        bool changed;
        do
        {
            changed = false;
            foreach (var d in definitions)
            {
                if (finite.Contains(d))
                {
                    continue;
                }

                if (IsFinite(d, finite))
                {
                    finite.Add(d);
                    changed = true;
                }
            }
        }
        while (changed);

        foreach (var d in definitions.Where(d => !finite.Contains(d)))
        {
            Report(DiagnosticCatalog.IllegalRecursion, d.Location, $"'{d.DisplayName}' has no finite value; every value would contain itself");
        }
    }

    private static bool IsFinite(AsnType def, HashSet<AsnType> finite)
    {
        bool TypeFinite(AsnType t)
        {
            var d = t.Definition;
            return !(d.IsStructured || d.IsCollection) || finite.Contains(d);
        }

        switch (def.Kind)
        {
            case TypeKind.Sequence:
            case TypeKind.Set:
                return def.Components.All(c => c.IsOptional || c.IsExtensionAddition || TypeFinite(c.Type));
            case TypeKind.Choice:
                return def.Components.Length == 0 || def.Components.Any(c => TypeFinite(c.Type));
            case TypeKind.SequenceOf:
            case TypeKind.SetOf:
                {
                    if (def.ElementType is null || TypeFinite(def.ElementType))
                    {
                        return true;
                    }

                    // Finite only if the collection may be empty.
                    foreach (var c in def.Constraints)
                    {
                        if (c.Root is SizeElement s && ConstraintSets.IntegerRoot(s.Constraint) is { } set && !set.Contains(0))
                        {
                            return false;
                        }
                    }

                    return true;
                }

            default:
                return true;
        }
    }

    // ------------------------------------------------------------------ defaults

    private void CheckDefaults()
    {
        foreach (var def in _allNodes.Where(n => n.Target is null && n.Kind is TypeKind.Sequence or TypeKind.Set).Distinct())
        {
            foreach (var c in def.Components)
            {
                if (c.DefaultValue is null or ErrorValue)
                {
                    continue;
                }

                if (ValueSubject(c.DefaultValue) is { } subject && ConstraintEvaluator.CheckSerial(c.Type.Constraints, subject) == ConstraintCheck.Violation)
                {
                    Report(DiagnosticCatalog.DefaultViolatesConstraint, c.DefaultValue.Location, c.Name);
                }
            }
        }
    }

    /// <summary>Builds a constraint subject for simple values (null for structured values).</summary>
    internal static ConstraintSubject? ValueSubject(AsnValue value) => value switch
    {
        IntegerValue i => ConstraintSubject.FromInteger(i.Value),
        EnumeratedValue e => ConstraintSubject.FromInteger(e.Number),
        BooleanValue b => ConstraintSubject.FromBoolean(b.Value),
        RealValue r => ConstraintSubject.FromReal(r.Value),
        StringValue s => ConstraintSubject.FromString(s.Value),
        OctetStringValue o => ConstraintSubject.FromOctets(o.Value.ToArray()),
        BitStringValue bs => ConstraintSubject.FromBits(bs.Value),
        ObjectIdentifierValue oid => ConstraintSubject.FromObjectIdentifier(oid.Value),
        NullValue => ConstraintSubject.FromNull(),
        SequenceOfValue so => ConstraintSubject.FromCollection(so.Items.Length, () => so.Items.Select(i => ValueSubject(i) ?? ConstraintSubject.Opaque)),
        SequenceValue sv => ConstraintSubject.FromStructure(n => sv[n] is { } v ? ValueSubject(v) ?? ConstraintSubject.Opaque : null),
        ChoiceValue cv => ConstraintSubject.FromStructure(n => n == cv.Alternative ? ValueSubject(cv.Value) ?? ConstraintSubject.Opaque : null),
        _ => null,
    };

    private void CheckImportOids()
    {
        foreach (var module in _moduleList)
        {
            foreach (var group in module.Syntax.Imports)
            {
                if (group.ModuleOid is not { } comps || !_modules.TryGetValue(group.ModuleName.Text, out var source) || source.Oid is null)
                {
                    continue;
                }

                var arcs = new List<System.Numerics.BigInteger>();
                foreach (var c in comps)
                {
                    if (c.Number is Syntax.NumberValueSyntax n)
                    {
                        arcs.Add(n.Value);
                    }
                    else if (c.Name is not null && WellKnownOids.Lookup(arcs, c.Name.Text) is { } wk)
                    {
                        arcs.Add(wk);
                    }
                    else
                    {
                        arcs.Clear();
                        break;
                    }
                }

                if (arcs.Count > 0 && !arcs.SequenceEqual(source.Oid.Arcs))
                {
                    Report(DiagnosticCatalog.ModuleIdentifierMismatch, group.ModuleName.Location, group.ModuleName.Text);
                }
            }
        }
    }
}
