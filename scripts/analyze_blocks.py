import struct
import sys
import json
import subprocess
import os

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
    if len(sys.argv) < 3:
        print("Usage: python analyze_blocks.py <dump_file> <libfibercpu_path> [output_json]")
        sys.exit(1)

    dump_file = sys.argv[1]
    lib_path = sys.argv[2]
    output_file = sys.argv[3] if len(sys.argv) > 3 else "blocks_analysis.json"

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
                # 8-11: imm (I)
                # 12-15: next_eip (I)
                # 16-23: handler (Q - ptr)
                # 24-27: branch_target (I)
                # 28: prefixes (B)
                # 29: modrm (B)
                # 30: meta (B)
                # 31: len (B)
                
                (mem_packed, imm, next_eip, handler_ptr, branch_target, 
                 prefixes, modrm, meta, length) = struct.unpack("<QIIQIBBBB", op_bytes)

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
                    "branch_target": f"0x{branch_target:x}",
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

    print(f"Writing analysis to {output_file}...")
    with open(output_file, "w") as f:
        json.dump(blocks_data, f, indent=2)
    print("Done.")

if __name__ == "__main__":
    main()