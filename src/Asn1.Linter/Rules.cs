using System.Numerics;
using System.Text.RegularExpressions;
using Asn1.Constraints;
using Asn1.Diagnostics;
using Asn1.Semantics;
using Asn1.Syntax;
using static Asn1.Diagnostics.DiagnosticCatalog;

namespace Asn1.Linter;

/// <summary>Built-in rules.</summary>
public static partial class BuiltInRules
{
    /// <summary>All built-in rules.</summary>
    public static IReadOnlyList<LintRule> All { get; } =
    [
        new NamingRule(),
        new UnusedRule(),
        new RedundantAliasRule(),
        new ConfusingNamesRule(),
        new CSharpCollisionRule(),
        new NestingRule(),
        new ExtensibilityRule(),
        new PerRule(),
        new RedundantConstraintRule(),
        new TagDefaultRule(),
        new EncodingCompatibilityRule(),
    ];

    [GeneratedRegex("^[A-Z][A-Za-z0-9]*$")]
    internal static partial Regex UpperCamel();

    [GeneratedRegex("^[A-Z][A-Za-z0-9]*(-[A-Za-z0-9]+)*$")]
    internal static partial Regex UpperCamelHyphen();

    [GeneratedRegex("^[a-z][A-Za-z0-9]*$")]
    internal static partial Regex LowerCamel();

    [GeneratedRegex("^[a-z][A-Za-z0-9]*(-[A-Za-z0-9]+)*$")]
    internal static partial Regex LowerCamelHyphen();

    internal static IEnumerable<(AsnType Type, string Path)> InlineTypes(AsnType root, string path, int depth = 0)
    {
        if (depth > 32)
        {
            yield break;
        }

        foreach (var c in root.Definition.Components)
        {
            if (!c.Type.IsReference)
            {
                yield return (c.Type, path + "." + c.Name);
                foreach (var x in InlineTypes(c.Type, path + "." + c.Name, depth + 1))
                {
                    yield return x;
                }
            }
        }

        if (root.Definition.ElementType is { IsReference: false } e)
        {
            yield return (e, path + ".*");
            foreach (var x in InlineTypes(e, path + ".*", depth + 1))
            {
                yield return x;
            }
        }
    }

    internal static IEnumerable<TypeAssignmentSymbol> Types(LintContext c) => c.Compilation.TypeAssignments.Where(t => t.Type is not null);
}

/// <summary>ASN5001/ASN5002: naming conventions.</summary>
internal sealed class NamingRule : LintRule
{
    public override IReadOnlyList<DiagnosticDescriptor> Descriptors => [TypeNaming, IdentifierNaming];

    public override void Analyze(LintContext context)
    {
        var cfg = context.Configuration;
        foreach (var module in context.Compilation.Modules)
        {
            foreach (var a in module.Assignments)
            {
                if (a is TypeAssignmentSymbol && !Matches(a.Name, cfg.TypeNaming, upper: true))
                {
                    context.Report(TypeNaming, a.Location, a.Name, Describe(cfg.TypeNaming, upper: true));
                }
                else if (a is ValueAssignmentSymbol && !Matches(a.Name, cfg.IdentifierNaming, upper: false))
                {
                    context.Report(IdentifierNaming, a.Location, a.Name, Describe(cfg.IdentifierNaming, upper: false));
                }

                if (a is TypeAssignmentSymbol { Type: { } t })
                {
                    CheckMembers(context, t);
                    foreach (var (inline, _) in BuiltInRules.InlineTypes(t, a.Name))
                    {
                        CheckMembers(context, inline);
                    }
                }
            }
        }
    }

    private static void CheckMembers(LintContext context, AsnType t)
    {
        if (t.IsReference)
        {
            return;
        }

        var cfg = context.Configuration;
        foreach (var c in t.Components)
        {
            if (!Matches(c.Name, cfg.IdentifierNaming, upper: false))
            {
                context.Report(IdentifierNaming, c.Location, c.Name, Describe(cfg.IdentifierNaming, upper: false));
            }
        }

        foreach (var i in t.EnumItems)
        {
            if (!Matches(i.Name, cfg.IdentifierNaming, upper: false))
            {
                context.Report(IdentifierNaming, i.Location, i.Name, Describe(cfg.IdentifierNaming, upper: false));
            }
        }
    }

