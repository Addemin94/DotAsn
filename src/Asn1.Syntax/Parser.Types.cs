using System.Collections.Immutable;

namespace Asn1.Syntax;

public sealed partial class Parser
{
    /// <summary>Parses a type, including serially applied constraints.</summary>
    public TypeSyntax ParseType()
    {
        Enter();
        try
        {
            var start = _pos;
            var type = ParseTypeCore();
            if (Is(TokenKind.LeftParen))
            {
                var constraints = ImmutableArray.CreateBuilder<ConstraintSyntax>();
                while (Is(TokenKind.LeftParen))
                {
                    constraints.Add(ParseConstraint());
                }

                type = new ConstrainedTypeSyntax(_source, SpanFrom(start), type, constraints.ToImmutable());
            }

            return type;
        }
        finally
        {
            Leave();
        }
    }

    /// <summary>True when the current token can start a type.</summary>
    private bool StartsType() => Current.Kind switch
    {
        TokenKind.TypeReference => true,
        TokenKind.LeftBracket => true,
        TokenKind.Keyword => s_typeKeywords.Contains(Current.Text),
        TokenKind.Identifier => PeekToken(1).Kind == TokenKind.LessThan || (PeekToken(1).Kind == TokenKind.Dot && PeekToken(2).Kind is TokenKind.TypeFieldReference),
        _ => false,
    };

    private TypeSyntax ParseTypeCore()
    {
        var start = _pos;
        var t = Current;
        switch (t.Kind)
        {
            case TokenKind.LeftBracket:
                return ParseTaggedType();
            case TokenKind.TypeReference:
                // "Name ::=" at the start of a line begins the next assignment; reporting the missing type here keeps
                // error recovery from swallowing that assignment.
                if (t.StartsLine && (PeekToken(1).Kind == TokenKind.Assignment || (PeekToken(1).Kind == TokenKind.LeftBrace && LooksLikeAssignmentStart())))
                {
                    throw Error("type");
                }

                return ParseTypeReferenceOrField();
            case TokenKind.Identifier:
                {
                    var name = Name(Advance());
                    if (Accept(TokenKind.LessThan))
                    {
                        var choice = ParseType();
                        return new SelectionTypeSyntax(_source, SpanFrom(start), name, choice);
                    }

                    if (Is(TokenKind.Dot) && PeekToken(1).Kind is TokenKind.TypeFieldReference or TokenKind.ValueFieldReference)
                    {
                        Advance();
                        var path = ParseFieldPath();
                        return new FieldTypeSyntax(_source, SpanFrom(start), null, name, path);
                    }

                    _pos = start;
                    throw Error("type");
                }

            case TokenKind.Keyword:
                return ParseBuiltinType();
            default:
                throw Error("type");
        }
    }

    private TypeSyntax ParseTaggedType()
    {
        var start = _pos;
        Expect(TokenKind.LeftBracket, "'['");

        // Encoding instruction prefix: [XER: ...] (X.680 31.3). Skip the instruction.
        if (Is(TokenKind.TypeReference) && PeekToken(1).Kind == TokenKind.Colon)
        {
            var encRef = Current.Text;
            _pos = start;
            SkipBalanced();
            _diagnostics.Report(Asn1.Diagnostics.DiagnosticCatalog.EncodingControlIgnored, new Asn1.Diagnostics.SourceLocation(_source, SpanFrom(start)), encRef);
            return ParseType();
        }

        var tagClass = TagClassSyntax.ContextSpecific;
        if (AcceptKeyword("UNIVERSAL"))
        {
            tagClass = TagClassSyntax.Universal;
        }
        else if (AcceptKeyword("APPLICATION"))
        {
            tagClass = TagClassSyntax.Application;
        }
        else if (AcceptKeyword("PRIVATE"))
        {
            tagClass = TagClassSyntax.Private;
        }

        ValueSyntax number;
        if (Is(TokenKind.Number))
        {
            var n = Advance();
            number = new NumberValueSyntax(_source, n.Span, n.NumberValue);
        }
        else if (Is(TokenKind.Identifier) || (Is(TokenKind.TypeReference) && PeekToken(1).Kind == TokenKind.Dot))
        {
            number = ParseValue();
        }
        else
        {
            throw Error("tag number");
        }

        Expect(TokenKind.RightBracket, "']'");
        var mode = TaggingMode.Default;
        if (AcceptKeyword("IMPLICIT"))
        {
            mode = TaggingMode.Implicit;
        }
        else if (AcceptKeyword("EXPLICIT"))
        {
            mode = TaggingMode.Explicit;
        }

        var inner = ParseType();
        return new TaggedTypeSyntax(_source, SpanFrom(start), tagClass, number, mode, inner);
    }

