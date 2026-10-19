using System.Collections.Immutable;

namespace Asn1.Syntax;

public sealed partial class Parser
{
    private ObjectClassDefinitionSyntax ParseObjectClassDefinition()
    {
        var start = _pos;
        ExpectKeyword("CLASS");
        Expect(TokenKind.LeftBrace, "'{'");
        var fields = ImmutableArray.CreateBuilder<FieldSpecSyntax>();
        do
        {
            fields.Add(ParseFieldSpec());
        }
        while (Accept(TokenKind.Comma));

        Expect(TokenKind.RightBrace, "'}'");
        ImmutableArray<SyntaxTokenSpecSyntax>? withSyntax = null;
        if (IsKeyword("WITH") && PeekToken(1).IsKeyword("SYNTAX"))
        {
            Advance();
            Advance();
            withSyntax = ParseSyntaxList();
        }

        return new ObjectClassDefinitionSyntax(_source, SpanFrom(start), fields.ToImmutable(), withSyntax);
    }

    private FieldSpecSyntax ParseFieldSpec()
    {
        var start = _pos;
        var fieldTok = Current;
        if (fieldTok.Kind is not (TokenKind.TypeFieldReference or TokenKind.ValueFieldReference))
        {
            throw Error("field reference (&name)");
        }

        Advance();
        var name = Name(fieldTok);
        TypeSyntax? governor = null;
        var governorPath = ImmutableArray<NameSyntax>.Empty;
        FieldSpecKind kind;
        var unique = false;
        var optional = false;
        SyntaxNode? defaultSetting = null;

        if (fieldTok.Kind == TokenKind.ValueFieldReference)
        {
            if (Is(TokenKind.TypeFieldReference))
            {
                kind = FieldSpecKind.VariableTypeValue;
                governorPath = ParseFieldPath();
            }
            else
            {
                kind = FieldSpecKind.ValueOrObject;
                governor = ParseType();
                unique = AcceptKeyword("UNIQUE");
            }

            if (AcceptKeyword("OPTIONAL"))
            {
                optional = true;
            }
            else if (AcceptKeyword("DEFAULT"))
            {
                defaultSetting = ParseValue();
            }
        }
        else if (Is(TokenKind.Comma) || Is(TokenKind.RightBrace) || IsKeyword("OPTIONAL") || IsKeyword("DEFAULT"))
        {
            kind = FieldSpecKind.Type;
            if (AcceptKeyword("OPTIONAL"))
            {
                optional = true;
            }
            else if (AcceptKeyword("DEFAULT"))
            {
                defaultSetting = ParseType();
            }
        }
        else
        {
            if (Is(TokenKind.TypeFieldReference))
            {
                kind = FieldSpecKind.VariableTypeValueSet;
                governorPath = ParseFieldPath();
            }
            else
            {
                kind = FieldSpecKind.ValueSetOrObjectSet;
                governor = ParseType();
            }

            if (AcceptKeyword("OPTIONAL"))
            {
                optional = true;
            }
            else if (AcceptKeyword("DEFAULT"))
            {
                defaultSetting = ParseBracedElementSetSpecs();
            }
        }

        return new FieldSpecSyntax(_source, SpanFrom(start), name, kind, governor, governorPath, unique, optional, defaultSetting);
    }

    private ImmutableArray<SyntaxTokenSpecSyntax> ParseSyntaxList()
    {
        Expect(TokenKind.LeftBrace, "'{'");
        var items = ParseSyntaxItems(TokenKind.RightBrace);
        Expect(TokenKind.RightBrace, "'}'");
        return items;
    }

    private ImmutableArray<SyntaxTokenSpecSyntax> ParseSyntaxItems(TokenKind terminator)
    {
        var items = ImmutableArray.CreateBuilder<SyntaxTokenSpecSyntax>();
        while (!Is(terminator) && !Is(TokenKind.EndOfFile))
        {
            var start = _pos;
            var t = Current;
            switch (t.Kind)
            {
                case TokenKind.LeftBracket:
                    {
                        Advance();
                        var group = ParseSyntaxItems(TokenKind.RightBracket);
                        Expect(TokenKind.RightBracket, "']'");
                        items.Add(new SyntaxTokenSpecSyntax(_source, SpanFrom(start), null, null, group));
                        break;
                    }

                case TokenKind.LeftVersionBrackets:
                    {
                        // "[[" inside WITH SYNTAX is two nested optional groups.
                        Advance();
                        var inner = ParseSyntaxItems(TokenKind.RightBracket);
                        var innerGroup = new SyntaxTokenSpecSyntax(_source, SpanFrom(start), null, null, inner);
                        var outer = ImmutableArray.CreateBuilder<SyntaxTokenSpecSyntax>();
                        outer.Add(innerGroup);
                        if (!Accept(TokenKind.RightVersionBrackets))
                        {
                            Expect(TokenKind.RightBracket, "']'");
                            outer.AddRange(ParseSyntaxItems(TokenKind.RightBracket));
                            Expect(TokenKind.RightBracket, "']'");
                        }

                        items.Add(new SyntaxTokenSpecSyntax(_source, SpanFrom(start), null, null, outer.ToImmutable()));
                        break;
                    }

                case TokenKind.RightVersionBrackets when terminator == TokenKind.RightBracket:
                    // "]]" closes two groups; split it by letting the caller see one ']'.
                    return items.ToImmutable();
                case TokenKind.TypeFieldReference or TokenKind.ValueFieldReference:
                    Advance();
                    items.Add(new SyntaxTokenSpecSyntax(_source, t.Span, null, Name(t), default));
                    break;
                case TokenKind.Comma:
                    Advance();
                    items.Add(new SyntaxTokenSpecSyntax(_source, t.Span, ",", null, default));
                    break;
                case TokenKind.TypeReference or TokenKind.Keyword:
                    Advance();
                    items.Add(new SyntaxTokenSpecSyntax(_source, t.Span, t.Text, null, default));
                    break;
                default:
                    throw Error("syntax word, field reference or '['");
            }
        }

        return items.ToImmutable();
    }
}
