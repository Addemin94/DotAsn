using Asn1.Semantics;
using Xunit;
using static Asn1.SemanticTests.TestHelpers;

namespace Asn1.SemanticTests;

public class TaggingTests
{
    [Fact]
    public void AutomaticTagsNumberRootComponentsBeforeExtensionAdditions()
    {
        var c = CompileClean(Module("S ::= SEQUENCE { a INTEGER, ..., x BOOLEAN, [[ y BOOLEAN, z NULL ]], ..., b INTEGER }"));
        var s = c.Type("S");
        Assert.Equal("[0]", s.Component("a").Type.Tags());
        Assert.Equal("[1]", s.Component("b").Type.Tags());
        Assert.Equal("[2]", s.Component("x").Type.Tags());
        Assert.Equal("[3]", s.Component("y").Type.Tags());
        Assert.Equal("[4]", s.Component("z").Type.Tags());
        Assert.Equal(0, s.Component("x").ExtensionAdditionIndex);
        Assert.Equal(1, s.Component("y").ExtensionAdditionIndex);
        Assert.Equal(1, s.Component("z").ExtensionAdditionIndex);
        Assert.True(s.Component("b").IsInRootPart2);
    }

    [Fact]
    public void AutomaticTaggingIsDisabledWhenAnyComponentIsTagged()
    {
        var c = CompileClean(Module("S ::= SEQUENCE { a INTEGER, b [5] BOOLEAN }"));
        var s = c.Type("S");
        Assert.Equal("[UNIVERSAL 2]", s.Component("a").Type.Tags());
        Assert.Equal("[5]", s.Component("b").Type.Tags());
    }

    [Fact]
    public void AutomaticTagsAreExplicitForUntaggedChoice()
    {
        var c = CompileClean(Module("S ::= SEQUENCE { a INTEGER, b C }\nC ::= CHOICE { x INTEGER, y BOOLEAN }"));
        var b = c.Type("S").Component("b").Type;
        Assert.Single(b.Tags);
        Assert.True(b.Tags[0].IsExplicit);
    }

    [Fact]
    public void ExplicitTagsWrapAndImplicitTagsReplace()
    {
        var c = CompileClean(Module("A ::= [3] INTEGER\nB ::= [APPLICATION 4] IMPLICIT A\nD ::= [PRIVATE 5] EXPLICIT A", "EXPLICIT TAGS"));
        Assert.Equal("[3] EXPLICIT [UNIVERSAL 2]", c.Type("A").Tags());
        Assert.Equal("[APPLICATION 4] EXPLICIT [UNIVERSAL 2]", c.Type("B").Tags());
        Assert.Equal("[PRIVATE 5] EXPLICIT [3] EXPLICIT [UNIVERSAL 2]", c.Type("D").Tags());
    }

    [Fact]
    public void ImplicitTagDefaultReplacesOutermostTag()
    {
        var c = CompileClean(Module("A ::= [3] INTEGER\nB ::= [4] A", "IMPLICIT TAGS"));
        Assert.Equal("[3]", c.Type("A").Tags());
        Assert.Equal("[4]", c.Type("B").Tags());
    }

    [Fact]
    public void ImplicitTagOnUntaggedChoiceIsReportedAndTreatedAsExplicit()
    {
        var c = Compile(Module("C ::= CHOICE { a INTEGER, b BOOLEAN }\nT ::= [1] IMPLICIT C", "EXPLICIT TAGS"));
        c.AssertDiagnostic("ASN2021");
        Assert.True(c.Type("T").Tags[0].IsExplicit);
    }

    [Fact]
    public void DuplicateTagsInSetAreRejected()
    {
        var c = Compile(Module("S ::= SET { a INTEGER, b INTEGER }", "EXPLICIT TAGS"));
        c.AssertDiagnostic("ASN2010");
    }

    [Fact]
    public void ChoiceAlternativesMustHaveDistinctTagsIncludingNestedChoices()
    {
        var c = Compile(Module("C ::= CHOICE { a INTEGER, b CHOICE { x BOOLEAN, y INTEGER } }", "EXPLICIT TAGS"));
        c.AssertDiagnostic("ASN2010");
    }

    [Fact]
    public void OptionalComponentMustBeDistinguishableFromFollowingComponent()
    {
        var c = Compile(Module("S ::= SEQUENCE { a INTEGER OPTIONAL, b INTEGER }", "EXPLICIT TAGS"));
        c.AssertDiagnostic("ASN2010");
        CompileClean(Module("S ::= SEQUENCE { a INTEGER, b INTEGER }", "EXPLICIT TAGS"));
    }

    [Fact]
    public void ComponentsOfCopiesRootComponentsAndRetagsAutomatically()
    {
        var c = CompileClean(Module("A ::= SEQUENCE { x INTEGER, ..., e BOOLEAN }\nB ::= SEQUENCE { y BOOLEAN, COMPONENTS OF A, z NULL }"));
        var b = c.Type("B");
        Assert.Equal(["y", "x", "z"], b.Components.Select(x => x.Name));
        Assert.Equal("[1]", b.Component("x").Type.Tags());
        Assert.True(b.Component("x").FromComponentsOf);
    }
}
