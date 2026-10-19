using System.Collections.Immutable;

namespace Asn1.Syntax;

public sealed partial class Parser
{
    /// <summary>Parses a parenthesized constraint.</summary>
    public ConstraintSyntax ParseConstraint()
    {
        Enter();
        try
        {
            var start = _pos;
            Expect(TokenKind.LeftParen, "'('");
            var spec = ParseConstraintSpec();
            ExceptionSpecSyntax? exception = null;
            if (Is(TokenKind.Exclamation))
            {
                exception = ParseExceptionSpec();
            }

            Expect(TokenKind.RightParen, "')'");
            return new ConstraintSyntax(_source, SpanFrom(start), spec, exception);
        }
        finally
        {
            Leave();
        }
    }

    private ConstraintSpecSyntax ParseConstraintSpec()
    {
        var start = _pos;
        if (IsKeyword("CONSTRAINED"))
        {
            Advance();
            ExpectKeyword("BY");
            if (!Is(TokenKind.LeftBrace))
            {
                throw Error("'{'");
            }

            SkipBalanced();
            return new UserDefinedConstraintSyntax(_source, SpanFrom(start));
        }

        if (IsKeyword("CONTAINING") || IsKeyword("ENCODED"))
        {
            TypeSyntax? contained = null;
            ValueSyntax? encodedBy = null;
            if (AcceptKeyword("CONTAINING"))
            {
                contained = ParseType();
            }

            if (AcceptKeyword("ENCODED"))
            {
                ExpectKeyword("BY");
                encodedBy = ParseValue();
            }

            return new ContentsConstraintSyntax(_source, SpanFrom(start), contained, encodedBy);
        }

        if (Is(TokenKind.LeftBrace) && LooksLikeTableConstraint())
        {
            var objectSet = ParseBracedElementSetSpecs();
            var at = ImmutableArray.CreateBuilder<AtNotationSyntax>();
            if (Is(TokenKind.LeftBrace))
            {
                Advance();
                do
                {
                    at.Add(ParseAtNotation());
                }
                while (Accept(TokenKind.Comma));

                Expect(TokenKind.RightBrace, "'}'");
            }

            return new TableConstraintSyntax(_source, SpanFrom(start), objectSet, at.ToImmutable());
        }

        return ParseElementSetSpecs();
    }

    private bool LooksLikeTableConstraint()
    {
        var close = FindMatching(_pos);
        if (close < 0)
        {
            return false;
        }

        var after = close + 1 < _tokens.Count ? _tokens[close + 1] : null;
        if (after is not null && after.Kind == TokenKind.LeftBrace && close + 2 < _tokens.Count && _tokens[close + 2].Kind == TokenKind.At)
        {
            return true;
        }

        var count = close - _pos - 1;
        if (count == 1 && _tokens[_pos + 1].Kind == TokenKind.TypeReference)
        {
            return true;
        }

        if (count >= 1 && _tokens[_pos + 1].Kind == TokenKind.TypeReference && (_tokens[_pos + 2].Kind == TokenKind.LeftBrace || (count == 3 && _tokens[_pos + 2].Kind == TokenKind.Dot && _tokens[_pos + 3].Kind == TokenKind.TypeReference)))
        {
            return true;
        }

        // Inline object sets use '|' or '...' at the top level of the braces.
        var depth = 0;
        for (var i = _pos + 1; i < close; i++)
        {
            var k = _tokens[i].Kind;
            if (k is TokenKind.LeftBrace or TokenKind.LeftParen)
            {
                depth++;
            }
            else if (k is TokenKind.RightBrace or TokenKind.RightParen)
            {
                depth--;
            }
            else if (depth == 0 && (k is TokenKind.Bar or TokenKind.Ellipsis || _tokens[i].IsKeyword("UNION")))
            {
                return true;
            }
        }

        return false;
    }

    private AtNotationSyntax ParseAtNotation()
    {
        var start = _pos;
        Expect(TokenKind.At, "'@'");
        var level = 0;
        while (Accept(TokenKind.Dot))
        {
            level++;
        }

        var path = ImmutableArray.CreateBuilder<NameSyntax>();
        path.Add(Name(Expect(TokenKind.Identifier, "component identifier")));
        while (Is(TokenKind.Dot) && PeekToken(1).Kind == TokenKind.Identifier)
        {
            Advance();
            path.Add(Name(Advance()));
        }

        return new AtNotationSyntax(_source, SpanFrom(start), level, path.ToImmutable());
    }

