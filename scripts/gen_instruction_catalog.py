#!/usr/bin/env python3
"""Generate the native instruction catalog from its reviewed JSON table."""

import argparse
import hashlib
import json
import re
from pathlib import Path

from operand_specialization_data import generated_fusions, instruction_targets, training_handlers
from scalar_modrm import SHARDS, load_baked_modrm, pin_keep, scalar_modrm_plans


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
    return f"RunLLVMFlagPolicy<{live}, {dead}>(metadata, LogicPassParams)"


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


def generate(catalog, output, fusion_source=None, pin_mode=0, pin_mask=0, modrm_plan=None):
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

    catalog_lines = ["// Generated by gen_instruction_catalog.py", "static constexpr Fallback fallbacks[] = {"]
    for opcode, logic in catalog["fallbacks"].items():
        opcode = int(opcode)
        if not 0 <= opcode < 1024:
            raise ValueError("fallback index out of range")
        catalog_lines.append(f'    {{0x{opcode:03x}, DispatchWrapper<op::{identifier(logic)}>, "op::{logic}"}},')
    catalog_lines += ["};", "static constexpr DeclaredInstructionVariant variants[] = {"]
    for row in sorted(catalog["variants"], key=lambda row: catalog["families"][row["family"]]["opcode"]):
        name = identifier(row["logic"])
        family = identifier(row["family"])
        rank = row["priority"]
        if type(rank) is not int:
            raise ValueError(f"invalid priority for {name}")
        catalog_lines.append(f'    DeclareVariant<spec::family::{family}, {spec_expression(row["spec"])}>'
                             f'(DispatchWrapper<op::{name}>, "op::{name}", "{family}", "op::{name}", false, {rank}),')
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

    fusions = generated_fusions(fusion_source.read_text()) if fusion_source else {}
    training = training_handlers(catalog, fusions)
    scalar_plans = scalar_modrm_plans(catalog)
    scalar_policies = catalog.get("scalar_policies", {})
    selected_modrm, selected_policies = load_baked_modrm(modrm_plan, scalar_plans, training, scalar_policies)
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
    for name in sorted(scalar_plans, key=lambda name: (-len(selected_modrm.get(name, ())), name)):
        plan = scalar_plans[name]
        keys = selected_modrm.get(name, ())
        variants = selected_policies.get(name, {})
        shard = min(range(SHARDS), key=shard_weights.__getitem__)
        shard_weights[shard] += len(keys) + len(variants) + 1
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
        mask = " | ".join(policies) if policies else "0"
        lines.append(f"constexpr std::array<ScalarBindingEntry, {len(targets)}> {entries} = {{{{")
        lines.extend(f"    MakeScalarBinding<{target}, 0x{key:04x}, {keep_for(name, key & 255)}>(),"
                     for key, target in sorted(targets.items()))
        link_slot = opcodes_for[name].issubset(branch_links)
        cold = ", ".join(
            f"DispatchWrapper<op::{name}, OperandSpec::SlotBound<{slots}{', false' if link_slot else ''}>>"
            if slots in plan.slot_counts and not is_link_only_return else "nullptr"
            for slots in (1, 2)
        )
        lines.extend(["}};", f"extern constinit const ScalarBindingTable {name}_scalar = "
                      f"{{{entries}.data(), {entries}.size(), 0x{plan.memory_mask:02x}, {mask}, {{{cold}}}}};"])
    scalar_headers = ['#include "scalar_bindings.h"'] + [
        f'#include "ops/{header.name}"' for header in sorted(CATALOG.with_name("ops").glob("*_impl.h"))
    ]
    for index, lines in enumerate(scalar_shards):
        path = output / f"scalar_bindings_{index}.cpp"
        content = "\n".join([*scalar_headers, "namespace fiberish {", *lines, "}  // namespace fiberish", ""])
        if not path.exists() or path.read_text() != content:
            path.write_text(content)
    scalar_training = ["// Generated scalar training IDs."]
    fused_training = ["// Generated fusion training IDs."]
    for op_id, handler in sorted(training.items()):
        name = identifier(handler.name)
        if handler.kind == "wrapper":
            scalar_training.append(f'FIBERCPU_SCALAR({name}, 0x{op_id:08x}u)')
        elif handler.kind == "fused":
            fused_training.append(f"FIBERCPU_TRAINING_FUSION({name}, 0x{op_id:08x}u, {len(handler.operations)})")

    for name, lines in (("instruction_families.h", families), ("instruction_catalog.inc", catalog_lines),
                        ("instruction_kernel_bindings.inc", bindings), ("llvm_block_operations.inc", llvm_operations),
                        ("llvm_block_entries.inc", llvm_entries), ("llvm_op_ids.h", llvm_op_ids),
                        ("block_link_opcodes.inc", link_cases),
                        ("training_scalar_handlers.inc", scalar_training),
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
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    generate(read_catalog(args.catalog), args.output, args.fusion_source, args.pin_mode, args.pin_mask, args.modrm_plan)
