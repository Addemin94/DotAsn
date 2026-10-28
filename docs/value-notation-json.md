# JSON value notation (CLI)

`asn1 encode` reads and `asn1 decode` writes values in a lossless JSON notation that follows the ASN.1 type:

| ASN.1 | JSON |
|---|---|
| BOOLEAN | `true` / `false` |
| INTEGER | number; values beyond ±(2^53−1) are written as decimal strings. Input accepts either form, parsed exactly. |
| ENUMERATED | the identifier as a string, e.g. `"accepted"` |
| REAL | number, or `"PLUS-INFINITY"`, `"MINUS-INFINITY"`, `"NOT-A-NUMBER"`, `"-0"` |
| NULL | `null` |
| BIT STRING | `{"hex": "B0", "length": 4}`; input also accepts `"'1011'B"`, `"'B0'H"` and arrays of named bits |
| OCTET STRING | hexadecimal string `"0A1B"`; input also accepts `{"base64": "..."}` |
| OBJECT IDENTIFIER / RELATIVE-OID | dotted string `"1.2.840.113549"` |
| character strings, time types | string |
| SEQUENCE / SET | object with the present components; absent OPTIONAL components are omitted |
| SEQUENCE OF / SET OF | array |
| CHOICE | object with exactly one member: `{"alternative": value}` |
| open type | `{"encoding": "BER", "hex": "..."}` |
| unknown CHOICE extension (decoded from a later version) | `{"$unknownExtension": {"encoding": "UPER", "hex": "...", "index": 1}}` (`index`: the PER extension index, when known); accepted as input so that it can be relayed with the same rule |

Decoders fill in DEFAULT values, so a decoded value can contain components that were omitted on input.