    /// <summary>Parses <c>{ ElementSetSpecs }</c> (value sets and object sets).</summary>
    public ElementSetSpecsSyntax ParseBracedElementSetSpecs()
    {
        Expect(TokenKind.LeftBrace, "'{'");
        var specs = Is(TokenKind.RightBrace) ? new ElementSetSpecsSyntax(_source, Current.Span, null, false, null) : ParseElementSetSpecs();
        Expect(TokenKind.RightBrace, "'}'");
        return specs;
    }

    /// <summary>Parses <c>Root [, ... [, Additions]]</c>.</summary>
    public ElementSetSpecsSyntax ParseElementSetSpecs()
    {
        var start = _pos;
        ElementSetSyntax? root = null;
        ElementSetSyntax? additions = null;
        var extensible = false;
        if (Is(TokenKind.Ellipsis))
        {
            Advance();
            extensible = true;
        }
        else
        {
            root = ParseElementSet();
            if (Is(TokenKind.Comma) && PeekToken(1).Kind == TokenKind.Ellipsis)
            {
                Advance();
                Advance();
                extensible = true;
            }
        }

        if (extensible && Is(TokenKind.Comma))
        {
            Advance();
            additions = ParseElementSet();
        }

        return new ElementSetSpecsSyntax(_source, SpanFrom(start), root, extensible, additions);
    }

    private ElementSetSyntax ParseElementSet()
    {
        var start = _pos;
        if (AcceptKeyword("ALL"))
        {
            ExpectKeyword("EXCEPT");
            var excluded = ParseElements();
            return new AllExceptSyntax(_source, SpanFrom(start), excluded);
        }

        var first = ParseIntersections();
        if (!Is(TokenKind.Bar) && !IsKeyword("UNION"))
        {
            return first;
        }

        var items = ImmutableArray.CreateBuilder<ElementSetSyntax>();
        items.Add(first);
        while (Accept(TokenKind.Bar) || AcceptKeyword("UNION"))
        {
            items.Add(ParseIntersections());
        }

        return new UnionSyntax(_source, SpanFrom(start), items.ToImmutable());
    }

    private ElementSetSyntax ParseIntersections()
    {
        var start = _pos;
        var first = ParseIntersectionElement();
        if (!Is(TokenKind.Caret) && !IsKeyword("INTERSECTION"))
        {
            return first;
        }

        var items = ImmutableArray.CreateBuilder<ElementSetSyntax>();
        items.Add(first);
        while (Accept(TokenKind.Caret) || AcceptKeyword("INTERSECTION"))
        {
            items.Add(ParseIntersectionElement());
        }

        return new IntersectionSyntax(_source, SpanFrom(start), items.ToImmutable());
    }

    private ElementSetSyntax ParseIntersectionElement()
    {
        var start = _pos;
        var e = ParseElements();
        if (AcceptKeyword("EXCEPT"))
        {
            var x = ParseElements();
            return new ExceptSyntax(_source, SpanFrom(start), e, x);
        }

        return e;
    }

    private ElementSetSyntax ParseElements()
    {
        Enter();
        try
        {
            return ParseElementsCore();
        }
        finally
        {
            Leave();
        }
    }

