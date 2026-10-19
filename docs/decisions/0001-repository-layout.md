# ADR 0001: Repository and project layout

**Status:** accepted

## Context
The initial brief suggested one project per phase and per encoding rule (`Asn1.Lexer`, `Asn1.Parser`,
`Asn1.Runtime.Der`, `Asn1.Runtime.Cer`, `Asn1.Runtime.Uper`, ...), and asked that no empty projects be created.

## Decision
* The lexer and parser share one project, `Asn1.Syntax`: the parser re-enters the token stream for deferred notation,
  and a separate lexer assembly would only add coupling.
* BER, CER and DER share one project, `Asn1.Runtime.Ber`. X.690 defines all three as profiles of one TLV syntax.
  Each profile has its own rule class and its own canonical encoding and strict decoding behaviour (length forms,
  string fragmentation, SET/SET OF ordering, DEFAULT omission, BOOLEAN/BIT STRING/REAL/time restrictions), which are
  tested independently. DER is not "BER with a different name": it rejects every BER-only form.
* APER and UPER share `Asn1.Runtime.Per` with an explicit, tested alignment mode (X.691 defines both variants together).
* `Asn1.Core` holds encoding-independent values and the constraint model, used by both the compiler and the runtime.
* `Asn1.Dynamic` bridges the semantic model and the runtime for schema-driven tools that do not use generated code.
* The solution is `Asn1Toolchain.sln`. The original `DotAsn/` template project is left untouched and excluded.
