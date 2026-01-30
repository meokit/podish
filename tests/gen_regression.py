#!/usr/bin/env python3
# /// script
# dependencies = [
#   "capstone",
# ]
# ///

import sqlite3
import os
import binascii

# Get script directory to make paths relative to project root
SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
PROJECT_ROOT = os.path.dirname(SCRIPT_DIR)

DB_PATH = os.path.join(PROJECT_ROOT, "analyze/instructions.db")
OUTPUT_DIR = os.path.join(SCRIPT_DIR, "regression")

def generate():
    if not os.path.exists(OUTPUT_DIR):
        os.makedirs(OUTPUT_DIR)
        
    conn = sqlite3.connect(DB_PATH)
    cursor = conn.cursor()
    
    # Select our instructions where raw_sample is available
    cursor.execute("SELECT id, mnemonic, format_str, raw_sample FROM instructions WHERE raw_sample IS NOT NULL")
    rows = cursor.fetchall()
    
    print(f"[*] Total raw instructions with samples: {len(rows)}")

    # Deduplicate based on (mnemonic, format_str)
    unique_map = {}
    for row in rows:
        mid, mnemonic, fmt, raw = row
        # Normalize format string (handle None)
        fmt_key = fmt if fmt else ""
        key = (mnemonic, fmt_key)
        
        # We process all rows. If we encounter a duplicate key, we stick with the first one we found.
        # This keeps the deduplication stable.
        if key not in unique_map:
            unique_map[key] = row

    # Sort by Mnemonic, then Format String
    sorted_instructions = sorted(unique_map.values(), key=lambda x: (x[1], x[2] if x[2] else ""))
    print(f"[*] Unique instructions after deduplication: {len(sorted_instructions)}")

    # Generate analyze/instructions.md
    md_path = os.path.join(PROJECT_ROOT, "analyze/instructions.md")
    # Ensure analyze dir exists
    if not os.path.exists(os.path.dirname(md_path)):
        os.makedirs(os.path.dirname(md_path))
        
    with open(md_path, "w") as f:
        f.write(f"# Instruction List\n\n")
        f.write(f"Total Unique Instructions: {len(sorted_instructions)}\n\n")
        f.write("Generated automatically by tests/gen_regression.py\n\n")
        f.write("| ID | Mnemonic | Format | Disassembly |\n")
        f.write("| --- | --- | --- | --- |\n")
        
        # Initialize Capstone for the MD file too if we want nice disassembly there,
        # but the request just said "unique sorted list". I'll add disassembly column for clarity.
        try:
            from capstone import Cs, CS_ARCH_X86, CS_MODE_32
            md = Cs(CS_ARCH_X86, CS_MODE_32)
        except ImportError:
            md = None
            print("[!] Capstone not found, disassembly will be skipped in MD.")

        for mid, mnemonic, fmt, raw in sorted_instructions:
            disasm = f"{mnemonic} {fmt if fmt else ''}"
            if md and raw:
                try:
                    insns = list(md.disasm(bytes(raw), 0x1000))
                    if insns:
                        disasm = f"{insns[0].mnemonic} {insns[0].op_str}"
                except:
                    pass
            f.write(f"| {mid} | {mnemonic} | {fmt if fmt else ''} | `{disasm}` |\n")
    
    print(f"[*] Generated instruction list at {md_path}")

    # Generate Regression Tests
    # Chunk into 50
    CHUNK_SIZE = 50
    chunks = [sorted_instructions[i:i + CHUNK_SIZE] for i in range(0, len(sorted_instructions), CHUNK_SIZE)]
    
    # Re-initialize Capstone if needed (though already done above)
    if not 'md' in locals() or md is None:
         try:
            from capstone import Cs, CS_ARCH_X86, CS_MODE_32
            md = Cs(CS_ARCH_X86, CS_MODE_32)
         except ImportError:
            pass

    for i, chunk in enumerate(chunks):
        filename = os.path.join(OUTPUT_DIR, f"test_redis_{i:03d}.py")
        with open(filename, "w") as f:
            f.write(f"# Redis Regression Test Batch {i:03d}\n")
            f.write("# Generated automatically. PLEASE EDIT THIS FILE MANUALLY TO FIX TESTS.\n")
            f.write("from tests.runner import Runner\n")
            f.write("import binascii\n")
            f.write("import pytest\n\n")
            
            
            for (mid, mnemonic, fmt, raw) in chunk:
                # Disassemble
                raw_bytes = bytes(raw)
                hex_str = binascii.hexlify(raw_bytes).decode('ascii')
                
                # Get disassembly
                disasm = f"{mnemonic} {fmt or ''}" # Default fallback
                try:
                    if md:
                        insns = list(md.disasm(raw_bytes, 0x1000))
                        if insns:
                            # Use the first instruction
                            disasm = f"{insns[0].mnemonic} {insns[0].op_str}"
                except:
                    pass
                
                # Sanitize description for function name
                # Convert to valid Python identifier
                func_suffix = f"{mnemonic}_{fmt or 'no_operands'}"
                func_suffix = func_suffix.replace(" ", "_").replace(",", "").replace("[", "").replace("]", "")
                func_suffix = func_suffix.replace(":", "_").replace("+", "plus").replace("-", "minus")
                func_suffix = func_suffix.lower()
                
                func_name = f"test_id_{mid}_{func_suffix}"
                desc = f"ID_{mid}: {disasm}"
                
                f.write(f"@pytest.mark.regression\n")
                f.write(f"def {func_name}():\n")
                f.write(f"    \"\"\"Test: {desc}\"\"\"\n")
                f.write(f"    runner = Runner()\n")
                f.write(f"    # Raw: {hex_str}\n")
                f.write(f"    assert runner.run_test_bytes(\n")
                f.write(f"        name='{desc}',\n")
                f.write(f"        code=binascii.unhexlify('{hex_str}'),\n")
                f.write(f"        initial_regs={{}},\n")
                f.write(f"        expected_regs={{}}\n")
                f.write(f"    )\n\n")
            


            
    print(f"[*] Generated {len(chunks)} test files in {OUTPUT_DIR}")

if __name__ == "__main__":
    generate()
