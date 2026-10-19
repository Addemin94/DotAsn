#:project ../src/Asn1.Dynamic/Asn1.Dynamic.csproj
// Generates random, constraint-satisfying values for ASN.1 types and writes a case file
// (test-vectors/cases/<name>.json) consumed by asn1tools_vectors.py.
//
// usage: dotnet run scripts/gen-cases.cs -- <name> <count-per-type> <seed> <spec.asn>... [--types A,B,C] [--no-special-reals] [--no-extensions]
using System.Text.Json;
using Asn1.Diagnostics;
using Asn1.Dynamic;
using Asn1.Runtime;
using Asn1.Semantics;

var name = args[0];
var count = int.Parse(args[1]);
var seed = int.Parse(args[2]);
var specs = args.Skip(3).TakeWhile(a => !a.StartsWith("--")).ToList();
var typeFilter = args.SkipWhile(a => a != "--types").Skip(1).FirstOrDefault()?.Split(',').ToHashSet();
var noSpecialReals = args.Contains("--no-special-reals");
var noExtensions = args.Contains("--no-extensions");
var root = Directory.GetCurrentDirectory();

var compilation = Asn1Compilation.Create(specs.Select(s => new SourceText(s, File.ReadAllText(s))));
foreach (var d in compilation.Diagnostics.Where(d => d.IsError))
{
    Console.Error.WriteLine(d);
}

var factory = new DynamicTypeFactory();
var random = new Random(seed);
var generator = new RandomValueGenerator(random, new RandomValueOptions { IncludeSpecialReals = !noSpecialReals, ExtensionProbability = noExtensions ? 0 : 0.3 });
using var ms = new MemoryStream();
using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
{
    w.WriteStartObject();
    w.WriteStartArray("specs");
    foreach (var s in specs)
    {
        w.WriteStringValue(Path.GetRelativePath(root, Path.GetFullPath(s)).Replace('\\', '/'));
    }

    w.WriteEndArray();
    w.WriteNumber("seed", seed);
    w.WriteStartArray("cases");
    var total = 0;
    foreach (var module in compilation.Modules)
    {
        foreach (var ta in module.TypeAssignments)
        {
            if (ta.Type is null || (typeFilter is not null && !typeFilter.Contains(ta.Name) && !typeFilter.Contains(ta.QualifiedName)))
            {
                continue;
            }

            Asn1Type<object?> descriptor;
            try
            {
                descriptor = factory.Create(ta);
            }
            catch (Asn1UnsupportedException)
            {
                continue;
            }

            var seen = new HashSet<string>();
            for (var i = 0; i < count * 3 && seen.Count < count; i++)
            {
                object? value;
                try
                {
                    value = generator.Generate(ta.Type);
                }
                catch (NotSupportedException)
                {
                    break;
                }

                if (Asn1Validator.ValidateValue(descriptor, value).Count > 0)
                {
                    continue;
                }

                var json = DynamicJson.Serialize(descriptor, value, indented: false);
                if (!seen.Add(json))
                {
                    continue;
                }

                w.WriteStartObject();
                w.WriteString("type", ta.QualifiedName);
                w.WritePropertyName("value");
                w.WriteRawValue(json);
                w.WriteEndObject();
                total++;
            }
        }
    }

    w.WriteEndArray();
    w.WriteEndObject();
    Console.Error.WriteLine($"{name}: {total} case(s)");
}

Directory.CreateDirectory(Path.Combine(root, "test-vectors", "cases"));
File.WriteAllBytes(Path.Combine(root, "test-vectors", "cases", name + ".json"), ms.ToArray());