    private ImmutableArray<NameSyntax> ParseFieldPath()
    {
        var path = ImmutableArray.CreateBuilder<NameSyntax>();
        do
        {
            if (Current.Kind is not (TokenKind.TypeFieldReference or TokenKind.ValueFieldReference))
            {
                throw Error("field reference");
            }

            path.Add(Name(Advance()));
        }
        while (Is(TokenKind.Dot) && PeekToken(1).Kind is TokenKind.TypeFieldReference or TokenKind.ValueFieldReference && Accept(TokenKind.Dot));

        return path.ToImmutable();
    }

    private TypeSyntax ParseTypeReferenceOrField()
    {
        var start = _pos;
        var reference = ParseTypeReference();
        if (Is(TokenKind.Dot) && PeekToken(1).Kind is TokenKind.TypeFieldReference or TokenKind.ValueFieldReference)
        {
            Advance();
            var path = ParseFieldPath();
            return new FieldTypeSyntax(_source, SpanFrom(start), reference, null, path);
        }

        return reference;
    }

    private TypeReferenceSyntax ParseTypeReference()
    {
        var start = _pos;
        NameSyntax? module = null;
        Token nameTok;
        if (Is(TokenKind.TypeReference) && PeekToken(1).Kind == TokenKind.Dot && PeekToken(2).Kind == TokenKind.TypeReference)
        {
            module = Name(Advance());
            Advance();
            nameTok = Advance();
        }
        else if (Is(TokenKind.TypeReference) || IsKeyword("TYPE-IDENTIFIER") || IsKeyword("ABSTRACT-SYNTAX"))
        {
            nameTok = Advance();
        }
        else
        {
            throw Error("type reference");
        }

        ImmutableArray<ActualParameterSyntax>? actual = null;
        if (Is(TokenKind.LeftBrace))
        {
            actual = ParseActualParameters();
        }

        return new TypeReferenceSyntax(_source, SpanFrom(start), module, Name(nameTok), actual);
    }

    private ImmutableArray<ActualParameterSyntax> ParseActualParameters()
    {
        var open = _pos;
        var close = FindMatching(open);
        if (close < 0)
        {
            throw Error("'}'");
        }

        var list = ImmutableArray.CreateBuilder<ActualParameterSyntax>();
        var segStart = open + 1;
        var depth = 0;
        for (var i = open + 1; i <= close; i++)
        {
            var k = _tokens[i].Kind;
            if (k is TokenKind.LeftBrace or TokenKind.LeftParen or TokenKind.LeftBracket or TokenKind.LeftVersionBrackets)
            {
                depth++;
            }
            else if (k is TokenKind.RightBrace or TokenKind.RightParen or TokenKind.RightBracket or TokenKind.RightVersionBrackets)
            {
                depth--;
            }

            if ((k == TokenKind.Comma && depth == 0) || i == close)
            {
                if (i > segStart)
                {
                    list.Add(CreateActualParameter(segStart, i));
                }

                segStart = i + 1;
            }
        }

        _pos = close + 1;
        return list.ToImmutable();
    }

