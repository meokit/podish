#!/usr/bin/env python3

from __future__ import annotations

import argparse
import json
import re
import sys
from collections import Counter
from pathlib import Path

GPR_NAMES = ("eax", "ecx", "edx", "ebx", "esp", "ebp", "esi", "edi")
REPO_ROOT = Path(__file__).resolve().parents[2]
SCRIPTS_DIR = REPO_ROOT / "scripts"
if str(SCRIPTS_DIR) not in sys.path:
    sys.path.insert(0, str(SCRIPTS_DIR))

from op_def_use_lut import analyze_def_use


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description=(
            "Aggregate SuperOpcode candidates across block analysis samples using "
            "global 2-gram scoring from hot anchors and dependency weights."
        )
    )
    parser.add_argument(
        "inputs",
        nargs="+",
        help="One or more blocks_analysis.json files, guest-stats directories, or results directories",
    )
    parser.add_argument(
        "--n-gram",
        type=int,
        default=2,
        help="Compatibility flag. Only 2-op SuperOpcodes are currently supported (default: 2).",
    )
    parser.add_argument(
        "--top",
        type=int,
        default=100,
        help="Maximum number of aggregate candidates to emit (default: 100)",
    )
    parser.add_argument(
        "--score-basis",
        choices=("anchor", "pair"),
        default="pair",
        help="Base frequency used in score computation (default: pair)",
    )
    parser.add_argument(
        "--raw-weight",
        type=int,
        default=2,
        help="Dependency weight for RAW pairs (default: 2)",
    )
    parser.add_argument(
        "--rar-weight",
        type=int,
        default=0,
        help="Dependency weight for RAR pairs (default: 0)",
    )
    parser.add_argument(
        "--waw-weight",
        type=int,
        default=0,
        help="Dependency weight for WAW pairs (default: 0)",
    )
    parser.add_argument(
        "--jcc-multiplier",
        type=int,
        default=1,
        help="Extra multiplier for Jcc-related pairs (default: 1)",
    )
    parser.add_argument(
        "--jcc-mode",
        choices=("none", "pair", "raw-only"),
        default="none",
        help="How to apply the Jcc multiplier (default: none)",
    )
    parser.add_argument(
        "--anchor-top",
        type=int,
        default=64,
        help="Show this many hottest single ops in the markdown summary (default: 64)",
    )
    parser.add_argument(
        "--min-samples",
        type=int,
        default=1,
        help="Minimum number of distinct samples a candidate must appear in (default: 1)",
    )
    parser.add_argument(
        "--min-weighted-exec-count",
        type=int,
        default=0,
        help="Minimum total weighted_exec_count required to keep a candidate (default: 0)",
    )
    parser.add_argument(
        "--output-json",
        required=True,
        help="Path to write aggregate candidate JSON",
    )
    parser.add_argument(
        "--output-md",
        default=None,
        help="Optional path to write a markdown summary alongside the JSON output",
    )
    return parser.parse_args()


def discover_analysis_files(inputs: list[str]) -> list[Path]:
    files: list[Path] = []
    seen: set[Path] = set()

    for raw in inputs:
        path = Path(raw).resolve()
        candidates: list[Path] = []
        if path.is_file():
            candidates = [path]
        elif path.is_dir():
            direct = path / "blocks_analysis.json"
            if direct.is_file():
                candidates = [direct]
            else:
                candidates = sorted(path.rglob("blocks_analysis.json"))

        for candidate in candidates:
            if candidate not in seen:
                files.append(candidate)
                seen.add(candidate)

    return sorted(files)


def infer_sample_metadata(path: Path) -> dict[str, object]:
    parts = list(path.parts)
    metadata: dict[str, object] = {
        "analysis_file": str(path),
        "sample_name": path.parent.name,
        "result_name": path.parent.parent.name if len(parts) >= 2 else "",
        "engine": None,
        "case": None,
        "iteration": None,
    }

    if "guest-stats" in parts:
        idx = parts.index("guest-stats")
        if idx + 1 < len(parts):
            sample_name = parts[idx + 1]
            metadata["sample_name"] = sample_name
            sample_parts = sample_name.split("-")
            if len(sample_parts) >= 3:
                metadata["engine"] = sample_parts[0]
                metadata["case"] = sample_parts[1]
                iteration_text = sample_parts[2]
                try:
                    metadata["iteration"] = int(iteration_text)
                except ValueError:
                    metadata["iteration"] = iteration_text
        if idx >= 1:
            metadata["result_name"] = parts[idx - 1]

    return metadata


