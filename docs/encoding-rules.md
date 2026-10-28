# Encoding rules

All rules implement `Asn1EncodingRule` and are exposed through `Asn1EncodingRules` (`src/Asn1.Serialization`).

## BER (X.690 clause 8)

* Encoding: definite lengths in minimal form, primitive strings, textual component order, DEFAULT values omitted.
  `new BerEncodingRule { UseIndefiniteLength = true, EncodeDefaultValues = true }` selects other valid forms.
* Decoding accepts every BER form: high tag numbers, long and indefinite lengths, constructed (fragmented) strings,
  any order of SET components, unknown extension additions (skipped) and unknown CHOICE alternatives (kept raw when
  the CHOICE is extensible).
* Always rejected, because they are invalid BER: non-minimal INTEGER contents, high-tag form for numbers below 31,
  leading 0x80 in tags and OID subidentifiers, reserved length octet 0xFF, indefinite length on primitives, and
  unused bits greater than 7.

## CER (X.690 clauses 9 and 11)

Indefinite lengths for constructed encodings; strings longer than 1000 octets fragmented into 1000-octet primitive
segments; SET components in canonical tag order with untagged CHOICEs ordered by their smallest tag (9.3); SET OF
sorted; plus the restrictions of clause 11. Decoding enforces all of these.

## DER (X.690 clauses 10 and 11)

Encoding is canonical. Decoding rejects every non-canonical form with `Asn1DecodeError.NonCanonical`:

* indefinite or non-minimal lengths, constructed strings
* BOOLEAN TRUE other than 0xFF
* non-zero unused bits, and trailing zero bits in named-bit BIT STRINGs
* explicitly encoded DEFAULT values
* SET components out of tag order, SET OF elements out of encoding order
* REALs that are not base 2 with an odd mantissa and minimal exponent
* UTCTime/GeneralizedTime not in the 11.7/11.8 form

## PER (X.691): APER and UPER

`PerEncodingRule.Aligned` (APER) and `PerEncodingRule.Unaligned` (UPER) share one implementation with an explicit
alignment mode. Implemented: BASIC-PER for both variants.

* PER-visible constraints (X.691 10.3): value ranges and single values on INTEGER; SIZE on strings, BIT/OCTET STRING
  and collections; permitted alphabets on known-multiplier strings. Not visible: PATTERN, user-defined, table and
  contents constraints, single values on strings, and constraints on non-known-multiplier strings. Serial
  constraints remove earlier extensibility (X.680 50.11). Unions with invisible parts are invisible, invisible
  intersection operands are ignored, and EXCEPT is ignored.
* Constrained, semi-constrained and unconstrained whole numbers; normally small numbers and lengths; constrained
  length determinants; 16K/32K/48K/64K fragmentation for lengths ≥ 16K when the upper bound is ≥ 64K or unset.
* SEQUENCE/SET preambles; extension bit, bitmap and open-type encoding of additions, including `[[ ]]` groups;
  SET components in canonical order.
* CHOICE indices in canonical tag order (untagged nested CHOICEs by their smallest root tag); extension
  alternatives as open types; unknown alternatives kept with their index so they can be re-encoded.
* Character strings with permitted alphabets: direct values when they fit, canonical-order indices otherwise
  (30.5.4); APER character widths rounded up to a power of two; alignment thresholds of 30.5.6/30.5.7.
* UTCTime/GeneralizedTime as VisibleString in the X.690 canonical form (10.6.5).
* A complete encoding is padded to an octet boundary; an empty encoding is one zero octet (11.1), or no octets with
  `Asn1EncodingOptions.EmptyPerEncodingAsZeroOctet = false`. Decoding accepts both.

Not implemented: CANONICAL-PER (SET OF sorting and DEFAULT rules for non-simple types), the X.680 time types (X.691
clause 32), EMBEDDED PDV/EXTERNAL/CHARACTER STRING, and PER encoding instructions (X.695).

## OER (X.696): BASIC-OER and CANONICAL-OER

`OerEncodingRule.Basic` ("OER") and `OerEncodingRule.Canonical` ("COER") share one implementation in
`Asn1.Runtime.Oer`. The encoder always produces the canonical form, which is also a valid BASIC-OER encoding.
The profiles differ in decoding: BASIC-OER accepts every form an encoder may choose, and CANONICAL-OER rejects
everything except the single form required by clause 31 (`Asn1DecodeError.NonCanonical`).