    private ActualParameterSyntax CreateActualParameter(int start, int end)
    {
        var span = Asn1.Diagnostics.TextSpan.FromBounds(_tokens[start].Span.Start, _tokens[end - 1].Span.End);
        var scratch = new Asn1.Diagnostics.DiagnosticBag();
        TypeSyntax? asType = null;
        ValueSyntax? asValue = null;
        var p = new Parser(_lex, start, end, scratch, _options) { _depth = _depth };
        try
        {
            var t = p.ParseType();
            if (p.IsAtEnd && !scratch.HasErrors)
            {
                asType = t;
            }
        }
        catch (ParseException)
        {
        }

        scratch = new Asn1.Diagnostics.DiagnosticBag();
        p = new Parser(_lex, start, end, scratch, _options) { _depth = _depth };
        try
        {
            var v = p.ParseValue();
            if (p.IsAtEnd && !scratch.HasErrors)
            {
                asValue = v;
            }
        }
        catch (ParseException)
        {
        }

        return new ActualParameterSyntax(_source, span, start, end, asType, asValue);
    }

    private TypeSyntax ParseBuiltinType()
    {
        var start = _pos;
        var kw = Current.Text;
        BuiltinTypeSyntax Simple(BuiltinTypeKind k) => new(_source, SpanFrom(start), k);
        switch (kw)
        {
            case "BOOLEAN":
                Advance();
                return Simple(BuiltinTypeKind.Boolean);
            case "NULL":
                Advance();
                return Simple(BuiltinTypeKind.Null);
            case "REAL":
                Advance();
                return Simple(BuiltinTypeKind.Real);
            case "INTEGER":
                Advance();
                return new IntegerTypeSyntax(_source, SpanFrom(start), Is(TokenKind.LeftBrace) ? ParseNamedNumberList() : []);
            case "BIT":
                Advance();
                ExpectKeyword("STRING");
                return new BitStringTypeSyntax(_source, SpanFrom(start), Is(TokenKind.LeftBrace) ? ParseNamedNumberList() : []);
            case "OCTET":
                Advance();
                ExpectKeyword("STRING");
                return Simple(BuiltinTypeKind.OctetString);
            case "OBJECT":
                Advance();
                ExpectKeyword("IDENTIFIER");
                return Simple(BuiltinTypeKind.ObjectIdentifier);
            case "RELATIVE-OID":
                Advance();
                return Simple(BuiltinTypeKind.RelativeOid);
            case "OID-IRI":
                Advance();
                return Simple(BuiltinTypeKind.OidIri);
            case "RELATIVE-OID-IRI":
                Advance();
                return Simple(BuiltinTypeKind.RelativeOidIri);
            case "EXTERNAL":
                Advance();
                return Simple(BuiltinTypeKind.External);
            case "EMBEDDED":
                Advance();
                ExpectKeyword("PDV");
                return Simple(BuiltinTypeKind.EmbeddedPdv);
            case "CHARACTER":
                Advance();
                ExpectKeyword("STRING");
                return Simple(BuiltinTypeKind.CharacterString);
            case "UTF8String":
                Advance();
                return Simple(BuiltinTypeKind.Utf8String);
            case "NumericString":
                Advance();
                return Simple(BuiltinTypeKind.NumericString);
            case "PrintableString":
                Advance();
                return Simple(BuiltinTypeKind.PrintableString);
            case "TeletexString" or "T61String":
                Advance();
                return Simple(BuiltinTypeKind.TeletexString);
            case "VideotexString":
                Advance();
                return Simple(BuiltinTypeKind.VideotexString);
            case "IA5String":
                Advance();
                return Simple(BuiltinTypeKind.IA5String);
            case "GraphicString":
                Advance();
                return Simple(BuiltinTypeKind.GraphicString);
            case "VisibleString" or "ISO646String":
                Advance();
                return Simple(BuiltinTypeKind.VisibleString);
            case "GeneralString":
                Advance();
                return Simple(BuiltinTypeKind.GeneralString);
            case "UniversalString":
                Advance();
                return Simple(BuiltinTypeKind.UniversalString);
            case "BMPString":
                Advance();
                return Simple(BuiltinTypeKind.BmpString);
            case "UTCTime":
                Advance();
                return Simple(BuiltinTypeKind.UtcTime);
            case "GeneralizedTime":
                Advance();
                return Simple(BuiltinTypeKind.GeneralizedTime);
            case "ObjectDescriptor":
                Advance();
                return Simple(BuiltinTypeKind.ObjectDescriptor);
            case "DATE":
                Advance();
                return Simple(BuiltinTypeKind.Date);
            case "TIME-OF-DAY":
                Advance();
                return Simple(BuiltinTypeKind.TimeOfDay);
            case "DATE-TIME":
                Advance();
                return Simple(BuiltinTypeKind.DateTime);
            case "DURATION":
                Advance();
                return Simple(BuiltinTypeKind.Duration);
            case "TIME":
                Advance();
                return Simple(BuiltinTypeKind.Time);
            case "ENUMERATED":
                return ParseEnumerated();
            case "SEQUENCE" or "SET":
                return ParseSequenceOrSet();
            case "CHOICE":
                Advance();
                return new StructuredTypeSyntax(_source, SpanFrom(start), StructuredKind.Choice, ParseComponentList(StructuredKind.Choice));
            case "INSTANCE":
                Advance();
                ExpectKeyword("OF");
                return new InstanceOfTypeSyntax(_source, SpanFrom(start), ParseTypeReference());
            case "TYPE-IDENTIFIER" or "ABSTRACT-SYNTAX":
                return ParseTypeReferenceOrField();
            case "ANY":
                {
                    Advance();
                    NameSyntax? definedBy = null;
                    if (AcceptKeyword("DEFINED"))
                    {
                        ExpectKeyword("BY");
                        definedBy = Name(Expect(TokenKind.Identifier, "identifier"));
                    }

                    var any = new AnyTypeSyntax(_source, SpanFrom(start), definedBy);
                    _diagnostics.Report(Asn1.Diagnostics.DiagnosticCatalog.DeprecatedNotation, any.Location, "ANY", "it is treated as an open type");
                    return any;
                }

            default:
                throw Error("type");
        }
    }

