using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using Asn1.Diagnostics;
using Asn1.Syntax;

namespace Asn1.Semantics.Binding;

internal sealed partial class Binder
{
    private readonly HashSet<ValueAssignmentSymbol> _valuesInProgress = new(ReferenceEqualityComparer.Instance);

    private AsnValue EnsureValue(ValueAssignmentSymbol va)
    {
        if (va.Value is not null)
        {
            return va.Value;
        }

        var syntax = (ValueAssignmentSyntax)va.Syntax;
        if (!_valuesInProgress.Add(va))
        {
            Report(DiagnosticCatalog.CyclicValue, va.Location, va.Name);
            return new ErrorValue(va.Location);
        }

        try
        {
            var scope = ModuleScope(va.Module);
            var type = va.Type ??= ResolveHeader(syntax.Type, scope);
            var value = ResolveValue(syntax.Value, type, scope);
            va.Value ??= value;
            return va.Value;
        }
        finally
        {
            _valuesInProgress.Remove(va);
        }
    }

    private AsnValue Mismatch(ValueSyntax v, AsnType type, string detail)
    {
        Report(DiagnosticCatalog.ValueTypeMismatch, v.Location, type.DisplayName, detail);
        return new ErrorValue(v.Location);
    }

    /// <summary>Resolves value notation against a governing type.</summary>
    private AsnValue ResolveValue(ValueSyntax v, AsnType type, Scope scope)
    {
        if (type.Kind == TypeKind.Error)
        {
            return new ErrorValue(v.Location);
        }

        var def = type.Definition;
        switch (v)
        {
            case ErrorValueSyntax:
                return new ErrorValue(v.Location);

            case ValueReferenceSyntax r:
                return ResolveValueReference(r, type, scope);

            case NumberValueSyntax n:
                return type.Kind switch
                {
                    TypeKind.Integer => new IntegerValue(n.Value, v.Location),
                    TypeKind.Real => new RealValue((double)n.Value, v.Location),
                    TypeKind.Enumerated => Mismatch(v, type, "ENUMERATED values must be written as identifiers"),
                    _ => Mismatch(v, type, "a number is not valid here"),
                };

            case RealValueSyntax rv:
                return type.Kind == TypeKind.Real ? new RealValue(rv.Value, v.Location) : Mismatch(v, type, "a real number is not valid here");

            case BooleanValueSyntax b:
                return type.Kind == TypeKind.Boolean ? new BooleanValue(b.Value, v.Location) : Mismatch(v, type, "TRUE/FALSE is not valid here");

            case NullValueSyntax:
                return type.Kind == TypeKind.Null ? new NullValue(v.Location) : Mismatch(v, type, "NULL is not valid here");

            case StringValueSyntax s:
                return ResolveStringValue(s, type);

            case BinaryStringValueSyntax bs:
                return ResolveBinaryString(bs, type);

            case ChoiceValueSyntax c:
                {
                    if (type.Kind != TypeKind.Choice)
                    {
                        return Mismatch(v, type, "a CHOICE value is not valid here");
                    }

                    EnsureStructureNow(def);
                    var alt = def.Components.FirstOrDefault(a => a.Name == c.Alternative.Text);
                    if (alt is null)
                    {
                        Report(DiagnosticCatalog.UnknownComponentInValue, c.Alternative.Location, c.Alternative.Text, type.DisplayName);
                        return new ErrorValue(v.Location);
                    }

                    return new ChoiceValue(alt.Name, ResolveValue(c.Value, alt.Type, scope), v.Location);
                }

            case BracedValueSyntax braced:
                return ResolveBraced(braced, type, scope);

            case ContainingValueSyntax:
                Report(DiagnosticCatalog.UnsupportedFeature, v.Location, "CONTAINING value notation", string.Empty);
                return new ErrorValue(v.Location);

            case FieldValueSyntax f:
                return ResolveFieldValue(f, type, scope);

            case OpenTypeValueSyntax ot:
                {
                    if (type.Kind != TypeKind.OpenType)
                    {
                        return Mismatch(v, type, "open type value notation (Type : value) requires an open type");
                    }

                    var actual = ResolveHeader(ot.Type, scope);
                    DrainStructureQueue();
                    return new OpenTypeValue(actual, ResolveValue(ot.Value, actual, scope), v.Location);
                }

            default:
                return Mismatch(v, type, "unsupported value notation");
        }
    }

