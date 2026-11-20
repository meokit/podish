"""Estimate decoded block payloads from the version 3 blocks.bin format."""

import argparse
import json
from pathlib import Path
from struct import unpack_from


def estimate(path: Path) -> dict:
    raw = path.read_bytes()
    magic, version, _, block_count, handler_count = unpack_from("<IIQII", raw)
    if magic != 0x324B4C42 or version != 3:
        raise ValueError(f"{path}: expected BLK2 version 3")

    cursor = 24

    def skip_string():
        nonlocal cursor
        size, = unpack_from("<I", raw, cursor)
        cursor += 4 + size

    for _ in range(handler_count):
        cursor += 16
        skip_string()
    operation_count, = unpack_from("<I", raw, cursor)
    cursor += 4
    for _ in range(operation_count):
        cursor += 4
        skip_string()

    ops = memory = controls = control_blocks = noncontiguous = 0
    weighted_ops = weighted_memory = weighted_controls = 0
    for _ in range(block_count):
        pc, _, count, weight = unpack_from("<IIIQ", raw, cursor)
        cursor += 20
        block_has_control = False
        for _ in range(count):
            _, next_eip, length, _, _, meta = unpack_from("<QIBBBB", raw, cursor)
            cursor += 36
            has_memory = bool(meta & 2)
            has_control = meta >> 6 == 2
            block_has_control |= has_control
            memory += has_memory
            controls += has_control
            noncontiguous += (next_eip - length) & 0xFFFFFFFF != pc
            pc = next_eip
            weighted_memory += weight * has_memory
            weighted_controls += weight * has_control
        ops += count
        weighted_ops += weight * count
        control_blocks += block_has_control

    if cursor != len(raw):
        raise ValueError(f"{path}: {len(raw) - cursor} unexpected trailing bytes")

    slots = ops + block_count  # The dump omits one sentinel per block.
    original = 48 * block_count + 32 * slots
    compact = 48 * block_count + 24 * slots + 8 * (controls + control_blocks)
    save = 100 * (original - compact) / original
    return {
        "profile": str(path), "blocks": block_count, "op_slots": ops,
        "memory_slots": memory, "control_slots": controls, "blocks_with_controls": control_blocks,
        "pc_anchor_discontinuities": noncontiguous,
        "slot_entry_weight": weighted_ops, "memory_slot_entry_weight": weighted_memory,
        "control_slot_entry_weight": weighted_controls,
        "old_block_payload_bytes": original, "estimated_compact_payload_bytes": compact,
        "estimated_reduction_pct": round(save, 2),
        "estimated_reduction_pct_with_all_replay_handlers": round(
            100 * (original - compact) / (original + 8 * ops), 2
        ),
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("profiles", type=Path, nargs="+")
    args = parser.parse_args()
    print(json.dumps([estimate(path) for path in args.profiles], indent=2))


if __name__ == "__main__":
    main()
