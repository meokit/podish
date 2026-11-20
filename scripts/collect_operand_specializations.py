#!/usr/bin/env python3
"""Export a CMake operand specialization table from one or more BLK2 v3 profiles."""

import argparse
import struct
from collections import Counter
from dataclasses import asdict
import json
from pathlib import Path

from gen_instruction_catalog import read_catalog
from operand_specialization_data import (
    HAS_IMMEDIATE, HAS_MEMORY, METADATA_MASK, Operand, generated_fusions, training_handlers,
)


ROOT = Path(__file__).resolve().parents[1]
SRC = ROOT / "libfibercpu/src"


def blocks(path):
    with path.open("rb") as stream:
        def read(size):
            data = stream.read(size)
            if len(data) != size:
                raise ValueError(f"{path}: truncated block profile")
            return data

        magic, version, _, block_count, handler_count = struct.unpack("<IIQii", read(24))
        if (magic, version) != (0x324b4c42, 3) or min(block_count, handler_count) < 0:
            raise ValueError(f"{path}: expected BLK2 version 3 with training IDs; recapture the profile")
        for _ in range(handler_count):
            _, _, _, size = struct.unpack("<iiQI", read(20))
            read(size)  # Diagnostic metadata has no role in training.

        operation_count, = struct.unpack("<I", read(4))
        for _ in range(operation_count):
            _, size = struct.unpack("<II", read(8))
            read(size)  # The catalog labels are for analysis and code generation.

        for _ in range(block_count):
            _, _, count, executions = struct.unpack("<IIIQ", read(20))
            if count > 255:
                raise ValueError(f"{path}: invalid block size {count}")
            payload = read(36 * count)
            if not executions:
                continue
            ops = []
            for mem, _, length, modrm, prefixes, meta, imm, training_id, _, _ in struct.iter_unpack(
                    "<QIBBBBIIQi", payload):
                operand = None
                meta &= METADATA_MASK
                if 1 <= length <= 15 and meta >> 6 in (0, 2):
                    data_memory = meta & HAS_MEMORY and meta >> 6 == 0
                    operand = Operand(modrm, prefixes, meta, length, mem >> 32 if data_memory else 0,
                                      mem & 0xffffffff if data_memory else 0, imm if meta & HAS_IMMEDIATE else 0)
                ops.append((training_id, operand))
            yield executions, ops
        if stream.read(1):
            raise ValueError(f"{path}: unexpected trailing data")


def collect(profiles, fusion_source, limit):
    catalog = read_catalog()
    handlers = training_handlers(catalog, generated_fusions(fusion_source.read_text()))
    scalar_ids = {handler.name: op_id for op_id, handler in handlers.items() if handler.kind == "wrapper"}
    observed, dispatches, skipped = Counter(), Counter(), Counter()
    for path in profiles:
        for executions, ops in blocks(path):
            cursor = 0
            while cursor < len(ops):
                op_id, operand = ops[cursor]
                handler = handlers.get(op_id)
                if op_id and handler is None:
                    raise ValueError(f"{path}: unknown training ID 0x{op_id:08x}; check the capture catalog and --fusions")
                if handler is None or operand is None:
                    skipped[op_id] += executions
                    cursor += 1
                    continue
                width = len(handler.operations)
                operands = tuple(op for _, op in ops[cursor:cursor + width])
                if len(operands) != width or any(op is None for op in operands):
                    skipped[op_id] += executions
                    cursor += 1
                    continue
                key = (op_id, operands)
                dispatches[key] += executions
                observed[key] += executions
                if width > 1:
                    for name, shape in zip(handler.operations, operands):
                        observed[scalar_ids[name], (shape,)] += executions
                cursor += width
    ranked = sorted(observed, key=lambda key: (-dispatches[key], -observed[key], key))
    entries = [
        dict(kind=handlers[op_id].kind, originalId=op_id, width=len(operands),
             operands=[asdict(op) for op in operands], count=observed[op_id, operands],
             dispatch_count=dispatches[op_id, operands])
        for op_id, operands in ranked[:limit]
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
        args.output.write_text(json.dumps(dict(version=3, entries=entries), indent=2) + "\n")
    except (ValueError, OSError, struct.error) as error:
        parser.error(str(error))
    wrappers = sum(entry["kind"] == "wrapper" for entry in entries)
    print(f"Wrote {len(entries)} of {observed} observed shapes: {wrappers} scalar, {len(entries) - wrappers} fused")
    if skipped:
        print(f"Skipped {sum(skipped.values())} estimates from {len(skipped)} unresolved or unsupported identities")
        for op_id, count in skipped.most_common(5):
            print(f"  {count:>12}  training ID 0x{op_id:08x}")


if __name__ == "__main__":
    main()
