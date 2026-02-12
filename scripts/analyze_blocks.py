import struct
import sys
import json
import subprocess
import os
import argparse
from collections import defaultdict

def load_symbols(lib_path):
    """
    Load symbols from the shared library using 'nm'.
    Returns a dictionary mapping offset (address) to symbol name.
    """
    symbols = {}
    try:
        # Use nm to list symbols. 
        # -n: sort numerically
        # -P: portable output
        # -C: demangle C++ symbols
        cmd = ["nm", "-n", "-P", "-C", lib_path]
        print(f"Running: {' '.join(cmd)}")
        output = subprocess.check_output(cmd).decode("utf-8")
        
        for line in output.splitlines():
            parts = line.split()
            if len(parts) < 3:
                continue
            # Output format with -P: Name Type Value [Size]
            # Example: fiberish::DecodeBlock(fiberish::EmuState*, unsigned int, unsigned int, unsigned long long) T 100001230
            
            # Since names can contain spaces (due to demangling), we need to handle parsing carefully.
            # But -P usually keeps the name as the first field. If the name has spaces, -P might be tricky?
            # Actually, POSIX nm -P says "Name Type Value". 
            # If name contains spaces, it might break simple split.
            # On macOS nm -P doesn't escape spaces in demangled names well?
            # Let's try without -P if we want robust demangling parsing, or just split by type code?
            # Type code is usually a single letter U, T, t, etc.
            
            # Let's find the type code column. It's usually the one that is a single char [a-zA-Z] and followed by a hex address.
            # But demangled names are complex.
            
            # Alternative: Use c++filt separately? Or rely on the fact that we look for the address at the end?
            # nm -n (numeric sort) output is: Address Type Name
            # Example: 0000000000003e60 T _main
            
            # Let's switch to standard BSD/Linux nm format (no -P) because it puts Address first.
            pass

    except Exception as e:
        print(f"Error loading symbols with -P: {e}")
        return symbols

    # Retry with standard format (Address Type Name) which is easier to parse if Name has spaces
    try:
        cmd = ["nm", "-n", "-C", lib_path] 
        # On some systems -C might not be supported or behavior differs.
        # If it fails, we fall back to non-demangled.
        print(f"Running: {' '.join(cmd)}")
        output = subprocess.check_output(cmd).decode("utf-8")
        
        for line in output.splitlines():
            # Line: "0000000000003e60 T fiberish::DecodeBlock(...)"
            parts = line.strip().split(' ', 2)
            if len(parts) < 3:
                continue
            
            addr_str = parts[0]
            type_code = parts[1]
            name = parts[2]
            
            try:
                addr = int(addr_str, 16)
                if type_code.upper() in ('T', 'W'): 
                     symbols[addr] = name
            except ValueError:
                continue

    except Exception as e:
        print(f"Error loading symbols: {e}")
    
    return symbols

import re

def find_symbol(offset, symbols_map):
    # Precise match
    if offset in symbols_map:
        name = symbols_map[offset]
        
        # Simplify complex template names like DispatchWrapper<&op::Name>
        # or standard names like fiberish::op::Name(...)
        # Look for the last occurrence of 'op::' and extract the identifier following it
        match = re.search(r'op::([a-zA-Z0-9_]+)', name)
        if match:
            return match.group(1)
            
        return name
    return f"func_{offset:x}"

