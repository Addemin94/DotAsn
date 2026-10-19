using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;
using Asn1.CodeGen.CSharp;
using Asn1.Diagnostics;
using Asn1.Runtime;
using Asn1.Semantics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Asn1.CodeGenTests;

/// <summary>Generates C# for specifications, compiles it with Roslyn and loads it.</summary>
public sealed class GeneratedAssembly
{
    private static readonly ConcurrentDictionary<string, GeneratedAssembly> s_cache = new();

    public static string Root { get; } = typeof(GeneratedAssembly).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == "RepositoryRoot").Value!;

    private GeneratedAssembly(Assembly assembly, CSharpGenerationResult generation, Asn1Compilation compilation)
    {
        Assembly = assembly;
        Generation = generation;
        Compilation = compilation;
    }

    public Assembly Assembly { get; }

    public CSharpGenerationResult Generation { get; }

    public Asn1Compilation Compilation { get; }

    public static GeneratedAssembly For(IReadOnlyList<string> specs, CSharpGeneratorOptions? options = null, string extraSource = "") =>
        s_cache.GetOrAdd(string.Join("|", specs) + "|" + options + "|" + extraSource.GetHashCode(StringComparison.Ordinal), _ => Build(specs, options, extraSource));

    private static GeneratedAssembly Build(IReadOnlyList<string> specs, CSharpGeneratorOptions? options, string extraSource)
    {
        var compilation = Asn1Compilation.Create(specs.Select(s => new SourceText(s, File.ReadAllText(Path.Combine(Root, s)))));
        var generation = CSharpGenerator.Generate(compilation, options);
        var trees = generation.Files.Select(f => CSharpSyntaxTree.ParseText(f.Content, new CSharpParseOptions(LanguageVersion.CSharp14), f.Path)).ToList();
        if (extraSource.Length > 0)
        {
            trees.Add(CSharpSyntaxTree.ParseText(extraSource, new CSharpParseOptions(LanguageVersion.CSharp14), "Extra.cs"));
        }

        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(p => MetadataReference.CreateFromFile(p))
            .Append(MetadataReference.CreateFromFile(typeof(Asn1Type).Assembly.Location))
            .Append(MetadataReference.CreateFromFile(typeof(Asn1Tag).Assembly.Location))
            .Append(MetadataReference.CreateFromFile(typeof(Asn1.Runtime.Ber.DerEncodingRule).Assembly.Location));
        var csharp = CSharpCompilation.Create(
            "Generated" + Guid.NewGuid().ToString("N"),
            trees,
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable, warningLevel: 9999));
        using var ms = new MemoryStream();
        var emit = csharp.Emit(ms);
        var problems = emit.Diagnostics.Where(d => d.Severity >= Microsoft.CodeAnalysis.DiagnosticSeverity.Warning).ToList();
        if (problems.Count > 0)
        {
            var first = problems.Take(20).Select(d => d.ToString());
            throw new InvalidOperationException("Generated code does not compile cleanly:\n" + string.Join("\n", first));
        }

        ms.Position = 0;
        var assembly = new AssemblyLoadContext(null, isCollectible: false).LoadFromStream(ms);
        return new GeneratedAssembly(assembly, generation, compilation);
    }

    /// <summary>Gets the descriptor of a type assignment from the generated module class.</summary>
    public Asn1Type Descriptor(string qualifiedType)
    {
        var dot = qualifiedType.IndexOf('.', StringComparison.Ordinal);
        var module = qualifiedType[..dot];
        var name = qualifiedType[(dot + 1)..];
        var moduleClass = Assembly.GetTypes().Single(t => t.Name == Naming.Pascal(module) + "Asn1" && t.IsAbstract && t.IsSealed);
        var property = moduleClass.GetProperty(Naming.Pascal(name), BindingFlags.Public | BindingFlags.Static) ?? throw new InvalidOperationException($"no descriptor for {qualifiedType}");
        return (Asn1Type)property.GetValue(null)!;
    }
}
