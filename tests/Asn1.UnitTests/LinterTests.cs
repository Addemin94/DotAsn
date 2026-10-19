using Asn1.Diagnostics;
using Asn1.Linter;
using Asn1.Semantics;
using Xunit;

namespace Asn1.UnitTests;

public class LinterTests
{
    private static IReadOnlyList<Diagnostic> Lint(string body, LintConfiguration? config = null, string header = "M DEFINITIONS AUTOMATIC TAGS ::=") =>
        Asn1Linter.Lint(Asn1Compilation.FromText(header + " BEGIN\n" + body + "\nEND"), config);

    [Fact]
    public void NamingConventions()
    {
        // Initial letter case is enforced by the grammar; the naming rules govern hyphenation and word style.
        Assert.DoesNotContain(Lint("Good-Name ::= SEQUENCE { good-field BOOLEAN }"), x => x.Id is "ASN5001" or "ASN5002");
        var strict = Lint("Good-Name ::= SEQUENCE { good-field BOOLEAN }", new LintConfiguration { TypeNaming = NamingStyle.Camel, IdentifierNaming = NamingStyle.Camel });
        Assert.Contains(strict, x => x.Id == "ASN5001" && x.Message.Contains("Good-Name"));
        Assert.Contains(strict, x => x.Id == "ASN5002" && x.Message.Contains("good-field"));
    }

    [Fact]
    public void UnusedImportsAndDefinitionsWithExplicitExports()
    {
        var a = "A DEFINITIONS ::= BEGIN X ::= INTEGER Y ::= BOOLEAN END";
        var b = "B DEFINITIONS ::= BEGIN EXPORTS T; IMPORTS X, Y FROM A; T ::= SEQUENCE { x X } Unused ::= NULL END";
        var d = Asn1Linter.Lint(Asn1Compilation.Create([new SourceText("a.asn", a), new SourceText("b.asn", b)]));
        Assert.Contains(d, x => x.Id == "ASN5004" && x.Message.Contains("'Y'"));
        Assert.Contains(d, x => x.Id == "ASN5003" && x.Message.Contains("Unused"));
        Assert.DoesNotContain(d, x => x.Id == "ASN5003" && x.Message.Contains("'T'"));
    }

    [Fact]
    public void RedundantAliasesAndConfusingNames()
    {
        var d = Lint("A ::= INTEGER\nB ::= A\nS ::= SEQUENCE { fooBar INTEGER, foo-bar BOOLEAN }");
        Assert.Contains(d, x => x.Id == "ASN5005");
        Assert.Contains(d, x => x.Id == "ASN5006");
    }

    [Fact]
    public void PerRulesOnlyApplyWhenPerIsTargeted()
    {
        const string body = "E ::= ENUMERATED { a, b }\nI ::= SEQUENCE { n INTEGER }\nB ::= OCTET STRING (SIZE (0..100000))";
        Assert.DoesNotContain(Lint(body), x => x.Id is "ASN5011" or "ASN5012" or "ASN5013");
        var per = Lint(body, new LintConfiguration { EncodingRules = new HashSet<string> { "uper" } });
        Assert.Contains(per, x => x.Id == "ASN5011");
        Assert.Contains(per, x => x.Id == "ASN5012");
        Assert.Contains(per, x => x.Id == "ASN5013");
        Assert.DoesNotContain(Lint("B ::= OCTET STRING (SIZE (0..65535))", new LintConfiguration { EncodingRules = new HashSet<string> { "uper" } }), x => x.Id == "ASN5013");
    }

    [Fact]
    public void SeverityOverridesAndDisabledRules()
    {
        var config = LintConfiguration.Load("""{ "rules": { "ASN5005": "error", "ASN5009": "warning", "ASN5002": "off" } }""");
        var d = Lint("A ::= INTEGER\nB ::= A\nS ::= SEQUENCE { Bad INTEGER }", config);
        Assert.Contains(d, x => x.Id == "ASN5005" && x.Severity == DiagnosticSeverity.Error);
        Assert.Contains(d, x => x.Id == "ASN5009" && x.Severity == DiagnosticSeverity.Warning);
        Assert.DoesNotContain(d, x => x.Id == "ASN5002");
    }

    [Fact]
    public void InlineSuppressions()
    {
        var d = Lint("""
            -- asn1-lint-disable-next-line ASN5005
            B ::= A
            C ::= A -- asn1-lint-disable ASN5005
            D ::= A
            A ::= INTEGER
            """);
        var aliases = d.Where(x => x.Id == "ASN5005").ToList();
        Assert.Single(aliases);
        Assert.Contains("'D'", aliases[0].Message);
    }

    [Fact]
    public void ErrorsCannotBeSuppressedOrDowngraded()
    {
        var config = LintConfiguration.Load("""{ "rules": { "ASN2001": "off" } }""");
        var d = Lint("-- asn1-lint-disable-next-line\nA ::= Missing", config);
        Assert.Contains(d, x => x.Id == "ASN2001" && x.IsError);
    }

    [Fact]
    public void LintingWorksOnIncompleteSpecifications()
    {
        var d = Lint("A ::= SEQUENCE { a Missing, b BOOLEAN }\nB ::= A", new LintConfiguration { TypeNaming = NamingStyle.Camel });
        Assert.Contains(d, x => x.Id == "ASN2001");
        Assert.Contains(d, x => x.Id == "ASN5005");
    }

    [Fact]
    public void MandatoryExtensionAdditionsAreSuspicious() =>
        Assert.Contains(Lint("S ::= SEQUENCE { a INTEGER, ..., b BOOLEAN }"), x => x.Id == "ASN5010");
}
