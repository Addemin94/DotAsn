// Errors of the WebAssembly runtime; they extend Asn1Error of @dotasn/runtime.

import { Asn1Error } from '@dotasn/runtime';

/** A value cannot be encoded (constraint violation, wrong value type, unsupported construct). */
export class Asn1EncodeError extends Asn1Error {}

/** Why decoding failed. */
export type DecodeErrorKind =
  | 'truncated' | 'invalidTag' | 'invalidLength' | 'nonCanonical' | 'malformed'
  | 'constraintViolation' | 'limitExceeded' | 'trailingData' | 'unsupported';

/** The input is not a valid encoding. */
export class Asn1DecodeError extends Asn1Error {
  readonly kind: DecodeErrorKind;
  /** Bit offset in the input where the problem was detected, or -1. */
  readonly bitOffset: number;

  constructor(kind: DecodeErrorKind, message: string, path = '', bitOffset = -1) {
    super(bitOffset < 0 ? message : `${message} at ${bitOffset % 8 === 0 ? `byte ${bitOffset / 8}` : `bit ${bitOffset}`}`, path);
    this.kind = kind;
    this.bitOffset = bitOffset;
  }
}

/** A feature of ASN.1 or of an encoding rule that the runtime does not implement. */
export class Asn1UnsupportedError extends Asn1Error {}
