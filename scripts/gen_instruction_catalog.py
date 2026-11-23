#!/usr/bin/env python3
"""Generate the native instruction catalog from its reviewed JSON table."""

import argparse
import hashlib
import json
import re
from pathlib import Path

from operand_specialization_data import (
    Operand, generated_fusions, instruction_targets, relative_control_operations, training_handlers, training_plan_match,
)
from scalar_modrm import SHARDS, UNBOUND_MODRM_KEY, load_baked_modrm, pin_keep, scalar_modrm_plans, scalar_policy_key


CATALOG = Path(__file__).resolve().parents[1] / "libfibercpu/src/instruction_catalog.json"

SPEC_FIELDS = {
    "width": ("Width",),
    "addressWidth": ("AddressWidth",),
    "flags": ("Flags",),
    "repeat": ("Repeat",),
    "regForm": (),
    "memoryForm": (),
    "dst": ("Gpr",),
    "src": ("Gpr",),
    "dst8": ("Gpr", "ByteLane"),
    "src8": ("Gpr", "ByteLane"),
    "base": ("Base",),
    "index": ("Index",),
    "segment": ("Segment",),
    "scale": (None,),
    "mandatory": ("MandatoryPrefix",),
}


def identifier(name):
    if not isinstance(name, str) or not re.fullmatch(r"[A-Za-z_]\w*", name, re.ASCII):
        raise ValueError(f"invalid catalog identifier {name!r}")
    return name


def spec_expression(fields):
    result = "spec::Spec{}"
    for method, values in fields.items():
        types = SPEC_FIELDS[method]
        if len(types) != len(values):
            raise ValueError(f"wrong number of arguments for {method}")
        arguments = []
        for kind, value in zip(types, values):
            if kind is None:
                if type(value) is not int or not -1 <= value <= 3:
                    raise ValueError(f"invalid scale {value!r}")
                arguments.append(str(value))
            else:
                arguments.append(f"spec::{kind}::{identifier(value)}")
        result += f".{method}({', '.join(arguments)})"
    return result


def read_catalog(path=CATALOG):
    catalog = json.loads(path.read_text())
    if catalog["version"] != 1:
        raise ValueError("unsupported instruction catalog version")
    expand_shift_bit(catalog)
    for source in catalog.get("specialization_sources", []):
        if Path(source).name != source:
            raise ValueError(f"expected a catalog filename, got {source}")
        add_specializations(catalog, json.loads(path.with_name(source).read_text()))
    if any(not is_semantic_variant(row) for row in catalog["variants"]):
        raise ValueError("the architectural catalog accepts only mandatory-prefix variants")
    return catalog


def llvm_operation_names(catalog):
    names = sorted(set(catalog["llvm_block_operations"]))
    if len(names) >= 1 << 16:
        raise ValueError("LLVM operation table exceeds 16-bit IDs")
    return names


def llvm_operation_fingerprint(names):
    return hashlib.sha256("\0".join(names).encode("ascii")).hexdigest()


def variant_criteria(row):
    return f"spec::Criteria<spec::family::{row['family']}>({spec_expression(row['spec'])})"


def is_semantic_variant(row):
    return set(row["spec"]) == {"mandatory"}


def add_specializations(catalog, source):
    for section, rows in source.get("bindings", {}).items():
        catalog["bindings"].setdefault(section, []).extend(rows)
    policies = catalog.setdefault("llvm_policies", {})
    for entry in source.get("llvm", []):
        if isinstance(entry, str):
            catalog["llvm_block_operations"].append(entry)
            continue
        name = entry["name"]
        if policies.setdefault(name, entry) != entry:
            raise ValueError(f"inconsistent LLVM policy for {name}")
        catalog["llvm_block_operations"].append(name)


def kernel_reference(name, arguments):
    if not all(re.fullmatch(r"(?:\w+::)*\w+", arg, re.ASCII) for arg in arguments):
        raise ValueError(f"invalid kernel argument for {name}")
    qualified = "::".join(map(identifier, name.split("::")))
    return f"{qualified}<{', '.join(arguments)}>"