    private AsnValue ResolveValueReference(ValueReferenceSyntax r, AsnType type, Scope scope)
    {
        var def = type.Definition;
        if (r.Module is null && !scope.Parameters.ContainsKey(r.Name.Text))
        {
            if (type.Kind == TypeKind.Integer)
            {
                EnsureStructureNow(def);
                var nn = def.NamedNumbers.FirstOrDefault(n => n.Name == r.Name.Text);
                if (nn is not null)
                {
                    return new IntegerValue(nn.Value, r.Location, nn.Name);
                }
            }
            else if (type.Kind == TypeKind.Enumerated)
            {
                EnsureStructureNow(def);
                var item = def.EnumItems.FirstOrDefault(i => i.Name == r.Name.Text);
                if (item is not null)
                {
                    return new EnumeratedValue(item.Name, item.Value, r.Location);
                }
            }
            else if (type.Kind == TypeKind.Real && r.Name.Text is "plus-infinity" or "minus-infinity")
            {
                return new RealValue(r.Name.Text[0] == 'p' ? double.PositiveInfinity : double.NegativeInfinity, r.Location);
            }
        }

        var lookup = LookupName(scope, r.Module, r.Name);
        if (lookup.Parameter is { } binding)
        {
            return ResolveParameterAsValue(binding, type);
        }

        switch (lookup.Symbol)
        {
            case ValueAssignmentSymbol va:
                {
                    var value = EnsureValue(va);
                    if (value is ErrorValue)
                    {
                        return value;
                    }

                    if (!IsCompatible(va.Type!, type))
                    {
                        return Mismatch(r, type, $"'{va.Name}' is a value of type {va.Type!.DisplayName}");
                    }

                    return value;
                }

            case null:
                return new ErrorValue(r.Location);
            default:
                Report(DiagnosticCatalog.WrongReferenceKind, r.Location, r.ToString(), lookup.Symbol.KindName, "value");
                return new ErrorValue(r.Location);
        }
    }

    private static bool IsCompatible(AsnType valueType, AsnType expected)
    {
        if (valueType.Kind == TypeKind.Error || expected.Kind == TypeKind.Error)
        {
            return true;
        }

        if (valueType.Kind == expected.Kind)
        {
            return true;
        }

        return (valueType.IsStringLike && expected.IsStringLike) || (valueType.Kind == TypeKind.Integer && expected.Kind == TypeKind.Real);
    }

    private AsnValue ResolveParameterAsValue(ParamBinding binding, AsnType type)
    {
        if (binding.CachedValue is not null)
        {
            return binding.CachedValue;
        }

        if (binding.Actual.AsValue is not { } value)
        {
            Report(DiagnosticCatalog.WrongReferenceKind, binding.Actual.Location, binding.Formal.Name.Text, "type", "value");
            return new ErrorValue(binding.Actual.Location);
        }

        // The governor type (if any) is resolved in the definition scope; the value in the caller's scope.
        var governor = type;
        if (binding.Formal.Governor is { } g && binding.DefinitionScope is { } defScope)
        {
            governor = ResolveHeader(g, defScope);
        }

        return binding.CachedValue = ResolveValue(value, governor, binding.CallerScope);
    }

    private AsnValue ResolveStringValue(StringValueSyntax s, AsnType type)
    {
        switch (type.Kind)
        {
            case TypeKind.CharacterString:
                {
                    var error = CharacterRepertoire.Validate(type.StringKind, s.Value);
                    if (error is not null)
                    {
                        return Mismatch(s, type, error);
                    }

                    return new StringValue(s.Value, s.Location);
                }

            case TypeKind.UtcTime or TypeKind.GeneralizedTime or TypeKind.Date or TypeKind.TimeOfDay or TypeKind.DateTime or TypeKind.Duration or TypeKind.Time or TypeKind.OidIri or TypeKind.RelativeOidIri or TypeKind.UnrestrictedCharacterString:
                return new StringValue(s.Value, s.Location);
            default:
                return Mismatch(s, type, "a character string is not valid here");
        }
    }

