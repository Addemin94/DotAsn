# Language support and C# mapping

## C# type mapping

| ASN.1 | C# | Notes |
|---|---|---|
| BOOLEAN | `bool` | |
| INTEGER | `byte`, `ushort`, `uint`, `ulong`, `sbyte`, `short`, `int`, `long` or `BigInteger` | The smallest type that holds every value of the effective constraint: unsigned for non-negative ranges (`(0..255)` is `byte`, `(0..4294967295)` is `uint`), signed otherwise (`(-1..255)` is `short`). DEFAULT values, named numbers and value assignments use the same type; named numbers outside the constraint (allowed by X.680) and values that violate it (warning ASN7003) get `int`, `long` or `ulong` instead (a `static readonly BigInteger` beyond 64 bits), as do constants of unbounded types. A DEFAULT that does not fit is generated as OPTIONAL (ASN7003). Unbounded, extensible or wider than 64-bit constraints map to `BigInteger` (`--integer-mapping int64` maps unbounded and extensible ones to `long`; larger values are decode errors, never truncated). |
| INTEGER named numbers | constants on the alias class | |
| ENUMERATED | C# `enum` (`int` or `long`) | Extensible enumerations get an `Asn1UnknownExtension` member for items from later versions (decode only). The descriptor is available as `Enum.Asn1Type` (C# 14 static extension member). |
| REAL | `double` | Decimal abstract values are rounded to binary64. |
| BIT STRING | `Asn1.Asn1BitString` | Exact bit length; named bits become constants (bit positions) on the alias class. |
| OCTET STRING | `byte[]` | |
| NULL | `Asn1.Asn1Null` | |
| OBJECT IDENTIFIER / RELATIVE-OID | `Asn1.ObjectIdentifier` / `Asn1.RelativeObjectIdentifier` | Arbitrary-precision arcs. |
| Character strings, time types | `string` | Characters are validated against the repertoire; SIZE counts code points. |
| SEQUENCE / SET | `sealed partial class` | Properties per component; `IEquatable<T>`; `ToString()` prints value notation. |
| OPTIONAL component, extension addition | nullable property | |
| DEFAULT component | property initialized to the default | Omitted on encoding when equal to the default (DER/CER/PER). |
| SEQUENCE OF / SET OF | `List<T>` | |
| CHOICE | `abstract partial class` with one `sealed` nested class per alternative (`Value` property) and a `Kind` enum | Extensible CHOICEs get an `UnknownExtension` class that keeps the raw encoding and index, so values can be relayed. |
| Open type | `Asn1.Runtime.Asn1OpenValue` | Raw encoding plus `DecodeAs<T>()`. |
| Type assignment that is an alias | static class with `Asn1Type` (and constants) | Fields use the underlying C# type. |
| Value assignment | `const` or `static readonly` member of `<Module>Asn1` | |

Inline (anonymous) types become nested classes named after their component (`<Component>Type`, `<Component>Item`
for SEQUENCE OF elements, `<Alternative>Value` for CHOICE alternatives). Parameterized instances become classes at
the point of first use. Name collisions (with the enclosing type, `Equals`, `ToString`, `Asn1Type`, `Kind`, other
members) are resolved with deterministic suffixes and reported as ASN7001.

## Generated artifacts

* `<Module>.g.cs` (or one file per type with `--layout per-type`)
* `<Module>Asn1`: a static class with one descriptor property per type assignment and the value assignments
* `asn1-generated.manifest.json`: generator version, input files with SHA-256, options, output files with SHA-256

Output is deterministic: no timestamps, ordinal ordering, LF line endings.

## Unsupported notation

Reported as diagnostics: parameterized information object classes and object-set parameters (ASN4001),
ENCODING-CONTROL sections and encoding instructions (ASN1015), and X.208 macros (ASN1014). Types containing EXTERNAL,
EMBEDDED PDV, CHARACTER STRING or the IRI types are skipped by the generator (ASN7002).
