using Asn1.Diagnostics;
using Asn1.Dynamic;
using Asn1.Runtime;
using Asn1.Semantics;
using BenchmarkDotNet.Attributes;

namespace Asn1.Benchmarks;

/// <summary>
/// Encoding and decoding without generated code, through descriptors built from the compiled module
/// (<see cref="DynamicTypeFactory"/>), as the CLI, DotAsn Studio and the WebAssembly runtime do.
/// </summary>
[MemoryDiagnoser]
public class DynamicCodecBenchmarks
{
    private Asn1EncodingRule _rule = null!;
    private Asn1Type<object?> _type = null!;
    private object? _value;
    private byte[] _encoding = [];

    [Params("ber", "uper", "jer")]
    public string Rule { get; set; } = "";

    /// <summary>Number of measurements in the batch.</summary>
    [Params(1, 32)]
    public int Count { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _rule = BenchmarkChecks.Rule(Rule);
        var path = Path.Combine(AppContext.BaseDirectory, "schemas", "telemetry.asn");
        var compilation = Asn1Compilation.Create([new SourceText(path, File.ReadAllText(path))]);
        _type = new DynamicTypeFactory().Create(compilation.FindType("Message")!);

        // The dynamic value is the decoding of the generated message, so both benchmarks use the same data.
        var message = TelemetryMessages.Create(Count);
        _encoding = _rule.Encode(TelemetryProtocol.Message.Asn1Type, message);
        _value = Decode();
        BenchmarkChecks.Require(Encode().AsSpan().SequenceEqual(_encoding), $"{Rule}: the dynamic encoding differs from the generated one");
    }

    [Benchmark]
    public byte[] Encode() => _rule.Encode(_type, _value);

    [Benchmark]
    public object? Decode() => _rule.Decode(_type, _encoding);
}