* OER-visible constraints (8.2): non-extensible value ranges and single values on INTEGER; non-extensible SIZE on
  known-multiplier strings, BIT STRING and OCTET STRING. Extensible constraints are not visible, except that serial
  application removes earlier extensibility (8.2.3). Unions with invisible parts are invisible, invisible
  intersection operands are ignored, EXCEPT is ignored, and a contained subtype contributes its root. Collection
  SIZE constraints never affect OER.
* INTEGER (10): fixed 1/2/4/8-octet unsigned or two's-complement words chosen from the bounds; otherwise a length
  determinant and a variable-size number. There is no extension bit.
* ENUMERATED (11): the numeric value in the short form (0..127) or the long form (signed, minimal).
* BOOLEAN FF/00; REAL as a length and the DER contents octets (12.4); OBJECT IDENTIFIER and RELATIVE-OID as a length
  and the BER contents octets.
* BIT STRING, OCTET STRING and known-multiplier strings with a fixed effective size have no length determinant
  (13.2, 14.1, 27.2); otherwise a length determinant precedes the contents (BIT STRING adds an unused-bits octet).
  Named-bit BIT STRINGs are trimmed to the smallest size permitted by the size constraint (13.2.4, 31.6).
* SEQUENCE/SET (16, 18): preamble with the extension bit, the presence bitmap and zero padding; root components;
  then an extension presence bitmap and each present addition (or `[[ ]]` group) as an open type. SET components
  are in canonical tag order. Unknown additions from a later version are skipped.
* SEQUENCE OF/SET OF (17, 19): a quantity field (length and minimal unsigned count). CANONICAL-OER sorts SET OF
  elements by their encodings (31.8), and its decoder checks the order.
* CHOICE (20): the outermost tag of the chosen alternative (8.7), then its encoding; extension alternatives as open
  types. An untagged CHOICE alternative repeats the tag (20.1 note 3). An unknown extension alternative is kept as
  its complete encoding (tag, length and contents), so it is relayed unchanged.
* UTCTime/GeneralizedTime as a length and VisibleString octets (8.4.4); CANONICAL-OER requires the X.690 canonical
  form (31.11).
* CANONICAL-OER decoding checks short and minimal lengths, minimal variable-size integers, enumerations and
  quantities, TRUE as FF, zero padding bits, trailing zero bits in named-bit BIT STRINGs, absent DEFAULT values
  (31.9) and SET OF order.

Not implemented: the IEEE 754 binary32/binary64 REAL forms selected by WITH COMPONENTS constraints (12.2/12.3);
such types are rejected with `Asn1UnsupportedException` instead of being encoded incorrectly. Also not implemented:
the X.680 time types (clause 29), EMBEDDED PDV/EXTERNAL/CHARACTER STRING, and the OID-IRI types.

## JER (X.697)

`JerEncodingRule.Instance` writes compact JSON and `JerEncodingRule.Indented` writes indented JSON; both are JER
(UTF-8, 7.6.2). Implemented: the encodings of clauses 20–41 **without encoding instructions**. JER type prefixes and
JER encoding control sections are parsed and ignored with warning ASN1015, so ARRAY, BASE64, NAME, OBJECT, TEXT
and UNWRAPPED have no effect.

* BOOLEAN `true`/`false`, NULL `null`, INTEGER as a JSON number of any size (no fraction or exponent, 21),
  ENUMERATED as the identifier (22).
* REAL (23): `"NaN"`, `"INF"`, `"-INF"` and `"-0"` as strings, other values as JSON numbers. DotAsn's REAL values are
  binary64, i.e. base-2 abstract values, so the `{"base10Value": n}` form is never written; it is accepted on input.
* BIT STRING (24): a hexadecimal string when the effective size is fixed (only non-extensible SIZE constraints are
  JER-visible, 7.2), otherwise `{"value": hex, "length": bits}`. Named-bit values are trimmed and padded to the
  effective size. OCTET STRING (25.3) as a hexadecimal string.
* OBJECT IDENTIFIER and RELATIVE-OID as dotted strings (32, 33); time types as strings (40).
* IA5, Visible, Numeric, Printable, BMP, Universal and UTF8 strings as JSON strings; Teletex, Videotex, Graphic,
  General and ObjectDescriptor as hexadecimal X.690 octets (38.2, 7.4.5).
* SEQUENCE/SET (27.3, 29) as objects with members in textual order; DEFAULT values equal to the default are
  omitted. SEQUENCE OF/SET OF as arrays (28, 30.2). CHOICE as an object with one member (31.3). Open types as the
  JSON of the contained value (41).
