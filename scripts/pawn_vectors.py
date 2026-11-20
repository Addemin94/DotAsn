#!/usr/bin/env python3
"""Run the OER known-answer vectors through the generated Pawn encoders and decoders.

For every vector file in test-vectors/oer/*.json this generates Pawn for the file's specifications
(asn1 generate-pawn), fills each case's value layout from its JSON value with the help of the layout manifest,
writes one Pawn program that encodes and decodes every case, compiles it with pawncc and runs it on the host.

- Encoding must give the vector's bytes exactly (COER).
- Decoding the vector's bytes must consume all of them and give the layout of the decoded value.

Cases are skipped when their type is not generated (outside the Pawn subset), or their value does not fit a layout,
such as an integer beyond 32 bits. A value with extension additions, which a layout does not hold, is only decoded,
ignoring the additions. For the asn1tools deviations listed in test-vectors/deviations.json the expected bytes are
DotAsn's own COER encoding of the value, as in the C# interoperability tests.

usage: python3 scripts/pawn_vectors.py --pawncc PATH --pawn-source DIR [vector-file ...]

--pawn-source is a Pawn 4 source tree (amx/, linux/, include/), for the host runner and console.inc.
"""
import argparse
import glob
import json
import os
import subprocess
import sys
import tempfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CLI = os.path.join(ROOT, "src", "Asn1.Cli", "bin", "Debug", "net10.0", "asn1.dll")
CASES_PER_PROGRAM = 60


class Unsupported(Exception):
    """The value cannot be held by the layout."""


class ExtensionValue(Exception):
    """The value holds extension additions: decodable, but not encodable from a layout."""


def to_cell(number):
    number &= 0xFFFFFFFF
    return number - (1 << 32) if number & 0x80000000 else number


def pack_bytes(data, cells, base):
    for index, byte in enumerate(data):
        cell = base + (index >> 2)
        shift = 24 - ((index & 3) << 3)
        cells[cell] = to_cell((cells[cell] & 0xFFFFFFFF) | (byte << shift))


class LayoutFiller:
    def __init__(self, manifest, ignore_extensions):
        self.types = manifest["types"]
        self.ignore_extensions = ignore_extensions

    def fill(self, layout, value, cells, base):
        kind = layout["kind"]
        if kind == "reference":
            self.fill(self.types[layout["type"]]["layout"], value, cells, base)
        elif kind == "boolean":
            cells[base] = 1 if value else 0
        elif kind == "integer":
            if not isinstance(value, int):
                raise Unsupported(f"integer value {value!r}")
            low, high = (-(1 << 31), (1 << 31) - 1) if layout["signed"] else (0, (1 << 32) - 1)
            if (value < low) or (value > high):
                raise Unsupported(f"integer value {value} does not fit a 32-bit cell")
            cells[base] = to_cell(value)
        elif kind == "enumerated":
            if value not in layout["items"]:
                raise Unsupported(f"enumeration {value!r} is not a root item")
            cells[base] = to_cell(layout["items"][value])
        elif kind == "null":
            pass
        elif kind in ("octets", "characters", "utf8", "bits"):
            self.fill_string(layout, value, cells, base)
        elif kind == "sequence":
            self.fill_sequence(layout, value, cells, base)
        elif kind == "choice":
            (name, inner), = value.items()
            alternatives = [a for a in layout["alternatives"] if a["name"] == name]
            if not alternatives:
                raise ExtensionValue(f"alternative {name} is an extension")
            cells[base] = alternatives[0]["index"]
            self.fill(alternatives[0]["layout"], inner, cells, base + 1)
        elif kind == "sequenceOf":
            if len(value) > layout["maximum"]:
                raise Unsupported("too many items")
            cells[base] = len(value)
            stride = layout["element"]["cells"]
            for index, item in enumerate(value):
                self.fill(layout["element"], item, cells, base + 1 + (index * stride))
        else:
            raise Unsupported(kind)

    def fill_string(self, layout, value, cells, base):
        kind = layout["kind"]
        if kind == "octets":
            data = bytes.fromhex(value)
            length = len(data)
        elif kind == "bits":
            data = bytes.fromhex(value["hex"])
            length = value["length"]
        elif kind == "utf8":
            data = value.encode("utf-8")
            length = len(data)
        else:
            data = value.encode("latin-1")
            length = len(data)
        if (length < layout["minimum"]) or (length > layout["maximum"]):
            raise Unsupported("string length outside its bounds")
        if not layout["fixed"]:
            cells[base] = length
        pack_bytes(data, cells, base + layout["dataOffset"])

    def fill_sequence(self, layout, value, cells, base):
        names = {field["name"] for field in layout["fields"]}
        extra = [key for key in value if key not in names]
        if extra and not self.ignore_extensions:
            raise ExtensionValue(f"extension additions {extra}")
        for field in layout["fields"]:
            present = field["name"] in value
            if field["presence"] == "default" and "default" in field:
                inner = field["layout"]
                if inner["kind"] == "reference":
                    inner = self.types[inner["type"]]["layout"]
                scratch = [0] * max(inner["cells"], 1)
                if present:
                    self.fill(inner, value[field["name"]], scratch, 0)
                if (not present) or (scratch[0] == to_cell(field["default"])):
                    cells[base + field["presenceOffset"]] = 0
                    cells[base + field["offset"]] = to_cell(field["default"])
                    continue
            if field["presence"] != "required":
                cells[base + field["presenceOffset"]] = 1 if present else 0
            if present:
                self.fill(field["layout"], value[field["name"]], cells, base + field["offset"])
            elif field["presence"] == "required":
                raise Unsupported(f"missing component {field['name']}")


