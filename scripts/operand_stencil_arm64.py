"""Extract movable AArch64 handlers from Clang's little-endian Mach-O objects."""

import struct
import re
from dataclasses import dataclass


# These preserve_none targets use typed native address bindings.
HELPERS = (
    "MemoryOpRestart",
    "MemoryOpRetry",
    "ResolveBranchTargetSlowLink",
    "ResolveBranchTargetSlowDoubleSlot",
)
HELPER_SYMBOLS = {f"__ZN8fiberish{len(name)}{name}E": name for name in HELPERS}
BRANCH26, PAGE21, PAGEOFF12, GOT_LOAD_PAGE21, GOT_LOAD_PAGEOFF12 = range(2, 7)
PAGE_RELOCATIONS = {PAGE21, GOT_LOAD_PAGE21}

def external_target(symbol):
    if not symbol or symbol[1] & 0x0f != 1:  # N_EXT | N_UNDF
        return None
    name = symbol[0]
    for prefix, helper in HELPER_SYMBOLS.items():
        if name.startswith(prefix):
            return helper
    return name if re.fullmatch(r"_[A-Za-z_][A-Za-z0-9_.$]*", name) else None


def page_offset_scale(word):
    if word & 0x7fc00000 == 0x11000000:  # ADD immediate with no shift
        return 0
    if word & 0x3b000000 == 0x39000000:  # unsigned load/store offset
        if word & 0x04800000 == 0x04800000:  # 128-bit SIMD/FP load/store
            return 4
        return word >> 30
    return None

# Decode the instruction class before masking immediate fields. Keep all
# registers, opcode bits and branch destinations in the structural signature.
IMMEDIATE_FIELDS = (
    (0x1f800000, 0x12800000, 0x001fffe0),  # MOVN, MOVZ, MOVK (same halfword selector)
    (0x1f800000, 0x11000000, 0x007ffc00),  # ADD(S), SUB(S) immediate
    (0x1f800000, 0x12000000, 0x007ffc00),  # logical bitmask immediate
    (0x1f800000, 0x13000000, 0x003ffc00),  # signed/unsigned bitfield immediate
    (0x1f800000, 0x13800000, 0x0000fc00),  # EXTR
    (0x3b000000, 0x39000000, 0x003ffc00),  # load/store unsigned offset
    (0x3b200000, 0x38000000, 0x001ff000),  # load/store signed offset
    (0x3a000000, 0x28000000, 0x003f8000),  # load/store pair offset
)


def immediate_mask(word):
    for mask, opcode, fields in IMMEDIATE_FIELDS:
        if word & mask == opcode:
            return fields
    return 0


def branch_width(word):
    if word & 0x7c000000 == 0x14000000:  # B, BL
        return 26, 0
    if word & 0xff000000 == 0x54000000 or word & 0x7e000000 == 0x34000000:  # B.cond, CBZ, CBNZ
        return 19, 5
    if word & 0x7e000000 == 0x36000000:  # TBZ, TBNZ
        return 14, 5
    return None


def signed_field(word, width, shift):
    bias = 1 << (width - 1)
    return (((word >> shift) & ((bias << 1) - 1)) ^ bias) - bias


def audit(words, relocations):
    """Return a reason if a function needs relocation support we do not have."""
    for i, word in enumerate(words):
        if i in relocations:
            continue
        if word & 0x1f000000 == 0x10000000:  # ADR, ADRP
            return "pc_relative_address"
        if word & 0x3b000000 == 0x18000000:  # literal load / prefetch
            return "pc_relative_literal"
        branch = branch_width(word)
        if branch and not 0 <= i + signed_field(word, *branch) < len(words):
            return "branch_outside_handler"
    return None


@dataclass(frozen=True)
class Section:
    name: str
    segment: str
    address: int
    size: int
    offset: int
    alignment: int
    relocs: int
    reloc_count: int