    private AsnValue ResolveBinaryString(BinaryStringValueSyntax bs, AsnType type)
    {
        switch (type.Kind)
        {
            case TypeKind.BitString:
                return new BitStringValue(bs.IsHex ? Asn1BitString.FromHex(bs.Digits) : Asn1BitString.FromBinary(bs.Digits), bs.Location);
            case TypeKind.OctetString:
                {
                    // X.680 22.3: a bstring/hstring that is not a multiple of eight bits is padded with trailing zeros.
                    var bits = bs.IsHex ? Asn1BitString.FromHex(bs.Digits) : Asn1BitString.FromBinary(bs.Digits);
                    return new OctetStringValue(bits.WithLength((bits.Length + 7) / 8 * 8).ToArray(), bs.Location);
                }

            default:
                return Mismatch(bs, type, "a bit or hexadecimal string is not valid here");
        }
    }

    private AsnValue ResolveBraced(BracedValueSyntax braced, AsnType type, Scope scope)
    {
        var def = type.Definition;
        var parser = Parser.ForTokenRange(braced.Lex, braced.OpenBrace + 1, braced.CloseBrace, _diagnostics, _options.Parser);
        switch (type.Kind)
        {
            case TypeKind.ObjectIdentifier:
            case TypeKind.RelativeOid:
                return ResolveOid(parser, braced, type, scope);

            case TypeKind.BitString:
                {
                    EnsureStructureNow(def);
                    var names = new List<string>();
                    var positions = new List<long>();
                    while (!parser.IsAtEnd)
                    {
                        var t = parser.Next();
                        if (t.Kind != TokenKind.Identifier)
                        {
                            return Mismatch(braced, type, $"expected a named bit but found {t}");
                        }

                        var bit = def.NamedNumbers.FirstOrDefault(n => n.Name == t.Text);
                        if (bit is null)
                        {
                            Report(DiagnosticCatalog.UnknownComponentInValue, new SourceLocation(braced.Source, t.Span), t.Text, type.DisplayName);
                        }
                        else
                        {
                            names.Add(bit.Name);
                            positions.Add((long)bit.Value);
                        }

                        if (!parser.IsAtEnd && !parser.ExpectToken(TokenKind.Comma, "','"))
                        {
                            return new ErrorValue(braced.Location);
                        }
                    }

                    // X.680 22.?: with a size constraint, named-bit values are padded with zero bits to the minimum size.
                    var bits = Asn1BitString.FromSetBits(positions);
                    if (MinimumSize(type) is { } min && bits.Length < min)
                    {
                        bits = bits.WithLength(min);
                    }

                    return new BitStringValue(bits, braced.Location, [.. names]);
                }

            case TypeKind.Sequence:
            case TypeKind.Set:
            case TypeKind.External:
            case TypeKind.EmbeddedPdv:
                return ResolveSequenceValue(parser, braced, type, scope);

            case TypeKind.SequenceOf:
            case TypeKind.SetOf:
                {
                    EnsureStructureNow(def);
                    var element = def.ElementType!;
                    var items = ImmutableArray.CreateBuilder<AsnValue>();
                    while (!parser.IsAtEnd)
                    {
                        if (def.ElementName is not null && parser.CurrentToken.Kind == TokenKind.Identifier && parser.CurrentToken.Text == def.ElementName && parser.Peek(1).Kind != TokenKind.Comma && parser.Peek(1).Kind != TokenKind.EndOfFile)
                        {
                            parser.Next();
                        }

                        var itemSyntax = parser.TryRun(p => p.ParseValue());
                        if (itemSyntax is null)
                        {
                            return new ErrorValue(braced.Location);
                        }

                        items.Add(ResolveValue(itemSyntax, element, scope));
                        if (!parser.IsAtEnd && !parser.ExpectToken(TokenKind.Comma, "','"))
                        {
                            return new ErrorValue(braced.Location);
                        }
                    }

                    return new SequenceOfValue(items.ToImmutable(), braced.Location);
                }

            case TypeKind.Real:
                return ResolveRealTriple(parser, braced, type, scope);

            case TypeKind.CharacterString:
                return ResolveCharacterStringList(parser, braced, type, scope);

            default:
                return Mismatch(braced, type, "braced value notation is not valid here");
        }
    }

