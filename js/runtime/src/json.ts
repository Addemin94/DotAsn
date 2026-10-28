// DotAsn JSON value notation (the format of `asn1 encode` / `asn1 decode` and of the test vectors), see
// docs/value-notation-json.md. Converts between that notation and the runtime's value model.

import { bitString, bitsFromBinary, fromHex, toHex } from './helpers.ts';
import { Asn1Error } from './errors.ts';
import { elementOf, resolve, typeOf, type Asn1Type, type BitString, type Descriptor, type OpenValue } from './types.ts';

const maxSafe = BigInt(Number.MAX_SAFE_INTEGER);

/** A value in the notation could not be converted. */
export class Asn1NotationError extends Asn1Error {}

/** Converts a value of the JSON value notation (already parsed with JSON.parse) to a runtime value. */
export function fromNotation<T>(type: Asn1Type<T>, json: unknown): T {
  return read(resolve(type), json, '$') as T;
}

/** Converts a runtime value to the JSON value notation (a JSON-compatible value). */
export function toNotation<T>(type: Asn1Type<T>, value: T): unknown {
  return write(resolve(type), value);
}

/** Parses JSON text in the value notation. */
export function parseNotation<T>(type: Asn1Type<T>, text: string): T {
  return fromNotation(type, JSON.parse(text));
}

/** Formats a value as JSON text in the value notation. */
export function formatNotation<T>(type: Asn1Type<T>, value: T, indent?: number): string {
  return JSON.stringify(toNotation(type, value), null, indent);
}

function fail(message: string, path: string): never {
  throw new Asn1NotationError(message, path);
}

function read(t: Descriptor, j: unknown, path: string): unknown {
  switch (t.kind) {
    case 'boolean':
      return typeof j === 'boolean' ? j : fail('expected true or false', path);
    case 'null':
      return j === null ? null : fail('expected null', path);
    case 'integer': {
      const text = typeof j === 'number' || typeof j === 'string' ? String(j) : fail('expected an integer (number or decimal string)', path);
      if (!/^-?\d+$/.test(text)) {
        fail(`'${text}' is not an integer`, path);
      }

      const v = BigInt(text);
      if (t.big) {
        return v;
      }

      return v > maxSafe || v < -maxSafe ? fail(`${text} exceeds the safe integer range of ${t.name}`, path) : Number(v);
    }

    case 'enumerated': {
      const name = typeof j === 'string' ? j : fail('expected a string', path);
      return t.items.includes(name) ? name : fail(`'${name}' is not an item of ${t.name}`, path);
    }

    case 'real':
      if (typeof j === 'number') {
        return j;
      }

      switch (j) {
        case 'PLUS-INFINITY': case 'INF': return Infinity;
        case 'MINUS-INFINITY': case '-INF': return -Infinity;
        case 'NOT-A-NUMBER': case 'NaN': return NaN;
        case '-0': return -0;
        default: {
          const d = typeof j === 'string' ? Number(j) : NaN;
          return Number.isNaN(d) ? fail(`'${String(j)}' is not a REAL value`, path) : d;
        }
      }

    case 'bitString':
      return readBits(t, j, path);
    case 'octetString':
      if (typeof j === 'object' && j !== null && typeof (j as { base64?: unknown }).base64 === 'string') {
        return Uint8Array.from(atob((j as { base64: string }).base64), c => c.charCodeAt(0));
      }

      return hex(typeof j === 'string' ? j : fail('expected a hexadecimal string', path), path);
    case 'objectIdentifier':
    case 'relativeOid':
    case 'string':
    case 'time':
      return typeof j === 'string' ? j : fail('expected a string', path);
    case 'open': {
      const o = j as { encoding?: string; hex?: string };
      if (typeof o === 'object' && o !== null && typeof o.hex === 'string') {
        return { encoded: hex(o.hex, path), rule: o.encoding ?? 'BER' } satisfies OpenValue;
      }

      return fail('open type values are written as {"encoding": "BER", "hex": "..."}', path);
    }

    case 'sequence':
    case 'set': {
      if (typeof j !== 'object' || j === null || Array.isArray(j)) {
        fail(`expected an object for ${t.name}`, path);
      }

      const out: Record<string, unknown> = {};
      for (const [key, v] of Object.entries(j as Record<string, unknown>)) {
        const c = t.components.find(x => x.name === key) ?? fail(`'${key}' is not a component of ${t.name}`, path);
        out[key] = read(typeOf(c), v, `${path}.${key}`);
      }

      for (const c of t.components) {
        if (!c.optional && !(c.name in out)) {
          fail(`mandatory component '${c.name}' of ${t.name} is missing`, path);
        }
      }

      return out;
    }

    case 'choice': {
      if (typeof j !== 'object' || j === null || Array.isArray(j)) {
        fail(`expected a single-member object selecting an alternative of ${t.name}`, path);
      }

      const members = Object.entries(j as Record<string, unknown>);
      if (members.length !== 1) {
        fail(`a CHOICE value must have exactly one member (found ${members.length})`, path);
      }

      const [key, v] = members[0]!;
      if (key === '$unknownExtension') {
        const raw = v as { encoding?: string; hex?: string; index?: number };
        return { $unknownExtension: { encoded: hex(raw.hex ?? '', path), rule: raw.encoding ?? 'BER', index: raw.index ?? -1 } };
      }

      const alt = t.alternatives.find(a => a.name === key) ?? fail(`'${key}' is not an alternative of ${t.name}`, path);
      return { [key]: read(typeOf(alt), v, `${path}.${key}`) };
    }

    case 'sequenceOf':
    case 'setOf': {
      if (!Array.isArray(j)) {
        fail(`expected an array for ${t.name}`, path);
      }

      const element = elementOf(t);
      return (j as unknown[]).map((item, i) => read(element, item, `${path}[${i}]`));
    }
  }
}

