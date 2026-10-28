# Handoff: current state and how to continue

_Read this first when resuming, then `docs/implementation-checklist.md`, `docs/architecture.md` and `docs/decisions/`._

The original brief is the "Master Engineering Prompt" (an ASN.1 compiler, linter, C# generator and multi-rule runtime).
Milestones are numbered as in that brief.

## 1. State

* Released: 0.2.0 (exact C# integer types, TypeScript generation with the WebAssembly runtime, the CodeMirror
  editor) and 0.2.1 (Studio's "Guess type"); CHANGELOG.md describes them. The npm packages are not published yet.

* `dotnet build Asn1Toolchain.sln`: 0 errors, 0 warnings (warnings are treated as errors). `dotnet format --verify-no-changes` is clean.
* Tests: `dotnet test --project tests/<Project>` (xunit.v3 on Microsoft Testing Platform, configured in `global.json`).
  Projects: ParserTests, SemanticTests, RuntimeTests, InteroperabilityTests, CodeGenTests, UnitTests, PropertyTests,
  FuzzTests. See the checklist for counts and evidence.
* Git: branch `main`, public at https://github.com/Addemin94/DotAsn. Ask the user before committing; the user pushes.
* JavaScript (npm workspace in `js/`): `npm ci && npm run generate -w @dotasn/wasm && npm run build:native -w @dotasn/wasm && npm run typecheck --workspaces && npm test --workspaces`.
  `build:native` needs Emscripten 3.1.56; locally it is installed in `.cache/emsdk` (git-ignored).

## 2. Done

* Milestones 1–10 are complete without encoding instructions (BER, CER, DER, APER, UPER, OER, COER, JER, BASIC-XER,
  CANONICAL-XER); see `docs/implementation-checklist.md`.
* `samples/telemetry/telemetry.asn` is the realistic public example protocol used by the CLI, code generation,
  property and fuzz tests, with asn1tools vectors for every codec (`test-vectors/*/telemetry.json`). Private or
  customer specifications must never be added to the repository.
* PER was verified against the X.691 (02/2021) Annex A published encodings (all 8 match).
* OER/COER (`src/Asn1.Runtime.Oer`) reproduces the X.696 Annex A encoding in both profiles and matches the 958
  asn1tools OER vectors byte for byte, except the documented deviations. The COER decoder is strict (clause 31). The IEEE 754 REAL forms
  (X.696 12.2/12.3) are rejected as unsupported.
* JER (`src/Asn1.Runtime.Jer`) reproduces the X.697 Annex A examples; asn1tools JER vectors are compared as
  canonicalised JSON trees. Shared OER/JER constraint visibility lives in `Asn1.Runtime/NonExtensibleConstraints.cs`.
* XER (`src/Asn1.Runtime.Xer`) reproduces X.693 Annex A.4 byte for byte; CXER decoding is strict by canonical
  re-encoding. XER exposed two generator naming bugs, fixed: referenced types are owned by their own assignment
  (no nested `S4.BType` for `b C1`), and alias descriptors carry the alias name.
* The C# generator builds its output with Roslyn `SyntaxFactory` (ADR 0004): `Cs.cs` helpers, `Literals.cs`,
  `SourceFormatter.cs` (normalisation, doc comments, wrapping). `CodeWriter` is gone.
* DotAsn Studio (`tools/Asn1.Studio`, Blazor WebAssembly; logic in `tools/Asn1.Studio.Engine`, tested by
  `tests/Asn1.StudioTests`) is a browser workbench deployed to GitHub Pages by `.github/workflows/pages.yml` via
  `scripts/publish-studio.sh`; `scripts/studio_e2e.py` checks the published site in headless Chromium (Playwright).
  Building it exposed and fixed a runtime bug: validation of dynamic values did not recurse into components
  (`IAsn1ValidationAdapter`).
* Releases: `.github/workflows/nuget.yml` (DotAsn.* packages and the DotAsn.Cli tool to GitHub Packages, via
  `scripts/pack.sh`, which smoke-tests them from a local feed) and `.github/workflows/cli.yml` (self-contained
  single-file `asn1` executables per platform attached to a GitHub Release, via `scripts/publish-cli.sh`). Both run
  for `v*` tags.
* `WellKnownOids` (X.660 arc names) moved to `Asn1.Core` and is shared by the binder and the XER decoder.
* `DynamicTypeFactory.Create` is thread-safe (descriptors resolve components lazily, possibly concurrently).
* Documented asn1tools deviations from the standards: see `docs/standards-support.md` (last section) and ADR 0003.
  The machine-readable list `test-vectors/deviations.json` is shared by the .NET and TypeScript tests.
* TypeScript generation (ADR 0005): `src/Asn1.CodeGen.TypeScript` (`asn1 generate-typescript`) writes types,
  descriptors (for `@dotasn/runtime` in `js/runtime`: descriptor/value types and the value notation) and
  `dotasn-schema.ts`. Tests: `tests/Asn1.CodeGenTests/TypeScriptGeneratorTests.cs`, `js/runtime/test`. CI job
  `javascript` in `ci.yml`.
* WebAssembly runtime (ADR 0006): `js/wasm` (`@dotasn/wasm`) with `native/` (NativeAOT-LLVM, `Exports.cs`).
  Types come from a DotAsn schema (`src/Asn1.Dynamic/Asn1Schema.cs`, `Asn1SchemaWriter.cs`, docs/schema-format.md),
  written by `asn1 generate-schema`, by `generate-typescript` (`dotasn-schema.ts`) and by Studio's TypeScript tab.
  Tests: `SchemaVectorTests`, `SchemaFormatTests`, `js/wasm/test`, the Studio e2e. Size about 1.1 MB brotli.
* `test-vectors/corpus/dotasn/primitives.asn` (`dotasn_primitives` vectors) covers REAL, OBJECT IDENTIFIER, times,
  wide strings, SET OF and OER integer sizes. It exposed six more asn1tools deviations (ADR 0003). No XER vectors:
  asn1tools does not finish XER for it.

## 3. Next tasks (in order)

0. JavaScript: publish `@dotasn/wasm` and `@dotasn/runtime` (needs a decision on the npm registry/scope).

1. Milestone 11 (hardening and release): NuGet packaging, benchmarks, `Asn1.Build` MSBuild integration, and the
   open items in `docs/implementation-checklist.md`. Encoding instructions/EXTENDED-XER are the largest remaining
   standards gap.
2. The remaining open items in `docs/implementation-checklist.md`.

## 4. Useful commands and notes

```sh
dotnet build Asn1Toolchain.sln
scripts/fetch-extended-corpus.sh                         # 3GPP etc. into .cache/ (for ExtendedCorpusTests)
python3 -m venv .venv && .venv/bin/pip install asn1tools pyasn1
dotnet run scripts/gen-cases.cs -- <name> <count> <seed> <spec.asn>...   # add --no-cache after editing src
.venv/bin/python scripts/asn1tools_vectors.py --codecs ber,der,uper,per,oer,jer,xer
dotnet run --project src/Asn1.Cli -- <command>           # or src/Asn1.Cli/bin/Debug/net10.0/asn1
UPDATE_DOCS=1 dotnet test --project tests/Asn1.UnitTests  # regenerates docs/diagnostics.md
```

* ITU-T PDFs (02/2021) are free from itu.int, e.g.
  `https://www.itu.int/rec/dologin_pub.asp?lang=e&id=T-REC-X.696-202102-I!!PDF-E&type=items`. Convert them with
  `pdftotext -layout` into `.cache/itu/` (git-ignored; never commit them).
* `dotnet run file.cs` caches builds. Use `--no-cache` after changing referenced projects.
* The shell is not bash: an unquoted `$A` holding a command line is not word-split, so call
  `src/Asn1.Cli/bin/Debug/net10.0/asn1` directly.