class MachO:
    def __init__(self, path, data):
        self.path = path
        self.data = data
        magic, cpu, subtype, file_type, commands = struct.unpack_from("<5I", self.data)
        if (magic, cpu, subtype & 0xffffff, file_type) != (0xfeedfacf, 0x0100000c, 0, 1):
            raise ValueError(f"{path}: expected a thin Mach-O arm64 object")
        self.sections = [None]  # Mach-O section ordinals start at one.
        symtab = None
        pos = 32
        for _ in range(commands):
            command, size = struct.unpack_from("<2I", self.data, pos)
            if command == 0x19:  # LC_SEGMENT_64
                count, = struct.unpack_from("<I", self.data, pos + 64)
                for i in range(count):
                    name, segment, address, length, offset, align, relocs, reloc_count = struct.unpack_from(
                        "<16s16s2Q4I", self.data, pos + 72 + 80 * i
                    )
                    self.sections.append(Section(name.rstrip(b"\0").decode(), segment.rstrip(b"\0").decode(),
                                                 address, length, offset, align, relocs, reloc_count))
            elif command == 2:  # LC_SYMTAB
                symtab = struct.unpack_from("<4I", self.data, pos + 8)
            if size < 8:
                raise ValueError(f"{path}: invalid load command")
            pos += size
        if symtab is None:
            raise ValueError(f"{path}: Mach-O symbol table missing")
        symoff, nsyms, stroff, strsize = symtab
        strings = self.data[stroff:stroff + strsize]
        self.symbols = []
        for i in range(nsyms):
            index, kind, section, _, address = struct.unpack_from("<IBBHQ", self.data, symoff + 16 * i)
            end = strings.index(b"\0", index)
            self.symbols.append((strings[index:end].decode(), kind, section, address))

    def section(self, name):
        return next(((i, section) for i, section in enumerate(self.sections[1:], 1) if section.name == name),
                    (None, None))

    def constant_location(self, symbol):
        if not symbol or not symbol[2]:
            return None
        section = self.sections[symbol[2]]
        offset = symbol[3] - section.address
        if (section.segment != "__TEXT" or section.name not in
                {"__const", "__cstring", "__literal4", "__literal8", "__literal16"} or section.reloc_count
                or section.alignment > 4 or not 0 <= offset < section.size):
            return None
        return section, offset

    def relocations(self, section):
        for i in range(section.reloc_count):
            offset, info = struct.unpack_from("<2I", self.data, section.relocs + 8 * i)
            if offset >= section.size:
                raise ValueError(f"{self.path}: unsupported scattered or invalid relocation")
            external = (info >> 27) & 1
            symbol = self.symbols[info & 0xffffff] if external else None
            yield offset, info >> 28, (info >> 24) & 1, (info >> 25) & 3, symbol

    def handler(self, name):
        text_id, code = self.section("__text")
        if not code:
            raise ValueError(f"{self.path}: missing LLVM code section")
        address = next((address for symbol, kind, section, address in self.symbols
                        if symbol == name and section == text_id and kind & 0x0e == 0x0e), None)
        if address is None:
            raise ValueError(f"{self.path}: missing LLVM handler {name}")

        entry = address - code.address
        if not 0 <= entry < code.size or entry % 4 or code.size % 4 or code.alignment > 4:
            raise ValueError(f"{self.path}: invalid LLVM code layout")
        # LLVM can outline part of the handler into local functions. Keep them
        # in the same text image so local branch deltas are position independent.
        words = list(struct.unpack_from(f"<{code.size // 4}I", self.data, code.offset))
        helper_calls = {}
        data_relocs = {}
        constants = bytearray()
        section_offsets = {}
        helper_offsets = {}
        reason = None
        for offset, kind, pc_relative, size, symbol in sorted(self.relocations(code)):
            slot = offset // 4
            if offset % 4 == 0 and slot < len(words) and size == 2:
                word = words[slot]
                helper = external_target(symbol)
                if kind == BRANCH26 and pc_relative and word & 0x7fffffff == 0x14000000:
                    if helper:
                        helper_calls[slot] = helper
                        continue
                    if symbol and symbol[2] == text_id:
                        target = symbol[3] - code.address
                        delta = target // 4 - slot
                        if target % 4 == 0 and 0 <= target < code.size and -(1 << 25) <= delta < 1 << 25:
                            words[slot] |= delta & 0x03ffffff
                            continue
                if helper and ((kind == GOT_LOAD_PAGE21 and pc_relative and word & 0xffffffe0 == 0x90000000) or
                               (kind == GOT_LOAD_PAGEOFF12 and not pc_relative and word & 0xfffffc00 == 0xf9400000)):
                    if helper not in helper_offsets:
                        constants.extend(bytes(-len(constants) % 8))
                        helper_offsets[helper] = len(constants)
                        constants.extend(bytes(8))
                    data_relocs[slot] = (kind, helper_offsets[helper], 3 if kind == GOT_LOAD_PAGEOFF12 else 0, helper)
                    continue
                location = self.constant_location(symbol)
                scale = page_offset_scale(word) if kind == PAGEOFF12 else None
                is_page = kind == PAGE21 and pc_relative and word & 0xffffffe0 == 0x90000000
                is_offset = kind == PAGEOFF12 and not pc_relative and scale is not None and not word & 0x003ffc00
                if location and (is_page or is_offset):
                    section, relative = location
                    if section not in section_offsets:
                        padding = -len(constants) % (1 << section.alignment)
                        constants.extend(bytes(padding))
                        section_offsets[section] = len(constants)
                        constants.extend(self.data[section.offset:section.offset + section.size])
                    if not is_offset or not (relative + section_offsets[section]) % (1 << scale):
                        data_relocs[slot] = (kind, section_offsets[section] + relative, scale or 0, None)
                        continue
            reason = "unsupported_relocation"
            break
        reason = reason or audit(words, helper_calls | data_relocs)
        data = tuple((slot, *reloc) for slot, reloc in data_relocs.items())
        return entry, tuple(words), tuple(helper_calls.items()), data, bytes(constants), reason
