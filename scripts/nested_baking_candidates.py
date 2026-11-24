"""Rank observed instruction variants and emit nested build plans."""

from collections import Counter, defaultdict
from dataclasses import asdict
import math

from collect_baked_fusions import select_variants
from collect_modrm_hotness import read_modrm_counts
from collect_operand_specializations import collect
from gen_instruction_catalog import read_catalog, supports_exact_operands
from nested_baking_workloads import write_json
from operand_specialization_data import (
    Operand, fusion_training_id, relative_control_operations, training_handlers,
)


PARTIAL_FIELDS = {
    "dynamic-disp": ("disp",),
    "dynamic-imm": ("imm",),
    "dynamic-both": ("disp", "imm"),
}


def rank(samples):
    hotness = Counter()
    breadth = defaultdict(set)
    visits = Counter()
    for group, counts in samples:
        total = sum(counts.values())
        for key, count in counts.items():
            fraction = count / total
            hotness[key] += fraction
            visits[key] += count
            if fraction >= 0.0001:
                breadth[key].add(group)
    return [
        (key, visits[key], sorted(breadth[key]))
        for key in sorted(hotness, key=lambda key: (
            -hotness[key] * math.sqrt(max(1, len(breadth[key]))), repr(key)))
    ]


def modrm_row(key, visits, groups):
    op_id, slots, modrm = key
    return dict(training_id=op_id, slots=slots, modrm=modrm, estimated_visits=visits,
                active_groups=groups)


def partial_pools(samples, catalog, handlers):
    from gen_instruction_catalog import partial_scalar_operand

    pools = {}
    for kind, fields in PARTIAL_FIELDS.items():
        per_case = []
        constant_values = defaultdict(lambda: {field: set() for field in fields})
        for group, counts in samples:
            projected = Counter()
            for (op_id, operand), count in counts.items():
                shape = partial_scalar_operand(
                    catalog, handlers[op_id].name, operand,
                    dynamic_disp="disp" in fields, dynamic_imm="imm" in fields)
                if shape is None:
                    continue
                slots, operands = shape
                key = op_id, slots, tuple(sorted(operands.items()))
                projected[key] += count
                for field, values in constant_values[key].items():
                    values.add(getattr(operand, field))
            per_case.append((group, projected))
        pools[kind] = [
            dict(kind="wrapper", originalId=op_id, width=1, slots=slots,
                 operands=[dict(operand)], count=visits, active_groups=groups)
            for (op_id, slots, operand), visits, groups in rank(per_case)
            if all(len(values) > 1 for values in constant_values[op_id, slots, operand].values())
        ]
    return pools


def build_pools(cases, directory, include_partial=False):
    catalog = read_catalog()
    handlers = training_handlers(catalog, {})
    relative_names = relative_control_operations(catalog)
    relative = {op_id for op_id, handler in handlers.items() if handler.name in relative_names}
    captures = [directory / case["id"] / "blocks.bin" for case in cases]
    modrm_samples, policy_samples, exact_samples = [], [], []
    for case, profile in zip(cases, captures):
        print(f"Ranking {case['id']}", flush=True)
        group = case["group"]
        modrm, policies = read_modrm_counts([profile], None, True)
        modrm_samples.append((group, modrm))
        policy_samples.append((group, policies))
        exact, _, _ = collect([profile], None, None)
        exact_samples.append((group, Counter({
            (row["originalId"], operand): row["count"] for row in exact
            if row["kind"] == "wrapper"
            and supports_exact_operands(catalog, handlers[row["originalId"]].name,
                                       operand := Operand(**row["operands"][0]))
        })))
    print("Replaying fusion candidates against all training profiles", flush=True)
    recipes, fusion, _ = select_variants(captures, handlers, relative, top=1024, budget=1024)
    sequences = {fusion_training_id(recipe): recipe for recipe in recipes}
    if len(sequences) != len(recipes):
        raise ValueError("fusion recipe IDs collided")
    pools = {
        "modrm": [modrm_row(key, visits, groups) for key, visits, groups in rank(modrm_samples)],
        "policies": [{**modrm_row(key, visits, groups), "width": width, "flags": flags}
                     for (key, width, flags), visits, groups in rank(policy_samples)],
        "exact": [dict(kind="wrapper", originalId=op_id, width=1, operands=[asdict(operand)],
                       count=visits, active_groups=groups)
                  for (op_id, operand), visits, groups in rank(exact_samples)],
        "fusion": [{**row, "sequence": sequences[row["originalId"]]} for row in fusion],
    }
    if include_partial:
        print("Grouping observed dynamic displacement and immediate shapes", flush=True)
        pools.update(partial_pools(exact_samples, catalog, handlers))
    return pools


def modrm_key(row):
    return row["training_id"], row["slots"], row["modrm"]


def write_plans(directory, counts, pools):
    m, p, e, f, *partial_counts = counts
    # A width/flags binding requires its corresponding ModRM binding. Include
    # that dependency even if it falls outside the ModRM prefix.
    required = {modrm_key(row) for row in pools["policies"][:p]}
    modrm = [row for i, row in enumerate(pools["modrm"]) if i < m or modrm_key(row) in required]
    scalar = pools["exact"][:e]
    for kind, count in zip(PARTIAL_FIELDS, partial_counts):
        scalar.extend(pools[kind][:count])
    fusion = pools["fusion"][:f]
    write_json(directory / "modrm.json", {
        "schema_version": 1, "entries": modrm, "scalar_policies": pools["policies"][:p]})
    write_json(directory / "scalar.json", {"version": 4 if any(partial_counts) else 3, "entries": scalar})
    write_json(directory / "fusion-operands.json", {"version": 3, "entries": fusion})
    recipes = dict.fromkeys(tuple(row["sequence"]) for row in fusion)
    names = {}
    if recipes:
        handlers = training_handlers(read_catalog(), {})
        names = {str(op_id): f"op::{handlers[op_id].name}"
                 for op_id in sorted({op_id for recipe in recipes for op_id in recipe})}
        assert all(fusion_training_id(row["sequence"]) == row["originalId"] for row in fusion)
    write_json(directory / "recipes.json", {
        "schemaVersion": 3, "operations": names, "metadata": {"extended_lookup": True},
        "candidates": [dict(pair=recipe) for recipe in recipes],
    })
    return len(modrm), p, e, len(fusion), *partial_counts
