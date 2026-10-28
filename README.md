# DotAsn: an ASN.1 toolchain for .NET

[![CI](https://github.com/Addemin94/DotAsn/actions/workflows/ci.yml/badge.svg)](https://github.com/Addemin94/DotAsn/actions/workflows/ci.yml)
[![Studio](https://github.com/Addemin94/DotAsn/actions/workflows/pages.yml/badge.svg)](https://addemin94.github.io/DotAsn/)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

DotAsn compiles ASN.1 specifications, lints them, generates strongly typed C# and TypeScript, and encodes and
decodes messages with BER, CER, DER, APER, UPER, OER, COER, JER, XER and CXER. Everything is written in C# for .NET 10 and has no
third-party ASN.1 dependency. For browsers and Node.js, the same runtime is compiled to WebAssembly (`@dotasn/wasm`).

**[Try it in your browser: DotAsn Studio](https://addemin94.github.io/DotAsn/)** · [Releases](https://github.com/Addemin94/DotAsn/releases) · [Documentation](#documentation)

```text
.asn files ─► parser ─► semantic model ─┬─► linter (asn1 lint)
                                         ├─► C# generator (asn1 generate-csharp)
                                         ├─► TypeScript generator (asn1 generate-typescript)
                                         ├─► schema (asn1 generate-schema)
                                         └─► dynamic descriptors (asn1 encode/decode)
generated C# ─► Asn1.Runtime descriptors ─► BER · CER · DER · APER · UPER · OER · COER · JER · XER · CXER
generated TS + schema ─► @dotasn/wasm (the .NET runtime as WebAssembly) ─► the same ten rules
```

## Features

* **Compiler** for X.680–X.683: modules, imports, automatic tagging, constraints, information object classes and
  sets, parameterization, with precise diagnostics (text, JSON or SARIF).
* **Linter** for standards violations, style and interoperability risks (for example PER-unfriendly constructs).
* **C# generator** producing partial classes, enums and typed descriptors, built as a Roslyn syntax tree.
* **TypeScript generator** producing interfaces, union types, descriptors and the schema of the types for
  [`@dotasn/wasm`](js/wasm/README.md), the .NET runtime compiled to WebAssembly with NativeAOT-LLVM. It brings all
  ten rules, with identical behaviour, to browsers and Node.js.
* **Runtime** with ten encoding rules, strict canonical decoders (DER, CER, COER, CXER), constraint validation,
  extension handling and relaying of unknown extensions, and limits against hostile input.
* **`asn1` CLI** for linting, checking, code generation, encoding, decoding, validation and inspection.
* **DotAsn Studio**, a browser workbench running the same toolchain client-side.

## Install

**Standalone CLI (no .NET needed).** Download the archive for your platform (Linux x64/arm64, Windows x64, macOS
x64/arm64) from [Releases](https://github.com/Addemin94/DotAsn/releases), unpack it and run `asn1`. On macOS a
downloaded executable may need `xattr -d com.apple.quarantine asn1` once.

**.NET tool and libraries.** The packages are published to
[GitHub Packages](https://github.com/Addemin94?tab=packages&repo_name=DotAsn). GitHub Packages requires a personal
access token (classic) with the `read:packages` scope to install NuGet packages, even public ones. Add the feed:

```xml
<!-- nuget.config next to your solution -->
<configuration>
  <packageSources>
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
    <add key="dotasn" value="https://nuget.pkg.github.com/Addemin94/index.json" />
  </packageSources>
  <packageSourceCredentials>
    <dotasn>
      <add key="Username" value="YOUR_GITHUB_USER" />
      <add key="ClearTextPassword" value="%DOTASN_PACKAGES_TOKEN%" />
    </dotasn>
  </packageSourceCredentials>
</configuration>
```

```sh
dotnet tool install --global DotAsn.Cli          # the asn1 command (needs the .NET 10 runtime)
dotnet add package DotAsn.Serialization          # runtime with all encoding rules (for generated code)
dotnet add package DotAsn.Dynamic                # schema-driven values without generated code
```

**From source.** With the .NET 10 SDK:

```sh
git clone https://github.com/Addemin94/DotAsn.git
cd DotAsn
dotnet build Asn1Toolchain.sln
dotnet run --project src/Asn1.Cli -- --help
```

## Quick start

```sh
asn1 lint samples/telemetry --encoding-rules uper
asn1 check samples/telemetry/telemetry.asn --dump
asn1 generate-csharp samples/telemetry --output generated --namespace Telemetry
asn1 generate-typescript samples/telemetry --output generated-ts    # types + dotasn-schema.ts
asn1 generate-schema samples/telemetry -o schema.json              # schema only, for @dotasn/wasm
asn1 encode samples/telemetry/telemetry.asn --type Heartbeat --format uper \
     --value '{"sequenceNumber": 7, "uptimeSeconds": 4000000000}'          # → 07EE6B2800
asn1 decode samples/telemetry/telemetry.asn --type Heartbeat --format uper --hex 07EE6B2800
asn1 validate-message samples/telemetry/telemetry.asn -t Heartbeat -f uper --hex 07EE6B2800
asn1 inspect --format der --hex 300A800107810500EE6B2800
asn1 standards
```

Values are written in the JSON value notation described in [docs/value-notation-json.md](docs/value-notation-json.md).

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

// Every generated value is also an IAsn1Value, for code that handles several types without generics.
IAsn1Value any = heartbeat;
byte[] bytes = Asn1Serializer.Encode(any, Asn1EncodingRules.Uper);   // any.Asn1Type is the descriptor
```

Generated types are `partial` classes with XML documentation taken from the ASN.1 comments. Each type exposes its
descriptor through `T.Asn1Type`. Encoding rules interpret the descriptors, so adding an encoding rule never requires
regenerating code. See [docs/architecture.md](docs/architecture.md).

## Using generated TypeScript

`asn1 generate-typescript` (or Studio's TypeScript tab) writes a TypeScript type and a descriptor constant per ASN.1
type, and `dotasn-schema.ts` with the schema of the types. With the WebAssembly runtime:

```ts
import { loadDotAsn } from '@dotasn/wasm';
import { schema } from './generated-ts/dotasn-schema.js';
import { Heartbeat, Message } from './generated-ts/TelemetryProtocol.js';

const asn1 = await loadDotAsn(schema);
const heartbeat: Heartbeat = { sequenceNumber: 7, uptimeSeconds: 4_000_000_000 };
const bytes = asn1.uper.encode(Heartbeat, heartbeat);      // 07EE6B2800, as in C#
const back = asn1.uper.decode(Heartbeat, bytes);
asn1.xer.encode(Message, { messageType: 'heartbeat', payload: { heartbeat } });
```

A step-by-step guide is in [docs/typescript.md](docs/typescript.md). The generated code imports [`@dotasn/runtime`](js/runtime/README.md), which holds its descriptor and value types and
the value notation. See [js/wasm/README.md](js/wasm/README.md) for loading, errors and limits.

## DotAsn Studio

**[addemin94.github.io/DotAsn](https://addemin94.github.io/DotAsn/)** runs the toolchain entirely in the browser
(Blazor WebAssembly). Nothing is sent to a server: schemas and values stay on the page, and the last schema is kept
in the browser's local storage.

* **Schema:** edit or load a sample in a code editor (CodeMirror) highlighted by DotAsn's own lexer, compile and
  lint (optionally for a target encoding) with errors and warnings shown inline, click a diagnostic to jump to it,
  browse types with their definitions and runtime descriptors.
* **Encode:** random example values, constraint validation, encoding with any of the ten rules (with the exact PER
  bit count), and a side-by-side size comparison of all rules.
* **Decode:** hexadecimal, Base64 or text input; decoded value, consumed octets, constraint violations, transcoding to
  another rule.
* **Inspect:** schema-less BER/DER/CER TLV dump. **Model:** the resolved model. **C#:** the generated code,
  highlighted from Roslyn's own parser.

The first load downloads about 10 MB (the .NET runtime and the toolchain); the C# generator and Roslyn (another
10 MB) load only when the C# tab is used. Run it locally with `dotnet run --project tools/Asn1.Studio`
(http://localhost:5119). The editor is bundled from `tools/Asn1.Studio/editor` into `wwwroot/js/editor.js`
(committed; rebuild with `npm ci && npm run build` there after changing it).

## Documentation

| Document | Contents |
|---|---|
| [Architecture](docs/architecture.md) | Projects, pipeline and design principles |
| [Standards support](docs/standards-support.md) | What is implemented per standard and per type, with evidence and known deviations of other tools |
| [Language support](docs/language-support.md) | Supported X.680–X.683 notation |
| [Encoding rules](docs/encoding-rules.md) | Details and limitations of each encoding rule |
| [JSON value notation](docs/value-notation-json.md) | How values are written for the CLI, the studio and `DotAsn.Dynamic` |
| [TypeScript guide](docs/typescript.md) | Generating TypeScript and encoding/decoding with `@dotasn/wasm` in Node.js and browsers |
| [Schema format](docs/schema-format.md) | The compiled description of types that the WebAssembly runtime loads |
| [Diagnostics](docs/diagnostics.md) | Every diagnostic ID with its meaning |
| [Security](docs/security.md) | Resource limits and hardening for untrusted input |
| [Decisions](docs/decisions/) | Architecture decision records |

## Repository layout

| Path | Contents |
|---|---|
| `src/Asn1.Diagnostics` | Source locations, diagnostics, stable IDs, text/JSON/SARIF output |
| `src/Asn1.Syntax` | Lexer, parser (X.680–X.683), AST |
| `src/Asn1.Core` | Tags, BIT STRING, OBJECT IDENTIFIER, character repertoires, constraint model |
| `src/Asn1.Semantics` | Binder producing the resolved model |
| `src/Asn1.Linter` | Lint rules, configuration, suppressions |
| `src/Asn1.CodeGen.CSharp` | C# generator (Roslyn syntax trees) |
| `src/Asn1.CodeGen.TypeScript` | TypeScript generator (a TypeScript syntax model and printer) |
| `src/Asn1.Runtime` | Descriptors, writer/reader contracts, buffers, shared content codecs, validation |
| `src/Asn1.Runtime.Ber` | BER, CER, DER |
| `src/Asn1.Runtime.Per` | APER, UPER |
| `src/Asn1.Runtime.Oer` | OER, COER |
| `src/Asn1.Runtime.Jer` | JER |
| `src/Asn1.Runtime.Xer` | XER, CXER |
| `src/Asn1.Serialization` | `Asn1EncodingRules` (all implemented rules) |
| `src/Asn1.Dynamic` | Schema-driven descriptors, dynamic values, JSON value notation, random value generator |
| `src/Asn1.Cli` | The `asn1` tool |
| `js/wasm` | `@dotasn/wasm`, the .NET runtime compiled to WebAssembly (`native/`: NativeAOT-LLVM project and exports) |
| `js/runtime` | `@dotasn/runtime`, the support library of generated TypeScript (descriptor and value types, value notation) |
| `tools/Asn1.Studio` | DotAsn Studio, the browser workbench (Blazor WebAssembly) |
| `tools/Asn1.Studio.Engine` | The studio's tool logic, independent of the UI |
| `tests/` | Unit, parser, semantic, code generation, runtime, interoperability, property, fuzz and studio tests |
| `test-vectors/` | Corpus specifications and known-answer vectors (see `test-vectors/README.md`) |
| `samples/` | Example specifications |
| `docs/` | Architecture, standards support, language support, encoding rules, security, diagnostics |

## Status

The supported features, their tests and their known limitations are listed in
[docs/standards-support.md](docs/standards-support.md). In short: parsing, semantic analysis, linting, C# generation,
BER, CER, DER, APER, UPER, OER, COER, JER, BASIC-XER and CANONICAL-XER are implemented and tested. In browsers and
Node.js, `@dotasn/wasm` provides all of these rules. Encoding
instructions and EXTENDED-XER are not implemented; the CLI reports EXTENDED-XER as unsupported.

## Verification

* **Interoperability:** about 9,200 known-answer vectors (about 1,350 per rule) computed by asn1tools (an independent
  implementation), checked bit for bit in both directions for BER, DER, APER, UPER and OER (COER through its strict
  decoder), and as normalised JSON/XML trees plus decoding for JER and XER. Generated C# types, descriptors loaded from
  a schema, and the WebAssembly runtime (through generated TypeScript) are checked against the same vectors.
* **Standards examples:** the X.691 Annex A encodings (ALIGNED and UNALIGNED), the X.696 Annex A encoding (BASIC-OER
  and CANONICAL-OER), the X.697 Annex A examples (JER), the X.693 Annex A
  encodings (BASIC-XER and CXER) and the X.690 encoding examples are reproduced exactly.
* **Robustness:** property-based round-trip tests (FsCheck), mutation fuzzing with allocation bounds, and resource
  limit tests.

Where asn1tools deviates from the ITU-T texts, the text wins. The deviations are documented in
[ADR 0003](docs/decisions/0003-standards-editions.md) and listed for the tests in `test-vectors/deviations.json`.

## Building and contributing

Issues and pull requests are welcome. The build treats warnings as errors and CI checks formatting:

```sh
dotnet build Asn1Toolchain.sln
dotnet format Asn1Toolchain.sln --verify-no-changes
for p in tests/*/; do dotnet test --project "$p" || break; done   # xunit v3 on Microsoft Testing Platform
scripts/fetch-extended-corpus.sh                                   # optional: 3GPP/ETSI corpus for ExtendedCorpusTests
cd js && npm ci && npm run generate -w @dotasn/wasm       # JavaScript packages (Node 22.18+)
npm run build:native -w @dotasn/wasm                       # needs Emscripten 3.1.56, see js/wasm/README.md
npm run typecheck --workspaces && npm test --workspaces
```

Test vectors are described in [test-vectors/README.md](test-vectors/README.md).

## Releasing (maintainers)

Push a version tag; both release workflows also run manually (*Actions > Run workflow*) with an explicit version.

```sh
git tag v0.1.0 && git push origin v0.1.0
```

* **[nuget.yml](.github/workflows/nuget.yml)** builds and tests, packs every library in `src/` as `DotAsn.*` plus the
  `DotAsn.Cli` .NET tool, smoke-tests them from a local feed (`scripts/pack.sh`) and pushes them to GitHub Packages.
  New packages may start out private; change their visibility in each package's settings if needed.
* **[cli.yml](.github/workflows/cli.yml)** builds self-contained single-file `asn1` executables for linux-x64,
  linux-arm64, win-x64, osx-x64 and osx-arm64 (`scripts/publish-cli.sh`), smoke-tests them on Linux, Windows and
  macOS, and attaches the archives with SHA-256 checksums to a GitHub Release.
* **[pages.yml](.github/workflows/pages.yml)** publishes DotAsn Studio on every push to `main`
  (*Settings > Pages > Source: GitHub Actions*). It builds with `scripts/publish-studio.sh` and first checks the site in
  headless Chromium with `scripts/studio_e2e.py`, served the way GitHub Pages serves it.

All three scripts run locally, e.g. `scripts/pack.sh 0.1.0-local`, `scripts/publish-cli.sh linux-x64 0.1.0-local` or
`scripts/publish-studio.sh /DotAsn/`.

## License

[MIT](LICENSE). The third-party specifications in `test-vectors/corpus/` keep their own licenses (see
[test-vectors/README.md](test-vectors/README.md)).
