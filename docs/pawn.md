# Using DotAsn from Pawn

This guide takes you from an ASN.1 specification to encoding and decoding messages in a Pawn 4 script with 32-bit
cells. The encoding is canonical OER (COER, X.696), so DotAsn, or any other OER decoder, decodes the messages from
the same specification. The design is in
[ADR 0007](decisions/0007-pawn-generation.md).

## 1. Generate

```sh
asn1 generate-pawn specs/measurements.asn --output generated
```

This writes:

* one include file per ASN.1 module, e.g. `Measurements.inc`, with the layout constants and an encoder and decoder
  per type;
* `dotasn_oer.inc`, the OER core the module files include;
* `dotasn-pawn-layout.json`, the layouts as JSON, for tools and tests (`--no-manifest` leaves it out).

Types outside the Pawn subset are reported as ASN7002 and left out. The subset is listed in the ADR.

## 2. Fill a value layout

Pawn has no structures, so a value lives in a cell array, at offsets the generated constants name.
For this specification:

```asn1
Reading ::= SEQUENCE {
    channel     INTEGER (0..255),
    level       INTEGER (-1000..1000)
}

ReadingLog ::= SEQUENCE {
    startTime   INTEGER (0..4294967295),
    readings    SEQUENCE (SIZE (1..8)) OF Reading,
    label       IA5String (SIZE (0..12)) OPTIONAL
}
```

the generator writes constants such as:

```pawn
const ReadingLog_CELLS = 23;
const ReadingLog_startTime = 0;
const ReadingLog_readings_COUNT = 1;
const ReadingLog_readings = 2;
const ReadingLog_readings_STRIDE = 2;
const ReadingLog_label_PRESENT = 18;
const ReadingLog_label_LENGTH = 19;
const ReadingLog_label = 20;
```

and a script fills a layout like this:

```pawn
new log[ReadingLog_CELLS];
log[ReadingLog_startTime] = 1700000000;
log[ReadingLog_readings_COUNT] = 2;
log[ReadingLog_readings + (1 * ReadingLog_readings_STRIDE) + Reading_level] = 213;
```

* **OPTIONAL and DEFAULT components** have a `_PRESENT` cell. The encoder leaves out a DEFAULT component whose value is the default.
* **Strings** have a `_LENGTH` cell unless their size is fixed, and their bytes are packed, four to a cell, with the
  first byte in the most significant byte. This is the order of Pawn's own packed strings.
* **SEQUENCE OF** has a `_COUNT` cell, then the items, `_STRIDE` cells apart.
* **CHOICE** has a `_CHOICE` cell holding the chosen alternative's `_CHOSEN` constant, followed by that alternative's value.
* **ENUMERATED** values are constants such as `ReportingMode_onChange`.
* **Values above 2^31 - 1** of an unsigned type, such as `INTEGER (0..4294967295)`, are stored in the cell's 32 bits.

## 3. Encode and decode

```pawn
#include "Measurements.inc"

new payload{48};
new position = 0;
if (!ReadingLog_encode(log, payload, position, sizeof payload)) {
    // The value is outside its constraints, or the buffer is too small.
}

new decoded[ReadingLog_CELLS];
new read = 0;
if (!ReadingLog_decode(decoded, payload, read, position)) {
    // The input is not valid COER for the type.
}
```

* Every function takes an optional `base`, the cell where the value starts, and the generated code uses it to encode nested types.
* Encoders send no extension additions, and decoders skip them.
* **Byte order.** A native function that takes the packed message from a script receives cells.
  The first byte of the message is the most significant byte of the first cell, so on a little-endian host the
  native swaps the bytes of each cell.

## 4. Size

Every generated function is `stock`, so a script only pays for the encoders and decoders it calls.
Compile with `pawncc -d0 -O3`: it removes the debug information and uses Pawn's packed opcodes,
which in practice halves the bytecode.
As a guide, an encoder of three fields takes about 0.7 KB with the core helpers it uses.

## 5. Testing

`scripts/pawn_vectors.py` runs the OER vectors through the generated code, compiled with pawncc and run in the
Pawn 4 virtual machine:

```sh
python3 scripts/pawn_vectors.py --pawncc path/to/pawncc --pawn-source path/to/pawn-4.1 --pawncc-options="-d0 -O3"
```

`--pawn-source` is a Pawn 4 source tree, for the host runner (`scripts/pawn_runner.c`) and `console.inc`.
