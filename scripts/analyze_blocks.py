#!/usr/bin/env python3

import argparse
import json
import os
import re
import struct
import subprocess
import sys
from collections import defaultdict


def load_symbols(lib_path):
    symbols = {}
    commands = [
        ["nm", "-n", "-C", lib_path],
        ["nm", "-n", lib_path],
    ]

    for cmd in commands:
        try:
            print(f"Running: {' '.join(cmd)}")
            output = subprocess.check_output(cmd).decode("utf-8", errors="replace")
            for line in output.splitlines():
                parts = line.strip().split(" ", 2)
                if len(parts) < 3:
                    continue
                addr_str, type_code, name = parts
                try:
                    addr = int(addr_str, 16)
                except ValueError:
                    continue
                if type_code.upper() in ("T", "W"):
                    symbols[addr] = name
            if symbols:
                return symbols
        except Exception as exc:
            print(f"Warning: failed to load symbols with {' '.join(cmd)}: {exc}")

    return symbols


def find_symbol(offset, symbols_map):
    name = symbols_map.get(offset)
    if not name:
        return f"func_{offset:x}"

    match = re.search(r"op::([a-zA-Z0-9_]+)", name)
    if match:
        return match.group(1)
    return name


def decode_ea_desc(ea_desc):
    return {
        "ea_desc": ea_desc,
        "ea_desc_hex": f"0x{ea_desc:x}",
        "base_offset": ea_desc & 0x3F,
        "base_offset_hex": f"0x{(ea_desc & 0x3F):x}",
        "index_offset": (ea_desc >> 6) & 0x3F,
        "index_offset_hex": f"0x{((ea_desc >> 6) & 0x3F):x}",
        "scale": (ea_desc >> 12) & 0x3,
        "scale_hex": f"0x{((ea_desc >> 12) & 0x3):x}",
        "segment": (ea_desc >> 14) & 0x7,
        "segment_hex": f"0x{((ea_desc >> 14) & 0x7):x}",
    }


def resolve_input_paths(input_path):
    if os.path.isdir(input_path):
        dump_file = os.path.join(input_path, "blocks.bin")
        summary_file = os.path.join(input_path, "summary.json")
        default_output = os.path.join(input_path, "blocks_analysis.json")
    else:
        dump_file = input_path
        summary_file = None
        default_output = "blocks_analysis.json"

    if not os.path.exists(dump_file):
        raise FileNotFoundError(f"Block dump file not found: {dump_file}")

    return dump_file, summary_file, default_output


def load_summary(summary_file):
    if not summary_file or not os.path.exists(summary_file):
        return None
    with open(summary_file, "r", encoding="utf-8") as f:
        return json.load(f)