def cell_literal(number):
    return f"0x{number & 0xFFFFFFFF:08X}" if number < 0 else str(number)


def array_literal(values, indent):
    """An unpacked Pawn array literal (Pawn 4 packs braces), split into lines Pawn accepts."""
    rows = [", ".join(values[i:i + 12]) for i in range(0, len(values), 12)]
    return "[\n" + ",\n".join(indent + "    " + row for row in rows) + "\n" + indent + "]"


def pawn_program(includes, cases):
    lines = ["#include <console>"]
    lines += [f'#include "{include}"' for include in includes]
    lines += [
        "",
        "print_hex(const buffer{}, count) {",
        "    for (new index = 0; index < count; index++) {",
        '        printf("%02x", buffer{index});',
        "    }",
        "}",
        "",
        "main() {",
        "    new buffer{4096};",
        "    new position;",
    ]
    for case in cases:
        cells = max(case["cells"], 1)
        if case.get("encode_cells") is not None:
            initial = array_literal([cell_literal(c) for c in case["encode_cells"]], "        ")
            lines += [
                "    {",
                f"        new value[{cells}] = {initial};",
                "        position = 0;",
                f"        new ok = {case['encode']}(value, buffer, position, 4096) ? 1 : 0;",
                f'        printf("E {case["id"]} %d ", ok);',
                "        print_hex(buffer, position);",
                '        printf("\\n");',
                "    }",
            ]
        if case.get("decode_cells") is not None:
            data = case["bytes"]
            literal = array_literal([str(b) for b in data] if data else ["0"], "        ")
            lines += [
                "    {",
                f"        new input[{max(len(data), 1)}] = {literal};",
                f"        for (new index = 0; index < {len(data)}; index++) {{",
                "            buffer{index} = input[index];",
                "        }",
                f"        new value[{cells}];",
                "        position = 0;",
                f"        new ok = {case['decode']}(value, buffer, position, {len(data)}) ? 1 : 0;",
                f'        printf("D {case["id"]} %d %d", ok, position);',
                f"        for (new index = 0; index < {case['cells']}; index++) {{",
                '            printf(" %d", value[index]);',
                "        }",
                '        printf("\\n");',
                "    }",
            ]
    lines += ["}", ""]
    return "\n".join(lines)


def build_runner(pawn_source, work):
    runner = os.path.join(work, "pawn_runner")
    amx = os.path.join(pawn_source, "amx")
    linux = os.path.join(pawn_source, "linux")
    sources = [os.path.join(ROOT, "scripts", "pawn_runner.c")]
    sources += [os.path.join(amx, name) for name in ("amx.c", "amxaux.c", "amxcons.c", "amxcore.c")]
    sources += [os.path.join(linux, "getch.c")]
    subprocess.run(
        ["gcc", "-O1", "-w", f"-I{amx}", f"-I{linux}", "-DHAVE_STDINT_H", "-DPAWN_CELL_SIZE=32",
         "-DAMX_ANSIONLY", "-DAMX_NODYNALOAD", "-o", runner] + sources,
        check=True,
    )
    return runner


def deviations():
    listed = json.load(open(os.path.join(ROOT, "test-vectors", "deviations.json")))["deviations"]
    return {(d["file"], d["type"]) for d in listed if d["codec"] == "oer"}


def dotasn_coer(specs, type_name, value):
    """DotAsn's own COER encoding of a value, as hex."""
    encoded = subprocess.run(
        ["dotnet", CLI, "encode", "--quiet", "-t", type_name, "-f", "coer", "--value", json.dumps(value)] + specs,
        capture_output=True, text=True,
    )
    return encoded.stdout.strip().lower() if encoded.returncode == 0 else None


