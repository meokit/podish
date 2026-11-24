"""Instruction identities and decoded operand fields shared by the collector and baker."""

import hashlib
import re
from dataclasses import asdict, dataclass, replace
from functools import cache
from pathlib import Path


def instruction_targets(catalog):
    targets = {}
    for opcode, name in catalog["fallbacks"].items():
        targets.setdefault(name, set()).add(int(opcode))
    for row in catalog["variants"]:
        targets.setdefault(row["logic"], set()).add(catalog["families"][row["family"]]["opcode"])
    return targets


def relative_control_operations(catalog):
    relative = set(catalog["block_links"]["with_operands"]) - {0xc2}  # RET imm16 pops extra stack bytes.
    return {name for name, opcodes in instruction_targets(catalog).items() if opcodes <= relative}


def generated_fusions(source):
    return {
        match[1]: match[0]
        for match in re.finditer(r'^ATTR_PRESERVE_NONE int64_t (SuperOpcode_\w+)\([^\{]*\{.*?^\}', source, re.M | re.S)
    }


FUSION_CALL = re.compile(
    r'RUN_SUPEROPCODE_OP_WITH_POLICY\(SlotPolicy\d+, op::(\w+),[^()]*\)'
)


@dataclass(frozen=True)
class TrainingHandler:
    name: str
    operations: tuple[str, ...]
    kind: str

    @property
    def cpp(self):
        return f"DispatchWrapper<op::{self.name}>" if self.kind == "wrapper" else self.name


def fusion_training_id(operation_ids):
    key = b"".join(op_id.to_bytes(4, "little") for op_id in operation_ids)
    digest = int.from_bytes(hashlib.sha256(key).digest()[:4], "little")
    return 0x80000000 | (digest & 0x3fffffff)


def training_handlers(catalog, fusions):
    """Assign persistent IDs to catalog entries and the selected fusion recipes."""
    handlers = {}

    def add(domain, key, name, operations, kind):
        digest = int.from_bytes(hashlib.sha256(key).digest()[:4], "little")
        identifier = domain | (digest & 0x3fffffff)
        if not identifier or identifier in handlers:
            raise ValueError(f"duplicate training identity: {name} (0x{identifier:08x})")
        handlers[identifier] = TrainingHandler(name, operations, kind)
        return identifier

    scalar_ids = {name: add(0, name.encode("ascii"), name, (name,), "wrapper")
                  for name in sorted(instruction_targets(catalog))}

    for name, body in sorted(fusions.items()):
        operations = tuple(call[1] for call in FUSION_CALL.finditer(body))
        if not 2 <= len(operations) <= 4 or any(op not in scalar_ids for op in operations):
            raise ValueError(f"invalid training fusion recipe: {name}")
        identifier = fusion_training_id(scalar_ids[op] for op in operations)
        if identifier in handlers:
            raise ValueError(f"duplicate training identity: {name} (0x{identifier:08x})")
        handlers[identifier] = TrainingHandler(name, operations, "fused")
    return handlers


def training_plan_match(plan):
    if (not isinstance(plan, dict) or type(plan.get("version")) is not int or plan["version"] != 3
            or not isinstance(plan.get("entries"), list)):
        raise ValueError("expected a version 3 operand plan; recapture legacy profiles")
    match = plan.get("match", "decoded")
    if match not in ("decoded", "encoding"):
        raise ValueError(f"unsupported version 3 operand match policy: {match!r}")
    return match


def resolve_training_plan(plan, catalog, fusions):
    handlers = training_handlers(catalog, fusions)
    relative = relative_control_operations(catalog)
    rows = {}
    for item in plan["entries"]:
        identifier = item["originalId"]
        if type(identifier) is not int or not 0 < identifier <= 0xffffffff:
            raise ValueError(f"invalid training handler ID: {identifier!r}")
        handler = handlers.get(identifier)
        if handler is None:
            if identifier >> 30 == 2 and item["kind"] == "fused":
                # A plan can be baked with fusion disabled or with a different fusion set.
                continue
            raise ValueError(f"unknown training handler ID: 0x{identifier:08x}")
        if (item["kind"], item["width"]) != (handler.kind, len(handler.operations)):
            raise ValueError(f"invalid shape for training handler ID: 0x{identifier:08x}")
        operands = tuple(Operand(**fields) for fields in item["operands"])
        if len(operands) != len(handler.operations):
            raise ValueError(f"invalid decoded operand count for {handler.name}")
        shapes = tuple(operand.for_specialization(name in relative)
                       for operand, name in zip(operands, handler.operations))
        key = identifier, shapes
        for field in ("count", "dispatch_count"):
            if field in item and (type(item[field]) is not int or item[field] < 0):
                raise ValueError(f"invalid {field} for {handler.name}")
        if key in rows:
            for field in ("count", "dispatch_count"):
                if field in item:
                    rows[key][field] = rows[key].get(field, 0) + item[field]
        else:
            rows[key] = {**item, "original": handler.cpp, "operands": [asdict(shape) for shape in shapes]}
    return list(rows.values())


def specialize_fusion(body, original, clone, policies):
    if len(list(FUSION_CALL.finditer(body))) != len(policies):
        raise ValueError("Fusion width does not match selected source: " + original)
    return re.sub(r'OperandSpec::FusionSlot<SlotMask, (\d+)>',
                  lambda match: policies[int(match[1])], body).replace(original, clone)


METADATA_MASK = 0xdf  # The lowering bit describes the host handler, not the x86 instruction.
HAS_MODRM = 1
HAS_MEMORY = 2
HAS_IMMEDIATE = 4
NO_FLAGS = 1 << 4
OPSIZE_PREFIX = 1 << 6


@cache
def compact_immediates():
    source = (Path(__file__).resolve().parents[1] / "libfibercpu/src/decoder.h").read_text()
    mask = re.search(r"constexpr uint8_t kCodeMask = (\w+);", source)
    last = re.search(r"constexpr uint8_t kLastLiteral = (\w+);", source)
    constants = re.search(r"kConstants\{([^}]+)\};", source)
    if not (mask and last and constants):
        raise ValueError("could not read compact immediate encoding from decoder.h")
    mask, last = int(mask[1], 0), int(last[1], 0)
    table = [int(value, 0) for value in constants[1].split(",") if value.strip()]
    if len(table) != mask - last:
        raise ValueError("invalid compact immediate table in decoder.h")
    return mask, frozenset(range(last + 1)) | frozenset(table)


def data_operand_slots(operand):
    prefix_mask, immediates = compact_immediates()
    extra = bool(operand.meta & HAS_MEMORY and operand.imm
                 and (operand.prefixes & prefix_mask or operand.imm not in immediates))
    return 1 + extra


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

    def shape_key(self):
        return self.modrm | self.prefixes << 8 | self.ea_desc << 16

    def for_specialization(self, relative):
        if relative and self.meta >> 6 == 2 and self.imm:
            return replace(self, imm=0)
        return self

    @property
    def encoding(self):
        return self.modrm | self.prefixes << 8 | self.meta << 16 | self.length << 24

    def cpp(self):
        return f"DecodedOperandShape{{0x{self.encoding:08x}u, 0x{self.ea_desc:08x}u, 0x{self.disp:08x}u, 0x{self.imm:08x}u}}"