def should_skip_analysis(data: dict[str, object]) -> tuple[bool, list[str]]:
    validation = data.get("validation") or {}
    warnings = list(validation.get("warnings") or [])
    blocks = data.get("blocks") or []
    reasons: list[str] = []

    if not blocks:
        reasons.append("blocks list is empty")

    for warning in warnings:
        if (
            "parsed blocks are empty" in warning
            or "dump/export format likely drifted" in warning
            or "truncated" in warning
        ):
            reasons.append(warning)

    symbol_count = 0
    unknown_symbol_count = 0
    for block in blocks[:200]:
        for op in (block.get("ops") or [])[:32]:
            symbol = str(op.get("symbol", ""))
            if not symbol:
                continue
            symbol_count += 1
            if symbol == "<unknown>" or symbol.startswith("func_"):
                unknown_symbol_count += 1

    if symbol_count > 0 and (unknown_symbol_count / symbol_count) >= 0.5:
        reasons.append(
            f"symbol resolution looks invalid ({unknown_symbol_count}/{symbol_count} sampled ops unresolved)"
        )

    return bool(reasons), reasons


def normalize_logic_name(symbol: object) -> str | None:
    if not symbol:
        return None
    text = str(symbol)
    if "SuperOpcode_" in text:
        return None
    if text.startswith("op::Op"):
        return text
    if text.startswith("Op"):
        return f"op::{text}"
    direct_match = re.search(r"(?:^|::)(Op[A-Za-z0-9_]+)(?:\(|$)", text)
    if direct_match:
        return f"op::{direct_match.group(1)}"
    mangled_match = re.search(r"(Op[A-Za-z0-9_]+?)EPNS_", text)
    if mangled_match:
        return f"op::{mangled_match.group(1)}"
    return None


def op_short_name(name: str) -> str:
    short = name.split("::")[-1]
    if short.startswith("Op"):
        return short[2:]
    return short


def infer_semantics(name: str) -> dict[str, object]:
    short = op_short_name(name)
    lower = short.lower()
    reads: set[str] = set()
    writes: set[str] = set()
    notes: list[str] = []
    control_flow = False
    memory_side_effect = False

    if lower.startswith(("jcc", "jmp", "call", "ret", "loop", "iret", "sys")):
        control_flow = True

    if lower.startswith("jcc"):
        reads.add("flags")
        notes.append("conditional branch consumes flags")
    elif lower.startswith("cmov"):
        reads.update({"flags", "gpr"})
        writes.add("gpr")
        notes.append("cmov consumes flags and forwards a register value")
    elif lower.startswith("setcc"):
        reads.add("flags")
        writes.add("gpr")
        notes.append("setcc consumes flags and defines a byte register")

    if lower.startswith(("cmp", "test", "bt", "btc", "btr", "bts", "comis", "ucomis")):
        reads.add("gpr")
        writes.add("flags")
        notes.append("compare/test family defines flags")
    elif lower.startswith(("adc", "sbb")):
        reads.update({"gpr", "flags"})
        writes.update({"gpr", "flags"})
        notes.append("adc/sbb both consume and define flags")
    elif lower.startswith(
        ("add", "sub", "and", "or", "xor", "inc", "dec", "neg", "shl", "shr", "sar", "sal", "rol", "ror", "shld", "shrd")
    ):
        reads.add("gpr")
        writes.update({"gpr", "flags"})
        notes.append("ALU op updates register and flags")

    is_load = "load" in lower
    is_store = "store" in lower

    if is_load:
        reads.add("mem")
        writes.add("gpr")
        notes.append("load defines a register from memory")
    if is_store:
        reads.add("gpr")
        writes.add("mem")
        memory_side_effect = True
        notes.append("store consumes a register and writes memory")

    if not is_store and lower.startswith(("mov", "lea", "movzx", "movsx", "pop")):
        writes.add("gpr")
    if not is_load and lower.startswith(("mov", "push", "xchg", "cmp", "test")):
        reads.add("gpr")
    if lower.startswith("push"):
        writes.add("mem")
        memory_side_effect = True
    if lower.startswith("xchg"):
        writes.add("gpr")
    if lower.startswith("movs"):
        reads.update({"gpr", "mem"})
        writes.add("mem")
        memory_side_effect = True
    if lower.startswith(("cmps", "scas")):
        reads.update({"gpr", "mem"})
        writes.add("flags")
        notes.append("string compare defines flags")

    return {
        "reads": sorted(reads),
        "writes": sorted(writes),
        "notes": notes,
        "control_flow": control_flow,
        "memory_side_effect": memory_side_effect,
    }


