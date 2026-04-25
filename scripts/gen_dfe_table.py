import os

# Flag Masks
CF_MASK = 1 << 0
PF_MASK = 1 << 2
AF_MASK = 1 << 4
ZF_MASK = 1 << 6
SF_MASK = 1 << 7
OF_MASK = 1 << 11
ALL_FLAGS = CF_MASK | PF_MASK | AF_MASK | ZF_MASK | SF_MASK | OF_MASK

# Types
DFE_TYPE_SIMPLE = 0
DFE_TYPE_GROUP1 = 1  # 80-83 (ADD, OR, ADC, SBB, AND, SUB, XOR, CMP)
DFE_TYPE_GROUP2 = 2  # C0/C1/D0-D3 (Shift/Rot) -> Writes All
DFE_TYPE_GROUP3 = 3  # F6/F7 (TEST/NOT/NEG/MUL...)
DFE_TYPE_GROUP4 = 4  # FE/FF (INC/DEC)
DFE_TYPE_UNKNOWN = 255

class OpInfo:
    def __init__(self, type=DFE_TYPE_SIMPLE, read=0, write=0):
        self.type = type
        self.read = read
        self.write = write

# Initialize table with UNKNOWN (0xFF) or defaults?
# Default to Simple, Read=0, Write=0 (Safe default: Assume NO flags read/written? NO.
# Safe assumption for optimization is: Writes NONE, Reads ALL (So we don't optimize it away).
# Actually, if we don't know, assuming "Reads ALL, Writes NONE" prevents DFE from killing it (good),
# and prevents DFE from marking previous writers as dead (good).
# So default: Type=Simple, Read=ALL, Write=0.
table = []
for _ in range(512):
    table.append(OpInfo(DFE_TYPE_SIMPLE, ALL_FLAGS, 0))

def set_range(start, end, info):
    for i in range(start, end + 1):
        table[i] = info

def set_op(idx, info):
    table[idx] = info

# --- Standard Map (0x00 - 0xFF) ---

# ALU (ADD, OR, ADC, SBB, AND, SUB, XOR, CMP)
# Pattern: 0x00-0x3F. 8 blocks of 8.
# 00-05: ADD (Write All)
# 08-0D: OR (Write All)
# 10-15: ADC (Read CF, Write All)
# 18-1D: SBB (Read CF, Write All)
# 20-25: AND (Write All)
# 28-2D: SUB (Write All)
# 30-35: XOR (Write All)
# 38-3D: CMP (Write All, Read None? No, CMP is Write All).

for base in range(0, 0x40, 8):
    op_base = base // 8
    # 0=ADD, 1=OR, 2=ADC, 3=SBB, 4=AND, 5=SUB, 6=XOR, 7=CMP
    read_mask = 0
    write_mask = ALL_FLAGS
    
    if op_base == 2 or op_base == 3: # ADC, SBB
        read_mask = CF_MASK
    
    # Set for base + 0..5 (0,1,2,3,4,5)
    for i in range(6):
        set_op(base + i, OpInfo(DFE_TYPE_SIMPLE, read_mask, write_mask))

# INC/DEC (0x40 - 0x4F)
# Writes All EXCEPT CF.
for i in range(0x40, 0x50):
    set_op(i, OpInfo(DFE_TYPE_SIMPLE, 0, ALL_FLAGS & ~CF_MASK))

# PUSH/POP (0x50 - 0x5F, 0x68, 0x6A, 0x58-0x5F) - No flags
set_range(0x50, 0x5F, OpInfo(DFE_TYPE_SIMPLE, 0, 0))
set_op(0x68, OpInfo(DFE_TYPE_SIMPLE, 0, 0))
set_op(0x6A, OpInfo(DFE_TYPE_SIMPLE, 0, 0))

# Jcc (0x70 - 0x7F)
# Reads flags.
set_range(0x70, 0x7F, OpInfo(DFE_TYPE_SIMPLE, ALL_FLAGS, 0)) # Read all conservatively

# Group 1 (0x80 - 0x83)
set_range(0x80, 0x83, OpInfo(DFE_TYPE_GROUP1, 0, 0))

# TEST (0x84, 0x85) - Write All, Read None? TEST is AND but discard result.
set_op(0x84, OpInfo(DFE_TYPE_SIMPLE, 0, ALL_FLAGS))
set_op(0x85, OpInfo(DFE_TYPE_SIMPLE, 0, ALL_FLAGS))

# XCHG (0x86, 0x87) - No flags
set_op(0x86, OpInfo(DFE_TYPE_SIMPLE, 0, 0))
set_op(0x87, OpInfo(DFE_TYPE_SIMPLE, 0, 0))

