#!/usr/bin/env python3

from __future__ import annotations

import argparse
import json
import sys
from dataclasses import dataclass, field
from pathlib import Path


TOKEN_ABBREVIATIONS = {
    "Group1": "G1",
    "Group2": "G2",
    "Group3": "G3",
    "Group4": "G4",
    "Group5": "G5",
    "Load": "Ld",
    "Store": "St",
    "Flags": "Flg",
    "NoFlags": "NoFlg",
    "ModReg": "MR",
    "Reg": "R",
    "Reg32": "R32",
    "Byte": "B",
    "Word": "W",
    "Dword": "D",
    "Qword": "Q",
    "EspBaseNoIndexNoSegment": "EspBase",
    "EaxBaseNoIndexNoSegment": "EaxBase",
    "EbxBaseNoIndexNoSegment": "EbxBase",
    "EcxBaseNoIndexNoSegment": "EcxBase",
    "EdxBaseNoIndexNoSegment": "EdxBase",
    "EbpBaseNoIndexNoSegment": "EbpBase",
    "EsiBaseNoIndexNoSegment": "EsiBase",
    "EdiBaseNoIndexNoSegment": "EdiBase",
}


@dataclass(slots=True)
class ShapeSummary:
    symbols: tuple[str, ...]
    total_exec_count: int = 0
    max_exec_count: int = 0
    instances: int = 0
    sample_start_eip_hex: str = ""
    distinct_start_eips: set[str] = field(default_factory=set)

    @property
    def op_count(self) -> int:
        return len(self.symbols)


def shorten_symbol(symbol: str) -> str:
    short = symbol
    for prefix in ("fiberish::op::", "op::"):
        if short.startswith(prefix):
            short = short[len(prefix):]
            break
    if short.startswith("Op"):
        short = short[2:]

    tokens = [token for token in short.split("_") if token]
    compact = [TOKEN_ABBREVIATIONS.get(token, token) for token in tokens]
    return "_".join(compact) or symbol


def load_block_shapes(input_path: Path) -> list[ShapeSummary]:
    with input_path.open("r", encoding="utf-8") as handle:
        payload = json.load(handle)

    blocks = payload.get("blocks")
    if not isinstance(blocks, list):
        raise ValueError(f"expected top-level 'blocks' array in {input_path}")

    grouped: dict[tuple[str, ...], ShapeSummary] = {}
    for block in blocks:
        if not isinstance(block, dict):
            continue

        ops = block.get("ops")
        if not isinstance(ops, list) or not ops:
            continue

        symbols = tuple(
            op.get("symbol", "<missing-symbol>")
            for op in ops
            if isinstance(op, dict)
        )
        if not symbols:
            continue

        exec_count = int(block.get("exec_count", 0))
        start_eip_hex = str(block.get("start_eip_hex", ""))

        summary = grouped.get(symbols)
        if summary is None:
            summary = ShapeSummary(symbols=symbols, sample_start_eip_hex=start_eip_hex)
            grouped[symbols] = summary

        summary.total_exec_count += exec_count
        summary.max_exec_count = max(summary.max_exec_count, exec_count)
        summary.instances += 1
        if start_eip_hex:
            summary.distinct_start_eips.add(start_eip_hex)
            if not summary.sample_start_eip_hex:
                summary.sample_start_eip_hex = start_eip_hex

    return sorted(
        grouped.values(),
        key=lambda item: (
            -item.total_exec_count,
            -item.max_exec_count,
            -item.instances,
            item.op_count,
            item.sample_start_eip_hex,
        ),
    )


def format_int(value: int) -> str:
    return f"{value:,}"


def render_markdown(input_path: Path, summaries: list[ShapeSummary], limit: int) -> str:
    top = summaries[:limit]
    lines: list[str] = []
    lines.append("# Top Block Shapes")
    lines.append("")
    lines.append(f"- Source: `{input_path}`")
    lines.append(f"- Ranking: exact `ops[].symbol` shape, sorted by total `exec_count`")
    lines.append(f"- Shape count: {format_int(len(summaries))}")
    lines.append(f"- Reported top-N: {format_int(len(top))}")
    lines.append("")

    for index, summary in enumerate(top, start=1):
        short_symbols = [shorten_symbol(symbol) for symbol in summary.symbols]
        shape_line = " -> ".join(short_symbols)

        lines.append(
            f"## {index}. total_exec={format_int(summary.total_exec_count)} | "
            f"instances={format_int(summary.instances)} | ops={summary.op_count}"
        )
        lines.append("")
        lines.append(f"- Shape: `{shape_line}`")
        lines.append(f"- Max single-block exec_count: {format_int(summary.max_exec_count)}")
        lines.append(f"- Distinct start EIPs: {format_int(len(summary.distinct_start_eips))}")
        lines.append(f"- Sample start EIP: `{summary.sample_start_eip_hex or 'n/a'}`")
        lines.append("")
        lines.append("| # | Short name | Full symbol |")
        lines.append("| ---: | --- | --- |")
        for op_index, (short_name, symbol) in enumerate(zip(short_symbols, summary.symbols), start=1):
            lines.append(f"| {op_index} | `{short_name}` | `{symbol}` |")
        lines.append("")

    return "\n".join(lines)


def main() -> int:
    parser = argparse.ArgumentParser(description="Summarize the hottest block shapes from blocks_analysis.full.json")
    parser.add_argument("input", type=Path, help="Path to blocks_analysis.full.json")
    parser.add_argument("-o", "--output", type=Path, required=True, help="Path to write the markdown report")
    parser.add_argument("--limit", type=int, default=100, help="Number of shapes to include in the report")
    args = parser.parse_args()

    if args.limit <= 0:
        raise ValueError("--limit must be positive")

    summaries = load_block_shapes(args.input)
    markdown = render_markdown(args.input, summaries, args.limit)

    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(markdown, encoding="utf-8")

    print(f"Wrote {args.output} with {min(args.limit, len(summaries))} shapes")
    return 0


if __name__ == "__main__":
    sys.exit(main())