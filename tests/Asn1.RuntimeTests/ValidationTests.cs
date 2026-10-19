using Asn1.Runtime;
using Xunit;

namespace Asn1.RuntimeTests;

public class ValidationTests
{
    [Fact]
    public void DynamicValuesAreValidatedRecursively()
    {
        // Regression: the boxing adapters of the dynamic model hid the structure from Asn1Validator, so only the
        // top-level constraint was checked and component, alternative and element constraints were missed.
        var schema = new Schema("""
            S ::= SEQUENCE { a INTEGER (0..255), b C, c SEQUENCE OF IA5String (SIZE (1..2)) }
            C ::= CHOICE { x INTEGER (1..3), y BOOLEAN }
            """);
        var type = schema["S"];
        Assert.Empty(type.ValidateBoxed(schema.Value("S", """{"a":1,"b":{"x":2},"c":["ab"]}""")));

        var errors = type.ValidateBoxed(schema.Value("S", """{"a":300,"b":{"x":9},"c":["abc"]}""")).Select(e => e.Path).ToList();
        Assert.Contains("a", errors);
        Assert.Contains("b.x", errors);
        Assert.Contains(errors, p => p.StartsWith("c", StringComparison.Ordinal));
    }
}
