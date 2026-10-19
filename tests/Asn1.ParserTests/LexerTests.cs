using Asn1.Diagnostics;
using Asn1.Syntax;
using Xunit;

namespace Asn1.ParserTests;

public class LexerTests
{
    private static (List<Token> Tokens, List<Diagnostic> Diagnostics, LexResult Result) Lex(string text)
    {
        var bag = new DiagnosticBag();
        var result = Lexer.Lex(new SourceText("t.asn", text), bag);
        return ([.. result.Tokens], [.. bag.ToImmutable()], result);
    }

    [Fact]
    public void RecognizesPunctuationAndRangeForms()
    {
        var (tokens, diags, _) = Lex("::= { } ( ) [ ] [[ ]] , . .. ... - : ; < > | ^ ! @");
        Assert.Empty(diags);
        Assert.Equal(
            [TokenKind.Assignment, TokenKind.LeftBrace, TokenKind.RightBrace, TokenKind.LeftParen, TokenKind.RightParen, TokenKind.LeftBracket, TokenKind.RightBracket,
             TokenKind.LeftVersionBrackets, TokenKind.RightVersionBrackets, TokenKind.Comma, TokenKind.Dot, TokenKind.Range, TokenKind.Ellipsis, TokenKind.Hyphen,
             TokenKind.Colon, TokenKind.Semicolon, TokenKind.LessThan, TokenKind.GreaterThan, TokenKind.Bar, TokenKind.Caret, TokenKind.Exclamation, TokenKind.At, TokenKind.EndOfFile],
            tokens.Select(t => t.Kind));
    }

    [Theory]
    [InlineData("0..255", new[] { TokenKind.Number, TokenKind.Range, TokenKind.Number })]
    [InlineData("1.5", new[] { TokenKind.RealNumber })]
    [InlineData("1.", new[] { TokenKind.RealNumber })]
    [InlineData("1...2", new[] { TokenKind.RealNumber, TokenKind.Range, TokenKind.Number })]
    [InlineData("2.5e-3", new[] { TokenKind.RealNumber })]
    public void NumbersAndRanges(string text, TokenKind[] kinds) =>
        Assert.Equal(kinds.Append(TokenKind.EndOfFile), Lex(text).Tokens.Select(t => t.Kind));

    [Fact]
    public void IdentifiersStopAtCommentsAndClassifyCase()
    {
        var (tokens, _, result) = Lex("id-foo--comment-- Bar-Baz SEQUENCE &Type &value\n");
        Assert.Equal(["id-foo", "Bar-Baz", "SEQUENCE", "&Type", "&value"], tokens.SkipLast(1).Select(t => t.Text));
        Assert.Equal([TokenKind.Identifier, TokenKind.TypeReference, TokenKind.Keyword, TokenKind.TypeFieldReference, TokenKind.ValueFieldReference], tokens.SkipLast(1).Select(t => t.Kind));
        Assert.Equal("comment", Assert.Single(result.Comments).Text);
    }

    [Fact]
    public void CommentsEndAtDoubleHyphenOrLineEnd()
    {
        var (tokens, _, result) = Lex("a -- one -- b -- two\nc /* nested /* block */ comment */ d");
        Assert.Equal(["a", "b", "c", "d"], tokens.SkipLast(1).Select(t => t.Text));
        Assert.Equal(["one", "two", "nested /* block */ comment"], result.Comments.Select(c => c.Text));
    }

    [Fact]
    public void StringLiteralsHandleQuotesAndLineBreaks()
    {
        var (tokens, diags, _) = Lex("\"say \"\"hi\"\"\" \"multi   \n    line\" '0101'B 'aF'H");
        Assert.DoesNotContain(diags, d => d.IsError && d.Id != "ASN1004");
        Assert.Equal("say \"hi\"", tokens[0].Text);
        Assert.Equal("multiline", tokens[1].Text);
        Assert.Equal(TokenKind.BString, tokens[2].Kind);
        Assert.Equal("0101", tokens[2].Text);
        Assert.Equal(TokenKind.HString, tokens[3].Kind);
    }

    [Theory]
    [InlineData("\"unterminated", "ASN1002")]
    [InlineData("/* never closed", "ASN1003")]
    [InlineData("'012'B", "ASN1004")]
    [InlineData("'01'X", "ASN1004")]
    [InlineData("$", "ASN1001")]
    [InlineData("bad_name", "ASN1005")]
    public void MalformedTokensAreReported(string text, string id) =>
        Assert.Contains(Lex(text).Diagnostics, d => d.Id == id);

    [Fact]
    public void TokenPositionsAreExact()
    {
        var (tokens, _, result) = Lex("A ::=\n  INTEGER");
        var integer = tokens[2];
        var pos = result.Source.GetLinePosition(integer.Span.Start);
        Assert.Equal(new LinePosition(2, 3), pos);
        Assert.True(integer.StartsLine);
    }
}
