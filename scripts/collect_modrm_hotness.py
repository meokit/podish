#!/usr/bin/env python3
"""Rank scalar ModRM bindings by execution weight in BLK2 v3 profiles."""

import argparse
from collections import Counter
from decimal import Decimal, InvalidOperation
import json
from pathlib import Path
import re
import struct

from collect_operand_specializations import SRC, blocks
from gen_instruction_catalog import read_catalog
from operand_specialization_data import HAS_MEMORY, HAS_MODRM, generated_fusions, training_handlers
from scalar_modrm import scalar_modrm_plans


DEFAULT_COVERAGE = Decimal("0.999")
DEFAULT_FUSIONS = SRC / "generated/superopcodes.extended.generated.cpp"


def compact_immediates():
    source = (SRC / "decoder.h").read_text()
    mask = re.search(r"constexpr uint8_t kCodeMask = (\w+);", source)
    last = re.search(r"constexpr uint8_t kLastLiteral = (\w+);", source)
    constants = re.search(r"kConstants\{([^}]+)\};", source)
    if not (mask and last and constants):
        raise ValueError("could not read compact immediate encoding from decoder.h")
    mask, last = int(mask[1], 0), int(last[1], 0)
    table = [int(value, 0) for value in constants[1].split(",") if value.strip()]
    if len(table) != mask - last:
        raise ValueError("invalid compact immediate table in decoder.h")
    return mask, set(range(last + 1)) | set(table)


def read_modrm_counts(profiles, fusion_source):
    catalog = read_catalog()
    fusions = generated_fusions(fusion_source.read_text()) if fusion_source else {}
    handlers = training_handlers(catalog, fusions)
    plans = scalar_modrm_plans(catalog)
    scalar_ids = {handler.name: op_id for op_id, handler in handlers.items() if handler.kind == "wrapper"}
    prefix_mask, immediates = compact_immediates()
    hits = Counter()

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
                    slots = 1
                    if op.meta & HAS_MEMORY and op.imm and (op.prefixes & prefix_mask or op.imm not in immediates):
                        slots = 2
                    if (slots << 8 | modrm) not in binding.modrm_keys:
                        raise ValueError(f"{profile}: missing scalar binding {name}, ModRM 0x{modrm:02x}, {slots} slots")
                    hits[scalar_ids[name], slots, modrm] += executions
                cursor = end

    if not hits:
        raise ValueError("no weighted ModRM observations; recapture with FIBERCPU_ENABLE_BLOCK_PROFILE=ON")
    return hits


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


def collect_modrm_hotness(profiles, fusion_source, coverage):
    hits = read_modrm_counts(profiles, fusion_source)
    return hotness_report(hits, hot_keys(hits, coverage))


def collect_workload_union(profiles, fusion_source, coverage):
    counts = {name: read_modrm_counts([path], fusion_source) for name, path in profiles.items()}
    keys = set().union(*(hot_keys(hits, coverage) for hits in counts.values()))
    total = Counter()
    for hits in counts.values():
        total.update(hits)
    report = hotness_report(total, keys)
    report["workloads"] = {name: dict(observed_keys=len(hits),
                                      coverage=sum(hits[key] for key in keys) / sum(hits.values()))
                           for name, hits in counts.items()}
    return report


def write_hotness(output, report):
    metadata = {key: value for key, value in report.items() if key != "entries"}
    prefix = json.dumps(metadata, indent=2).removesuffix("\n}")
    entries = ",\n".join("    " + json.dumps(row) for row in report["entries"])
    output.write_text(f'{prefix},\n  "entries": [\n{entries}\n  ]\n}}\n', encoding="utf-8")
    print(f"Retained {len(report['entries'])}/{report['observed_keys']} ModRM bindings "
          f"covering {report['coverage']:.4%} of weighted visits", flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("profiles", nargs="+", type=Path, help="BLK2 v3 files or guest stats directories")
    parser.add_argument("--fusions", type=Path, default=DEFAULT_FUSIONS, help="Fusion source used for the capture")
    parser.add_argument("--coverage", default=str(DEFAULT_COVERAGE), help="Weighted visit fraction to retain")
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--per-profile", action="store_true", help="Take the union of the hot keys of each profile")
    args = parser.parse_args()
    profiles = [path / "blocks.bin" if path.is_dir() else path for path in args.profiles]
    try:
        coverage = Decimal(args.coverage)
        if args.per_profile:
            named = {path.parent.name: path for path in profiles}
            if len(named) != len(profiles):
                raise ValueError("per-profile captures need distinct directory names")
            report = collect_workload_union(named, args.fusions, coverage)
        else:
            report = collect_modrm_hotness(profiles, args.fusions, coverage)
        args.output.parent.mkdir(parents=True, exist_ok=True)
        write_hotness(args.output, report)
    except InvalidOperation:
        parser.error("coverage must be a number in (0, 1]")
    except (OSError, ValueError, struct.error) as error:
        parser.error(str(error))


if __name__ == "__main__":
    main()
