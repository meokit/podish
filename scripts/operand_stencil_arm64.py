"""Extract movable AArch64 handlers from Clang's little-endian Mach-O objects."""

import struct
import re
from bisect import bisect_left
from dataclasses import dataclass


# These preserve_none targets use typed native address bindings.
HELPERS = (
    "MemoryOpRestart",
    "MemoryOpRetry",
    "ResolveBranchTargetSlowLink",
    "ResolveBranchTargetSlowControlFlow",
    "ResolveBranchTarget",
)
HELPER_SYMBOLS = {f"__ZN8fiberish{len(name)}{name}E": i for i, name in enumerate(HELPERS)}
# The rest are normal-ABI functions explicitly retained by the native build.
MMU_SLOW = re.compile(r"__ZN8fiberish3mem3Mmu(?:9read_slow|10write_slow)I[htjy]EE\w+")


def branch_target(symbol):
    if not symbol or not symbol[1] & 1:
        return None
    name = symbol[0]
    for prefix, index in HELPER_SYMBOLS.items():
        if name.startswith(prefix):
            return HELPERS[index]
    if (MMU_SLOW.fullmatch(name) or re.fullmatch(r"_f80_\w+", name) or name == "_LogMsgState"):
        return name
    return None


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
    def __init__(self, path):
        self.path = path
        self.data = path.read_bytes()
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

    def probe_handlers(self, count):
        if count == 0:
            return []
        text_id, code = self.section("__text")
        _, pointers = self.section("__fiber_probes")
        if not code or not pointers or pointers.size != 8 * count:
            raise ValueError(f"{self.path}: incomplete probe object")
        probe_addresses = {}
        for offset, kind, pc_relative, size, symbol in self.relocations(pointers):
            if (kind, pc_relative, size) != (0, 0, 3) or not symbol or symbol[2] != text_id or offset % 8:
                raise ValueError(f"{self.path}: unexpected probe pointer relocation")
            if struct.unpack_from("<Q", self.data, pointers.offset + offset)[0] != 0:
                raise ValueError(f"{self.path}: unexpected probe pointer addend")
            probe_addresses[offset // 8] = symbol[3]
        if len(probe_addresses) != count:
            raise ValueError(f"{self.path}: missing probe pointers")

        # Clang labels function starts in __text. Mach-O also includes local
        # assembler bookkeeping labels which do not delimit functions.
        starts = set(probe_addresses.values())
        starts.update(address for name, kind, section, address in self.symbols
                      if section == text_id and kind & 0x0e == 0x0e and name.startswith("_"))
        starts = sorted(starts | {code.address + code.size})
        ends = dict(zip(starts, starts[1:]))
        relocations = sorted(self.relocations(code), key=lambda relocation: relocation[0])
        offsets = [relocation[0] for relocation in relocations]
        result = []
        for i in range(count):
            address = probe_addresses[i]
            start = address - code.address
            end = ends[address] - code.address
            if not 0 <= start < end <= code.size or start % 4 or end % 4:
                raise ValueError(f"{self.path}: invalid probe function range")
            words = struct.unpack_from(f"<{(end - start) // 4}I", self.data, code.offset + start)
            # Padding between __text symbols is not part of the handler.
            while words and words[-1] == 0xd503201f:
                words = words[:-1]
            helper_calls = {}
            data_relocs = {}
            constants = bytearray()
            section_offsets = {}
            reason = None
            for offset, kind, pc_relative, size, symbol in relocations[
                bisect_left(offsets, start):bisect_left(offsets, end)
            ]:
                slot = (offset - start) // 4
                if offset % 4 == 0 and slot < len(words) and size == 2:
                    word = words[slot]
                    helper = branch_target(symbol)
                    if kind == 2 and pc_relative and helper and word & 0x7fffffff == 0x14000000:
                        helper_calls[slot] = helper
                        continue
                    location = self.constant_location(symbol)
                    scale = page_offset_scale(word) if kind == 4 else None
                    is_page = kind == 3 and pc_relative and word & 0xffffffe0 == 0x90000000
                    is_offset = kind == 4 and not pc_relative and scale is not None and not word & 0x003ffc00
                    if location and (is_page or is_offset):
                        section, relative = location
                        if section not in section_offsets:
                            padding = -len(constants) % (1 << section.alignment)
                            constants.extend(bytes(padding))
                            section_offsets[section] = len(constants)
                            constants.extend(self.data[section.offset:section.offset + section.size])
                        if not is_offset or not (relative + section_offsets[section]) % (1 << scale):
                            data_relocs[slot] = (kind, section_offsets[section] + relative, scale or 0)
                            continue
                reason = "unsupported_relocation"
                break
            if not words:
                reason = "empty_handler"
            reason = reason or audit(words, helper_calls | data_relocs)
            data = tuple((slot, *reloc) for slot, reloc in data_relocs.items())
            result.append((words, tuple(helper_calls.items()), data, bytes(constants), reason))
        return result
