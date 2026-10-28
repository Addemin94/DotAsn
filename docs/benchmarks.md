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
dotnet run -c Release --project benchmarks/Asn1.Benchmarks -- --filter '*' --list flat        # list without running
```

The filter matches benchmark names, so `--filter '*CodecBenchmarks.Decode*'` runs only the decode benchmarks.
Results go to `BenchmarkDotNet.Artifacts/` (not committed). CI runs every benchmark once with
`--job Dry --inProcess`, which exercises the setup checks but measures nothing; the process exits with 1 if any
benchmark fails.

To compare a change, run the affected class before and after it on the same machine. Use the default job, not
`--job short`, because short runs vary by about ±10%.

## Reference results

These are ShortRun numbers (3 iterations), so treat them as indicative. They were measured on 2026-10-29 with
DotAsn 0.2.1, .NET 10.0.13 and an AMD Ryzen 7 7735HS on Linux. Times are per operation, and memory is the managed
allocation per operation.

### Generated types

| Rule | Encode, 1 | Encode, 32 | Decode, 1 | Decode, 32 | Allocated (encode, 32) |
|---|---:|---:|---:|---:|---:|
| BER | 2.3 µs | 32 µs | 3.2 µs | 52 µs | 36 KB |
| CER | 2.3 µs | 34 µs | 3.3 µs | 50 µs | 36 KB |
| DER | 2.3 µs | 36 µs | 3.2 µs | 50 µs | 36 KB |
| APER | 3.6 µs | 72 µs | 3.6 µs | 69 µs | 86 KB |
| UPER | 3.5 µs | 67 µs | 3.6 µs | 68 µs | 78 KB |
| OER | 3.0 µs | 60 µs | 2.8 µs | 51 µs | 94 KB |
| COER | 3.2 µs | 61 µs | 3.2 µs | 52 µs | 90 KB |
| JER | 2.8 µs | 31 µs | 6.4 µs | 114 µs | 62 KB |
| XER | 4.3 µs | 62 µs | 9.5 µs | 130 µs | 172 KB |
| CXER | 3.9 µs | 59 µs | 14.6 µs | 197 µs | 172 KB |

### Dynamic descriptors

| Rule | Encode, 1 | Encode, 32 | Decode, 1 | Decode, 32 | Allocated (encode, 32) |
|---|---:|---:|---:|---:|---:|
| BER | 2.7 µs | 45 µs | 3.6 µs | 62 µs | 101 KB |
| UPER | 4.4 µs | 88 µs | 4.4 µs | 80 µs | 170 KB |
| JER | 3.6 µs | 57 µs | 7.3 µs | 135 µs | 124 KB |

### Compiler

| Module | Compile | Lint | GenerateCSharp | Allocated (GenerateCSharp) |
|---|---:|---:|---:|---:|
| telemetry | 0.13 ms | 0.05 ms | 49 ms | 12.5 MB |
| rfc5280 | 1.3 ms | 0.44 ms | 347 ms | 74 MB |
| zforce | 1.0 ms | 0.24 ms | 285 ms | 63 MB |

### What stands out

* **PER and OER encode more slowly than BER and JER**, and they allocate 2.4–2.9 KB per measurement, against 1.1 KB
  for BER. See open item 10 in [implementation-checklist.md](implementation-checklist.md).
* **JER and XER decode 2–4× more slowly than they encode.** The JER decoder parses the input into a `JsonDocument`
  first, and the XER decoder builds its own element tree.
* **C# generation takes 270–380× as long as compiling**, and it allocates 55–65 times as much. About 90% of it is
  `SourceFormatter.Format`, which lays out the Roslyn tree as text. Timing its phases inside `CSharpGenerator.Generate`
  gave `NormalizeWhitespace` 38–55%, the `Layout` rewriter that follows it 32–40% (it calls `ReplaceToken` for every
  member) and `ToFullString` 4–10%. Building the model and the syntax tree takes the remaining 9–12%.
