# ADR 0005: TypeScript generation

**Status:** accepted

## Context

Users want to exchange ASN.1-encoded messages from browsers and Node.js with the same specifications they compile
for .NET, with typed values in TypeScript.

## Decision

* `asn1 generate-typescript` (`src/Asn1.CodeGen.TypeScript`) emits one TypeScript module per ASN.1 module. Each type
  becomes a TypeScript type (interface, union or alias) and a descriptor constant of the same name. Like the C#
  generator (ADR 0004), it builds a syntax model (`TsSyntax.cs`) that `TsPrinter` prints, never string templates.
* Descriptors describe only the shape of the values: kinds, names, components (and whether they may be absent),
  alternatives, enumeration identifiers, named bits, and whether an INTEGER is a `bigint`. `@dotasn/runtime`
  (`js/runtime`) uses them to convert values to and from the JSON value notation. Encoding facts (tags,
  constraints, DEFAULT values) go into the schema that the generator also emits (`dotasn-schema.ts`). The engine,
  `@dotasn/wasm`, uses that schema (ADR 0006).
* Values are plain data:
  * INTEGER is `number` when every permitted value is a safe integer, otherwise `bigint`.
  * ENUMERATED is a union of string literals, and CHOICE is a single-property object.
  * OCTET STRING is `Uint8Array`, and BIT STRING is `{ bytes, length }`.
  * Unknown extensions are `{ $unknown }` and `{ $unknownExtension }`.
* References to other types are thunks (`() => T`), so recursive and out-of-order definitions work without
  initialisation-order problems.

## Consequences

* Generated code does not depend on an encoding engine. It is type-checked with `tsc --strict` for every vector
  specification in CI.
* An earlier version of this decision included a TypeScript port of seven encoding rules. ADR 0006 replaced it with
  the .NET runtime compiled to WebAssembly, so that each rule exists once.
