using System.Globalization;
using System.Numerics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Asn1.CodeGen.CSharp;

/// <summary>Helpers for building C# syntax trees with Roslyn's <see cref="SyntaxFactory"/>.</summary>
internal static class Cs
{
    // ------------------------------------------------------------------ names and types

    /// <summary>A <c>global::</c>-qualified name, e.g. <c>Global("Asn1.Runtime.Asn1Type", T)</c>.</summary>
    public static NameSyntax Global(string dottedName, params TypeSyntax[] typeArguments)
    {
        var parts = dottedName.Split('.');
        NameSyntax name = AliasQualifiedName(IdentifierName(Token(SyntaxKind.GlobalKeyword)), Simple(parts[0], parts.Length == 1 ? typeArguments : []));
        for (var i = 1; i < parts.Length; i++)
        {
            name = QualifiedName(name, Simple(parts[i], i == parts.Length - 1 ? typeArguments : []));
        }

        return name;
    }

    /// <summary>A type of the runtime namespace <c>Asn1.Runtime</c>.</summary>
    public static NameSyntax Runtime(string name, params TypeSyntax[] typeArguments) => Global("Asn1.Runtime." + name, typeArguments);

    /// <summary>A type of the constraint model namespace <c>Asn1.Constraints</c>.</summary>
    public static NameSyntax ConstraintModel(string name) => Global("Asn1.Constraints." + name);

    /// <summary>A type of the core namespace <c>Asn1</c>.</summary>
    public static NameSyntax Core(string name) => Global("Asn1." + name);

    /// <summary>A simple (optionally generic) name.</summary>
    public static SimpleNameSyntax Simple(string identifier, params TypeSyntax[] typeArguments) =>
        typeArguments.Length == 0
            ? IdentifierName(identifier)
            : GenericName(Identifier(identifier)).WithTypeArgumentList(TypeArgumentList(SeparatedList(typeArguments)));

    /// <summary><paramref name="left"/>.<paramref name="right"/> as a name.</summary>
    public static NameSyntax Dot(NameSyntax left, string right) => QualifiedName(left, IdentifierName(right));

    public static TypeSyntax IntType => PredefinedType(Token(SyntaxKind.IntKeyword));

    public static TypeSyntax LongType => PredefinedType(Token(SyntaxKind.LongKeyword));

    /// <summary>The predefined type of an integer keyword (sbyte, byte, short, ushort, int, uint, long, ulong).</summary>
    public static TypeSyntax IntegerKeywordType(string keyword) => PredefinedType(Token(keyword switch
    {
        "sbyte" => SyntaxKind.SByteKeyword,
        "byte" => SyntaxKind.ByteKeyword,
        "short" => SyntaxKind.ShortKeyword,
        "ushort" => SyntaxKind.UShortKeyword,
        "int" => SyntaxKind.IntKeyword,
        "uint" => SyntaxKind.UIntKeyword,
        "long" => SyntaxKind.LongKeyword,
        "ulong" => SyntaxKind.ULongKeyword,
        _ => throw new ArgumentOutOfRangeException(nameof(keyword), keyword, "not an integer keyword"),
    }));

    public static TypeSyntax BoolType => PredefinedType(Token(SyntaxKind.BoolKeyword));

    public static TypeSyntax DoubleType => PredefinedType(Token(SyntaxKind.DoubleKeyword));

    public static TypeSyntax StringType => PredefinedType(Token(SyntaxKind.StringKeyword));

    public static TypeSyntax ObjectType => PredefinedType(Token(SyntaxKind.ObjectKeyword));

    public static TypeSyntax ByteArray => ArrayOf(PredefinedType(Token(SyntaxKind.ByteKeyword)));

    public static NameSyntax BigIntegerType => Global("System.Numerics.BigInteger");

    public static TypeSyntax ArrayOf(TypeSyntax element) =>
        ArrayType(element).WithRankSpecifiers(SingletonList(ArrayRankSpecifier(SingletonSeparatedList<ExpressionSyntax>(OmittedArraySizeExpression()))));

