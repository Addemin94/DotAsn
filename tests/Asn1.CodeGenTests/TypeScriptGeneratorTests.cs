using Asn1.CodeGen.TypeScript;
using Asn1.Diagnostics;
using Asn1.Semantics;
using Xunit;

namespace Asn1.CodeGenTests;

/// <summary>
/// Tests of the TypeScript generator's output. The generated code is type-checked with tsc and run against the
/// asn1tools vectors by the runtime's own tests (js/runtime, <c>npm run generate &amp;&amp; npm run typecheck &amp;&amp; npm test</c>).
/// </summary>
public class TypeScriptGeneratorTests
{
    private static readonly string[] s_specs =
    [
        "samples/telemetry/telemetry.asn",
        "samples/basic/basic.asn",
        "samples/recursive-types/tree.asn",
        "samples/constraints/constraints.asn",
        "samples/extensibility/extensibility.asn",
        "samples/protocol-messages/sensor-protocol.asn",
        "test-vectors/corpus/asn1tools/all_types.asn",
        "test-vectors/corpus/asn1tools/information_object.asn",
        "test-vectors/corpus/asn1tools/parameterization.asn",
        "test-vectors/corpus/asn1tools/x683.asn",
        "test-vectors/corpus/asn1tools/zforce.asn",
        "test-vectors/corpus/asn1tools/time_types.asn",
        "test-vectors/corpus/dotasn/primitives.asn",
        "test-vectors/corpus/ietf/rfc5280.asn",
    ];

    public static TheoryData<string> Specs() => [.. s_specs];

    private static Asn1Compilation Compile(params (string Name, string Text)[] sources) =>
        Asn1Compilation.Create(sources.Select(s => new SourceText(s.Name, s.Text)));

    private static TypeScriptGenerationResult Generate(string asn, TypeScriptGeneratorOptions? options = null) =>
        TypeScriptGenerator.Generate(Compile(("test.asn", asn)), options);

    private static string Single(TypeScriptGenerationResult result) => Assert.Single(result.Files, f => f.Path != TypeScriptGenerator.SchemaFileName).Content;

