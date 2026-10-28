using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Asn1.CodeGen.CSharp;
using Asn1.CodeGen.TypeScript;
using Asn1.Diagnostics;
using Asn1.Dynamic;
using Asn1.Linter;
using Asn1.Runtime;
using Asn1.Runtime.Ber;
using Asn1.Runtime.Per;
using Asn1.Semantics;
using Asn1.Syntax;

namespace Asn1.Studio.Engine;

/// <summary>A diagnostic prepared for display.</summary>
/// <param name="Severity">Severity.</param>
/// <param name="Id">Diagnostic ID, e.g. ASN2001.</param>
/// <param name="Line">One-based line (0 without a location).</param>
/// <param name="Column">One-based column (0 without a location).</param>
/// <param name="Message">Message.</param>
/// <param name="From">Start offset in the schema text (UTF-16 code units), or -1 without a location.</param>
/// <param name="To">Exclusive end offset in the schema text, or -1 without a location.</param>
public sealed record DiagnosticView(DiagnosticSeverity Severity, string Id, int Line, int Column, string Message, int From = -1, int To = -1);

/// <summary>The result of compiling (and optionally linting) a schema.</summary>
public sealed record CompileResult(IReadOnlyList<DiagnosticView> Diagnostics, IReadOnlyList<string> Types, TimeSpan Elapsed)
{
    /// <summary>The number of errors.</summary>
    public int Errors => Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error);

    /// <summary>True if the schema compiled without errors.</summary>
    public bool Success => Errors == 0;
}

/// <summary>The encoding of a value with one rule, or the reason it failed.</summary>
public sealed record RuleOutput(string Rule, bool IsText, byte[]? Bytes, long? BitLength, string? Error)
{
    /// <summary>The encoding as text (JER, XER), or null for binary rules.</summary>
    public string? Text => IsText && Bytes is not null ? Encoding.UTF8.GetString(Bytes) : null;
}

/// <summary>The result of decoding.</summary>
/// <param name="Json">The decoded value in DotAsn JSON value notation.</param>
/// <param name="BytesConsumed">Octets consumed by the value.</param>
/// <param name="TotalBytes">Octets in the input.</param>
/// <param name="Error">The error, if decoding failed.</param>
/// <param name="Violations">Constraint violations of a decoded value.</param>
/// <summary>A generated file.</summary>
/// <param name="Path">File name.</param>
/// <param name="Content">Text.</param>
public sealed record GeneratedFile(string Path, string Content);

/// <summary>The result of decoding.</summary>
public sealed record DecodeOutput(string? Json, int BytesConsumed, int TotalBytes, string? Error, IReadOnlyList<string> Violations);

/// <summary>How encoded input is written.</summary>
public enum InputFormat
{
    /// <summary>Hexadecimal digits; white space, ':', '-', ',' and "0x" prefixes are ignored.</summary>
    Hex,

    /// <summary>Base64.</summary>
    Base64,

    /// <summary>UTF-8 text (JER, XER).</summary>
    Text,
}

/// <summary>
/// The tool logic behind DotAsn Studio: compiles a schema once and then encodes, decodes, validates, compares and
/// inspects values of its types, using the same compiler, runtime and generator as the <c>asn1</c> CLI.
/// </summary>
public sealed class Workbench
{
    private Asn1Compilation? _compilation;
    private DynamicTypeFactory _factory = new();

    /// <summary>All implemented encoding rules, in display order.</summary>
    public static IReadOnlyList<Asn1EncodingRule> Rules => Asn1EncodingRules.All;

    /// <summary>The type names of the last successful compilation (Module.Type).</summary>
    public IReadOnlyList<string> Types { get; private set; } = [];

    /// <summary>True when a schema compiled without errors.</summary>
    public bool HasSchema => _compilation is not null;

