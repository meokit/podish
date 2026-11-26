#!/usr/bin/env python3
"""Rank scalar ModRM bindings by execution weight in BLK2 v3 profiles."""

import argparse
from collections import Counter
from decimal import Decimal, InvalidOperation
import json
from pathlib import Path
import struct

from collect_operand_specializations import SRC, blocks
from gen_instruction_catalog import read_catalog
from operand_specialization_data import (
    HAS_MODRM, NO_FLAGS, OPSIZE_PREFIX, data_operand_slots, generated_fusions, training_handlers,
)
from scalar_modrm import scalar_modrm_plans


DEFAULT_COVERAGE = Decimal("0.999")
DEFAULT_FUSIONS = SRC.parent / "presets/3m/superopcodes.generated.cpp"


def read_modrm_counts(profiles, fusion_source, collect_policies):
    catalog = read_catalog()
    fusions = generated_fusions(fusion_source.read_text()) if fusion_source else {}
    handlers = training_handlers(catalog, fusions)
    plans = scalar_modrm_plans(catalog)
    scalar_ids = {handler.name: op_id for op_id, handler in handlers.items() if handler.kind == "wrapper"}
    hits = Counter()
    policy_hits = Counter()
    policies = catalog.get("scalar_policies", {}) if collect_policies else {}

    for profile in profiles:
        for executions, ops in blocks(profile):
            cursor = 0
            while cursor < len(ops):
                op_id, operand = ops[cursor]
                handler = handlers.get(op_id)
                if handler is None:
                    if op_id or (operand and operand.meta & HAS_MODRM):
                        raise ValueError(f"{profile}: unknown training ID 0x{op_id:08x}; check the catalog and --fusions")
                    cursor += 1
                    continue
                end = cursor + len(handler.operations)
                if end > len(ops):
                    raise ValueError(f"{profile}: truncated fusion 0x{op_id:08x}")
                for name, (_, op) in zip(handler.operations, ops[cursor:end]):
                    if op is None or not op.meta & HAS_MODRM:
                        continue
                    binding = plans[name]
                    if not binding.modrm_keys:
                        continue  # Invalid-opcode trap handlers have no trainable ModRM shape.
                    mem_mask = binding.memory_mask
                    modrm = op.modrm & mem_mask if op.modrm < 0xc0 or not mem_mask else op.modrm
                    slots = data_operand_slots(op)
                    if (slots << 8 | modrm) not in binding.modrm_keys:
                        raise ValueError(f"{profile}: missing scalar binding {name}, ModRM 0x{modrm:02x}, {slots} slots")
                    key = scalar_ids[name], slots, modrm
                    hits[key] += executions
                    if name in policies:
                        arguments = policies[name]["arguments"]
                        width = (16 if op.prefixes & OPSIZE_PREFIX else 32) if "$width" in arguments else None
                        flags = ("dead" if op.meta & NO_FLAGS else "live") if "$flags" in arguments else None
                        policy_hits[key, width, flags] += executions
                cursor = end

    if not hits:
        raise ValueError("no weighted ModRM observations; recapture with FIBERCPU_ENABLE_BLOCK_PROFILE=ON")
    return hits, policy_hits


def hot_keys(hits, coverage):
    if not coverage.is_finite() or not 0 < coverage <= 1:
        raise ValueError("coverage must be in (0, 1]")
    keys = set()
    selected = 0
    numerator, denominator = coverage.as_integer_ratio()
    minimum = (sum(hits.values()) * numerator + denominator - 1) // denominator
    for key, count in sorted(hits.items(), key=lambda item: (-item[1], item[0])):
        keys.add(key)
        selected += count
        if selected >= minimum:
            break
    return keys


def hotness_report(hits, keys):
    total = sum(hits.values())
    selected = sum(hits[key] for key in keys)
    entries = [dict(training_id=op_id, slots=slots, modrm=modrm, estimated_visits=hits[op_id, slots, modrm])
               for op_id, slots, modrm in sorted(keys, key=lambda key: (-hits[key], key))]
    return dict(schema_version=1, weights="block_entry_count", observed_keys=len(hits),
                weighted_count=total, coverage=selected / total, entries=entries)