    private long? MinimumSize(AsnType type)
    {
        if (State(type).ConstraintsInProgress)
        {
            return null;
        }

        EnsureConstraints(type);
        long? min = null;
        foreach (var c in type.Constraints)
        {
            if (c.Root is Asn1.Constraints.SizeElement size && Asn1.Constraints.ConstraintSets.IntegerRoot(size.Constraint) is { LowerBound: { } lb } && lb > 0 && lb < int.MaxValue)
            {
                min = Math.Max(min ?? 0, (long)lb);
            }
        }

        return min;
    }

    private AsnValue ResolveSequenceValue(Parser parser, BracedValueSyntax braced, AsnType type, Scope scope)
    {
        var def = type.Definition;
        EnsureStructureNow(def);
        var values = ImmutableArray.CreateBuilder<NamedValue>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (!parser.IsAtEnd)
        {
            var nameTok = parser.CurrentToken;
            if (nameTok.Kind != TokenKind.Identifier)
            {
                parser.ReportUnexpected("component identifier");
                return new ErrorValue(braced.Location);
            }

            parser.Next();
            var comp = def.Components.FirstOrDefault(c => c.Name == nameTok.Text);
            var valueSyntax = parser.TryRun(p => p.ParseValue());
            if (valueSyntax is null)
            {
                return new ErrorValue(braced.Location);
            }

            if (comp is null)
            {
                Report(DiagnosticCatalog.UnknownComponentInValue, new SourceLocation(braced.Source, nameTok.Span), nameTok.Text, type.DisplayName);
            }
            else if (!seen.Add(comp.Name))
            {
                Report(DiagnosticCatalog.DuplicateComponent, new SourceLocation(braced.Source, nameTok.Span), comp.Name, "value");
            }
            else
            {
                values.Add(new NamedValue(comp.Name, ResolveValue(valueSyntax, comp.Type, scope)));
            }

            if (!parser.IsAtEnd && !parser.ExpectToken(TokenKind.Comma, "','"))
            {
                return new ErrorValue(braced.Location);
            }
        }

        foreach (var c in def.Components)
        {
            if (c.Presence == Presence.Required && !c.IsExtensionAddition && !seen.Contains(c.Name))
            {
                Report(DiagnosticCatalog.MissingComponentInValue, braced.Location, type.DisplayName, c.Name);
            }
        }

        return new SequenceValue(values.ToImmutable(), braced.Location);
    }

    private AsnValue ResolveRealTriple(Parser parser, BracedValueSyntax braced, AsnType type, Scope scope)
    {
        BigInteger? mantissa = null, @base = null, exponent = null;
        while (!parser.IsAtEnd)
        {
            var name = parser.Next();
            var valueSyntax = parser.TryRun(p => p.ParseValue());
            if (valueSyntax is null || ResolveValue(valueSyntax, IntegerType, scope) is not IntegerValue iv)
            {
                return new ErrorValue(braced.Location);
            }

            switch (name.Text)
            {
                case "mantissa":
                    mantissa = iv.Value;
                    break;
                case "base":
                    @base = iv.Value;
                    break;
                case "exponent":
                    exponent = iv.Value;
                    break;
                default:
                    return Mismatch(braced, type, $"unknown REAL component '{name.Text}'");
            }

            if (!parser.IsAtEnd && !parser.ExpectToken(TokenKind.Comma, "','"))
            {
                return new ErrorValue(braced.Location);
            }
        }

        if (mantissa is null || @base is null || exponent is null || (@base != 2 && @base != 10))
        {
            return Mismatch(braced, type, "REAL values need mantissa, base (2 or 10) and exponent");
        }

        var value = (double)mantissa.Value * Math.Pow((double)@base.Value, (double)exponent.Value);
        return new RealValue(value, braced.Location);
    }

