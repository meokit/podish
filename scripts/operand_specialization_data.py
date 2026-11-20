"""Instruction identities and decoded operand fields shared by the collector and baker."""

import hashlib
import re
from dataclasses import dataclass


def instruction_targets(catalog):
    targets = {}
    for opcode, name in catalog["fallbacks"].items():
        targets.setdefault(name, set()).add(int(opcode))
    for opcode, name in catalog.get("generic_fallbacks", {}).items():
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


@dataclass(frozen=True)
class TrainingHandler:
    name: str
    operations: tuple[str, ...]
    kind: str

    @property
    def cpp(self):
        return f"DispatchWrapper<op::{self.name}>" if self.kind == "wrapper" else self.name


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

    def add_fusion(domain, name, operations, kind):
        if not 2 <= len(operations) <= 4 or any(op not in scalar_ids for op in operations):
            raise ValueError(f"invalid training fusion recipe: {name}")
        key = b"".join(scalar_ids[op].to_bytes(4, "little") for op in operations)
        add(domain, key, name, operations, kind)

    for name, body in sorted(fusions.items()):
        add_fusion(0x80000000, name, tuple(call[1] for call in FUSION_CALL.finditer(body)), "fused")
    for row in catalog["handwritten_fusions"]:
        add_fusion(0xc0000000, row["name"], tuple(row["operations"]), "handwritten")
    return handlers


def training_plan_match(plan):
    if (not isinstance(plan, dict) or type(plan.get("version")) is not int or plan["version"] != 3
            or not isinstance(plan.get("entries"), list)):
        raise ValueError("expected a version 3 operand plan; recapture legacy profiles")
    match = plan.get("match", "decoded")
    if match not in ("decoded", "encoding"):
        raise ValueError(f"unsupported version 3 operand match policy: {match!r}")
    return match


def resolve_training_plan(plan, catalog, fusions, limit=None):
    handlers = training_handlers(catalog, fusions)
    rows = []
    for item in plan["entries"][:limit]:
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
        rows.append({**item, "original": handler.cpp})
    return rows


def specialize_fusion(body, original, clone, policies):
    calls = list(FUSION_CALL.finditer(body))
    if len(calls) != len(policies):
        raise ValueError("Fusion width does not match selected source: " + original)
    names = {}
    declarations = []
    for i, (call, policy) in reversed(list(enumerate(zip(calls, policies)))):
        slot = call[0].split(",")[2].strip()
        alias = f"SlotPolicy{i}"
        names[slot] = alias
        declarations.append(f"    using {alias} = {policy};\n")
        replacement = call[0].replace("RUN_SUPEROPCODE_OP(", f"RUN_SUPEROPCODE_OP_WITH_POLICY({alias}, ", 1)
        body = body[:call.start()] + replacement + body[call.end():]
    body = re.sub(r"NextOp\((\w+)\)", lambda match: (
        f"OperandSpec::Advance<{names[match[1]]}>({match[1]})" if match[1] in names else match[0]
    ), body)
    pos = body.index("{") + 1
    return (body[:pos] + "\n" + "".join(reversed(declarations)) + body[pos:]).replace(original, clone)


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

    def shape_key(self):
        return self.modrm | self.prefixes << 8 | self.ea_desc << 16

    @property
    def encoding(self):
        return self.modrm | self.prefixes << 8 | self.meta << 16 | self.length << 24

    def cpp(self):
        return f"DecodedOperandShape{{0x{self.encoding:08x}u, 0x{self.ea_desc:08x}u, 0x{self.disp:08x}u, 0x{self.imm:08x}u}}"