# MOV (0x88-0x8C, 0x8E, 0xA0-0xA3, 0xB0-0xBF, 0xC6, 0xC7) - No flags
set_range(0x88, 0x8E, OpInfo(DFE_TYPE_SIMPLE, 0, 0))
set_range(0xA0, 0xA3, OpInfo(DFE_TYPE_SIMPLE, 0, 0))
set_range(0xB0, 0xBF, OpInfo(DFE_TYPE_SIMPLE, 0, 0))
set_op(0xC6, OpInfo(DFE_TYPE_SIMPLE, 0, 0))
set_op(0xC7, OpInfo(DFE_TYPE_SIMPLE, 0, 0))

# XCHG EAX (0x90 - 0x97)
set_range(0x90, 0x97, OpInfo(DFE_TYPE_SIMPLE, 0, 0))

# SAHF (0x9E) - Write SF,ZF,AF,PF,CF
set_op(0x9E, OpInfo(DFE_TYPE_SIMPLE, 0, ALL_FLAGS)) 

# LAHF (0x9F) - Read SF,ZF,AF,PF,CF
set_op(0x9F, OpInfo(DFE_TYPE_SIMPLE, ALL_FLAGS, 0))

# Shifts (C0, C1, D0-D3)
set_op(0xC0, OpInfo(DFE_TYPE_GROUP2, 0, 0))
set_op(0xC1, OpInfo(DFE_TYPE_GROUP2, 0, 0))
set_range(0xD0, 0xD3, OpInfo(DFE_TYPE_GROUP2, 0, 0))

# Group 3 (F6, F7)
set_op(0xF6, OpInfo(DFE_TYPE_GROUP3, 0, 0))
set_op(0xF7, OpInfo(DFE_TYPE_GROUP3, 0, 0))

# Group 4/5 (FE, FF) - INC/DEC are in here
set_op(0xFE, OpInfo(DFE_TYPE_GROUP4, 0, 0))
set_op(0xFF, OpInfo(DFE_TYPE_GROUP4, 0, 0))

# --- Extended Map (0x0F ...) represented as 256 + opcode ---

# Jcc Long (0x0F 80 - 0x0F 8F)
set_range(256 + 0x80, 256 + 0x8F, OpInfo(DFE_TYPE_SIMPLE, ALL_FLAGS, 0))

# SETcc (0x0F 90 - 0x0F 9F) - Reads flags, writes dest (not flags)
set_range(256 + 0x90, 256 + 0x9F, OpInfo(DFE_TYPE_SIMPLE, ALL_FLAGS, 0))

# CMOVcc (0x0F 40 - 0x0F 4F) - Reads flags
set_range(256 + 0x40, 256 + 0x4F, OpInfo(DFE_TYPE_SIMPLE, ALL_FLAGS, 0))

# IMUL (0x0F AF) - Write All
set_op(256 + 0xAF, OpInfo(DFE_TYPE_SIMPLE, 0, ALL_FLAGS))

# Generate Header
header_path = os.path.join("libfibercpu", "dfe_lut.h")
with open(header_path, "w") as f:
    f.write("#pragma once\n")
    f.write("#include <cstdint>\n\n")
    
    f.write("namespace x86emu {\n\n")
    
    f.write("// DFE Types\n")
    f.write(f"constexpr uint8_t DFE_TYPE_SIMPLE = {DFE_TYPE_SIMPLE};\n")
    f.write(f"constexpr uint8_t DFE_TYPE_GROUP1 = {DFE_TYPE_GROUP1};\n")
    f.write(f"constexpr uint8_t DFE_TYPE_GROUP2 = {DFE_TYPE_GROUP2};\n")
    f.write(f"constexpr uint8_t DFE_TYPE_GROUP3 = {DFE_TYPE_GROUP3};\n")
    f.write(f"constexpr uint8_t DFE_TYPE_GROUP4 = {DFE_TYPE_GROUP4};\n")
    f.write(f"constexpr uint8_t DFE_TYPE_UNKNOWN = {DFE_TYPE_UNKNOWN};\n\n")
    
    f.write("// Flag Masks (for convenience)\n")
    f.write(f"constexpr uint32_t DFE_CF_MASK = {CF_MASK};\n")
    f.write(f"constexpr uint32_t DFE_ALL_FLAGS = {ALL_FLAGS};\n\n")
    
    f.write("struct DfeOpInfo {\n")
    f.write("    uint8_t type;\n")
    f.write("    uint32_t read_mask;\n")
    f.write("    uint32_t write_mask;\n")
    f.write("};\n\n")
    
    f.write("constexpr DfeOpInfo kOpFlagTable[512] = {\n")
    
    for idx, info in enumerate(table):
        f.write(f"    {{ {info.type}, 0x{info.read:X}, 0x{info.write:X} }}, // 0x{idx:X}\n")
        
    f.write("};\n\n")
    f.write("} // namespace x86emu\n")

print(f"Generated {header_path}")
