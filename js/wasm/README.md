# @dotasn/wasm

The [DotAsn](https://github.com/Addemin94/DotAsn) .NET runtime compiled to WebAssembly with
[NativeAOT-LLVM](https://github.com/dotnet/runtimelab/tree/feature/NativeAOT-LLVM). It encodes and decodes **BER, CER,
DER, APER, UPER, OER, COER, JER, XER and CXER** in browsers, workers and Node.js. It is the exact code that the .NET
packages and the `asn1` CLI run, so it shares their encodings, constraint checks and error behaviour.

The compiler is not included. You generate code once, with the `asn1` CLI or in
[DotAsn Studio](https://addemin94.github.io/DotAsn/). The module then loads a compact **schema** of your types: the
tags, the full constraint model, components and DEFAULT values.

For a step-by-step introduction, see the
[TypeScript guide](https://github.com/Addemin94/DotAsn/blob/main/docs/typescript.md).

## Generate

```sh
asn1 generate-typescript samples/telemetry --output src/generated
```

This writes the TypeScript types and descriptors (one file per ASN.1 module) and `dotasn-schema.ts`, which exports
the schema. Studio's TypeScript tab produces the same files. Without TypeScript, `asn1 generate-schema samples/telemetry -o
schema.json` writes just the schema.

## Use

```ts
import { loadDotAsn } from '@dotasn/wasm';
import { schema } from './generated/dotasn-schema.js';
import { Heartbeat, Message } from './generated/TelemetryProtocol.js';

const asn1 = await loadDotAsn(schema);           // instantiates the module once, then loads the schema

const heartbeat: Heartbeat = { sequenceNumber: 7, uptimeSeconds: 4_000_000_000 };
const bytes = asn1.uper.encode(Heartbeat, heartbeat);          // 07EE6B2800
const back = asn1.uper.decode(Heartbeat, bytes);
asn1.der.encode(Message, { messageType: 'heartbeat', payload: { heartbeat } });
asn1.xer.encode(Heartbeat, heartbeat);                         // <Heartbeat>…</Heartbeat>
asn1.validate(Heartbeat, { sequenceNumber: 300, uptimeSeconds: 1 });  // [{ path: 'sequenceNumber', message: … }]
```

The rules are `ber`, `cer`, `der`, `aper`, `uper`, `oer`, `coer`, `jer`, `xer` and `cxer`. Each has
`encode(type, value)` and `decode(type, bytes, options?)`. Values are typed by the generated code; the value model
(`bigint` for large integers, `Uint8Array` for OCTET STRING and so on) is described in
[`@dotasn/runtime`](../runtime/README.md), the support library of the generated code.

Without generated TypeScript, values in the
[JSON value notation](https://github.com/Addemin94/DotAsn/blob/main/docs/value-notation-json.md) are addressed by type
name:

```js
const asn1 = await loadDotAsn(await (await fetch('schema.json')).text());
const bytes = asn1.encodeNotation('Heartbeat', 'UPER', { sequenceNumber: 7, uptimeSeconds: 4000000000 });
asn1.decodeNotation('Telemetry-Protocol.Heartbeat', 'UPER', bytes);
```

## Errors and limits

Failures throw these errors (all extend `Asn1Error` of `@dotasn/runtime`):

* `Asn1DecodeError` has a `kind` (`truncated`, `malformed`, `nonCanonical`, `limitExceeded`, …), a `path` and a bit
  offset.
* `Asn1EncodeError` is raised for invalid values and constraint violations, with the component `path`.
* `Asn1NotationError` is raised for values that do not fit the type.

Decoders take `{ rejectTrailingData, maxDepth, maxCollectionCount, maxStringLength, validateConstraints }`; the defaults
are the .NET defaults.

## Loading the module

`lib/dotasn.wasm` sits next to its loader and is found with `import.meta.url`. Bundlers that understand
`new URL(…, import.meta.url)` (Vite, webpack 5, Rollup, esbuild with a plugin) copy it automatically. Otherwise pass
it yourself:

```ts
await loadDotAsn(schema, { wasm: '/assets/dotasn.wasm' });            // URL
await loadDotAsn(schema, { wasm: await (await fetch(url)).arrayBuffer() });
await loadDotAsn(schema, { wasm: compiledModule });                     // a WebAssembly.Module
```

The file is also exported as `@dotasn/wasm/dotasn.wasm`. Every schema loaded without options shares one instance. The
module is about 1.1 MB compressed (brotli) and starts in about 20 ms; an encode takes about 10 µs (Node 26).

## Building

The module is built from `native/` (`Asn1.Wasm.csproj` and `Exports.cs`). The build needs:

* the .NET 10 SDK on Linux x64 or Windows x64;
* the NativeAOT-LLVM packages from the dotnet-experimental feed (`native/nuget.config`);
* Emscripten 3.1.56 (`./emsdk install 3.1.56 && ./emsdk activate 3.1.56`).

```sh
npm run build:native   # → lib/dotasn.wasm, lib/dotasn.js (finds emcc on PATH, $EMSDK or ../../.cache/emsdk)
npm run typecheck
npm test               # the asn1tools vectors through the module (uses the runtime package's generated fixtures)
npm run build
```

NativeAOT-LLVM is an experimental Microsoft project. The package pins the compiler version in
`Directory.Packages.props`.

MIT licensed.