def main():
    parser = argparse.ArgumentParser(description='Analyze x86 emulator basic blocks')
    parser.add_argument('dump_file', help='Binary dump file containing block data')
    parser.add_argument('lib_path', help='Path to the library for symbol resolution')
    parser.add_argument('--min-exec-count', type=int, default=0, 
                        help='Minimum execution count to include a block (default: 0, no filter)')
    parser.add_argument('--min-instr', type=int, default=0,
                        help='Minimum number of instructions to include a block (default: 0, no filter)')
    parser.add_argument('--n-gram', type=int, default=0,
                        help='Analyze N-Grams of symbol sequences within blocks. N=0 disables analysis.')
    parser.add_argument('--top-n', type=int, default=0,
                        help='Output only top N blocks sorted by execution count (default: 0, all blocks)')
    parser.add_argument('--output', '-o', default='blocks_analysis.json',
                        help='Output JSON file path')
    
    args = parser.parse_args()
    
    dump_file = args.dump_file
    lib_path = args.lib_path
    min_exec_count = args.min_exec_count
    min_instr = args.min_instr
    n_gram_size = args.n_gram
    top_n = args.top_n
    output_file = args.output

    print(f"Loading symbols from {lib_path}...")
    symbols = load_symbols(lib_path)
    print(f"Loaded {len(symbols)} symbols.")

    blocks_data = []

    with open(dump_file, "rb") as f:
        # 1. Header: Base Address (8 bytes)
        base_addr_bytes = f.read(8)
        if len(base_addr_bytes) != 8:
            print("Error reading base address")
            sys.exit(1)
        base_addr = struct.unpack("<Q", base_addr_bytes)[0]
        print(f"Runtime Base Address: 0x{base_addr:x}")

        # 2. Block Count (4 bytes)
        count_bytes = f.read(4)
        count = struct.unpack("<i", count_bytes)[0]
        print(f"Block Count: {count}")

        for i in range(count):
            # Block Header: Start(4), End(4), InstCount(4), ExecCount(8)
            hdr_bytes = f.read(20)
            if len(hdr_bytes) != 20:
                break
            
            start_eip, end_eip, inst_count, exec_count = struct.unpack("<IIIQ", hdr_bytes)
            
            block_info = {
                "start_eip": f"0x{start_eip:x}",
                "end_eip": f"0x{end_eip:x}",
                "inst_count": inst_count,
                "exec_count": exec_count,
                "ops": []
            }

            for j in range(inst_count):
                # DecodedOp (32 bytes)
                op_bytes = f.read(32)
                if len(op_bytes) != 32:
                    break
                
                # Layout:
                # 0-7: mem_packed (Q)
                # 8-11: next_eip (I)
                # 12: len (B)
                # 13: modrm (B)
                # 14: prefixes (B)
                # 15: meta (B)
                # 16-19: imm (I)
                # 20-23: padding (I)
                # 24-31: handler (Q - ptr)
                
                (mem_packed, next_eip, length, modrm, prefixes, meta, imm, padding, handler_ptr) = struct.unpack("<QIBBBBIIQ", op_bytes)

                # Decode mem_packed
                # struct {
                #     uint32_t disp;         // 0-3
                #     uint8_t base_offset;   // 4
                #     uint8_t index_offset;  // 5
                #     uint8_t scale;         // 6
                #     uint8_t mem_flags;     // 7
                # } mem;
                
                mem_disp = mem_packed & 0xFFFFFFFF
                mem_base = (mem_packed >> 32) & 0xFF
                mem_index = (mem_packed >> 40) & 0xFF
                mem_scale = (mem_packed >> 48) & 0xFF
                mem_flags = (mem_packed >> 56) & 0xFF

                # Calculate Offset
                handler_offset = handler_ptr - base_addr
                if handler_offset < 0:
                    # Should not happen unless base_addr is wrong or ptr is bad
                    handler_offset = 0

                # Symbol resolution
                symbol_name = find_symbol(handler_offset, symbols)

                op_info = {
                    "next_eip": f"0x{next_eip:x}",
                    # "handler_ptr": f"0x{handler_ptr:x}",
                    # "handler_offset": f"0x{handler_offset:x}",
                    "symbol": symbol_name,
                    "imm": f"0x{imm:x}",
                    "len": f"0x{length:x}",
                    "prefixes": f"0x{prefixes:x}",
                    "modrm": f"0x{modrm:x}",
                    "meta": f"0x{meta:x}",
                    "mem": {
                        "disp": f"0x{mem_disp:x}",
                        "base": f"0x{mem_base:x}",
                        "index": f"0x{mem_index:x}",
                        "scale": f"0x{mem_scale:x}",
                        "flags": f"0x{mem_flags:x}"
                    }
                }
                block_info["ops"].append(op_info)

            blocks_data.append(block_info)

    # Filter by execution count if threshold is set
    if min_exec_count > 0:
        original_count = len(blocks_data)
        blocks_data = [b for b in blocks_data if b['exec_count'] >= min_exec_count]
        print(f"Filtered blocks: {original_count} -> {len(blocks_data)} (min_exec_count >= {min_exec_count})")
    
    # Filter by minimum instructions
    if min_instr > 0:
        original_count = len(blocks_data)
        blocks_data = [b for b in blocks_data if b['inst_count'] >= min_instr]
        print(f"Filtered blocks: {original_count} -> {len(blocks_data)} (min_instr >= {min_instr})")
    
    # Sort blocks by execution count * instruction count (descending)
    blocks_data.sort(key=lambda x: x['exec_count'] * x['inst_count'], reverse=True)
    
    # Apply top-N limit
    if top_n > 0:
        blocks_data = blocks_data[:top_n]
        print(f"Limited to top {top_n} blocks")

    # N-Grams analysis
    ngrams_data = {}
    if n_gram_size > 0:
        print(f"Analyzing {n_gram_size}-grams...")
        ngram_counts = defaultdict(int)
        
        for block in blocks_data:
            # Extract symbol sequence for this block
            symbols_seq = [op['symbol'] for op in block['ops']]
            
            # Generate N-Grams
            for i in range(len(symbols_seq) - n_gram_size + 1):
                ngram = tuple(symbols_seq[i:i + n_gram_size])
                # Weight by execution count
                ngram_counts[ngram] += block['exec_count']
        
        # Sort by frequency (weighted execution count)
        sorted_ngrams = sorted(ngram_counts.items(), key=lambda x: x[1], reverse=True)
        
        # Store top N-Grams
        ngrams_data = {
            'n_gram_size': n_gram_size,
            'total_unique_ngrams': len(sorted_ngrams),
            'top_ngrams': [
                {'ngram': ' -> '.join(ngram), 'weighted_exec_count': count}
                for ngram, count in sorted_ngrams[:100]  # Top 100
            ]
        }
        
        print(f"Found {len(sorted_ngrams)} unique {n_gram_size}-grams")

    print(f"Writing analysis to {output_file}...")
    
    result = {
        'blocks': blocks_data,
        'metadata': {
            'total_blocks': len(blocks_data),
            'min_exec_count_filter': min_exec_count,
            'min_instr_filter': min_instr,
            'n_gram_size': n_gram_size
        }
    }
    
    if n_gram_size > 0:
        result['ngrams'] = ngrams_data
    
    with open(output_file, "w") as f:
        json.dump(result, f, indent=2)
    print("Done.")

if __name__ == "__main__":
    main()