# Standards support matrix

Editions: ITU-T X.680, X.681, X.682, X.683 (02/2021) | ISO/IEC 8824-1..4:2021; X.690, X.691, X.693, X.696, X.697
(02/2021) | ISO/IEC 8825-x:2021.

Status legend: **Tested**: implemented and covered by automated tests. **Limited**: implemented with known
limitations. **Partial**: some forms only. **No**: not supported.

## Notation (X.680–X.683)

| Feature | Clause | Status | Tests |
|---|---|---|---|
| Lexical items, comments, cstring/bstring/hstring | X.680 12 | Tested | `ParserTests/LexerTests` |
| Module definition, OID/IRI, tag default, EXTENSIBILITY IMPLIED | X.680 13 | Tested | `ParserTests`, `SemanticTests/TaggingTests` |
| EXPORTS / IMPORTS, module-qualified references | X.680 13–14 | Tested | `SemanticTests/ResolutionTests` |
| BOOLEAN, INTEGER (named numbers), ENUMERATED (root, additions, value assignment) | X.680 18–20 | Tested | `ResolutionTests`, interop vectors |
| REAL | X.680 21 | Limited | modelled as IEEE 754 binary64 |
| BIT STRING (named bits), OCTET STRING, NULL | X.680 22–24 | Tested | |
| SEQUENCE, SET (extension markers, second root part, `[[ ]]` groups, COMPONENTS OF, OPTIONAL, DEFAULT) | X.680 25–28 | Tested | |
| Automatic tagging (root parts first, then additions) | X.680 25.3–25.10 | Tested | `TaggingTests` |
| Tag distinctness rules (SEQUENCE, SET, CHOICE, extensions) | X.680 25.6–25.7, 27.3, 29.3 | Tested | `TaggingTests` |
| CHOICE, selection types | X.680 29–30 | Tested | |
| Tagged types, IMPLICIT/EXPLICIT/AUTOMATIC rules incl. CHOICE/open type exceptions | X.680 31 | Tested | `TaggingTests` |
| OBJECT IDENTIFIER, RELATIVE-OID values (name forms, references) | X.680 32–33 | Tested | `ValueAndConstraintTests` |
| OID-IRI, RELATIVE-OID-IRI | X.680 34–35 | Partial | parsed; not encodable |
| EMBEDDED PDV, EXTERNAL, CHARACTER STRING | X.680 36–37, 44 | Partial | parsed; not encodable |
| Restricted character strings, Quadruple/Tuple, character lists | X.680 41 | Tested | |
| UTCTime, GeneralizedTime, ObjectDescriptor | X.680 45–48 | Tested | DER canonical forms |
| DATE, TIME-OF-DAY, DATE-TIME, DURATION, TIME | X.680 38 | Limited | syntax validation; BER/DER string encoding; no PER/OER |
| Value notation (all types, braced values, open type values) | X.680 | Tested | `ValueAndConstraintTests` |
| Value set assignments | X.680 16 | Tested | |
| Single value, contained subtype (root only, 52.4), value range (open endpoints), SIZE, FROM, PATTERN, WITH COMPONENT(S), union/intersection/EXCEPT/ALL EXCEPT, extensibility (50.4, 50.11) | X.680 49–52 | Tested | `ValueAndConstraintTests`, `CoreTests` |
| PATTERN regular expressions | X.680 Annex A | Partial | `#n`, `#(n,m)`, classes; no `\N{}` names |
| SETTINGS (property settings) | X.680 51.11 | Partial | recorded, not enforced |
| Information object classes, WITH SYNTAX, objects, object sets | X.681 | Limited | no parameterized classes; set intersection/EXCEPT unsupported |
| TYPE-IDENTIFIER, ABSTRACT-SYNTAX, INSTANCE OF | X.681 Annex A–C | Tested | |
| Table and component relation constraints | X.682 10 | Limited | resolved and recorded; open types decode to raw contents |
| User-defined constraints, contents constraints | X.682 9, 11 | Partial | recorded, not enforced |
| Parameterized types, values and value sets (recursive instances memoised) | X.683 | Tested | `ResolutionTests` |
| Class, object and object-set parameters | X.683 | No | reported as ASN4001 |
| X.208 ANY / ANY DEFINED BY, MACRO | withdrawn | Partial | ANY as open type; MACRO skipped with a warning |
| ENCODING-CONTROL sections, encoding instructions | X.680 54 | No | skipped with warning ASN1015 |

## Encoding rules

