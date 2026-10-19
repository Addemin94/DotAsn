using System.Numerics;
using Asn1.Constraints;
using Asn1.Semantics;
using Xunit;
using static Asn1.SemanticTests.TestHelpers;

namespace Asn1.SemanticTests;

public class ValueAndConstraintTests
{
    private static AsnValue Value(Asn1Compilation c, string name) =>
        c.Modules.SelectMany(m => m.ValueAssignments).Single(v => v.Name == name).Value!;

    [Fact]
    public void ObjectIdentifierValuesResolveNamesAndReferences()
    {
        var c = CompileClean(Module("base OBJECT IDENTIFIER ::= { iso member-body(2) us(840) 113549 }\nrsa OBJECT IDENTIFIER ::= { base 1 1 }"));
        Assert.Equal("1.2.840.113549.1.1", ((ObjectIdentifierValue)Value(c, "rsa")).Value.ToString());
    }

    [Fact]
    public void InvalidObjectIdentifierIsReported() =>
        Compile(Module("x OBJECT IDENTIFIER ::= { 3 1 }")).AssertDiagnostic("ASN2032");

    [Fact]
    public void SequenceValuesAreResolvedAgainstComponents()
    {
        var c = CompileClean(Module("S ::= SEQUENCE { a INTEGER, b BOOLEAN OPTIONAL, c IA5String }\ns S ::= { a 5, c \"hi\" }"));
        var s = (SequenceValue)Value(c, "s");
        Assert.Equal(new BigInteger(5), ((IntegerValue)s["a"]!).Value);
        Assert.Null(s["b"]);
    }

    [Fact]
    public void MissingMandatoryComponentInValueIsReported() =>
        Compile(Module("S ::= SEQUENCE { a INTEGER, b BOOLEAN }\ns S ::= { a 5 }")).AssertDiagnostic("ASN2029");

    [Fact]
    public void NamedBitsAndHexStrings()
    {
        var c = CompileClean(Module("B ::= BIT STRING { r(0), w(1), x(2) }\nb B ::= { r, x }\nh BIT STRING ::= 'A5'H\no OCTET STRING ::= '0101'B"));
        Assert.Equal("101", ((BitStringValue)Value(c, "b")).Value.ToBinaryString());
        Assert.Equal("10100101", ((BitStringValue)Value(c, "h")).Value.ToBinaryString());
        Assert.Equal("50", Convert.ToHexString(((OctetStringValue)Value(c, "o")).Value.AsSpan()));
    }

    [Fact]
    public void ChoiceAndSequenceOfValues()
    {
        var c = CompileClean(Module("C ::= CHOICE { a INTEGER, b BOOLEAN }\nc C ::= b : TRUE\nL ::= SEQUENCE OF INTEGER\nl L ::= { 1, 2, 3 }"));
        Assert.Equal("b", ((ChoiceValue)Value(c, "c")).Alternative);
        Assert.Equal(3, ((SequenceOfValue)Value(c, "l")).Items.Length);
    }

    [Fact]
    public void QuadrupleDenotesCharacter()
    {
        var c = CompileClean(Module("sigma BMPString ::= {0, 0, 3, 163}"));
        Assert.Equal("Σ", ((StringValue)Value(c, "sigma")).Value);
    }

    [Fact]
    public void InvalidCharacterForRepertoireIsReported() =>
        Compile(Module("n NumericString ::= \"12a\"")).AssertDiagnostic("ASN2014");

    [Fact]
    public void ConstraintsResolveValueReferencesAndSerialApplication()
    {
        var c = CompileClean(Module("max INTEGER ::= 100\nA ::= INTEGER (0..max)\nB ::= A (10..20, ...)"));
        var b = c.Type("B");
        Assert.Equal(2, b.Constraints.Length);
        Assert.Equal("(0..100)", b.Constraints[0].ToString());
        Assert.True(b.Constraints[1].IsExtensible);
        Assert.Equal(ConstraintCheck.InRoot, ConstraintEvaluator.CheckSerial(b.Constraints, ConstraintSubject.FromInteger(15)));
        Assert.Equal(ConstraintCheck.OutsideExtensible, ConstraintEvaluator.CheckSerial(b.Constraints, ConstraintSubject.FromInteger(50)));
        Assert.Equal(ConstraintCheck.Violation, ConstraintEvaluator.CheckSerial(b.Constraints, ConstraintSubject.FromInteger(500)));
    }

    [Fact]
    public void SizeAndPermittedAlphabetConstraints()
    {
        var c = CompileClean(Module("S ::= IA5String (SIZE (1..4)) (FROM (\"A\"..\"F\" | \"x\"))"));
        var s = c.Type("S");
        Assert.Equal(ConstraintCheck.InRoot, ConstraintEvaluator.CheckSerial(s.Constraints, ConstraintSubject.FromString("ABx")));
        Assert.Equal(ConstraintCheck.Violation, ConstraintEvaluator.CheckSerial(s.Constraints, ConstraintSubject.FromString("ABG")));
        Assert.Equal(ConstraintCheck.Violation, ConstraintEvaluator.CheckSerial(s.Constraints, ConstraintSubject.FromString("ABCDE")));
    }