def get_op_semantics(op: dict[str, object], normalized_name: str) -> dict[str, object]:
    def_use = analyze_def_use(
        op_id=op.get("op_id"),
        modrm=int(op.get("modrm", 0) or 0),
        meta=int(op.get("meta", 0) or 0),
        prefixes=int(op.get("prefixes", 0) or 0),
        ea_desc=int(((op.get("mem") or {}).get("ea_desc", 0)) or 0),
    )
    if not isinstance(def_use, dict):
        def_use = op.get("def_use")
    if not isinstance(def_use, dict):
        return infer_semantics(normalized_name)

    reads_data = [str(name) for name in def_use.get("reads_data_gpr") or def_use.get("reads_gpr") or []]
    writes_data = [str(name) for name in def_use.get("writes_gpr") or []]
    reads: set[str] = set(reads_data)
    writes: set[str] = set(writes_data)
    addr_reads = [str(name) for name in def_use.get("reads_addr_gpr") or []]

    reads_flags = [str(name) for name in def_use.get("reads_flags") or []]
    writes_flags = [str(name) for name in def_use.get("writes_flags") or []]
    reads.update(f"flag:{name}" for name in reads_flags)
    writes.update(f"flag:{name}" for name in writes_flags)

    if bool(def_use.get("reads_memory")):
        reads.add("mem")
    if bool(def_use.get("writes_memory")):
        writes.add("mem")

    fallback = infer_semantics(normalized_name)
    control_flow = bool(fallback["control_flow"])
    memory_side_effect = bool(def_use.get("writes_memory")) or bool(fallback["memory_side_effect"])
    notes = [str(note) for note in def_use.get("notes") or []]
    notes.extend(str(note) for note in fallback["notes"] if str(note) not in notes)
    if addr_reads:
        notes.append(f"address regs: {', '.join(addr_reads)}")

    return {
        "reads": sorted(reads),
        "writes": sorted(writes),
        "notes": notes,
        "control_flow": control_flow,
        "memory_side_effect": memory_side_effect,
    }


def classify_pair(
    first_name: str,
    second_name: str,
    first_op: dict[str, object],
    second_op: dict[str, object],
) -> dict[str, object] | None:
    first_info = get_op_semantics(first_op, first_name)
    second_info = get_op_semantics(second_op, second_name)
    shared_raw = sorted(set(first_info["writes"]) & set(second_info["reads"]))
    shared_rar = sorted(set(first_info["reads"]) & set(second_info["reads"]))
    shared_waw = sorted(set(first_info["writes"]) & set(second_info["writes"]))
    if not shared_raw and not shared_rar and not shared_waw:
        return None

    shared_resources: list[str]
    relation_kind: str
    dep_weight = 1

    if shared_raw:
        relation_kind = "RAW"
        shared_resources = shared_raw
        dep_weight = 2
    elif shared_rar and shared_waw:
        relation_kind = "RAR/WAW"
        shared_resources = sorted(set(shared_rar) | set(shared_waw))
    elif shared_rar:
        relation_kind = "RAR"
        shared_resources = shared_rar
    else:
        relation_kind = "WAW"
        shared_resources = shared_waw

    legality_notes: list[str] = []
    if bool(second_info["control_flow"]):
        legality_notes.append("second op is control-flow and needs strict mid-exit handling")
    if bool(first_info["memory_side_effect"]) or bool(second_info["memory_side_effect"]):
        legality_notes.append("pair touches memory side effects and should keep restart semantics conservative")
    if relation_kind != "RAW":
        legality_notes.append("pair is non-RAW and may only be profitable when repeated reads or write coalescing can be shared")

    return {
        "relation_kind": relation_kind,
        "relation_priority": dep_weight,
        "shared_resources": shared_resources,
        "first_semantics": first_info,
        "second_semantics": second_info,
        "legality_notes": legality_notes,
    }


