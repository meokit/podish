#!/usr/bin/env python3
"""Bake canonical SuperOpcode recipes and exact operands from unfused BLK2 captures."""

import argparse
from collections import Counter
from dataclasses import asdict
import json
from pathlib import Path

from collect_operand_specializations import blocks
from gen_instruction_catalog import read_catalog
from operand_specialization_data import fusion_training_id, training_handlers


def sequences(profile, handlers):
    for executions, ops in blocks(profile):
        for start, (op_id, _) in enumerate(ops):
            if op_id not in handlers:
                continue
            ids, shapes = [], []
            for candidate, shape in ops[start:start + 4]:
                handler = handlers.get(candidate)
                if not handler or handler.kind != "wrapper" or shape is None:
                    break
                ids.append(candidate)
                shapes.append(shape)
                if len(ids) >= 2:
                    yield executions, tuple(ids), tuple(shapes)
                # A branch (including one created by block concatenation) ends
                # this sequence. Replay and target selection stay with its last op.
                if shape.meta & 8:
                    break


def rank_candidates(profiles, handlers, top):
    scores = Counter()
    for profile in profiles:
        counts = Counter()
        for count, ids, _ in sequences(profile, handlers):
            counts[ids] += count
        total = sum(counts.values())
        for ids, count in counts.items():
            scores[ids] += count * (len(ids) - 1) / total
    return sorted(scores, key=lambda ids: (-scores[ids], ids))[:top]


def rank_shapes(profiles, handlers, candidates, budget):
    selected = set(candidates)
    scores, visits = Counter(), Counter()
    for profile in profiles:
        counts = Counter()
        for count, ids, shapes in sequences(profile, handlers):
            if ids in selected:
                counts[ids, shapes] += count
        visits.update(counts)
        total = sum(counts.values())
        for key, count in counts.items():
            scores[key] += count * (len(key[0]) - 1) / total
    return [{
        "kind": "fused",
        "originalId": fusion_training_id(ids),
        "width": len(ids),
        "operands": [asdict(shape) for shape in shapes],
        "count": visits[ids, shapes],
    } for ids, shapes in sorted(scores, key=lambda key: (-scores[key], key))[:budget]]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("profiles", type=Path, nargs="+")
    parser.add_argument("--candidates", type=Path, required=True)
    parser.add_argument("--operands", type=Path, required=True)
    parser.add_argument("--top", type=int, default=64)
    parser.add_argument("--budget", type=int, default=256)
    args = parser.parse_args()
    if min(args.top, args.budget) <= 0:
        parser.error("--top and --budget must be positive")
    profiles = [p / "blocks.bin" if p.is_dir() else p for p in args.profiles]
    handlers = training_handlers(read_catalog(), {})
    candidates = rank_candidates(profiles, handlers, args.top)
    if not candidates:
        parser.error("no executable sequences in the captures")
    names = {str(i): f"op::{handlers[i].name}" for i in sorted({i for row in candidates for i in row})}
    entries = rank_shapes(profiles, handlers, candidates, args.budget)
    for path, output in (
        (args.candidates, dict(schemaVersion=3, operations=names, metadata={"extended_lookup": True},
                               candidates=[dict(pair=ids) for ids in candidates])),
        (args.operands, dict(version=3, entries=entries)),
    ):
        path.write_text(json.dumps(output, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
