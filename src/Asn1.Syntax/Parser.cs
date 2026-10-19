using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using System.Text;
using Asn1.Diagnostics;

namespace Asn1.Syntax;

/// <summary>Options that bound parser resource usage.</summary>
public sealed record ParserOptions
{
    /// <summary>Default options.</summary>
    public static readonly ParserOptions Default = new();

    /// <summary>Maximum syntactic nesting depth (types, constraints, values).</summary>
    public int MaxDepth { get; init; } = 200;

    /// <summary>Maximum source length in characters.</summary>
    public int MaxSourceLength { get; init; } = 64 * 1024 * 1024;
}

/// <summary>Thrown internally to unwind to an error-recovery point. Never escapes the parser.</summary>
internal sealed class ParseException : Exception
{
    public ParseException()
    {
    }

    public ParseException(string message) : base(message)
    {
    }

    public ParseException(string message, Exception inner) : base(message, inner)
    {
    }
}

/// <summary>
/// Recursive-descent parser for ASN.1 modules. The grammar follows X.680, X.681, X.682 and X.683.
/// Notation that is ambiguous without semantic information (braced values, actual parameters,
/// information object definitions) is preserved as token ranges for re-parsing in later phases.
/// </summary>
public sealed partial class Parser
{
    private static readonly HashSet<string> s_typeKeywords = new(StringComparer.Ordinal)
    {
        "BOOLEAN", "INTEGER", "BIT", "OCTET", "NULL", "OBJECT", "REAL", "ENUMERATED", "SEQUENCE", "SET", "CHOICE",
        "RELATIVE-OID", "OID-IRI", "RELATIVE-OID-IRI", "EXTERNAL", "EMBEDDED", "CHARACTER", "UTF8String", "NumericString",
        "PrintableString", "TeletexString", "T61String", "VideotexString", "IA5String", "GraphicString", "VisibleString",
        "ISO646String", "GeneralString", "UniversalString", "BMPString", "UTCTime", "GeneralizedTime", "ObjectDescriptor",
        "DATE", "TIME-OF-DAY", "DATE-TIME", "DURATION", "TIME", "INSTANCE", "ANY", "TYPE-IDENTIFIER", "ABSTRACT-SYNTAX",
    };

    private readonly LexResult _lex;
    private readonly IReadOnlyList<Token> _tokens;
    private readonly SourceText _source;
    private readonly DiagnosticBag _diagnostics;
    private readonly ParserOptions _options;
    private readonly int _end;
    private readonly Token _eof;
    private int _pos;
    private int _depth;
    private int _lastErrorPos = -1;

    private Parser(LexResult lex, int start, int end, DiagnosticBag diagnostics, ParserOptions options)
    {
        _lex = lex;
        _tokens = lex.Tokens;
        _source = lex.Source;
        _diagnostics = diagnostics;
        _options = options;
        _pos = start;
        _end = Math.Min(end, _tokens.Count - 1);
        var eofOffset = _end < _tokens.Count ? _tokens[_end].Span.Start : _source.Text.Length;
        _eof = _end == _tokens.Count - 1 ? _tokens[^1] : new Token(TokenKind.EndOfFile, string.Empty, new TextSpan(eofOffset, 0), _tokens[_end].Line, true);
    }

    /// <summary>Parses a complete source file.</summary>
    public static CompilationUnitSyntax ParseCompilationUnit(SourceText source, DiagnosticBag diagnostics, ParserOptions? options = null)
    {
        options ??= ParserOptions.Default;
        if (source.Text.Length > options.MaxSourceLength)
        {
            diagnostics.Report(DiagnosticCatalog.ResourceLimitExceeded, new SourceLocation(source, default), $"source is {source.Text.Length} characters; the limit is {options.MaxSourceLength}");
            var empty = Lexer.Lex(new SourceText(source.Path, string.Empty), diagnostics);
            return new CompilationUnitSyntax(source, default, [], empty);
        }

        var lex = Lexer.Lex(source, diagnostics);
        var parser = new Parser(lex, 0, lex.Tokens.Count - 1, diagnostics, options);
        return parser.ParseUnit();
    }

