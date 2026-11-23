#!/usr/bin/env python3
"""Render validated benchmark results as Markdown tables."""
import argparse
import json
from pathlib import Path


BACKEND_LABELS = {
    "podish-off": "Podish interpreter",
    "podish-jit": "Podish LLVM JIT",
    "qemu-user": "QEMU Usermode TCG",
    "qemu-system": "QEMU system TCG",
    "ish": "iSH",
}


def format_result(outcome, metric):
    if "median" in outcome:
        return f"{outcome['median']:.3f}" if metric == "s" else f"{outcome['median']:.1f}"
    if outcome.get("failed"):
        return f"Failed ({outcome['failed']})"
    return "—"


def print_coremark_table(tables):
    backends = [
        backend for backend in BACKEND_LABELS
        if any(backend in summary["coremark"] for _, summary in tables)
    ]
    print("| Environment | " + " | ".join(BACKEND_LABELS[backend] for backend in backends) + " |")
    print("| --- | " + " | ".join("---:" for _ in backends) + " |")
    for metadata, summary in tables:
        row = [format_result(summary["coremark"].get(backend, {}), "iterations/s") for backend in backends]
        print(f"| {metadata['machine']} | " + " | ".join(row) + " |")
    print()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("results", type=Path, nargs="+")
    parser.add_argument("--allow-smoke", action="store_true")
    options = parser.parse_args()

    reference = None
    final_crcs = set()
    tables = []
    for result in options.results:
        metadata = json.loads((result / "metadata.json").read_text())
        summary = json.loads((result / "summary.json").read_text())
        if metadata["smoke"] and not options.allow_smoke:
            parser.error(f"{result} contains smoke results")
        identity = tuple(metadata[key] for key in ("commit", "fixture", "iterations", "repeats", "warmup", "smoke"))
        identity += (metadata.get("coremark_build"),)
        if reference is None:
            reference = identity
        elif reference != identity:
            parser.error(f"{result} uses a different commit, fixture or run protocol")
        for line in (result / "runs.jsonl").read_text().splitlines():
            run = json.loads(line)
            if run["case"] == "coremark" and run["status"] == "ok" and run["round"] > 0:
                final_crcs.add(run["final_crc"])
        tables.append((metadata, summary))
    if len(final_crcs) > 1:
        parser.error("CoreMark final CRC differs across hosts")

    metadata = tables[0][0]
    build = metadata.get("coremark_build")
    if build:
        print(f"CoreMark 1.0 uses one i386 ELF built with {build['compiler']} and `{build['flags']}`."
              f" Linkage is {build['linkage']}. SHA-256 `{build['sha256']}`.\n")
    if all(set(summary) == {"coremark"} for _, summary in tables):
        print("CoreMark scores in iterations/s.\n")
        print_coremark_table(tables)
        return

    for metadata, summary in tables:
        backends = list(dict.fromkeys(backend for case in summary.values() for backend in case))
        print(f"### {metadata['machine']}\n")
        print("| Workload | Unit | " + " | ".join(backends) + " |")
        print("| --- | --- | " + " | ".join("---:" for _ in backends) + " |")
        for workload, outcomes in summary.items():
            metric = next((item["unit"] for item in outcomes.values() if "unit" in item), "—")
            results = [format_result(outcomes.get(backend, {}), metric) for backend in backends]
            print(f"| {workload} | {metric} | " + " | ".join(results) + " |")
        print()


if __name__ == "__main__":
    main()
