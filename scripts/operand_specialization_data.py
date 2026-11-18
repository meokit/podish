"""Instruction identities and decoded operand fields shared by the collector and baker."""

import re
from dataclasses import dataclass
from pathlib import Path


def instruction_targets(families: Path, variants: Path, fallbacks: Path):
    encodings = {
        name: int(opcode, 16)
        for name, opcode in re.findall(r'using\s+(\w+)\s*=\s*Encoding<0x([\da-fA-F]+),', families.read_text())
    }
    targets = {}
    for opcode, name in re.findall(r'INSTRUCTION_FALLBACK\(0x([\da-fA-F]+),\s*(\w+)\)', fallbacks.read_text()):
        targets.setdefault(name, set()).add(int(opcode, 16))
    for name, family in re.findall(r'INSTRUCTION_(?:FUSION_)?VARIANT\((\w+),\s*(\w+),', variants.read_text()):
        targets.setdefault(name, set()).add(encodings[family])
    return targets


def generated_fusions(source):
    return {
        match[1]: match[0]
        for match in re.finditer(r'^ATTR_PRESERVE_NONE int64_t (SuperOpcode_\w+)\([^\{]*\{.*?^\}', source, re.M | re.S)
    }


FUSION_CALL = re.compile(r'RUN_SUPEROPCODE_OP\(op::(\w+),[^()]*\)')
METADATA_MASK = 0xdf  # The lowering bit describes the host handler, not the x86 instruction.
HAS_MEMORY = 2
HAS_IMMEDIATE = 4


@dataclass(frozen=True, order=True)
class Operand:
    modrm: int
    prefixes: int
    meta: int
    length: int
    ea_desc: int
    disp: int
    imm: int

    def __post_init__(self):
        for value in (self.modrm, self.prefixes, self.meta, self.length):
            if type(value) is not int or not 0 <= value <= 255:
                raise ValueError("encoding fields must be unsigned bytes")
        for value in (self.ea_desc, self.disp, self.imm):
            if type(value) is not int or not 0 <= value < 1 << 32:
                raise ValueError("operand values must be unsigned 32-bit integers")
        if not 1 <= self.length <= 15 or self.meta & ~METADATA_MASK or self.meta >> 6 not in (0, 2):
            raise ValueError("invalid decoded instruction length or metadata")
        if (not self.meta & HAS_MEMORY or self.meta >> 6 != 0) and (self.ea_desc or self.disp):
            raise ValueError("effective address fields require a decoded memory operand")
        if not self.meta & HAS_IMMEDIATE and self.imm:
            raise ValueError("immediate must be zero when absent")

    def legacy_shape(self):
        return self.modrm | self.prefixes << 8 | self.ea_desc << 16

    def cpp(self):
        encoding = self.modrm | self.prefixes << 8 | self.meta << 16 | self.length << 24
        return f"DecodedOperandShape{{0x{encoding:08x}u, 0x{self.ea_desc:08x}u, 0x{self.disp:08x}u, 0x{self.imm:08x}u}}"