    [Theory]
    [MemberData(nameof(Specs))]
    public void GeneratesEveryModuleDeterministically(string spec)
    {
        var compilation = Asn1Compilation.Create([new SourceText(spec, File.ReadAllText(Path.Combine(GeneratedAssembly.Root, spec)))]);
        var first = TypeScriptGenerator.Generate(compilation);
        var second = TypeScriptGenerator.Generate(compilation);

        var modules = first.Files.Where(f => f.Path != TypeScriptGenerator.SchemaFileName).ToList();
        Assert.Equal(compilation.Modules.Length, modules.Count);
        Assert.Single(first.Files, f => f.Path == TypeScriptGenerator.SchemaFileName);
        Assert.Equal(first.Files.Length, first.Files.Select(f => f.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(first.Files.Select(f => f.Content), second.Files.Select(f => f.Content));
        Assert.All(first.Files, f =>
        {
            Assert.EndsWith(".ts", f.Path, StringComparison.Ordinal);
            Assert.DoesNotContain("\r", f.Content, StringComparison.Ordinal);
        });
        Assert.All(modules, f => Assert.Contains("import * as $ from '@dotasn/runtime';", f.Content, StringComparison.Ordinal));
        Assert.DoesNotContain(first.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void SequencesBecomeInterfacesWithDescriptors()
    {
        var ts = Single(Generate("""
            M DEFINITIONS AUTOMATIC TAGS ::= BEGIN
              Point ::= SEQUENCE { x INTEGER (0..255), y INTEGER, label UTF8String OPTIONAL, n BOOLEAN DEFAULT TRUE, ... }
            END
            """));

        Assert.Contains("export interface Point {", ts, StringComparison.Ordinal);
        Assert.Contains("  x: number;", ts, StringComparison.Ordinal);
        Assert.Contains("  y: bigint;", ts, StringComparison.Ordinal);
        Assert.Contains("  label?: string;", ts, StringComparison.Ordinal);
        Assert.Contains("  n?: boolean;", ts, StringComparison.Ordinal);
        Assert.Contains("export const Point: $.Asn1Type<Point> = {", ts, StringComparison.Ordinal);
        Assert.Contains("name: 'M.Point'", ts, StringComparison.Ordinal);

        // Descriptors describe the shape of the values; encoding facts live in the schema.
        Assert.Contains("{ name: 'label', type: () => ({ kind: 'string', name: 'UTF8String' }), optional: true }", ts, StringComparison.Ordinal);
        Assert.DoesNotContain("tags", ts, StringComparison.Ordinal);
    }

    [Fact]
    public void IntegersAreBigintUnlessTheirRangeIsSafe()
    {
        var ts = Single(Generate("""
            M DEFINITIONS ::= BEGIN
              Small ::= INTEGER (0..255)
              Natural ::= INTEGER (0..MAX)
              Ext ::= INTEGER (1..10, ...)
            END
            """));

        Assert.Contains("export type Small = number;", ts, StringComparison.Ordinal);
        Assert.Contains("export type Natural = bigint;", ts, StringComparison.Ordinal);
        Assert.Contains("export type Ext = bigint;", ts, StringComparison.Ordinal);
        Assert.Contains("big: false", ts, StringComparison.Ordinal);
    }

    [Fact]
    public void ChoicesAndEnumerationsBecomeUnions()
    {
        var ts = Single(Generate("""
            M DEFINITIONS AUTOMATIC TAGS ::= BEGIN
              Color ::= ENUMERATED { red, green, ... }
              Shape ::= CHOICE { circle INTEGER, square BOOLEAN, ... }
              Fixed ::= ENUMERATED { a, b }
            END
            """));

        Assert.Contains("export type Color = 'red' | 'green' | $.UnknownEnumItem;", ts, StringComparison.Ordinal);
        Assert.Contains("export type Fixed = 'a' | 'b';", ts, StringComparison.Ordinal);
        Assert.Contains("{ circle: bigint }", ts, StringComparison.Ordinal);
        Assert.Contains("{ square: boolean }", ts, StringComparison.Ordinal);
        Assert.Contains("$.UnknownAlternative", ts, StringComparison.Ordinal);
    }

    [Fact]
    public void ImportsUseTheConfiguredRuntimeModuleAndExtension()
    {
        var compilation = Compile(
            ("a.asn", "A DEFINITIONS ::= BEGIN IMPORTS B-Type FROM B; Wrapper ::= SEQUENCE { b B-Type } END"),
            ("b.asn", "B DEFINITIONS ::= BEGIN B-Type ::= INTEGER (0..7) END"));

        var defaults = TypeScriptGenerator.Generate(compilation).Files.Single(f => f.Path == "A.ts").Content;
        Assert.Contains("import { BType } from './B.js';", defaults, StringComparison.Ordinal);

        var custom = TypeScriptGenerator.Generate(compilation, new TypeScriptGeneratorOptions { RuntimeModule = "./runtime/index.js", ImportExtension = "" })
            .Files.Single(f => f.Path == "A.ts").Content;
        Assert.Contains("import * as $ from './runtime/index.js';", custom, StringComparison.Ordinal);
        Assert.Contains("import { BType } from './B';", custom, StringComparison.Ordinal);
    }

    [Fact]
    public void ModulesOptionSelectsModules()
    {
        var compilation = Compile(
            ("a.asn", "A DEFINITIONS ::= BEGIN X ::= BOOLEAN END"),
            ("b.asn", "B DEFINITIONS ::= BEGIN Y ::= BOOLEAN END"));

        var result = TypeScriptGenerator.Generate(compilation, new TypeScriptGeneratorOptions { Modules = new HashSet<string> { "B" } });

        Assert.Equal(["B.ts", TypeScriptGenerator.SchemaFileName], result.Files.Select(f => f.Path));
    }

    [Fact]
    public void ReservedWordsAreRenamedWithADiagnostic()
    {
        var result = Generate("""
            M DEFINITIONS ::= BEGIN
              T ::= INTEGER
              delete T ::= 5
            END
            """);

        var ts = Single(result);
        Assert.DoesNotContain("export const delete ", ts, StringComparison.Ordinal);
        Assert.Contains(result.Diagnostics, d => d.Descriptor == DiagnosticCatalog.IdentifierRenamed && d.Message.Contains("'delete'", StringComparison.Ordinal));
    }

    [Fact]
    public void UnsupportedTypesAreReportedAndSkipped()
    {
        var result = Generate("""
            M DEFINITIONS ::= BEGIN
              Ext ::= EXTERNAL
              Ok ::= BOOLEAN
            END
            """);

        var ts = Single(result);
        Assert.Contains("export const Ok:", ts, StringComparison.Ordinal);
        Assert.DoesNotContain("export const Ext:", ts, StringComparison.Ordinal);
        Assert.Contains(result.Diagnostics, d => d.Descriptor == DiagnosticCatalog.GenerationUnsupported);
    }

    [Fact]
    public void DocumentationCanBeDisabled()
    {
        const string asn = "M DEFINITIONS ::= BEGIN -- a flag\n Flag ::= BOOLEAN END";

        Assert.Contains("/**", Single(Generate(asn)), StringComparison.Ordinal);
        Assert.DoesNotContain("/**", Single(Generate(asn, new TypeScriptGeneratorOptions { GenerateDocumentation = false })), StringComparison.Ordinal);
    }

    [Fact]
    public void EmitsTheSchemaForTheWebAssemblyRuntime()
    {
        const string asn = "M DEFINITIONS ::= BEGIN Flag ::= BOOLEAN Small ::= INTEGER (0..7) END";
        var file = Generate(asn).Files.Single(f => f.Path == TypeScriptGenerator.SchemaFileName).Content;
        Assert.Contains("export const schema: string = '", file, StringComparison.Ordinal);

        // The literal holds a loadable schema with every type of the generated modules.
        var literal = file[(file.IndexOf("= '", StringComparison.Ordinal) + 3)..file.LastIndexOf("';", StringComparison.Ordinal)];
        var schema = Asn1.Dynamic.Asn1Schema.Parse(literal.Replace("\\'", "'", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal));
        Assert.Equal(["M.Flag", "M.Small"], schema.TypeNames.Order(StringComparer.Ordinal));

        Assert.DoesNotContain(Generate(asn, new TypeScriptGeneratorOptions { GenerateSchema = false }).Files, f => f.Path == TypeScriptGenerator.SchemaFileName);
    }
}