def llvm_flag_policy_call(live, dead, *, baked):
    if baked:
        return (f"(metadata & Meta::no_flags_mask ? {dead}(LogicPassParams, operands)"
                f" : {live}(LogicPassParams, operands))")
    return f"RunFlagPolicy<{live}, {dead}>(metadata, LogicPassParams)"


def llvm_flag_policy(kernel, *parameters, baked=False):
    live, dead = (kernel_reference(kernel, [*parameters, f"FlagsSpec::{flags}"]) for flags in ("Live", "Dead"))
    return llvm_flag_policy_call(live, dead, baked=baked)


def expand_shift_bit(catalog):
    config = catalog.get("shift_bit")
    if not config:
        return
    bindings = []
    exact = {}
    dynamic_shape = ("OpsizeSpec::Dynamic", "AddrModeSpec::Dynamic")

    def bind(opcode, kernel, arguments, extra=(), *, use_flags=True):
        name = catalog["fallbacks"][str(opcode)]
        if name in exact:
            return
        bindings.append({"name": name, "kernel": kernel, "arguments": arguments})
        exact[name] = (llvm_flag_policy(kernel, *arguments, *extra, *dynamic_shape, baked=True)
                       if use_flags else f"op::{name}(LogicPassParams)")

    for opcode, width, count_source in config["group2_forms"]:
        shift_width = "Byte" if width == "Fixed8" else "Scalar"
        arguments = [f"ShiftWidth::{shift_width}", f"ShiftCountSource::{count_source}"]
        bind(opcode, "OpGroup2_Internal", arguments, ("GroupOpSpec::Dynamic",))
        name = catalog["fallbacks"][str(opcode)]
        size = "$width" if shift_width == "Scalar" else "OpsizeSpec::Dynamic"
        catalog.setdefault("scalar_policies", {})[name] = {
            "kernel": "OpGroup2_Internal",
            "arguments": [*arguments, "GroupOpSpec::Dynamic", size, "AddrModeSpec::Dynamic", "$flags"],
        }
    for opcode, _, extension in config["register_bit_ops"]:
        bind(opcode, "OpBitRegister_Internal", [f"GroupOpSpec::Ext{extension}"])
    bind(config["group8_opcode"], "OpGroup8_Internal", ["GroupOpSpec::Dynamic"])
    for opcode, _, operation, _ in config["scan_ops"]:
        generic = "Reverse" if operation == "Reverse" else "Dynamic"
        bind(opcode, "OpBitScan_Internal", [f"BitScanOperation::{generic}"])
    bind(config["bswap_first_opcode"], "OpBswap_Internal", ["RmOperandSpec::Dynamic"], use_flags=False)

    catalog["bindings"]["FIBERCPU_BIND_SHIFT_BIT"] = bindings
    catalog["llvm_block_operations"].extend(exact)
    catalog["shift_bit_llvm_exact"] = exact


def supports_exact_operands(catalog, name, operand):
    if name not in catalog["llvm_block_operations"] or operand.prefixes & 1:
        return False
    policy = catalog.get("llvm_policies", {}).get(name, {})
    requirements = policy.get("requires", ())
    memory = operand.meta & 2
    if "register" in requirements and (not operand.meta & 1 or operand.modrm < 0xc0):
        return False
    if memory and operand.meta >> 6 != 0:
        return False
    if "memory" in requirements and not memory:
        return False
    if "memory_with_66" in requirements and operand.prefixes & 0x40 and not memory:
        return False
    subop = (operand.modrm >> 3) & 7
    if "extensions" in policy and (not operand.meta & 1 or subop not in policy["extensions"]):
        return False
    return True


def static_operand_shapes(plan, catalog, training):
    if not plan:
        return {}
    profile = json.loads(plan.read_text())
    if training_plan_match(profile) != "decoded":
        raise ValueError("expected a decoded version 3 scalar operand plan")
    result = {}
    relative = relative_control_operations(catalog)
    for row in profile["entries"]:
        handler = training.get(row["originalId"])
        if handler is None or handler.kind != "wrapper" or row["kind"] != "wrapper" or row["width"] != 1:
            raise ValueError("static scalar plans require a scalar training ID for each entry")
        fields, = row["operands"]
        operand = Operand(**fields).for_specialization(handler.name in relative)
        if supports_exact_operands(catalog, handler.name, operand):
            result.setdefault(handler.name, set()).add(operand)
    return result