function hex(text: string, path: string): Uint8Array {
  try {
    return fromHex(text);
  } catch {
    return fail(`'${text}' is not a hexadecimal string`, path);
  }
}

function readBits(t: Descriptor & { kind: 'bitString' }, j: unknown, path: string): BitString {
  if (Array.isArray(j)) {
    // Named bits.
    let positions: number[] = [];
    for (const name of j) {
      const p = t.named?.[name as string];
      if (p === undefined) {
        fail(`'${String(name)}' is not a named bit of ${t.name}`, path);
      }

      positions.push(p);
    }

    positions = positions.sort((a, b) => a - b);
    const length = positions.length === 0 ? 0 : positions[positions.length - 1]! + 1;
    const bytes = new Uint8Array((length + 7) >>> 3);
    positions.forEach(p => (bytes[p >>> 3]! |= 0x80 >>> (p & 7)));
    return { bytes, length };
  }

  if (typeof j === 'string') {
    const m = /^'([01\s]*)'B$/.exec(j) ?? fail(`'${j}' is not a BIT STRING value`, path);
    return bitsFromBinary(m[1]!.replace(/\s/g, ''));
  }

  const o = j as { hex?: string; length?: number };
  if (typeof o !== 'object' || o === null || typeof o.hex !== 'string' || typeof o.length !== 'number') {
    fail('a BIT STRING object needs "hex" and "length"', path);
  }

  const bytes = hex(o.hex!, path);
  const length = o.length!;
  if (length < 0 || length > bytes.length * 8 || bytes.length * 8 - length >= 8) {
    fail(`length ${length} does not match ${bytes.length} hex octet(s)`, path);
  }

  return bitString(bytes, length);
}

function write(t: Descriptor, v: unknown): unknown {
  switch (t.kind) {
    case 'integer': {
      const b = BigInt(v as number | bigint);
      return b > maxSafe || b < -maxSafe ? b.toString() : Number(b);
    }

    case 'real': {
      const d = v as number;
      if (d === Infinity) return 'PLUS-INFINITY';
      if (d === -Infinity) return 'MINUS-INFINITY';
      if (Number.isNaN(d)) return 'NOT-A-NUMBER';
      if (Object.is(d, -0)) return '-0';
      return d;
    }

    case 'bitString': {
      const b = v as BitString;
      return { hex: toHex(bitString(b.bytes, b.length).bytes), length: b.length };
    }

    case 'octetString':
      return toHex(v as Uint8Array);
    case 'open': {
      const o = v as OpenValue;
      return 'encoded' in o ? { encoding: o.rule, hex: toHex(o.encoded) } : { type: resolve(o.type).name, value: write(resolve(o.type), o.value) };
    }

    case 'sequence':
    case 'set': {
      const obj = v as Record<string, unknown>;
      const out: Record<string, unknown> = {};
      for (const [key, value] of Object.entries(obj)) {
        if (value === undefined) {
          continue;
        }

        const c = t.components.find(x => x.name === key);
        out[key] = c ? write(typeOf(c), value) : value;
      }

      return out;
    }

    case 'choice': {
      const [key, value] = Object.entries(v as Record<string, unknown>).find(([, x]) => x !== undefined)!;
      if (key === '$unknownExtension') {
        const u = value as { encoded: Uint8Array; rule: string; index: number };
        return { $unknownExtension: u.index >= 0 ? { encoding: u.rule, hex: toHex(u.encoded), index: u.index } : { encoding: u.rule, hex: toHex(u.encoded) } };
      }

      const alt = t.alternatives.find(a => a.name === key)!;
      return { [key]: write(typeOf(alt), value) };
    }

    case 'sequenceOf':
    case 'setOf': {
      const element = elementOf(t);
      return (v as unknown[]).map(item => write(element, item));
    }

    default:
      return v;
  }
}