    private AsnValue ResolveCharacterStringList(Parser parser, BracedValueSyntax braced, AsnType type, Scope scope)
    {
        // A braced value made only of 2 or 4 numbers is a Tuple or Quadruple denoting one character (X.680 41.8).
        var contentTokens = Enumerable.Range(braced.OpenBrace + 1, braced.CloseBrace - braced.OpenBrace - 1).Select(i => braced.Lex.Tokens[i]).ToList();
        if (contentTokens.Count is 3 or 7 && contentTokens.Where((_, i) => i % 2 == 0).All(t => t.Kind == TokenKind.Number) && contentTokens.Where((_, i) => i % 2 == 1).All(t => t.Kind == TokenKind.Comma))
        {
            var nums = contentTokens.Where(t => t.Kind == TokenKind.Number).Select(t => t.NumberValue).ToList();
            var codePoint = nums.Count == 4 ? (nums[0] << 24) + (nums[1] << 16) + (nums[2] << 8) + nums[3] : nums[0] * 16 + nums[1];
            if (codePoint > 0x10FFFF || (codePoint >= 0xD800 && codePoint <= 0xDFFF))
            {
                return Mismatch(braced, type, "invalid Quadruple or Tuple");
            }

            var ch = char.ConvertFromUtf32((int)codePoint);
            var err = CharacterRepertoire.Validate(type.StringKind, ch);
            return err is null ? new StringValue(ch, braced.Location) : Mismatch(braced, type, err);
        }

        // CharacterStringList: { cstring | Quadruple | Tuple | DefinedValue, ... } (X.680 41.8).
        var sb = new System.Text.StringBuilder();
        while (!parser.IsAtEnd)
        {
            var t = parser.CurrentToken;
            if (t.Kind == TokenKind.CString)
            {
                parser.Next();
                sb.Append(t.Text);
            }
            else if (t.Kind == TokenKind.LeftBrace)
            {
                var inner = parser.TryRun(p => p.ParseValue()) as BracedValueSyntax;
                if (inner is null)
                {
                    return new ErrorValue(braced.Location);
                }

                var numbers = new List<BigInteger>();
                for (var i = inner.OpenBrace + 1; i < inner.CloseBrace; i++)
                {
                    var tok = inner.Lex.Tokens[i];
                    if (tok.Kind == TokenKind.Number)
                    {
                        numbers.Add(tok.NumberValue);
                    }
                    else if (tok.Kind != TokenKind.Comma)
                    {
                        return Mismatch(braced, type, "Quadruple and Tuple components must be numbers");
                    }
                }

                var cp = numbers.Count switch
                {
                    4 => (numbers[0] << 24) + (numbers[1] << 16) + (numbers[2] << 8) + numbers[3],
                    2 => numbers[0] * 16 + numbers[1],
                    _ => BigInteger.MinusOne,
                };
                if (cp < 0 || cp > 0x10FFFF)
                {
                    return Mismatch(braced, type, "invalid Quadruple or Tuple");
                }

                sb.Append(char.ConvertFromUtf32((int)cp));
            }
            else
            {
                var vs = parser.TryRun(p => p.ParseValue());
                if (vs is null || ResolveValue(vs, type, scope) is not StringValue sv)
                {
                    return new ErrorValue(braced.Location);
                }

                sb.Append(sv.Value);
            }

            if (!parser.IsAtEnd && !parser.ExpectToken(TokenKind.Comma, "','"))
            {
                return new ErrorValue(braced.Location);
            }
        }

        var value = sb.ToString();
        var error = CharacterRepertoire.Validate(type.StringKind, value);
        return error is null ? new StringValue(value, braced.Location) : Mismatch(braced, type, error);
    }