| Rule | Standard | Status | Evidence |
|---|---|---|---|
| BER | X.690 8 | Tested | asn1tools vectors (decode both ways), constructed/indefinite forms, X.690 examples |
| CER | X.690 9, 11 | Tested | X.690 9.3 SET example, fragmentation, strict decoding |
| DER | X.690 10, 11 | Tested | asn1tools vectors (bit-exact), X.690 examples, rejection of BER-only forms |
| BASIC-PER ALIGNED (APER) | X.691 | Tested | X.691 Annex A.1–A.4, asn1tools vectors (bit-exact), fragmentation |
| BASIC-PER UNALIGNED (UPER) | X.691 | Tested | X.691 Annex A.1–A.4, asn1tools vectors (bit-exact), fragmentation |
| CANONICAL-PER | X.691 | No | |
| BASIC-OER | X.696 | Tested | X.696 Annex A, asn1tools vectors (byte-exact), hand-checked clause-by-clause cases |
| CANONICAL-OER | X.696 31 | Tested | X.696 Annex A, asn1tools vectors through the strict decoder, rejection of BASIC-only forms |
| BASIC-XER | X.693 8 | Tested | X.693 Annex A.3, asn1tools vectors (normalised XML trees and decoding), clause-by-clause cases |
| CANONICAL-XER | X.693 9 | Tested | X.693 Annex A.4 (byte-exact), strict decoding by canonical re-encoding |
| EXTENDED-XER, XER encoding instructions | X.693 10–39 | No | instructions parsed and ignored with warning ASN1015 |
| JER (without encoding instructions) | X.697 | Tested | X.697 Annex A (A.3 and all A.4 examples), asn1tools vectors (canonicalised JSON trees), sender's-option and rejection tests |
| JER encoding instructions | X.697 14–19 | No | parsed and ignored with warning ASN1015 |

## Per-type encoding support

| Type | BER | CER | DER | APER/UPER | OER/COER | JER | XER/CXER |
|---|---|---|---|---|---|---|---|
| BOOLEAN, INTEGER, ENUMERATED, NULL | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| REAL (binary64) | ✓ (decodes decimal too) | ✓ | ✓ | ✓ | ✓ (DER form; IEEE forms ✗) | ✓ (decodes base10Value) | ✓ |
| BIT STRING, OCTET STRING | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ ("containing" form ✗) | ✓ (typed form ✗) |
| OBJECT IDENTIFIER, RELATIVE-OID | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ (decodes name forms) |
| Known-multiplier strings (Numeric, Printable, Visible, IA5, BMP, Universal) | ✓ | ✓ | ✓ | ✓ (permitted alphabets) | ✓ | ✓ | ✓ |
| UTF8String, Teletex, Videotex, Graphic, General, ObjectDescriptor | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ (CXER: CR becomes LF) |
| UTCTime, GeneralizedTime | ✓ | ✓ (canonical) | ✓ (canonical) | ✓ (canonical) | ✓ (COER: canonical) | ✓ | ✓ (CXER: canonical) |
| DATE, TIME-OF-DAY, DATE-TIME, DURATION, TIME | ✓ (string form) | ✓ | ✓ | ✗ | ✗ | ✓ | ✓ (CXER: no intervals/durations) |
| SEQUENCE, SET (extensions, groups, defaults) | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| SEQUENCE OF, SET OF | ✓ | ✓ | ✓ (sorted) | ✓ | ✓ (COER: sorted) | ✓ | ✓ (sorted) |
| CHOICE (extensions, unknown alternatives kept for relay) | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| Open types | raw | raw | raw | raw | raw | raw JSON | raw XML |
| EXTERNAL, EMBEDDED PDV, CHARACTER STRING | ✗ | ✗ | ✗ | ✗ | ✗ | ✗ | ✗ |

## Deviations of the reference implementation

The interoperability vectors are produced by asn1tools 0.169.0. Where it deviates from the ITU-T texts, DotAsn
follows the text. The affected vectors are checked by round trip and by the X.691 Annex A examples instead
(see `tests/Asn1.InteroperabilityTests/X690VectorTests.cs`):

| Topic | Clause |
|---|---|
| Automatic tags: root part 2 is numbered before extension additions | X.680 25.10 |
| EXTENSIBILITY IMPLIED applies to ENUMERATED | X.680 13.4 |
| DER SET components in tag order, SET OF elements in encoding order | X.690 10.3, 11.6 |
| PER CHOICE indices in canonical tag order | X.691 23.2 |
| An empty PER encoding is one zero octet | X.691 11.1.3 |
| A known-multiplier string with a fixed effective size (`NameString (SIZE(1))`) has no OER length | X.696 27.2 |
| CANONICAL-OER SET OF elements in encoding order | X.696 31.8 |
| An OER extension addition group starts with a presence bitmap for its OPTIONAL components | X.696 16.5.2 |
