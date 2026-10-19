using System.Collections.Immutable;
using System.Globalization;
using Asn1.Diagnostics;

namespace Asn1.Syntax;

public sealed partial class Parser
{
    /// <summary>Parses a value. Braced values are captured as token ranges.</summary>
    public ValueSyntax ParseValue()
    {
        Enter();
        try
        {
            return ParseValueCore();
        }
        finally
        {
            Leave();
        }
    }

    private ValueSyntax ParseValueCore()
    {
        var start = _pos;
        var t = Current;
        switch (t.Kind)
        {
            case TokenKind.LeftBrace:
                {
                    var close = FindMatching(_pos);
                    if (close < 0)
                    {
                        throw Error("'}'");
                    }

                    _pos = close + 1;
                    return new BracedValueSyntax(_source, SpanFrom(start), _lex, start, close);
                }

            case TokenKind.Number:
                Advance();
                return new NumberValueSyntax(_source, t.Span, t.NumberValue);
            case TokenKind.RealNumber:
                Advance();
                return MakeReal(t.Text, SpanFrom(start));
            case TokenKind.Hyphen:
                {
                    Advance();
                    var n = Current;
                    if (n.Kind == TokenKind.Number)
                    {
                        Advance();
                        if (n.NumberValue.IsZero)
                        {
                            // X.680 12.8 / 20.?: "-0" is not a valid SignedNumber.
                            _diagnostics.Report(DiagnosticCatalog.InvalidNumber, new SourceLocation(_source, SpanFrom(start)), "-0");
                        }

                        return new NumberValueSyntax(_source, SpanFrom(start), -n.NumberValue);
                    }

                    if (n.Kind == TokenKind.RealNumber)
                    {
                        Advance();
                        return MakeReal("-" + n.Text, SpanFrom(start));
                    }

                    throw Error("number");
                }

            case TokenKind.CString:
                Advance();
                return new StringValueSyntax(_source, t.Span, t.Text);
            case TokenKind.BString:
                Advance();
                return new BinaryStringValueSyntax(_source, t.Span, t.Text, false);
            case TokenKind.HString:
                Advance();
                return new BinaryStringValueSyntax(_source, t.Span, t.Text, true);
            case TokenKind.Keyword:
                switch (t.Text)
                {
                    case "TRUE":
                        Advance();
                        return new BooleanValueSyntax(_source, t.Span, true);
                    case "FALSE":
                        Advance();
                        return new BooleanValueSyntax(_source, t.Span, false);
                    case "NULL":
                        Advance();
                        return new NullValueSyntax(_source, t.Span);
                    case "PLUS-INFINITY":
                        Advance();
                        return new RealValueSyntax(_source, t.Span, t.Text, double.PositiveInfinity);
                    case "MINUS-INFINITY":
                        Advance();
                        return new RealValueSyntax(_source, t.Span, t.Text, double.NegativeInfinity);
                    case "NOT-A-NUMBER":
                        Advance();
                        return new RealValueSyntax(_source, t.Span, t.Text, double.NaN);
                    case "CONTAINING":
                        Advance();
                        return new ContainingValueSyntax(_source, SpanFrom(start), ParseValue());
                }

                if (LooksLikeOpenTypeValue())
                {
                    var type = ParseType();
                    Expect(TokenKind.Colon, "':'");
                    var inner = ParseValue();
                    return new OpenTypeValueSyntax(_source, SpanFrom(start), type, inner);
                }

                throw Error("value");
            case TokenKind.Identifier:
                {
                    var name = Name(Advance());
                    if (Is(TokenKind.Colon))
                    {
                        Advance();
                        var inner = ParseValue();
                        return new ChoiceValueSyntax(_source, SpanFrom(start), name, inner);
                    }

                    if (Is(TokenKind.Dot) && PeekToken(1).Kind is TokenKind.TypeFieldReference or TokenKind.ValueFieldReference)
                    {
                        Advance();
                        var path = ParseFieldPath();
                        return new FieldValueSyntax(_source, SpanFrom(start), null, name, path);
                    }

                    ImmutableArray<ActualParameterSyntax>? actual = null;
                    if (Is(TokenKind.LeftBrace) && Current.Span.Start == Previous.Span.End)
                    {
                        actual = ParseActualParameters();
                    }

                    return new ValueReferenceSyntax(_source, SpanFrom(start), null, name, actual);
                }

            case TokenKind.TypeReference when PeekToken(1).Kind == TokenKind.Dot && PeekToken(2).Kind == TokenKind.Identifier:
                {
                    var module = Name(Advance());
                    Advance();
                    var name = Name(Advance());
                    if (Is(TokenKind.Dot) && PeekToken(1).Kind is TokenKind.TypeFieldReference or TokenKind.ValueFieldReference)
                    {
                        Advance();
                        var path = ParseFieldPath();
                        return new FieldValueSyntax(_source, SpanFrom(start), module, name, path);
                    }

                    return new ValueReferenceSyntax(_source, SpanFrom(start), module, name, null);
                }

            default:
                if (LooksLikeOpenTypeValue())
                {
                    var type = ParseType();
                    Expect(TokenKind.Colon, "':'");
                    var inner = ParseValue();
                    return new OpenTypeValueSyntax(_source, SpanFrom(start), type, inner);
                }

                throw Error("value");
        }
    }

    /// <summary>True if a type followed by ':' starts here (open type value notation).</summary>
    private bool LooksLikeOpenTypeValue()
    {
        if (!StartsType() || Current.Kind == TokenKind.Identifier)
        {
            return false;
        }

        var depth = 0;
        for (var i = 0; _pos + i < _end && i < 256; i++)
        {
            var k = PeekToken(i).Kind;
            switch (k)
            {
                case TokenKind.LeftBrace or TokenKind.LeftParen or TokenKind.LeftBracket:
                    depth++;
                    break;
                case TokenKind.RightBrace or TokenKind.RightParen or TokenKind.RightBracket:
                    if (--depth < 0)
                    {
                        return false;
                    }

                    break;
                case TokenKind.Colon when depth == 0:
                    return true;
                case TokenKind.Comma or TokenKind.Assignment or TokenKind.EndOfFile when depth == 0:
                    return false;
            }
        }

        return false;
    }

    private RealValueSyntax MakeReal(string text, TextSpan span)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
        {
            _diagnostics.Report(DiagnosticCatalog.InvalidReal, new SourceLocation(_source, span), text);
        }

        return new RealValueSyntax(_source, span, text, d);
    }
}