    private static bool Matches(string name, NamingStyle style, bool upper) => style switch
    {
        NamingStyle.Camel => (upper ? BuiltInRules.UpperCamel() : BuiltInRules.LowerCamel()).IsMatch(name),
        NamingStyle.CamelWithHyphens => (upper ? BuiltInRules.UpperCamelHyphen() : BuiltInRules.LowerCamelHyphen()).IsMatch(name),
        _ => true,
    } || (upper && name.All(ch => char.IsUpper(ch) || char.IsDigit(ch) || ch == '-'));

    private static string Describe(NamingStyle style, bool upper) => style switch
    {
        NamingStyle.Camel => upper ? "UpperCamelCase" : "lowerCamelCase",
        _ => upper ? "UpperCamelCase (optionally hyphenated)" : "lowerCamelCase (optionally hyphenated)",
    };
}

/// <summary>ASN5003/ASN5004/ASN5020: unused definitions, imports and values.</summary>
internal sealed class UnusedRule : LintRule
{
    public override IReadOnlyList<DiagnosticDescriptor> Descriptors => [UnusedDefinition, UnusedImport, UnusedValue];

    public override void Analyze(LintContext context)
    {
        foreach (var module in context.Compilation.Modules)
        {
            var used = context.ReferencedNames(module);
            foreach (var imp in module.Imports)
            {
                if (!used.Contains(imp.Name) && !module.ExportedNames.Contains(imp.Name))
                {
                    context.Report(UnusedImport, imp.Syntax.Location, imp.Name);
                }
            }

            var checkTypes = !module.ExportsAll || context.Configuration.ReportUnreferencedWhenExportingAll;
            foreach (var a in module.Assignments)
            {
                if (used.Contains(a.Name) || (module.IsExported(a.Name) && !module.ExportsAll))
                {
                    continue;
                }

                if (a is ValueAssignmentSymbol)
                {
                    if (!module.ExportsAll || context.Configuration.ReportUnreferencedWhenExportingAll)
                    {
                        context.Report(UnusedValue, a.Location, a.Name);
                    }
                }
                else if (checkTypes && a is TypeAssignmentSymbol or ClassAssignmentSymbol or ObjectSetAssignmentSymbol)
                {
                    context.Report(UnusedDefinition, a.Location, a.Name);
                }
            }
        }
    }
}

/// <summary>ASN5005: plain aliases.</summary>
internal sealed class RedundantAliasRule : LintRule
{
    public override IReadOnlyList<DiagnosticDescriptor> Descriptors => [RedundantAlias];

    public override void Analyze(LintContext context)
    {
        foreach (var t in BuiltInRules.Types(context))
        {
            if (t.Syntax is TypeAssignmentSyntax { Type: TypeReferenceSyntax { ActualParameters: null } r } && !t.IsParameterized)
            {
                context.Report(RedundantAlias, t.Location, t.Name, r.ToString());
            }
        }
    }
}

/// <summary>ASN5006: names differing only by case or hyphens.</summary>
internal sealed class ConfusingNamesRule : LintRule
{
    public override IReadOnlyList<DiagnosticDescriptor> Descriptors => [ConfusingNames];

    public override void Analyze(LintContext context)
    {
        foreach (var module in context.Compilation.Modules)
        {
            Check(context, module.Assignments.Select(a => (a.Name, a.Location)));
            foreach (var t in module.TypeAssignments.Where(t => t.Type is not null))
            {
                Check(context, t.Type!.Definition.Components.Select(c => (c.Name, c.Location)));
                foreach (var (inline, _) in BuiltInRules.InlineTypes(t.Type!, t.Name))
                {
                    if (!inline.IsReference)
                    {
                        Check(context, inline.Components.Select(c => (c.Name, c.Location)));
                    }
                }
            }
        }
    }

    private static void Check(LintContext context, IEnumerable<(string Name, SourceLocation Location)> names)
    {
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, location) in names)
        {
            var key = name.Replace("-", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
            if (seen.TryGetValue(key, out var other) && other != name)
            {
                context.Report(ConfusingNames, location, name, other);
            }
            else
            {
                seen.TryAdd(key, name);
            }
        }
    }
}

/// <summary>ASN5007: names that collide after C# name mapping.</summary>
internal sealed class CSharpCollisionRule : LintRule
{
    public override IReadOnlyList<DiagnosticDescriptor> Descriptors => [CSharpNameCollision];

    public override void Analyze(LintContext context)
    {
        foreach (var module in context.Compilation.Modules)
        {
            Check(context, module.Assignments.Where(a => a is TypeAssignmentSymbol).Select(a => (a.Name, a.Location)));
            foreach (var t in module.TypeAssignments.Where(t => t.Type is not null && !t.Type.IsReference))
            {
                Check(context, t.Type!.Components.Select(c => (c.Name, c.Location)).Append((t.Name, t.Location)));
            }
        }
    }

