using Asn1.Diagnostics;
using Asn1.Syntax;
using Xunit;

namespace Asn1.ParserTests;

public class ParserTests
{
    private static (CompilationUnitSyntax Unit, List<Diagnostic> Diagnostics) Parse(string text)
    {
        var bag = new DiagnosticBag();
        var unit = Parser.ParseCompilationUnit(new SourceText("t.asn", text), bag);
        return (unit, [.. bag.ToImmutableSorted()]);
    }

    private static ModuleDefinitionSyntax Module(string body, string header = "M DEFINITIONS AUTOMATIC TAGS ::=")
    {
        var (unit, diags) = Parse(header + " BEGIN\n" + body + "\nEND");
        Assert.Empty(diags.Where(d => d.IsError).Select(d => d.ToString()));
        return Assert.Single(unit.Modules);
    }

    [Fact]
    public void ModuleHeaderWithOidTagsExtensibilityExportsAndImports()
    {
        var (unit, diags) = Parse("""
            My-Module { iso(1) member-body(2) 840 } DEFINITIONS IMPLICIT TAGS EXTENSIBILITY IMPLIED ::= BEGIN
            EXPORTS T, v;
            IMPORTS A, B{} FROM Other { 1 2 3 } C FROM Third id-third D FROM Fourth;
            T ::= A
            v INTEGER ::= 1
            END
            """);
        Assert.Empty(diags);
        var m = Assert.Single(unit.Modules);
        Assert.Equal("My-Module", m.Name.Text);
        Assert.Equal(3, m.DefinitiveOid!.Value.Length);
        Assert.Equal(TagDefault.Implicit, m.TagDefault);
        Assert.True(m.ExtensibilityImplied);
        Assert.Equal(["T", "v"], m.Exports!.Symbols.Select(s => s.Name.Text));
        Assert.Equal(["Other", "Third", "Fourth"], m.Imports.Select(i => i.ModuleName.Text));
        Assert.True(m.Imports[0].Symbols[1].IsParameterized);
        Assert.NotNull(m.Imports[1].AssignedValue);
    }

    [Fact]
    public void SequenceWithExtensionsGroupsDefaultsAndComponentsOf()
    {
        var m = Module("S ::= SEQUENCE { a INTEGER, b BOOLEAN OPTIONAL, c IA5String DEFAULT \"x\", COMPONENTS OF T, ..., [[ 2: d NULL ]], e REAL, ..., f OCTET STRING }");
        var s = (StructuredTypeSyntax)((TypeAssignmentSyntax)m.Assignments[0]).Type;
        Assert.Equal(StructuredKind.Sequence, s.Kind);
        Assert.Equal(9, s.Items.Length);
        Assert.IsType<ComponentsOfSyntax>(s.Items[3]);
        Assert.IsType<ExtensionGroupSyntax>(s.Items[5]);
        Assert.Equal(ComponentPresence.Default, ((NamedComponentSyntax)s.Items[2]).Presence);
    }

    [Fact]
    public void ConstraintsOfAllShapes()
    {
        var m = Module("""
            A ::= INTEGER (0..10 | 20<..<30, ..., 40)
            B ::= IA5String (SIZE (1..4) ^ FROM ("a".."z")) (PATTERN "[a-z]+")
            C ::= SEQUENCE OF INTEGER (ALL EXCEPT 5)
            D ::= S (WITH COMPONENTS { ..., a (1..2) PRESENT, b ABSENT })
            E ::= SEQUENCE SIZE (1..MAX) OF E2
            F ::= OCTET STRING (CONTAINING INTEGER ENCODED BY { 2 1 2 1 })
            G ::= INTEGER (MIN..0)
            """);
        Assert.Equal(7, m.Assignments.Length);
        var a = (ConstrainedTypeSyntax)((TypeAssignmentSyntax)m.Assignments[0]).Type;
        var specs = (ElementSetSpecsSyntax)a.Constraints[0].Spec;
        Assert.True(specs.IsExtensible);
        Assert.IsType<UnionSyntax>(specs.Root);
        var range = (ValueRangeSyntax)((UnionSyntax)specs.Root!).Items[1];
        Assert.True(range.Lower.IsOpen && range.Upper.IsOpen);
    }

    [Fact]
    public void ClassesObjectsAndTableConstraints()
    {
        var m = Module("""
            ALG ::= CLASS { &id OBJECT IDENTIFIER UNIQUE, &Params OPTIONAL } WITH SYNTAX { ID &id [PARAMS &Params] }
            Algs ALG ::= { { ID { 1 2 3 } PARAMS INTEGER } | alg2, ... }
            alg2 ALG ::= { ID { 1 2 4 } }
            AlgId ::= SEQUENCE { id ALG.&id ({Algs}), params ALG.&Params ({Algs}{@id}) OPTIONAL }
            """);
        var cls = Assert.IsType<ClassAssignmentSyntax>(m.Assignments[0]);
        var def = (ObjectClassDefinitionSyntax)cls.Class;
        Assert.Equal(2, def.Fields.Length);
        Assert.True(def.Fields[0].IsUnique);
        Assert.NotNull(def.WithSyntax);
        Assert.IsType<SetAssignmentSyntax>(m.Assignments[1]);
        var seq = (StructuredTypeSyntax)((TypeAssignmentSyntax)m.Assignments[3]).Type;
        var param = (NamedComponentSyntax)seq.Items[1];
        var table = (TableConstraintSyntax)((ConstrainedTypeSyntax)param.Type).Constraints[0].Spec;
        Assert.Equal("id", Assert.Single(table.AtNotations).Path[0].Text);
    }