def run_file(path, args, runner, work):
    vectors = json.load(open(path))
    name = os.path.splitext(os.path.basename(path))[0]
    deviated = deviations()
    out = os.path.join(work, name)
    specs = [os.path.join(ROOT, spec) for spec in vectors["specs"]]
    generated = subprocess.run(
        ["dotnet", CLI, "generate-pawn", "--quiet", "-o", out] + specs, capture_output=True, text=True
    )
    if not os.path.exists(os.path.join(out, "dotasn-pawn-layout.json")):
        print(f"{name}: generation failed\n{generated.stdout}{generated.stderr}")
        return {"failed": 1}
    manifest = json.load(open(os.path.join(out, "dotasn-pawn-layout.json")))
    includes = sorted({entry["file"] for entry in manifest["types"].values()})
    encoder = LayoutFiller(manifest, ignore_extensions=False)
    decoder = LayoutFiller(manifest, ignore_extensions=True)

    stats = {"encoded": 0, "decoded": 0, "failed": 0, "skipped_type": 0, "skipped_value": 0}
    cases = []
    for index, case in enumerate(vectors["cases"]):
        entry = manifest["types"].get(case["type"])
        if entry is None:
            stats["skipped_type"] += 1
            continue
        expected = case["hex"].lower()
        if ((name, case["type"]) in deviated) or ((name, "*") in deviated):
            expected = dotasn_coer(specs, case["type"], case["value"])
            if expected is None:
                stats["skipped_value"] += 1
                continue
            case = dict(case, decoded=case["value"])
        item = {
            "id": index, "type": case["type"], "cells": entry["cells"], "encode": entry["encode"],
            "decode": entry["decode"], "bytes": list(bytes.fromhex(expected)), "hex": expected,
        }
        try:
            cells = [0] * entry["cells"]
            encoder.fill(entry["layout"], case["value"], cells, 0)
            item["encode_cells"] = cells
        except (Unsupported, ExtensionValue, KeyError, ValueError, TypeError, AttributeError):
            item["encode_cells"] = None
        try:
            cells = [0] * entry["cells"]
            decoder.fill(entry["layout"], case.get("decoded", case["value"]), cells, 0)
            item["decode_cells"] = cells
        except (Unsupported, ExtensionValue, KeyError, ValueError, TypeError, AttributeError):
            item["decode_cells"] = None
        if item["encode_cells"] is None and item["decode_cells"] is None:
            stats["skipped_value"] += 1
            continue
        cases.append(item)

    by_id = {case["id"]: case for case in cases}
    for start in range(0, len(cases), CASES_PER_PROGRAM):
        chunk = cases[start:start + CASES_PER_PROGRAM]
        source = os.path.join(out, f"vectors_{start}.p")
        with open(source, "w") as handle:
            handle.write(pawn_program(includes, chunk))
        program = os.path.join(out, f"vectors_{start}.amx")
        compiled = subprocess.run(
            [args.pawncc, source, f"-i{os.path.join(args.pawn_source, 'include')}", f"-i{out}", "-S65536",
             f"-o{program}"] + args.pawncc_options.split(),
            capture_output=True, text=True,
        )
        if compiled.returncode != 0:
            print(f"{name}: pawncc failed on {source}\n{compiled.stdout}{compiled.stderr}")
            stats["failed"] += len(chunk)
            continue
        ran = subprocess.run([runner, program], capture_output=True, text=True)
        if ran.returncode != 0:
            print(f"{name}: run failed on {source}: {ran.stderr}")
        for line in ran.stdout.splitlines():
            parts = line.split()
            if len(parts) < 3 or parts[0] not in ("E", "D"):
                continue
            case = by_id[int(parts[1])]
            if parts[0] == "E":
                produced = parts[3].lower() if len(parts) > 3 else ""
                if parts[2] == "1" and produced == case["hex"]:
                    stats["encoded"] += 1
                else:
                    stats["failed"] += 1
                    print(f"{name}#{case['id']} {case['type']} encode: ok={parts[2]} got {produced} want {case['hex']}")
            else:
                consumed = int(parts[3])
                got = [int(p) for p in parts[4:]]
                if parts[2] == "1" and consumed == len(case["bytes"]) and got == case["decode_cells"]:
                    stats["decoded"] += 1
                else:
                    stats["failed"] += 1
                    print(f"{name}#{case['id']} {case['type']} decode: ok={parts[2]} consumed={consumed} "
                          f"got {got} want {case['decode_cells']}")
    print(f"{name}: " + " ".join(f"{key}={value}" for key, value in stats.items()))
    return stats


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--pawncc", required=True, help="the Pawn 4 compiler")
    parser.add_argument("--pawn-source", required=True, help="a Pawn 4 source tree, for the host runner and console.inc")
    parser.add_argument("--keep", help="keep the generated programs in this directory")
    parser.add_argument("--pawncc-options", default="", help="extra pawncc options, e.g. \"-d0 -O3\"")
    parser.add_argument("files", nargs="*", help="vector files (default: test-vectors/oer/*.json)")
    args = parser.parse_args()
    files = args.files or sorted(glob.glob(os.path.join(ROOT, "test-vectors", "oer", "*.json")))
    work = args.keep or tempfile.mkdtemp(prefix="pawn_vectors_")
    os.makedirs(work, exist_ok=True)
    runner = build_runner(args.pawn_source, work)
    total = {}
    for path in files:
        for key, value in run_file(path, args, runner, work).items():
            total[key] = total.get(key, 0) + value
    print("total: " + " ".join(f"{key}={value}" for key, value in total.items()))
    return 1 if total.get("failed") else 0


if __name__ == "__main__":
    sys.exit(main())
