using System.CommandLine;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Asn1.CodeGen.CSharp;
using Asn1.Diagnostics;
using Asn1.Dynamic;
using Asn1.Linter;
using Asn1.Runtime;
using Asn1.Runtime.Ber;
using Asn1.Semantics;

namespace Asn1.Cli;

/// <summary>Entry point of the <c>asn1</c> tool.</summary>
internal static class Program
{
    private static readonly Option<bool> s_verbose = new("--verbose", "-v") { Description = "Print additional information (source excerpts, timings)." };
    private static readonly Option<bool> s_quiet = new("--quiet", "-q") { Description = "Only print errors." };
    private static readonly Option<string?> s_config = new("--config") { Description = "Configuration file (default: asn1.json in the current or a parent directory)." };
    private static readonly Option<long> s_maxFileSize = new("--max-file-size") { Description = "Maximum size of an input file in bytes.", DefaultValueFactory = _ => 64L * 1024 * 1024 };

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        var root = new RootCommand("DotAsn ASN.1 toolchain: lint, compile, generate C# and encode/decode messages.");
        root.Options.Add(s_verbose);
        root.Options.Add(s_quiet);
        root.Options.Add(s_config);
        root.Options.Add(s_maxFileSize);
        foreach (var o in root.Options)
        {
            o.Recursive = true;
        }