    /// <summary>Creates a parser over a token sub-range (used for kind-directed reparsing of deferred notation).</summary>
    public static Parser ForTokenRange(LexResult lex, int start, int end, DiagnosticBag diagnostics, ParserOptions? options = null) =>
        new(lex, start, end, diagnostics, options ?? ParserOptions.Default);

    /// <summary>True when all tokens in the range have been consumed.</summary>
    public bool IsAtEnd => Current.Kind == TokenKind.EndOfFile;

    /// <summary>The index of the current token in the file's token list.</summary>
    public int Position => _pos;

    /// <summary>The current token.</summary>
    public Token CurrentToken => Current;

    /// <summary>Returns the token <paramref name="ahead"/> positions after the current one (end-of-file past the range).</summary>
    public Token Peek(int ahead) => PeekToken(ahead);

    /// <summary>Consumes and returns the current token.</summary>
    public Token Next() => Advance();

    /// <summary>Consumes the current token if it has the given kind.</summary>
    public bool TryAccept(TokenKind kind) => Accept(kind);

    /// <summary>Consumes the current token if it is the given kind, otherwise reports a syntax error and returns false.</summary>
    public bool ExpectToken(TokenKind kind, string description)
    {
        if (Accept(kind))
        {
            return true;
        }

        try
        {
            throw Error(description);
        }
        catch (ParseException)
        {
            return false;
        }
    }

    /// <summary>Reports a syntax error at the current token (once per position).</summary>
    public void ReportUnexpected(string expected)
    {
        try
        {
            throw Error(expected);
        }
        catch (ParseException)
        {
        }
    }

    /// <summary>Runs a parse function, converting internal parse failures into a null result (diagnostics are already reported).</summary>
    public T? TryRun<T>(Func<Parser, T> parse)
        where T : class
    {
        try
        {
            return parse(this);
        }
        catch (ParseException)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ token helpers

    private Token Current => _pos < _end ? _tokens[_pos] : _eof;

    private Token PeekToken(int ahead) => _pos + ahead < _end ? _tokens[_pos + ahead] : _eof;

    private Token Previous => _pos > 0 ? _tokens[_pos - 1] : _tokens[0];

    private Token Advance()
    {
        var t = Current;
        if (_pos < _end)
        {
            _pos++;
        }

        return t;
    }

    private bool Is(TokenKind kind) => Current.Kind == kind;

    private bool IsKeyword(string kw) => Current.IsKeyword(kw);

    private bool Accept(TokenKind kind)
    {
        if (Current.Kind != kind)
        {
            return false;
        }

        Advance();
        return true;
    }

    private bool AcceptKeyword(string kw)
    {
        if (!Current.IsKeyword(kw))
        {
            return false;
        }

        Advance();
        return true;
    }

    private Token Expect(TokenKind kind, string description)
    {
        if (Current.Kind == kind)
        {
            return Advance();
        }

        throw Error(description);
    }

    private Token ExpectKeyword(string kw)
    {
        if (Current.IsKeyword(kw))
        {
            return Advance();
        }

        throw Error(kw);
    }

    private ParseException Error(string expected)
    {
        // Report at most one error per position to avoid cascades.
        if (_pos != _lastErrorPos)
        {
            _lastErrorPos = _pos;
            var t = Current;
            _diagnostics.Report(DiagnosticCatalog.UnexpectedToken, new SourceLocation(_source, t.Kind == TokenKind.EndOfFile ? new TextSpan(t.Span.Start, 0) : t.Span), expected, t.ToString());
        }

        return new ParseException(expected);
    }

    private TextSpan SpanFrom(int startToken)
    {
        var start = startToken < _tokens.Count ? _tokens[startToken].Span.Start : _source.Text.Length;
        var endTok = Math.Max(startToken, _pos - 1);
        var end = endTok < _tokens.Count ? _tokens[endTok].Span.End : start;
        return TextSpan.FromBounds(start, Math.Max(start, end));
    }

    private NameSyntax Name(Token t) => new(_source, t.Span, t.Text);

    private void Enter()
    {
        if (++_depth > _options.MaxDepth)
        {
            _diagnostics.Report(DiagnosticCatalog.NestingTooDeep, new SourceLocation(_source, Current.Span), _options.MaxDepth);
            throw new ParseException("depth");
        }
    }

    private void Leave() => _depth--;

    /// <summary>Returns the index of the token matching the bracket at <paramref name="openIndex"/>, or -1.</summary>
    private int FindMatching(int openIndex)
    {
        var open = _tokens[openIndex].Kind;
        var close = open switch
        {
            TokenKind.LeftBrace => TokenKind.RightBrace,
            TokenKind.LeftParen => TokenKind.RightParen,
            TokenKind.LeftBracket => TokenKind.RightBracket,
            TokenKind.LeftVersionBrackets => TokenKind.RightVersionBrackets,
            _ => throw new InvalidOperationException("not an opening bracket"),
        };
        var depth = 0;
        for (var i = openIndex; i < _end; i++)
        {
            var k = _tokens[i].Kind;
            if (k == open)
            {
                depth++;
            }
            else if (k == close)
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }
        }

        return -1;
    }

