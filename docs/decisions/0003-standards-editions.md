# ADR 0003: Target standards editions and interpretation sources

**Status:** accepted

DotAsn targets the 02/2021 editions of ITU-T X.680, X.681, X.682, X.683 (| ISO/IEC 8824-1..4:2021) and X.690, X.691,
X.693, X.696 and X.697 (| ISO/IEC 8825-x:2021). These are the in-force editions listed by the ITU-T (verified October 2026).

Where the behaviour of the reference implementation used for interoperability vectors (asn1tools 0.169.0) differs
from the text, the text wins, and the deviation is recorded in the interoperability tests:

| Topic | Standard | asn1tools behaviour |
|---|---|---|
| Automatic tag numbers with a second root part | X.680 25.10 c/d: both root parts are numbered first, then extension additions | numbers components in textual order |
| DER SET component order | X.690 10.3: ascending tag order | textual order |
| DER SET OF element order | X.690 11.6: ascending order of encodings | value order |
| PER CHOICE index order | X.691 23.2: canonical tag order of the root alternatives | textual order |
| ENUMERATED in a module with EXTENSIBILITY IMPLIED | X.680 13.4: the extension marker is implied | not extensible |
| Empty PER encoding | X.691 11.1.3: a single zero octet | no octets (so does asn1scc); decoded too, and sent with `EmptyPerEncodingAsZeroOctet = false` |
| OER fixed-size string after serial constraints (`NameString (SIZE(1))`) | X.696 27.2: no length determinant | emits a length determinant |
| OER extension addition group with OPTIONAL components | X.696 16.5.2: encoded as a sequence in an open type, starting with a presence bitmap | omits the presence bitmap |
| CANONICAL-OER SET OF element order | X.696 31.8: ascending order of encodings | value order (asn1tools implements BASIC-OER only) |
