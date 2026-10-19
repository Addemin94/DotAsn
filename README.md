# DotAsn: an ASN.1 toolchain for .NET

DotAsn compiles ASN.1 specifications, lints them, generates strongly typed C#, and encodes and decodes messages
with BER, CER, DER, APER, UPER, OER, COER, JER, XER and CXER. Everything is written in C# for .NET 10 and has no third-party ASN.1 dependency.

```text
.asn files ─► parser ─► semantic model ─┬─► linter (asn1 lint)
                                         ├─► C# generator (asn1 generate-csharp)
                                         └─► dynamic descriptors (asn1 encode/decode)
generated C# ─► Asn1.Runtime descriptors ─► BER · CER · DER · APER · UPER · OER · COER · JER · XER · CXER
```

## Quick start

```sh
dotnet build Asn1Toolchain.sln
dotnet test --project tests/Asn1.InteroperabilityTests   # or any other test project

# The CLI (src/Asn1.Cli, executable "asn1"):
asn1 lint samples/telemetry --encoding-rules uper
asn1 check samples/telemetry/telemetry.asn --dump
asn1 generate-csharp samples/telemetry --output generated --namespace Telemetry
asn1 encode samples/telemetry/telemetry.asn --type Heartbeat --format uper \
     --value '{"sequenceNumber": 7, "uptimeSeconds": 4000000000}'          # → 07EE6B2800
asn1 decode samples/telemetry/telemetry.asn --type Heartbeat --format uper --hex 07EE6B2800
asn1 validate-message samples/telemetry/telemetry.asn -t Heartbeat -f uper --hex 07EE6B2800
asn1 inspect --format der --hex 300A800107810500EE6B2800
asn1 standards
```

Run the CLI from source with `dotnet run --project src/Asn1.Cli -- <command>`.

## Using generated code

```csharp
using Asn1.Runtime;
using Telemetry;

var heartbeat = new Heartbeat { SequenceNumber = 7, UptimeSeconds = 4_000_000_000 };
byte[] uper = Asn1Serializer.Encode(heartbeat, Asn1EncodingRules.Uper);
Heartbeat back = Asn1Serializer.Decode<Heartbeat>(uper, Asn1EncodingRules.Uper);

// CHOICE values are nested classes; ENUMERATED types are C# enums with a static Asn1Type extension.
var message = new Message { MessageType = MessageType.Heartbeat, Payload = new Payload.Heartbeat(heartbeat) };
byte[] der = Asn1Serializer.Encode(MessageType.TimeSyncRequest, MessageType.Asn1Type, Asn1EncodingRules.Der);
IReadOnlyList<Asn1ValidationError> problems = Asn1Serializer.Validate(message);
```

Generated types are `partial` classes with XML documentation taken from the ASN.1 comments. Each type exposes its
descriptor through `T.Asn1Type`. Encoding rules interpret the descriptors, so adding an encoding rule never requires
regenerating code. See [docs/architecture.md](docs/architecture.md).

## DotAsn Studio (browser workbench)

`tools/Asn1.Studio` is a single-page Blazor WebAssembly app that runs the toolchain entirely in the browser. Nothing
is sent to a server: schemas and values stay on the page (the last schema is kept in the browser's local storage).

* **Schema:** edit or load a sample, compile and lint (optionally for a target encoding), click a diagnostic to see
  the source line, browse types with their definitions and runtime descriptors.
* **Encode:** random example values, constraint validation, encoding with any of the ten rules (with the exact PER
  bit count), and a side-by-side size comparison of all rules.
* **Decode:** hexadecimal, Base64 or text input; decoded value, consumed octets, constraint violations, transcoding to
  another rule.
* **Inspect:** schema-less BER/DER/CER TLV dump. **Model:** the resolved model. **C#:** the generated code.

```sh
dotnet run --project tools/Asn1.Studio          # http://localhost:5119
scripts/publish-studio.sh /DotAsn/               # static site in artifacts/studio/publish/wwwroot
```

**GitHub Pages:** in the repository settings choose *Pages > Build and deployment > Source: GitHub Actions*. The
workflow `.github/workflows/pages.yml` then publishes the studio on every push to `main`, at
`https://<owner>.github.io/<repository>/` (the base path is taken from the Pages configuration, so user sites and
custom domains work too). Before deploying, it runs `scripts/studio_e2e.py`, which serves the site the way GitHub
Pages does and exercises every tab in headless Chromium:

```sh
.venv/bin/pip install playwright && .venv/bin/playwright install chromium
.venv/bin/python scripts/studio_e2e.py artifacts/studio/publish/wwwroot --base /DotAsn/
```

The first load downloads about 10 MB (the .NET runtime and the toolchain); the C# generator and Roslyn (another
10 MB) load only when the C# tab is used.

