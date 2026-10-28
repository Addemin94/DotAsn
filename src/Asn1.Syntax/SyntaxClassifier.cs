using Asn1.Diagnostics;

namespace Asn1.Syntax;

/// <summary>The syntactic class of a piece of ASN.1 text, for highlighting.</summary>
public enum SyntaxClass
{
    /// <summary>A comment (<c>-- ... --</c> or <c>/* ... */</c>).</summary>
    Comment,

    /// <summary>A reserved word that is not a type or a value (DEFINITIONS, BEGIN, OPTIONAL, SIZE, ...).</summary>
    Keyword,

    /// <summary>A reserved word that denotes a built-in type (INTEGER, SEQUENCE, UTF8String, ...).</summary>
    BuiltinType,

    /// <summary>A reserved word that denotes a value (TRUE, FALSE, MIN, MAX, PLUS-INFINITY, ...).</summary>
    BuiltinValue,

    /// <summary>A type reference or module reference (starts with an upper-case letter).</summary>
    TypeReference,

    /// <summary>An identifier or value reference (starts with a lower-case letter).</summary>
    Identifier,

    /// <summary>A field reference of an information object class (<c>&amp;Type</c>, <c>&amp;id</c>).</summary>
    FieldReference,

    /// <summary>A number.</summary>
    Number,

    /// <summary>A character string, bstring or hstring.</summary>
    String,

    /// <summary>An operator or punctuation (<c>::=</c>, <c>...</c>, <c>|</c>, <c>,</c>, ...).</summary>
    Operator,

    /// <summary>A bracket (<c>{ } ( ) [ ] [[ ]]</c>).</summary>
    Bracket,

    /// <summary>Text the lexer could not read.</summary>
    Invalid,
}

/// <summary>A classified piece of ASN.1 text.</summary>
/// <param name="Start">Start offset (UTF-16 code units).</param>
/// <param name="Length">Length.</param>
/// <param name="Class">Its class.</param>
public readonly record struct ClassifiedSpan(int Start, int Length, SyntaxClass Class);

/// <summary>
/// Classifies ASN.1 text with the DotAsn lexer, so that editors highlight exactly what the compiler reads (X.680
/// clause 12 lexical items, nested comments, hyphenated references).
/// </summary>
public static class SyntaxClassifier
{
    private static readonly HashSet<string> s_builtinTypes = new(StringComparer.Ordinal)
    {
        "BIT", "BMPString", "BOOLEAN", "CHARACTER", "CHOICE", "DATE", "DATE-TIME", "DURATION", "EMBEDDED", "ENUMERATED",
        "EXTERNAL", "GeneralizedTime", "GeneralString", "GraphicString", "IA5String", "IDENTIFIER", "INSTANCE", "INTEGER",
        "ISO646String", "NULL", "NumericString", "OBJECT", "ObjectDescriptor", "OCTET", "OID-IRI", "PDV",
        "PrintableString", "REAL", "RELATIVE-OID", "RELATIVE-OID-IRI", "SEQUENCE", "SET", "STRING", "T61String",
        "TeletexString", "TIME", "TIME-OF-DAY", "TYPE-IDENTIFIER", "ABSTRACT-SYNTAX", "UniversalString", "UTCTime",
        "UTF8String", "VideotexString", "VisibleString", "ANY",
    };

    private static readonly HashSet<string> s_builtinValues = new(StringComparer.Ordinal)
    {
        "TRUE", "FALSE", "MIN", "MAX", "PLUS-INFINITY", "MINUS-INFINITY", "NOT-A-NUMBER",
    };

    /// <summary>Classifies the tokens and comments of <paramref name="text"/>, ordered by position.</summary>
    public static IReadOnlyList<ClassifiedSpan> Classify(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        // Lexical errors are reported by the compiler; here they only mark text as invalid.
        var lexed = Lexer.Lex(new SourceText("highlight.asn", text), new DiagnosticBag());
        var spans = new List<ClassifiedSpan>(lexed.Tokens.Count + lexed.Comments.Count);
        foreach (var token in lexed.Tokens)
        {
            if (token.Kind != TokenKind.EndOfFile && token.Span.Length > 0)
            {
                spans.Add(new ClassifiedSpan(token.Span.Start, token.Span.Length, Classify(token)));
            }
        }

        foreach (var comment in lexed.Comments)
        {
            spans.Add(new ClassifiedSpan(comment.Span.Start, comment.Span.Length, SyntaxClass.Comment));
        }

        spans.Sort((a, b) => a.Start.CompareTo(b.Start));
        return spans;
    }

    private static SyntaxClass Classify(Token token) => token.Kind switch
    {
        TokenKind.Keyword when s_builtinTypes.Contains(token.Text) => SyntaxClass.BuiltinType,
        TokenKind.Keyword when s_builtinValues.Contains(token.Text) => SyntaxClass.BuiltinValue,
        TokenKind.Keyword => SyntaxClass.Keyword,
        TokenKind.TypeReference => SyntaxClass.TypeReference,
        TokenKind.Identifier => SyntaxClass.Identifier,
        TokenKind.TypeFieldReference or TokenKind.ValueFieldReference => SyntaxClass.FieldReference,
        TokenKind.Number or TokenKind.RealNumber => SyntaxClass.Number,
        TokenKind.CString or TokenKind.BString or TokenKind.HString => SyntaxClass.String,
        TokenKind.LeftBrace or TokenKind.RightBrace or TokenKind.LeftParen or TokenKind.RightParen or TokenKind.LeftBracket
            or TokenKind.RightBracket or TokenKind.LeftVersionBrackets or TokenKind.RightVersionBrackets => SyntaxClass.Bracket,
        TokenKind.Bad => SyntaxClass.Invalid,
        _ => SyntaxClass.Operator,
    };
}