        root.Subcommands.Add(LintCommand());
        root.Subcommands.Add(CheckCommand());
        root.Subcommands.Add(GenerateCommand());
        root.Subcommands.Add(EncodeCommand());
        root.Subcommands.Add(DecodeCommand());
        root.Subcommands.Add(ValidateCommand());
        root.Subcommands.Add(InspectCommand());
        root.Subcommands.Add(new Command("standards", "Print the standards support matrix.") { Action = new SyncAction(_ => Standards()) });
        root.Subcommands.Add(new Command("version", "Print the tool version.") { Action = new SyncAction(_ => VersionCommand()) });
        try
        {
            return await root.Parse(args).InvokeAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await Console.Error.WriteLineAsync("cancelled").ConfigureAwait(false);
            return ExitCodes.Cancelled;
        }
    }

    // ------------------------------------------------------------------ shared options

    private static Argument<string[]> Paths() => new("paths") { Description = "ASN.1 files or directories (searched recursively for *.asn, *.asn1).", Arity = ArgumentArity.OneOrMore };

    private static Option<string> FormatOption() => new("--format", "-f")
    {
        Description = "Diagnostic output format: text, json or sarif.",
        DefaultValueFactory = _ => "text",
    };

    private static DiagnosticOutputFormat ParseFormat(string format) => format.ToLowerInvariant() switch
    {
        "json" => DiagnosticOutputFormat.Json,
        "sarif" => DiagnosticOutputFormat.Sarif,
        "text" => DiagnosticOutputFormat.Text,
        _ => throw new UsageException($"unknown diagnostic format '{format}' (expected text, json or sarif)"),
    };

    private static int Report(ParseResult p, IReadOnlyList<Diagnostic> diagnostics, DiagnosticOutputFormat format, bool warningsAsErrors = false)
    {
        var quiet = p.GetValue(s_quiet);
        var shown = quiet ? diagnostics.Where(d => d.IsError).ToList() : diagnostics.ToList();
        var writer = format == DiagnosticOutputFormat.Text ? Console.Error : Console.Out;
        DiagnosticFormatter.Write(writer, shown, format, CSharpGenerator.Version, showSource: p.GetValue(s_verbose));
        if (format == DiagnosticOutputFormat.Text && !quiet)
        {
            Console.Error.WriteLine(DiagnosticFormatter.Summary(diagnostics));
        }

        var failed = diagnostics.Any(d => d.IsError) || (warningsAsErrors && diagnostics.Any(d => d.Severity == DiagnosticSeverity.Warning));
        return failed ? ExitCodes.Errors : ExitCodes.Success;
    }

    private static Asn1Compilation? Compile(ParseResult p, string[] paths, List<Diagnostic> diagnostics) =>
        Inputs.Compile(paths, p.GetValue(s_maxFileSize), diagnostics);

    private static CliConfiguration Configuration(ParseResult p) => CliConfiguration.Load(p.GetValue(s_config));

    private static Command Wrap(Command command, Func<ParseResult, CancellationToken, Task<int>> action)
    {
        command.SetAction(async (p, ct) =>
        {
            try
            {
                return await action(p, ct).ConfigureAwait(false);
            }
            catch (UsageException e)
            {
                await Console.Error.WriteLineAsync("error: " + e.Message).ConfigureAwait(false);
                return ExitCodes.Usage;
            }
            catch (Asn1UnsupportedException e)
            {
                await Console.Error.WriteLineAsync("unsupported: " + e.Message).ConfigureAwait(false);
                return ExitCodes.Unsupported;
            }
            catch (Asn1Exception e)
            {
                await Console.Error.WriteLineAsync("error: " + e.Message).ConfigureAwait(false);
                return ExitCodes.Errors;
            }
            catch (Asn1JsonException e)
            {
                await Console.Error.WriteLineAsync("error: invalid value: " + e.Message).ConfigureAwait(false);
                return ExitCodes.Errors;
            }
            catch (JsonException e)
            {
                await Console.Error.WriteLineAsync("error: invalid JSON: " + e.Message).ConfigureAwait(false);
                return ExitCodes.Errors;
            }
            catch (IOException e)
            {
                await Console.Error.WriteLineAsync("error: " + e.Message).ConfigureAwait(false);
                return ExitCodes.Errors;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                await Console.Error.WriteLineAsync("internal error: " + e).ConfigureAwait(false);
                return ExitCodes.Internal;
            }
        });
        return command;
    }

    // ------------------------------------------------------------------ lint / check

    private static Command LintCommand()
    {
        var paths = Paths();
        var format = FormatOption();
        var rules = new Option<string[]>("--encoding-rules") { Description = "Target encoding rules for interoperability checks (e.g. uper,der).", AllowMultipleArgumentsPerToken = true };
        var warnAsError = new Option<bool>("--warnings-as-errors") { Description = "Exit with code 1 when warnings are reported." };
        var cmd = new Command("lint", "Check ASN.1 definitions for standards violations, style issues and interoperability risks.") { paths, format, rules, warnAsError };
        return Wrap(cmd, (p, ct) =>
        {
            var diagnostics = new List<Diagnostic>();
            var compilation = Compile(p, p.GetValue(paths)!, diagnostics);
            var config = Configuration(p).Lint;
            var targets = p.GetValue(rules);
            if (targets is { Length: > 0 })
            {
                config = config with { EncodingRules = targets.SelectMany(t => t.Split(',', StringSplitOptions.RemoveEmptyEntries)).ToHashSet(StringComparer.OrdinalIgnoreCase) };
            }

            if (compilation is not null)
            {
                diagnostics.AddRange(Asn1Linter.Lint(compilation, config));
            }

            return Task.FromResult(Report(p, diagnostics, ParseFormat(p.GetValue(format)!), p.GetValue(warnAsError)));
        });
    }

    private static Command CheckCommand()
    {
        var paths = Paths();
        var format = FormatOption();
        var dump = new Option<bool>("--dump") { Description = "Print the resolved model (effective tags, constraints, extension metadata)." };
        var cmd = new Command("check", "Parse and semantically check ASN.1 definitions (no style rules).") { paths, format, dump };
        return Wrap(cmd, (p, ct) =>
        {
            var diagnostics = new List<Diagnostic>();
            var compilation = Compile(p, p.GetValue(paths)!, diagnostics);
            if (compilation is not null)
            {
                diagnostics.AddRange(compilation.Diagnostics);
                if (p.GetValue(dump))
                {
                    Console.Out.Write(ModelPrinter.Print(compilation));
                }
            }

            return Task.FromResult(Report(p, diagnostics, ParseFormat(p.GetValue(format)!)));
        });
    }

    // ------------------------------------------------------------------ generate-csharp

    private static Command GenerateCommand()
    {
        var paths = Paths();
        var output = new Option<string?>("--output", "-o") { Description = "Output directory (default: ./generated)." };
        var ns = new Option<string?>("--namespace", "-n") { Description = "Root namespace of the generated code." };
        var layout = new Option<string?>("--layout") { Description = "File layout: per-module (default) or per-type." };
        var integers = new Option<string?>("--integer-mapping") { Description = "Unbounded INTEGER mapping: precise (BigInteger, default) or int64." };
        var modules = new Option<string[]>("--modules") { Description = "Only generate these modules.", AllowMultipleArgumentsPerToken = true };
        var isInternal = new Option<bool>("--internal") { Description = "Generate internal instead of public types." };
        var noDocs = new Option<bool>("--no-docs") { Description = "Do not emit XML documentation from ASN.1 comments." };
        var cmd = new Command("generate-csharp", "Generate C# types and descriptors.") { paths, output, ns, layout, integers, modules, isInternal, noDocs };
        return Wrap(cmd, async (p, ct) =>
        {
            var diagnostics = new List<Diagnostic>();
            var compilation = Compile(p, p.GetValue(paths)!, diagnostics);
            if (compilation is null)
            {
                return Report(p, diagnostics, DiagnosticOutputFormat.Text);
            }

            diagnostics.AddRange(compilation.Diagnostics);
            if (diagnostics.Any(d => d.IsError))
            {
                return Report(p, diagnostics, DiagnosticOutputFormat.Text);
            }

            var cfg = Configuration(p).Generate;
            var options = new CSharpGeneratorOptions
            {
                Namespace = p.GetValue(ns) ?? cfg.Namespace,
                Layout = (p.GetValue(layout) ?? cfg.Layout ?? "per-module").ToLowerInvariant() switch
                {
                    "per-module" or "module" => OutputLayout.FilePerModule,
                    "per-type" or "type" => OutputLayout.FilePerType,
                    var other => throw new UsageException($"unknown layout '{other}'"),
                },
                IntegerMapping = (p.GetValue(integers) ?? cfg.IntegerMapping ?? "precise").ToLowerInvariant() switch
                {
                    "precise" or "biginteger" => IntegerMapping.Precise,
                    "int64" or "long" => IntegerMapping.Int64,
                    var other => throw new UsageException($"unknown integer mapping '{other}'"),
                },
                InternalTypes = p.GetValue(isInternal) || cfg.Internal,
                GenerateDocumentation = !p.GetValue(noDocs),
                Modules = p.GetValue(modules) is { Length: > 0 } m ? m.SelectMany(x => x.Split(',')).ToHashSet(StringComparer.Ordinal) : null,
            };
            var result = CSharpGenerator.Generate(compilation, options);
            diagnostics.AddRange(result.Diagnostics);
            var dir = p.GetValue(output) ?? cfg.Output ?? "generated";
            Directory.CreateDirectory(dir);
            foreach (var f in result.Files)
            {
                ct.ThrowIfCancellationRequested();
                var path = Path.Combine(dir, f.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, f.Content, ct).ConfigureAwait(false);
            }

            await File.WriteAllTextAsync(Path.Combine(dir, "asn1-generated.manifest.json"), result.Manifest, ct).ConfigureAwait(false);
            if (!p.GetValue(s_quiet))
            {
                Console.Out.WriteLine($"Generated {result.Files.Length} file(s) in {Path.GetFullPath(dir)}");
            }

            return Report(p, diagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning || p.GetValue(s_verbose)).ToList(), DiagnosticOutputFormat.Text);
        });
    }

    // ------------------------------------------------------------------ encode / decode / validate

    private static Option<string> TypeOption() => new("--type", "-t") { Description = "The type, as Module.Type (or Type when unique).", Required = true };

    private static Option<string> RuleOption() => new("--format", "-f") { Description = "Encoding rule: ber, der, cer, uper, aper, oer, coer, jer, xer, cxer.", Required = true };

    private static Option<string?> InputOption() => new("--input", "-i") { Description = "Input file ('-' or omitted: standard input)." };

    private static Option<string?> OutputOption() => new("--output", "-o") { Description = "Output file (default: standard output)." };

    private static Asn1EncodingRule Rule(string name)
    {
        if (Asn1EncodingRules.TryGet(name, out var rule))
        {
            return rule;
        }

        if (Asn1EncodingRules.NotImplemented.Contains(name.ToUpperInvariant()))
        {
            throw new Asn1UnsupportedException($"the {name.ToUpperInvariant()} encoding rules are not implemented yet (see 'asn1 standards')");
        }

        throw new UsageException($"unknown encoding rule '{name}' (expected one of: ber, der, cer, uper, aper, oer, coer, jer, xer, cxer)");
    }

    private static (Asn1Type<object?> Type, Asn1Compilation Compilation)? Schema(ParseResult p, string[] paths, string typeName)
    {
        var diagnostics = new List<Diagnostic>();
        var compilation = Compile(p, paths, diagnostics);
        if (compilation is not null)
        {
            diagnostics.AddRange(compilation.Diagnostics);
        }

        if (compilation is null || diagnostics.Any(d => d.IsError))
        {
            Report(p, diagnostics, DiagnosticOutputFormat.Text);
            return null;
        }

        var ta = compilation.FindType(typeName) ?? throw new UsageException($"type '{typeName}' was not found (use Module.Type when the name is ambiguous)");
        return (new DynamicTypeFactory().Create(ta), compilation);
    }

    private static async Task<byte[]> ReadInput(string? path, CancellationToken ct)
    {
        if (path is null or "-")
        {
            using var ms = new MemoryStream();
            await Console.OpenStandardInput().CopyToAsync(ms, ct).ConfigureAwait(false);
            return ms.ToArray();
        }

        return await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
    }

    private static byte[] DecodeText(byte[] raw, string format)
    {
        var text = Encoding.UTF8.GetString(raw);
        return format.ToLowerInvariant() switch
        {
            "binary" or "bin" => raw,
            "hex" => Convert.FromHexString(new string(text.Where(c => !char.IsWhiteSpace(c) && c != ':').ToArray())),
            "base64" => Convert.FromBase64String(text.Trim()),
            _ => throw new UsageException($"unknown input format '{format}' (expected hex, binary or base64)"),
        };
    }

    private static async Task WriteOutput(string? path, byte[] bytes, CancellationToken ct)
    {
        if (path is null or "-")
        {
            using var stdout = Console.OpenStandardOutput();
            await stdout.WriteAsync(bytes, ct).ConfigureAwait(false);
            return;
        }

        await File.WriteAllBytesAsync(path, bytes, ct).ConfigureAwait(false);
    }

    private static Command EncodeCommand()
    {
        var paths = Paths();
        var type = TypeOption();
        var rule = RuleOption();
        var input = InputOption();
        var value = new Option<string?>("--value") { Description = "The value as JSON (instead of --input)." };
        var output = OutputOption();
        var outputFormat = new Option<string?>("--output-format") { Description = "hex (default for binary rules), text (default for JER and XER), binary or base64." };
        var cmd = new Command("encode", "Encode a JSON value (DotAsn value notation) with an encoding rule.") { paths, type, rule, input, value, output, outputFormat };
        return Wrap(cmd, async (p, ct) =>
        {
            var encodingRule = Rule(p.GetValue(rule)!);
            if (Schema(p, p.GetValue(paths)!, p.GetValue(type)!) is not { } schema)
            {
                return ExitCodes.Errors;
            }

            var json = p.GetValue(value) ?? Encoding.UTF8.GetString(await ReadInput(p.GetValue(input), ct).ConfigureAwait(false));
            var v = DynamicJson.Parse(schema.Type, json);
            var bytes = encodingRule.Encode(schema.Type, v, new Asn1EncodingOptions { CancellationToken = ct });
            var formatted = (p.GetValue(outputFormat) ?? (encodingRule.IsBinary ? "hex" : "text")).ToLowerInvariant() switch
            {
                "binary" or "bin" => bytes,
                "text" when !encodingRule.IsBinary => [.. bytes, (byte)'\n'],
                "text" => throw new UsageException($"{encodingRule.Name} is a binary encoding; use hex, binary or base64"),
                "hex" => Encoding.ASCII.GetBytes(Convert.ToHexString(bytes) + "\n"),
                "base64" => Encoding.ASCII.GetBytes(Convert.ToBase64String(bytes) + "\n"),
                var other => throw new UsageException($"unknown output format '{other}'"),
            };
            await WriteOutput(p.GetValue(output), formatted, ct).ConfigureAwait(false);
            return ExitCodes.Success;
        });
    }

    private static Command DecodeCommand()
    {
        var paths = Paths();
        var type = TypeOption();
        var rule = RuleOption();
        var input = InputOption();
        var hex = new Option<string?>("--hex") { Description = "The encoding as hexadecimal text (instead of --input)." };
        var inputFormat = new Option<string>("--input-format") { Description = "binary (default for files), hex or base64.", DefaultValueFactory = _ => "binary" };
        var output = OutputOption();
        var cmd = new Command("decode", "Decode a message and print it as JSON (DotAsn value notation).") { paths, type, rule, input, hex, inputFormat, output };
        return Wrap(cmd, async (p, ct) =>
        {
            var encodingRule = Rule(p.GetValue(rule)!);
            if (Schema(p, p.GetValue(paths)!, p.GetValue(type)!) is not { } schema)
            {
                return ExitCodes.Errors;
            }

            var data = p.GetValue(hex) is { } h ? DecodeText(Encoding.UTF8.GetBytes(h), "hex") : DecodeText(await ReadInput(p.GetValue(input), ct).ConfigureAwait(false), p.GetValue(inputFormat)!);
            var value = encodingRule.Decode(schema.Type, data, new Asn1DecodingOptions { CancellationToken = ct });
            await WriteOutput(p.GetValue(output), Encoding.UTF8.GetBytes(DynamicJson.Serialize(schema.Type, value) + "\n"), ct).ConfigureAwait(false);
            return ExitCodes.Success;
        });
    }

    private static Command ValidateCommand()
    {
        var paths = Paths();
        var type = TypeOption();
        var rule = RuleOption();
        var input = InputOption();
        var hex = new Option<string?>("--hex") { Description = "The encoding as hexadecimal text (instead of --input)." };
        var inputFormat = new Option<string>("--input-format") { Description = "binary (default for files), hex or base64.", DefaultValueFactory = _ => "binary" };
        var cmd = new Command("validate-message", "Check that a message is a valid, completely consumed encoding of the type and satisfies all constraints.") { paths, type, rule, input, hex, inputFormat };
        return Wrap(cmd, async (p, ct) =>
        {
            var encodingRule = Rule(p.GetValue(rule)!);
            if (Schema(p, p.GetValue(paths)!, p.GetValue(type)!) is not { } schema)
            {
                return ExitCodes.Errors;
            }

            var data = p.GetValue(hex) is { } h ? DecodeText(Encoding.UTF8.GetBytes(h), "hex") : DecodeText(await ReadInput(p.GetValue(input), ct).ConfigureAwait(false), p.GetValue(inputFormat)!);
            try
            {
                var value = encodingRule.Decode(schema.Type, data, new Asn1DecodingOptions { CancellationToken = ct });
                var errors = Asn1Validator.ValidateValue(schema.Type, value);
                foreach (var e in errors)
                {
                    await Console.Error.WriteLineAsync("invalid: " + e).ConfigureAwait(false);
                }

                if (errors.Count == 0 && !p.GetValue(s_quiet))
                {
                    Console.Out.WriteLine($"valid {encodingRule.Name} encoding of {schema.Type.Name} ({data.Length} octets)");
                }

                return errors.Count == 0 ? ExitCodes.Success : ExitCodes.Errors;
            }
            catch (Asn1DecodeException e)
            {
                await Console.Error.WriteLineAsync($"invalid: {e.Error}: {e.Message}").ConfigureAwait(false);
                return ExitCodes.Errors;
            }
        });
    }

    private static Command InspectCommand()
    {
        var rule = new Option<string>("--format", "-f") { Description = "ber/der/cer (schema-less TLV dump) or uper/aper/oer/coer/jer/xer/cxer (requires --schema and --type).", DefaultValueFactory = _ => "ber" };
        var input = InputOption();
        var hex = new Option<string?>("--hex") { Description = "The encoding as hexadecimal text (instead of --input)." };
        var inputFormat = new Option<string>("--input-format") { Description = "binary (default for files), hex or base64.", DefaultValueFactory = _ => "binary" };
        var schema = new Option<string[]>("--schema") { Description = "ASN.1 files for schema-aware inspection.", AllowMultipleArgumentsPerToken = true };
        var type = new Option<string?>("--type", "-t") { Description = "The type for schema-aware inspection." };
        var cmd = new Command("inspect", "Show the structure of an encoding.") { rule, input, hex, inputFormat, schema, type };
        return Wrap(cmd, async (p, ct) =>
        {
            var data = p.GetValue(hex) is { } h ? DecodeText(Encoding.UTF8.GetBytes(h), "hex") : DecodeText(await ReadInput(p.GetValue(input), ct).ConfigureAwait(false), p.GetValue(inputFormat)!);
            var name = p.GetValue(rule)!;
            var encodingRule = Rule(name);
            if (p.GetValue(schema) is { Length: > 0 } files && p.GetValue(type) is { } typeName)
            {
                if (Schema(p, files, typeName) is not { } s)
                {
                    return ExitCodes.Errors;
                }

                var value = encodingRule.Decode(s.Type, data, new Asn1DecodingOptions { RejectTrailingData = false }, out var consumed);
                Console.Out.WriteLine($"{encodingRule.Name} {s.Type.Name}: {consumed} of {data.Length} octet(s) consumed");
                Console.Out.WriteLine(DynamicJson.Serialize(s.Type, value));
                return ExitCodes.Success;
            }

            if (encodingRule is not X690EncodingRule)
            {
                throw new UsageException($"{encodingRule.Name} is not self-describing; pass --schema and --type");
            }

            Console.Out.Write(TlvInspector.Dump(data));
            return ExitCodes.Success;
        });
    }

    // ------------------------------------------------------------------ informational

    private static int VersionCommand()
    {
        // The informational version keeps prerelease labels and the source revision, e.g. 1.2.0-beta.1+3464ac7....
        var version = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? CSharpGenerator.Version;
        Console.Out.WriteLine($"asn1 (DotAsn) {version}");
        Console.Out.WriteLine($".NET {Environment.Version}");
        return ExitCodes.Success;
    }

    private static int Standards()
    {
        Console.Out.Write(StandardsMatrix.Text);
        return ExitCodes.Success;
    }
}

/// <summary>Invalid usage reported with exit code 2.</summary>
internal sealed class UsageException(string message) : Exception(message);

/// <summary>Adapts a synchronous delegate to a command action.</summary>
internal sealed class SyncAction(Func<ParseResult, int> action) : System.CommandLine.Invocation.SynchronousCommandLineAction
{
    public override int Invoke(ParseResult parseResult) => action(parseResult);
}