    private void SkipBalanced()
    {
        var match = FindMatching(_pos);
        if (match < 0)
        {
            throw Error("matching closing bracket");
        }

        _pos = match + 1;
    }

    // ------------------------------------------------------------------ documentation comments

    private string? DocumentationFor(int firstToken, int lastToken)
    {
        var comments = _lex.Comments;
        if (comments.Count == 0 || firstToken >= _tokens.Count)
        {
            return null;
        }

        var first = _tokens[firstToken];
        var prevEnd = firstToken > 0 ? _tokens[firstToken - 1].Span.End : 0;
        var leading = new List<string>();

        // Leading block: contiguous comment lines ending on the line before the first token.
        var idx = LowerBound(first.Span.Start) - 1;
        var expectedLine = first.Line - 1;
        while (idx >= 0)
        {
            var c = comments[idx];
            if (c.Span.Start < prevEnd || c.IsTrailing)
            {
                break;
            }

            if (c.EndLine == expectedLine || c.EndLine == expectedLine + 1)
            {
                if (c.EndLine == expectedLine)
                {
                    expectedLine = c.Line - 1;
                }

                leading.Insert(0, c.Text);
                idx--;
            }
            else
            {
                break;
            }
        }

        // Trailing comment on the same line as the last token (after an optional comma).
        string? trailing = null;
        if (lastToken >= 0 && lastToken < _tokens.Count)
        {
            var last = _tokens[lastToken];
            var after = lastToken + 1 < _tokens.Count ? _tokens[lastToken + 1] : null;
            var limitTok = after is not null && after.Kind == TokenKind.Comma && after.Line == last.Line && lastToken + 2 < _tokens.Count ? _tokens[lastToken + 2] : after;
            var limit = limitTok?.Span.Start ?? int.MaxValue;
            var j = LowerBound(last.Span.End);
            var parts = new List<string>();
            while (j < comments.Count && comments[j].Span.Start < limit && comments[j].Line == last.Line)
            {
                parts.Add(comments[j].Text);
                j++;
            }

            if (parts.Count > 0)
            {
                trailing = string.Join(" ", parts);
            }
        }

        var lines = leading.Select(CleanComment).Where(l => l.Length > 0).ToList();
        if (trailing is not null && CleanComment(trailing) is { Length: > 0 } t)
        {
            lines.Add(t);
        }

        return lines.Count == 0 ? null : string.Join("\n", lines);
    }

    private static string CleanComment(string text)
    {
        var trimmed = text.Trim();
        return trimmed.All(c => c is '-' or '=' or '*' or '#' or '~' or '_' or ' ') ? string.Empty : trimmed;
    }