    private ImmutableArray<NamedNumberSyntax> ParseNamedNumberList()
    {
        Expect(TokenKind.LeftBrace, "'{'");
        var list = ImmutableArray.CreateBuilder<NamedNumberSyntax>();
        do
        {
            var start = _pos;
            var name = Name(Expect(TokenKind.Identifier, "identifier"));
            Expect(TokenKind.LeftParen, "'('");
            var value = ParseValue();
            Expect(TokenKind.RightParen, "')'");
            list.Add(new NamedNumberSyntax(_source, SpanFrom(start), name, value, DocumentationFor(start, _pos - 1)));
        }
        while (Accept(TokenKind.Comma));

        Expect(TokenKind.RightBrace, "'}'");
        return list.ToImmutable();
    }

    private EnumeratedTypeSyntax ParseEnumerated()
    {
        var start = _pos;
        ExpectKeyword("ENUMERATED");
        Expect(TokenKind.LeftBrace, "'{'");
        var root = ImmutableArray.CreateBuilder<NamedNumberSyntax>();
        var additions = ImmutableArray.CreateBuilder<NamedNumberSyntax>();
        var extensible = false;
        ExceptionSpecSyntax? exception = null;
        if (!Is(TokenKind.RightBrace))
        {
            do
            {
                if (Is(TokenKind.Ellipsis))
                {
                    if (extensible)
                    {
                        throw Error("enumeration item (only one extension marker is allowed)");
                    }

                    Advance();
                    extensible = true;
                    if (Is(TokenKind.Exclamation))
                    {
                        exception = ParseExceptionSpec();
                    }

                    continue;
                }

                var itemStart = _pos;
                var name = Name(Expect(TokenKind.Identifier, "enumeration identifier"));
                ValueSyntax? value = null;
                if (Accept(TokenKind.LeftParen))
                {
                    value = ParseValue();
                    Expect(TokenKind.RightParen, "')'");
                }

                var item = new NamedNumberSyntax(_source, SpanFrom(itemStart), name, value, DocumentationFor(itemStart, _pos - 1));
                (extensible ? additions : root).Add(item);
            }
            while (Accept(TokenKind.Comma));
        }

        Expect(TokenKind.RightBrace, "'}'");
        return new EnumeratedTypeSyntax(_source, SpanFrom(start), root.ToImmutable(), extensible, exception, additions.ToImmutable());
    }

