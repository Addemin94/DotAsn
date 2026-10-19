# Implementation checklist

Status at the last update. Evidence refers to test projects under `tests/`.

## Milestones

| # | Milestone | Status | Evidence |
|---|---|---|---|
| 1 | Foundation (solution, build settings, diagnostics, docs) | Done | warnings-as-errors build; `dotnet format --verify-no-changes` clean; CI workflow |
| 2 | Lexer, parser, AST, error recovery | Done | ParserTests (30); all 59 asn1tools corpus files incl. 3GPP parse without errors |
| 3 | Semantic compiler | Done (Tier 4 items limited) | SemanticTests (81) incl. extended 3GPP/ETSI/OMA/IEEE/CEN corpus |
| 4 | Linter and CLI | Done | UnitTests/LinterTests, UnitTests/CliTests (exit codes, SARIF/JSON) |
| 5 | C# generator | Done | CodeGenTests (81): Roslyn compile with no warnings, deterministic output, vectors through generated types (DER, UPER, APER, OER, JER, XER) |
| 6 | Shared runtime and BER | Done | RuntimeTests, InteroperabilityTests (BER) |
| 7 | DER | Done | RuntimeTests/DerTests (X.690 examples, rejection of non-canonical input), DER vectors |
| 8 | PER and UPER | Done (BASIC-PER) | X.691 Annex A (8 encodings), UPER/APER vectors, PerTests (fragmentation, extensions, relay) |
| 9 | CER, OER, COER | Done | CER: DerTests (X.690 9.3 example). OER/COER: X.696 Annex A (both profiles), OER vectors (byte-exact, also through the strict COER decoder), RuntimeTests/OerTests (clause-by-clause known answers, rejection of non-canonical forms, relay), fuzzing |
| 10 | XER, JER | Done without encoding instructions (BASIC-XER, CXER, JER); EXTENDED-XER not implemented | XER: X.693 Annex A.3/A.4, XER vectors, RuntimeTests/XerTests (clause-by-clause encodings, encoder options, rejections incl. DTDs), fuzzing. JER: X.697 Annex A (A.3, all A.4 examples), JER vectors (canonicalised JSON trees), RuntimeTests/JerTests, fuzzing (found and fixed lazy UTF-8 validation) |
| 11 | Hardening and release | In progress | PropertyTests, FuzzTests, docs/security.md; packaging and benchmarks outstanding |

## Open items (prioritized)

1. Encoding instructions (a model for XER/JER instructions, which the parser currently skips with ASN1015), then
   EXTENDED-XER (X.693 clause 10) and the JER instructions (X.697 14–19).
2. PER and OER for the X.680 time types (X.691 clause 32, X.696 clause 29); CANONICAL-PER; the OER IEEE 754 REAL
   forms (X.696 12.2/12.3).
3. Relaying unknown ENUMERATED extension values (decoded as placeholders today, not re-encodable).
4. Automatic open-type decoding through table constraints (the model already records `TableConstraintInfo`).
5. Preservation of unknown SEQUENCE extension additions for relaying.
6. Parameterized information object classes and object-set parameters; object-set intersection/EXCEPT.
7. EXTERNAL, EMBEDDED PDV, CHARACTER STRING, OID-IRI, RELATIVE-OID-IRI in the runtime.
8. Decimal REAL abstract values (exact representation), ISO 2022 for Teletex/Videotex/Graphic/General strings.
9. MSBuild integration (`Asn1.Build`: generate C# during build), NuGet packaging, benchmarks.
