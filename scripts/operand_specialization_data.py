"""Instruction identities and decoded operand fields shared by the collector and baker."""

import re
from dataclasses import dataclass


def instruction_targets(catalog):
    targets = {}
    for opcode, name in catalog["fallbacks"].items():
        targets.setdefault(name, set()).add(int(opcode))
    for row in catalog["variants"]:
        targets.setdefault(row["logic"], set()).add(catalog["families"][row["family"]]["opcode"])
    return targets


def generated_fusions(source):
    return {
        match[1]: match[0]
        for match in re.finditer(r'^ATTR_PRESERVE_NONE int64_t (SuperOpcode_\w+)\([^\{]*\{.*?^\}', source, re.M | re.S)
    }


FUSION_CALL = re.compile(r'RUN_SUPEROPCODE_OP\(op::(\w+),[^()]*\)')


def specialize_profile_fusion(body, original, clone, operands):
    calls = list(FUSION_CALL.finditer(body))
    if len(calls) != len(operands):
        raise ValueError("Fusion width does not match selected source: " + original)
    for call, operand in reversed(list(zip(calls, operands))):
        replacement = "RUN_SUPEROPCODE_OP_IMM" + call[0][len("RUN_SUPEROPCODE_OP"):-1]
        body = body[:call.start()] + replacement + f", 0x{operand.imm:08x}u)" + body[call.end():]
    assumptions = "".join(
        f"    OperandSpec::Profile<{operand.cpp()}>::Assume(op + {i});\n"
        for i, operand in enumerate(operands)
    )
    pos = body.index("{") + 1
    return (body[:pos] + "\n" + assumptions + body[pos:]).replace(original, clone)


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

    @property
    def encoding(self):
        return self.modrm | self.prefixes << 8 | self.meta << 16 | self.length << 24

    def cpp(self):
        return f"DecodedOperandShape{{0x{self.encoding:08x}u, 0x{self.ea_desc:08x}u, 0x{self.disp:08x}u, 0x{self.imm:08x}u}}"
