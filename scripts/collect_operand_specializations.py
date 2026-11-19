#!/usr/bin/env python3
"""Export a CMake operand specialization table from one or more BLK2 v2 profiles."""

import argparse
import re
import struct
from collections import Counter
from dataclasses import asdict
import json
from pathlib import Path

from gen_instruction_catalog import read_catalog
from operand_specialization_data import (
    FUSION_CALL, HAS_IMMEDIATE, HAS_MEMORY, METADATA_MASK, Operand, generated_fusions, instruction_targets,
)


ROOT = Path(__file__).resolve().parents[1]
SRC = ROOT / "libfibercpu/src"
WRAPPER = re.compile(r'\bDispatchWrapper<\s*&?[^()]*?\bop::(\w+)(?:<([^<>]*)>)?(?=\s*[(,>])')
FUSED = re.compile(r'\b(?:OperandClone_\d+_)?((?:SuperOpcode_|HandWrittenSuperOpcode_)\w+)\(')


def scalar_name(symbol):
    match = WRAPPER.search(symbol)
    if not match:
        return None
    name, arguments = match.groups()
    if not arguments:
        return name
    # Reflection names template instantiations, whereas the instruction catalog
    # names their portable C++ aliases.
    if name == "OpRotateRegImmediate":
        rotation = re.fullmatch(r'(?:unsigned (short|int)|uint(16|32)_t),\s*(true|false|0|1),\s*(true|false|0|1)',
                                arguments.strip())
        if rotation:
            builtin, width, right, live = rotation.groups()
            if width is None:
                width = "16" if builtin == "short" else "32"
            direction = "Ror" if right in ("true", "1") else "Rol"
            liveness = "Live" if live in ("true", "1") else "Dead"
            return f"Op{direction}_RegImm{width}_{liveness}"
    return None


def blocks(path):
    with path.open("rb") as stream:
        def read(size):
            data = stream.read(size)
            if len(data) != size:
                raise ValueError(f"{path}: truncated block profile")
            return data

        magic, version, _, block_count, handler_count = struct.unpack("<IIQii", read(24))
        if (magic, version) != (0x324b4c42, 2) or min(block_count, handler_count) < 0:
            raise ValueError(f"{path}: expected BLK2 version 2 with embedded handler symbols")
        handlers = {}
        for _ in range(handler_count):
            handler_id, _, _, size = struct.unpack("<iiQI", read(20))
            handlers[handler_id] = read(size).decode("utf-8")

        for _ in range(block_count):
            _, _, count, executions = struct.unpack("<IIIQ", read(20))
            if count > 255:
                raise ValueError(f"{path}: invalid block size {count}")
            payload = read(36 * count)
            if not executions:
                continue
            ops = []
            for mem, _, length, modrm, prefixes, meta, imm, _, _, handler in struct.iter_unpack(
                    "<QIBBBBIIQi", payload):
                operand = None
                meta &= METADATA_MASK
                if 1 <= length <= 15 and meta >> 6 in (0, 2):
                    data_memory = meta & HAS_MEMORY and meta >> 6 == 0
                    operand = Operand(modrm, prefixes, meta, length, mem >> 32 if data_memory else 0,
                                      mem & 0xffffffff if data_memory else 0, imm if meta & HAS_IMMEDIATE else 0)
                ops.append((handlers.get(handler, ""), operand))
            yield executions, ops
        if stream.read(1):
            raise ValueError(f"{path}: unexpected trailing data")


def collect(profiles, fusion_source, limit):
    catalog = read_catalog()
    targets = instruction_targets(catalog)
    fusions = {
        name: tuple(call[1] for call in FUSION_CALL.finditer(body))
        for name, body in generated_fusions(fusion_source.read_text()).items()
    }
    handwritten = {row["name"]: tuple(row["operations"]) for row in catalog["handwritten_fusions"]}
    sources = fusions | handwritten
    observed, dispatches, skipped = Counter(), Counter(), Counter()
    for path in profiles:
        for executions, ops in blocks(path):
            cursor = 0
            while cursor < len(ops):
                symbol, operand = ops[cursor]
                match = FUSED.search(symbol)
                if match and (logic := sources.get(match[1])):
                    width = len(logic)
                    operands = tuple(op for _, op in ops[cursor:cursor + width])
                    if len(operands) == width and all(op is not None for op in operands):
                        if match[1] in fusions:
                            key = ("fused", match[1], operands)
                            dispatches[key] += executions
                            observed[key] += executions
                        for name, shape in zip(logic, operands):
                            if name in targets:
                                observed["wrapper", f"DispatchWrapper<op::{name}>", (shape,)] += executions
                        cursor += width
                        continue
                name = scalar_name(symbol)
                if not operand or name not in targets:
                    skipped[match[1] if match else name or symbol or "<unknown handler>"] += executions
                else:
                    key = ("wrapper", f"DispatchWrapper<op::{name}>", (operand,))
                    dispatches[key] += executions
                    observed[key] += executions
                cursor += 1
    ranked = sorted(observed, key=lambda key: (-dispatches[key], -observed[key], key))
    entries = [
        dict(kind=kind, original=original, width=len(operands), operands=[asdict(op) for op in operands],
             count=observed[kind, original, operands], dispatch_count=dispatches[kind, original, operands])
        for kind, original, operands in ranked[:limit]
    ]
    return entries, len(ranked), skipped


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("profiles", type=Path, nargs="+", help="blocks.bin files or guest stats directories")
    parser.add_argument("--fusions", type=Path, default=SRC / "generated/superopcodes.extended.generated.cpp",
                        help="C++ fusion source selected when capturing the profiles")
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--limit", type=int, help="Export only this many rows (default: all observed rows)")
    args = parser.parse_args()
    if args.limit is not None and args.limit <= 0:
        parser.error("--limit must be positive")
    profiles = [path / "blocks.bin" if path.is_dir() else path for path in args.profiles]
    try:
        entries, observed, skipped = collect(profiles, args.fusions, args.limit)
        if not entries:
            raise ValueError("no eligible executed instructions found; capture with EnableBlockProfile=true")
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps(dict(version=2, entries=entries), indent=2) + "\n")
    except (ValueError, OSError, struct.error) as error:
        parser.error(str(error))
    wrappers = sum(entry["kind"] == "wrapper" for entry in entries)
    print(f"Wrote {len(entries)} of {observed} observed shapes: {wrappers} scalar, {len(entries) - wrappers} fused")
    if skipped:
        print(f"Skipped {sum(skipped.values())} estimates from {len(skipped)} unresolved or unsupported identities")
        for name, count in skipped.most_common(5):
            print(f"  {count:>12}  {name}")


if __name__ == "__main__":
    main()
