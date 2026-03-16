#!/usr/bin/env python3

from __future__ import annotations

import argparse
import json
from collections import Counter
from pathlib import Path


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Aggregate handler N-gram data across block analysis samples."
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
        help="N-gram size to aggregate (default: 2)",
    )
    parser.add_argument(
        "--top",
        type=int,
        default=100,
        help="Maximum number of aggregate candidates to emit (default: 100)",
    )
    parser.add_argument(
        "--min-samples",
        type=int,
        default=1,
        help="Minimum number of distinct samples an N-gram must appear in (default: 1)",
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


def analyze_sample_ngrams(blocks: list[dict[str, object]], n: int) -> dict[tuple[str, ...], dict[str, object]]:
    stats: dict[tuple[str, ...], dict[str, object]] = {}

    for block in blocks:
        ops = block.get("ops") or []
        symbols = [op.get("symbol", "<unknown>") for op in ops]
        if len(symbols) < n:
            continue

        block_start = block.get("start_eip_hex") or hex(block.get("start_eip", 0))
        block_exec_count = int(block.get("exec_count", 0))
        for index in range(len(symbols) - n + 1):
            ngram = tuple(symbols[index:index + n])
            entry = stats.setdefault(ngram, {
                "weighted_exec_count": 0,
                "occurrences": 0,
                "unique_block_starts": set(),
                "example_blocks": [],
            })
            entry["weighted_exec_count"] += block_exec_count
            entry["occurrences"] += 1
            entry["unique_block_starts"].add(block_start)
            if len(entry["example_blocks"]) < 3:
                entry["example_blocks"].append({
                    "start_eip_hex": block_start,
                    "exec_count": block_exec_count,
                    "start_op_index": index,
                })

    return stats


def merge_candidate(
    aggregate: dict[tuple[str, ...], dict[str, object]],
    ngram: tuple[str, ...],
    sample_meta: dict[str, object],
    sample_stats: dict[str, object],
) -> None:
    entry = aggregate.setdefault(ngram, {
        "ngram": list(ngram),
        "ngram_display": " -> ".join(ngram),
        "weighted_exec_count": 0,
        "occurrences": 0,
        "unique_block_count": 0,
        "sample_count": 0,
        "engine_counts": Counter(),
        "case_counts": Counter(),
        "example_sources": [],
    })

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
        entry["example_sources"].append({
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
        })


def normalize_aggregate_entry(entry: dict[str, object]) -> dict[str, object]:
    normalized = dict(entry)
    normalized["engine_counts"] = dict(sorted(entry["engine_counts"].items()))
    normalized["case_counts"] = dict(sorted(entry["case_counts"].items()))
    return normalized


def build_markdown(
    inputs: list[str],
    analysis_files: list[Path],
    included_samples: list[dict[str, object]],
    skipped_samples: list[dict[str, object]],
    candidates: list[dict[str, object]],
    ngram_size: int,
) -> str:
    lines = [
        "# SuperOpcode Candidates",
        "",
        f"- Inputs: {', '.join(inputs)}",
        f"- N-gram size: {ngram_size}",
        f"- Analysis files discovered: {len(analysis_files)}",
        f"- Included samples: {len(included_samples)}",
        f"- Skipped samples: {len(skipped_samples)}",
        f"- Candidate count: {len(candidates)}",
        "",
        "## Top Candidates",
        "",
        "| Rank | N-Gram | Weighted Exec | Samples | Occurrences | Unique Blocks | Cases |",
        "| --- | --- | ---: | ---: | ---: | ---: | --- |",
    ]

    for rank, candidate in enumerate(candidates, start=1):
        case_counts = candidate.get("case_counts") or {}
        cases = ", ".join(f"{name}:{count}" for name, count in case_counts.items()) or "-"
        lines.append(
            f"| {rank} | `{candidate['ngram_display']}` | "
            f"{candidate['weighted_exec_count']} | {candidate['sample_count']} | "
            f"{candidate['occurrences']} | {candidate['unique_block_count']} | {cases} |"
        )

    if skipped_samples:
        lines.extend([
            "",
            "## Skipped Samples",
            "",
            "| Sample | Reason |",
            "| --- | --- |",
        ])
        for sample in skipped_samples:
            reason = "; ".join(sample["reasons"])
            lines.append(f"| `{sample['analysis_file']}` | {reason} |")

    return "\n".join(lines) + "\n"


def main() -> int:
    args = parse_args()
    analysis_files = discover_analysis_files(args.inputs)
    if not analysis_files:
        raise RuntimeError("No blocks_analysis.json files found under the provided inputs")

    aggregate: dict[tuple[str, ...], dict[str, object]] = {}
    included_samples: list[dict[str, object]] = []
    skipped_samples: list[dict[str, object]] = []

    for analysis_file in analysis_files:
        data = json.loads(analysis_file.read_text(encoding="utf-8"))
        sample_meta = infer_sample_metadata(analysis_file)
        skip, reasons = should_skip_analysis(data)
        if skip:
            skipped_samples.append({
                "analysis_file": str(analysis_file),
                "reasons": reasons,
            })
            continue

        blocks = data.get("blocks") or []
        sample_ngrams = analyze_sample_ngrams(blocks, args.n_gram)
        if not sample_ngrams:
            skipped_samples.append({
                "analysis_file": str(analysis_file),
                "reasons": [f"no {args.n_gram}-grams found in blocks"],
            })
            continue

        included_samples.append(sample_meta)
        for ngram, sample_stats in sample_ngrams.items():
            merge_candidate(aggregate, ngram, sample_meta, sample_stats)

    candidates = [
        normalize_aggregate_entry(entry)
        for entry in aggregate.values()
        if entry["sample_count"] >= args.min_samples
        and entry["weighted_exec_count"] >= args.min_weighted_exec_count
    ]
    candidates.sort(
        key=lambda entry: (
            entry["weighted_exec_count"],
            entry["sample_count"],
            entry["occurrences"],
            entry["unique_block_count"],
        ),
        reverse=True,
    )
    candidates = candidates[:args.top]

    output = {
        "metadata": {
            "inputs": [str(Path(item).resolve()) for item in args.inputs],
            "n_gram_size": args.n_gram,
            "analysis_file_count": len(analysis_files),
            "included_sample_count": len(included_samples),
            "skipped_sample_count": len(skipped_samples),
            "candidate_count": len(candidates),
            "min_samples": args.min_samples,
            "min_weighted_exec_count": args.min_weighted_exec_count,
            "top_limit": args.top,
        },
        "included_samples": included_samples,
        "skipped_samples": skipped_samples,
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
                candidates=candidates,
                ngram_size=args.n_gram,
            ),
            encoding="utf-8",
        )

    print(f"Wrote {len(candidates)} candidates from {len(included_samples)} samples to {output_json}")
    if args.output_md:
        print(f"Wrote markdown summary to {Path(args.output_md).resolve()}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
