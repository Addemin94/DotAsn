using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;

namespace Asn1.CodeGen.TypeScript;

// A small TypeScript syntax model. The generator builds these nodes instead of writing source text, and
// TsPrinter turns them into deterministic, formatted source.

/// <summary>A TypeScript type.</summary>
public abstract record TsType;

/// <summary>A named type, e.g. <c>number</c>, <c>Heartbeat</c> or <c>$.BitString</c>.</summary>
public sealed record TsTypeName(string Name) : TsType;

/// <summary>A generic type, e.g. <c>$.Asn1Type&lt;Heartbeat&gt;</c>.</summary>
public sealed record TsGenericType(string Name, ImmutableArray<TsType> Arguments) : TsType;

/// <summary>An array type <c>T[]</c>.</summary>
public sealed record TsArrayType(TsType Element) : TsType;

/// <summary>A union type <c>A | B</c>.</summary>
public sealed record TsUnionType(ImmutableArray<TsType> Members) : TsType;

/// <summary>A string literal type <c>'red'</c>.</summary>
public sealed record TsLiteralType(string Value) : TsType;

/// <summary>An object type <c>{ a: T; b?: U }</c>.</summary>
public sealed record TsObjectType(ImmutableArray<TsProperty> Members) : TsType;

/// <summary>A property of an interface or object type.</summary>
public sealed record TsProperty(string Name, TsType Type, bool Optional, string? Documentation);

/// <summary>A TypeScript expression.</summary>
public abstract record TsExpression;

/// <summary>An identifier or dotted name, e.g. <c>Heartbeat</c> or <c>$.context</c>.</summary>
public sealed record TsIdentifier(string Name) : TsExpression;

/// <summary>A call <c>f(a, b)</c>.</summary>
public sealed record TsCall(TsExpression Callee, ImmutableArray<TsExpression> Arguments) : TsExpression;

/// <summary>A string literal.</summary>
public sealed record TsString(string Value) : TsExpression;

/// <summary>A numeric literal (already formatted), e.g. <c>42</c>, <c>-1.5</c>, <c>Infinity</c>.</summary>
public sealed record TsNumber(string Text) : TsExpression
{
    /// <summary>An integer literal.</summary>
    public static TsNumber Integer(BigInteger value) => new(value.ToString(CultureInfo.InvariantCulture));

    /// <summary>A double literal (special values as <c>Infinity</c>, <c>-Infinity</c>, <c>NaN</c>, <c>-0</c>).</summary>
    public static TsNumber Float(double value) => new(
        double.IsNaN(value) ? "NaN"
        : double.IsPositiveInfinity(value) ? "Infinity"
        : double.IsNegativeInfinity(value) ? "-Infinity"
        : value == 0 && double.IsNegative(value) ? "-0"
        : value.ToString("R", CultureInfo.InvariantCulture));
}

/// <summary>A bigint literal <c>42n</c>.</summary>
public sealed record TsBigInt(BigInteger Value) : TsExpression;

/// <summary><c>true</c> or <c>false</c>.</summary>
public sealed record TsBoolean(bool Value) : TsExpression;

/// <summary><c>null</c>.</summary>
public sealed record TsNull : TsExpression
{
    /// <summary>The literal.</summary>
    public static readonly TsNull Value = new();
}

/// <summary>An array literal.</summary>
public sealed record TsArray(ImmutableArray<TsExpression> Items) : TsExpression;

/// <summary>An object literal.</summary>
public sealed record TsObject(ImmutableArray<TsObjectMember> Members) : TsExpression;

/// <summary>A member of an object literal.</summary>
public sealed record TsObjectMember(string Key, TsExpression Value);

/// <summary>An arrow function with an expression body, e.g. <c>() =&gt; Heartbeat</c>.</summary>
public sealed record TsArrow(ImmutableArray<string> Parameters, TsExpression Body) : TsExpression;

/// <summary>A statement of a source file.</summary>
public abstract record TsStatement;

/// <summary>A line comment (each line is prefixed with <c>// </c>).</summary>
public sealed record TsComment(ImmutableArray<string> Lines) : TsStatement;

/// <summary><c>import * as Alias from 'module';</c></summary>
public sealed record TsNamespaceImport(string Alias, string Module) : TsStatement;

/// <summary><c>import { a, b } from 'module';</c></summary>
public sealed record TsNamedImport(ImmutableArray<string> Names, string Module) : TsStatement;

/// <summary><c>export interface Name { ... }</c></summary>
public sealed record TsInterface(string Name, ImmutableArray<TsProperty> Members, string? Documentation) : TsStatement;

/// <summary><c>export type Name = T;</c></summary>
public sealed record TsTypeAlias(string Name, TsType Type, string? Documentation) : TsStatement;