    [Fact]
    public void ParameterizedAssignmentsAndReferences()
    {
        var m = Module("""
            Pair { A, INTEGER : n } ::= SEQUENCE { a A, b INTEGER (0..n) }
            P ::= Pair { BOOLEAN, 5 }
            """);
        Assert.Equal(2, m.Assignments[0].Parameters.Length);
        Assert.NotNull(m.Assignments[0].Parameters[1].Governor);
        var p = (TypeReferenceSyntax)((TypeAssignmentSyntax)m.Assignments[1]).Type;
        Assert.Equal(2, p.ActualParameters!.Value.Length);
        Assert.NotNull(p.ActualParameters.Value[0].AsType);
        Assert.NotNull(p.ActualParameters.Value[1].AsValue);
    }

    [Fact]
    public void DeeplyNestedDefinitionsParse()
    {
        var depth = 60;
        var text = "T ::= " + string.Concat(Enumerable.Repeat("SEQUENCE { a ", depth)) + "INTEGER" + string.Concat(Enumerable.Repeat(" }", depth));
        Module(text);
    }

    [Fact]
    public void ExcessiveNestingIsReportedInsteadOfOverflowingTheStack()
    {
        var depth = 5000;
        var text = "M DEFINITIONS ::= BEGIN T ::= " + string.Concat(Enumerable.Repeat("SEQUENCE { a ", depth)) + "INTEGER" + string.Concat(Enumerable.Repeat(" }", depth)) + " END";
        var (_, diags) = Parse(text);
        Assert.Contains(diags, d => d.Id == "ASN1018");
    }

    [Fact]
    public void ErrorRecoveryReportsSeveralErrorsAndKeepsGoodAssignments()
    {
        var (unit, diags) = Parse("""
            M DEFINITIONS ::= BEGIN
            A ::= SEQUENCE { a INTEGER, b }
            B ::= INTEGER
            C ::= SEQUENCE OF
            D ::= BOOLEAN
            E ::= CHOICE { x INTEGER y BOOLEAN }
            F ::= NULL
            END
            """);
        var errors = diags.Where(d => d.IsError).ToList();
        Assert.True(errors.Count >= 3, string.Join("\n", errors));
        // The error in C (missing element type) is reported at the next token, on the line of D.
        Assert.Equal([2, 5, 6], errors.Select(e => e.Location.Start.Line).Distinct().Take(3));
        var names = unit.Modules[0].Assignments.Select(a => a.Name.Text).ToList();
        Assert.Contains("B", names);
        Assert.Contains("D", names);
        Assert.Contains("F", names);
    }

    [Fact]
    public void MissingEndIsReported()
    {
        var (_, diags) = Parse("M DEFINITIONS ::= BEGIN T ::= INTEGER");
        Assert.Contains(diags, d => d.Id == "ASN1011");
    }

    [Fact]
    public void CommentsAreAttachedAsDocumentation()
    {
        var m = Module("""
            -- A request.
            -- Second line.
            Req ::= SEQUENCE {
                id INTEGER, -- The identifier
                -- Leading comment for flag
                flag BOOLEAN
            }
            """);
        var a = (TypeAssignmentSyntax)m.Assignments[0];
        Assert.Equal("A request.\nSecond line.", a.Documentation);
        var s = (StructuredTypeSyntax)a.Type;
        Assert.Equal("The identifier", ((NamedComponentSyntax)s.Items[0]).Documentation);
        Assert.Equal("Leading comment for flag", ((NamedComponentSyntax)s.Items[1]).Documentation);
    }

    [Fact]
    public void BracedValuesAreDeferredAsTokenRanges()
    {
        var m = Module("v SEQUENCE { a INTEGER } ::= { a 5 }\noid OBJECT IDENTIFIER ::= { iso 3 6 1 }");
        var v = (ValueAssignmentSyntax)m.Assignments[0];
        var braced = Assert.IsType<BracedValueSyntax>(v.Value);
        Assert.False(braced.IsEmpty);
    }

    [Fact]
    public void LegacyNotationIsAcceptedWithWarnings()
    {
        var (_, diags) = Parse("M DEFINITIONS ::= BEGIN T ::= SEQUENCE { a ANY DEFINED BY b, b INTEGER } END");
        Assert.Contains(diags, d => d.Id == "ASN1014" && d.Severity == DiagnosticSeverity.Warning);
        Assert.DoesNotContain(diags, d => d.IsError);
    }

    [Fact]
    public void MultipleModulesInOneFile()
    {
        var (unit, diags) = Parse("A DEFINITIONS ::= BEGIN T ::= INTEGER END B DEFINITIONS ::= BEGIN U ::= BOOLEAN END");
        Assert.Empty(diags);
        Assert.Equal(["A", "B"], unit.Modules.Select(m => m.Name.Text));
    }
}