def update_anchor_entry(
    stats: dict[str, dict[str, object]],
    anchor: str,
    block_start: str,
    block_exec_count: int,
    op_index: int,
) -> None:
    entry = stats.setdefault(
        anchor,
        {
            "anchor": anchor,
            "anchor_display": op_short_name(anchor),
            "weighted_exec_count": 0,
            "occurrences": 0,
            "unique_block_starts": set(),
            "example_blocks": [],
            "semantics": infer_semantics(anchor),
        },
    )
    entry["weighted_exec_count"] += block_exec_count
    entry["occurrences"] += 1
    entry["unique_block_starts"].add(block_start)
    if len(entry["example_blocks"]) < 5:
        entry["example_blocks"].append(
            {
                "start_eip_hex": block_start,
                "exec_count": block_exec_count,
                "anchor_op_index": op_index,
            }
        )


def update_pair_entry(
    stats: dict[tuple[str, str], dict[str, object]],
    pair: tuple[str, str],
    anchor: str,
    direction: str,
    relation: dict[str, object],
    block_start: str,
    block_exec_count: int,
    start_op_index: int,
) -> None:
    entry = stats.setdefault(
        pair,
        {
            "pair": list(pair),
            "pair_display": " -> ".join(pair),
            "ngram": list(pair),
            "ngram_display": " -> ".join(pair),
            "first_handler": pair[0],
            "second_handler": pair[1],
            "anchor_handler": anchor,
            "anchor_display": op_short_name(anchor),
            "direction": direction,
            "relation_kind": relation["relation_kind"],
            "relation_priority": relation["relation_priority"],
            "shared_resources": relation["shared_resources"],
            "shared_resource_variants": Counter(),
            "relation_kind_variants": Counter(),
            "legality_notes": list(relation["legality_notes"]),
            "weighted_exec_count": 0,
            "occurrences": 0,
            "unique_block_starts": set(),
            "example_blocks": [],
        },
    )
    entry["weighted_exec_count"] += block_exec_count
    entry["occurrences"] += 1
    entry["unique_block_starts"].add(block_start)
    entry["shared_resource_variants"][tuple(relation["shared_resources"])] += 1
    entry["relation_kind_variants"][str(relation["relation_kind"])] += 1
    if len(entry["example_blocks"]) < 3:
        entry["example_blocks"].append(
            {
                "start_eip_hex": block_start,
                "exec_count": block_exec_count,
                "start_op_index": start_op_index,
                "anchor_handler": anchor,
                "direction": direction,
            }
        )


def analyze_sample_candidates(
    blocks: list[dict[str, object]],
) -> tuple[dict[str, dict[str, object]], dict[tuple[str, str], dict[str, object]]]:
    anchor_stats: dict[str, dict[str, object]] = {}
    pair_stats: dict[tuple[str, str], dict[str, object]] = {}

    for block in blocks:
        ops = block.get("ops") or []
        symbols = [normalize_logic_name(op.get("logic_func") or op.get("symbol")) for op in ops]
        block_start = str(block.get("start_eip_hex") or hex(int(block.get("start_eip", 0))))
        block_exec_count = int(block.get("exec_count", 0))

        for index, anchor in enumerate(symbols):
            if not anchor or not anchor.startswith("op::Op"):
                continue

            update_anchor_entry(anchor_stats, anchor, block_start, block_exec_count, index)

            if index > 0 and symbols[index - 1]:
                first = str(symbols[index - 1])
                relation = classify_pair(first, anchor, ops[index - 1], ops[index])
                if relation is not None:
                    update_pair_entry(
                        pair_stats,
                        (first, anchor),
                        anchor=anchor,
                        direction="predecessor",
                        relation=relation,
                        block_start=block_start,
                        block_exec_count=block_exec_count,
                        start_op_index=index - 1,
                    )

            if index + 1 < len(symbols) and symbols[index + 1]:
                second = str(symbols[index + 1])
                relation = classify_pair(anchor, second, ops[index], ops[index + 1])
                if relation is not None:
                    update_pair_entry(
                        pair_stats,
                        (anchor, second),
                        anchor=anchor,
                        direction="successor",
                        relation=relation,
                        block_start=block_start,
                        block_exec_count=block_exec_count,
                        start_op_index=index,
                    )

    return anchor_stats, pair_stats