def generate(catalog, output, fusion_source=None, pin_mode=0, pin_mask=0, modrm_plan=None, static_operand_plan=None):
    output.mkdir(parents=True, exist_ok=True)
    families = ['// Generated by gen_instruction_catalog.py', '#pragma once', '#include "instruction_spec.h"',
                'namespace fiberish::spec::family {']
    for name, item in catalog["families"].items():
        opcode, extension = item["opcode"], item["extension"]
        if type(opcode) is not int or not 0 <= opcode < 1024 or type(extension) is not int or not -1 <= extension < 8:
            raise ValueError(f"invalid encoding for {name}")
        layout = identifier(item["layout"])
        rule = identifier(item["width_rule"])
        families.append(f"using {identifier(name)} = Encoding<0x{opcode:03x}, Layout::{layout}, {extension}, WidthRule::{rule}>;")
    families.append("}  // namespace fiberish::spec::family")

    fusions = generated_fusions(fusion_source.read_text()) if fusion_source else {}
    training = training_handlers(catalog, fusions)
    scalar_training = [(op_id, identifier(handler.name)) for op_id, handler in sorted(training.items())
                       if handler.kind == "wrapper"]
    if len(scalar_training) > 0xffff:
        raise ValueError("scalar binding indices exceed 16 bits")
    scalar_indices = {name: index for index, (_, name) in enumerate(scalar_training, 1)}

    catalog_lines = ["// Generated by gen_instruction_catalog.py", "static constexpr Fallback fallbacks[] = {"]
    for opcode, logic in catalog["fallbacks"].items():
        opcode = int(opcode)
        if not 0 <= opcode < 1024:
            raise ValueError("fallback index out of range")
        name = identifier(logic)
        catalog_lines.append(f'    {{0x{opcode:03x}, DispatchWrapper<op::{name}>, "op::{name}", '
                             f'ScalarBindingIndex{{{scalar_indices[name]}}}}},')
    catalog_lines += ["};", "static constexpr DeclaredInstructionVariant variants[] = {"]
    for row in sorted(catalog["variants"], key=lambda row: catalog["families"][row["family"]]["opcode"]):
        name = identifier(row["logic"])
        family = identifier(row["family"])
        rank = row["priority"]
        if type(rank) is not int:
            raise ValueError(f"invalid priority for {name}")
        catalog_lines.append(f'    DeclareVariant<spec::family::{family}, {spec_expression(row["spec"])}>'
                             f'(DispatchWrapper<op::{name}>, "op::{name}", "{family}", "op::{name}", false, {rank}, '
                             f'ScalarBindingIndex{{{scalar_indices[name]}}}),')
    catalog_lines.append("};")

    group1_forms = {}
    for opcode, immediate in catalog["group1_immediates"].items():
        name = catalog["fallbacks"][opcode]
        if immediate not in ("Byte", "OperandWidth", "SignExtendedByte"):
            raise ValueError(f"invalid Group1 immediate form for opcode {opcode}")
        previous = group1_forms.setdefault(name, f"Group1Immediate::{immediate}")
        if previous != f"Group1Immediate::{immediate}":
            raise ValueError(f"inconsistent Group1 immediate forms for {name}")

    bindings = ["// Generated by gen_instruction_catalog.py. Included at the kernel's declaration site."]
    group1_bindings = [{"name": name, "kernel": "OpGroup1_Internal", "arguments": [immediate]}
                       for name, immediate in group1_forms.items()]
    binding_groups = {**catalog["bindings"], "FIBERCPU_BIND_GROUP1": group1_bindings}
    seen = set()
    for group, rows in binding_groups.items():
        bindings.append(f"#if defined({identifier(group)})")
        for row in rows:
            name = identifier(row["name"])
            arguments = row["arguments"]
            if name in seen:
                raise ValueError(f"duplicate kernel binding {name}")
            seen.add(name)
            entry = kernel_reference(row["kernel"], arguments)
            bindings.append(f"FORCE_INLINE LogicFlow {name}(LogicFuncParams) {{")
            bindings.append(f"    return {entry}(LogicPassParams);")
            bindings.append("}")
        bindings.append("#endif")

    extended_decode = ["// Generated by gen_instruction_catalog.py for the legacy 0F 38 and 0F 3A maps."]
    extended_keys = set()
    for handler, encoding in catalog.get("extended_encodings", {}).items():
        index = int(handler)
        escape, opcode = encoding["escape"], encoding["opcode"]
        if (handler not in catalog["fallbacks"] or not 512 <= index < 1024
                or escape not in (0x38, 0x3a) or not 0 <= opcode <= 0xff
                or (escape, opcode) in extended_keys):
            raise ValueError(f"invalid or duplicate extended encoding for {handler}")
        extended_keys.add((escape, opcode))
        imm_type = 9 if encoding["imm8"] else 1
        require_66 = str(encoding["require_66"]).lower()
        extended_decode.append(f"case 0x{escape:02x}{opcode:02x}: return {{{index}, {imm_type}, {require_66}}};")

    llvm_ops = llvm_operation_names(catalog)
    generic_handlers = {name for opcode, name in catalog["fallbacks"].items() if int(opcode) < 512}
    generic_handlers.update(row["logic"] for row in catalog["variants"] if is_semantic_variant(row))
    if set(llvm_ops) - generic_handlers:
        raise ValueError("LLVM templates require architectural fallback handlers: " +
                         ", ".join(sorted(set(llvm_ops) - generic_handlers)))
    llvm_operations = []
    shift_exact = catalog.get("shift_bit_llvm_exact", {})
    requirements = {"register": 1, "memory": 2, "exact_only": 4, "memory_with_66": 8}
    for op_id, name in enumerate(llvm_ops):
        exact = shift_exact.get(name, f"op::{identifier(name)}(LogicPassParams)")
        if name in group1_forms:
            exact = llvm_flag_policy("OpGroup1_Internal", group1_forms[name], baked=True)
        policy = catalog.get("llvm_policies", {}).get(name, {})
        baked = policy.get("baked")
        if "live" in policy or "dead" in policy:
            if baked is not None and baked is not True:
                raise ValueError(f"invalid baked flag policy for {name}")
            live, dead = (kernel_reference(policy[kind]["kernel"], policy[kind].get("arguments", []))
                          for kind in ("live", "dead"))
            exact = llvm_flag_policy_call(live, dead, baked=baked is True)
        elif baked is not None:
            if not isinstance(baked, dict):
                raise ValueError(f"invalid baked operand policy for {name}")
            kernel = kernel_reference(baked["kernel"], baked.get("arguments", []))
            exact = f"op::{kernel}(LogicPassParams, operands)"
        modes = set(policy.get("requires", []))
        if {"register", "memory"} <= modes:
            raise ValueError(f"conflicting operand forms for {name}")
        mask = sum(requirements[mode] for mode in modes)
        extensions = policy.get("extensions", list(range(8)))
        if not extensions or any(type(ext) is not int or not 0 <= ext < 8 for ext in extensions):
            raise ValueError(f"invalid group extensions for {name}")
        subops = sum(1 << ext for ext in set(extensions))
        llvm_operations.append(f"FIBERCPU_LLVM_OP({op_id}, {identifier(name)}, ({exact}), {mask}, {subops})")
    llvm_op_ids = [
        "// Generated by gen_instruction_catalog.py",
        "#pragma once",
        "#include <cstdint>",
        "#include <string_view>",
        f'#define FIBERCPU_LLVM_OP_ID_FINGERPRINT "{llvm_operation_fingerprint(llvm_ops)}"',
        "namespace fiberish::block_recipe {",
        "enum class OpId : uint16_t {};",
        f"inline constexpr unsigned kOpIdCount = {len(llvm_ops)};",
        "inline constexpr auto kInvalidOpId = OpId{UINT16_MAX};",
        "inline constexpr std::string_view kOpIdFingerprint = FIBERCPU_LLVM_OP_ID_FINGERPRINT;",
        "}  // namespace fiberish::block_recipe",
    ]
    llvm_entries = [f"FIBERCPU_LLVM_STEP({i});" for i in range(64)]

    scalar_plans = scalar_modrm_plans(catalog)
    scalar_policies = catalog.get("scalar_policies", {})
    width_kernels = catalog.get("scalar_width_kernels", {})
    if width_kernels.keys() - scalar_plans.keys() or width_kernels.keys() & scalar_policies.keys():
        raise ValueError("required scalar width kernels must have a scalar identity and no optional policy")
    selected_modrm, selected_policies = load_baked_modrm(modrm_plan, scalar_plans, training, scalar_policies)
    exact_shapes = static_operand_shapes(static_operand_plan, catalog, training)
    keep_for = pin_keep(catalog, pin_mode, pin_mask)
    opcodes_for = instruction_targets(catalog)
    branch_links = catalog["block_links"]["with_operands"]
    ret = catalog["block_links"]["link_only_return"]
    if len(set(branch_links + [ret])) != len(branch_links) + 1:
        raise ValueError("duplicate block link encoding")
    link_cases = [f"case 0x{opcode:03x}:" for opcode in sorted(branch_links + [ret])]
    llvm_second_slots = ["true" if opcodes_for[name].issubset(branch_links) else "false" for name in llvm_ops]
    llvm_op_ids[-1:-1] = [
        "inline constexpr bool kHasBranchLinkSlot[] = {",
        *("    " + ", ".join(llvm_second_slots[i:i + 12]) + "," for i in range(0, len(llvm_second_slots), 12)),
        "};",
    ]
    scalar_shards = [[] for _ in range(SHARDS)]
    shard_weights = [0] * SHARDS
    for name in sorted(scalar_plans, key=lambda name: (-len(selected_modrm.get(name, ())) - len(exact_shapes.get(name, ())), name)):
        plan = scalar_plans[name]
        keys = selected_modrm.get(name, ())
        variants = selected_policies.get(name, {})
        shapes = sorted(exact_shapes.get(name, ()), key=lambda s: (s.encoding, s.ea_desc, s.disp, s.imm))
        shard = min(range(SHARDS), key=shard_weights.__getitem__)
        shard_weights[shard] += len(keys) + len(variants) + len(shapes) + 1
        lines = scalar_shards[shard]
        entries = f"{name}_entries"
        targets = {key: f"op::{name}" for key in keys}
        is_link_only_return = opcodes_for[name] == {ret}
        if is_link_only_return:
            # There is no payload metadata once the native block pointer
            # occupies the slot. Bind both legal stack operand widths.
            low_opcode = ret & 7
            targets.update({0x100 | low_opcode: f"op::{name}", 0x140 | low_opcode: f"op::{name}"})
        policies = []
        if variants:
            policy = scalar_policies[name]
            if "$width" in policy["arguments"]:
                policies.append("kBindWidth")
            if "$flags" in policy["arguments"]:
                policies.append("kBindFlags")
            declarations = set()
            for key, (width, flags) in sorted(variants.items()):
                suffix = f"{width or ''}{'_' + flags.title() if flags else ''}"
                target = f"{name}_Baked{suffix}"
                if target not in declarations:
                    declarations.add(target)
                    modes = {
                        "$width": f"OpsizeSpec::Opsize{width}",
                        "$flags": "FlagsSpec::Dead" if flags == "dead" else "FlagsSpec::Live",
                    }
                    kernel = kernel_reference(policy["kernel"], [modes.get(arg, arg) for arg in policy["arguments"]])
                    lines.extend([f"FORCE_INLINE LogicFlow {target}(LogicFuncParams) {{",
                                  f"    return {kernel}(LogicPassParams);", "}"])
                targets[key] = target
        width_fallbacks = []
        if name in width_kernels:
            assert not is_link_only_return
            policies.extend(("kBindWidth", "kRequireWidth"))
            targets.clear()
            kernel = f"op::{identifier(width_kernels[name])}"
            for width in (16, 32):
                width_key = scalar_policy_key(width, None)
                target = kernel_reference(kernel, [f"OpsizeSpec::Opsize{width}"])
                targets.update((key | width_key, target) for key in keys)
                for slots in sorted(plan.slot_counts):
                    key = UNBOUND_MODRM_KEY | width_key | (slots << 8)
                    width_fallbacks.append((key, target, slots))
        mask = " | ".join(policies) if policies else "0"
        lines.append(f"constexpr std::array<ScalarBindingEntry, {len(targets) + len(width_fallbacks)}> {entries} = {{{{")
        lines.extend(f"    MakeScalarBinding<{target}, 0x{key:04x}, {keep_for(name, key & 255)}>(),"
                     for key, target in sorted(targets.items()))
        lines.extend(f"    {{0x{key:04x}, DispatchWrapper<{target}, OperandSpec::SlotBound<{slots}>>}},"
                     for key, target, slots in sorted(width_fallbacks))
        link_slot = opcodes_for[name].issubset(branch_links)
        cold = ", ".join(
            f"DispatchWrapper<op::{name}, OperandSpec::SlotBound<{slots}{', false' if link_slot else ''}>>"
            if slots in plan.slot_counts and not is_link_only_return and not width_fallbacks else "nullptr"
            for slots in (1, 2)
        )
        lines.append("}};")
        exact = "{}"
        if shapes:
            exact = f"{name}_exact"
            lines.append(f"constexpr std::array<ExactScalarBinding, {len(shapes)}> {exact} = {{{{")
            for shape in shapes:
                key = shape.cpp()
                policy = f"OperandSpec::Profile<{key}, {keep_for(name, shape.modrm)}>"
                handler = f"DispatchWrapper<baked_operand::{name}<{policy}>, {policy}>"
                lines.append(f"    {{{key}, {handler}}},")
            lines.append("}};")
        lines.append(f"extern constinit const ScalarBindingTable {name}_scalar = "
                     f"{{{entries}.data(), {entries}.size(), 0x{plan.memory_mask:02x}, {mask}, {{{cold}}}, {exact}}};")
    scalar_headers = ['#include "scalar_bindings.h"'] + [
        f'#include "ops/{header.name}"' for header in sorted(CATALOG.with_name("ops").glob("*_impl.h"))
    ]
    if exact_shapes:
        scalar_headers.append('#include "baked_operand_kernels.h"')
    for index, lines in enumerate(scalar_shards):
        path = output / f"scalar_bindings_{index}.cpp"
        content = "\n".join([*scalar_headers, "namespace fiberish {", *lines, "}  // namespace fiberish", ""])
        if not path.exists() or path.read_text() != content:
            path.write_text(content)
    scalar_training_lines = ["// Generated scalar training IDs.",
                             *(f"FIBERCPU_SCALAR({name}, 0x{op_id:08x}u)" for op_id, name in scalar_training)]
    fused_training = ["// Generated fusion training IDs."]
    for op_id, handler in sorted(training.items()):
        if handler.kind == "fused":
            name = identifier(handler.name)
            fused_training.append(f"FIBERCPU_TRAINING_FUSION({name}, 0x{op_id:08x}u, {len(handler.operations)})")

    for name, lines in (("instruction_families.h", families), ("instruction_catalog.inc", catalog_lines),
                        ("instruction_kernel_bindings.inc", bindings), ("llvm_block_operations.inc", llvm_operations),
                        ("llvm_block_entries.inc", llvm_entries), ("llvm_op_ids.h", llvm_op_ids),
                        ("block_link_opcodes.inc", link_cases), ("extended_decode.inc", extended_decode),
                        ("training_scalar_handlers.inc", scalar_training_lines),
                        ("training_fusion_handlers.inc", fused_training)):
        path = output / name
        content = "\n".join(lines) + "\n"
        if not path.exists() or path.read_text() != content:
            path.write_text(content)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--catalog", type=Path, default=CATALOG)
    parser.add_argument("--fusion-source", type=Path)
    parser.add_argument("--pin-mode", type=int, default=0, choices=range(3))
    parser.add_argument("--pin-mask", type=int, default=0)
    parser.add_argument("--modrm-plan", type=Path)
    parser.add_argument("--static-operand-plan", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    generate(read_catalog(args.catalog), args.output, args.fusion_source, args.pin_mode, args.pin_mask, args.modrm_plan,
             args.static_operand_plan)
