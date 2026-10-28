// Helpers for values: hexadecimal text and BIT STRING values.

import type { BitString } from './types.ts';

/** Octets as upper-case hexadecimal text. */
export function toHex(bytes: Uint8Array): string {
  let s = '';
  for (const b of bytes) {
    s += b.toString(16).padStart(2, '0');
  }

  return s.toUpperCase();
}

/** Octets from hexadecimal text (either case). */
export function fromHex(hex: string): Uint8Array {
  if (hex.length % 2 !== 0 || !/^[0-9a-fA-F]*$/.test(hex)) {
    throw new RangeError('invalid hexadecimal string');
  }

  const out = new Uint8Array(hex.length / 2);
  for (let i = 0; i < out.length; i++) {
    out[i] = parseInt(hex.slice(i * 2, i * 2 + 2), 16);
  }

  return out;
}

/** Creates a BIT STRING value of `length` bits from octets (unused trailing bits are cleared). */
export function bitString(bytes: Uint8Array, length = bytes.length * 8): BitString {
  const n = (length + 7) >>> 3;
  if (bytes.length < n) {
    throw new RangeError(`${bytes.length} octet(s) cannot hold ${length} bits`);
  }

  const copy = bytes.slice(0, n);
  const unused = n * 8 - length;
  if (unused > 0) {
    copy[n - 1]! &= (0xff << unused) & 0xff;
  }

  return { bytes: copy, length };
}

/** The value of bit `i` (0 is the leading bit). */
export const getBit = (b: BitString, i: number): boolean => (b.bytes[i >>> 3]! & (0x80 >>> (i & 7))) !== 0;

/** A BIT STRING from '0'/'1' digits. */
export function bitsFromBinary(digits: string): BitString {
  const bytes = new Uint8Array((digits.length + 7) >>> 3);
  for (let i = 0; i < digits.length; i++) {
    if (digits[i] === '1') {
      bytes[i >>> 3]! |= 0x80 >>> (i & 7);
    } else if (digits[i] !== '0') {
      throw new RangeError('binary digits must be 0 or 1');
    }
  }

  return { bytes, length: digits.length };
}

/** The '0'/'1' digits of a BIT STRING. */
export function bitsToBinary(b: BitString): string {
  let s = '';
  for (let i = 0; i < b.length; i++) {
    s += getBit(b, i) ? '1' : '0';
  }

  return s;
}
