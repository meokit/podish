#!/usr/bin/env python3
"""Bake SuperOpcode recipes and exact operands from unfused BLK2 captures."""

import argparse
from collections import Counter, defaultdict
from dataclasses import asdict
import json
from pathlib import Path

from collect_operand_specializations import blocks
from gen_instruction_catalog import read_catalog
from operand_specialization_data import fusion_training_id, training_handlers

DEFAULT_TOP = 384
DEFAULT_BUDGET = 768


def sequences(ops, handlers):
    for start, (op_id, _) in enumerate(ops):
        if op_id not in handlers:
            continue
        ids, shapes = [], []
        for candidate, shape in ops[start:start + 4]:
            if candidate not in handlers or shape is None:
                break
            ids.append(candidate)
            shapes.append(shape)
            if len(ids) >= 2:
                yield start, (tuple(ids), tuple(shapes))
            # A branch (including one created by block concatenation) ends
            # this sequence. Replay and target selection stay with its last op.
            if shape.meta & 8:
                break


def unfused_blocks(profile, handlers):
    for executions, ops in blocks(profile):
        for op_id, _ in ops:
            if op_id and op_id not in handlers:
                raise ValueError(f"{profile}: unknown or fused training ID 0x{op_id:08x}; "
                                 "recapture with static specialization and fusion disabled")
        yield executions, ops


def record_scores(profiles, handlers):
    scores, visits, totals = Counter(), Counter(), {}
    for profile in profiles:
        counts = Counter()
        total = 0
        for executions, ops in unfused_blocks(profile, handlers):
            total += executions * len(ops)
            for _, key in sequences(ops, handlers):
                counts[key] += executions
        if not total:
            raise ValueError(f"{profile}: no weighted instruction visits")
        visits.update(counts)
        totals[profile] = total
        for key, count in counts.items():
            scores[key] += count * (len(key[0]) - 1) / total
    return scores, visits, totals


def replay_layouts(profile, handlers, candidates):
    """Compress blocks with the same eligible exact windows for replay."""
    layouts = Counter()
    for executions, ops in blocks(profile):
        matching = tuple((start, index) for start, key in sequences(ops, handlers)
                         if (index := candidates.get(key)) is not None)
        if matching:
            layouts[matching] += executions
    return layouts


def saved_dispatches(layout, widths, enabled):
    skip_until = saved = 0
    # Sequences arrive in ascending width for each position. Try the longest
    # available match first, as GeneratedFindSuperOpcode does at decode time.
    matches = {}
    for start, candidate in layout:
        if enabled[candidate]:
            matches[start] = widths[candidate]
    for start, width in matches.items():
        if start >= skip_until:
            skip_until = start + width
            saved += width - 1
    return saved


def select_variants(profiles, handlers, top, budget):
    scores, visits, totals = record_scores(profiles, handlers)
    # Rank by potential savings before replay. Keep a wider shortlist so
    # overlap can reject redundant shapes.
    ranked = sorted(scores, key=lambda key: (-scores[key], key))[:max(top, 4 * budget)]
    indexes = {key: index for index, key in enumerate(ranked)}
    widths = [len(ids) for ids, _ in ranked]
    profile_layouts = {profile: replay_layouts(profile, handlers, indexes) for profile in profiles}
    weights = Counter()
    for profile, layouts in profile_layouts.items():
        for layout, count in layouts.items():
            weights[layout] += count / totals[profile]
    layouts = list(weights)
    affected = defaultdict(list)
    for i, layout in enumerate(layouts):
        for candidate in {candidate for _, candidate in layout}:
            affected[candidate].append(i)

    enabled = [False] * len(ranked)
    savings = [0] * len(layouts)
    recipes, variants = {}, []
    for index, (ids, shapes) in enumerate(ranked):
        if len(recipes) == top and ids not in recipes:
            continue
        enabled[index] = True
        updates = [(i, saved_dispatches(layouts[i], widths, enabled)) for i in affected[index]]
        gain = sum((after - savings[i]) * weights[layouts[i]] for i, after in updates)
        if gain <= 0:
            enabled[index] = False
            continue
        for i, after in updates:
            savings[i] = after
        recipes[ids] = None
        variants.append({
            "kind": "fused",
            "originalId": fusion_training_id(ids),
            "width": len(ids),
            "operands": [asdict(shape) for shape in shapes],
            "count": visits[ids, shapes],
        })
        if len(variants) == budget:
            break

    coverage = []
    for profile, layouts in profile_layouts.items():
        saved = sum(count * saved_dispatches(layout, widths, enabled) for layout, count in layouts.items())
        coverage.append({"profile": profile.parent.name, "instruction_visits": totals[profile],
                         "saved_dispatches": saved, "fraction": saved / totals[profile]})
    return list(recipes), variants, coverage


def bake(profiles, candidates, operands, top=DEFAULT_TOP, budget=DEFAULT_BUDGET):
    if min(top, budget) <= 0:
        raise ValueError("top and budget must be positive")
    handlers = training_handlers(read_catalog(), {})
    recipes, entries, coverage = select_variants(sorted(set(profiles)), handlers, top, budget)
    if not entries:
        raise ValueError("no executable fusion variants in the captures")
    names = {str(i): f"op::{handlers[i].name}" for i in sorted({i for row in recipes for i in row})}
    for path, output in (
        (candidates, dict(schemaVersion=3, operations=names, metadata={"extended_lookup": True},
                          candidates=[dict(pair=ids) for ids in recipes])),
        (operands, dict(version=3, entries=entries, coverage=coverage)),
    ):
        path.write_text(json.dumps(output, indent=2) + "\n", encoding="utf-8")
    print(f"Baked {len(entries)} exact variants across {len(recipes)} recipes")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("profiles", type=Path, nargs="+")
    parser.add_argument("--candidates", type=Path, required=True)
    parser.add_argument("--operands", type=Path, required=True)
    parser.add_argument("--top", type=int, default=DEFAULT_TOP)
    parser.add_argument("--budget", type=int, default=DEFAULT_BUDGET)
    args = parser.parse_args()
    profiles = [p / "blocks.bin" if p.is_dir() else p for p in args.profiles]
    try:
        bake(profiles, args.candidates, args.operands, args.top, args.budget)
    except (ValueError, OSError) as error:
        parser.error(str(error))


if __name__ == "__main__":
    main()