## Repository layout

| Path | Contents |
|---|---|
| `src/Asn1.Diagnostics` | Source locations, diagnostics, stable IDs, text/JSON/SARIF output |
| `src/Asn1.Syntax` | Lexer, parser (X.680–X.683), AST |
| `src/Asn1.Core` | Tags, BIT STRING, OBJECT IDENTIFIER, character repertoires, constraint model |
| `src/Asn1.Semantics` | Binder producing the resolved model |
| `src/Asn1.Linter` | Lint rules, configuration, suppressions |
| `src/Asn1.CodeGen.CSharp` | C# generator (Roslyn syntax trees) |
| `src/Asn1.Runtime` | Descriptors, writer/reader contracts, buffers, shared content codecs, validation |
| `src/Asn1.Runtime.Ber` | BER, CER, DER |
| `src/Asn1.Runtime.Per` | APER, UPER |
| `src/Asn1.Runtime.Oer` | OER, COER |
| `src/Asn1.Runtime.Jer` | JER |
| `src/Asn1.Runtime.Xer` | XER, CXER |
| `src/Asn1.Serialization` | `Asn1EncodingRules` (all implemented rules) |
| `src/Asn1.Dynamic` | Schema-driven descriptors, dynamic values, JSON value notation, random value generator |
| `src/Asn1.Cli` | The `asn1` tool |
| `tools/Asn1.Studio` | DotAsn Studio, the browser workbench (Blazor WebAssembly) |
| `tools/Asn1.Studio.Engine` | The studio's tool logic, independent of the UI |
| `tests/` | Unit, parser, semantic, code generation, runtime, interoperability, property, fuzz and studio tests |
| `test-vectors/` | Corpus specifications and known-answer vectors (see `test-vectors/README.md`) |
| `samples/` | Example specifications |
| `docs/` | Architecture, standards support, language support, encoding rules, security, diagnostics |

## Status

The supported features, their tests and their known limitations are listed in
[docs/standards-support.md](docs/standards-support.md). In short: parsing, semantic analysis, linting, C# generation,
BER, CER, DER, APER, UPER, OER, COER, JER, BASIC-XER and CANONICAL-XER are implemented and tested. Encoding
instructions and EXTENDED-XER are not implemented; the CLI reports EXTENDED-XER as unsupported.

## Verification

* **Interoperability:** about 6,600 known-answer vectors (about 950 per rule) computed by asn1tools (an independent implementation),
  checked bit for bit in both directions for BER, DER, APER, UPER and OER (COER through its strict decoder), and as
  normalised JSON/XML trees plus decoding for JER and XER. Generated C# types are checked against the
  same vectors.
* **Standards examples:** the X.691 Annex A encodings (ALIGNED and UNALIGNED), the X.696 Annex A encoding (BASIC-OER
  and CANONICAL-OER), the X.697 Annex A examples (JER), the X.693 Annex A
  encodings (BASIC-XER and CXER) and the X.690 encoding examples are reproduced exactly.
* **Robustness:** property-based round-trip tests (FsCheck), mutation fuzzing with allocation bounds, and resource
  limit tests.

Where asn1tools deviates from the ITU-T texts, the text wins. The deviations are documented in
[ADR 0003](docs/decisions/0003-standards-editions.md).
