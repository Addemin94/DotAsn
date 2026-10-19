# Handoff: current state and how to continue

_Read this first when resuming, then `docs/implementation-checklist.md`, `docs/architecture.md` and `docs/decisions/`._

The original brief is the "Master Engineering Prompt" (an ASN.1 compiler, linter, C# generator and multi-rule runtime).
Milestones are numbered as in that brief.

## 1. State

* `dotnet build Asn1Toolchain.sln`: 0 errors, 0 warnings (warnings are treated as errors). `dotnet format --verify-no-changes` is clean.
* Tests: `dotnet test --project tests/<Project>` (xunit.v3 on Microsoft Testing Platform, configured in `global.json`).
  Projects: ParserTests, SemanticTests, RuntimeTests, InteroperabilityTests, CodeGenTests, UnitTests, PropertyTests,
  FuzzTests. See the checklist for counts and evidence.
* Git: initialised on branch `main`; **nothing committed yet**. Ask the user before committing. The original Rider
  template project `DotAsn/` and `DotAsn.sln` were deliberately left untouched (deleting them was refused) and are
  not part of `Asn1Toolchain.sln`.

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
* `WellKnownOids` (X.660 arc names) moved to `Asn1.Core` and is shared by the binder and the XER decoder.
* `DynamicTypeFactory.Create` is thread-safe (descriptors resolve components lazily, possibly concurrently).
* Documented asn1tools deviations from the standards: see `docs/standards-support.md` (last section) and ADR 0003.

## 3. Next tasks (in order)

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