def merge_anchor_stats(
    aggregate: dict[str, dict[str, object]],
    anchor: str,
    sample_meta: dict[str, object],
    sample_stats: dict[str, object],
) -> None:
    entry = aggregate.setdefault(
        anchor,
        {
            "anchor": anchor,
            "anchor_display": op_short_name(anchor),
            "weighted_exec_count": 0,
            "occurrences": 0,
            "unique_block_count": 0,
            "sample_count": 0,
            "engine_counts": Counter(),
            "case_counts": Counter(),
            "example_sources": [],
            "semantics": sample_stats["semantics"],
        },
    )
    entry["weighted_exec_count"] += int(sample_stats["weighted_exec_count"])
    entry["occurrences"] += int(sample_stats["occurrences"])
    entry["unique_block_count"] += len(sample_stats["unique_block_starts"])
    entry["sample_count"] += 1

    engine = sample_meta.get("engine")
    case = sample_meta.get("case")
    if engine:
        entry["engine_counts"][str(engine)] += 1
    if case:
        entry["case_counts"][str(case)] += 1

    if len(entry["example_sources"]) < 5:
        entry["example_sources"].append(
            {
                "analysis_file": sample_meta["analysis_file"],
                "result_name": sample_meta["result_name"],
                "sample_name": sample_meta["sample_name"],
                "engine": sample_meta["engine"],
                "case": sample_meta["case"],
                "iteration": sample_meta["iteration"],
                "weighted_exec_count": int(sample_stats["weighted_exec_count"]),
                "occurrences": int(sample_stats["occurrences"]),
                "unique_block_count": len(sample_stats["unique_block_starts"]),
                "example_blocks": sample_stats["example_blocks"],
            }
        )


def merge_pair_stats(
    aggregate: dict[tuple[str, str], dict[str, object]],
    pair: tuple[str, str],
    sample_meta: dict[str, object],
    sample_stats: dict[str, object],
) -> None:
    entry = aggregate.setdefault(
        pair,
        {
            "pair": list(pair),
            "pair_display": " -> ".join(pair),
            "ngram": list(pair),
            "ngram_display": " -> ".join(pair),
            "first_handler": pair[0],
            "second_handler": pair[1],
            "anchor_handler": sample_stats["anchor_handler"],
            "anchor_display": sample_stats["anchor_display"],
            "direction": sample_stats["direction"],
            "relation_kind": sample_stats["relation_kind"],
            "relation_priority": sample_stats["relation_priority"],
            "shared_resources": list(sample_stats["shared_resources"]),
            "shared_resource_variants": Counter(),
            "relation_kind_variants": Counter(),
            "legality_notes": list(sample_stats["legality_notes"]),
            "weighted_exec_count": 0,
            "occurrences": 0,
            "unique_block_count": 0,
            "sample_count": 0,
            "engine_counts": Counter(),
            "case_counts": Counter(),
            "example_sources": [],
        },
    )

    entry["weighted_exec_count"] += int(sample_stats["weighted_exec_count"])
    entry["occurrences"] += int(sample_stats["occurrences"])
    entry["unique_block_count"] += len(sample_stats["unique_block_starts"])
    entry["sample_count"] += 1
    entry["shared_resource_variants"].update(sample_stats["shared_resource_variants"])
    entry["relation_kind_variants"].update(sample_stats["relation_kind_variants"])

    engine = sample_meta.get("engine")
    case = sample_meta.get("case")
    if engine:
        entry["engine_counts"][str(engine)] += 1
    if case:
        entry["case_counts"][str(case)] += 1

    if len(entry["example_sources"]) < 5:
        entry["example_sources"].append(
            {
                "analysis_file": sample_meta["analysis_file"],
                "result_name": sample_meta["result_name"],
                "sample_name": sample_meta["sample_name"],
                "engine": sample_meta["engine"],
                "case": sample_meta["case"],
                "iteration": sample_meta["iteration"],
                "weighted_exec_count": int(sample_stats["weighted_exec_count"]),
                "occurrences": int(sample_stats["occurrences"]),
                "unique_block_count": len(sample_stats["unique_block_starts"]),
                "example_blocks": sample_stats["example_blocks"],
            }
        )