    /// <summary>
    /// Highlighting of ASN.1 text from the DotAsn lexer (<see cref="SyntaxClassifier"/>), as flat (start, length,
    /// class) triples for the editor; the class is the <see cref="SyntaxClass"/> value.
    /// </summary>
    public static int[] Highlight(string text)
    {
        var spans = SyntaxClassifier.Classify(text);
        var result = new int[spans.Count * 3];
        for (var i = 0; i < spans.Count; i++)
        {
            result[i * 3] = spans[i].Start;
            result[(i * 3) + 1] = spans[i].Length;
            result[(i * 3) + 2] = (int)spans[i].Class;
        }

        return result;
    }

    /// <summary>Compiles a schema; on success its types become available to the other tools.</summary>
    public CompileResult Compile(string text, bool lint = true, IEnumerable<string>? lintEncodingRules = null)
    {
        var watch = Stopwatch.StartNew();
        var compilation = Asn1Compilation.FromText(text, "schema.asn");
        var diagnostics = compilation.Diagnostics.ToList();
        if (lint && !compilation.Diagnostics.Any(d => d.IsError))
        {
            var configuration = new LintConfiguration { EncodingRules = new HashSet<string>(lintEncodingRules ?? [], StringComparer.OrdinalIgnoreCase) };
            diagnostics.AddRange(Asn1Linter.Lint(compilation, configuration));
        }

        var views = diagnostics
            .Select(d => d.Location.IsNone
                ? new DiagnosticView(d.Severity, d.Descriptor.Id, 0, 0, d.Message)
                : new DiagnosticView(d.Severity, d.Descriptor.Id, d.Location.Start.Line, d.Location.Start.Column, d.Message, d.Location.Span.Start, d.Location.Span.End))
            .Distinct()
            .OrderBy(d => d.Line).ThenBy(d => d.Column).ThenBy(d => d.Id, StringComparer.Ordinal)
            .ToList();
        var types = compilation.TypeAssignments.Where(t => !t.IsParameterized && t.Type is not null).Select(t => t.QualifiedName).ToList();
        var result = new CompileResult(views, types, watch.Elapsed);
        if (result.Success)
        {
            _compilation = compilation;
            _factory = new DynamicTypeFactory();
            Types = types;
        }

        return result;
    }

    /// <summary>The ASN.1 source text of a type assignment.</summary>
    public string Definition(string type)
    {
        var location = Assignment(type).Syntax.Location;
        return location.Source.GetText(location.Span);
    }