    private ElementSetSyntax ParseElementsCore()
    {
        var start = _pos;
        if (Is(TokenKind.LeftParen))
        {
            Advance();
            var inner = ParseElementSetSpecs();
            Expect(TokenKind.RightParen, "')'");
            return new NestedElementSetSyntax(_source, SpanFrom(start), inner);
        }

        if (Current.Kind == TokenKind.Keyword)
        {
            switch (Current.Text)
            {
                case "SIZE":
                    Advance();
                    return new SizeConstraintSyntax(_source, SpanFrom(start), ParseConstraint());
                case "FROM":
                    Advance();
                    return new PermittedAlphabetSyntax(_source, SpanFrom(start), ParseConstraint());
                case "PATTERN":
                    Advance();
                    return new PatternConstraintSyntax(_source, SpanFrom(start), ParseValue());
                case "SETTINGS":
                    Advance();
                    return new PropertySettingsSyntax(_source, SpanFrom(start), Expect(TokenKind.CString, "settings string").Text);
                case "INCLUDES":
                    Advance();
                    return new ContainedSubtypeSyntax(_source, SpanFrom(start), ParseType(), true);
                case "WITH":
                    Advance();
                    if (AcceptKeyword("COMPONENT"))
                    {
                        return new SingleTypeConstraintSyntax(_source, SpanFrom(start), ParseConstraint());
                    }

                    ExpectKeyword("COMPONENTS");
                    return ParseMultipleTypeConstraints(start);
                case "MIN":
                    {
                        Advance();
                        var lower = new RangeEndpointSyntax(_source, SpanFrom(start), null, Accept(TokenKind.LessThan));
                        return ParseRangeRest(start, lower);
                    }

                case "NULL" or "TRUE" or "FALSE" or "PLUS-INFINITY" or "MINUS-INFINITY" or "NOT-A-NUMBER" or "CONTAINING":
                    break;
                default:
                    if (StartsType())
                    {
                        return new ContainedSubtypeSyntax(_source, SpanFrom(start), ParseType(), false);
                    }

                    break;
            }
        }

        if (Is(TokenKind.TypeReference) && !(PeekToken(1).Kind == TokenKind.Dot && PeekToken(2).Kind == TokenKind.Identifier))
        {
            return new ContainedSubtypeSyntax(_source, SpanFrom(start), ParseType(), false);
        }

        if (Is(TokenKind.LeftBracket))
        {
            return new ContainedSubtypeSyntax(_source, SpanFrom(start), ParseType(), false);
        }

        var value = ParseValue();
        if (Is(TokenKind.Range) || (Is(TokenKind.LessThan) && PeekToken(1).Kind == TokenKind.Range))
        {
            var open = Accept(TokenKind.LessThan);
            var lower = new RangeEndpointSyntax(_source, SpanFrom(start), value, open);
            return ParseRangeRest(start, lower);
        }

        return new SingleValueSyntax(_source, SpanFrom(start), value);
    }

    private ValueRangeSyntax ParseRangeRest(int start, RangeEndpointSyntax lower)
    {
        Expect(TokenKind.Range, "'..'");
        var upperStart = _pos;
        var open = Accept(TokenKind.LessThan);
        ValueSyntax? upperValue = null;
        if (!AcceptKeyword("MAX"))
        {
            upperValue = ParseValue();
        }

        var upper = new RangeEndpointSyntax(_source, SpanFrom(upperStart), upperValue, open);
        return new ValueRangeSyntax(_source, SpanFrom(start), lower, upper);
    }

    private MultipleTypeConstraintsSyntax ParseMultipleTypeConstraints(int start)
    {
        Expect(TokenKind.LeftBrace, "'{'");
        var partial = false;
        if (Is(TokenKind.Ellipsis))
        {
            Advance();
            partial = true;
            Expect(TokenKind.Comma, "','");
        }

        var list = ImmutableArray.CreateBuilder<NamedConstraintSyntax>();
        do
        {
            var itemStart = _pos;
            var name = Name(Expect(TokenKind.Identifier, "component identifier"));
            ConstraintSyntax? valueConstraint = null;
            if (Is(TokenKind.LeftParen))
            {
                valueConstraint = ParseConstraint();
            }

            var presence = PresenceConstraint.None;
            if (AcceptKeyword("PRESENT"))
            {
                presence = PresenceConstraint.Present;
            }
            else if (AcceptKeyword("ABSENT"))
            {
                presence = PresenceConstraint.Absent;
            }
            else if (AcceptKeyword("OPTIONAL"))
            {
                presence = PresenceConstraint.Optional;
            }

            list.Add(new NamedConstraintSyntax(_source, SpanFrom(itemStart), name, valueConstraint, presence));
        }
        while (Accept(TokenKind.Comma));

        Expect(TokenKind.RightBrace, "'}'");
        return new MultipleTypeConstraintsSyntax(_source, SpanFrom(start), partial, list.ToImmutable());
    }
}
