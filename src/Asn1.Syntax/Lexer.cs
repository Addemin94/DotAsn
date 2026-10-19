using System.Globalization;
using System.Numerics;
using System.Text;
using Asn1.Diagnostics;

namespace Asn1.Syntax;

/// <summary>
/// Tokenizes ASN.1 notation according to X.680 clause 12, preserving comments and source positions.
/// </summary>
public sealed class Lexer
{
    /// <summary>The reserved words of X.680 (2021) clause 12.38, plus withdrawn X.208 words that still appear in legacy specifications.</summary>
    public static readonly IReadOnlySet<string> ReservedWords = new HashSet<string>(StringComparer.Ordinal)
    {
        "ABSENT", "ABSTRACT-SYNTAX", "ALL", "APPLICATION", "AUTOMATIC", "BEGIN", "BIT", "BMPString", "BOOLEAN", "BY",
        "CHARACTER", "CHOICE", "CLASS", "COMPONENT", "COMPONENTS", "CONSTRAINED", "CONTAINING", "DATE", "DATE-TIME",
        "DEFAULT", "DEFINITIONS", "DURATION", "EMBEDDED", "ENCODED", "ENCODING-CONTROL", "END", "ENUMERATED", "EXCEPT",
        "EXPLICIT", "EXPORTS", "EXTENSIBILITY", "EXTERNAL", "FALSE", "FROM", "GeneralizedTime", "GeneralString",
        "GraphicString", "IA5String", "IDENTIFIER", "IMPLICIT", "IMPLIED", "IMPORTS", "INCLUDES", "INSTANCE",
        "INSTRUCTIONS", "INTEGER", "INTERSECTION", "ISO646String", "MAX", "MIN", "MINUS-INFINITY", "NOT-A-NUMBER",
        "NULL", "NumericString", "OBJECT", "ObjectDescriptor", "OCTET", "OF", "OID-IRI", "OPTIONAL", "PATTERN", "PDV",
        "PLUS-INFINITY", "PRESENT", "PrintableString", "PRIVATE", "REAL", "RELATIVE-OID", "RELATIVE-OID-IRI", "SEQUENCE",
        "SET", "SETTINGS", "SIZE", "STRING", "SYNTAX", "T61String", "TAGS", "TeletexString", "TIME", "TIME-OF-DAY",
        "TRUE", "TYPE-IDENTIFIER", "UNION", "UNIQUE", "UNIVERSAL", "UniversalString", "UTCTime", "UTF8String",
        "VideotexString", "VisibleString", "WITH",

        // Withdrawn X.208 notation, recognised so it can be diagnosed precisely.
        "ANY", "DEFINED", "MACRO",
    };

    private readonly SourceText _source;
    private readonly string _text;
    private readonly DiagnosticBag _diagnostics;
    private readonly List<Token> _tokens = [];
    private readonly List<Comment> _comments = [];
    private int _pos;
    private int _lastTokenLine = -1;

    private Lexer(SourceText source, DiagnosticBag diagnostics)
    {
        _source = source;
        _text = source.Text;
        _diagnostics = diagnostics;
    }

    /// <summary>Tokenizes a source text.</summary>
    public static LexResult Lex(SourceText source, DiagnosticBag diagnostics)
    {
        var lexer = new Lexer(source, diagnostics);
        lexer.Run();
        return new LexResult(source, lexer._tokens, lexer._comments);
    }

    private char Peek(int ahead = 0) => _pos + ahead < _text.Length ? _text[_pos + ahead] : '\0';

    private void Report(DiagnosticDescriptor d, int start, int length, params object?[] args) =>
        _diagnostics.Report(d, new SourceLocation(_source, new TextSpan(start, Math.Max(length, 1))), args);

    private void Add(TokenKind kind, string text, int start, BigInteger number = default)
    {
        var line = _source.GetLineIndex(start);
        var startsLine = line != _lastTokenLine;
        _lastTokenLine = _source.GetLineIndex(Math.Max(start, _pos - 1));
        _tokens.Add(new Token(kind, text, TextSpan.FromBounds(start, _pos), line, startsLine) { NumberValue = number });
    }

    private static bool IsWhite(char c) => c is ' ' or '\t' or '\n' or '\r' or '\u000B' or '\u000C' or ' ' || (c > 127 && char.IsWhiteSpace(c));

