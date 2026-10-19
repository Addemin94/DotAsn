using System.Reflection;
using System.Text.Json.Nodes;
using Asn1.Diagnostics;
using Asn1.Dynamic;
using Asn1.Runtime;
using Asn1.Semantics;
using Xunit;

namespace Asn1.FuzzTests;

/// <summary>
/// Mutation fuzzing: valid encodings from the vector corpus are mutated (bit flips, byte changes, insertions,
/// deletions, truncation, length-field inflation) and decoded with every rule. Decoding must either succeed or throw
/// an <see cref="Asn1Exception"/>, and must stay within a bounded allocation budget.
/// </summary>
public class MutationFuzzTests
{
    private static readonly string s_root = typeof(MutationFuzzTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == "RepositoryRoot").Value!;

    public static TheoryData<string, string> Inputs()
    {
        var data = new TheoryData<string, string>();
        foreach (var codec in new[] { "der", "uper", "per", "oer", "jer", "xer" })
        {
            foreach (var name in new[] { "telemetry", "asn1tools_x691_a3", "derived_zforce_not_implied" })
            {
                data.Add(codec, name);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Inputs))]
    public void MutatedEncodingsFailSafely(string codec, string name)
    {
        var json = JsonNode.Parse(File.ReadAllText(Path.Combine(s_root, "test-vectors", codec, name + ".json")))!;
        var specs = json["specs"]!.AsArray().Select(s => s!.GetValue<string>()).ToList();
        var compilation = Asn1Compilation.Create(specs.Select(s => new SourceText(s, File.ReadAllText(Path.Combine(s_root, s)))));
        var factory = new DynamicTypeFactory();
        var random = new Random(name.Length * 31 + codec.Length);
        Asn1EncodingRules.TryGet(codec == "per" ? "aper" : codec, out var primary);
        Asn1EncodingRule[] rules = codec switch
        {
            "oer" => [primary, Asn1EncodingRules.Coer, Asn1EncodingRules.Ber],
            "xer" => [primary, Asn1EncodingRules.Cxer, Asn1EncodingRules.Ber],
            _ => [primary, Asn1EncodingRules.Ber],
        };
        var options = new Asn1DecodingOptions { MaxTotalAllocation = 4 * 1024 * 1024, MaxCollectionCount = 10_000 };
        var cases = json["cases"]!.AsArray().Take(60).ToList();
        foreach (var c in cases)
        {
            var type = factory.Create(compilation.FindType(c!["type"]!.GetValue<string>())!);
            var original = Convert.FromHexString(c["hex"]!.GetValue<string>());
            for (var i = 0; i < 150; i++)
            {
                var mutated = Mutate(original, random);
                foreach (var rule in rules)
                {
                    var before = GC.GetAllocatedBytesForCurrentThread();
                    try
                    {
                        rule.Decode(type, mutated, options);
                    }
                    catch (Asn1Exception)
                    {
                    }

                    var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                    Assert.True(allocated < 32 * 1024 * 1024, $"decoding {mutated.Length} bytes allocated {allocated} bytes ({rule.Name}, {Convert.ToHexString(mutated)})");
                }
            }
        }
    }

    private static byte[] Mutate(byte[] input, Random random)
    {
        var data = input.ToList();
        var operations = random.Next(1, 4);
        for (var op = 0; op < operations; op++)
        {
            switch (random.Next(7))
            {
                case 0 when data.Count > 0:
                    {
                        var i = random.Next(data.Count);
                        data[i] ^= (byte)(1 << random.Next(8));
                        break;
                    }

                case 1 when data.Count > 0:
                    data[random.Next(data.Count)] = (byte)random.Next(256);
                    break;
                case 2:
                    data.Insert(random.Next(data.Count + 1), (byte)random.Next(256));
                    break;
                case 3 when data.Count > 0:
                    data.RemoveAt(random.Next(data.Count));
                    break;
                case 4 when data.Count > 0:
                    data.RemoveRange(random.Next(data.Count), data.Count - random.Next(data.Count) > 0 ? 1 : 0);
                    break;
                case 5 when data.Count > 1:
                    data = data.Take(random.Next(1, data.Count)).ToList();
                    break;
                case 6 when data.Count > 0:
                    // Inflate a byte that might be a length: 0x84 followed by large lengths, or 0xFF.
                    data[random.Next(data.Count)] = random.Next(2) == 0 ? (byte)0x84 : (byte)0xFF;
                    break;
            }
        }

        return [.. data];
    }

    [Fact]
    public void RandomAsn1SourceNeverCrashesTheCompiler()
    {
        var random = new Random(4242);
        var fragments = new[]
        {
            "M", "DEFINITIONS", "AUTOMATIC", "TAGS", "::=", "BEGIN", "END", "SEQUENCE", "{", "}", "(", ")", "[", "]", "[[", "]]", "a", "B",
            "INTEGER", "OF", "SIZE", "..", "...", ",", "0", "1", "-5", "CHOICE", "OPTIONAL", "DEFAULT", "IMPORTS", "FROM", ";", "CLASS", "&id",
            "WITH", "COMPONENTS", "\"x\"", "'01'B", "ENUMERATED", "|", "^", "EXCEPT", "ALL", "<", "MIN", "MAX", "UNIQUE", "@", ".", "B{", "T", "::=",
        };
        for (var i = 0; i < 3000; i++)
        {
            var text = "M DEFINITIONS ::= BEGIN " + string.Join(' ', Enumerable.Range(0, random.Next(1, 60)).Select(_ => fragments[random.Next(fragments.Length)])) + " END";
            var compilation = Asn1Compilation.Create([new SourceText("fuzz.asn", text)]);
            Assert.DoesNotContain(compilation.Diagnostics, d => d.Id == "ASN0001");
        }
    }

    [Fact]
    public void PathologicalSpecificationsAreBounded()
    {
        // Exponential parameterized instantiation must hit the instantiation limit, not exhaust memory.
        var text = "M DEFINITIONS ::= BEGIN\n" + string.Concat(Enumerable.Range(0, 30).Select(i => $"T{i}{{X}} ::= SEQUENCE {{ a T{i + 1}{{X}}, b T{i + 1}{{BOOLEAN}} }}\n")) + "T30{X} ::= X\nR ::= T0{INTEGER}\nEND";
        var compilation = Asn1Compilation.Create([new SourceText("p.asn", text)], new Asn1CompilerOptions { MaxInstantiationDepth = 24, MaxTypeNodes = 200_000 });
        Assert.Contains(compilation.Diagnostics, d => d.Id is "ASN0003" or "ASN2008");
        Assert.DoesNotContain(compilation.Diagnostics, d => d.Id == "ASN0001");
    }
}
