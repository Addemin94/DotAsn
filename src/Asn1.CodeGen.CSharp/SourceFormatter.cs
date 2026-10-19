using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Asn1.CodeGen.CSharp;

/// <summary>
/// Turns a generated syntax tree into deterministic source text: Roslyn's <c>NormalizeWhitespace</c>,
/// then XML documentation comments (attached to members as annotations), blank lines between members, and line
/// breaks in long argument lists and collection expressions.
/// </summary>
internal static class SourceFormatter
{
    private const string DocKind = "DotAsn.Doc";
    private const char Separator = '\u0001';
    private const string Eol = "\n";
    private const int MaxLine = 140;

    /// <summary>Attaches an XML documentation comment (summary and remarks) to a member.</summary>
    public static T WithDoc<T>(this T node, string? summary, string? remarks = null)
        where T : SyntaxNode =>
        string.IsNullOrWhiteSpace(summary) && string.IsNullOrWhiteSpace(remarks)
            ? node
            : node.WithAdditionalAnnotations(new SyntaxAnnotation(DocKind, (summary ?? string.Empty) + Separator + (remarks ?? string.Empty)));

    /// <summary>Formats a compilation unit as source text with LF line endings.</summary>
    public static string Format(CompilationUnitSyntax unit)
    {
        var normalized = unit.NormalizeWhitespace("    ", Eol);
        var laidOut = new Layout().Visit(normalized)!;
        var text = laidOut.ToFullString();
        return text.EndsWith(Eol, StringComparison.Ordinal) ? text : text + Eol;
    }

    private sealed class Layout : CSharpSyntaxRewriter
    {
        private readonly Stack<int> _indent = new();

        public override SyntaxNode? Visit(SyntaxNode? node)
        {
            if (node is not MemberDeclarationSyntax member || node is BaseNamespaceDeclarationSyntax)
            {
                return base.Visit(node);
            }

            var indent = 4 * member.Ancestors().Count(a => a is BaseTypeDeclarationSyntax);
            _indent.Push(indent);
            var visited = base.Visit(node)!;
            _indent.Pop();

            // Exactly one line break after a member; blank lines come from the next member's leading trivia. (An enum
            // member is followed by its comma separator, which carries the line break.)
            if (member is not EnumMemberDeclarationSyntax)
            {
                var last = visited.GetLastToken();
                visited = visited.ReplaceToken(last, last.WithTrailingTrivia(EndOfLine(Eol)));
            }
            return visited.WithLeadingTrivia(Leading(member, indent));
        }

        public override SyntaxNode? VisitFileScopedNamespaceDeclaration(FileScopedNamespaceDeclarationSyntax node)
        {
            // A blank line between the file header and the namespace, and after the namespace declaration.
            var visited = (FileScopedNamespaceDeclarationSyntax)base.VisitFileScopedNamespaceDeclaration(node)!;
            var keyword = visited.NamespaceKeyword;
            return visited.WithNamespaceKeyword(keyword.WithLeadingTrivia(keyword.LeadingTrivia.Add(EndOfLine(Eol))));
        }

        public override SyntaxNode? VisitBinaryExpression(BinaryExpressionSyntax node)
        {
            var visited = (BinaryExpressionSyntax)base.VisitBinaryExpression(node)!;
            if (_indent.Count == 0 || !node.IsKind(SyntaxKind.LogicalAndExpression) || node.Parent.IsKind(SyntaxKind.LogicalAndExpression)
                || node.Span.Length + _indent.Peek() <= MaxLine)
            {
                return visited;
            }

            // One condition per line: "a\n    && b\n    && c".
            var indent = Whitespace(new string(' ', _indent.Peek() + 4));
            var operators = visited.DescendantTokens().Where(t => t.IsKind(SyntaxKind.AmpersandAmpersandToken) && t.Parent is BinaryExpressionSyntax b && IsChainOf(b, visited)).ToList();
            var before = operators.Select(o => o.GetPreviousToken()).ToList();
            return visited.ReplaceTokens(operators.Concat(before), (original, _) =>
                operators.Contains(original)
                    ? original.WithLeadingTrivia(EndOfLine(Eol), indent)
                    : original.WithTrailingTrivia());
        }

        private static bool IsChainOf(BinaryExpressionSyntax b, BinaryExpressionSyntax root)
        {
            for (SyntaxNode? n = b; n is not null; n = n.Parent)
            {
                if (n == root)
                {
                    return true;
                }

                if (!n.IsKind(SyntaxKind.LogicalAndExpression))
                {
                    return false;
                }
            }

            return false;
        }