def normalize_counter_map(counter: Counter[str]) -> dict[str, int]:
    return dict(sorted(counter.items()))


def is_jcc_pair(pair: list[str]) -> bool:
    return any(op_short_name(name).startswith("Jcc") for name in pair)


def relation_dep_weight(relation_kind: str, raw_weight: int, rar_weight: int, waw_weight: int) -> int:
    if relation_kind == "RAW":
        return raw_weight
    if relation_kind == "RAR":
        return rar_weight
    if relation_kind == "WAW":
        return waw_weight
    return max(rar_weight, waw_weight)


def relation_jcc_weight(pair: list[str], relation_kind: str, jcc_multiplier: int, jcc_mode: str) -> int:
    if jcc_multiplier <= 1 or not is_jcc_pair(pair):
        return 1
    if jcc_mode == "none":
        return 1
    if jcc_mode == "raw-only" and relation_kind != "RAW":
        return 1
    return jcc_multiplier


def normalize_anchor_entry(entry: dict[str, object]) -> dict[str, object]:
    normalized = dict(entry)
    normalized["engine_counts"] = normalize_counter_map(entry["engine_counts"])
    normalized["case_counts"] = normalize_counter_map(entry["case_counts"])
    return normalized


def normalize_candidate_entry(
    entry: dict[str, object],
    anchor_entry: dict[str, object],
    *,
    score_basis: str,
    raw_weight: int,
    rar_weight: int,
    waw_weight: int,
    jcc_multiplier: int,
    jcc_mode: str,
) -> dict[str, object]:
    normalized = dict(entry)
    normalized["engine_counts"] = normalize_counter_map(entry["engine_counts"])
    normalized["case_counts"] = normalize_counter_map(entry["case_counts"])
    shared_variant_counts = Counter({
        ",".join(variant): count for variant, count in entry["shared_resource_variants"].items()
    })
    dominant_shared_variant: tuple[str, ...] = ()
    dominant_shared_count = 0
    if entry["shared_resource_variants"]:
        dominant_shared_variant, dominant_shared_count = entry["shared_resource_variants"].most_common(1)[0]

    dominant_relation_kind = str(entry["relation_kind"])
    dominant_relation_count = 0
    if entry["relation_kind_variants"]:
        dominant_relation_kind, dominant_relation_count = entry["relation_kind_variants"].most_common(1)[0]
        if "RAW" in entry["relation_kind_variants"]:
            dominant_relation_kind = "RAW"
            dominant_relation_count = int(entry["relation_kind_variants"]["RAW"])
        elif "RAR/WAW" in entry["relation_kind_variants"]:
            dominant_relation_kind = "RAR/WAW"
            dominant_relation_count = int(entry["relation_kind_variants"]["RAR/WAW"])

    dominant_dep_weight = relation_dep_weight(
        dominant_relation_kind,
        raw_weight=raw_weight,
        rar_weight=rar_weight,
        waw_weight=waw_weight,
    )

    normalized["shared_resources"] = list(dominant_shared_variant)
    normalized["shared_resource_variants"] = normalize_counter_map(shared_variant_counts)
    normalized["dominant_shared_resource_count"] = int(dominant_shared_count)
    normalized["dominant_shared_resource_ratio"] = (
        dominant_shared_count / int(entry["occurrences"]) if int(entry["occurrences"]) else 0.0
    )
    normalized["relation_kind"] = dominant_relation_kind
    normalized["relation_priority"] = dominant_dep_weight
    normalized["relation_kind_variants"] = normalize_counter_map(entry["relation_kind_variants"])
    normalized["dominant_relation_kind_count"] = int(dominant_relation_count)
    normalized["anchor_weighted_exec_count"] = int(anchor_entry["weighted_exec_count"])
    normalized["anchor_sample_count"] = int(anchor_entry["sample_count"])
    normalized["anchor_unique_block_count"] = int(anchor_entry["unique_block_count"])
    normalized["anchor_semantics"] = anchor_entry["semantics"]
    base_freq = (
        int(anchor_entry["weighted_exec_count"])
        if score_basis == "anchor"
        else int(entry["weighted_exec_count"])
    )
    jcc_weight = relation_jcc_weight(
        entry["pair"],
        dominant_relation_kind,
        jcc_multiplier=jcc_multiplier,
        jcc_mode=jcc_mode,
    )
    normalized["score"] = base_freq * dominant_dep_weight * jcc_weight
    normalized["score_basis"] = score_basis
    normalized["base_frequency"] = base_freq
    normalized["jcc_weight"] = jcc_weight
    return normalized


