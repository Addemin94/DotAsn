using Asn1.Diagnostics;
using Asn1.Semantics;

namespace Asn1.Cli;

/// <summary>Collects ASN.1 inputs from files and directories (recursively).</summary>
internal static class Inputs
{
    private static readonly string[] s_extensions = [".asn", ".asn1", ".ASN", ".ASN1"];

    public static (List<SourceText> Sources, List<Diagnostic> Diagnostics) Read(IEnumerable<string> paths, long maxFileSize)
    {
        var sources = new List<SourceText>();
        var diagnostics = new List<Diagnostic>();
        var files = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Where(f => s_extensions.Contains(Path.GetExtension(f))))
                {
                    files.Add(f);
                }
            }
            else if (File.Exists(path))
            {
                files.Add(path);
            }
            else
            {
                diagnostics.Add(Diagnostic.Create(DiagnosticCatalog.FileReadError, SourceLocation.None, path, "no such file or directory"));
            }
        }

        foreach (var f in files)
        {
            try
            {
                var info = new FileInfo(f);
                if (info.Length > maxFileSize)
                {
                    diagnostics.Add(Diagnostic.Create(DiagnosticCatalog.ResourceLimitExceeded, SourceLocation.None, $"'{f}' is {info.Length} bytes; the limit is {maxFileSize}"));
                    continue;
                }

                sources.Add(new SourceText(Path.GetRelativePath(Directory.GetCurrentDirectory(), f), File.ReadAllText(f)));
            }
            catch (IOException e)
            {
                diagnostics.Add(Diagnostic.Create(DiagnosticCatalog.FileReadError, SourceLocation.None, f, e.Message));
            }
            catch (UnauthorizedAccessException e)
            {
                diagnostics.Add(Diagnostic.Create(DiagnosticCatalog.FileReadError, SourceLocation.None, f, e.Message));
            }
        }

        if (sources.Count == 0 && diagnostics.Count == 0)
        {
            diagnostics.Add(Diagnostic.Create(DiagnosticCatalog.NoInputs, SourceLocation.None, string.Empty));
        }

        return (sources, diagnostics);
    }

    public static Asn1Compilation? Compile(IEnumerable<string> paths, long maxFileSize, List<Diagnostic> diagnostics)
    {
        var (sources, inputDiagnostics) = Read(paths, maxFileSize);
        diagnostics.AddRange(inputDiagnostics);
        return sources.Count == 0 ? null : Asn1Compilation.Create(sources);
    }
}