        private static SyntaxTriviaList Leading(MemberDeclarationSyntax member, int indent)
        {
            var trivia = new List<SyntaxTrivia>();
            if (BlankLineBefore(member))
            {
                trivia.Add(EndOfLine(Eol));
            }

            var ws = new string(' ', indent);
            if (member.GetAnnotations(DocKind).FirstOrDefault()?.Data is { } data)
            {
                var parts = data.Split(Separator);
                if (indent > 0)
                {
                    trivia.Add(Whitespace(ws));
                }

                trivia.Add(Trivia(Doc(ws, parts[0], parts[1])));
            }

            if (indent > 0)
            {
                trivia.Add(Whitespace(ws));
            }

            return TriviaList(trivia);
        }

        private static bool BlankLineBefore(MemberDeclarationSyntax member) => member.Parent switch
        {
            BaseNamespaceDeclarationSyntax => true,
            TypeDeclarationSyntax t => t.Members.IndexOf(member) > 0,
            CompilationUnitSyntax u => u.Members.IndexOf(member) > 0,
            _ => false,
        };

        public override SyntaxNode? VisitArgumentList(ArgumentListSyntax node)
        {
            if (_indent.Count == 0 || node.Arguments.Count < 2 || node.Span.Length + _indent.Peek() <= MaxLine)
            {
                return base.VisitArgumentList(node);
            }

            var inner = _indent.Peek() + 4;
            _indent.Push(inner);
            var visited = (ArgumentListSyntax)base.VisitArgumentList(node)!;
            _indent.Pop();
            var arguments = visited.Arguments.Select(a => a.WithLeadingTrivia(EndOfLine(Eol), Whitespace(new string(' ', inner))));
            var separators = visited.Arguments.GetSeparators().Select(s => s.WithTrailingTrivia());
            return visited.WithArguments(SeparatedList(arguments, separators));
        }

        public override SyntaxNode? VisitCollectionExpression(CollectionExpressionSyntax node)
        {
            if (_indent.Count == 0 || node.Elements.Count == 0 || node.Span.Length + _indent.Peek() <= MaxLine)
            {
                return base.VisitCollectionExpression(node);
            }

            var outer = _indent.Peek();
            var inner = outer + 4;
            _indent.Push(inner);
            var visited = (CollectionExpressionSyntax)base.VisitCollectionExpression(node)!;
            _indent.Pop();
            var elements = visited.Elements.Select(e => e.WithLeadingTrivia(EndOfLine(Eol), Whitespace(new string(' ', inner))));
            var separators = visited.Elements.GetSeparators().Select(s => s.WithTrailingTrivia());
            return visited
                .WithElements(SeparatedList(elements, separators))
                .WithCloseBracketToken(visited.CloseBracketToken.WithLeadingTrivia(EndOfLine(Eol), Whitespace(new string(' ', outer))));
        }
    }

    /// <summary>A single-line XML documentation comment built from Roslyn XML syntax nodes.</summary>
    private static DocumentationCommentTriviaSyntax Doc(string indent, string summary, string remarks)
    {
        XmlTextSyntax NextLine() => XmlText(TokenList(
            XmlTextNewLine(TriviaList(), Eol, Eol, TriviaList()),
            XmlTextLiteral(TriviaList(Whitespace(indent), DocumentationCommentExterior("///")), " ", " ", TriviaList())));

        var nodes = new List<XmlNodeSyntax>
        {
            XmlText(XmlTextLiteral(TriviaList(DocumentationCommentExterior("///")), " ", " ", TriviaList())),
        };

        if (!string.IsNullOrWhiteSpace(summary))
        {
            var content = new List<XmlNodeSyntax>();
            foreach (var line in summary.Split('\n'))
            {
                content.Add(NextLine());
                content.Add(XmlText(line.TrimEnd()));
            }

            content.Add(NextLine());
            nodes.Add(XmlElement(XmlElementStartTag(XmlName("summary")), List(content), XmlElementEndTag(XmlName("summary"))));
            if (!string.IsNullOrWhiteSpace(remarks))
            {
                nodes.Add(NextLine());
            }
        }

        if (!string.IsNullOrWhiteSpace(remarks))
        {
            nodes.Add(XmlElement(XmlElementStartTag(XmlName("remarks")), SingletonList<XmlNodeSyntax>(XmlText(remarks.Replace('\n', ' '))), XmlElementEndTag(XmlName("remarks"))));
        }

        nodes.Add(XmlText(XmlTextNewLine(TriviaList(), Eol, Eol, TriviaList())));
        return DocumentationCommentTrivia(SyntaxKind.SingleLineDocumentationCommentTrivia, List(nodes));
    }
}
