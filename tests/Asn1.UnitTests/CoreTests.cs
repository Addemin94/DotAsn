using System.Numerics;
using System.Text.Json;
using Asn1.Constraints;
using Asn1.Diagnostics;
using Xunit;

namespace Asn1.UnitTests;

public class CoreTests
{
    [Fact]
    public void BitStringsKeepExactLengthAndZeroUnusedBits()
    {
        var b = new Asn1BitString([0xFF], 3);
        Assert.Equal(3, b.Length);
        Assert.Equal(0xE0, b.Bytes[0]);
        Assert.Equal(5, b.UnusedBits);
        Assert.Equal("111", b.ToBinaryString());
        Assert.Equal(Asn1BitString.FromBinary("111"), b);
        Assert.Equal("1", Asn1BitString.FromBinary("1000").TrimTrailingZeros().ToBinaryString());
        Assert.Equal("10100", Asn1BitString.FromSetBits([0, 2], 5).ToBinaryString());
        Assert.Throws<ArgumentOutOfRangeException>(() => new Asn1BitString([0xFF], 9));
    }

    [Theory]
    [InlineData("1.2.840.113549", true)]
    [InlineData("2.999.3", true)]
    [InlineData("0.39", true)]
    [InlineData("0.40", false)]
    [InlineData("3.1", false)]
    [InlineData("1", false)]
    [InlineData("1.02", false)]
    [InlineData("1..2", false)]
    public void ObjectIdentifierParsingFollowsX660(string text, bool valid) =>
        Assert.Equal(valid, ObjectIdentifier.TryParse(text, out _, out _));

    [Fact]
    public void ObjectIdentifiersSupportHugeArcs()
    {
        var oid = ObjectIdentifier.Parse("2.25.329800735698586629295641978511506172918");
        Assert.Equal(BigInteger.Parse("329800735698586629295641978511506172918", System.Globalization.CultureInfo.InvariantCulture), oid.Arcs[2]);
    }

    [Fact]
    public void IntegerSetAlgebra()
    {
        var a = IntegerSet.Range(0, 10).Union(IntegerSet.Range(20, 30));
        Assert.True(a.Contains(25));
        Assert.False(a.Contains(15));
        Assert.Equal("0..10 | 20..30", a.ToString());
        Assert.Equal(IntegerSet.Range(5, 10).Union(IntegerSet.Range(20, 25)), a.Intersect(IntegerSet.Range(5, 25)));
        Assert.Equal("MIN..-1 | 11..19 | 31..MAX", a.Complement().ToString());
        Assert.Equal(IntegerSet.Range(0, 30), IntegerSet.Range(0, 10).Union(IntegerSet.Range(11, 30)));
        Assert.True(IntegerSet.Range(5, 1).IsEmpty);
    }

    [Theory]
    [InlineData(CharacterStringKind.Numeric, "12 3", true)]
    [InlineData(CharacterStringKind.Numeric, "12a", false)]
    [InlineData(CharacterStringKind.Printable, "Hello, World?", true)]
    [InlineData(CharacterStringKind.Printable, "a@b", false)]
    [InlineData(CharacterStringKind.IA5, "\u007F", true)]
    [InlineData(CharacterStringKind.IA5, "é", false)]
    [InlineData(CharacterStringKind.Bmp, "Ωж中", true)]
    [InlineData(CharacterStringKind.Bmp, "𝄞", false)]
    [InlineData(CharacterStringKind.Universal, "𝄞", true)]
    public void CharacterRepertoires(CharacterStringKind kind, string value, bool valid) =>
        Assert.Equal(valid, CharacterRepertoire.Validate(kind, value) is null);

    [Fact]
    public void CharacterCountsUseCodePoints() => Assert.Equal(3, CharacterRepertoire.CountCharacters("a𝄞b"));

    [Theory]
    [InlineData("[0-9]#3", "123", true)]
    [InlineData("[0-9]#3", "12", false)]
    [InlineData("a#(2,3)", "aaa", true)]
    [InlineData("a#(2,3)", "aaaa", false)]
    public void Asn1PatternsTranslate(string pattern, string value, bool match) =>
        Assert.Equal(match, ConstraintEvaluator.MatchesPattern(pattern, value));

    [Fact]
    public void ContainedSubtypesContributeOnlyTheirRoot()
    {
        // X.680 52.4: B ::= INTEGER (A) where A ::= INTEGER (0..10, ..., 12) is constrained to 0..10 and not extensible.
        var a = new Asn1Constraint(new ValueRangeElement((BigInteger)0, (BigInteger)10), true, new SingleValueElement((BigInteger)12));
        var b = new Asn1Constraint(new NestedElement(a, isContainedSubtype: true));
        Assert.Equal(ConstraintCheck.Violation, ConstraintEvaluator.Check(b, ConstraintSubject.FromInteger(12)));
        Assert.Equal(ConstraintCheck.InAdditions, ConstraintEvaluator.Check(a, ConstraintSubject.FromInteger(12)));
    }

    [Fact]
    public void DiagnosticsSerializeToValidJsonAndSarif()
    {
        var source = new SourceText("x.asn", "A ::= B\n");
        var d = Diagnostic.Create(DiagnosticCatalog.UndefinedReference, new SourceLocation(source, new TextSpan(6, 1)), "B", string.Empty);
        using var json = JsonDocument.Parse(DiagnosticFormatter.ToJson([d]));
        var item = json.RootElement.GetProperty("diagnostics")[0];
        Assert.Equal("ASN2001", item.GetProperty("id").GetString());
        Assert.Equal(1, item.GetProperty("location").GetProperty("startLine").GetInt32());
        Assert.Equal(7, item.GetProperty("location").GetProperty("startColumn").GetInt32());
        using var sarif = JsonDocument.Parse(DiagnosticFormatter.ToSarif([d], "1.0"));
        Assert.Equal("2.1.0", sarif.RootElement.GetProperty("version").GetString());
        var result = sarif.RootElement.GetProperty("runs")[0].GetProperty("results")[0];
        Assert.Equal("error", result.GetProperty("level").GetString());
    }

    [Fact]
    public void DiagnosticIdsAreUniqueAndStable()
    {
        var ids = DiagnosticCatalog.All.Select(d => d.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.All(ids, id => Assert.Matches("^ASN[0-9]{4}$", id));
        Assert.Equal("ASN2001", DiagnosticCatalog.UndefinedReference.Id);
    }
}