    private void Run()
    {
        while (_pos < _text.Length)
        {
            var c = Peek();
            if (IsWhite(c))
            {
                _pos++;
                continue;
            }

            var start = _pos;
            if (c == '-' && Peek(1) == '-')
            {
                LexLineComment();
                continue;
            }

            if (c == '/' && Peek(1) == '*')
            {
                LexBlockComment();
                continue;
            }

            if (char.IsAsciiLetter(c))
            {
                LexName(start, TokenKind.Identifier);
                continue;
            }

            if (c == '&')
            {
                if (char.IsAsciiLetter(Peek(1)))
                {
                    _pos++;
                    LexName(start, TokenKind.ValueFieldReference);
                }
                else
                {
                    _pos++;
                    Add(TokenKind.Ampersand, "&", start);
                    Report(DiagnosticCatalog.UnexpectedCharacter, start, 1, "&");
                }

                continue;
            }

            if (char.IsAsciiDigit(c))
            {
                LexNumber(start);
                continue;
            }

            switch (c)
            {
                case '"':
                    LexCString(start);
                    continue;
                case '\'':
                    LexBinaryString(start);
                    continue;
                case ':':
                    if (Peek(1) == ':' && Peek(2) == '=')
                    {
                        _pos += 3;
                        Add(TokenKind.Assignment, "::=", start);
                    }
                    else
                    {
                        _pos++;
                        Add(TokenKind.Colon, ":", start);
                    }

                    continue;
                case '.':
                    if (Peek(1) == '.' && Peek(2) == '.')
                    {
                        _pos += 3;
                        Add(TokenKind.Ellipsis, "...", start);
                    }
                    else if (Peek(1) == '.')
                    {
                        _pos += 2;
                        Add(TokenKind.Range, "..", start);
                    }
                    else
                    {
                        _pos++;
                        Add(TokenKind.Dot, ".", start);
                    }

                    continue;
                case '[':
                    if (Peek(1) == '[')
                    {
                        _pos += 2;
                        Add(TokenKind.LeftVersionBrackets, "[[", start);
                    }
                    else
                    {
                        _pos++;
                        Add(TokenKind.LeftBracket, "[", start);
                    }

                    continue;
                case ']':
                    if (Peek(1) == ']')
                    {
                        _pos += 2;
                        Add(TokenKind.RightVersionBrackets, "]]", start);
                    }
                    else
                    {
                        _pos++;
                        Add(TokenKind.RightBracket, "]", start);
                    }

                    continue;
            }

            var kind = c switch
            {
                '{' => TokenKind.LeftBrace,
                '}' => TokenKind.RightBrace,
                '(' => TokenKind.LeftParen,
                ')' => TokenKind.RightParen,
                ',' => TokenKind.Comma,
                '-' => TokenKind.Hyphen,
                ';' => TokenKind.Semicolon,
                '<' => TokenKind.LessThan,
                '>' => TokenKind.GreaterThan,
                '|' => TokenKind.Bar,
                '^' => TokenKind.Caret,
                '!' => TokenKind.Exclamation,
                '@' => TokenKind.At,
                '=' => TokenKind.Equals,
                _ => TokenKind.Bad,
            };
            _pos++;
            if (kind == TokenKind.Bad)
            {
                Report(DiagnosticCatalog.UnexpectedCharacter, start, 1, c < ' ' ? $"\\u{(int)c:X4}" : c.ToString());
                continue;
            }

            Add(kind, c.ToString(), start);
        }

        var eofStart = _text.Length;
        _tokens.Add(new Token(TokenKind.EndOfFile, string.Empty, new TextSpan(eofStart, 0), _source.GetLineIndex(eofStart), true));
    }

    private void LexName(int start, TokenKind defaultKind)
    {
        // start points at the first letter, or at '&' for field references.
        var nameStart = _pos;
        _pos++;
        var hasUnderscore = false;
        while (_pos < _text.Length)
        {
            var c = Peek();
            if (char.IsAsciiLetterOrDigit(c))
            {
                _pos++;
            }
            else if (c == '-')
            {
                // "--" begins a comment and terminates the name.
                if (Peek(1) == '-')
                {
                    break;
                }

                if (!char.IsAsciiLetterOrDigit(Peek(1)))
                {
                    break;
                }

                _pos++;
            }
            else if (c == '_')
            {
                hasUnderscore = true;
                _pos++;
            }
            else
            {
                break;
            }
        }

        var name = _text[nameStart.._pos];
        if (hasUnderscore)
        {
            Report(DiagnosticCatalog.InvalidIdentifier, nameStart, name.Length, name, "'_' is not permitted in ASN.1 names (X.680 12.2)");
        }

        if (defaultKind == TokenKind.ValueFieldReference)
        {
            var kind = char.IsAsciiLetterUpper(name[0]) ? TokenKind.TypeFieldReference : TokenKind.ValueFieldReference;
            Add(kind, "&" + name, start);
            return;
        }

        if (ReservedWords.Contains(name))
        {
            Add(TokenKind.Keyword, name, start);
            return;
        }

        Add(char.IsAsciiLetterUpper(name[0]) ? TokenKind.TypeReference : TokenKind.Identifier, name, start);
    }

