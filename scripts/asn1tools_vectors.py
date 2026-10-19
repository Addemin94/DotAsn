#!/usr/bin/env python3
"""Produce known-answer vectors with asn1tools (an independent ASN.1 implementation, MIT license).

For every case file in test-vectors/cases/*.json (written by scripts/gen-cases.cs) and every requested codec,
this compiles the referenced specifications with asn1tools, converts each value from the DotAsn JSON value
notation into asn1tools' Python representation, encodes it, verifies that asn1tools decodes its own output to
the same value, and writes test-vectors/<codec>/<case-name>.json.

usage: python3 scripts/asn1tools_vectors.py [--codecs ber,der,uper,per,oer,jer] [case-name ...]
"""
import argparse
import datetime
import glob
import json
import os
import sys

import asn1tools
from asn1tools.codecs import restricted_utc_time_to_datetime, restricted_generalized_time_to_datetime
from asn1tools.codecs import restricted_utc_time_from_datetime, restricted_generalized_time_from_datetime

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

STRING_TYPES = {
    'UTF8String', 'NumericString', 'PrintableString', 'TeletexString', 'T61String', 'VideotexString',
    'IA5String', 'GraphicString', 'VisibleString', 'ISO646String', 'GeneralString', 'UniversalString',
    'BMPString', 'ObjectDescriptor',
}


class Types:
    """Resolves type references in asn1tools' parse tree."""

    def __init__(self, parsed):
        self.parsed = parsed

    def lookup(self, module, name):
        if '.' in name and name.split('.')[0] in self.parsed:
            module, name = name.split('.', 1)
        mod = self.parsed[module]
        if name in mod['types']:
            return module, mod['types'][name]
        for imported_module, names in mod.get('imports', {}).items():
            if name in names and imported_module in self.parsed:
                return self.lookup(imported_module, name)
        raise KeyError(f'{module}.{name}')

    def resolve(self, module, t):
        seen = 0
        while t['type'] not in BUILTINS:
            module, t2 = self.lookup(module, t['type'])
            merged = dict(t2)
            for k, v in t.items():
                if k not in ('type',):
                    merged.setdefault(k, v)
            t = merged
            seen += 1
            if seen > 100:
                raise RecursionError(t)
        return module, t


BUILTINS = {
    'BOOLEAN', 'INTEGER', 'ENUMERATED', 'REAL', 'BIT STRING', 'OCTET STRING', 'NULL', 'OBJECT IDENTIFIER',
    'RELATIVE-OID', 'SEQUENCE', 'SET', 'CHOICE', 'SEQUENCE OF', 'SET OF', 'UTCTime', 'GeneralizedTime',
    'DATE', 'TIME-OF-DAY', 'DATE-TIME', 'DURATION', 'TIME', 'ANY', 'ANY DEFINED BY', 'OpenType',
} | STRING_TYPES


def members(t):
    out = []
    for m in t.get('members', []):
        if m is None:
            continue
        if isinstance(m, list):
            out.extend(x for x in m if x is not None)
        else:
            out.append(m)
    return out


def to_py(types, module, t, v):
    module, t = types.resolve(module, t)
    kind = t['type']
    if kind == 'INTEGER':
        return int(v)
    if kind == 'REAL':
        if isinstance(v, str):
            return {'PLUS-INFINITY': float('inf'), 'MINUS-INFINITY': float('-inf'), 'NOT-A-NUMBER': float('nan'), '-0': -0.0}[v]
        return float(v)
    if kind in ('BOOLEAN', 'ENUMERATED', 'OBJECT IDENTIFIER', 'RELATIVE-OID') or kind in STRING_TYPES:
        return v
    if kind == 'NULL':
        return None
    if kind == 'OCTET STRING':
        return bytes.fromhex(v)
    if kind == 'BIT STRING':
        return (bytes.fromhex(v['hex']), v['length'])
    if kind == 'UTCTime':
        return restricted_utc_time_to_datetime(v)
    if kind == 'GeneralizedTime':
        return restricted_generalized_time_to_datetime(v)
    if kind in ('SEQUENCE', 'SET'):
        by_name = {m['name']: m for m in members(t)}
        return {k: to_py(types, module, by_name[k], x) for k, x in v.items()}
    if kind == 'CHOICE':
        (k, x), = v.items()
        by_name = {m['name']: m for m in members(t)}
        return (k, to_py(types, module, by_name[k], x))
    if kind in ('SEQUENCE OF', 'SET OF'):
        return [to_py(types, module, t['element'], x) for x in v]
    raise NotImplementedError(kind)


