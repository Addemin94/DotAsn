# DotAsn schema format

A DotAsn schema describes the types of a compiled ASN.1 specification. Runtimes that do not contain the compiler use
it to build their descriptors; the WebAssembly runtime [`@dotasn/wasm`](../js/wasm/README.md) is one. Producers:

* `asn1 generate-schema <specs> -o schema.json`;
* `asn1 generate-typescript`, which writes the schema as a string in `dotasn-schema.ts`;
* DotAsn Studio (TypeScript tab);
* `Asn1SchemaWriter.Write(compilation)` in `DotAsn.Dynamic`.

`Asn1Schema.Parse` reads it and yields the same descriptors as `DynamicTypeFactory`. `SchemaVectorTests` checks this
by running every asn1tools vector through both paths.

A schema is generated output, like generated code. Regenerate it with the DotAsn version whose runtime loads it; a
runtime rejects other format versions.

## Document

```json
{
  "format": "dotasn-schema",
  "version": 1,
  "names": { "Telemetry-Protocol.Heartbeat": 1 },
  "types": [ { "kind": "...", "name": "..." } ]
}
```

| Member | Meaning |
|---|---|
| `names` | qualified type assignment names (`Module.Type`) mapped to indices into `types` |
| `types` | type nodes: one per type occurrence (an assignment, or a component's type with its own tags and constraints). Nodes refer to each other by index, so recursive types are cycles. |

## Type nodes

Every node has `kind` and `name`. Optional members are `tags` (outermost first, each `[class, number, explicit]`,
where class is 0 universal, 1 application, 2 context-specific or 3 private) and `constraints` (an array of
constraints, applied in order).

| `kind` | Further members |
|---|---|
| `boolean`, `real`, `octetString`, `null`, `objectIdentifier`, `relativeOid`, `open` | none |
| `integer`, `bitString` | `named`: `[[identifier, number], …]` |
| `enumerated` | `items`: `[[identifier, number, isExtensionAddition], …]`, `ext` |
| `string` | `stringKind`: `utf8`, `numeric`, `printable`, `teletex`, `videotex`, `ia5`, `graphic`, `visible`, `general`, `universal`, `bmp` or `objectDescriptor` |
| `time` | `timeKind`: `utcTime`, `generalizedTime`, `date`, `timeOfDay`, `dateTime`, `duration` or `time` |
| `sequence`, `set` | `ext`, `components` (below) |
| `choice` | `ext`, `alternatives`: `[{name, type, addition?, group?}, …]` |
| `sequenceOf`, `setOf` | `element` (node index), `elementName` (optional) |
| `unsupported` | `reason`; using the type fails (EXTERNAL, EMBEDDED PDV, CHARACTER STRING, OID-IRI types) |

A component is `{name, type, presence?, default?, addition?, group?, part2?}`:

* `presence` is `optional` or `default`; when it is absent, the component is required.
* `default` is the DEFAULT value in the [JSON value notation](value-notation-json.md) of the component's type.
* `addition` is the extension addition index; `group` marks members of a `[[ ]]` group; `part2` marks root
  components after the second extension marker.

## Constraints

A constraint is `{root?, ext?, add?}`: the extension root, the extension marker and the extension additions.
Elements are arrays tagged by their first item:

| Element | Form |
|---|---|
| all values | `["all"]` |
| single value | `["single", value]` |
| value range | `["range", lower, upper, lowerOpen, upperOpen]`; `null` bounds are MIN/MAX |
| SIZE | `["size", constraint]` |
| FROM | `["from", constraint]` |
| PATTERN | `["pattern", "regex"]` |
| WITH COMPONENT | `["withComponent", constraint]` |
| WITH COMPONENTS | `["withComponents", partial, [[name, constraint or null, "present"/"absent"/"optional"/"none"], …]]` |
| union, intersection | `["union", [elements]]`, `["intersection", [elements]]` |
| EXCEPT | `["except", included, excluded]` |
| nested constraint | `["nested", constraint, isContainedSubtype]` |
| not evaluated | `["opaque", kind, notation]` (table, user-defined, contents and similar constraints) |

A value is a one-member object that names its representation: `{"int": "123"}` (decimal text),
`{"bool": true}`, `{"real": 1.5}` (or `"INF"`, `"-INF"`, `"NaN"`, `"-0"`), `{"null": null}`, `{"str": "..."}`,
`{"bytes": "hex"}`, `{"bits": ["hex", length]}`, `{"oid": "1.2.3"}` or `{"relOid": "4.5"}`.
