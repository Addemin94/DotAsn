using System.Buffers;
using Asn1.Runtime;
using BenchmarkDotNet.Attributes;
using TelemetryProtocol;

namespace Asn1.Benchmarks;

/// <summary>Encoding and decoding generated C# types with every implemented encoding rule.</summary>
[MemoryDiagnoser]
public class CodecBenchmarks
{
    private readonly ArrayBufferWriter<byte> _buffer = new(16 * 1024);
    private Asn1EncodingRule _rule = null!;
    private Message _message = null!;
    private byte[] _encoding = [];

    [Params("ber", "cer", "der", "aper", "uper", "oer", "coer", "jer", "xer", "cxer")]
    public string Rule { get; set; } = "";

    /// <summary>Number of measurements in the batch.</summary>
    [Params(1, 32)]
    public int Count { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _rule = BenchmarkChecks.Rule(Rule);
        _message = TelemetryMessages.Create(Count);
        _encoding = _rule.Encode(Message.Asn1Type, _message);
        BenchmarkChecks.Require(Decode().Equals(_message), $"{Rule} does not round-trip the message");
    }

    [Benchmark]
    public byte[] Encode() => _rule.Encode(Message.Asn1Type, _message);

    /// <summary>Encodes into a reused buffer (no result array).</summary>
    [Benchmark]
    public int EncodeToBuffer()
    {
        _buffer.ResetWrittenCount();
        _rule.Encode(Message.Asn1Type, _message, _buffer);
        return _buffer.WrittenCount;
    }

    [Benchmark]
    public Message Decode() => _rule.Decode(Message.Asn1Type, _encoding);
}