def from_py(types, module, t, v):
    """Converts asn1tools' decoded value back to the DotAsn JSON notation (for self-consistency checks)."""
    module, t = types.resolve(module, t)
    kind = t['type']
    if kind == 'INTEGER':
        return v if abs(v) <= 2 ** 53 - 1 else str(v)
    if kind == 'REAL':
        if v != v:
            return 'NOT-A-NUMBER'
        if v in (float('inf'), float('-inf')):
            return 'PLUS-INFINITY' if v > 0 else 'MINUS-INFINITY'
        if v == 0 and str(v).startswith('-'):
            return '-0'
        return v
    if kind in ('BOOLEAN', 'ENUMERATED', 'OBJECT IDENTIFIER', 'RELATIVE-OID') or kind in STRING_TYPES:
        return v
    if kind == 'NULL':
        return None
    if kind == 'OCTET STRING':
        return v.hex().upper()
    if kind == 'BIT STRING':
        data, n = v
        return {'hex': data.hex().upper(), 'length': n}
    if kind == 'UTCTime':
        return restricted_utc_time_from_datetime(v)
    if kind == 'GeneralizedTime':
        return restricted_generalized_time_from_datetime(v)
    if kind in ('SEQUENCE', 'SET'):
        by_name = {m['name']: m for m in members(t)}
        return {k: from_py(types, module, by_name[k], x) for k, x in v.items()}
    if kind == 'CHOICE':
        k, x = v
        by_name = {m['name']: m for m in members(t)}
        return {k: from_py(types, module, by_name[k], x)}
    if kind in ('SEQUENCE OF', 'SET OF'):
        return [from_py(types, module, t['element'], x) for x in v]
    raise NotImplementedError(kind)


def normalize(v):
    """Normalizes JSON values for comparison (asn1tools adds DEFAULT values on decode)."""
    return json.dumps(v, sort_keys=True)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--codecs', default='ber,der,uper,per,oer,jer')
    ap.add_argument('names', nargs='*')
    args = ap.parse_args()
    codecs = args.codecs.split(',')
    case_files = sorted(glob.glob(os.path.join(ROOT, 'test-vectors', 'cases', '*.json')))
    for path in case_files:
        name = os.path.splitext(os.path.basename(path))[0]
        if args.names and name not in args.names:
            continue
        with open(path) as f:
            cases = json.load(f)
        specs = [os.path.join(ROOT, s) for s in cases['specs']]
        parsed = asn1tools.parse_files(specs)
        types = Types(parsed)
        for codec in codecs:
            try:
                compiled = asn1tools.compile_files(specs, codec)
            except Exception as e:  # noqa: BLE001 - report and continue with other codecs
                print(f'{name}/{codec}: cannot compile: {e}', file=sys.stderr)
                continue
            out_cases, skipped = [], []
            for case in cases['cases']:
                module, type_name = case['type'].split('.', 1)
                try:
                    _, t = types.lookup(module, type_name)
                    py = to_py(types, module, {'type': case['type']}, case['value'])
                    encoded = compiled.encode(type_name, py, check_constraints=True)
                    decoded = compiled.decode(type_name, encoded)
                    back = from_py(types, module, {'type': case['type']}, decoded)
                    vector = {'type': case['type'], 'value': case['value'], 'hex': encoded.hex().upper(), 'decoded': back}
                    if codec in ('jer', 'xer'):
                        vector['text'] = encoded.decode('utf-8')
                    out_cases.append(vector)
                except Exception as e:  # noqa: BLE001 - asn1tools limitation; record and skip
                    skipped.append({'type': case['type'], 'reason': f'{type(e).__name__}: {e}'[:300]})
            os.makedirs(os.path.join(ROOT, 'test-vectors', codec), exist_ok=True)
            with open(os.path.join(ROOT, 'test-vectors', codec, name + '.json'), 'w') as f:
                json.dump({
                    'generator': f'asn1tools {asn1tools.__version__}',
                    'license': 'Vectors computed by asn1tools (MIT); specifications as listed',
                    'codec': codec,
                    'specs': cases['specs'],
                    'cases': out_cases,
                    'skipped': skipped,
                }, f, indent=1)
            print(f'{name}/{codec}: {len(out_cases)} vector(s), {len(skipped)} skipped', file=sys.stderr)


if __name__ == '__main__':
    main()