* The decoder accepts every sender's option: members in any order, `null` for absent OPTIONAL/DEFAULT components
  (27.3.4), escapes (7.6.3), lower- or upper-case hexadecimal, white space, and REAL numbers in any notation. It
  rejects invalid UTF-8, duplicate members, unknown members of non-extensible types, missing mandatory root
  components, fractional or exponent INTEGERs, and trailing data. Unknown members of extensible SEQUENCE/SET types
  are ignored; an unknown alternative of an extensible CHOICE is kept as its complete JSON object and relayed
  unchanged. Open types decode to their raw JSON text.

Not implemented: encoding instructions (14–19); decoding the `{"containing": …}` form of contents-constrained BIT
STRING and OCTET STRING (24.4/25.4, reported as `Asn1UnsupportedException`); EMBEDDED PDV, EXTERNAL, CHARACTER
STRING and the IRI types.

## XER (X.693): BASIC-XER and CANONICAL-XER

`XerEncodingRule.Basic` ("XER") and `XerEncodingRule.Canonical` ("CXER") share one implementation. The encoder writes
the CANONICAL-XER form for both (no prolog, no white-space, empty-element tags for empty values, DEFAULT values
present, SET components in canonical tag order, SET OF elements sorted by their CXER text). The one difference is that
BASIC-XER writes CARRIAGE RETURN as `&#13;`, because XML processors normalise it to LINE FEED; CANONICAL-XER forbids
character references (9.1.3) and therefore cannot preserve it (X.680 12.15.3 note 2).

* The document element is the "NonParameterizedTypeName" (X.680 14.2): the type reference, or the xmlasn1typename
  of a built-in type (`BIT_STRING`, `SEQUENCE_OF`, ...), with `_` before names beginning with "XML".
* BOOLEAN and ENUMERATED as empty elements (`<true/>`, `<red/>`, 8.3.5/8.3.7); INTEGER as XMLSignedNumber; REAL as
  the canonical `d.dddE[-]n` form, `0`, `-0`, or `<PLUS-INFINITY/>`, `<MINUS-INFINITY/>`, `<NOT-A-NUMBER/>` (9.2,
  8.3.8); BIT STRING as an xmlbstring (named-bit values without trailing zeros, 9.3); OCTET STRING as upper-case
  xmlhstring (9.4); OBJECT IDENTIFIER and RELATIVE-OID in number form (9.8/9.9); character strings as xmlcstring with
  `&amp;`, `&lt;`, `&gt;` and the control-character elements of X.680 Table 3; times as text.
* SEQUENCE OF/SET OF items follow X.680 26.4–26.10: "XMLValueList" for BOOLEAN, ENUMERATED and CHOICE, otherwise
  "XMLDelimitedItemList" with the item identifier or the element type's name.
* CHOICE as the alternative's element; open types as the "XMLTypedValue" of the contained value.
* The BASIC-XER decoder accepts every encoder's option: an XML declaration, white-space (8.1.4), character references
  (12.15.8), CDATA, components in any order, absent DEFAULT components, `<x />` empty tags, OBJECT IDENTIFIER name
  forms (`iso.member-body(2)`). The XML parser prohibits DTDs and external entities, and comments, processing
  instructions and attributes are rejected (8.1.2). Unknown SEQUENCE/SET elements of extensible types are ignored, an
  unknown CHOICE alternative is kept as its element and relayed unchanged, and an unknown ENUMERATED item decodes to
  a placeholder (8.6).
* The CANONICAL-XER decoder decodes the same way and then requires the input to be exactly the canonical encoding of
  the decoded value (7.4: CXER has no alternatives), compared after XML end-of-line normalisation. Input with unknown
  extensions cannot be re-encoded and is not checked this way.

Not implemented: EXTENDED-XER (clause 10) and XER encoding instructions (parsed and ignored with warning ASN1015);
the "XMLTypedValue" form of contents-constrained BIT STRING/OCTET STRING (CXER encoding of such types is rejected);
CXER normalisation of TIME intervals, durations and recurrences (9.13 c/d); EMBEDDED PDV, EXTERNAL, CHARACTER STRING
and the IRI types.

## Not implemented

EXTENDED-XER (X.693 clause 10) and encoding instructions for XER and JER. The CLI returns exit code 3 with a clear message
when these are requested.
