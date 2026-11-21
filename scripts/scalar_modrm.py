"""Enumerate the mandatory scalar bindings from decoded instruction constraints."""

import json
import re
from collections import defaultdict
from dataclasses import dataclass
from pathlib import Path

from operand_specialization_data import instruction_targets

SHARDS = 32
OPCODE_ALIASES = {0x2bc: 0x1bc}
REGISTERS = ("Eax", "Ecx", "Edx", "Ebx", "Esp", "Ebp", "Esi", "Edi")


@dataclass(frozen=True)
class ScalarModRMPlan:
    memory_mask: int
    slot_counts: frozenset[int]
    modrm_keys: frozenset[int]

# These kernels distinguish register/memory via mod == 3. Their memory address
# comes from the decoder's EA descriptor. Other families keep the raw byte.
DECODED_EA = {
    0x00, 0x01, 0x02, 0x03, 0x08, 0x09, 0x0a, 0x0b,
    0x10, 0x11, 0x12, 0x13, 0x18, 0x19, 0x1a, 0x1b,
    0x20, 0x21, 0x22, 0x23, 0x28, 0x29, 0x2a, 0x2b,
    0x30, 0x31, 0x32, 0x33, 0x38, 0x39, 0x3a, 0x3b,
    0x69, 0x6b, 0x80, 0x81, 0x82, 0x83, 0x84, 0x85,
    0x86, 0x87, 0x88, 0x89, 0x8a, 0x8b, 0x8d, 0xc6, 0xc7,
    0x1af, 0x1b6, 0x1b7, 0x1be, 0x1bf,
}

def decoder_tables():
    header = Path(__file__).resolve().parent.parent / "libfibercpu/src/decoder_lut.h"
    source = header.read_text()
    def read(table):
        contents = source.split(f"{table}[2][256] = ", 1)[1].split(";", 1)[0]
        result = tuple(map(int, re.findall(r"\d+", contents)))
        if len(result) != 512:
            raise ValueError(f"invalid decoder table {table}")
        return result
    return read("kHasModRM"), read("kImmType")

def constraints(family, spec):
    layout = family["layout"]
    ext = family["extension"]
    reg = ext if ext >= 0 else None
    rm = None
    form = "reg" if "regForm" in spec else "any"
    if set(spec) & {"memoryForm", "base", "index", "scale", "segment"}:
        form = "mem"
    def register(role):
        bits = spec.get(role + "8", spec.get(role, []))
        if not bits or bits[0] not in REGISTERS:
            return None
        return REGISTERS.index(bits[0]) + (4 if len(bits) == 2 and bits[1] == "High" else 0)
    if layout in ("RmReg", "RmOnly"):
        rm = register("dst")
        if layout == "RmReg":
            reg = register("src")
    elif layout == "RegRm":
        rm = register("src")
        reg = register("dst")
    if rm is not None:
        form = "reg"
    if spec.get("base") == ["Esp"]:
        rm = 4
    return form, reg, rm

def scalar_modrm_plans(catalog):
    has_modrm, imm_type = decoder_tables()
    sources = defaultdict(list)
    names = instruction_targets(catalog)
    for opcode, name in catalog["fallbacks"].items():
        sources[name].append((int(opcode), "any", None, None))
    for row in catalog["variants"]:
        family = catalog["families"][row["family"]]
        sources[row["logic"]].append((family["opcode"], *constraints(family, row["spec"])))

    branch_links = set(catalog["block_links"]["with_operands"])
    plans = {}
    for name, targets in sorted(sources.items()):
        if name == catalog["fallbacks"]["267"]:  # The decoder uses UD2 for all absent opcodes.
            plans[name] = ScalarModRMPlan(0, frozenset((1, 2)), frozenset())
            continue
        extended = [(OPCODE_ALIASES.get(opcode, opcode), form, reg, rm) for opcode, form, reg, rm in targets]
        normalize_mem = all(code in DECODED_EA for code, _, _, _ in extended)
        mem_mask = 0x38 if normalize_mem else 0xff
        if normalize_mem and any(form != "reg" and rm is not None for _, form, _, rm in extended):
            mem_mask |= 7
        keys = set()
        slot_counts = {2} if all(opcode in branch_links for opcode, _, _, _ in targets) else {1}
        for opcode, form, reg, rm in extended:
            if not 0 <= opcode < 512:
                raise ValueError(f"no decoder row for handler {name} at {opcode:#x}")
            if not has_modrm[opcode]:
                continue
            for raw in range(256):
                register = raw >= 0xc0
                if (form == ("mem" if register else "reg") or
                    reg is not None and (raw >> 3) & 7 != reg or
                    rm is not None and raw & 7 != rm):
                    continue
                value = raw if register else raw & mem_mask
                keys.add(0x100 | value)
                if not register and imm_type[opcode] not in (0, 1):
                    keys.add(0x200 | value)
                    slot_counts.add(2)
        plans[name] = ScalarModRMPlan(mem_mask, frozenset(slot_counts), frozenset(keys))
    if set(names) != set(plans):
        raise ValueError("ModRM catalog does not cover every scalar identity")
    return plans


