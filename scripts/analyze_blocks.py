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


def main():
    parser = argparse.ArgumentParser(description="Analyze x86 emulator basic blocks")
    parser.add_argument("input_path", help="Path to blocks.bin or exported stats directory")
    parser.add_argument("lib_path", help="Path to the library for symbol resolution")
    parser.add_argument("--min-exec-count", type=int, default=0,
                        help="Minimum execution count to include a block (default: 0)")
    parser.add_argument("--min-instr", type=int, default=0,
                        help="Minimum number of instructions to include a block (default: 0)")
    parser.add_argument("--n-gram", type=int, default=0,
                        help="Analyze N-Grams of symbol sequences within blocks. N=0 disables analysis.")
    parser.add_argument("--top-n", type=int, default=0,
                        help="Output only top N blocks sorted by execution weight (default: 0, all blocks)")
    parser.add_argument("--output", "-o", default=None,
                        help="Output JSON file path")
    args = parser.parse_args()

    dump_file, summary_file, default_output = resolve_input_paths(args.input_path)
    output_file = args.output or default_output
    summary = load_summary(summary_file)

    print(f"Loading symbols from {args.lib_path}...")
    symbols = load_symbols(args.lib_path)
    print(f"Loaded {len(symbols)} symbols.")

    blocks_data = []
    with open(dump_file, "rb") as f:
        base_addr_bytes = f.read(8)
        if len(base_addr_bytes) != 8:
            print("Error reading base address")
            sys.exit(1)
        base_addr = struct.unpack("<Q", base_addr_bytes)[0]
        print(f"Runtime Base Address: 0x{base_addr:x}")

        count_bytes = f.read(4)
        if len(count_bytes) != 4:
            print("Error reading block count")
            sys.exit(1)
        count = struct.unpack("<i", count_bytes)[0]
        print(f"Block Count: {count}")

        for _ in range(count):
            hdr_bytes = f.read(20)
            if len(hdr_bytes) != 20:
                break

            start_eip, end_eip, inst_count, exec_count = struct.unpack("<IIIQ", hdr_bytes)
            block_info = {
                "start_eip": start_eip,
                "start_eip_hex": f"0x{start_eip:x}",
                "end_eip": end_eip,
                "end_eip_hex": f"0x{end_eip:x}",
                "inst_count": inst_count,
                "exec_count": exec_count,
                "ops": []
            }

            for _ in range(inst_count):
                op_bytes = f.read(32)
                if len(op_bytes) != 32:
                    break

                mem_packed, next_eip, length, modrm, prefixes, meta, imm, _padding, handler_ptr = struct.unpack(
                    "<QIBBBBIIQ", op_bytes)

                mem_disp = mem_packed & 0xFFFFFFFF
                ea_desc = (mem_packed >> 32) & 0xFFFFFFFF
                handler_offset = handler_ptr - base_addr if handler_ptr >= base_addr else 0

                block_info["ops"].append({
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
                        **decode_ea_desc(ea_desc)
                    }
                })

            blocks_data.append(block_info)

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

    ngrams_data = {}
    if args.n_gram > 0:
        print(f"Analyzing {args.n_gram}-grams...")
        ngram_counts = defaultdict(int)
        for block in blocks_data:
            symbols_seq = [op["symbol"] for op in block["ops"]]
            for i in range(len(symbols_seq) - args.n_gram + 1):
                ngram = tuple(symbols_seq[i:i + args.n_gram])
                ngram_counts[ngram] += block["exec_count"]

        sorted_ngrams = sorted(ngram_counts.items(), key=lambda x: x[1], reverse=True)
        ngrams_data = {
            "n_gram_size": args.n_gram,
            "total_unique_ngrams": len(sorted_ngrams),
            "top_ngrams": [
                {"ngram": " -> ".join(ngram), "weighted_exec_count": count}
                for ngram, count in sorted_ngrams[:100]
            ]
        }
        print(f"Found {len(sorted_ngrams)} unique {args.n_gram}-grams")

    result = {
        "blocks": blocks_data,
        "metadata": {
            "runtime_base": f"0x{base_addr:x}",
            "input_path": args.input_path,
            "dump_file": dump_file,
            "summary_file": summary_file,
            "total_blocks": len(blocks_data),
            "min_exec_count_filter": args.min_exec_count,
            "min_instr_filter": args.min_instr,
            "n_gram_size": args.n_gram
        }
    }

    if summary is not None:
        result["export_summary"] = summary
    if args.n_gram > 0:
        result["ngrams"] = ngrams_data

    print(f"Writing analysis to {output_file}...")
    with open(output_file, "w", encoding="utf-8") as f:
        json.dump(result, f, indent=2)
    print("Done.")


if __name__ == "__main__":
    main()
