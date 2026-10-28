# Using DotAsn from TypeScript

This guide takes you from an ASN.1 specification to encoding and decoding messages in TypeScript, in Node.js or in a
browser app. The encoder is the DotAsn .NET runtime compiled to WebAssembly (`@dotasn/wasm`). It produces the same
bytes as the .NET packages and the `asn1` CLI, for BER, CER, DER, APER, UPER, OER, COER, JER, XER and CXER.

## 1. Generate code from your specification

Use the `asn1` CLI ([releases](https://github.com/Addemin94/DotAsn/releases); no .NET needed) or the TypeScript tab
of [DotAsn Studio](https://addemin94.github.io/DotAsn/):

```sh
asn1 generate-typescript specs/ --output src/generated
```

This writes:

* one file per ASN.1 module (e.g. `TelemetryProtocol.ts`), with a TypeScript type and a descriptor constant of the
  same name for every ASN.1 type;
* `dotasn-schema.ts`, the compiled description of your types that the WebAssembly runtime loads.

Commit the generated files or generate them in your build, and regenerate them whenever the specification changes.
The schema must come from the same DotAsn version as `@dotasn/wasm`.

## 2. Install the packages

```sh
npm install @dotasn/runtime @dotasn/wasm
```

`@dotasn/runtime` (15 KB) holds the types used by the generated code. `@dotasn/wasm` holds the runtime itself
(about 1.1 MB compressed).

> Until the packages are published, build them from a clone of the repository and install the tarballs:
> `cd js && npm ci && npm run build:native -w @dotasn/wasm && npm pack --workspaces`
> (building the WebAssembly module needs Emscripten 3.1.56, see [js/wasm/README.md](../js/wasm/README.md)).

## 3. Configure TypeScript

The packages are ES modules targeting ES2022. A typical `tsconfig.json` for Node.js:

```json
{
  "compilerOptions": {
    "target": "ES2022",
    "module": "NodeNext",
    "moduleResolution": "NodeNext",
    "strict": true,
    "lib": ["ES2022", "DOM"]
  }
}
```

Generated imports end in `.js`, as `NodeNext` requires. With a bundler you may prefer
`--import-extension none` and `"moduleResolution": "bundler"`. Keep `"DOM"` (or `@types/node`) in `lib` for the
`WebAssembly` types.

## 4. Encode and decode

```ts
import { loadDotAsn } from '@dotasn/wasm';
import { schema } from './generated/dotasn-schema.js';
import { Heartbeat, Message } from './generated/TelemetryProtocol.js';

// Load once at startup and reuse; this instantiates the module and loads the schema.
const asn1 = await loadDotAsn(schema);

const heartbeat: Heartbeat = { sequenceNumber: 7, uptimeSeconds: 4_000_000_000 };
const bytes = asn1.uper.encode(Heartbeat, heartbeat);    // Uint8Array 07 EE 6B 28 00
const decoded = asn1.uper.decode(Heartbeat, bytes);       // typed as Heartbeat

const message: Message = { messageType: 'heartbeat', payload: { heartbeat } };
const der = asn1.der.encode(Message, message);
```

The first argument is the descriptor, which the generator emits under the same name as the type. The value is
checked against the TypeScript type at compile time and against the ASN.1 constraints at run time.

Values follow the specification:

* SEQUENCE is an object, and OPTIONAL/DEFAULT components are optional properties.
* CHOICE is `{ alternative: value }`.
* ENUMERATED is a string.
* INTEGERs whose range does not fit ±(2^53 − 1) are `bigint`.
* OCTET STRING is a `Uint8Array`, and BIT STRING is `{ bytes, length }`.

[js/runtime/README.md](../js/runtime/README.md) has the complete table.

## 5. Handle errors

```ts
import { Asn1DecodeError, Asn1EncodeError } from '@dotasn/wasm';

try {
  asn1.uper.decode(Heartbeat, untrustedBytes, { maxDepth: 32 });
} catch (e) {
  if (e instanceof Asn1DecodeError) {
    console.log(e.kind, e.path, e.bitOffset);   // e.g. 'truncated', '', 8
  }
}

asn1.validate(Heartbeat, { sequenceNumber: 300, uptimeSeconds: 1 });   // [{ path: 'sequenceNumber', message: '...' }]
```

The errors work as follows:

* Encoding a value that breaks a constraint throws `Asn1EncodeError` with the component `path`.
* Decoding throws `Asn1DecodeError`, unless the input is complete, valid and within the limits.
* The limits are `maxDepth`, `maxCollectionCount` and `maxStringLength`, plus `rejectTrailingData` (default `true`).

## 6. Run it

**Node.js:** compile with `tsc` and run the output, or run the `.ts` directly with Node 22.18 or later. For direct
runs, generate with `--import-extension .ts`.

**Browsers:** bundlers that understand `new URL('…', import.meta.url)` find and copy `dotasn.wasm` automatically.
These include Vite, webpack 5 and Rollup; for example `vite build` needs no configuration. The module must be served
as `application/wasm`. To host the file yourself, pass its location:

```ts
const asn1 = await loadDotAsn(schema, { wasm: '/assets/dotasn.wasm' });
```

`loadDotAsn` is asynchronous; top-level `await` needs `"target": "ES2022"` or later (or call it from an async
function).

## 7. Without generated types

Plain JavaScript or dynamic tools can skip the TypeScript and address types by name, with values in the
[JSON value notation](value-notation-json.md):

```sh
asn1 generate-schema specs/ -o schema.json
```

```js
const asn1 = await loadDotAsn(await (await fetch('schema.json')).text());
const bytes = asn1.encodeNotation('Heartbeat', 'UPER', { sequenceNumber: 7, uptimeSeconds: 4000000000 });
asn1.decodeNotation('Heartbeat', 'UPER', bytes);   // { sequenceNumber: 7, uptimeSeconds: 4000000000 }
```

## Further reading

* [js/wasm/README.md](../js/wasm/README.md): loading options, all rules, building the module.
* [js/runtime/README.md](../js/runtime/README.md): generator options and the value model.
* [schema-format.md](schema-format.md): what the schema contains.