/// <summary><c>export const Name: T = value;</c></summary>
public sealed record TsConst(string Name, TsType? Annotation, TsExpression Value, string? Documentation) : TsStatement;

/// <summary>A source file.</summary>
public sealed record TsSourceFile(ImmutableArray<TsStatement> Statements);

/// <summary>Prints the syntax model as formatted TypeScript (2-space indentation, LF line endings).</summary>
public static partial class TsPrinter
{
    private const int MaxLine = 110;

    [GeneratedRegex(@"^[A-Za-z_$][A-Za-z0-9_$]*$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierRegex();

    /// <summary>True if <paramref name="name"/> can be used as an identifier or unquoted property name.</summary>
    public static bool IsIdentifierName(string name) => IdentifierRegex().IsMatch(name);

    /// <summary>Prints a source file.</summary>
    public static string Print(TsSourceFile file)
    {
        var sb = new StringBuilder();
        TsStatement? previous = null;
        foreach (var statement in file.Statements)
        {
            var groupedWithPrevious = (previous is TsNamespaceImport or TsNamedImport && statement is TsNamespaceImport or TsNamedImport)
                || (previous is TsInterface or TsTypeAlias && statement is TsConst c && c.Documentation is null);
            if (previous is not null && !groupedWithPrevious)
            {
                sb.Append('\n');
            }

            Statement(sb, statement);
            previous = statement;
        }

        return sb.ToString();
    }

    private static void Statement(StringBuilder sb, TsStatement statement)
    {
        switch (statement)
        {
            case TsComment c:
                foreach (var line in c.Lines)
                {
                    sb.Append(line.Length == 0 ? "//" : "// " + line).Append('\n');
                }

                break;
            case TsNamespaceImport i:
                sb.Append("import * as ").Append(i.Alias).Append(" from ").Append(Quote(i.Module)).Append(";\n");
                break;
            case TsNamedImport i:
                sb.Append("import { ").Append(string.Join(", ", i.Names)).Append(" } from ").Append(Quote(i.Module)).Append(";\n");
                break;
            case TsInterface i:
                Doc(sb, i.Documentation, 0);
                sb.Append("export interface ").Append(i.Name).Append(" {\n");
                foreach (var p in i.Members)
                {
                    Doc(sb, p.Documentation, 2);
                    sb.Append("  ").Append(Key(p.Name)).Append(p.Optional ? "?: " : ": ").Append(Type(p.Type, 2)).Append(";\n");
                }

                sb.Append("}\n");
                break;
            case TsTypeAlias a:
                {
                    Doc(sb, a.Documentation, 0);
                    var head = $"export type {a.Name} =";
                    var inline = InlineType(a.Type);
                    if (a.Type is TsUnionType u && head.Length + inline.Length + 2 > MaxLine)
                    {
                        sb.Append(head).Append('\n');
                        foreach (var m in u.Members)
                        {
                            sb.Append("  | ").Append(Type(m, 4)).Append('\n');
                        }

                        sb.Length--;
                        sb.Append(";\n");
                    }
                    else
                    {
                        sb.Append(head).Append(' ').Append(Type(a.Type, 0)).Append(";\n");
                    }

                    break;
                }

            case TsConst c:
                {
                    Doc(sb, c.Documentation, 0);
                    var head = $"export const {c.Name}" + (c.Annotation is null ? string.Empty : ": " + InlineType(c.Annotation)) + " = ";
                    sb.Append(head).Append(Expression(c.Value, 0, head.Length)).Append(";\n");
                    break;
                }

            default:
                throw new NotSupportedException(statement.GetType().Name);
        }
    }

    private static void Doc(StringBuilder sb, string? text, int indent)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var pad = new string(' ', indent);
        var lines = text.Replace("*/", "*\\/", StringComparison.Ordinal).Split('\n').Select(l => l.TrimEnd()).ToList();
        if (lines.Count == 1)
        {
            sb.Append(pad).Append("/** ").Append(lines[0]).Append(" */\n");
            return;
        }

        sb.Append(pad).Append("/**\n");
        foreach (var line in lines)
        {
            sb.Append(pad).Append(line.Length == 0 ? " *" : " * " + line).Append('\n');
        }

        sb.Append(pad).Append(" */\n");
    }

    private static string Key(string name) => IsIdentifierName(name) ? name : Quote(name);

    /// <summary>A single-quoted string literal.</summary>
    public static string Quote(string value)
    {
        var sb = new StringBuilder(value.Length + 2).Append('\'');
        foreach (var c in value)
        {
            switch (c)
            {
                case '\'': sb.Append("\\'"); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20 || c == 0x2028 || c == 0x2029 || (c >= 0x7f && c <= 0x9f))
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        return sb.Append('\'').ToString();
    }

    // ------------------------------------------------------------------ types

    private static string InlineType(TsType type) => type switch
    {
        TsTypeName n => n.Name,
        TsGenericType g => $"{g.Name}<{string.Join(", ", g.Arguments.Select(InlineType))}>",
        TsArrayType a => a.Element is TsUnionType ? $"({InlineType(a.Element)})[]" : InlineType(a.Element) + "[]",
        TsUnionType u => string.Join(" | ", u.Members.Select(InlineType)),
        TsLiteralType l => Quote(l.Value),
        TsObjectType o => o.Members.Length == 0 ? "{}" : "{ " + string.Join("; ", o.Members.Select(p => Key(p.Name) + (p.Optional ? "?: " : ": ") + InlineType(p.Type))) + " }",
        _ => throw new NotSupportedException(type.GetType().Name),
    };

    private static string Type(TsType type, int indent)
    {
        var inline = InlineType(type);
        if (indent + inline.Length <= MaxLine || type is not TsObjectType o)
        {
            return inline;
        }

        var pad = new string(' ', indent);
        var sb = new StringBuilder("{\n");
        foreach (var p in o.Members)
        {
            sb.Append(pad).Append("  ").Append(Key(p.Name)).Append(p.Optional ? "?: " : ": ").Append(Type(p.Type, indent + 2)).Append(";\n");
        }

        return sb.Append(pad).Append('}').ToString();
    }

    // ------------------------------------------------------------------ expressions

    private static string Inline(TsExpression e) => e switch
    {
        TsIdentifier i => i.Name,
        TsCall c => Inline(c.Callee) + "(" + string.Join(", ", c.Arguments.Select(Inline)) + ")",
        TsString s => Quote(s.Value),
        TsNumber n => n.Text,
        TsBigInt b => b.Value.ToString(CultureInfo.InvariantCulture) + "n",
        TsBoolean b => b.Value ? "true" : "false",
        TsNull => "null",
        TsArray a => "[" + string.Join(", ", a.Items.Select(Inline)) + "]",
        TsObject o => o.Members.Length == 0 ? "{}" : "{ " + string.Join(", ", o.Members.Select(m => Key(m.Key) + ": " + Inline(m.Value))) + " }",
        TsArrow f => (f.Parameters.Length == 1 ? f.Parameters[0] : "(" + string.Join(", ", f.Parameters) + ")") + " => " + (f.Body is TsObject ? "(" + Inline(f.Body) + ")" : Inline(f.Body)),
        _ => throw new NotSupportedException(e.GetType().Name),
    };

    /// <summary>Prints an expression that starts at column <paramref name="column"/> on a line indented by <paramref name="indent"/>.</summary>
    private static string Expression(TsExpression e, int indent, int column)
    {
        var inline = Inline(e);
        if (column + inline.Length <= MaxLine)
        {
            return inline;
        }

        var pad = new string(' ', indent);
        var inner = indent + 2;
        var innerPad = new string(' ', inner);
        switch (e)
        {
            case TsObject o:
                {
                    var sb = new StringBuilder("{\n");
                    foreach (var m in o.Members)
                    {
                        var key = Key(m.Key) + ": ";
                        sb.Append(innerPad).Append(key).Append(Expression(m.Value, inner, inner + key.Length)).Append(",\n");
                    }

                    return sb.Append(pad).Append('}').ToString();
                }

            case TsArray a:
                {
                    var sb = new StringBuilder("[\n");
                    foreach (var item in a.Items)
                    {
                        sb.Append(innerPad).Append(Expression(item, inner, inner)).Append(",\n");
                    }

                    return sb.Append(pad).Append(']').ToString();
                }

            case TsCall c:
                {
                    var callee = Inline(c.Callee);
                    if (c.Arguments.Length == 1)
                    {
                        return callee + "(" + Expression(c.Arguments[0], indent, column + callee.Length + 1) + ")";
                    }

                    var sb = new StringBuilder(callee).Append("(\n");
                    foreach (var arg in c.Arguments)
                    {
                        sb.Append(innerPad).Append(Expression(arg, inner, inner)).Append(",\n");
                    }

                    return sb.Append(pad).Append(')').ToString();
                }

            case TsArrow f:
                {
                    var head = (f.Parameters.Length == 1 ? f.Parameters[0] : "(" + string.Join(", ", f.Parameters) + ")") + " => ";
                    var body = Expression(f.Body, indent, column + head.Length + (f.Body is TsObject ? 1 : 0));
                    return head + (f.Body is TsObject ? "(" + body + ")" : body);
                }

            default:
                return inline;
        }
    }
}
