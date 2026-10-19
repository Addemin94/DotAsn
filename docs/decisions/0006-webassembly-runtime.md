# ADR 0006: The .NET runtime as WebAssembly (NativeAOT-LLVM) with a compiled schema

**Status:** accepted

## Context

Generated TypeScript (ADR 0005) needs an engine in browsers and Node.js. A TypeScript port of the encoding rules
duplicates every rule and can drift from the .NET implementation; one was written and then removed. The aim is to
reuse the C# runtime and publish it on npm, without shipping the compiler: code is generated with the CLI or in
DotAsn Studio.

## Decision

* The runtime is compiled to WebAssembly with **NativeAOT-LLVM** (dotnet/runtimelab, `Microsoft.DotNet.ILCompiler.LLVM`
  from the dotnet-experimental feed, Emscripten 3.1.56). The project is `js/wasm/native` and lives outside
  `Asn1Toolchain.sln`, because it needs the experimental feed and Emscripten.
* Types reach the module as a **DotAsn schema** (docs/schema-format.md). `Asn1SchemaWriter` writes it from the
  compilation, with tags, the complete constraint model, components and DEFAULT values. `Asn1Schema` builds the same
  descriptors as `DynamicTypeFactory` from it, without the compiler, so trimming removes the compiler from the module.
  The CLI (`generate-schema`, and `dotasn-schema.ts` from `generate-typescript`) and Studio produce schemas.
* The exports are `[UnmanagedCallersOnly]` functions (Exports.cs) that pass UTF-8 buffers. Values cross the boundary
  in the JSON value notation, which is lossless. Results and errors (as JSON with type, kind, path and bit offset) are
  read from a result buffer.
* `@dotasn/wasm` wraps the module. Its rules (`encode(type, value)`, `decode(type, bytes)`) convert typed values with
  the generated descriptors and the value notation of `@dotasn/runtime`.

## Consequences

* JavaScript runs the exact .NET code: ten rules, full constraint validation, identical errors and limits.
  `SchemaVectorTests` (.NET) and the vector suite of `@dotasn/wasm` check the schema path against every vector.
* The module is about 1.1 MB compressed (brotli). It is trimmed with feature switches: no XML networking, stack-trace
  data, EventSource or debugger support. Loading is asynchronous (one `await loadDotAsn(schema)`).
* NativeAOT-LLVM is experimental, and the compiler version is pinned in `Directory.Packages.props`. Builds need Linux
  x64 or Windows x64 and Emscripten. CI installs both.
* A schema belongs to the DotAsn version that wrote it; the format version is checked when loading.
* The JSON value notation gained a readable form of unknown CHOICE alternatives, including the PER extension index,
  so that relaying works across the boundary.
