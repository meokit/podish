#!/usr/bin/env python3

from __future__ import annotations

import argparse
import glob
import json
import re
from pathlib import Path


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Generate simple 2-op SuperOpcode handlers from candidate JSON")
    parser.add_argument("--input", "-i", default="superopcode_candidates.json", help="Path to candidate JSON")
    parser.add_argument(
        "--output",
        "-o",
        default="libfibercpu/generated/superopcodes.generated.cpp",
        help="Path to generated C++ output",
    )
    parser.add_argument("--top", type=int, default=32, help="Maximum number of candidate pairs to emit")
    return parser.parse_args()


def sanitize_name(name: str) -> str:
    return re.sub(r"[^A-Za-z0-9_]", "_", name)


def build_symbol_namespace_map() -> dict[str, str]:
    symbol_map: dict[str, str] = {}
    pattern = re.compile(r"\b(Op[A-Za-z0-9_]+)\s*\(")

    for file_path in sorted(glob.glob("libfibercpu/ops/*_impl.h")):
        inside_op_namespace = False
        for raw_line in Path(file_path).read_text(encoding="utf-8").splitlines():
            line = raw_line.strip()
            if line == "namespace op {":
                inside_op_namespace = True
                continue
            if inside_op_namespace and line == "}  // namespace op":
                inside_op_namespace = False
                continue

            match = pattern.search(line)
            if not match:
                continue

            symbol_map.setdefault(match.group(1), "op" if inside_op_namespace else "root")

    return symbol_map


def load_candidates(path: Path, top: int) -> list[dict[str, object]]:
    data = json.loads(path.read_text(encoding="utf-8"))
    out: list[dict[str, object]] = []
    seen: set[tuple[str, str]] = set()
    for candidate in data.get("candidates", []):
        ngram = candidate.get("ngram") or []
        if len(ngram) != 2:
            continue
        op0 = str(ngram[0])
        op1 = str(ngram[1])
        if not op0.startswith("Op") or not op1.startswith("Op"):
            continue
        key = (op0, op1)
        if key in seen:
            continue
        seen.add(key)
        out.append({
            "op0": op0,
            "op1": op1,
            "weighted_exec_count": int(candidate.get("weighted_exec_count", 0)),
            "occurrences": int(candidate.get("occurrences", 0)),
        })
        if len(out) >= top:
            break
    return out


def qualify_symbol(name: str, symbol_map: dict[str, str]) -> str:
    return name if symbol_map.get(name) == "root" else f"op::{name}"


def emit_handler(index: int, candidate: dict[str, object], symbol_map: dict[str, str]) -> str:
    op0_name = str(candidate["op0"])
    op1_name = str(candidate["op1"])
    handler_name = f"SuperOpcode_{index:03d}_{sanitize_name(op0_name)}__{sanitize_name(op1_name)}"
    op0_qualified = qualify_symbol(op0_name, symbol_map)
    op1_qualified = qualify_symbol(op1_name, symbol_map)
    return f"""// weighted_exec_count={candidate["weighted_exec_count"]} occurrences={candidate["occurrences"]}
ATTR_PRESERVE_NONE int64_t {handler_name}(EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit,
                                          mem::MicroTLB utlb, uint32_t branch, uint64_t flags_cache) {{
    auto flow0 = {op0_qualified}(state, op, &utlb, GetImm(op), &branch, flags_cache);
    if (flow0 != LogicFlow::Continue) [[unlikely]] {{
        return HandleSuperOpcodeFlow(flow0, state, op, instr_limit, utlb, branch, flags_cache);
    }}

    DecodedOp* second_op = NextOp(op);
    auto flow1 = {op1_qualified}(state, second_op, &utlb, GetImm(second_op), &branch, flags_cache);
    if (flow1 != LogicFlow::Continue) [[unlikely]] {{
        return HandleSuperOpcodeFlow(flow1, state, second_op, instr_limit, utlb, branch, flags_cache);
    }}

    if (auto* next_op = NextOp(second_op)) {{
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }}
    __builtin_unreachable();
}}
"""


def build_cpp(candidates: list[dict[str, object]]) -> str:
    symbol_map = build_symbol_namespace_map()
    impl_includes: list[str] = []
    for file_path in sorted(glob.glob("libfibercpu/ops/*_impl.h")):
        impl_includes.append(f'#include "../ops/{Path(file_path).name}"')

    lines = [
        '#include "../ops.h"',
        '#include "../superopcodes.h"',
        *impl_includes,
        "",
        "namespace fiberish {",
        "",
    ]

    for index, candidate in enumerate(candidates):
        lines.append(emit_handler(index, candidate, symbol_map))

    lines.append("__attribute__((used)) HandlerFunc FindSuperOpcode(const DecodedOp* ops) {")
    lines.append("    if (!ops) return nullptr;")
    for index, candidate in enumerate(candidates):
        op0 = str(candidate["op0"])
        op1 = str(candidate["op1"])
        op0_qualified = qualify_symbol(op0, symbol_map)
        op1_qualified = qualify_symbol(op1, symbol_map)
        handler_name = f"SuperOpcode_{index:03d}_{sanitize_name(op0)}__{sanitize_name(op1)}"
        lines.append(
            f"    if (ops[0].handler == (HandlerFunc)DispatchWrapper<{op0_qualified}> && "
            f"ops[1].handler == (HandlerFunc)DispatchWrapper<{op1_qualified}>) return {handler_name};"
        )
    lines.append("    return nullptr;")
    lines.append("}")
    lines.append("")
    lines.append("}  // namespace fiberish")
    lines.append("")
    return "\n".join(lines)


def main() -> int:
    args = parse_args()
    input_path = Path(args.input)
    if not input_path.exists():
        raise FileNotFoundError(f"{input_path} not found")

    candidates = load_candidates(input_path, args.top)
    output_path = Path(args.output)
    output_path.parent.mkdir(parents=True, exist_ok=True)
    output_path.write_text(build_cpp(candidates), encoding="utf-8")
    print(f"Wrote {len(candidates)} superopcodes to {output_path}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
