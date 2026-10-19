using System.Numerics;
using Asn1.Diagnostics;

namespace Asn1.Syntax;

/// <summary>Lexical token kinds (X.680 clause 12).</summary>
public enum TokenKind
{
    /// <summary>End of input.</summary>
    EndOfFile,

    /// <summary>A name starting with an upper-case letter (typereference, modulereference, objectclassreference, word).</summary>
    TypeReference,

    /// <summary>A name starting with a lower-case letter (identifier, valuereference, objectreference).</summary>
    Identifier,

    /// <summary>A reserved word (X.680 12.38).</summary>
    Keyword,

    /// <summary>A field reference starting with an ampersand followed by an upper-case letter.</summary>
    TypeFieldReference,

    /// <summary>A field reference starting with an ampersand followed by a lower-case letter.</summary>
    ValueFieldReference,

    /// <summary>A non-negative integer literal.</summary>
    Number,

    /// <summary>A real number literal.</summary>
    RealNumber,

    /// <summary>A character string literal (cstring).</summary>
    CString,

    /// <summary>A binary string literal (bstring).</summary>
    BString,

    /// <summary>A hexadecimal string literal (hstring).</summary>
    HString,

    /// <summary><c>::=</c></summary>
    Assignment,

    /// <summary><c>{</c></summary>
    LeftBrace,

    /// <summary><c>}</c></summary>
    RightBrace,

    /// <summary><c>(</c></summary>
    LeftParen,

    /// <summary><c>)</c></summary>
    RightParen,

    /// <summary><c>[</c></summary>
    LeftBracket,

    /// <summary><c>]</c></summary>
    RightBracket,

    /// <summary><c>[[</c></summary>
    LeftVersionBrackets,

    /// <summary><c>]]</c></summary>
    RightVersionBrackets,

    /// <summary><c>,</c></summary>
    Comma,

    /// <summary><c>.</c></summary>
    Dot,

    /// <summary><c>..</c></summary>
    Range,

    /// <summary><c>...</c></summary>
    Ellipsis,

    /// <summary><c>-</c></summary>
    Hyphen,

    /// <summary><c>:</c></summary>
    Colon,

    /// <summary><c>;</c></summary>
    Semicolon,

    /// <summary><c>&lt;</c></summary>
    LessThan,

    /// <summary><c>&gt;</c></summary>
    GreaterThan,

    /// <summary><c>|</c></summary>
    Bar,

    /// <summary><c>^</c></summary>
    Caret,

    /// <summary><c>!</c></summary>
    Exclamation,

    /// <summary><c>@</c></summary>
    At,

    /// <summary><c>=</c></summary>
    Equals,

    /// <summary><c>&amp;</c> not followed by a name (invalid).</summary>
    Ampersand,

    /// <summary>An invalid character.</summary>
    Bad,
}

/// <summary>A lexical token.</summary>
public sealed class Token
{
    /// <summary>Creates a token.</summary>
    public Token(TokenKind kind, string text, TextSpan span, int line, bool startsLine)
    {
        Kind = kind;
        Text = text;
        Span = span;
        Line = line;
        StartsLine = startsLine;
    }

    /// <summary>Token kind.</summary>
    public TokenKind Kind { get; }

    /// <summary>Token text. For strings this is the decoded content (without quotes).</summary>
    public string Text { get; }

    /// <summary>Source span including delimiters.</summary>
    public TextSpan Span { get; }

    /// <summary>Zero-based line index of the token start.</summary>
    public int Line { get; }

    /// <summary>True if the token is the first token on its line.</summary>
    public bool StartsLine { get; }

    /// <summary>Numeric value for <see cref="TokenKind.Number"/>.</summary>
    public BigInteger NumberValue { get; init; }

    /// <summary>True when this token is the given reserved word.</summary>
    public bool IsKeyword(string keyword) => Kind == TokenKind.Keyword && Text == keyword;

    /// <summary>True for type references and identifiers.</summary>
    public bool IsName => Kind is TokenKind.TypeReference or TokenKind.Identifier;

    /// <inheritdoc />
    public override string ToString() => Kind switch
    {
        TokenKind.EndOfFile => "end of file",
        TokenKind.CString => $"\"{Text}\"",
        TokenKind.BString => $"'{Text}'B",
        TokenKind.HString => $"'{Text}'H",
        _ => $"'{Text}'",
    };
}

/// <summary>A comment preserved for documentation generation and lint suppressions.</summary>
/// <param name="Text">Comment text without delimiters, trimmed.</param>
/// <param name="Span">Source span including delimiters.</param>
/// <param name="Line">Zero-based line index of the comment start.</param>
/// <param name="EndLine">Zero-based line index of the comment end.</param>
/// <param name="IsTrailing">True if code precedes the comment on its line.</param>
public sealed record Comment(string Text, TextSpan Span, int Line, int EndLine, bool IsTrailing);

/// <summary>The output of lexing a source text.</summary>
/// <param name="Source">The source text.</param>
/// <param name="Tokens">Tokens, terminated by an end-of-file token.</param>
/// <param name="Comments">All comments in order.</param>
public sealed record LexResult(SourceText Source, IReadOnlyList<Token> Tokens, IReadOnlyList<Comment> Comments);