    [Fact]
    public void ContainedSubtypesInlineConstraints()
    {
        var c = CompileClean(Module("Small ::= INTEGER (1..5)\nBig ::= INTEGER (Small | 100)"));
        var big = c.Type("Big");
        Assert.True(ConstraintEvaluator.Satisfies(big.Constraints[0], ConstraintSubject.FromInteger(3)));
        Assert.True(ConstraintEvaluator.Satisfies(big.Constraints[0], ConstraintSubject.FromInteger(100)));
        Assert.False(ConstraintEvaluator.Satisfies(big.Constraints[0], ConstraintSubject.FromInteger(50)));
        Assert.Equal(IntegerSet.Range(1, 5).Union(IntegerSet.Single(100)), ConstraintSets.IntegerRoot(big.Constraints[0]));
    }

    [Fact]
    public void WithComponentsConstraintIsChecked()
    {
        var c = CompileClean(Module("S ::= SEQUENCE { a INTEGER OPTIONAL, b BOOLEAN OPTIONAL }\nT ::= S (WITH COMPONENTS { ..., a PRESENT })"));
        var t = c.Type("T");
        var present = ConstraintSubject.FromStructure(n => n == "a" ? ConstraintSubject.FromInteger(1) : null);
        var absent = ConstraintSubject.FromStructure(_ => null);
        Assert.Equal(ConstraintCheck.InRoot, ConstraintEvaluator.CheckSerial(t.Constraints, present));
        Assert.Equal(ConstraintCheck.Violation, ConstraintEvaluator.CheckSerial(t.Constraints, absent));
    }

    [Fact]
    public void InapplicableConstraintsAreReported()
    {
        Compile(Module("T ::= INTEGER (SIZE (1..2))")).AssertDiagnostic("ASN3001");
        Compile(Module("T ::= OCTET STRING (FROM (\"a\"))")).AssertDiagnostic("ASN3001");
        Compile(Module("T ::= BOOLEAN (1..2)")).AssertDiagnostic("ASN3001");
    }

    [Fact]
    public void EmptyRangeIsReported() =>
        Compile(Module("T ::= INTEGER (10..1)")).AssertDiagnostic("ASN3002");

    [Fact]
    public void DefaultValuesAreResolvedAndChecked()
    {
        var c = CompileClean(Module("S ::= SEQUENCE { a INTEGER (0..10) DEFAULT 5, e ENUMERATED { x, y } DEFAULT y }"));
        Assert.Equal("5", c.Type("S").Component("a").DefaultValue!.ToString());
        Assert.Equal("y", c.Type("S").Component("e").DefaultValue!.ToString());
        Compile(Module("S ::= SEQUENCE { a INTEGER (0..10) DEFAULT 50 }")).AssertDiagnostic("ASN2019");
    }

    [Fact]
    public void NamedBitDefaultIsPaddedToMinimumSize()
    {
        var c = CompileClean(Module("E ::= BIT STRING { app(0), enrol(1) } (SIZE (8))\nS ::= SEQUENCE { e E DEFAULT { app } }"));
        Assert.Equal(8, ((BitStringValue)c.Type("S").Component("e").DefaultValue!).Value.Length);
    }

    [Fact]
    public void PatternConstraintIsValidated()
    {
        var c = CompileClean(Module("Digits ::= IA5String (PATTERN \"[0-9]#3\")"));
        var t = c.Type("Digits");
        Assert.True(ConstraintEvaluator.Satisfies(t.Constraints[0], ConstraintSubject.FromString("123")));
        Assert.False(ConstraintEvaluator.Satisfies(t.Constraints[0], ConstraintSubject.FromString("12")));
    }

    [Fact]
    public void InformationObjectSetsAndTableConstraints()
    {
        var text = Module("""
            ITEM ::= CLASS { &id INTEGER UNIQUE, &Value } WITH SYNTAX { ID &id TYPE &Value }
            Items ITEM ::= { { ID 1 TYPE BOOLEAN } | { ID 2 TYPE IA5String }, ... }
            Message ::= SEQUENCE { id ITEM.&id ({Items}), value ITEM.&Value ({Items}{@id}) }
            """);
        var c = CompileClean(text);
        var value = c.Type("Message").Component("value").Type;
        Assert.Equal(TypeKind.OpenType, value.Kind);
        Assert.NotNull(value.TableConstraint);
        Assert.Equal(2, value.TableConstraint!.ObjectSet.Objects.Length);
        Assert.Equal(["id"], value.TableConstraint.RelatedPath);
        Assert.Equal(TypeKind.Integer, c.Type("Message").Component("id").Type.Kind);
        var obj = value.TableConstraint.ObjectSet.Objects[1];
        Assert.Equal(TypeKind.CharacterString, obj["&Value"]!.Type!.Kind);
    }
}