def scalar_policy_key(width, flags):
    widths = {None: 0, 16: 1, 32: 2}
    flag_modes = {None: 0, "live": 1, "dead": 2}
    return widths[width] << 10 | flag_modes[flags] << 12


def load_baked_modrm(path, legal, handlers, policies):
    if path is None:
        return {}, {}
    plan = json.loads(path.read_text())
    if plan.get("schema_version") != 1 or not isinstance(plan.get("entries"), list):
        raise ValueError(f"{path}: expected a version 1 ModRM table")
    selected = defaultdict(set)
    for row in plan["entries"]:
        op_id, slots, modrm = (row[field] for field in ("training_id", "slots", "modrm"))
        if any(type(value) is not int for value in (op_id, slots, modrm)) or slots not in (1, 2) or not 0 <= modrm < 256:
            raise ValueError(f"{path}: invalid ModRM key {row}")
        handler = handlers.get(op_id)
        if handler is None or handler.kind != "wrapper":
            raise ValueError(f"{path}: unknown scalar training ID 0x{op_id:08x}")
        key = slots << 8 | modrm
        keys = selected[handler.name]
        if key not in legal[handler.name].modrm_keys or key in keys:
            raise ValueError(f"{path}: illegal or duplicate shape {handler.name} {key:#05x}")
        keys.add(key)
    variants = defaultdict(dict)
    for row in plan.get("scalar_policies", []):
        op_id, slots, modrm = (row[field] for field in ("training_id", "slots", "modrm"))
        width, flags = row.get("width"), row.get("flags")
        if any(type(value) is not int for value in (op_id, slots, modrm)) or slots not in (1, 2) or not 0 <= modrm < 256:
            raise ValueError(f"{path}: invalid scalar policy key {row}")
        handler = handlers.get(op_id)
        name = handler.name if handler and handler.kind == "wrapper" else None
        if name is None or name not in policies:
            raise ValueError(f"{path}: unsupported scalar policy for training ID 0x{op_id:08x}")
        arguments = policies[name]["arguments"]
        if ((width is not None) != ("$width" in arguments) or width not in (None, 16, 32)
                or (flags is not None) != ("$flags" in arguments) or flags not in (None, "live", "dead")):
            raise ValueError(f"{path}: invalid scalar policy variant {name} {row}")
        base = slots << 8 | modrm
        key = base | scalar_policy_key(width, flags)
        if base not in selected[name] or key in variants[name]:
            raise ValueError(f"{path}: absent base ModRM or duplicate scalar policy {name} {row}")
        variants[name][key] = (width, flags)
    return {name: sorted(keys) for name, keys in selected.items()}, variants


def pin_keep(catalog, pin_mode, pin_mask):
    targets = instruction_targets(catalog)
    pin_bits = {reg: 1 << slot for slot, reg in enumerate(reg for reg in range(8) if pin_mask & (1 << reg))}
    all_pins = (1 << len(pin_bits)) - 1
    basic_pin = bool(pin_bits) and tuple(pin_bits) != (0, 4)

    pin_rules = {opcode: row for row in catalog["pin_rules"] for opcode in row["opcodes"]}

    def traits(opcode):
        rule = pin_rules.get(opcode)
        if not rule or (basic_pin and not rule["basic"]) or pin_mode < rule["minimum_pin_mode"]:
            return None
        return rule["role"], rule["byte"], rule["form"]

    safe = {}
    for name, codes in targets.items():
        choices = {traits(c) for c in codes}
        if len(choices) == 1 and None not in choices: safe[name] = choices.pop()
    def keep_for(name, shape):
        t = safe.get(name)
        if t is None: return 0
        role, byte, group = t
        if group not in ('Opcode','Address') and (shape & 0xc0) != 0xc0:
            # These integer kernels use nonblocking MMU access. A miss exits via
            # Restart/Retry (which reload pins); normal completion has no observer.
            # Guest memory is separate from EmuState. A memory Rm destination cannot
            # write a guest GPR. Keep this opt-in and restricted to audited families.
            memory_safe = all(pin_rules[opcode]["memory"] for opcode in targets[name])
            if pin_mode < 2 or not memory_safe: return 0
            if role == 'Rm': return all_pins
        if role == 'None' or group == 'Group' and ((shape >> 3) & 7) == 7: return all_pins
        field = (shape >> 3) if role == 'Reg' else shape
        idx = field & (3 if byte else 7)
        return all_pins & ~pin_bits.get(idx, 0)
    return keep_for
