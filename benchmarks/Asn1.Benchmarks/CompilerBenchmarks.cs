using System.Collections.Immutable;
using Asn1.CodeGen.CSharp;
using Asn1.Diagnostics;
using Asn1.Linter;
using Asn1.Semantics;
using BenchmarkDotNet.Attributes;

namespace Asn1.Benchmarks;

/// <summary>The compiler pipeline: parsing and binding, linting and C# generation.</summary>
[MemoryDiagnoser]
public class CompilerBenchmarks
{
    private SourceText[] _sources = [];
    private Asn1Compilation _compilation = null!;

    /// <summary>
    /// The module: the telemetry sample (small), RFC 5280 (X.509, 34 KB, imports and information objects) and
    /// zforce (45 KB, many types).
    /// </summary>
    [Params("telemetry", "rfc5280", "zforce")]
    public string Module { get; set; } = "";

    [GlobalSetup]
    public void Setup()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "schemas", Module + ".asn");
        _sources = [new SourceText(path, File.ReadAllText(path))];
        _compilation = Compile();
        BenchmarkChecks.Require(!_compilation.HasErrors, $"{Module} does not compile");
        BenchmarkChecks.Require(!GenerateCSharp().Files.IsEmpty, $"{Module} generates no C#");
    }

    [Benchmark]
    public Asn1Compilation Compile() => Asn1Compilation.Create(_sources);

    [Benchmark]
    public ImmutableArray<Diagnostic> Lint() => Asn1Linter.Lint(_compilation);

    [Benchmark]
    public CSharpGenerationResult GenerateCSharp() => CSharpGenerator.Generate(_compilation);
}
