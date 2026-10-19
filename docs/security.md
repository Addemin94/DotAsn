# Security and resource management

DotAsn assumes that ASN.1 specifications and encoded messages can be malicious.

## Decoding (`Asn1DecodingOptions`)

| Limit | Default | Protects against |
|---|---|---|
| `MaxInputSize` | 64 MiB | oversized input |
| `MaxDepth` | 128 | stack exhaustion through nesting (TLV regions, structures, collections) |
| `MaxCollectionCount` | 1,000,000 | unbounded SEQUENCE OF / SET OF processing, including zero-width elements |
| `MaxStringLength` | 16 MiB | single huge string, octet string or bit string allocations |
| `MaxTotalAllocation` | 256 MiB | aggregate allocation (nested open-type readers share the budget) |
| `MaxIntegerLength` | 1024 octets | expensive big-integer parsing |
| `RejectTrailingData` | true | smuggled bytes after the value |
| `CancellationToken` | none | long decodes; checked in collection loops |

Guarantees, each covered by tests:

* All readers check bounds before reading: `BitReader` and the TLV parser validate every length against the
  current region before slicing, so out-of-range access is impossible and truncated input fails with
  `Asn1DecodeError.Truncated`.
* Declared lengths never cause allocation by themselves. A length is checked against the remaining input
  before any buffer is created (`RobustnessTests.HugeDeclaredLengthDoesNotAllocate`).
* Integers that do not fit the generated CLR type fail with `Asn1DecodeError.Overflow`. Values are never truncated.
* DER and CER decoding is strict, so BER-only forms cannot slip through (see `docs/encoding-rules.md`).
* Mutation fuzzing (`tests/Asn1.FuzzTests`) checks that only `Asn1Exception`s escape and that allocation stays
  below a fixed budget.

## Text encodings (JER, XER)

* Input must be valid UTF-8; it is validated before parsing, so malformed sequences are reported as
  `Asn1DecodeException` (Malformed) rather than surfacing from the JSON or XML parser (found by fuzzing).
* JER is parsed with `Utf8JsonReader`/`JsonDocument` with `MaxDepth` from the options, comments disallowed, and
  trailing data rejected.
* XER is parsed with `XmlReader` with `DtdProcessing.Prohibit`, no `XmlResolver` and `MaxCharactersFromEntities = 0`,
  so DTDs, entity expansion and external entities (XXE) are impossible. Element nesting is bounded by `MaxDepth`.
  Comments, processing instructions and attributes are rejected (X.693 8.1.2).
* Large numbers in JSON or XML text are bounded by `MaxIntegerLength`; strings and hexadecimal data by
  `MaxStringLength`; the input as a whole by `MaxInputSize` and `MaxTotalAllocation`.

## Encoding (`Asn1EncodingOptions`)

* Constraint validation is on by default. Values outside the root and additions of an extensible constraint are
  rejected unless `AllowValuesOutsideExtensibleConstraints` is set.
* `MaxDepth` and `MaxEncodedSize` bound recursion and output size.

## Compilation (`Asn1CompilerOptions`, `ParserOptions`)

| Limit | Default |
|---|---|
| `ParserOptions.MaxDepth` (syntactic nesting) | 200 |
| `ParserOptions.MaxSourceLength` | 64 Mi characters |
| `MaxInstantiationDepth` (parameterized types) | 24 |
| `MaxTypeNodes` (resolved type nodes) | 2,000,000 |
| CLI `--max-file-size` | 64 MiB |

Recursive and pathological definitions (alias cycles, exponential parameter instantiation, self-including
COMPONENTS OF) are reported as diagnostics instead of looping. PATTERN regular expressions run with a 1-second
timeout.

## Logging

The CLI and libraries do not log message contents. Error messages include component paths and positions,
not payload data. Diagnostic messages may quote names and values from the specification.
