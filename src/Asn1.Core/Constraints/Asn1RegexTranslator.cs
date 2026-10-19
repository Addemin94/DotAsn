using System.Text;

namespace Asn1.Constraints;

/// <summary>
/// Translates X.680 Annex A regular expressions to .NET regular expressions. Annex A uses XML Schema-like
/// syntax with additions such as the <c>#n</c> quantifier
/// shorthand <c>#(n,m)</c>. Supported: literal characters, <c>.</c>, character classes, <c>\d \w \s</c>,
/// grouping, alternation and the quantifiers <c>* + ?</c>, <c>#n</c> and <c>#(n,m)</c>.
/// </summary>
public static class Asn1RegexTranslator
{
    /// <summary>Translates <paramref name="pattern"/>.</summary>
    /// <exception cref="ArgumentException">The pattern is malformed.</exception>
    public static string Translate(string pattern)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c == '#')
            {
                // #n  => {n};  #(n,m) => {n,m}; #(n,) => {n,}; #(,m) => {0,m}
                if (i + 1 < pattern.Length && pattern[i + 1] == '(')
                {
                    var close = pattern.IndexOf(')', i + 2);
                    if (close < 0)
                    {
                        throw new ArgumentException("unterminated #( quantifier");
                    }

                    var body = pattern[(i + 2)..close];
                    if (body.StartsWith(",", StringComparison.Ordinal))
                    {
                        body = "0" + body;
                    }

                    sb.Append('{').Append(body).Append('}');
                    i = close;
                }
                else
                {
                    var j = i + 1;
                    while (j < pattern.Length && char.IsAsciiDigit(pattern[j]))
                    {
                        j++;
                    }

                    if (j == i + 1)
                    {
                        throw new ArgumentException("'#' must be followed by a number");
                    }

                    sb.Append('{').Append(pattern, i + 1, j - i - 1).Append('}');
                    i = j - 1;
                }

                continue;
            }

            if (c == '\\' && i + 1 < pattern.Length && pattern[i + 1] == 'N')
            {
                // \N{name}: named characters are not supported by .NET; only accept U+XXXX forms via cells.
                throw new ArgumentException("\\N{...} character names are not supported");
            }

            if (c == '\\' && i + 1 < pattern.Length)
            {
                sb.Append(c).Append(pattern[i + 1]);
                i++;
                continue;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }
}