def parse_blocks(dump_file, symbols):
    blocks_data = []
    parse_warnings = []

    with open(dump_file, "rb") as f:
        base_addr_bytes = f.read(8)
        if len(base_addr_bytes) != 8:
            raise RuntimeError("Error reading base address")
        base_addr = struct.unpack("<Q", base_addr_bytes)[0]
        print(f"Runtime Base Address: 0x{base_addr:x}")

        count_bytes = f.read(4)
        if len(count_bytes) != 4:
            raise RuntimeError("Error reading block count")
        count = struct.unpack("<i", count_bytes)[0]
        print(f"Block Count: {count}")

        for block_index in range(count):
            hdr_bytes = f.read(20)
            if len(hdr_bytes) != 20:
                parse_warnings.append(
                    f"truncated block header at index {block_index}: expected 20 bytes, got {len(hdr_bytes)}"
                )
                break

            start_eip, end_eip, inst_count, exec_count = struct.unpack("<IIIQ", hdr_bytes)
            block_info = {
                "start_eip": start_eip,
                "start_eip_hex": f"0x{start_eip:x}",
                "end_eip": end_eip,
                "end_eip_hex": f"0x{end_eip:x}",
                "inst_count": inst_count,
                "exec_count": exec_count,
                "ops": [],
            }

            truncated_block = False
            for op_index in range(inst_count):
                op_bytes = f.read(32)
                if len(op_bytes) != 32:
                    parse_warnings.append(
                        f"truncated op payload in block 0x{start_eip:x} at op {op_index}: "
                        f"expected 32 bytes, got {len(op_bytes)}"
                    )
                    truncated_block = True
                    break

                mem_packed, next_eip, length, modrm, prefixes, meta, imm, _padding, handler_ptr = struct.unpack(
                    "<QIBBBBIIQ", op_bytes
                )

                mem_disp = mem_packed & 0xFFFFFFFF
                ea_desc = (mem_packed >> 32) & 0xFFFFFFFF
                handler_offset = handler_ptr - base_addr if handler_ptr >= base_addr else 0

                block_info["ops"].append({
                    "index": op_index,
                    "next_eip": next_eip,
                    "next_eip_hex": f"0x{next_eip:x}",
                    "handler_ptr": handler_ptr,
                    "handler_ptr_hex": f"0x{handler_ptr:x}",
                    "handler_offset": handler_offset,
                    "handler_offset_hex": f"0x{handler_offset:x}",
                    "symbol": find_symbol(handler_offset, symbols),
                    "imm": imm,
                    "imm_hex": f"0x{imm:x}",
                    "len": length,
                    "len_hex": f"0x{length:x}",
                    "prefixes": prefixes,
                    "prefixes_hex": f"0x{prefixes:x}",
                    "modrm": modrm,
                    "modrm_hex": f"0x{modrm:x}",
                    "meta": meta,
                    "meta_hex": f"0x{meta:x}",
                    "mem": {
                        "disp": mem_disp,
                        "disp_hex": f"0x{mem_disp:x}",
                        **decode_ea_desc(ea_desc),
                    },
                })

            if truncated_block:
                break

            blocks_data.append(block_info)

    return base_addr, count, blocks_data, parse_warnings


def analyze_ngrams(blocks_data, n, top_ngrams):
    ngram_stats = {}

    for block in blocks_data:
        symbols_seq = [op["symbol"] for op in block["ops"]]
        if len(symbols_seq) < n:
            continue
        for i in range(len(symbols_seq) - n + 1):
            ngram = tuple(symbols_seq[i:i + n])
            entry = ngram_stats.setdefault(ngram, {
                "weighted_exec_count": 0,
                "occurrences": 0,
                "unique_block_starts": set(),
                "example_blocks": [],
            })
            entry["weighted_exec_count"] += block["exec_count"]
            entry["occurrences"] += 1
            entry["unique_block_starts"].add(block["start_eip"])
            if len(entry["example_blocks"]) < 5:
                entry["example_blocks"].append({
                    "start_eip": block["start_eip"],
                    "start_eip_hex": block["start_eip_hex"],
                    "exec_count": block["exec_count"],
                    "start_op_index": i,
                })

    sorted_ngrams = sorted(
        ngram_stats.items(),
        key=lambda item: (item[1]["weighted_exec_count"], item[1]["occurrences"]),
        reverse=True,
    )

    top_entries = []
    for ngram, stats in sorted_ngrams[:top_ngrams]:
        top_entries.append({
            "ngram": list(ngram),
            "ngram_display": " -> ".join(ngram),
            "weighted_exec_count": stats["weighted_exec_count"],
            "occurrences": stats["occurrences"],
            "unique_block_count": len(stats["unique_block_starts"]),
            "example_blocks": stats["example_blocks"],
        })

    return {
        "n_gram_size": n,
        "total_unique_ngrams": len(sorted_ngrams),
        "top_ngrams": top_entries,
    }


def build_validation(summary, declared_block_count, parsed_blocks, parse_warnings):
    validation = {
        "warnings": list(parse_warnings),
    }

    if summary is not None:
        exported_count = summary.get("block_stats", {}).get("block_count")
        if exported_count is not None and exported_count != declared_block_count:
            validation["warnings"].append(
                f"summary block_count={exported_count} but dump declared block count={declared_block_count}"
            )
        if exported_count and parsed_blocks == 0:
            validation["warnings"].append(
                "summary reports non-zero block_count but parsed blocks are empty; dump/export format likely drifted"
            )

    return validation


