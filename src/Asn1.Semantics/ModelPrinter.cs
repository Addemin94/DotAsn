using System.Text;

namespace Asn1.Semantics;

/// <summary>Renders the resolved model as annotated ASN.1-like text (effective tags, constraints, extension metadata).</summary>
public static class ModelPrinter
{
    /// <summary>Renders every module of a compilation.</summary>
    public static string Print(Asn1Compilation compilation)
    {
        var sb = new StringBuilder();
        foreach (var m in compilation.Modules)
        {
            Print(sb, m);
        }

        return sb.ToString();
    }

    /// <summary>Renders one module.</summary>
    public static void Print(StringBuilder sb, ModuleSymbol module)
    {
        sb.Append(module.Name);
        if (module.Oid is not null)
        {
            sb.Append(" { ").Append(string.Join(' ', module.Oid.Arcs)).Append(" }");
        }

        sb.Append(" -- ").Append(module.TagDefault).AppendLine(" TAGS");
        foreach (var a in module.Assignments)
        {
            switch (a)
            {
                case TypeAssignmentSymbol { Type: { } t } ta:
                    sb.Append("  ").Append(ta.Name).Append(" ::= ");
                    PrintType(sb, t, 2, depth: 0);
                    sb.AppendLine();
                    break;
                case TypeAssignmentSymbol { IsParameterized: true } tp:
                    sb.Append("  ").Append(tp.Name).AppendLine("{...} -- parameterized");
                    break;
                case ValueAssignmentSymbol { Value: { } v, Type: { } vt } va:
                    sb.Append("  ").Append(va.Name).Append(' ').Append(vt.DisplayName).Append(" ::= ").AppendLine(v.ToString());
                    break;
                case ClassAssignmentSymbol { Class: { } c }:
                    sb.Append("  ").Append(a.Name).Append(" ::= CLASS { ").Append(string.Join(", ", c.Fields.Select(f => f.Name + ":" + f.Kind))).AppendLine(" }");
                    break;
                case ObjectSetAssignmentSymbol { ObjectSet: { } os }:
                    sb.Append("  ").Append(a.Name).Append(' ').Append(os.Class.Name).Append(" ::= { ").Append(os.Objects.Length).Append(" object(s)").Append(os.IsExtensible ? ", ..." : string.Empty).AppendLine(" }");
                    break;
                case ObjectAssignmentSymbol { Object: { } o }:
                    sb.Append("  ").Append(a.Name).Append(' ').Append(o.Class.Name).Append(" ::= { ").Append(string.Join(", ", o.Settings.Keys.Order(StringComparer.Ordinal))).AppendLine(" }");
                    break;
            }
        }
    }

    private static void PrintType(StringBuilder sb, AsnType t, int indent, int depth)
    {
        foreach (var tag in t.Tags)
        {
            sb.Append(tag.Tag).Append(tag.IsExplicit ? " EXPLICIT " : " ");
        }

        var def = t.Definition;
        var expand = depth == 0 || !t.IsReference;
        if (t.IsReference && !expand)
        {
            sb.Append(t.Reference?.Name ?? t.DisplayName);
        }
        else if (t.Kind is TypeKind.Sequence or TypeKind.Set or TypeKind.Choice && depth < 12)
        {
            if (t.IsReference)
            {
                sb.Append(t.Reference?.Name).Append(" = ");
            }

            sb.Append(AsnType.KindName(t.Kind)).AppendLine(" {");
            var markerPrinted = false;
            foreach (var c in def.Components)
            {
                if (c.IsExtensionAddition && !markerPrinted)
                {
                    sb.Append(' ', indent + 2).AppendLine("...,");
                    markerPrinted = true;
                }

                sb.Append(' ', indent + 2).Append(c.Name).Append(' ');
                PrintType(sb, c.Type, indent + 2, depth + 1);
                if (c.Presence == Presence.Optional)
                {
                    sb.Append(" OPTIONAL");
                }
                else if (c.Presence == Presence.Default)
                {
                    sb.Append(" DEFAULT ").Append(c.DefaultValue);
                }

                if (c.IsExtensionAddition)
                {
                    sb.Append(" -- addition #").Append(c.ExtensionAdditionIndex).Append(c.IsInExtensionGroup ? " (group)" : string.Empty);
                }

                if (c.IsInRootPart2)
                {
                    sb.Append(" -- root part 2");
                }

                sb.AppendLine(",");
            }

            if (def.IsExtensible && !markerPrinted)
            {
                sb.Append(' ', indent + 2).AppendLine("...");
            }

            sb.Append(' ', indent).Append('}');
        }
        else if (t.Kind is TypeKind.SequenceOf or TypeKind.SetOf && def.ElementType is { } e)
        {
            sb.Append(AsnType.KindName(t.Kind)).Append(' ');
            PrintType(sb, e, indent, depth + 1);
        }
        else if (t.Kind == TypeKind.Enumerated)
        {
            sb.Append("ENUMERATED { ").Append(string.Join(", ", def.EnumItems.Select(i => (i.IsExtension ? "+" : string.Empty) + i.Name + "(" + i.Value + ")"))).Append(def.IsExtensible ? ", ..." : string.Empty).Append(" }");
        }
        else
        {
            sb.Append(t.IsReference ? t.Reference?.Name ?? AsnType.KindName(t.Kind, t.StringKind) : AsnType.KindName(t.Kind, t.StringKind));
            if (def.NamedNumbers.Length > 0)
            {
                sb.Append(" { ").Append(string.Join(", ", def.NamedNumbers.Select(n => n.Name + "(" + n.Value + ")"))).Append(" }");
            }
        }

        foreach (var c in t.Constraints)
        {
            sb.Append(' ').Append(c);
        }
    }
}