    private static void Check(LintContext context, IEnumerable<(string Name, SourceLocation Location)> names)
    {
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, location) in names)
        {
            var cs = Pascal(name);
            if (seen.TryGetValue(cs, out var other) && other != name && other.ToUpperInvariant().Replace("-", string.Empty, StringComparison.Ordinal) != name.ToUpperInvariant().Replace("-", string.Empty, StringComparison.Ordinal))
            {
                context.Report(CSharpNameCollision, location, name, other, cs);
            }
            else
            {
                seen.TryAdd(cs, name);
            }
        }
    }

    private static string Pascal(string name) =>
        string.Concat(name.Split('-').Where(p => p.Length > 0).Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
}

/// <summary>ASN5008: deeply nested anonymous types.</summary>
internal sealed class NestingRule : LintRule
{
    public override IReadOnlyList<DiagnosticDescriptor> Descriptors => [ExcessiveNesting];

    public override void Analyze(LintContext context)
    {
        foreach (var t in BuiltInRules.Types(context))
        {
            var depth = Depth(t.Type!, 0);
            if (depth > context.Configuration.MaxAnonymousNesting)
            {
                context.Report(ExcessiveNesting, t.Location, t.Name, depth);
            }
        }
    }

    private static int Depth(AsnType t, int level)
    {
        if (level > 32)
        {
            return level;
        }

        var max = 0;
        foreach (var c in t.Definition.Components.Select(c => c.Type).Concat(t.Definition.ElementType is { } e ? [e] : []))
        {
            if (!c.IsReference && (c.Definition.IsStructured || c.Definition.IsCollection))
            {
                max = Math.Max(max, 1 + Depth(c, level + 1));
            }
        }

        return max;
    }
}

/// <summary>ASN5009/ASN5010: extensibility.</summary>
internal sealed class ExtensibilityRule : LintRule
{
    public override IReadOnlyList<DiagnosticDescriptor> Descriptors => [NotExtensible, SuspiciousExtension];

    public override void Analyze(LintContext context)
    {
        foreach (var t in BuiltInRules.Types(context))
        {
            var type = t.Type!;
            if (type.IsReference)
            {
                continue;
            }

            if (type.Kind is TypeKind.Sequence or TypeKind.Set or TypeKind.Choice or TypeKind.Enumerated && !type.IsExtensible)
            {
                context.Report(NotExtensible, t.Location, AsnType.KindName(type.Kind), t.Name);
            }

            foreach (var c in type.Components.Where(c => c.IsExtensionAddition && !c.IsOptional && !c.IsInExtensionGroup && type.Kind != TypeKind.Choice))
            {
                context.Report(SuspiciousExtension, c.Location, $"extension addition '{c.Name}' is mandatory; senders of earlier versions cannot provide it and decoders must treat it as optional");
            }

            if (type.Kind == TypeKind.Enumerated && type.IsExtensible && type.EnumItems.Count(i => !i.IsExtension) == 1 && !type.EnumItems.Any(i => i.IsExtension))
            {
                context.Report(SuspiciousExtension, t.Location, $"extensible ENUMERATED '{t.Name}' has a single root item");
            }
        }
    }
}

/// <summary>ASN5011/ASN5012/ASN5013: PER interoperability risks (only when PER is targeted).</summary>
internal sealed class PerRule : LintRule
{
    public override IReadOnlyList<DiagnosticDescriptor> Descriptors => [UnconstrainedInteger, EnumeratedIndexEncoding, FragmentationRisk];

    public override void Analyze(LintContext context)
    {
        if (!context.Configuration.TargetsPer)
        {
            return;
        }

        var reported = new HashSet<SourceLocation>();
        foreach (var t in BuiltInRules.Types(context))
        {
            Check(context, t.Type!, t.Name, reported);
            foreach (var (inline, path) in BuiltInRules.InlineTypes(t.Type!, t.Name))
            {
                Check(context, inline, path, reported);
            }
        }
    }