def parse_args():
    parser = argparse.ArgumentParser(description="Analyze x86 emulator basic blocks")
    parser.add_argument("input_path", help="Path to blocks.bin or exported stats directory")
    parser.add_argument("lib_path", help="Path to the library for symbol resolution")
    parser.add_argument(
        "--min-exec-count",
        type=int,
        default=0,
        help="Minimum execution count to include a block (default: 0)",
    )
    parser.add_argument(
        "--min-instr",
        type=int,
        default=0,
        help="Minimum number of instructions to include a block (default: 0)",
    )
    parser.add_argument(
        "--n-gram",
        type=int,
        default=0,
        help="Analyze N-Grams of symbol sequences within blocks. N=0 disables analysis.",
    )
    parser.add_argument(
        "--top-n",
        type=int,
        default=0,
        help="Output only top N blocks sorted by execution weight (default: 0, all blocks)",
    )
    parser.add_argument(
        "--top-ngrams",
        type=int,
        default=100,
        help="Maximum number of n-gram entries to emit (default: 100)",
    )
    parser.add_argument(
        "--output",
        "-o",
        default=None,
        help="Output JSON file path",
    )
    return parser.parse_args()


def main():
    args = parse_args()

    dump_file, summary_file, default_output = resolve_input_paths(args.input_path)
    output_file = args.output or default_output
    summary = load_summary(summary_file)

    print(f"Loading symbols from {args.lib_path}...")
    symbols = load_symbols(args.lib_path)
    print(f"Loaded {len(symbols)} symbols.")

    base_addr, declared_block_count, blocks_data, parse_warnings = parse_blocks(dump_file, symbols)

    if args.min_exec_count > 0:
        before = len(blocks_data)
        blocks_data = [b for b in blocks_data if b["exec_count"] >= args.min_exec_count]
        print(f"Filtered blocks: {before} -> {len(blocks_data)} (min_exec_count >= {args.min_exec_count})")

    if args.min_instr > 0:
        before = len(blocks_data)
        blocks_data = [b for b in blocks_data if b["inst_count"] >= args.min_instr]
        print(f"Filtered blocks: {before} -> {len(blocks_data)} (min_instr >= {args.min_instr})")

    blocks_data.sort(key=lambda x: x["exec_count"] * x["inst_count"], reverse=True)

    if args.top_n > 0:
        blocks_data = blocks_data[:args.top_n]
        print(f"Limited to top {args.top_n} blocks")

    result = {
        "blocks": blocks_data,
        "metadata": {
            "runtime_base": f"0x{base_addr:x}",
            "input_path": args.input_path,
            "dump_file": dump_file,
            "summary_file": summary_file,
            "declared_block_count": declared_block_count,
            "total_blocks": len(blocks_data),
            "min_exec_count_filter": args.min_exec_count,
            "min_instr_filter": args.min_instr,
            "n_gram_size": args.n_gram,
            "top_ngrams_limit": args.top_ngrams,
        },
        "validation": build_validation(summary, declared_block_count, len(blocks_data), parse_warnings),
    }

    if summary is not None:
        result["export_summary"] = summary

    if args.n_gram > 0:
        print(f"Analyzing {args.n_gram}-grams...")
        result["ngrams"] = analyze_ngrams(blocks_data, args.n_gram, args.top_ngrams)
        print(f"Found {result['ngrams']['total_unique_ngrams']} unique {args.n_gram}-grams")

    print(f"Writing analysis to {output_file}...")
    with open(output_file, "w", encoding="utf-8") as f:
        json.dump(result, f, indent=2)
    print("Done.")


if __name__ == "__main__":
    try:
        main()
    except Exception as exc:
        print(f"Error: {exc}", file=sys.stderr)
        raise SystemExit(1)
