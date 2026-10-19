# Samples

| Directory | Shows |
|---|---|
| `basic/` | SEQUENCE, OPTIONAL/DEFAULT, ENUMERATED, value assignments |
| `recursive-types/` | recursive and mutually recursive types |
| `constraints/` | value ranges, extensible constraints, SIZE, FROM, contained subtypes, WITH COMPONENTS |
| `extensibility/` | extension markers, `[[ ]]` groups, second root part, extensible CHOICE and ENUMERATED |
| `protocol-messages/` | an information object class, an object set, and a table-constrained open type |
| `telemetry/` | a compact UPER device protocol: extensible enumerations, sequences and choices, an extension group, DEFAULT values, SIZE constraints, named bits |

Every sample can also be loaded in [DotAsn Studio](https://addemin94.github.io/DotAsn/) (*Schema > Load sample*),
which compiles, encodes and decodes in the browser without installing anything.

With the [`asn1` CLI](../README.md#install):

```sh
asn1 lint samples --encoding-rules uper
asn1 generate-csharp samples/extensibility -o generated
asn1 encode samples/basic/basic.asn -t Person -f uper --value '{"name":"Ada","roles":["admin"]}'
```