    private int LowerBound(int offset)
    {
        var comments = _lex.Comments;
        int lo = 0, hi = comments.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) >> 1;
            if (comments[mid].Span.Start < offset)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }

    // ------------------------------------------------------------------ compilation unit and modules

    private CompilationUnitSyntax ParseUnit()
    {
        var modules = ImmutableArray.CreateBuilder<ModuleDefinitionSyntax>();
        while (!Is(TokenKind.EndOfFile))
        {
            if (Is(TokenKind.TypeReference))
            {
                var m = ParseModule();
                if (m is not null)
                {
                    modules.Add(m);
                }
            }
            else
            {
                _diagnostics.Report(modules.Count == 0 ? DiagnosticCatalog.UnexpectedToken : DiagnosticCatalog.TrailingContent, new SourceLocation(_source, Current.Span), modules.Count == 0 ? "module reference" : Current.ToString(), Current.ToString());
                // Skip to the next plausible module header.
                Advance();
                while (!Is(TokenKind.EndOfFile) && !(Is(TokenKind.TypeReference) && LooksLikeModuleHeader()))
                {
                    Advance();
                }
            }
        }

        if (modules.Count == 0 && !_diagnostics.HasErrors)
        {
            _diagnostics.Report(DiagnosticCatalog.NoModule, new SourceLocation(_source, default));
        }

        return new CompilationUnitSyntax(_source, new TextSpan(0, _source.Text.Length), modules.ToImmutable(), _lex);
    }

    private bool LooksLikeModuleHeader()
    {
        for (var i = 1; i < 64 && _pos + i < _end; i++)
        {
            var t = PeekToken(i);
            if (t.IsKeyword("DEFINITIONS"))
            {
                return true;
            }

            if (t.Kind == TokenKind.Assignment || t.IsKeyword("BEGIN"))
            {
                return false;
            }
        }

        return false;
    }

    private ModuleDefinitionSyntax? ParseModule()
    {
        var start = _pos;
        var doc = DocumentationFor(start, -1);
        var nameTok = Advance();
        ImmutableArray<OidComponentSyntax>? oid = null;
        string? iri = null;
        string? encodingDefault = null;
        var tagDefault = TagDefault.Unspecified;
        var extImplied = false;
        try
        {
            if (Is(TokenKind.LeftBrace))
            {
                oid = ParseOidComponents();
            }

            if (Is(TokenKind.CString))
            {
                iri = Advance().Text;
            }

            ExpectKeyword("DEFINITIONS");
            if (Is(TokenKind.TypeReference) && PeekToken(1).IsKeyword("INSTRUCTIONS"))
            {
                encodingDefault = Advance().Text;
                Advance();
            }

            if (AcceptKeyword("EXPLICIT"))
            {
                tagDefault = TagDefault.Explicit;
                ExpectKeyword("TAGS");
            }
            else if (AcceptKeyword("IMPLICIT"))
            {
                tagDefault = TagDefault.Implicit;
                ExpectKeyword("TAGS");
            }
            else if (AcceptKeyword("AUTOMATIC"))
            {
                tagDefault = TagDefault.Automatic;
                ExpectKeyword("TAGS");
            }

            if (AcceptKeyword("EXTENSIBILITY"))
            {
                ExpectKeyword("IMPLIED");
                extImplied = true;
            }

            Expect(TokenKind.Assignment, "'::='");
            ExpectKeyword("BEGIN");
        }
        catch (ParseException)
        {
            while (!Is(TokenKind.EndOfFile) && !IsKeyword("BEGIN"))
            {
                Advance();
            }

            if (!AcceptKeyword("BEGIN"))
            {
                return null;
            }
        }

        ExportsSyntax? exports = null;
        var imports = ImmutableArray<ImportGroupSyntax>.Empty;
        if (IsKeyword("EXPORTS"))
        {
            exports = TryParse(ParseExports, SkipPastSemicolon);
        }

        if (IsKeyword("IMPORTS"))
        {
            imports = TryParse(ParseImports, SkipPastSemicolon) ?? [];
        }

        var assignments = ImmutableArray.CreateBuilder<AssignmentSyntax>();
        while (!Is(TokenKind.EndOfFile) && !IsKeyword("END") && !IsKeyword("ENCODING-CONTROL"))
        {
            var before = _pos;
            try
            {
                var a = ParseAssignment();
                if (a is not null)
                {
                    assignments.Add(a);
                }
            }
            catch (ParseException)
            {
                SkipToNextAssignment(before);
            }

            if (_pos == before)
            {
                Advance();
            }
        }

        while (IsKeyword("ENCODING-CONTROL"))
        {
            var ecStart = Advance();
            var encRef = Is(TokenKind.TypeReference) ? Current.Text : "?";
            _diagnostics.Report(DiagnosticCatalog.EncodingControlIgnored, new SourceLocation(_source, ecStart.Span), encRef);
            while (!Is(TokenKind.EndOfFile) && !IsKeyword("END") && !IsKeyword("ENCODING-CONTROL"))
            {
                Advance();
            }
        }

        if (!AcceptKeyword("END"))
        {
            _diagnostics.Report(DiagnosticCatalog.MissingEnd, new SourceLocation(_source, nameTok.Span), nameTok.Text);
        }

        return new ModuleDefinitionSyntax(_source, SpanFrom(start), Name(nameTok), oid, iri, encodingDefault, tagDefault, extImplied, exports, imports, assignments.ToImmutable(), doc);
    }

    private T? TryParse<T>(Func<T> parse, Action recover)
        where T : class
    {
        try
        {
            return parse();
        }
        catch (ParseException)
        {
            recover();
            return null;
        }
    }

    private ImmutableArray<ImportGroupSyntax>? TryParse(Func<ImmutableArray<ImportGroupSyntax>> parse, Action recover)
    {
        try
        {
            return parse();
        }
        catch (ParseException)
        {
            recover();
            return null;
        }
    }

    private void SkipPastSemicolon()
    {
        while (!Is(TokenKind.EndOfFile) && !Is(TokenKind.Semicolon) && !IsKeyword("END"))
        {
            Advance();
        }

        Accept(TokenKind.Semicolon);
    }

    private void SkipToNextAssignment(int errorStart)
    {
        if (_pos == errorStart)
        {
            Advance();
        }

        while (!Is(TokenKind.EndOfFile))
        {
            if (IsKeyword("END") && (Current.StartsLine || PeekToken(1).Kind == TokenKind.EndOfFile || LooksLikeModuleHeaderAt(1)))
            {
                return;
            }

            if (Current.IsName && Current.StartsLine && LooksLikeAssignmentStart())
            {
                return;
            }

            Advance();
        }
    }

    private bool LooksLikeModuleHeaderAt(int offset)
    {
        var save = _pos;
        _pos = Math.Min(_pos + offset, _end);
        var result = Is(TokenKind.TypeReference) && LooksLikeModuleHeader();
        _pos = save;
        return result;
    }

    private bool LooksLikeAssignmentStart()
    {
        var depth = 0;
        for (var i = 1; i < 200 && _pos + i < _end; i++)
        {
            var t = PeekToken(i);
            switch (t.Kind)
            {
                case TokenKind.LeftBrace or TokenKind.LeftParen or TokenKind.LeftBracket:
                    depth++;
                    break;
                case TokenKind.RightBrace or TokenKind.RightParen or TokenKind.RightBracket:
                    depth--;
                    if (depth < 0)
                    {
                        return false;
                    }

                    break;
                case TokenKind.Assignment:
                    return depth == 0;
            }

            if (depth == 0 && t.StartsLine && t.Kind != TokenKind.Assignment)
            {
                return false;
            }
        }

        return false;
    }

    private ImmutableArray<OidComponentSyntax> ParseOidComponents()
    {
        Expect(TokenKind.LeftBrace, "'{'");
        var list = ImmutableArray.CreateBuilder<OidComponentSyntax>();
        while (!Is(TokenKind.RightBrace) && !Is(TokenKind.EndOfFile))
        {
            var start = _pos;
            if (Is(TokenKind.Identifier))
            {
                var name = Name(Advance());
                ValueSyntax? number = null;
                if (Accept(TokenKind.LeftParen))
                {
                    number = ParseValue();
                    Expect(TokenKind.RightParen, "')'");
                }

                list.Add(new OidComponentSyntax(_source, SpanFrom(start), name, number));
            }
            else if (Is(TokenKind.Number))
            {
                var t = Advance();
                list.Add(new OidComponentSyntax(_source, t.Span, null, new NumberValueSyntax(_source, t.Span, t.NumberValue)));
            }
            else if (Is(TokenKind.TypeReference) && PeekToken(1).Kind == TokenKind.Dot)
            {
                var v = ParseValue();
                list.Add(new OidComponentSyntax(_source, SpanFrom(start), null, v));
            }
            else
            {
                throw Error("object identifier component");
            }
        }

        Expect(TokenKind.RightBrace, "'}'");
        return list.ToImmutable();
    }

    private ExportsSyntax ParseExports()
    {
        var start = _pos;
        ExpectKeyword("EXPORTS");
        if (AcceptKeyword("ALL"))
        {
            Expect(TokenKind.Semicolon, "';'");
            return new ExportsSyntax(_source, SpanFrom(start), true, []);
        }

        var symbols = ParseSymbolList(stopAtFrom: false);
        Expect(TokenKind.Semicolon, "';'");
        return new ExportsSyntax(_source, SpanFrom(start), false, symbols);
    }

    private ImmutableArray<SymbolSyntax> ParseSymbolList(bool stopAtFrom)
    {
        var list = ImmutableArray.CreateBuilder<SymbolSyntax>();
        while (Current.IsName || IsImportableKeyword())
        {
            var start = _pos;
            if (IsImportableKeyword())
            {
                // Legacy specifications (e.g. RFC 5280) import UTF8String/BMPString for X.208 compilers.
                _diagnostics.Report(DiagnosticCatalog.DeprecatedNotation, new SourceLocation(_source, Current.Span), Current.Text, "built-in types cannot be imported; the symbol is ignored");
                Advance();
                if (!Accept(TokenKind.Comma))
                {
                    break;
                }

                continue;
            }

            var name = Name(Advance());
            var parameterized = false;
            if (Is(TokenKind.LeftBrace) && PeekToken(1).Kind == TokenKind.RightBrace)
            {
                Advance();
                Advance();
                parameterized = true;
            }

            list.Add(new SymbolSyntax(_source, SpanFrom(start), name, parameterized));
            if (!Accept(TokenKind.Comma))
            {
                break;
            }
        }

        if (stopAtFrom && !IsKeyword("FROM"))
        {
            throw Error("',' or FROM");
        }

        return list.ToImmutable();
    }

    private bool IsImportableKeyword() =>
        Current.Kind == TokenKind.Keyword && Current.Text is "UTF8String" or "BMPString" or "UniversalString";

    private ImmutableArray<ImportGroupSyntax> ParseImports()
    {
        ExpectKeyword("IMPORTS");
        var groups = ImmutableArray.CreateBuilder<ImportGroupSyntax>();
        while (Current.IsName || IsImportableKeyword())
        {
            var start = _pos;
            var symbols = ParseSymbolList(stopAtFrom: true);
            ExpectKeyword("FROM");
            var moduleName = Name(Expect(TokenKind.TypeReference, "module reference"));
            ImmutableArray<OidComponentSyntax>? oid = null;
            ValueSyntax? assigned = null;
            if (Is(TokenKind.LeftBrace))
            {
                oid = ParseOidComponents();
            }
            else if (Is(TokenKind.Identifier) && PeekToken(1).Kind != TokenKind.Comma && !PeekToken(1).IsKeyword("FROM") && !(PeekToken(1).Kind == TokenKind.LeftBrace && PeekToken(2).Kind == TokenKind.RightBrace))
            {
                assigned = new ValueReferenceSyntax(_source, Current.Span, null, Name(Advance()), null);
            }

            if (AcceptKeyword("WITH"))
            {
                // WITH SUCCESSORS / WITH DESCENDANTS (X.680 13.16).
                if (Is(TokenKind.TypeReference))
                {
                    Advance();
                }
            }

            groups.Add(new ImportGroupSyntax(_source, SpanFrom(start), symbols, moduleName, oid, assigned));
        }

        Expect(TokenKind.Semicolon, "';'");
        return groups.ToImmutable();
    }

    // ------------------------------------------------------------------ assignments

    private AssignmentSyntax? ParseAssignment()
    {
        var start = _pos;
        if (!Current.IsName)
        {
            throw Error("assignment");
        }

        var nameTok = Advance();
        var name = Name(nameTok);
        var parameters = ImmutableArray<ParameterSyntax>.Empty;
        if (Is(TokenKind.LeftBrace))
        {
            parameters = ParseParameterList();
        }

        if (nameTok.Kind == TokenKind.TypeReference)
        {
            if (IsKeyword("MACRO"))
            {
                Advance();
                _diagnostics.Report(DiagnosticCatalog.DeprecatedNotation, name.Location, "MACRO", "the macro definition is skipped; use information object classes instead");
                Expect(TokenKind.Assignment, "'::='");
                ExpectKeyword("BEGIN");
                while (!Is(TokenKind.EndOfFile) && !IsKeyword("END"))
                {
                    Advance();
                }

                ExpectKeyword("END");
                return new MacroAssignmentSyntax(_source, SpanFrom(start), name);
            }

            if (Accept(TokenKind.Assignment))
            {
                if (IsKeyword("CLASS"))
                {
                    var cls = ParseObjectClassDefinition();
                    return new ClassAssignmentSyntax(_source, SpanFrom(start), name, parameters, cls, DocumentationFor(start, _pos - 1));
                }

                if ((IsKeyword("TYPE-IDENTIFIER") || IsKeyword("ABSTRACT-SYNTAX")) && PeekToken(1).Kind != TokenKind.Dot)
                {
                    var t = Advance();
                    return new ClassAssignmentSyntax(_source, SpanFrom(start), name, parameters, new BuiltinClassSyntax(_source, t.Span, t.Text), DocumentationFor(start, _pos - 1));
                }

                var typeStart = _pos;
                var type = ParseType();
                var lastTypeTok = _pos - 1;
                return new TypeAssignmentSyntax(_source, SpanFrom(start), name, parameters, type, DocumentationFor(start, StructureHeaderEnd(typeStart, lastTypeTok)));
            }

            var governor = ParseType();
            Expect(TokenKind.Assignment, "'::='");
            var set = ParseBracedElementSetSpecs();
            return new SetAssignmentSyntax(_source, SpanFrom(start), name, parameters, governor, set, DocumentationFor(start, _pos - 1));
        }

        var valueType = ParseType();
        Expect(TokenKind.Assignment, "'::='");
        var value = ParseValue();
        return new ValueAssignmentSyntax(_source, SpanFrom(start), name, parameters, valueType, value, DocumentationFor(start, _pos - 1));
    }

    /// <summary>For trailing comment lookup of a type assignment, use the last token only if the type is on one line.</summary>
    private int StructureHeaderEnd(int typeStart, int lastTypeTok) =>
        typeStart < _tokens.Count && lastTypeTok < _tokens.Count && _tokens[typeStart].Line == _tokens[lastTypeTok].Line ? lastTypeTok : -1;

    private ImmutableArray<ParameterSyntax> ParseParameterList()
    {
        Expect(TokenKind.LeftBrace, "'{'");
        var list = ImmutableArray.CreateBuilder<ParameterSyntax>();
        do
        {
            var start = _pos;
            TypeSyntax? governor = null;
            if (HasColonBeforeSeparator())
            {
                governor = ParseType();
                Expect(TokenKind.Colon, "':'");
            }

            if (!Current.IsName)
            {
                throw Error("parameter name");
            }

            list.Add(new ParameterSyntax(_source, SpanFrom(start), governor, Name(Advance())));
        }
        while (Accept(TokenKind.Comma));

        Expect(TokenKind.RightBrace, "'}'");
        return list.ToImmutable();
    }

    private bool HasColonBeforeSeparator()
    {
        var depth = 0;
        for (var i = 0; _pos + i < _end; i++)
        {
            var t = PeekToken(i);
            switch (t.Kind)
            {
                case TokenKind.LeftBrace or TokenKind.LeftParen or TokenKind.LeftBracket:
                    depth++;
                    break;
                case TokenKind.RightBrace or TokenKind.RightParen or TokenKind.RightBracket:
                    if (depth == 0)
                    {
                        return false;
                    }

                    depth--;
                    break;
                case TokenKind.Comma when depth == 0:
                    return false;
                case TokenKind.Colon when depth == 0:
                    return true;
            }
        }

        return false;
    }
}
