# ADR 0002: Typed runtime descriptors instead of generated codecs per rule

**Status:** accepted

## Context
Generated types must be encodable with many rules (BER, DER, CER, APER, UPER, OER, COER, XER, JER), and the generator
must not contain encoding logic. Options considered:
1. Generate a codec per type per rule: fast, but every new rule needs regeneration and the generator duplicates rules.
2. A universal boxed value model: simple, but erases type information and boxes every value.
3. Typed descriptors (`Asn1Type<T>`) with per-kind double dispatch into rule-specific writers and readers.

## Decision
Option 3. Generated code emits plain C# types plus one immutable descriptor per type, built from lambdas for field
access. Writers expose generic methods (`WriteSequence<T>`, `WriteCollection<TElement>`, `WriteChoice<T>`), so
structured values stay typed. Primitive descriptors convert to canonical forms (`long`/`BigInteger` for integers).
Descriptors are created lazily so recursive types are possible.

## Consequences
* Adding an encoding rule requires no regeneration.
* Rule-specific derived data (PER bounds, tag maps) is cached on descriptors via `GetRuleData`.
* The dynamic model (`Asn1.Dynamic`) reuses the same descriptors with `object?` values.
