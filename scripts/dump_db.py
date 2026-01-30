import sqlite3
import csv
import binascii

DB_PATH = "analyze/instructions.db"
OUTPUT_PATH = "analyze/instructions.md"

def dump():
    conn = sqlite3.connect(DB_PATH)
    cursor = conn.cursor()
    
    cursor.execute("SELECT id, mnemonic, format_str, length, prefix, opcode, has_modrm, modrm_byte, raw_sample, count FROM instructions ORDER BY mnemonic ASC, format_str ASC, count DESC")
    rows = cursor.fetchall()
    
    print(f"[*] Dumping {len(rows)} instructions to {OUTPUT_PATH}...")
    
    PREFIX_MAP = {
        0xF0: "LOCK",
        0xF2: "REPNE",
        0xF3: "REP",
        0x2E: "CS",
        0x36: "SS",
        0x3E: "DS",
        0x26: "ES",
        0x64: "FS",
        0x65: "GS",
        0x66: "OPSIZE",
        0x67: "ADDRSIZE"
    }

    with open(OUTPUT_PATH, 'w') as f:
        # Markdown Header
        f.write("# Instruction Database Dump\n\n")
        f.write("| ID | Mnemonic | Format | Len | Prefix | Opcode | ModRM? | ModRM Byte | Raw Sample | Count |\n")
        f.write("|----|----------|--------|-----|--------|--------|--------|------------|------------|-------|\n")
        
        for row in rows:
            # Unpack
            mid, mnem, fmt, length, pfx_hex, op, has_modrm, modrm, raw, count = row
            
            # Format Prefix
            pfx_str = ""
            if pfx_hex:
                # Convert hex string "662E" -> [0x66, 0x2E]
                bytes_list = binascii.unhexlify(pfx_hex)
                names = []
                for b in bytes_list:
                    names.append(PREFIX_MAP.get(b, f"{b:02X}"))
                pfx_str = "; ".join(names)
            
            # Format fields
            raw_hex = binascii.hexlify(raw).decode('ascii') if raw else ""
            fmt_code = f"`{fmt}`" if fmt else ""
            
            line = f"| {mid} | **{mnem}** | {fmt_code} | {length} | {pfx_str} | `{op}` | {has_modrm} | {modrm} | `{raw_hex}` | {count} |\n"
            f.write(line)
            
    print("[*] Done.")

if __name__ == "__main__":
    dump()
