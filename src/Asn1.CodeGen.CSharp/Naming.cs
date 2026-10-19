using System.Text;

namespace Asn1.CodeGen.CSharp;

/// <summary>Maps ASN.1 names to C# identifiers.</summary>
public static class Naming
{
    private static readonly HashSet<string> s_keywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class", "const", "continue",
        "decimal", "default", "delegate", "do", "double", "else", "enum", "event", "explicit", "extern", "false", "finally",
        "fixed", "float", "for", "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock",
        "long", "namespace", "new", "null", "object", "operator", "out", "override", "params", "private", "protected",
        "public", "readonly", "ref", "return", "sbyte", "sealed", "short", "sizeof", "stackalloc", "static", "string",
        "struct", "switch", "this", "throw", "true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort",
        "using", "virtual", "void", "volatile", "while", "field", "value",
    };

    /// <summary>True for C# keywords.</summary>
    public static bool IsKeyword(string name) => s_keywords.Contains(name);

    /// <summary>Converts an ASN.1 reference to PascalCase: hyphen-separated parts are capitalized and joined.</summary>
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
        if (result.Length == 0 || !char.IsLetter(result[0]))
        {
            result = "N" + result;
        }

        return result;
    }

    /// <summary>Converts to camelCase.</summary>
    public static string Camel(string name)
    {
        var p = Pascal(name);
        var c = char.ToLowerInvariant(p[0]) + p[1..];
        return IsKeyword(c) ? "@" + c : c;
    }
}

/// <summary>Allocates unique names within one C# scope, recording renames.</summary>
internal sealed class NameScope
{
    private readonly HashSet<string> _used = new(StringComparer.Ordinal);

    public NameScope(IEnumerable<string>? reserved = null)
    {
        if (reserved is not null)
        {
            _used.UnionWith(reserved);
        }
    }

    public bool Contains(string name) => _used.Contains(name);

    public void Reserve(string name) => _used.Add(name);

    /// <summary>Returns <paramref name="preferred"/>, or a suffixed variant when taken.</summary>
    public string Allocate(string preferred, string suffix = "Value")
    {
        if (_used.Add(preferred))
        {
            return preferred;
        }

        var candidate = preferred + suffix;
        for (var i = 2; !_used.Add(candidate); i++)
        {
            candidate = preferred + suffix + i;
        }

        return candidate;
    }
}