def candidate_sort_key(entry: dict[str, object]) -> tuple[object, ...]:
    return (
        entry["score"],
        entry["weighted_exec_count"],
        entry["sample_count"],
        entry["occurrences"],
        entry["unique_block_count"],
    )


def build_markdown(
    inputs: list[str],
    analysis_files: list[Path],
    included_samples: list[dict[str, object]],
    skipped_samples: list[dict[str, object]],
    anchors: list[dict[str, object]],
    candidates: list[dict[str, object]],
    anchor_top: int,
) -> str:
    lines = [
        "# SuperOpcode Candidates",
        "",
        f"- Inputs: {', '.join(inputs)}",
        "- Strategy: global 2-gram scoring with hot anchors and dependency weights",
        f"- Analysis files discovered: {len(analysis_files)}",
        f"- Included samples: {len(included_samples)}",
        f"- Skipped samples: {len(skipped_samples)}",
        f"- Anchor display limit: {anchor_top}",
        f"- Candidate count: {len(candidates)}",
        "",
        "## Top Anchors",
        "",
        "| Rank | Anchor | Weighted Exec | Samples | Occurrences | Unique Blocks |",
        "| --- | --- | ---: | ---: | ---: | ---: |",
    ]

    for rank, anchor in enumerate(anchors, start=1):
        lines.append(
            f"| {rank} | `{anchor['anchor_display']}` | "
            f"{anchor['weighted_exec_count']} | {anchor['sample_count']} | "
            f"{anchor['occurrences']} | {anchor['unique_block_count']} |"
        )

    lines.extend(
        [
            "",
            "## Top Candidates",
            "",
            "| Rank | Pair | Relation | Score | Anchor | Dir | Weighted Exec | Samples | Occurrences | Shared |",
            "| --- | --- | --- | ---: | --- | --- | ---: | ---: | ---: | --- |",
        ]
    )

    for rank, candidate in enumerate(candidates, start=1):
        shared = ", ".join(candidate.get("shared_resources") or []) or "-"
        lines.append(
            f"| {rank} | `{candidate['pair_display']}` | {candidate['relation_kind']} | "
            f"{candidate['score']} | `{candidate['anchor_display']}` | {candidate['direction']} | "
            f"{candidate['weighted_exec_count']} | {candidate['sample_count']} | "
            f"{candidate['occurrences']} | {shared} |"
        )

    if skipped_samples:
        lines.extend(
            [
                "",
                "## Skipped Samples",
                "",
                "| Sample | Reason |",
                "| --- | --- |",
            ]
        )
        for sample in skipped_samples:
            reason = "; ".join(sample["reasons"])
            lines.append(f"| `{sample['analysis_file']}` | {reason} |")

    return "\n".join(lines) + "\n"


