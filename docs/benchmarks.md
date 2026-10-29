# Benchmarks

`benchmarks/Asn1.Benchmarks` measures the runtime and the compiler with
[BenchmarkDotNet](https://benchmarkdotnet.org/).

## What is measured

| Class | Measures | Parameters |
|---|---|---|
| `CodecBenchmarks` | `Encode` (into a new array), `EncodeToBuffer` (into a reused `IBufferWriter`) and `Decode` of generated C# types | every encoding rule; 1 or 32 measurements |
| `DynamicCodecBenchmarks` | `Encode` and `Decode` through descriptors built from the compiled module (`DynamicTypeFactory`), as the CLI, DotAsn Studio and `@dotasn/wasm` do | BER, UPER, JER; 1 or 32 measurements |
| `CompilerBenchmarks` | `Compile` (parse and bind), `Lint` and `GenerateCSharp` | `telemetry` (the sample, 3 KB), `rfc5280` (X.509, 34 KB), `zforce` (45 KB) |

The codec benchmarks encode a `Message` of [samples/telemetry/telemetry.asn](../samples/telemetry/telemetry.asn) that
carries a `MeasurementBatch` with every OPTIONAL component present, including the `pressurePa` extension addition.
`protocolVersion` differs from its DEFAULT so no rule can omit it. The encodings are 40 (UPER) to 545 (XER) bytes for
1 measurement and 822 to 9,938 bytes for 32.

The project generates its C# types at build time: the `GenerateAsn1Types` target runs `asn1 generate-csharp` on the
schemas listed as `Asn1Schema` items and compiles the output from `obj/`. The benchmarks therefore always measure the
current generator's output.

Each `[GlobalSetup]` checks its result before anything is measured. The codec benchmarks check that the message
round-trips, the dynamic benchmarks that the dynamic encoding equals the generated one, and the compiler benchmarks
that the module compiles without errors and generates C#.

## Running

```sh
dotnet run -c Release --project benchmarks/Asn1.Benchmarks -- --filter '*'                    # everything (about an hour)
dotnet run -c Release --project benchmarks/Asn1.Benchmarks -- --filter '*' --job short        # quicker, less precise (about 11 min)
dotnet run -c Release --project benchmarks/Asn1.Benchmarks -- --filter '*CodecBenchmarks*'    # one class
dotnet run -c Release --project benchmarks/Asn1.Benchmarks -- --filter '*DynamicCodecBenchmarks*' \
    --inProcess --warmupCount 1 --iterationCount 3                                           # a quick check (about 1 min)
dotnet run -c Release --project benchmarks/Asn1.Benchmarks -- --filter '*' --list flat        # list without running
```

The filter matches benchmark names, so `--filter '*CodecBenchmarks.Decode*'` runs only the decode benchmarks.
Results go to `BenchmarkDotNet.Artifacts/` (not committed). CI runs every benchmark once with
`--job Dry --inProcess`, which exercises the setup checks but measures nothing; the process exits with 1 if any
benchmark fails.

To compare a change, run the affected class before and after it on the same machine. Use the default job, not
`--job short`, because short runs vary by about ±10%.

## Reference results

Measured on 2026-10-29 with .NET 10.0.13 on an AMD Ryzen 7 7735HS (Linux). "Before" is DotAsn 0.2.1 (ShortRun job);
"after" is the allocation-free runtime of the next release (in-process, 4 iterations). Treat them as indicative: the
gaps are far larger than the run-to-run variation. Times are per operation, memory is the managed allocation per
operation (for encoding, the result array is part of it).

### Generated types (a batch of 32 measurements)

| Rule | Encode, before → after | Decode, before → after | Allocated (encode), before → after |
|---|---|---|---|
| UPER | 67 → 15 µs | 68 → 18 µs | 78 → 0.8 KB |
| APER | 72 → 16 µs | 69 → 18 µs | 86 → 1.0 KB |
| OER | 60 → 14 µs | 51 → 18 µs | 94 → 1.0 KB |
| COER | 61 → 17 µs | 52 → 19 µs | 90 → 1.0 KB |
| BER | 32 → 16 µs | 52 → 27 µs | 36 → 1.4 KB |
| DER | 36 → 17 µs | 50 → 27 µs | 36 → 1.4 KB |
| CER | 34 → 17 µs | 50 → 28 µs | 36 → 1.6 KB |
| JER | 31 → 16 µs | 114 → 62 µs | 62 → 5.9 KB |
| XER | 62 → 35 µs | 130 → 74 µs | 172 → 57 KB |
| CXER | 59 → 37 µs | 197 → 117 µs | 172 → 57 KB |

The encodings are 822 (UPER) to 9,938 (XER) bytes. For comparison, asn1scc's generated C needs 6.4 µs (encode) and
5.9 µs (decode) for the same UPER message, on a non-extensible copy of the module.

### Dynamic descriptors (after)

| Rule | Encode, 1 | Encode, 32 | Decode, 1 | Decode, 32 | Allocated (encode, 32) |
|---|---:|---:|---:|---:|---:|
| BER | 1.6 µs | 30 µs | 2.2 µs | 39 µs | 38 KB |
| UPER | 1.3 µs | 24 µs | 1.9 µs | 36 µs | 8.5 KB |
| JER | 2.1 µs | 38 µs | 5.9 µs | 117 µs | 40 KB |

Before, these were 45 (BER), 88 (UPER) and 57 µs (JER) to encode 32 measurements. Dynamic values are boxed, so they
allocate more than generated types.

### Compiler

| Module | Compile | Lint | GenerateCSharp | Allocated (GenerateCSharp) |
|---|---:|---:|---:|---:|
| telemetry | 0.13 ms | 0.05 ms | 49 ms | 12.5 MB |
| rfc5280 | 1.3 ms | 0.44 ms | 347 ms | 74 MB |
| zforce | 1.0 ms | 0.24 ms | 285 ms | 63 MB |

### Where the time goes

* **Encoding and decoding allocate (almost) nothing but the values themselves.** The rules reuse their writers and
  readers per thread (when the options are the same instance), keep sequence presence in stack memory, pass
  length-prefixed content as structs instead of lambdas, and compute integers, lengths and indices with 64-bit
  arithmetic, falling back to `BigInteger` only for values that need it. What remains is mostly the per-component
  dispatch through the descriptors.
* **JER and XER decode about 2× more slowly than they encode.** The JER decoder parses the input into a
  `JsonDocument` first; the XER decoder builds an element tree with `XmlReader`, which alone costs about 11 KB per call.
* **C# generation takes 270–380× as long as compiling**, and it allocates 55–65 times as much. About 90% of it is
  `SourceFormatter.Format`, which lays out the Roslyn tree as text. Timing its phases inside `CSharpGenerator.Generate`
  gave `NormalizeWhitespace` 38–55%, the `Layout` rewriter that follows it 32–40% (it calls `ReplaceToken` for every
  member) and `ToFullString` 4–10%. Building the model and the syntax tree takes the remaining 9–12%.