    private void LexNumber(int start)
    {
        while (char.IsAsciiDigit(Peek()))
        {
            _pos++;
        }

        var isReal = false;

        // "1.5" and "1." are real numbers; "1..5" is a range, while "1...5" is the real "1." followed by "..".
        if (Peek() == '.' && (Peek(1) != '.' || Peek(2) == '.'))
        {
            isReal = true;
            _pos++;
            while (char.IsAsciiDigit(Peek()))
            {
                _pos++;
            }
        }

        if ((Peek() == 'e' || Peek() == 'E') && (char.IsAsciiDigit(Peek(1)) || (Peek(1) == '-' && char.IsAsciiDigit(Peek(2)))))
        {
            isReal = true;
            _pos += Peek(1) == '-' ? 2 : 1;
            while (char.IsAsciiDigit(Peek()))
            {
                _pos++;
            }
        }

        var text = _text[start.._pos];
        if (isReal)
        {
            Add(TokenKind.RealNumber, text, start);
            return;
        }

        Add(TokenKind.Number, text, start, BigInteger.Parse(text, NumberStyles.None, CultureInfo.InvariantCulture));
    }

    private void LexCString(int start)
    {
        // X.680 12.14: "" denotes a quotation mark; spacing characters adjacent to a line break are removed.
        _pos++;
        var sb = new StringBuilder();
        while (true)
        {
            if (_pos >= _text.Length)
            {
                Report(DiagnosticCatalog.UnterminatedString, start, _pos - start);
                break;
            }

            var c = Peek();
            if (c == '"')
            {
                if (Peek(1) == '"')
                {
                    sb.Append('"');
                    _pos += 2;
                    continue;
                }

                _pos++;
                break;
            }

            if (c is '\r' or '\n')
            {
                while (sb.Length > 0 && sb[^1] is ' ' or '\t')
                {
                    sb.Length--;
                }

                while (_pos < _text.Length && IsWhite(Peek()))
                {
                    _pos++;
                }

                continue;
            }

            sb.Append(c);
            _pos++;
        }

        Add(TokenKind.CString, sb.ToString(), start);
    }

    private void LexBinaryString(int start)
    {
        _pos++;
        var sb = new StringBuilder();
        var terminated = false;
        while (_pos < _text.Length)
        {
            var c = Peek();
            if (c == '\'')
            {
                terminated = true;
                _pos++;
                break;
            }

            if (!IsWhite(c))
            {
                sb.Append(c);
            }

            _pos++;
        }

        var content = sb.ToString();
        if (!terminated)
        {
            Report(DiagnosticCatalog.InvalidBinaryString, start, _pos - start, "missing closing apostrophe");
            Add(TokenKind.BString, content, start);
            return;
        }

        var suffix = Peek();
        if (suffix is 'B' or 'b')
        {
            _pos++;
            if (content.Any(ch => ch is not ('0' or '1')))
            {
                Report(DiagnosticCatalog.InvalidBinaryString, start, _pos - start, $"'{content}'B contains characters other than 0 and 1");
            }

            Add(TokenKind.BString, content, start);
        }
        else if (suffix is 'H' or 'h')
        {
            _pos++;
            if (content.Any(ch => !char.IsAsciiHexDigitUpper(ch) && !char.IsAsciiDigit(ch)))
            {
                Report(DiagnosticCatalog.InvalidBinaryString, start, _pos - start, $"'{content}'H must contain only digits and upper-case A-F");
            }

            Add(TokenKind.HString, content.ToUpperInvariant(), start);
        }
        else
        {
            Report(DiagnosticCatalog.InvalidBinaryString, start, _pos - start, "expected 'B' or 'H' after the closing apostrophe");
            Add(TokenKind.BString, content, start);
        }
    }

    private void LexLineComment()
    {
        var start = _pos;
        var startLine = _source.GetLineIndex(start);
        _pos += 2;
        var contentStart = _pos;
        var contentEnd = _pos;
        while (_pos < _text.Length)
        {
            var c = Peek();
            if (c is '\n' or '\r')
            {
                contentEnd = _pos;
                break;
            }

            if (c == '-' && Peek(1) == '-')
            {
                contentEnd = _pos;
                _pos += 2;
                goto done;
            }

            _pos++;
            contentEnd = _pos;
        }

    done:
        var trailing = _lastTokenLine == startLine;
        _comments.Add(new Comment(_text[contentStart..contentEnd].Trim(), TextSpan.FromBounds(start, _pos), startLine, startLine, trailing));
    }

    private void LexBlockComment()
    {
        var start = _pos;
        var startLine = _source.GetLineIndex(start);
        _pos += 2;
        var depth = 1;
        while (_pos < _text.Length && depth > 0)
        {
            if (Peek() == '/' && Peek(1) == '*')
            {
                depth++;
                _pos += 2;
            }
            else if (Peek() == '*' && Peek(1) == '/')
            {
                depth--;
                _pos += 2;
            }
            else
            {
                _pos++;
            }
        }

        if (depth > 0)
        {
            Report(DiagnosticCatalog.UnterminatedComment, start, 2);
        }

        var inner = _text[(start + 2)..Math.Max(start + 2, depth > 0 ? _pos : _pos - 2)];
        _comments.Add(new Comment(inner.Trim(), TextSpan.FromBounds(start, _pos), startLine, _source.GetLineIndex(_pos), _lastTokenLine == startLine));
    }
}