    /// <summary>A summary of the runtime descriptor: kind, tags, constraints and components.</summary>
    public string Describe(string type)
    {
        var d = Descriptor(type);
        var sb = new StringBuilder();
        sb.Append("Kind:        ").Append(d.Kind).Append('\n');
        sb.Append("Tags:        ").Append(d.Tags.IsDefaultOrEmpty ? "(none: open type or untagged CHOICE)" : string.Join(", ", d.Tags)).Append('\n');
        sb.Append("Constraints: ").Append(d.Constraints.IsDefaultOrEmpty ? "(none)" : string.Join(" ", d.Constraints.Select(c => "(" + c + ")"))).Append('\n');
        if (d is IStructuredTypeInfo s)
        {
            sb.Append(s.IsExtensible ? "Extensible:  yes\n" : "Extensible:  no\n");
            sb.Append("Components:\n");
            foreach (var c in s.ComponentInfos)
            {
                sb.Append("  ").Append(c.Name).Append(' ').Append(c.Type.Name)
                    .Append(c.Presence == Asn1Presence.Required ? string.Empty : " " + c.Presence.ToString().ToUpperInvariant())
                    .Append(c.IsExtensionAddition ? " (extension addition)" : string.Empty).Append('\n');
            }
        }
        else if (d is ICollectionTypeInfo c)
        {
            sb.Append("Element:     ").Append(c.ElementName is null ? string.Empty : c.ElementName + " ").Append(c.ElementTypeInfo.Name).Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>The whole resolved model as text (tags, constraints and parameters resolved).</summary>
    public string Model() => _compilation is null ? string.Empty : ModelPrinter.Print(_compilation);

    /// <summary>A random value of a type in JSON value notation (the same generator the test vectors use).</summary>
    public string Example(string type, int seed)
    {
        var value = new RandomValueGenerator(new Random(seed)).Generate(Assignment(type).Type!);
        return DynamicJson.Serialize(Descriptor(type), value);
    }

    /// <summary>Parses a JSON value and returns its constraint violations (empty when valid).</summary>
    public IReadOnlyList<string> Validate(string type, string json)
    {
        var d = Descriptor(type);
        return [.. d.ValidateBoxed(DynamicJson.Parse(d, json)).Select(e => e.ToString())];
    }

    /// <summary>Encodes a JSON value with one rule.</summary>
    public RuleOutput Encode(string type, string json, string rule)
    {
        var r = Rule(rule);
        var d = Descriptor(type);
        try
        {
            var value = DynamicJson.Parse(d, json);
            if (r is PerEncodingRule per)
            {
                var bytes = per.Encode(d, value, out var bits);
                return new RuleOutput(r.Name, false, bytes, bits, null);
            }

            return new RuleOutput(r.Name, !r.IsBinary, r.Encode(d, value), null, null);
        }
        catch (Exception e) when (IsUserError(e))
        {
            return new RuleOutput(r.Name, !r.IsBinary, null, null, e.Message);
        }
    }

    /// <summary>Encodes a JSON value with every rule (for comparing sizes and forms).</summary>
    public IReadOnlyList<RuleOutput> EncodeAll(string type, string json) => [.. Rules.Select(r => Encode(type, json, r.Name))];

    /// <summary>
    /// Decodes an encoding into JSON value notation. Constraint violations do not stop decoding (the structure is
    /// usually what one is debugging); they are reported in <see cref="DecodeOutput.Violations"/>.
    /// </summary>
    public DecodeOutput Decode(string type, string rule, byte[] data, bool allowTrailingData = false)
    {
        var r = Rule(rule);
        var d = Descriptor(type);
        try
        {
            var value = r.Decode(d, data, new Asn1DecodingOptions { RejectTrailingData = !allowTrailingData, ValidateConstraints = false }, out var consumed);
            var violations = d.ValidateBoxed(value).Select(e => e.ToString()).ToList();
            return new DecodeOutput(DynamicJson.Serialize(d, value), consumed, data.Length, null, violations);
        }
        catch (Exception e) when (IsUserError(e))
        {
            return new DecodeOutput(null, 0, data.Length, e.Message, []);
        }
    }

    /// <summary>Decodes with one rule and re-encodes the value with another.</summary>
    public RuleOutput Transcode(string type, string fromRule, byte[] data, string toRule)
    {
        var decoded = Decode(type, fromRule, data);
        return decoded.Json is null
            ? new RuleOutput(Rule(toRule).Name, !Rule(toRule).IsBinary, null, null, $"cannot decode the input with {Rule(fromRule).Name}: {decoded.Error}")
            : Encode(type, decoded.Json, toRule);
    }

    /// <summary>A schema-less dump of BER/DER/CER TLVs.</summary>
    public static string Inspect(byte[] data)
    {
        try
        {
            return TlvInspector.Dump(data);
        }
        catch (Exception e) when (IsUserError(e))
        {
            return "error: " + e.Message;
        }
    }

    /// <summary>
    /// The TypeScript files that <c>asn1 generate-typescript</c> writes for the schema (types and descriptors per module,
    /// and dotasn-schema.ts for @dotasn/wasm), with the generator's diagnostics.
    /// </summary>
    public (IReadOnlyList<GeneratedFile> Files, IReadOnlyList<string> Diagnostics) GenerateTypeScript()
    {
        if (_compilation is null)
        {
            return ([], []);
        }

        // No lambdas here: their cached delegates would live in the class's shared closure type, which would then
        // reference the lazy-loaded generator assembly and fail to load before it is (see Asn1.Studio.csproj).
        var result = TypeScriptGenerator.Generate(_compilation);
        var files = new List<GeneratedFile>();
        foreach (var f in result.Files)
        {
            files.Add(new GeneratedFile(f.Path, f.Content));
        }

        var diagnostics = new List<string>();
        foreach (var d in result.Diagnostics)
        {
            diagnostics.Add($"{d.Descriptor.Id}: {d.Message}");
        }

        return (files, diagnostics);
    }

    /// <summary>The DotAsn schema (JSON) that the WebAssembly runtime (@dotasn/wasm) loads, as <c>asn1 generate-schema</c> writes it.</summary>
    public string GenerateSchema(bool indented = false) =>
        _compilation is null ? string.Empty : Dynamic.Asn1SchemaWriter.Write(_compilation, indented: indented);

    /// <summary>The C# the generator produces for the schema, all files concatenated.</summary>
    public string GenerateCSharp(string? rootNamespace)
    {
        if (_compilation is null)
        {
            return string.Empty;
        }

        var result = CSharpGenerator.Generate(_compilation, new CSharpGeneratorOptions { Namespace = string.IsNullOrWhiteSpace(rootNamespace) ? null : rootNamespace.Trim() });
        var sb = new StringBuilder();
        foreach (var d in result.Diagnostics)
        {
            sb.Append("// ").Append(d.Descriptor.Id).Append(": ").Append(d.Message).Append('\n');
        }

        foreach (var f in result.Files)
        {
            if (result.Files.Length > 1)
            {
                sb.Append("// ---- ").Append(f.Path).Append(" ----\n");
            }

            sb.Append(f.Content);
        }

        return sb.ToString();
    }

    /// <summary>Parses encoded input written as hexadecimal, Base64 or text.</summary>
    /// <exception cref="FormatException">The input is not valid in the given format.</exception>
    public static byte[] ParseInput(string text, InputFormat format)
    {
        switch (format)
        {
            case InputFormat.Text:
                return Encoding.UTF8.GetBytes(text);
            case InputFormat.Base64:
                return Convert.FromBase64String(string.Concat(text.Where(c => !char.IsWhiteSpace(c))));
            default:
                {
                    var sb = new StringBuilder(text.Length);
                    for (var i = 0; i < text.Length; i++)
                    {
                        var c = text[i];
                        if (c == '0' && i + 1 < text.Length && text[i + 1] is 'x' or 'X')
                        {
                            i++;
                        }
                        else if (char.IsAsciiHexDigit(c))
                        {
                            sb.Append(c);
                        }
                        else if (!char.IsWhiteSpace(c) && c is not (':' or '-' or ','))
                        {
                            throw new FormatException($"'{c}' is not a hexadecimal digit");
                        }
                    }

                    if (sb.Length % 2 != 0)
                    {
                        throw new FormatException("an odd number of hexadecimal digits");
                    }

                    return Convert.FromHexString(sb.ToString());
                }
        }
    }

    /// <summary>Hexadecimal with a space between octets and 16 octets per line.</summary>
    public static string FormatHex(ReadOnlySpan<byte> bytes)
    {
        var sb = new StringBuilder(bytes.Length * 3);
        for (var i = 0; i < bytes.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(i % 16 == 0 ? '\n' : ' ');
            }

            sb.Append(bytes[i].ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }

    /// <summary>Errors caused by the user's input (as opposed to bugs), reported as messages.</summary>
    public static bool IsUserError(Exception e) =>
        e is Asn1Exception or Asn1JsonException or JsonException or FormatException or NotSupportedException or ArgumentException or OverflowException;

    private static Asn1EncodingRule Rule(string name) =>
        Asn1EncodingRules.TryGet(name, out var rule) ? rule : throw new ArgumentException($"unknown encoding rule '{name}'");

    private TypeAssignmentSymbol Assignment(string type) =>
        (_compilation ?? throw new InvalidOperationException("compile a schema first")).FindType(type)
        ?? throw new ArgumentException($"type '{type}' not found");

    private Asn1Type<object?> Descriptor(string type) => _factory.Create(Assignment(type));
}
