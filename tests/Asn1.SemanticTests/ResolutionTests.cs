using Asn1.Semantics;
using Xunit;
using static Asn1.SemanticTests.TestHelpers;

namespace Asn1.SemanticTests;

public class ResolutionTests
{
    [Fact]
    public void ImportsResolveAcrossModules()
    {
        var a = "A DEFINITIONS AUTOMATIC TAGS ::= BEGIN EXPORTS T, maxT; T ::= INTEGER (0..maxT) maxT INTEGER ::= 7 U ::= BOOLEAN END";
        var b = "B DEFINITIONS AUTOMATIC TAGS ::= BEGIN IMPORTS T FROM A; S ::= SEQUENCE { t T } END";
        var c = CompileClean(a, b);
        var t = c.Type("B.S").Component("t").Type;
        Assert.Equal(TypeKind.Integer, t.Kind);
        Assert.Equal("(0..7)", t.Constraints.Single().ToString());
    }

    [Fact]
    public void ImportOfUnexportedSymbolIsReported()
    {
        var a = "A DEFINITIONS ::= BEGIN EXPORTS T; T ::= INTEGER U ::= BOOLEAN END";
        var b = "B DEFINITIONS ::= BEGIN IMPORTS U FROM A; S ::= SEQUENCE { u U } END";
        Compile(a, b).AssertDiagnostic("ASN2005");
    }

    [Fact]
    public void MissingImportedModuleIsReported() =>
        Compile("B DEFINITIONS ::= BEGIN IMPORTS X FROM Nowhere; S ::= SEQUENCE { x X } END").AssertDiagnostic("ASN2004");

    [Fact]
    public void AmbiguousImportIsReported()
    {
        var a = "A DEFINITIONS ::= BEGIN T ::= INTEGER END";
        var b = "B DEFINITIONS ::= BEGIN T ::= BOOLEAN END";
        var c = "C DEFINITIONS ::= BEGIN IMPORTS T FROM A T FROM B; S ::= SEQUENCE { t T } END";
        Compile(a, b, c).AssertDiagnostic("ASN2007");
    }

    [Fact]
    public void ModuleQualifiedReferencesResolve()
    {
        var a = "A DEFINITIONS ::= BEGIN T ::= INTEGER END";
        var b = "B DEFINITIONS ::= BEGIN IMPORTS T FROM A; S ::= SEQUENCE { t A.T } END";
        Assert.Equal(TypeKind.Integer, CompileClean(a, b).Type("B.S").Component("t").Type.Kind);
    }

    [Fact]
    public void UndefinedReferenceHasPreciseLocation()
    {
        var c = Compile(Module("S ::= SEQUENCE {\n  a Missing\n}"));
        var d = Assert.Single(c.Diagnostics, d => d.Id == "ASN2001");
        Assert.Equal(3, d.Location.Start.Line);
        Assert.Equal(5, d.Location.Start.Column);
    }

    [Fact]
    public void DuplicateDefinitionIsReported() =>
        Compile(Module("T ::= INTEGER\nT ::= BOOLEAN")).AssertDiagnostic("ASN2002", 3);

    [Fact]
    public void RecursiveTypesThroughOptionalAreAccepted()
    {
        var c = CompileClean(Module("Node ::= SEQUENCE { value INTEGER, next Node OPTIONAL }\nTree ::= SEQUENCE { children SEQUENCE OF Tree }"));
        var next = c.Type("Node").Component("next").Type;
        Assert.Same(c.Type("Node").Definition, next.Definition);
    }

    [Fact]
    public void MutuallyRecursiveTypesAreAccepted() =>
        CompileClean(Module("A ::= SEQUENCE { b B OPTIONAL }\nB ::= CHOICE { a A, leaf NULL }"));

    [Fact]
    public void InfinitelyRecursiveTypeIsRejected() =>
        Compile(Module("A ::= SEQUENCE { a A }")).AssertDiagnostic("ASN2008");