def main() -> int:
    args = parse_args()
    if args.n_gram != 2:
        raise RuntimeError("--n-gram must remain 2 because current SuperOpcode generation only supports 2-op pairs")

    analysis_files = discover_analysis_files(args.inputs)
    if not analysis_files:
        raise RuntimeError("No blocks_analysis.json files found under the provided inputs")

    aggregate_anchors: dict[str, dict[str, object]] = {}
    aggregate_pairs: dict[tuple[str, str], dict[str, object]] = {}
    included_samples: list[dict[str, object]] = []
    skipped_samples: list[dict[str, object]] = []

    for analysis_file in analysis_files:
        data = json.loads(analysis_file.read_text(encoding="utf-8"))
        sample_meta = infer_sample_metadata(analysis_file)
        skip, reasons = should_skip_analysis(data)
        if skip:
            skipped_samples.append(
                {
                    "analysis_file": str(analysis_file),
                    "reasons": reasons,
                }
            )
            continue

        blocks = data.get("blocks") or []
        sample_anchors, sample_pairs = analyze_sample_candidates(blocks)
        if not sample_pairs:
            skipped_samples.append(
                {
                    "analysis_file": str(analysis_file),
                    "reasons": ["no def-use-adjacent 2-op candidates found in blocks"],
                }
            )
            continue

        included_samples.append(sample_meta)
        for anchor, sample_stats in sample_anchors.items():
            merge_anchor_stats(aggregate_anchors, anchor, sample_meta, sample_stats)
        for pair, sample_stats in sample_pairs.items():
            merge_pair_stats(aggregate_pairs, pair, sample_meta, sample_stats)

    anchors = [normalize_anchor_entry(entry) for entry in aggregate_anchors.values()]
    anchors.sort(
        key=lambda entry: (
            entry["weighted_exec_count"],
            entry["sample_count"],
            entry["occurrences"],
            entry["unique_block_count"],
        ),
        reverse=True,
    )

    anchor_index = {str(anchor["anchor"]): anchor for anchor in anchors}

    candidates = []
    for entry in aggregate_pairs.values():
        anchor_name = str(entry["anchor_handler"])
        if anchor_name not in anchor_index:
            continue
        if entry["sample_count"] < args.min_samples:
            continue
        if entry["weighted_exec_count"] < args.min_weighted_exec_count:
            continue
        normalized_entry = normalize_candidate_entry(
            entry,
            anchor_index[anchor_name],
            score_basis=args.score_basis,
            raw_weight=args.raw_weight,
            rar_weight=args.rar_weight,
            waw_weight=args.waw_weight,
            jcc_multiplier=args.jcc_multiplier,
            jcc_mode=args.jcc_mode,
        )
        candidates.append(normalized_entry)

    candidates.sort(key=candidate_sort_key, reverse=True)
    candidates = candidates[: args.top]
    relation_kind_counts = Counter(str(entry["relation_kind"]) for entry in candidates)

    output = {
        "metadata": {
            "inputs": [str(Path(item).resolve()) for item in args.inputs],
            "strategy": "global-score-anchor-freq-times-dep-weight",
            "analysis_file_count": len(analysis_files),
            "included_sample_count": len(included_samples),
            "skipped_sample_count": len(skipped_samples),
            "candidate_count": len(candidates),
            "anchor_count": len(anchors),
            "anchor_top_limit": args.anchor_top,
            "score_basis": args.score_basis,
            "raw_weight": args.raw_weight,
            "rar_weight": args.rar_weight,
            "waw_weight": args.waw_weight,
            "jcc_multiplier": args.jcc_multiplier,
            "jcc_mode": args.jcc_mode,
            "min_samples": args.min_samples,
            "min_weighted_exec_count": args.min_weighted_exec_count,
            "top_limit": args.top,
            "superopcode_width": 2,
            "selected_relation_kind_counts": normalize_counter_map(relation_kind_counts),
        },
        "included_samples": included_samples,
        "skipped_samples": skipped_samples,
        "anchors": anchors[: args.anchor_top],
        "candidates": candidates,
    }

    output_json = Path(args.output_json).resolve()
    output_json.parent.mkdir(parents=True, exist_ok=True)
    output_json.write_text(json.dumps(output, indent=2), encoding="utf-8")

    if args.output_md:
        output_md = Path(args.output_md).resolve()
        output_md.parent.mkdir(parents=True, exist_ok=True)
        output_md.write_text(
            build_markdown(
                inputs=args.inputs,
                analysis_files=analysis_files,
                included_samples=included_samples,
                skipped_samples=skipped_samples,
                anchors=anchors[: min(len(anchors), max(20, args.anchor_top))],
                candidates=candidates,
                anchor_top=args.anchor_top,
            ),
            encoding="utf-8",
        )

    print(f"Wrote {len(candidates)} candidates from {len(included_samples)} samples to {output_json}")
    if args.output_md:
        print(f"Wrote markdown summary to {Path(args.output_md).resolve()}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
