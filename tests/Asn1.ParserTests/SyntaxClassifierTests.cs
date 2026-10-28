using Asn1.Syntax;
using Xunit;

namespace Asn1.ParserTests;

/// <summary>Highlighting classes from the DotAsn lexer (used by the Studio's editor).</summary>
public class SyntaxClassifierTests
{
    private static List<(string Text, SyntaxClass Class)> Classify(string text) =>
        [.. SyntaxClassifier.Classify(text).Select(s => (text.Substring(s.Start, s.Length), s.Class))];

    [Fact]
    public void ClassifiesEveryLexicalItem()
    {
        const string text = """
            M-1 DEFINITIONS AUTOMATIC TAGS ::= BEGIN -- a comment --
              T ::= SEQUENCE { a INTEGER (MIN..10) OPTIONAL, b UTF8String DEFAULT "x", c BIT STRING DEFAULT '0101'B }
              C ::= CLASS { &id INTEGER UNIQUE, &Type }
              /* block /* nested */ comment */
              v T ::= { a 5, b "y", c 'FF'H }
            END
            """;
        var spans = Classify(text);

        Assert.Contains(("M-1", SyntaxClass.TypeReference), spans);
        Assert.Contains(("DEFINITIONS", SyntaxClass.Keyword), spans);
        Assert.Contains(("OPTIONAL", SyntaxClass.Keyword), spans);
        Assert.Contains(("SEQUENCE", SyntaxClass.BuiltinType), spans);
        Assert.Contains(("UTF8String", SyntaxClass.BuiltinType), spans);
        Assert.Contains(("MIN", SyntaxClass.BuiltinValue), spans);
        Assert.Contains(("T", SyntaxClass.TypeReference), spans);
        Assert.Contains(("a", SyntaxClass.Identifier), spans);
        Assert.Contains(("&id", SyntaxClass.FieldReference), spans);
        Assert.Contains(("&Type", SyntaxClass.FieldReference), spans);
        Assert.Contains(("10", SyntaxClass.Number), spans);
        Assert.Contains(("\"x\"", SyntaxClass.String), spans);
        Assert.Contains(("'0101'B", SyntaxClass.String), spans);
        Assert.Contains(("'FF'H", SyntaxClass.String), spans);
        Assert.Contains(("::=", SyntaxClass.Operator), spans);
        Assert.Contains(("..", SyntaxClass.Operator), spans);
        Assert.Contains(("{", SyntaxClass.Bracket), spans);
        Assert.Contains(("-- a comment --", SyntaxClass.Comment), spans);
        Assert.Contains(("/* block /* nested */ comment */", SyntaxClass.Comment), spans);
    }

    [Fact]
    public void SpansAreOrderedAndDoNotOverlap()
    {
        var spans = SyntaxClassifier.Classify("A ::= INTEGER -- c -- (0..5) /* d */ B ::= BOOLEAN");
        for (var i = 1; i < spans.Count; i++)
        {
            Assert.True(spans[i].Start >= spans[i - 1].Start + spans[i - 1].Length, $"span {i} overlaps its predecessor");
        }
    }

    [Fact]
    public void IncompleteTextIsStillClassified()
    {
        // Editors classify text while it is being typed: unterminated strings and comments must not throw.
        var spans = Classify("A ::= SEQUENCE { s UTF8String DEFAULT \"open");
        Assert.Contains(("SEQUENCE", SyntaxClass.BuiltinType), spans);
        Assert.NotEmpty(SyntaxClassifier.Classify("/* never closed"));
        Assert.Empty(SyntaxClassifier.Classify(string.Empty));
    }
}