    private TypeSyntax ParseSequenceOrSet()
    {
        var start = _pos;
        var isSet = Advance().Text == "SET";
        var kind = isSet ? StructuredKind.Set : StructuredKind.Sequence;
        if (Is(TokenKind.LeftBrace))
        {
            var items = ParseComponentList(kind);
            return new StructuredTypeSyntax(_source, SpanFrom(start), kind, items);
        }

        ConstraintSyntax? constraint = null;
        if (IsKeyword("SIZE"))
        {
            var sizeStart = _pos;
            Advance();
            var inner = ParseConstraint();
            var size = new SizeConstraintSyntax(_source, SpanFrom(sizeStart), inner);
            constraint = new ConstraintSyntax(_source, SpanFrom(sizeStart), new ElementSetSpecsSyntax(_source, SpanFrom(sizeStart), size, false, null), null);
        }
        else if (Is(TokenKind.LeftParen))
        {
            constraint = ParseConstraint();
        }

        ExpectKeyword("OF");
        NameSyntax? elementName = null;
        if (Is(TokenKind.Identifier) && PeekToken(1).Kind != TokenKind.LessThan && PeekToken(1).Kind != TokenKind.Dot)
        {
            elementName = Name(Advance());
        }

        var element = ParseType();
        return new CollectionTypeSyntax(_source, SpanFrom(start), isSet, constraint, elementName, element);
    }

    private ImmutableArray<ComponentListItemSyntax> ParseComponentList(StructuredKind kind)
    {
        Expect(TokenKind.LeftBrace, "'{'");
        var items = ImmutableArray.CreateBuilder<ComponentListItemSyntax>();
        if (Accept(TokenKind.RightBrace))
        {
            return items.ToImmutable();
        }

        while (true)
        {
            var before = _pos;
            try
            {
                items.Add(ParseComponentItem(kind, inGroup: false));
            }
            catch (ParseException)
            {
                RecoverInList(before);
            }

            if (Accept(TokenKind.Comma))
            {
                continue;
            }

            if (Accept(TokenKind.RightBrace))
            {
                break;
            }

            // Missing comma or garbage: report once and resynchronise inside the list.
            if (Current.Kind == TokenKind.EndOfFile)
            {
                throw Error("'}'");
            }

            var errorPos = _pos;
            _ = Error("',' or '}'");
            RecoverInList(errorPos);
            if (Accept(TokenKind.RightBrace))
            {
                break;
            }

            Accept(TokenKind.Comma);
        }

        return items.ToImmutable();
    }

    /// <summary>Skips to the next ',' or the closing '}' of the current list, respecting nesting.</summary>
    private void RecoverInList(int errorStart)
    {
        if (_pos == errorStart && !Is(TokenKind.Comma) && !Is(TokenKind.RightBrace))
        {
            Advance();
        }

        var depth = 0;
        while (!Is(TokenKind.EndOfFile))
        {
            var k = Current.Kind;
            if (depth == 0 && (k == TokenKind.Comma || k == TokenKind.RightBrace || k == TokenKind.RightVersionBrackets))
            {
                return;
            }

            if (k is TokenKind.LeftBrace or TokenKind.LeftParen or TokenKind.LeftBracket)
            {
                depth++;
            }
            else if (k is TokenKind.RightBrace or TokenKind.RightParen or TokenKind.RightBracket)
            {
                depth--;
            }

            // Stop at an apparent assignment start so a missing '}' does not swallow the module.
            if (Current.StartsLine && Current.IsName && LooksLikeAssignmentStart())
            {
                throw new ParseException("unterminated list");
            }

            Advance();
        }
    }