    private static void Check(LintContext context, AsnType t, string path, HashSet<SourceLocation> reported)
    {
        if (!reported.Add(t.Location))
        {
            return;
        }

        if (t.Kind == TypeKind.Integer && !t.IsReference && ConstraintSets.IntegerSerial(t.Constraints) is { } set && (set.LowerBound is null || set.UpperBound is null))
        {
            context.Report(UnconstrainedInteger, t.Location, path);
        }

        if (t.Kind == TypeKind.Enumerated && !t.IsReference && !t.IsExtensible)
        {
            context.Report(EnumeratedIndexEncoding, t.Location, path);
        }

        // X.691 11.9: lengths with an upper bound below 64K are constrained numbers and never fragment.
        if (t.Kind is TypeKind.OctetString or TypeKind.BitString or TypeKind.SequenceOf or TypeKind.SetOf or TypeKind.CharacterString && !t.IsReference)
        {
            var sizes = IntegerSet.Range(0, null);
            foreach (var c in t.Constraints)
            {
                if (c.Root is SizeElement s && ConstraintSets.IntegerRoot(s.Constraint) is { } sizeSet)
                {
                    sizes = sizes.Intersect(sizeSet);
                }
            }

            if (sizes.UpperBound is null || sizes.UpperBound >= 65536)
            {
                if (sizes.UpperBound is not null || t.Constraints.Length > 0)
                {
                    context.Report(FragmentationRisk, t.Location, path);
                }
            }
        }
    }
}

/// <summary>ASN5014: constraints that do not restrict the inherited value set.</summary>
internal sealed class RedundantConstraintRule : LintRule
{
    public override IReadOnlyList<DiagnosticDescriptor> Descriptors => [RedundantConstraint];

    public override void Analyze(LintContext context)
    {
        foreach (var node in context.Compilation.AllTypeNodes)
        {
            if (node.Kind != TypeKind.Integer || node.OwnConstraints.IsDefaultOrEmpty || node.Location.IsNone)
            {
                continue;
            }

            var inherited = node.Constraints.Take(node.Constraints.Length - node.OwnConstraints.Length).ToList();
            if (inherited.Count == 0)
            {
                continue;
            }

            var before = ConstraintSets.IntegerSerial(inherited);
            var after = ConstraintSets.IntegerSerial(node.Constraints);
            if (before is not null && after is not null && before.Equals(after) && !node.OwnConstraints.Any(c => c.IsExtensible))
            {
                context.Report(RedundantConstraint, node.Location, $"the constraint {string.Concat(node.OwnConstraints)} does not restrict the values of '{node.DisplayName}'");
            }
        }
    }
}

/// <summary>ASN5017: modules without tag default.</summary>
internal sealed class TagDefaultRule : LintRule
{
    public override IReadOnlyList<DiagnosticDescriptor> Descriptors => [ImplicitTagDefault];

    public override void Analyze(LintContext context)
    {
        foreach (var m in context.Compilation.Modules.Where(m => m.TagDefault == TagDefault.Unspecified))
        {
            context.Report(ImplicitTagDefault, m.Location, m.Name);
        }
    }
}

/// <summary>ASN6001: constructs the targeted encoding rules (as implemented) cannot encode.</summary>
internal sealed class EncodingCompatibilityRule : LintRule
{
    public override IReadOnlyList<DiagnosticDescriptor> Descriptors => [EncodingRuleIncompatible];

    public override void Analyze(LintContext context)
    {
        var rules = context.Configuration.EncodingRules;
        if (rules.Count == 0)
        {
            return;
        }

        foreach (var t in BuiltInRules.Types(context))
        {
            foreach (var rule in rules)
            {
                if (Problem(t.Type!, rule.ToUpperInvariant(), new HashSet<AsnType>(ReferenceEqualityComparer.Instance)) is { } problem)
                {
                    context.Report(EncodingRuleIncompatible, t.Location, t.Name, rule.ToUpperInvariant(), problem);
                }
            }
        }
    }

    private static string? Problem(AsnType t, string rule, HashSet<AsnType> seen)
    {
        if (!seen.Add(t.Definition))
        {
            return null;
        }

        switch (t.Kind)
        {
            case TypeKind.External or TypeKind.EmbeddedPdv or TypeKind.UnrestrictedCharacterString or TypeKind.OidIri or TypeKind.RelativeOidIri:
                return $"{AsnType.KindName(t.Kind)} is not supported";
            case TypeKind.Date or TypeKind.TimeOfDay or TypeKind.DateTime or TypeKind.Duration or TypeKind.Time when rule is "UPER" or "APER" or "PER":
                return $"the PER encoding of {AsnType.KindName(t.Kind)} (X.691 clause 32) is not implemented";
        }

        foreach (var c in t.Definition.Components)
        {
            if (Problem(c.Type, rule, seen) is { } p)
            {
                return p;
            }
        }

        return t.Definition.ElementType is { } e ? Problem(e, rule, seen) : null;
    }
}