def add_scalar_policies(report, policy_hits, keys):
    def order(item):
        (key, width, flags), count = item
        return -count, key, width or 0, flags or ""

    report["scalar_policies"] = [
        dict(training_id=op_id, slots=slots, modrm=modrm, width=width, flags=flags, estimated_visits=visits)
        for ((op_id, slots, modrm), width, flags), visits in sorted(policy_hits.items(), key=order)
        if (op_id, slots, modrm) in keys and visits > 0
    ]


def collect_modrm_hotness(profiles, fusion_source, coverage, scalar_policies=False):
    hits, policy_hits = read_modrm_counts(profiles, fusion_source, scalar_policies)
    keys = hot_keys(hits, coverage)
    report = hotness_report(hits, keys)
    if scalar_policies:
        add_scalar_policies(report, policy_hits, keys)
    return report


def collect_workload_union(profiles, fusion_source, coverage, scalar_policies=False):
    counts = {name: read_modrm_counts([path], fusion_source, scalar_policies)
              for name, path in profiles.items()}
    keys = set().union(*(hot_keys(hits, coverage) for hits, _ in counts.values()))
    total = Counter()
    total_policies = Counter()
    for hits, policy_hits in counts.values():
        total.update(hits)
        total_policies.update(policy_hits)
    report = hotness_report(total, keys)
    report["workloads"] = {name: dict(observed_keys=len(hits),
                                      coverage=sum(hits[key] for key in keys) / sum(hits.values()))
                           for name, (hits, _) in counts.items()}
    if scalar_policies:
        add_scalar_policies(report, total_policies, keys)
    return report


def write_hotness(output, report):
    tables = ("entries", "scalar_policies")
    metadata = {key: value for key, value in report.items() if key not in tables}
    prefix = json.dumps(metadata, indent=2).removesuffix("\n}")
    chunks = [prefix]
    for table in tables:
        if table in report:
            entries = ",\n".join("    " + json.dumps(row) for row in report[table])
            chunks.append(f',\n  "{table}": [\n{entries}\n  ]')
    output.write_text("".join(chunks) + "\n}\n", encoding="utf-8")
    print(f"Retained {len(report['entries'])}/{report['observed_keys']} ModRM bindings "
          f"covering {report['coverage']:.4%} of weighted visits", flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("profiles", nargs="+", type=Path, help="BLK2 v3 files or guest stats directories")
    parser.add_argument("--fusions", type=Path, default=DEFAULT_FUSIONS, help="Fusion source used for the capture")
    parser.add_argument("--coverage", default=str(DEFAULT_COVERAGE), help="Weighted visit fraction to retain")
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--per-profile", action="store_true", help="Take the union of the hot keys of each profile")
    parser.add_argument("--scalar-policies", action="store_true",
                        help="Also bake observed operand widths and dead/live flags for supported scalar kernels")
    args = parser.parse_args()
    profiles = [path / "blocks.bin" if path.is_dir() else path for path in args.profiles]
    try:
        coverage = Decimal(args.coverage)
        if args.per_profile:
            named = {path.parent.name: path for path in profiles}
            if len(named) != len(profiles):
                raise ValueError("per-profile captures need distinct directory names")
            report = collect_workload_union(named, args.fusions, coverage, args.scalar_policies)
        else:
            report = collect_modrm_hotness(profiles, args.fusions, coverage, args.scalar_policies)
        args.output.parent.mkdir(parents=True, exist_ok=True)
        write_hotness(args.output, report)
    except InvalidOperation:
        parser.error("coverage must be a number in (0, 1]")
    except (OSError, ValueError, struct.error) as error:
        parser.error(str(error))


if __name__ == "__main__":
    main()