    private ComponentListItemSyntax ParseComponentItem(StructuredKind kind, bool inGroup)
    {
        var start = _pos;
        if (Is(TokenKind.Ellipsis))
        {
            if (inGroup)
            {
                throw Error("component (extension markers are not allowed inside [[ ]])");
            }

            Advance();
            ExceptionSpecSyntax? exception = null;
            if (Is(TokenKind.Exclamation))
            {
                exception = ParseExceptionSpec();
            }

            return new ExtensionMarkerSyntax(_source, SpanFrom(start), exception);
        }

        if (Is(TokenKind.LeftVersionBrackets))
        {
            if (inGroup)
            {
                throw Error("component (extension groups cannot be nested)");
            }

            Advance();
            ValueSyntax? version = null;
            if (Is(TokenKind.Number) && PeekToken(1).Kind == TokenKind.Colon)
            {
                var n = Advance();
                version = new NumberValueSyntax(_source, n.Span, n.NumberValue);
                Advance();
            }

            var groupItems = ImmutableArray.CreateBuilder<ComponentListItemSyntax>();
            do
            {
                groupItems.Add(ParseComponentItem(kind, inGroup: true));
            }
            while (Accept(TokenKind.Comma));

            Expect(TokenKind.RightVersionBrackets, "']]'");
            return new ExtensionGroupSyntax(_source, SpanFrom(start), version, groupItems.ToImmutable());
        }

        if (IsKeyword("COMPONENTS") && kind != StructuredKind.Choice)
        {
            Advance();
            ExpectKeyword("OF");
            return new ComponentsOfSyntax(_source, SpanFrom(start), ParseType());
        }

        if (!Is(TokenKind.Identifier))
        {
            if (StartsType())
            {
                _ = Error("component identifier (X.680 requires every component to be named)");
            }

            throw Error("component identifier");
        }

        var name = Name(Advance());
        var type = ParseType();
        var presence = ComponentPresence.Required;
        ValueSyntax? defaultValue = null;
        if (kind != StructuredKind.Choice)
        {
            if (AcceptKeyword("OPTIONAL"))
            {
                presence = ComponentPresence.Optional;
            }
            else if (AcceptKeyword("DEFAULT"))
            {
                presence = ComponentPresence.Default;
                defaultValue = ParseValue();
            }
        }

        return new NamedComponentSyntax(_source, SpanFrom(start), name, type, presence, defaultValue, DocumentationFor(start, _pos - 1));
    }

    private ExceptionSpecSyntax ParseExceptionSpec()
    {
        var start = _pos;
        Expect(TokenKind.Exclamation, "'!'");

        // ExceptionIdentification ::= SignedNumber | DefinedValue | Type ":" Value
        if (Is(TokenKind.Number) || Is(TokenKind.Hyphen) || (Is(TokenKind.Identifier) && PeekToken(1).Kind != TokenKind.LessThan) || (Is(TokenKind.TypeReference) && PeekToken(1).Kind == TokenKind.Dot && PeekToken(2).Kind == TokenKind.Identifier))
        {
            var v = ParseValue();
            return new ExceptionSpecSyntax(_source, SpanFrom(start), null, v);
        }

        var type = ParseType();
        Expect(TokenKind.Colon, "':'");
        var value = ParseValue();
        return new ExceptionSpecSyntax(_source, SpanFrom(start), type, value);
    }
}
