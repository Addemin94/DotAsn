# ADR 0007: Pawn generation

**Status:** proposed

## Context

Pawn is a small embeddable scripting language. Its virtual machine typically runs with 32-bit cells and a few kilobytes
of RAM and bytecode. A Pawn program that exchanges messages described in ASN.1 needs encoders and decoders that fit
there, and that agree with DotAsn's own codecs on every other platform.

Pawn cannot host the DotAsn runtime: there is no room for a descriptor interpreter, and Pawn has no structures,
references or heap.

## Decision

* `asn1 generate-pawn` (`src/Asn1.CodeGen.Pawn`) emits one include file per ASN.1 module, the OER core
  `dotasn_oer.inc`, and `dotasn-pawn-layout.json`.
* The encoding is canonical OER (COER, X.696). Every field starts on an octet, so the generated code writes whole
  bytes and needs no bit packing.
* Unlike the C# and TypeScript generators (ADR 0002, ADR 0005), the generated code **contains the encoding logic**:
  straight-line encoders and decoders per type, calling small helpers in the core. Only what a script calls is
  compiled in, since every function is `stock`.
* A value lives in a **cell array at fixed offsets**, named by constants such as `Report_label_PRESENT`:
  * OPTIONAL and DEFAULT components have a presence cell;
  * strings and SEQUENCE OF have a length or count cell;
  * CHOICE has an index cell;
  * strings are packed bytes, four to a cell with the first in the most significant byte, as in Pawn's own packed
    strings.
* The encoded message is a packed array (`buffer{}`).
* The supported subset is what fits 32-bit cells and a fixed layout:
  * INTEGER within 32 bits, BOOLEAN, ENUMERATED, NULL;
  * octet, bit and character strings (IA5, Visible, Numeric, Printable, UTF-8) with an upper size bound;
  * SEQUENCE, CHOICE with one-octet tags, and SEQUENCE OF with an upper size bound.
* Extension additions are not part of a layout. Encoders send none, decoders skip them.
* Other types are reported with ASN7002 and left out, together with the types that use them.
* Pawn keeps 31 characters of a symbol. A type whose constants would be longer gets a shorter prefix, its capital
  initials (ASN7001).

## Consequences

* The output is checked by `scripts/pawn_vectors.py` against the OER vectors, compiled with pawncc and run in the
  Pawn 4 virtual machine. It needs a Pawn 4 toolchain, so it is not part of `dotnet test`.
* Bytecode grows with the number of fields. With `pawncc -d0 -O3`, an encoder of three fields is about 0.7 KB with
  the core helpers it uses.
* A table-driven codec, a descriptor per type interpreted by one core function, would cost less per type but more
  for one or two types. It can be added later behind the same layouts.
