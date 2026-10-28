// The value notation conversion and the value helpers, with hand-written descriptors (generated code produces the
// same shapes). Encoding is tested in @dotasn/wasm.

import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import * as rt from '@dotasn/runtime';

const int: rt.Asn1Type<number> = { kind: 'integer', name: 'INTEGER', big: false };
const big: rt.Asn1Type<bigint> = { kind: 'integer', name: 'M.Big', big: true };
const color: rt.Asn1Type<'red' | 'green'> = { kind: 'enumerated', name: 'M.Color', items: ['red', 'green'] };
const flags: rt.Asn1Type<rt.BitString> = { kind: 'bitString', name: 'M.Flags', named: { a: 0, b: 3 } };
const octets: rt.Asn1Type<Uint8Array> = { kind: 'octetString', name: 'OCTET STRING' };
const real: rt.Asn1Type<number> = { kind: 'real', name: 'REAL' };

interface Point {
  x: number;
  label?: string;
}

const point: rt.Asn1Type<Point> = {
  kind: 'sequence',
  name: 'M.Point',
  components: [
    { name: 'x', type: () => int },
    { name: 'label', type: () => ({ kind: 'string', name: 'UTF8String' }), optional: true },
  ],
};

type Shape = { point: Point } | { color: 'red' | 'green' } | rt.UnknownAlternative;

const shape: rt.Asn1Type<Shape> = {
  kind: 'choice',
  name: 'M.Shape',
  alternatives: [
    { name: 'point', type: () => point },
    { name: 'color', type: () => color },
  ],
};

const points: rt.Asn1Type<Point[]> = { kind: 'sequenceOf', name: 'M.Points', element: () => point };
const alias: rt.Asn1Type<Point> = { kind: 'ref', name: 'M.Alias', target: () => point };

describe('value notation', () => {
  it('round trips values of every kind', () => {
    const cases: [rt.Asn1Type<never>, unknown][] = [
      [int as rt.Asn1Type<never>, 5],
      [big as rt.Asn1Type<never>, 2n ** 70n],
      [color as rt.Asn1Type<never>, 'green'],
      [octets as rt.Asn1Type<never>, rt.fromHex('0aff')],
      [real as rt.Asn1Type<never>, -0],
      [real as rt.Asn1Type<never>, Infinity],
      [point as rt.Asn1Type<never>, { x: 1, label: 'p' }],
      [shape as rt.Asn1Type<never>, { point: { x: 2 } }],
      [points as rt.Asn1Type<never>, [{ x: 1 }, { x: 2, label: 'q' }]],
    ];
    for (const [type, value] of cases) {
      assert.deepEqual(rt.parseNotation(type, rt.formatNotation(type, value as never)), value);
    }
  });

  it('writes the documented forms', () => {
    assert.deepEqual(rt.toNotation(big, 2n ** 60n), '1152921504606846976');
    assert.deepEqual(rt.toNotation(big, 5n), 5);
    assert.deepEqual(rt.toNotation(octets, rt.fromHex('0AFF')), '0AFF');
    assert.deepEqual(rt.toNotation(flags, rt.bitsFromBinary('1001')), { hex: '90', length: 4 });
    assert.deepEqual(rt.toNotation(real, NaN), 'NOT-A-NUMBER');
    assert.deepEqual(rt.toNotation(shape, { color: 'red' }), { color: 'red' });
  });

  it('reads BIT STRINGs as hex, binary or named bits', () => {
    assert.deepEqual(rt.fromNotation(flags, { hex: '90', length: 4 }), rt.bitsFromBinary('1001'));
    assert.deepEqual(rt.fromNotation(flags, "'1001'B"), rt.bitsFromBinary('1001'));
    assert.deepEqual(rt.fromNotation(flags, ['a', 'b']), rt.bitsFromBinary('1001'));
  });

  it('keeps unknown alternatives with their PER index', () => {
    const unknown: Shape = { $unknownExtension: { encoded: rt.fromHex('0102'), rule: 'UPER', index: 3 } };
    const json = rt.toNotation(shape, unknown);
    assert.deepEqual(json, { $unknownExtension: { encoding: 'UPER', hex: '0102', index: 3 } });
    assert.deepEqual(rt.fromNotation(shape, json), unknown);
  });

  it('resolves aliases under their own name', () => {
    assert.equal(rt.resolve(alias).name, 'M.Alias');
    assert.deepEqual(rt.fromNotation(alias, { x: 3 }), { x: 3 });
  });

  it('reports invalid values with a path', () => {
    assert.throws(() => rt.fromNotation(point, { x: 'one' }), (e: unknown) => e instanceof rt.Asn1NotationError && e.path.includes('x'));
    assert.throws(() => rt.fromNotation(point, {}), /mandatory component 'x'/);
    assert.throws(() => rt.fromNotation(point, { x: 1, z: 2 }), /'z' is not a component/);
    assert.throws(() => rt.fromNotation(shape, { point: { x: 1 }, color: 'red' }), rt.Asn1NotationError);
    assert.throws(() => rt.fromNotation(color, 'blue'), /not an item/);
    assert.throws(() => rt.fromNotation(int, String(2n ** 60n)), /safe integer range/);
  });
});

describe('helpers', () => {
  it('convert hexadecimal text and BIT STRINGs', () => {
    assert.equal(rt.toHex(rt.fromHex('00ffA0')), '00FFA0');
    assert.throws(() => rt.fromHex('abc'), RangeError);
    assert.equal(rt.bitsToBinary(rt.bitsFromBinary('10110')), '10110');
    assert.deepEqual(rt.bitString(rt.fromHex('FF'), 3), { bytes: rt.fromHex('E0'), length: 3 });
    assert.equal(rt.getBit(rt.bitsFromBinary('0010'), 2), true);
  });
});
