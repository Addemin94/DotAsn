# Changelog

## Unreleased

### Added

- **DotAsn Studio: Guess type.** The Decode tab decodes a message as every type of the schema and lists the types it
  could be. Exact matches come first: the whole input is consumed, the value satisfies its constraints, holds nothing
  from a later version and encodes back to the same octets. "Decode as" decodes the message as one of them.
  `Workbench.Guess` is the engine behind it.

## 0.2.0 - 2026-10-28

### Breaking changes

- **Generated C# uses the exact integer type of each INTEGER.** A bounded INTEGER maps to the smallest C# type that
  holds its whole range: `byte`, `ushort`, `uint` or `ulong` for non-negative ranges, and `sbyte`, `short`, `int` or
  `long` otherwise. For example, `INTEGER (0..255)` is `byte` and `INTEGER (0..4294967295)` is `uint` (it was `long`).
  Properties, descriptors, DEFAULT values, named-number constants and value assignments all use the same type.
  Code that mixes these properties with `int` or `long` arithmetic may need casts after regenerating.
- A named number or named bit with the name of its type (`Top ::= INTEGER { top(1) }`) is generated as `TopValue`,
  because the old name did not compile (CS0542).

### Added

- **TypeScript generation and the WebAssembly runtime.**
  - `asn1 generate-typescript` writes typed interfaces and descriptors per ASN.1 module, plus `dotasn-schema.ts`.
  - `@dotasn/wasm` is the .NET runtime compiled to WebAssembly with NativeAOT-LLVM: all ten encoding rules in
    browsers and Node.js, about 1.1 MB compressed, with the .NET encoding and decoding options. `@dotasn/runtime`
    holds the types that generated code uses.
  - Inline types are named by their path (e.g. `Module.Type.component`) in the generated TypeScript and the schema,
    so they can be encoded on their own.
  - `asn1 generate-schema` writes the DotAsn schema (docs/schema-format.md) for use without generated TypeScript.
  - Guide: docs/typescript.md.
- **IAsn1Value.** `IAsn1Value<T>` now inherits a non-generic `IAsn1Value`, whose `Asn1Type` property is the value's
  descriptor. `Asn1Serializer` accepts it in `Encode`, `EncodeAsync` and `Validate`.
- **DotAsn Studio.**
  - The schema editor is CodeMirror, highlighted by DotAsn's own lexer. Errors and warnings are shown inline, and
    clicking a diagnostic jumps to it.
  - Generated C# is highlighted from Roslyn's parser; the namespace defaults to `Protocol.Test`.
  - A TypeScript tab generates TypeScript and downloads the schema.
- `Asn1.Syntax.SyntaxClassifier`, which classifies ASN.1 text for highlighting.
- Diagnostic ASN7003: a value does not fit the C# or TypeScript type of its INTEGER type (with what the generator does instead).
- Test vectors for REAL, OBJECT IDENTIFIER, the time types, wide strings, SET OF and OER integer sizes
  (`dotasn_primitives`). Six more deviations of asn1tools from the ITU-T texts are documented in ADR 0003 and in
  `test-vectors/deviations.json`.

### Fixed

- The C# generator no longer crashes or emits code that does not compile for:
  - named numbers outside the constraint (allowed by X.680);
  - named bits beyond `int`;
  - values outside their type.
- BIT STRING named bits and ENUMERATED values beyond 64 bits are reported as unsupported instead of crashing the
  generator, the dynamic runtime (CLI encode/decode, Studio) and the schema writer.
- `Asn1ValueFormatter` formats `sbyte`, `byte` and `ushort` values in the same way for every culture.
- An unknown CHOICE alternative written in the JSON value notation can be read back, and keeps its PER extension
  index, so it can be relayed.

## 0.1.1

- PER decoding accepts empty encodings, which asn1scc and asn1tools send for values of no bits.
  `Asn1EncodingOptions.EmptyPerEncodingAsZeroOctet = false` sends no octets to peers that expect that.

## 0.1.0

- First release:
  - the compiler (X.680 to X.683) and linter;
  - the C# generator;
  - the runtime with BER, CER, DER, APER, UPER, OER, COER, JER, XER and CXER;
  - the `asn1` CLI and DotAsn Studio.