    public static TypeSyntax Nullable(TypeSyntax type) => NullableType(type);

    // ------------------------------------------------------------------ expressions

    public static IdentifierNameSyntax Id(string name) => IdentifierName(name);

    public static ExpressionSyntax Null => LiteralExpression(SyntaxKind.NullLiteralExpression);

    /// <summary><c>null!</c></summary>
    public static ExpressionSyntax NullForgiving => PostfixUnaryExpression(SyntaxKind.SuppressNullableWarningExpression, Null);

    public static ExpressionSyntax Bool(bool value) => LiteralExpression(value ? SyntaxKind.TrueLiteralExpression : SyntaxKind.FalseLiteralExpression);

    public static ExpressionSyntax Str(string value) => LiteralExpression(SyntaxKind.StringLiteralExpression, Literal(value));

    public static ExpressionSyntax Int32(int value) => value < 0
        ? value == int.MinValue ? Member(IntType, "MinValue") : PrefixUnaryExpression(SyntaxKind.UnaryMinusExpression, Int32(-value))
        : LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(value));

    public static ExpressionSyntax Int64(long value) => value < 0
        ? value == long.MinValue ? Member(LongType, "MinValue") : PrefixUnaryExpression(SyntaxKind.UnaryMinusExpression, Int64(-value))
        : LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(value.ToString(CultureInfo.InvariantCulture) + "L", value));

    public static ExpressionSyntax UInt32(uint value) =>
        LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(value.ToString(CultureInfo.InvariantCulture) + "U", value));

    public static ExpressionSyntax UInt64(ulong value) =>
        LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(value.ToString(CultureInfo.InvariantCulture) + "UL", value));

    /// <summary>A hexadecimal byte literal such as <c>0x0A</c>.</summary>
    public static ExpressionSyntax HexByte(byte value) =>
        LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal("0x" + value.ToString("X2", CultureInfo.InvariantCulture), (int)value));

    /// <summary>A double literal that always reads as a double (<c>1.0</c>, <c>1E+20d</c>) or a named special value.</summary>
    public static ExpressionSyntax Float64(double d)
    {
        if (double.IsPositiveInfinity(d))
        {
            return Member(DoubleType, "PositiveInfinity");
        }

        if (double.IsNegativeInfinity(d))
        {
            return Member(DoubleType, "NegativeInfinity");
        }

        if (double.IsNaN(d))
        {
            return Member(DoubleType, "NaN");
        }

        if (d < 0 || (d == 0 && double.IsNegative(d)))
        {
            return PrefixUnaryExpression(SyntaxKind.UnaryMinusExpression, Float64(-d));
        }

        var s = d.ToString("R", CultureInfo.InvariantCulture);
        s = s.Contains('.', StringComparison.Ordinal) || s.Contains('E', StringComparison.Ordinal) ? s + "d" : s + ".0";
        return LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(s, d));
    }

    private static readonly string[] s_unsigned = ["byte", "ushort", "uint", "ulong"];
    private static readonly string[] s_signed = ["sbyte", "short", "int", "long"];
    private static readonly string[] s_constants = ["int", "long", "ulong"];

    /// <summary>
    /// The smallest CLR integer keyword that holds every value of [<paramref name="lo"/>, <paramref name="hi"/>]:
    /// unsigned for non-negative ranges, signed otherwise; "BigInteger" beyond 64 bits.
    /// </summary>
    public static string SmallestIntegerKeyword(BigInteger lo, BigInteger hi) =>
        (lo >= 0 ? s_unsigned : s_signed).FirstOrDefault(k => Fits(lo, k) && Fits(hi, k)) ?? "BigInteger";

    /// <summary>The type of an integer constant without a fixed type: int, long, ulong or BigInteger, by value.</summary>
    public static string ConstantKeyword(BigInteger value) => s_constants.FirstOrDefault(k => Fits(value, k)) ?? "BigInteger";

    /// <summary>True if <paramref name="value"/> is representable in the integer type named by <paramref name="clrKeyword"/>.</summary>
    public static bool Fits(BigInteger value, string clrKeyword) => clrKeyword switch
    {
        "sbyte" => value >= sbyte.MinValue && value <= sbyte.MaxValue,
        "byte" => value >= byte.MinValue && value <= byte.MaxValue,
        "short" => value >= short.MinValue && value <= short.MaxValue,
        "ushort" => value >= ushort.MinValue && value <= ushort.MaxValue,
        "int" => value >= int.MinValue && value <= int.MaxValue,
        "uint" => value >= uint.MinValue && value <= uint.MaxValue,
        "long" => value >= long.MinValue && value <= long.MaxValue,
        "ulong" => value >= ulong.MinValue && value <= ulong.MaxValue,
        "BigInteger" => true,
        _ => throw new ArgumentOutOfRangeException(nameof(clrKeyword), clrKeyword, "not an integer keyword"),
    };

    /// <summary>
    /// An integer literal of the given CLR keyword type, or a BigInteger expression, for a value known to fit the type.
    /// C# has no literal suffix for sbyte, byte, short and ushort, so their values are cast (<c>(byte)5</c>) and keep
    /// their type in every context.
    /// </summary>
    public static ExpressionSyntax Integer(BigInteger value, string clrKeyword) =>
        TryInteger(value, clrKeyword) ?? throw new ArgumentOutOfRangeException(nameof(value), value, $"does not fit {clrKeyword}");

    /// <summary>Like <see cref="Integer"/>, but null if the value does not fit the type.</summary>
    public static ExpressionSyntax? TryInteger(BigInteger value, string clrKeyword) => !Fits(value, clrKeyword) ? null : clrKeyword switch
    {
        "sbyte" or "byte" or "short" or "ushort" => Cast(IntegerKeywordType(clrKeyword), Int32((int)value)),
        "int" => Int32((int)value),
        "uint" => UInt32((uint)value),
        "long" => Int64((long)value),
        "ulong" => UInt64((ulong)value),
        _ => Big(value),
    };

    /// <summary><c>new BigInteger(nL)</c>, or <c>BigInteger.Parse("...", CultureInfo.InvariantCulture)</c> beyond 64 bits.</summary>
    public static ExpressionSyntax Big(BigInteger value) =>
        value >= long.MinValue && value <= long.MaxValue
            ? New(BigIntegerType, Int64((long)value))
            : Call(Member(BigIntegerType, "Parse"), Str(value.ToString(CultureInfo.InvariantCulture)), Member(Global("System.Globalization.CultureInfo"), "InvariantCulture"));

    public static MemberAccessExpressionSyntax Member(ExpressionSyntax target, string name) =>
        MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, target, IdentifierName(name));

    public static InvocationExpressionSyntax Call(ExpressionSyntax target, params ExpressionSyntax[] arguments) =>
        InvocationExpression(target, Args(arguments));

    public static InvocationExpressionSyntax Call(ExpressionSyntax target, IEnumerable<ArgumentSyntax> arguments) =>
        InvocationExpression(target, ArgumentList(SeparatedList(arguments)));

    public static ArgumentListSyntax Args(params ExpressionSyntax[] arguments) => ArgumentList(SeparatedList(arguments.Select(Argument)));

    /// <summary>A named argument <c>name: value</c>.</summary>
    public static ArgumentSyntax Named(string name, ExpressionSyntax value) => Argument(value).WithNameColon(NameColon(IdentifierName(name)));

    public static ObjectCreationExpressionSyntax New(TypeSyntax type, params ExpressionSyntax[] arguments) =>
        ObjectCreationExpression(type).WithArgumentList(Args(arguments));

    public static ObjectCreationExpressionSyntax New(TypeSyntax type, IEnumerable<ArgumentSyntax> arguments) =>
        ObjectCreationExpression(type).WithArgumentList(ArgumentList(SeparatedList(arguments)));

    /// <summary><c>new T { A = a, B = b }</c></summary>
    public static ObjectCreationExpressionSyntax NewWithInitializer(TypeSyntax type, IEnumerable<(string Member, ExpressionSyntax Value)> members) =>
        ObjectCreationExpression(type).WithInitializer(InitializerExpression(
            SyntaxKind.ObjectInitializerExpression,
            SeparatedList<ExpressionSyntax>(members.Select(m => AssignmentExpression(SyntaxKind.SimpleAssignmentExpression, IdentifierName(m.Member), m.Value)))));

    /// <summary><c>new T { a, b }</c> (a collection initializer).</summary>
    public static ObjectCreationExpressionSyntax NewWithItems(TypeSyntax type, IEnumerable<ExpressionSyntax> items) =>
        ObjectCreationExpression(type).WithInitializer(InitializerExpression(SyntaxKind.CollectionInitializerExpression, SeparatedList(items)));

    /// <summary><c>new byte[] { 0x01, ... }</c></summary>
    public static ExpressionSyntax NewByteArray(ReadOnlySpan<byte> bytes)
    {
        var items = new List<ExpressionSyntax>(bytes.Length);
        foreach (var b in bytes)
        {
            items.Add(HexByte(b));
        }

        return ArrayCreationExpression((ArrayTypeSyntax)ByteArray).WithInitializer(InitializerExpression(SyntaxKind.ArrayInitializerExpression, SeparatedList(items)));
    }

    /// <summary>A collection expression <c>[a, b]</c>.</summary>
    public static CollectionExpressionSyntax Collection(IEnumerable<ExpressionSyntax> items) =>
        CollectionExpression(SeparatedList<CollectionElementSyntax>(items.Select(ExpressionElement)));

    public static CollectionExpressionSyntax EmptyCollection => CollectionExpression();

    /// <summary>A static lambda; no parameters gives <c>static () =&gt; body</c>, one gives <c>static p =&gt; body</c>.</summary>
    public static LambdaExpressionSyntax Lambda(ExpressionSyntax body, params string[] parameters)
    {
        var modifiers = TokenList(Token(SyntaxKind.StaticKeyword));
        if (parameters.Length == 1)
        {
            return SimpleLambdaExpression(Parameter(Identifier(parameters[0]))).WithModifiers(modifiers).WithExpressionBody(body);
        }

        return ParenthesizedLambdaExpression()
            .WithParameterList(ParameterList(SeparatedList(parameters.Select(p => Parameter(Identifier(p))))))
            .WithModifiers(modifiers)
            .WithExpressionBody(body);
    }

    public static ExpressionSyntax Assign(ExpressionSyntax target, ExpressionSyntax value) =>
        AssignmentExpression(SyntaxKind.SimpleAssignmentExpression, target, value);

    public static ExpressionSyntax Cast(TypeSyntax type, ExpressionSyntax value) => CastExpression(type, value);

    public static ExpressionSyntax Paren(ExpressionSyntax value) => ParenthesizedExpression(value);

    public static ExpressionSyntax And(ExpressionSyntax left, ExpressionSyntax right) => BinaryExpression(SyntaxKind.LogicalAndExpression, left, right);

    /// <summary><c>value!</c></summary>
    public static ExpressionSyntax NotNull(ExpressionSyntax value) => PostfixUnaryExpression(SyntaxKind.SuppressNullableWarningExpression, value);

    /// <summary><c>value is not null</c></summary>
    public static ExpressionSyntax IsNotNull(ExpressionSyntax value) =>
        IsPatternExpression(value, UnaryPattern(Token(SyntaxKind.NotKeyword), ConstantPattern(Null)));

    /// <summary><c>value is T name</c></summary>
    public static ExpressionSyntax IsType(ExpressionSyntax value, TypeSyntax type, string name) =>
        IsPatternExpression(value, DeclarationPattern(type, SingleVariableDesignation(Identifier(name))));

    /// <summary><c>field ??= value</c> (C# 14 field-backed property).</summary>
    public static ExpressionSyntax FieldOrInit(ExpressionSyntax value) =>
        AssignmentExpression(SyntaxKind.CoalesceAssignmentExpression, FieldExpression(), value);

    // ------------------------------------------------------------------ declarations

    public static SyntaxToken Kw(SyntaxKind kind) => Token(kind);

    public static SyntaxTokenList Modifiers(params SyntaxKind[] kinds) => TokenList(kinds.Select(Token));

    /// <summary><c>{ get; set; }</c> or <c>{ get; }</c></summary>
    public static AccessorListSyntax Accessors(bool settable) => AccessorList(List(
        settable
            ? new[] { Accessor(SyntaxKind.GetAccessorDeclaration), Accessor(SyntaxKind.SetAccessorDeclaration) }
            : new[] { Accessor(SyntaxKind.GetAccessorDeclaration) }));

    private static AccessorDeclarationSyntax Accessor(SyntaxKind kind) => AccessorDeclaration(kind).WithSemicolonToken(Token(SyntaxKind.SemicolonToken));

    /// <summary>An auto-property, optionally initialised.</summary>
    public static PropertyDeclarationSyntax AutoProperty(SyntaxTokenList modifiers, TypeSyntax type, string name, bool settable, ExpressionSyntax? initializer = null)
    {
        var p = PropertyDeclaration(type, name).WithModifiers(modifiers).WithAccessorList(Accessors(settable));
        return initializer is null ? p : p.WithInitializer(EqualsValueClause(initializer)).WithSemicolonToken(Token(SyntaxKind.SemicolonToken));
    }

    /// <summary>An expression-bodied property <c>T Name =&gt; body;</c></summary>
    public static PropertyDeclarationSyntax ArrowProperty(SyntaxTokenList modifiers, TypeSyntax type, string name, ExpressionSyntax body) =>
        PropertyDeclaration(type, name).WithModifiers(modifiers).WithExpressionBody(ArrowExpressionClause(body)).WithSemicolonToken(Token(SyntaxKind.SemicolonToken));

    /// <summary>An expression-bodied method.</summary>
    public static MethodDeclarationSyntax ArrowMethod(SyntaxTokenList modifiers, TypeSyntax returnType, string name, IEnumerable<ParameterSyntax> parameters, ExpressionSyntax body) =>
        MethodDeclaration(returnType, name).WithModifiers(modifiers).WithParameterList(ParameterList(SeparatedList(parameters)))
            .WithExpressionBody(ArrowExpressionClause(body)).WithSemicolonToken(Token(SyntaxKind.SemicolonToken));

    /// <summary>An abstract method (no body).</summary>
    public static MethodDeclarationSyntax AbstractMethod(SyntaxTokenList modifiers, TypeSyntax returnType, string name, IEnumerable<ParameterSyntax> parameters) =>
        MethodDeclaration(returnType, name).WithModifiers(modifiers).WithParameterList(ParameterList(SeparatedList(parameters))).WithSemicolonToken(Token(SyntaxKind.SemicolonToken));

    public static ParameterSyntax Param(TypeSyntax type, string name) => Parameter(Identifier(name)).WithType(type);

    /// <summary>A field such as <c>public const int X = 1;</c>.</summary>
    public static FieldDeclarationSyntax Field(SyntaxTokenList modifiers, TypeSyntax type, string name, ExpressionSyntax value) =>
        FieldDeclaration(VariableDeclaration(type, SingletonSeparatedList(VariableDeclarator(name).WithInitializer(EqualsValueClause(value))))).WithModifiers(modifiers);

    /// <summary>An attribute list holding one attribute.</summary>
    public static AttributeListSyntax Attribute(NameSyntax name, params ExpressionSyntax[] arguments) =>
        AttributeList(SingletonSeparatedList(SyntaxFactory.Attribute(name, AttributeArgumentList(SeparatedList(arguments.Select(AttributeArgument))))));

    /// <summary>A base list of the given types.</summary>
    public static BaseListSyntax Bases(params TypeSyntax[] types) => BaseList(SeparatedList<BaseTypeSyntax>(types.Select(SimpleBaseType)));
}