    private AsnValue ResolveOid(Parser parser, BracedValueSyntax braced, AsnType type, Scope scope)
    {
        var relative = type.Kind == TypeKind.RelativeOid;
        var arcs = new List<BigInteger>();
        var first = true;
        while (!parser.IsAtEnd)
        {
            var t = parser.CurrentToken;
            if (t.Kind == TokenKind.Number)
            {
                parser.Next();
                arcs.Add(t.NumberValue);
            }
            else if (t.Kind == TokenKind.Identifier && parser.Peek(1).Kind == TokenKind.LeftParen)
            {
                parser.Next();
                parser.Next();
                var numSyntax = parser.TryRun(p => p.ParseValue());
                if (numSyntax is null || ResolveValue(numSyntax, IntegerType, scope) is not IntegerValue iv || iv.Value < 0)
                {
                    Report(DiagnosticCatalog.InvalidObjectIdentifier, new SourceLocation(braced.Source, t.Span), $"arc '{t.Text}' needs a non-negative number");
                    return new ErrorValue(braced.Location);
                }

                parser.ExpectToken(TokenKind.RightParen, "')'");
                arcs.Add(iv.Value);
            }
            else if (t.Kind is TokenKind.Identifier or TokenKind.TypeReference)
            {
                var wellKnown = !relative && t.Kind == TokenKind.Identifier ? WellKnownOids.Lookup(arcs, t.Text) : null;
                if (wellKnown is not null && !scope.Module.Assignments.Any(a => a.Name == t.Text))
                {
                    parser.Next();
                    arcs.Add(wellKnown.Value);
                }
                else
                {
                    var vs = parser.TryRun(p => p.ParseValue()) as ValueReferenceSyntax;
                    if (vs is null)
                    {
                        return new ErrorValue(braced.Location);
                    }

                    var lookup = LookupName(scope, vs.Module, vs.Name);
                    AsnValue? resolved = lookup.Symbol is ValueAssignmentSymbol va ? EnsureValue(va) : lookup.Parameter is { } pb ? ResolveParameterAsValue(pb, type) : null;
                    switch (resolved)
                    {
                        case ObjectIdentifierValue oid when first && !relative:
                            arcs.AddRange(oid.Value.Arcs);
                            break;
                        case RelativeOidValue rel:
                            arcs.AddRange(rel.Value.Arcs);
                            break;
                        case IntegerValue num when num.Value >= 0:
                            arcs.Add(num.Value);
                            break;
                        case null:
                        case ErrorValue:
                            return new ErrorValue(braced.Location);
                        default:
                            Report(DiagnosticCatalog.InvalidObjectIdentifier, vs.Location, $"'{vs}' cannot be used as an object identifier component");
                            return new ErrorValue(braced.Location);
                    }
                }
            }
            else
            {
                parser.ReportUnexpected("object identifier component");
                return new ErrorValue(braced.Location);
            }

            first = false;
        }

        try
        {
            return relative ? new RelativeOidValue(new RelativeObjectIdentifier(arcs), braced.Location) : new ObjectIdentifierValue(new ObjectIdentifier(arcs), braced.Location);
        }
        catch (ArgumentException e)
        {
            Report(DiagnosticCatalog.InvalidObjectIdentifier, braced.Location, e.Message);
            return new ErrorValue(braced.Location);
        }
    }

    private void ResolveDefaults(AsnType node)
    {
        if (_pendingDefaults.Count == 0)
        {
            return;
        }

        var pending = _pendingDefaults.ToList();
        _pendingDefaults.Clear();
        foreach (var (component, value, scope) in pending)
        {
            component.DefaultValue = ResolveValue(value, component.Type, scope);
        }
    }

    private void ResolveModuleOids()
    {
        foreach (var module in _moduleList)
        {
            if (module.Syntax.DefinitiveOid is not { } components)
            {
                continue;
            }

            var arcs = new List<BigInteger>();
            var ok = true;
            foreach (var c in components)
            {
                if (c.Number is NumberValueSyntax n)
                {
                    arcs.Add(n.Value);
                }
                else if (c.Name is not null && WellKnownOids.Lookup(arcs, c.Name.Text) is { } wk)
                {
                    arcs.Add(wk);
                }
                else
                {
                    ok = false;
                    break;
                }
            }

            if (ok)
            {
                try
                {
                    module.Oid = new ObjectIdentifier(arcs);
                }
                catch (ArgumentException e)
                {
                    Report(DiagnosticCatalog.InvalidObjectIdentifier, module.Syntax.Name.Location, e.Message);
                }
            }
        }
    }
}
