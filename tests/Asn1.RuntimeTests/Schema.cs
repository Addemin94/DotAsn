using Asn1.Dynamic;
using Asn1.Runtime;
using Asn1.Semantics;
using Xunit;

namespace Asn1.RuntimeTests;

/// <summary>Compiles inline ASN.1 and builds dynamic descriptors for tests.</summary>
internal sealed class Schema
{
    private readonly Asn1Compilation _compilation;
    private readonly DynamicTypeFactory _factory = new();

    public Schema(string body, string tags = "AUTOMATIC TAGS")
    {
        _compilation = Asn1Compilation.FromText($"T DEFINITIONS {tags} ::= BEGIN\n{body}\nEND");
        var errors = _compilation.Diagnostics.Where(d => d.IsError).Select(d => d.ToString()).ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors));
    }

    public Asn1Type<object?> this[string name] => _factory.Create(_compilation.FindType(name)!);

    public object? Value(string type, string json) => DynamicJson.Parse(this[type], json);

    public string Encode(Asn1EncodingRule rule, string type, string json) => Convert.ToHexString(rule.Encode(this[type], Value(type, json)));

    public string Decode(Asn1EncodingRule rule, string type, string hex) => DynamicJson.Serialize(this[type], rule.Decode(this[type], Convert.FromHexString(hex)), indented: false);
}