    [Fact]
    public void RecursionThroughNonEmptyCollectionIsRejected() =>
        Compile(Module("A ::= SEQUENCE { a SEQUENCE SIZE(1..2) OF A }")).AssertDiagnostic("ASN2008");

    [Fact]
    public void AliasCycleIsRejected() =>
        Compile(Module("A ::= B\nB ::= A")).AssertDiagnostic("ASN2008");

    [Fact]
    public void EnumerationValuesFollowX680()
    {
        var c = CompileClean(Module("E ::= ENUMERATED { a, b(0), c, ..., d, e(10), f }"));
        var items = c.Type("E").EnumItems.ToDictionary(i => i.Name, i => (int)i.Value);
        Assert.Equal(1, items["a"]);
        Assert.Equal(0, items["b"]);
        Assert.Equal(2, items["c"]);
        Assert.Equal(3, items["d"]);
        Assert.Equal(10, items["e"]);
        Assert.Equal(11, items["f"]);
        Assert.True(c.Type("E").IsExtensible);
    }

    [Fact]
    public void DuplicateEnumerationValuesAndNamesAreReported()
    {
        Compile(Module("E ::= ENUMERATED { a(1), b(1) }")).AssertDiagnostic("ASN2012");
        Compile(Module("E ::= ENUMERATED { a, a }")).AssertDiagnostic("ASN2011");
    }

    [Fact]
    public void ExtensibilityImpliedMakesTypesExtensible()
    {
        var c = CompileClean(Module("S ::= SEQUENCE { a INTEGER }\nE ::= ENUMERATED { x }", "AUTOMATIC TAGS EXTENSIBILITY IMPLIED"));
        Assert.True(c.Type("S").IsExtensible);
        Assert.True(c.Type("E").IsExtensible);
    }

    [Fact]
    public void ParameterizedTypesAreInstantiated()
    {
        var c = CompileClean(Module("Pair { A, B } ::= SEQUENCE { first A, second B }\nP ::= Pair { INTEGER, BOOLEAN }\nBounded { INTEGER : n } ::= INTEGER (0..n)\nX ::= Bounded { 9 }"));
        var p = c.Type("P");
        Assert.Equal(TypeKind.Integer, p.Component("first").Type.Kind);
        Assert.Equal(TypeKind.Boolean, p.Component("second").Type.Kind);
        Assert.Equal("(0..9)", c.Type("X").Constraints.Single().ToString());
    }

    [Fact]
    public void RecursiveParameterizedTypeTerminates()
    {
        var c = CompileClean(Module("List { T } ::= SEQUENCE { elem T, next List { T } OPTIONAL }\nIntList ::= List { INTEGER }"));
        var l = c.Type("IntList");
        Assert.Same(l.Definition, l.Component("next").Type.Definition);
    }

    [Fact]
    public void ParameterCountMismatchIsReported() =>
        Compile(Module("Pair { A, B } ::= SEQUENCE { first A, second B }\nP ::= Pair { INTEGER }")).AssertDiagnostic("ASN2022");

    [Fact]
    public void SelectionTypeResolvesAlternative()
    {
        var c = CompileClean(Module("C ::= CHOICE { a INTEGER, b BOOLEAN }\nT ::= b < C"));
        Assert.Equal(TypeKind.Boolean, c.Type("T").Kind);
    }

    [Fact]
    public void ValueSetAssignmentDefinesConstrainedType()
    {
        var c = CompileClean(Module("Small INTEGER ::= { 1 | 2 | 3 }"));
        Assert.Equal("(1 | 2 | 3)", c.Type("Small").Constraints.Single().ToString());
    }

    [Fact]
    public void MacroDefinitionsAreSkippedWithWarning()
    {
        var c = Compile(Module("OBJECT-TYPE MACRO ::= BEGIN TYPE NOTATION ::= \"SYNTAX\" type(Syntax) VALUE NOTATION ::= value(VALUE ObjectName) END\nT ::= INTEGER", "EXPLICIT TAGS"));
        c.AssertDiagnostic("ASN1014");
        Assert.NotNull(c.FindType("T"));
    }
}
