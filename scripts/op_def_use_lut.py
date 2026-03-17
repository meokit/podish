from __future__ import annotations

from dataclasses import dataclass


GPR_NAMES = ("eax", "ecx", "edx", "ebx", "esp", "ebp", "esi", "edi")

FLAG_BITS = {
    "cf": 1 << 0,
    "pf": 1 << 2,
    "af": 1 << 4,
    "zf": 1 << 6,
    "sf": 1 << 7,
    "of": 1 << 11,
}

ALL_STATUS_FLAGS_MASK = (
    FLAG_BITS["cf"] | FLAG_BITS["pf"] | FLAG_BITS["af"] | FLAG_BITS["zf"] | FLAG_BITS["sf"] | FLAG_BITS["of"]
)


@dataclass(slots=True)
class DefUseState:
    reads_gpr_mask: int = 0
    reads_addr_gpr_mask: int = 0
    writes_gpr_mask: int = 0
    reads_flags_mask: int = 0
    writes_flags_mask: int = 0
    reads_memory: bool = False
    writes_memory: bool = False
    notes: list[str] | None = None

    def note(self, message: str) -> None:
        if self.notes is None:
            self.notes = []
        self.notes.append(message)

    def to_dict(self) -> dict[str, object]:
        combined_reads_gpr_mask = self.reads_gpr_mask | self.reads_addr_gpr_mask
        return {
            "reads_gpr_mask": combined_reads_gpr_mask,
            "reads_gpr": mask_to_reg_names(combined_reads_gpr_mask),
            "reads_data_gpr_mask": self.reads_gpr_mask,
            "reads_data_gpr": mask_to_reg_names(self.reads_gpr_mask),
            "reads_addr_gpr_mask": self.reads_addr_gpr_mask,
            "reads_addr_gpr": mask_to_reg_names(self.reads_addr_gpr_mask),
            "writes_gpr_mask": self.writes_gpr_mask,
            "writes_gpr": mask_to_reg_names(self.writes_gpr_mask),
            "reads_flags_mask": self.reads_flags_mask,
            "writes_flags_mask": self.writes_flags_mask,
            "reads_flags": mask_to_flag_names(self.reads_flags_mask),
            "writes_flags": mask_to_flag_names(self.writes_flags_mask),
            "reads_memory": self.reads_memory,
            "writes_memory": self.writes_memory,
            "notes": self.notes or [],
        }


def reg_mask(*regs: int) -> int:
    mask = 0
    for reg in regs:
        if 0 <= reg < len(GPR_NAMES):
            mask |= 1 << reg
    return mask


def mask_to_reg_names(mask: int) -> list[str]:
    return [name for idx, name in enumerate(GPR_NAMES) if mask & (1 << idx)]


def mask_to_flag_names(mask: int) -> list[str]:
    return [name for name, bit in FLAG_BITS.items() if mask & bit]


