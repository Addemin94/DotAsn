# Architecture

DotAsn is an ASN.1 toolchain built around one encoding-independent semantic model and a typed runtime
descriptor model that every encoding rule interprets.

```text
ASN.1 sources ──► Asn1.Syntax (lexer, recursive-descent parser, AST, comments)
                     │  braced values, actual parameters and object definitions are kept as token ranges
                     ▼
                  Asn1.Semantics (binder)
                     │  modules, imports/exports, two-phase type resolution (headers, then structure),
                     │  tagging incl. automatic tagging, parameterization, information objects,
                     │  type-directed value parsing, constraints → Asn1.Core constraint trees
                     ▼
              Resolved model (ModuleSymbol, AsnType, AsnComponent, AsnValue, ObjectClass, ...)
                ├──► Asn1.Linter            rule-based diagnostics (standards + style + interoperability)
                ├──► Asn1.CodeGen.CSharp    generated C# classes + typed descriptors (no encoding logic)
                └──► Asn1.Dynamic           descriptors built at run time (CLI, interop tests, fuzzing)
                                │
                                ▼
                     Asn1.Runtime: Asn1Type<T> descriptors ──► Asn1Writer / Asn1Reader contracts
                                │
        ┌────────────┬──────────┴──────────┬───────────────┬─────────────┐
        ▼            ▼                     ▼               ▼             ▼
   BER/CER/DER   PER (APER/UPER)      OER/COER          XER           JER
 (Asn1.Runtime.Ber)  (Asn1.Runtime.Per)  (Asn1.Runtime.Oer) ...
```

## Projects

| Project | Responsibility |
|---|---|
| `Asn1.Diagnostics` | Source text and locations, the diagnostic model, the central catalog of stable diagnostic IDs, text/JSON/SARIF output. |
| `Asn1.Syntax` | X.680–X.683 lexer and parser with error recovery; the AST with full source spans and attached comments. |
| `Asn1.Core` | Encoding-independent values (tags, BIT STRING, OBJECT IDENTIFIER, NULL), character repertoires and the structured constraint model with its evaluator and integer interval algebra. Shared by the compiler and the runtime. |
| `Asn1.Semantics` | The binder producing the resolved model, plus semantic checks (tag distinctness, finiteness of recursion, DEFAULT values, imports). |
| `Asn1.Runtime` | Descriptors (`Asn1Type<T>`), the writer/reader contracts, options and resource limits, errors, validation, bit buffers and content codecs shared by several rules (two's complement, OID, REAL, character strings). |
| `Asn1.Runtime.Ber` | X.690 BER, CER and DER. One TLV engine; canonical encoding and strict decoding differ per profile. |
| `Asn1.Runtime.Per` | X.691 PER, aligned and unaligned variants, with PER-visible constraint analysis. |
| `Asn1.Runtime.Oer` | X.696 OER, BASIC and CANONICAL profiles. |
| `Asn1.Runtime.Jer` | X.697 JER (JSON) without encoding instructions. |
| `Asn1.Runtime.Xer` | X.693 BASIC-XER and CANONICAL-XER (XML); DTDs prohibited. |
| `tools/Asn1.Studio` | Browser workbench (Blazor WebAssembly) over the same libraries; static site for GitHub Pages. |
| `Asn1.Serialization` | `Asn1EncodingRules`: one place that references every implemented rule. |
| `Asn1.Dynamic` | Descriptors from the semantic model at run time, a dynamic value model, the lossless JSON value notation and a constraint-aware random value generator. |
| `Asn1.Linter` | Configurable lint rules on top of the semantic model. |
| `Asn1.CodeGen.CSharp` | C# generator consuming the semantic model; builds the output as a Roslyn syntax tree (ADR 0004). |
| `Asn1.Cli` | The `asn1` command-line tool. |

## Key design decisions

* **Typed descriptors, double dispatch.** Generated code exposes `Asn1Type<T>` descriptors. A descriptor's
  `Encode`/`Decode` calls the writer/reader method for its kind (`WriteSequence<T>`, `ReadInteger`, ...), so encoding
  rules operate on typed values without reflection or boxing of structured values. New encoding rules work with
  already generated code. See [ADR 0002](decisions/0002-typed-descriptors.md).
* **The compiler resolves, the encoding rules interpret.** The semantic model resolves constraints into structured
  trees with literal values (references inlined). Encoding-specific interpretation, such as PER visibility of
  constraints, lives in the rule implementation, which caches derived data per descriptor
  (`Asn1Type.GetRuleData`).
* **Two-phase type resolution.** A type occurrence first gets a *header* (kind and effective tags), and its
  *structure* (components, items) is resolved from a work queue. References only need headers, which makes
  recursive and mutually recursive types finite and keeps tag computation correct for recursive references.
* **Deferred notation.** Braced values, actual parameters and information object definitions are ambiguous without
  type information. The parser keeps their token ranges and the binder re-parses them once the governing type or
  class is known.
* **Encoding-specific interpretation lives in the rule.** For example, `PerVisibleConstraints` (in `Asn1.Runtime.Per`)
  derives effective bounds, extensibility and permitted alphabets from the generic constraint trees.
* **Tags in the model.** Every type occurrence carries its effective tag list (outermost first, with explicitness).
  Tags drive BER/DER/CER, CER/DER SET ordering, and PER CHOICE and SET ordering.

## Error handling

* Compiler: diagnostics with stable IDs and categories (syntax, semantic, constraint, unsupported, style,
  interoperability, code generation, internal); see `DiagnosticCatalog`.
* Runtime: `Asn1DecodeException` (classified by `Asn1DecodeError`, with bit offset and component path),
  `Asn1EncodeException`, `Asn1ConstraintViolationException`, `Asn1UnsupportedException`. Readers validate bounds
  before every access, and limits come from `Asn1DecodingOptions`.

## Buffer ownership

* Encoders own their scratch buffers (pooled for TLV encodings), and results are copied into caller-owned
  `byte[]` or an `IBufferWriter<byte>`.
* Decoders read from caller-owned `ReadOnlyMemory<byte>`, which must not change during decoding. Decoded values
  never alias the input: octet strings, bit strings and open-type contents are copied.
