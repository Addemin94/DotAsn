using Asn1.Runtime;

namespace Asn1.Benchmarks;

/// <summary>Sanity checks run in [GlobalSetup], so that a benchmark never measures a wrong result.</summary>
internal static class BenchmarkChecks
{
    public static Asn1EncodingRule Rule(string name) =>
        Asn1EncodingRules.TryGet(name, out var rule) ? rule : throw new ArgumentException($"Unknown encoding rule '{name}'", nameof(name));

    public static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
