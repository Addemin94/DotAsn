using System.Reflection;

namespace Asn1.Studio.Engine;

/// <summary>A sample specification shipped with DotAsn.</summary>
/// <param name="Name">The path below <c>samples/</c>.</param>
/// <param name="Text">The ASN.1 text.</param>
public sealed record Sample(string Name, string Text);

/// <summary>The specifications in the repository's <c>samples/</c> directory, embedded at build time.</summary>
public static class Samples
{
    private static readonly Lazy<IReadOnlyList<Sample>> s_all = new(Load);

    /// <summary>All samples, ordered by name.</summary>
    public static IReadOnlyList<Sample> All => s_all.Value;

    private static IReadOnlyList<Sample> Load()
    {
        var assembly = typeof(Samples).Assembly;
        return [.. assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith("samples/", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .Select(n => new Sample(n["samples/".Length..], Read(assembly, n)))];
    }

    private static string Read(Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
