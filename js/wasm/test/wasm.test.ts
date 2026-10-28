// Behaviour of the WebAssembly runtime itself: the rules only it has, the notation API, errors, validation and
// loading. Uses the TypeScript generated for test/specs/runtime.asn and samples/telemetry (`npm run generate`).

import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';
import { fileURLToPath } from 'node:url';
import * as rt from '@dotasn/runtime';
import { Asn1DecodeError, Asn1EncodeError, loadDotAsn, ruleNames } from '@dotasn/wasm';
import { schema as runtimeSchema } from './generated/runtime/dotasn-schema.ts';
import * as R from './generated/runtime/RuntimeTest.ts';
import { schema as telemetrySchema } from './generated/telemetry/dotasn-schema.ts';
import * as T from './generated/telemetry/TelemetryProtocol.ts';

const heartbeat: T.Heartbeat = { sequenceNumber: 7, uptimeSeconds: 4_000_000_000 };
const message: T.Message = { messageType: 'heartbeat', payload: { heartbeat } };

describe('WebAssembly runtime', () => {
  it('encodes like the .NET runtime', async () => {
    const asn1 = await loadDotAsn(telemetrySchema);
    assert.equal(rt.toHex(asn1.uper.encode(T.Heartbeat, heartbeat)), '07EE6B2800');
    assert.equal(rt.toHex(asn1.der.encode(T.Message, message)), '3011810101A20CA00A800107810500EE6B2800');
  });

  it('has CER, XER and CXER as well', async () => {
    const asn1 = await loadDotAsn(telemetrySchema);
    for (const rule of [asn1.cer, asn1.xer, asn1.cxer]) {
      // Decoding fills in DEFAULT values (protocolVersion).
      assert.deepEqual(rule.decode(T.Message, rule.encode(T.Message, message)), { protocolVersion: 1, ...message });
    }

    assert.equal(new TextDecoder().decode(asn1.xer.encode(T.Heartbeat, heartbeat)).replace(/\s/g, ''),
      '<Heartbeat><sequenceNumber>7</sequenceNumber><uptimeSeconds>4000000000</uptimeSeconds></Heartbeat>');
    assert.deepEqual(ruleNames.map(n => asn1.rule(n).name), [...ruleNames]);
  });

  it('works by type name with the JSON value notation', async () => {
    const asn1 = await loadDotAsn(JSON.parse(telemetrySchema) as object);
    assert.ok(asn1.typeNames.includes('Telemetry-Protocol.Heartbeat'));
    const bytes = asn1.encodeNotation('Heartbeat', 'UPER', { sequenceNumber: 7, uptimeSeconds: 4000000000 });
    assert.equal(rt.toHex(bytes), '07EE6B2800');
    assert.deepEqual(asn1.decodeNotation('Telemetry-Protocol.Heartbeat', 'UPER', bytes), { sequenceNumber: 7, uptimeSeconds: 4000000000 });
  });

  it('keeps big integers exact', async () => {
    const asn1 = await loadDotAsn(runtimeSchema);
    for (const v of [2n ** 100n, -(2n ** 70n) - 1n]) {
      for (const rule of ruleNames) {
        assert.equal(asn1.rule(rule).decode(R.Big, asn1.rule(rule).encode(R.Big, v)), v, rule);
      }
    }
  });

  it('reports errors with the runtime error classes', async () => {
    const asn1 = await loadDotAsn(telemetrySchema);
    assert.throws(() => asn1.uper.encode(T.Heartbeat, { sequenceNumber: 700, uptimeSeconds: 1 }),
      (e: unknown) => e instanceof Asn1EncodeError && e.path === 'sequenceNumber');
    assert.throws(() => asn1.uper.decode(T.Heartbeat, rt.fromHex('07EE6B')),
      (e: unknown) => e instanceof Asn1DecodeError && e.kind === 'truncated');
    assert.throws(() => asn1.uper.decode(T.Heartbeat, rt.fromHex('07EE6B280000')),
      (e: unknown) => e instanceof Asn1DecodeError && e.kind === 'trailingData');
    assert.deepEqual(asn1.uper.decode(T.Heartbeat, rt.fromHex('07EE6B280000'), { rejectTrailingData: false }), heartbeat);
    assert.throws(() => asn1.encodeNotation('Heartbeat', 'UPER', { sequenceNumber: 'x' }), rt.Asn1NotationError);
    assert.throws(() => asn1.encodeNotation('NoSuchType', 'UPER', {}), /no type 'NoSuchType'/);
    assert.throws(() => asn1.encodeNotation('Heartbeat', 'PER' as never, {}), /unknown encoding rule/);
  });

  it('enforces decoding limits', async () => {
    const asn1 = await loadDotAsn(runtimeSchema);
    let node: R.Node = { value: 0 };
    for (let i = 1; i < 40; i++) {
      node = { value: i, next: node };
    }

    const bytes = asn1.ber.encode(R.Node, node);
    assert.equal(asn1.ber.decode(R.Node, bytes).value, 39);
    assert.throws(() => asn1.ber.decode(R.Node, bytes, { maxDepth: 20 }), (e: unknown) => e instanceof Asn1DecodeError && e.kind === 'limitExceeded');
  });

  it('validates values against all constraints', async () => {
    const asn1 = await loadDotAsn(telemetrySchema);
    assert.deepEqual(asn1.validate(T.Heartbeat, heartbeat), []);
    const errors = asn1.validate(T.Heartbeat, { sequenceNumber: 300, uptimeSeconds: 1 });
    assert.equal(errors.length, 1);
    assert.equal(errors[0]!.path, 'sequenceNumber');
  });

  it('encodes values outside their constraints only when asked to', async () => {
    const asn1 = await loadDotAsn(telemetrySchema);
    const value = { sequenceNumber: 300, uptimeSeconds: 1 };
    assert.throws(() => asn1.ber.encode(T.Heartbeat, value));
    const bytes = asn1.ber.encode(T.Heartbeat, value, { validateConstraints: false });
    assert.equal(asn1.ber.decode(T.Heartbeat, bytes, { validateConstraints: false }).sequenceNumber, 300);
  });

  it('decodes ENUMERATED items from a later version, which cannot be encoded', async () => {
    const asn1 = await loadDotAsn(runtimeSchema);
    for (const rule of [asn1.uper, asn1.ber, asn1.oer]) {
      const unknown = rule.decode(R.Color1, rule.encode(R.Color2, 'blue'));
      assert.ok(typeof unknown === 'object' && '$unknown' in unknown, rule.name);
      assert.throws(() => rule.encode(R.Color1, unknown), /later version/);
    }

    // UPER carries the extension index: blue is the first extension addition.
    assert.deepEqual(asn1.uper.decode(R.Color1, asn1.uper.encode(R.Color2, 'blue')), { $unknown: 0 });
  });

  it('encodes inline types through their own descriptors', async () => {
    const asn1 = await loadDotAsn(runtimeSchema);
    assert.equal(rt.resolve(R.NestedInner).name, 'Runtime-Test.Nested.inner');
    const bytes = asn1.uper.encode(R.NestedInner, { a: 5, b: true });
    assert.deepEqual(asn1.uper.decode(R.NestedInner, bytes), { a: 5, b: true });
    assert.deepEqual(asn1.uper.encode(R.Nested, { inner: { a: 5, b: true } }), bytes);
  });

  it('relays unknown extensions within the same rule', async () => {
    const asn1 = await loadDotAsn(runtimeSchema);
    for (const rule of [asn1.ber, asn1.uper, asn1.aper, asn1.oer, asn1.jer]) {
      const original = rule.encode(R.Choice2, { b: 'hello' });
      const unknown = rule.decode(R.Choice1, original);
      assert.ok('$unknownExtension' in unknown);
      assert.deepEqual(rule.encode(R.Choice1, unknown), original);
    }
  });

  it('loads from bytes and from a compiled module, and rejects bad schemas', async () => {
    const wasm = readFileSync(fileURLToPath(new URL('../lib/dotasn.wasm', import.meta.url)));
    const fromBytes = await loadDotAsn(telemetrySchema, { wasm });
    assert.equal(rt.toHex(fromBytes.uper.encode(T.Heartbeat, heartbeat)), '07EE6B2800');
    const fromModule = await loadDotAsn(telemetrySchema, { wasm: await WebAssembly.compile(wasm) });
    assert.equal(rt.toHex(fromModule.uper.encode(T.Heartbeat, heartbeat)), '07EE6B2800');
    await assert.rejects(loadDotAsn('{"format":"something-else"}'), /not a DotAsn schema/);
    await assert.rejects(loadDotAsn('{"format":"dotasn-schema","version":99,"names":{},"types":[]}'), /unsupported schema version/);
    const disposed = await loadDotAsn(telemetrySchema);
    disposed.dispose();
    assert.throws(() => disposed.uper.encode(T.Heartbeat, heartbeat), /disposed/);
  });
});
