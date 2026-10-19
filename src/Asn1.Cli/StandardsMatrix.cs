namespace Asn1.Cli;

/// <summary>The support matrix printed by <c>asn1 standards</c> (kept in sync with docs/standards-support.md).</summary>
internal static class StandardsMatrix
{
    public const string Text = """
        DotAsn standards support (editions: ITU-T X.680-X.683, X.690, X.691, X.693, X.696, X.697 (02/2021))

        Notation
          X.680 basic notation ............................ implemented (see docs/language-support.md)
          X.681 information objects ....................... implemented with limitations (no parameterized classes)
          X.682 constraints ............................... implemented (table constraints recorded; open types raw)
          X.683 parameterization .......................... implemented with limitations (no class/object-set parameters)

        Encoding rules
          BER  (X.690 clause 8) ........................... implemented and tested (asn1tools vectors, X.690 examples)
          CER  (X.690 clause 9, 11) ....................... implemented and tested (X.690 examples, round trips)
          DER  (X.690 clause 10, 11) ...................... implemented and tested (asn1tools vectors, strict decoding)
          APER (X.691, ALIGNED BASIC-PER) ................. implemented and tested (X.691 Annex A, asn1tools vectors)
          UPER (X.691, UNALIGNED BASIC-PER) ............... implemented and tested (X.691 Annex A, asn1tools vectors)
          CANONICAL-PER ................................... not implemented
          OER  (X.696, BASIC-OER) ......................... implemented and tested (X.696 Annex A, asn1tools vectors)
          COER (X.696 clause 31, CANONICAL-OER) ........... implemented and tested (X.696 Annex A, strict decoding)
          XER  (X.693, BASIC-XER) ......................... implemented and tested (X.693 Annex A, asn1tools vectors)
          CXER (X.693 clause 9, CANONICAL-XER) ............ implemented and tested (X.693 Annex A, strict decoding)
          EXTENDED-XER (X.693 clause 10) .................. not implemented
          JER (X.697) ..................................... implemented and tested without encoding instructions
                                                            (X.697 Annex A, asn1tools vectors)

        Known limitations
          - REAL values are IEEE 754 binary64; decimal (base 10) abstract values are rounded on decode.
          - DATE, TIME-OF-DAY, DATE-TIME, DURATION and TIME: BER/DER only (no PER/OER encoding yet).
          - OER: REAL with WITH COMPONENTS constraints (IEEE 754 binary32/64 forms, X.696 12.2/12.3) is rejected.
          - JER: encoding instructions (X.697 14-19) are ignored with a warning; the "containing" form of
            contents-constrained strings is not decoded; REAL values are written as base-2 JSON numbers.
          - XER: the XMLTypedValue form of contents-constrained strings is not supported (CXER cannot encode
            them); CXER cannot preserve CARRIAGE RETURN in strings (X.680 12.15.3 note 2); CXER normalisation
            of TIME intervals, durations and recurrences (X.693 9.13 c/d) is not implemented.
          - EXTERNAL, EMBEDDED PDV, CHARACTER STRING, OID-IRI and RELATIVE-OID-IRI are not encodable.
          - Open types decode to raw contents; table-driven automatic decoding is not implemented.
        """;
}
