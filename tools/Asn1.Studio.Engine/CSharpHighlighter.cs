using Asn1.Syntax;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Asn1.Studio.Engine;

/// <summary>
/// Highlighting of C# (the generated code) from Roslyn's own parser, in the same (start, length, class) triples and
/// classes (<see cref="SyntaxClass"/>) as <see cref="Workbench.Highlight"/>, so both editors look alike.
/// </summary>
/// <remarks>
/// Roslyn is lazy-loaded with the C# generator (see Asn1.Studio.csproj): this class has no Roslyn-typed fields, so
/// that only calling <see cref="Highlight"/> needs the assembly.
/// </remarks>
public static class CSharpHighlighter
{
    /// <summary>Classifies C# source text.</summary>
    public static int[] Highlight(string text)
    {
        var root = CSharpSyntaxTree.ParseText(text, new CSharpParseOptions(LanguageVersion.Preview)).GetRoot();
        var spans = new List<(int Start, int Length, SyntaxClass Class)>();
        foreach (var token in root.DescendantTokens(descendIntoTrivia: true))
        {
            AddTrivia(spans, token.LeadingTrivia);
            if (Classify(token) is { } cls && token.Span.Length > 0)
            {
                Add(spans, token.Span.Start, token.Span.Length, cls);
            }

            AddTrivia(spans, token.TrailingTrivia);
        }

        // Structured trivia (directives) is visited before the comments next to it: order by position.
        spans.Sort((a, b) => a.Start.CompareTo(b.Start));
        var result = new int[spans.Count * 3];
        for (var i = 0; i < spans.Count; i++)
        {
            result[i * 3] = spans[i].Start;
            result[(i * 3) + 1] = spans[i].Length;
            result[(i * 3) + 2] = (int)spans[i].Class;
        }

        return result;
    }

    private static void Add(List<(int, int, SyntaxClass)> spans, int start, int length, SyntaxClass cls) => spans.Add((start, length, cls));

    private static void AddTrivia(List<(int, int, SyntaxClass)> spans, SyntaxTriviaList trivia)
    {
        foreach (var t in trivia)
        {
            // Comments, including XML documentation, and directives (#nullable, #pragma) without their structure.
            if (t.IsKind(SyntaxKind.SingleLineCommentTrivia) || t.IsKind(SyntaxKind.MultiLineCommentTrivia))
            {
                Add(spans, t.Span.Start, t.Span.Length, SyntaxClass.Comment);
            }
            else if (t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) || t.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))
            {
                // The span of documentation starts after its first "///"; the full span includes it.
                Add(spans, t.FullSpan.Start, t.FullSpan.Length, SyntaxClass.Comment);
            }
        }
    }

    private static SyntaxClass? Classify(SyntaxToken token)
    {
        if (token.Parent is { } parent && parent.Ancestors().Any(a => a is DocumentationCommentTriviaSyntax))
        {
            // Tokens inside XML documentation are covered by the comment span.
            return null;
        }

        if (token.Parent is DirectiveTriviaSyntax || token.Parent?.Parent is DirectiveTriviaSyntax)
        {
            return SyntaxClass.Keyword;
        }

        var kind = token.Kind();
        if (SyntaxFacts.IsPredefinedType(kind))
        {
            return SyntaxClass.BuiltinType;
        }

        if (SyntaxFacts.IsKeywordKind(kind) || SyntaxFacts.IsContextualKeyword(kind) && token.Parent is not IdentifierNameSyntax)
        {
            return kind is SyntaxKind.TrueKeyword or SyntaxKind.FalseKeyword or SyntaxKind.NullKeyword ? SyntaxClass.BuiltinValue : SyntaxClass.Keyword;
        }

        switch (kind)
        {
            case SyntaxKind.IdentifierToken:
                return IsTypeName(token) ? SyntaxClass.TypeReference : SyntaxClass.Identifier;
            case SyntaxKind.NumericLiteralToken:
                return SyntaxClass.Number;
            case SyntaxKind.StringLiteralToken or SyntaxKind.CharacterLiteralToken or SyntaxKind.Utf8StringLiteralToken
                or SyntaxKind.InterpolatedStringStartToken or SyntaxKind.InterpolatedStringEndToken or SyntaxKind.InterpolatedStringTextToken
                or SyntaxKind.SingleLineRawStringLiteralToken or SyntaxKind.MultiLineRawStringLiteralToken:
                return SyntaxClass.String;
            case SyntaxKind.OpenBraceToken or SyntaxKind.CloseBraceToken or SyntaxKind.OpenParenToken or SyntaxKind.CloseParenToken
                or SyntaxKind.OpenBracketToken or SyntaxKind.CloseBracketToken:
                return SyntaxClass.Bracket;
            case SyntaxKind.BadToken:
                return SyntaxClass.Invalid;
            default:
                return SyntaxFacts.IsPunctuation(kind) ? SyntaxClass.Operator : null;
        }
    }

    /// <summary>True for identifiers that name a type: declarations, and names in type-only positions.</summary>
    private static bool IsTypeName(SyntaxToken token) => token.Parent switch
    {
        BaseTypeDeclarationSyntax or DelegateDeclarationSyntax or TypeParameterSyntax => true,
        ConstructorDeclarationSyntax => true,
        SimpleNameSyntax name => SyntaxFacts.IsInTypeOnlyContext(name) || IsQualifierOfType(name),
        _ => false,
    };

    // `global::Asn1.Runtime.SequenceType<T>`: the right-most name of a qualified type name is the type.
    private static bool IsQualifierOfType(SimpleNameSyntax name) =>
        name.Parent is QualifiedNameSyntax q && q.Right == name && SyntaxFacts.IsInTypeOnlyContext(q);
}