def decode_memory_address_regs(ea_desc: int) -> int:
    mask = 0
    base_offset = ea_desc & 0x3F
    index_offset = (ea_desc >> 6) & 0x3F

    if base_offset != 32:
        mask |= 1 << (base_offset // 4)
    if index_offset != 32:
        mask |= 1 << (index_offset // 4)
    return mask


def apply_rm_operand(state: DefUseState, modrm: int, has_mem: bool, ea_desc: int, role: str) -> None:
    if role == "none":
        return

    if has_mem:
        state.reads_addr_gpr_mask |= decode_memory_address_regs(ea_desc)
        if role in ("read", "readwrite"):
            state.reads_memory = True
        if role in ("write", "readwrite"):
            state.writes_memory = True
        return

    rm = modrm & 7
    if role in ("read", "readwrite"):
        state.reads_gpr_mask |= reg_mask(rm)
    if role in ("write", "readwrite"):
        state.writes_gpr_mask |= reg_mask(rm)


def apply_reg_operand(state: DefUseState, modrm: int, role: str) -> None:
    if role == "none":
        return
    reg = (modrm >> 3) & 7
    if role in ("read", "readwrite"):
        state.reads_gpr_mask |= reg_mask(reg)
    if role in ("write", "readwrite"):
        state.writes_gpr_mask |= reg_mask(reg)


def analyze_def_use(op_id: int | None, modrm: int, meta: int, prefixes: int, ea_desc: int) -> dict[str, object] | None:
    if op_id is None or op_id < 0:
        return None

    has_modrm = (meta & 0x01) != 0
    has_mem = (meta & 0x02) != 0
    state = DefUseState()

    # Jcc rel8 / rel32 / loop-family.
    if 0x70 <= op_id <= 0x7F or 0x180 <= op_id <= 0x18F or 0xE0 <= op_id <= 0xE3:
        state.reads_flags_mask |= ALL_STATUS_FLAGS_MASK
        if 0xE0 <= op_id <= 0xE2:
            state.reads_gpr_mask |= reg_mask(1)  # ECX/loop counter
            state.writes_gpr_mask |= reg_mask(1)
            state.note("loop family decrements ECX")
        elif op_id == 0xE3:
            state.reads_gpr_mask |= reg_mask(1)
            state.note("jecxz reads ECX")
        state.note("conditional control flow consumes flags")
        return state.to_dict()

    # Unconditional jumps have no interesting data dependency for current SuperOpcode mining.
    if op_id in {0xE9, 0xEB}:
        state.note("unconditional jump")
        return state.to_dict()

    # CMOVcc
    if 0x140 <= op_id <= 0x14F and has_modrm:
        state.reads_flags_mask |= ALL_STATUS_FLAGS_MASK
        apply_reg_operand(state, modrm, "readwrite")
        apply_rm_operand(state, modrm, has_mem, ea_desc, "read")
        state.note("cmov reads destination because the old value is kept on false predicate")
        return state.to_dict()

    # SETcc
    if 0x190 <= op_id <= 0x19F and has_modrm:
        state.reads_flags_mask |= ALL_STATUS_FLAGS_MASK
        apply_rm_operand(state, modrm, has_mem, ea_desc, "write")
        return state.to_dict()

    # CMP / TEST explicit two-operand forms
    if op_id in {0x38, 0x39, 0x3A, 0x3B, 0x84, 0x85} and has_modrm:
        apply_reg_operand(state, modrm, "read")
        apply_rm_operand(state, modrm, has_mem, ea_desc, "read")
        state.writes_flags_mask |= ALL_STATUS_FLAGS_MASK
        return state.to_dict()

    # MOV / XCHG explicit forms, including decoded specialized MOV op ids.
    if op_id in {0x89, 0x200, 0x201} and has_modrm:
        apply_reg_operand(state, modrm, "read")
        apply_rm_operand(state, modrm, has_mem, ea_desc, "write")
        return state.to_dict()
    if op_id in {0x8B, 0x202, 0x203} and has_modrm:
        apply_reg_operand(state, modrm, "write")
        apply_rm_operand(state, modrm, has_mem, ea_desc, "read")
        return state.to_dict()
    if op_id == 0x88 and has_modrm:
        apply_reg_operand(state, modrm, "read")
        apply_rm_operand(state, modrm, has_mem, ea_desc, "write")
        return state.to_dict()
    if op_id == 0x8A and has_modrm:
        apply_reg_operand(state, modrm, "write")
        apply_rm_operand(state, modrm, has_mem, ea_desc, "read")
        return state.to_dict()
    if op_id in {0xC6, 0xC7} and has_modrm:
        apply_rm_operand(state, modrm, has_mem, ea_desc, "write")
        return state.to_dict()
    if op_id == 0x87 and has_modrm:
        apply_reg_operand(state, modrm, "readwrite")
        apply_rm_operand(state, modrm, has_mem, ea_desc, "readwrite")
        return state.to_dict()

    # XCHG EAX, r32 short form.
    if 0x91 <= op_id <= 0x97:
        reg = op_id - 0x90
        state.reads_gpr_mask |= reg_mask(0, reg)
        state.writes_gpr_mask |= reg_mask(0, reg)
        return state.to_dict()

    # LEA reads only address regs and defines destination.
    if op_id == 0x8D and has_modrm:
        apply_reg_operand(state, modrm, "write")
        state.reads_addr_gpr_mask |= decode_memory_address_regs(ea_desc)
        state.note("lea reads effective-address registers without touching memory")
        return state.to_dict()

    # MOVZX / MOVSX
    if op_id in {0x1B6, 0x1B7, 0x1BE, 0x1BF} and has_modrm:
        apply_reg_operand(state, modrm, "write")
        apply_rm_operand(state, modrm, has_mem, ea_desc, "read")
        return state.to_dict()

    # Common binary ALU families.
    if op_id in {
        0x00, 0x01, 0x08, 0x09, 0x10, 0x11, 0x18, 0x19, 0x20, 0x21, 0x28, 0x29, 0x30, 0x31
    } and has_modrm:
        apply_reg_operand(state, modrm, "read")
        apply_rm_operand(state, modrm, has_mem, ea_desc, "readwrite")
        state.writes_flags_mask |= ALL_STATUS_FLAGS_MASK
        if op_id in {0x10, 0x11, 0x18, 0x19}:  # ADC/SBB
            state.reads_flags_mask |= FLAG_BITS["cf"]
        return state.to_dict()
    if op_id in {
        0x02, 0x03, 0x0A, 0x0B, 0x12, 0x13, 0x1A, 0x1B, 0x22, 0x23, 0x2A, 0x2B, 0x32, 0x33
    } and has_modrm:
        apply_reg_operand(state, modrm, "readwrite")
        apply_rm_operand(state, modrm, has_mem, ea_desc, "read")
        state.writes_flags_mask |= ALL_STATUS_FLAGS_MASK
        if op_id in {0x12, 0x13, 0x1A, 0x1B}:  # ADC/SBB
            state.reads_flags_mask |= FLAG_BITS["cf"]
        return state.to_dict()

    # Group1 imm: ADD/OR/ADC/SBB/AND/SUB/XOR/CMP
    if op_id in {0x80, 0x81, 0x83} and has_modrm:
        subop = (modrm >> 3) & 7
        if subop == 7:  # CMP
            apply_rm_operand(state, modrm, has_mem, ea_desc, "read")
        else:
            apply_rm_operand(state, modrm, has_mem, ea_desc, "readwrite")
            if subop in {2, 3}:  # ADC/SBB
                state.reads_flags_mask |= FLAG_BITS["cf"]
        state.writes_flags_mask |= ALL_STATUS_FLAGS_MASK
        return state.to_dict()

    # Group2 shifts/rotates by imm8 / 1 / CL.
    if op_id in {0xC0, 0xC1, 0xD0, 0xD1, 0xD2, 0xD3} and has_modrm:
        apply_rm_operand(state, modrm, has_mem, ea_desc, "readwrite")
        if op_id in {0xD2, 0xD3}:
            state.reads_gpr_mask |= reg_mask(1)  # CL
            state.note("shift count comes from CL")
        state.writes_flags_mask |= ALL_STATUS_FLAGS_MASK
        return state.to_dict()

    # Group3 byte/word-dword forms.
    if op_id in {0xF6, 0xF7} and has_modrm:
        subop = (modrm >> 3) & 7
        if subop in {0, 1}:  # TEST
            apply_rm_operand(state, modrm, has_mem, ea_desc, "read")
            state.writes_flags_mask |= ALL_STATUS_FLAGS_MASK
        elif subop == 2:  # NOT
            apply_rm_operand(state, modrm, has_mem, ea_desc, "readwrite")
        elif subop == 3:  # NEG
            apply_rm_operand(state, modrm, has_mem, ea_desc, "readwrite")
            state.writes_flags_mask |= ALL_STATUS_FLAGS_MASK
        elif subop in {4, 5}:  # MUL/IMUL
            apply_rm_operand(state, modrm, has_mem, ea_desc, "read")
            state.reads_gpr_mask |= reg_mask(0)
            state.writes_gpr_mask |= reg_mask(0, 2)
            state.writes_flags_mask |= ALL_STATUS_FLAGS_MASK
        elif subop in {6, 7}:  # DIV/IDIV
            apply_rm_operand(state, modrm, has_mem, ea_desc, "read")
            state.reads_gpr_mask |= reg_mask(0, 2)
            state.writes_gpr_mask |= reg_mask(0, 2)
        return state.to_dict()

    # BT family
    if op_id in {0x1A3, 0x1AB, 0x1B3, 0x1BB} and has_modrm:
        apply_reg_operand(state, modrm, "read")
        rm_role = "readwrite" if op_id in {0x1AB, 0x1B3, 0x1BB} else "read"
        apply_rm_operand(state, modrm, has_mem, ea_desc, rm_role)
        state.writes_flags_mask |= FLAG_BITS["cf"]
        return state.to_dict()

    # IMUL r32, r/m32
    if op_id == 0x1AF and has_modrm:
        apply_reg_operand(state, modrm, "readwrite")
        apply_rm_operand(state, modrm, has_mem, ea_desc, "read")
        state.writes_flags_mask |= FLAG_BITS["cf"] | FLAG_BITS["of"]
        return state.to_dict()

    # PUSH/POP/RET/CALL are useful for ESP dependencies.
    if 0x50 <= op_id <= 0x57:  # PUSH reg
        state.reads_gpr_mask |= reg_mask(op_id - 0x50, 4)
        state.writes_gpr_mask |= reg_mask(4)
        state.writes_memory = True
        return state.to_dict()
    if 0x58 <= op_id <= 0x5F:  # POP reg
        state.reads_gpr_mask |= reg_mask(4)
        state.writes_gpr_mask |= reg_mask(op_id - 0x58, 4)
        state.reads_memory = True
        return state.to_dict()
    if op_id in {0xE8, 0xC2, 0xC3}:
        state.reads_gpr_mask |= reg_mask(4)
        state.writes_gpr_mask |= reg_mask(4)
        state.reads_memory = op_id in {0xC2, 0xC3}
        state.writes_memory = op_id == 0xE8
        return state.to_dict()
    if op_id == 0x6A:  # PUSH imm8
        state.reads_gpr_mask |= reg_mask(4)
        state.writes_gpr_mask |= reg_mask(4)
        state.writes_memory = True
        return state.to_dict()

    # Group5 /r: INC, DEC, CALL, JMP, PUSH
    if op_id == 0xFF and has_modrm:
        subop = (modrm >> 3) & 7
        if subop == 0:  # INC
            apply_rm_operand(state, modrm, has_mem, ea_desc, "readwrite")
            state.writes_flags_mask |= ALL_STATUS_FLAGS_MASK & ~FLAG_BITS["cf"]
        elif subop == 1:  # DEC
            apply_rm_operand(state, modrm, has_mem, ea_desc, "readwrite")
            state.writes_flags_mask |= ALL_STATUS_FLAGS_MASK & ~FLAG_BITS["cf"]
        elif subop == 2:  # CALL r/m
            apply_rm_operand(state, modrm, has_mem, ea_desc, "read")
            state.reads_gpr_mask |= reg_mask(4)
            state.writes_gpr_mask |= reg_mask(4)
            state.writes_memory = True
        elif subop == 4:  # JMP r/m
            apply_rm_operand(state, modrm, has_mem, ea_desc, "read")
        elif subop == 6:  # PUSH r/m
            apply_rm_operand(state, modrm, has_mem, ea_desc, "read")
            state.reads_gpr_mask |= reg_mask(4)
            state.writes_gpr_mask |= reg_mask(4)
            state.writes_memory = True
        return state.to_dict()

    # MOV reg, imm forms.
    if 0xB8 <= op_id <= 0xBF:
        state.writes_gpr_mask |= reg_mask(op_id - 0xB8)
        return state.to_dict()
    if 0xB0 <= op_id <= 0xB7:
        state.writes_gpr_mask |= reg_mask(op_id - 0xB0)
        return state.to_dict()

    # Accumulator-immediate ALU / test / cmp.
    if op_id in {0x05, 0x0D, 0x15, 0x1D, 0x25, 0x2D, 0x35, 0x3D, 0xA9}:
        state.reads_gpr_mask |= reg_mask(0)
        if op_id != 0x3D and op_id != 0xA9:
            state.writes_gpr_mask |= reg_mask(0)
        state.writes_flags_mask |= ALL_STATUS_FLAGS_MASK
        if op_id in {0x15, 0x1D}:  # ADC/SBB
            state.reads_flags_mask |= FLAG_BITS["cf"]
        return state.to_dict()
    if op_id in {0x3C, 0xA8}:  # CMP AL, imm8 / TEST AL, imm8
        state.reads_gpr_mask |= reg_mask(0)
        state.writes_flags_mask |= ALL_STATUS_FLAGS_MASK
        return state.to_dict()

    # INC/DEC reg short forms.
    if 0x40 <= op_id <= 0x47:
        reg = op_id - 0x40
        state.reads_gpr_mask |= reg_mask(reg)
        state.writes_gpr_mask |= reg_mask(reg)
        state.writes_flags_mask |= ALL_STATUS_FLAGS_MASK & ~FLAG_BITS["cf"]
        return state.to_dict()
    if 0x48 <= op_id <= 0x4F:
        reg = op_id - 0x48
        state.reads_gpr_mask |= reg_mask(reg)
        state.writes_gpr_mask |= reg_mask(reg)
        state.writes_flags_mask |= ALL_STATUS_FLAGS_MASK & ~FLAG_BITS["cf"]
        return state.to_dict()

    # CDQ / CLD
    if op_id == 0x99:
        state.reads_gpr_mask |= reg_mask(0)
        state.writes_gpr_mask |= reg_mask(2)
        return state.to_dict()
    if op_id == 0xFC:
        state.reads_flags_mask |= FLAG_BITS["cf"]  # placeholder to mark flag touch? no architectural dependency
        state.reads_flags_mask &= 0
        state.writes_flags_mask |= 1 << 10  # DF bit
        state.note("cld clears direction flag")
        return state.to_dict()

    # MOV moffs
    if op_id in {0xA0, 0xA1}:
        state.writes_gpr_mask |= reg_mask(0)
        state.reads_memory = True
        return state.to_dict()
    if op_id in {0xA2, 0xA3}:
        state.reads_gpr_mask |= reg_mask(0)
        state.writes_memory = True
        return state.to_dict()

    # Fallback for unsupported entries.
    return None
