#!/usr/bin/env python3
"""Generate the native instruction catalog from its reviewed JSON table."""

import argparse
import hashlib
import json
import re
from pathlib import Path

from operand_specialization_data import generated_fusions, training_handlers


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
    return "mandatory" in row["spec"] and not row.get("optimization_only", False)


def variant_dispatch_base(catalog, row):
    if is_semantic_variant(row):
        return row["logic"]
    family = catalog["families"][row["family"]]
    return row.get("base", catalog["fallbacks"].get(str(family["opcode"])))


def add_specializations(catalog, source):
    generic = catalog.setdefault("generic_fallbacks", {})
    for opcode, name in source.get("generic_fallbacks", {}).items():
        if opcode not in catalog["fallbacks"] or generic.setdefault(opcode, name) != name:
            raise ValueError(f"inconsistent generic fallback for opcode {opcode}")
    families = catalog["families"]
    for name, family in source.get("families", {}).items():
        if families.setdefault(name, family) != family:
            raise ValueError(f"inconsistent encoding family {name}")
    bindings = catalog["bindings"]
    for section, rows in source.get("bindings", {}).items():
        bindings.setdefault(section, []).extend(rows)
    for row in source.get("variants", []):
        name = row["logic"]
        binding = {"name": name, "arguments": row.get("arguments", []), "shape": row}
        if "kernel" in row:
            binding["kernel"] = row["kernel"]
        else:
            binding["base"] = variant_dispatch_base(catalog, row)
            if not binding["base"] or binding["base"] == name:
                raise ValueError(f"missing base handler for {name}")
        bindings.setdefault(row["section"], []).append(binding)
        catalog["variants"].append(row)
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
    return f"{identifier(name)}<{', '.join(arguments)}>"


def llvm_flag_policy(kernel, *parameters):
    live, dead = (kernel_reference(kernel, [*parameters, f"FlagsSpec::{flags}"]) for flags in ("Live", "Dead"))
    return f"RunLLVMFlagPolicy<{live}, {dead}>(metadata, LogicPassParams)"


def expand_shift_bit(catalog):
    config = catalog.get("shift_bit")
    if not config:
        return
    bindings = []
    exact = {}
    families = catalog["families"]
    variants = catalog["variants"]
    dynamic_shape = ["OpsizeSpec::Dynamic", "AddrModeSpec::Dynamic"]

    def fallback(opcode):
        return catalog["fallbacks"][str(opcode)]

    def family(name, opcode, layout, extension, width):
        if name in families:
            raise ValueError(f"duplicate shift/bit family {name}")
        families[name] = {"opcode": opcode, "layout": layout, "extension": extension, "width_rule": width}

    def variant(name, encoding, fields):
        row = {"logic": name, "family": encoding, "spec": fields, "priority": 0}
        variants.append(row)
        return row

    def bind(name, kernel, arguments):
        bindings.append({"name": name, "kernel": kernel, "arguments": arguments})

    def typed_variant(row, kernel, parameters):
        fields = row["spec"]
        (width,) = fields.get("width", [None])
        size = {None: "Dynamic", "W16": "Opsize16", "W32": "Opsize32"}[width]
        form = "ModReg" if "regForm" in fields else "Memory" if "memoryForm" in fields else "Dynamic"
        (flags,) = fields.get("flags", ["Live"])
        arguments = [*parameters, f"OpsizeSpec::{size}", f"AddrModeSpec::{form}", f"FlagsSpec::{flags}"]
        bind(row["logic"], kernel, arguments)

    def shaped_variants(encoding, stem, width_rule, forms, dead_register, dead_memory=False, repeat=None):
        for width in ([None] if width_rule == "Fixed8" else ["W16", "W32"]):
            for form in forms:
                dead = dead_register if form == "Reg" else dead_memory
                for flags in (["Live", "Dead"] if dead else ["Live"]):
                    fields = {"regForm" if form == "Reg" else "memoryForm": []}
                    if width:
                        fields["width"] = [width]
                    if flags == "Dead":
                        fields["flags"] = ["Dead"]
                    if repeat:
                        fields["repeat"] = [repeat]
                    suffix = (f"_{width[1:]}" if width else "") + f"_{form}_{flags}"
                    yield variant(f"{stem}{suffix}", encoding, fields)

    for opcode, width, count_source in config["group2_forms"]:
        root = fallback(opcode)
        shift_width = "Byte" if width == "Fixed8" else "Scalar"
        base = [f"ShiftWidth::{shift_width}", f"ShiftCountSource::{count_source}"]
        bind(root, "OpGroup2_Internal", base)
        exact[root] = llvm_flag_policy("OpGroup2_Internal", *base, "GroupOpSpec::Dynamic", *dynamic_shape)
        for extension, mnemonic in enumerate(config["group2_extensions"]):
            encoding = f"{root[2:]}_{mnemonic}"
            family(encoding, opcode, "RmOnly", extension, width)
            parameters = [*base, f"GroupOpSpec::Ext{extension}"]
            old_rows = [row for row in variants if row["family"] == encoding]
            # Keep the registered SHL/SHR/SAR shapes used by existing fusions.
            # The old immediate register rotates leave their memory forms open.
            forms = (["Reg", "Mem"] if not old_rows else
                     ["Mem"] if all("regForm" in row["spec"] for row in old_rows) else [])
            for row in [*old_rows, *shaped_variants(encoding, f"Op{encoding}", width, forms,
                                                  count_source != "Cl", count_source != "Cl")]:
                typed_variant(row, "OpGroup2_Internal", parameters)

    for opcode, mnemonic, extension in config["register_bit_ops"]:
        root = fallback(opcode)
        encoding = f"{mnemonic}_EvGv"
        operation = f"GroupOpSpec::Ext{extension}"
        family(encoding, opcode, "RmReg", -1, "ScalarEv")
        bind(root, "OpBitRegister_Internal", [operation])
        exact[root] = llvm_flag_policy("OpBitRegister_Internal", operation, *dynamic_shape)
        for row in shaped_variants(encoding, root, "ScalarEv", ["Reg", "Mem"], True):
            typed_variant(row, "OpBitRegister_Internal", [operation])

    opcode = config["group8_opcode"]
    root = fallback(opcode)
    bind(root, "OpGroup8_Internal", ["GroupOpSpec::Dynamic"])
    exact[root] = llvm_flag_policy("OpGroup8_Internal", "GroupOpSpec::Dynamic", *dynamic_shape)
    for extension, mnemonic in enumerate(config["group8_extensions"], 4):
        encoding = f"Group8_EvIb_{mnemonic}"
        family(encoding, opcode, "RmOnly", extension, "ScalarEv")
        for row in shaped_variants(encoding, f"Op{encoding}", "ScalarEv", ["Reg", "Mem"], True):
            typed_variant(row, "OpGroup8_Internal", [f"GroupOpSpec::Ext{extension}"])

    for opcode, mnemonic, operation, repeat in config["scan_ops"]:
        root = fallback(opcode)
        encoding = f"{mnemonic}_GvEv"
        family(encoding, opcode, "RegRm", -1, "ScalarEv")
        generic = "Reverse" if operation == "Reverse" else "Dynamic"
        if root not in exact:
            bind(root, "OpBitScan_Internal", [f"BitScanOperation::{generic}"])
            exact[root] = llvm_flag_policy("OpBitScan_Internal", f"BitScanOperation::{generic}", *dynamic_shape)
        for row in shaped_variants(encoding, f"Op{encoding}", "ScalarEv", ["Reg", "Mem"], True, repeat=repeat):
            typed_variant(row, "OpBitScan_Internal", [f"BitScanOperation::{operation}"])

    for index, register in enumerate(config["bswap_registers"]):
        opcode = config["bswap_first_opcode"] + index
        root = fallback(opcode)
        encoding = f"Bswap_{register}"
        family(encoding, opcode, "OpcodeReg", -1, "Fixed32")
        if root not in exact:
            bind(root, "OpBswap_Internal", ["RmOperandSpec::Dynamic"])
            exact[root] = f"op::{root}(LogicPassParams)"
        row = variant(f"Op{encoding}", encoding, {"flags": ["Any"]})
        bind(row["logic"], "OpBswap_Internal", [f"RmOperandSpec::Reg{register}"])

    catalog["bindings"]["FIBERCPU_BIND_SHIFT_BIT"] = bindings
    catalog["llvm_block_operations"].extend(exact)
    catalog["shift_bit_llvm_exact"] = exact


def group1_bindings(catalog, forms):
    bindings = {}

    def add(name, *arguments):
        binding = {"name": name, "kernel": "OpGroup1_Internal", "arguments": list(arguments)}
        if name in bindings and bindings[name] != binding:
            raise ValueError(f"inconsistent Group1 binding {name}")
        bindings[name] = binding

    for name, immediate in forms.items():
        add(name, immediate)

    for row in catalog["variants"]:
        family = catalog["families"][row["family"]]
        fallback = catalog["fallbacks"].get(str(family["opcode"]))
        if fallback not in forms or "section" in row:
            continue

        name, fields = row["logic"], row["spec"]
        immediate = forms[fallback]
        if family["layout"] != "RmOnly" or family["extension"] < 0:
            raise ValueError(f"invalid Group1 encoding for {name}")
        if fields.keys() - {"width", "flags", "regForm", "dst", "dst8"} or {"dst", "dst8"} <= fields.keys():
            raise ValueError(f"unsupported Group1 operand spec for {name}")
        (width,) = fields.get("width", ["Dynamic"])
        if immediate == "Group1Immediate::Byte":
            if family["width_rule"] != "Fixed8" or width not in ("Dynamic", "W8"):
                raise ValueError(f"invalid Group1 byte width for {name}")
            size = "Dynamic"
        else:
            if family["width_rule"] != "ScalarEv" or width not in ("Dynamic", "W16", "W32"):
                raise ValueError(f"invalid Group1 operand width for {name}")
            size = {"Dynamic": "Dynamic", "W16": "Opsize16", "W32": "Opsize32"}[width]
        (flags,) = fields.get("flags", ["Live"])
        flags = {"Live": "Live", "Dead": "Dead", "Any": "Dynamic"}[flags]

        rm = "ModReg" if "regForm" in fields else "Dynamic"
        if "dst" in fields or "dst8" in fields:
            register, lane = fields["dst8"] if "dst8" in fields else (*fields["dst"], "Low")
            rm_index = ("Eax", "Ecx", "Edx", "Ebx", "Esp", "Ebp", "Esi", "Edi").index(register)
            if lane == "High":
                rm_index += 4
            rm = f"Reg{rm_index}"
        add(name, immediate, f"FlagsSpec::{flags}", f"GroupOpSpec::Ext{family['extension']}",
            f"OpsizeSpec::{size}", f"RmOperandSpec::{rm}")
    return bindings.values()


def generate(catalog, output, fusion_source=None):
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
    catalog_lines += ["};", "static constexpr Fallback generic_fallbacks[] = {"]
    for opcode, name in sorted(catalog.get("generic_fallbacks", {}).items(), key=lambda pair: int(pair[0])):
        catalog_lines.append(f'    {{0x{int(opcode):03x}, DispatchWrapper<op::{identifier(name)}>, "op::{name}"}},')
    catalog_lines += ["    {0xffff, nullptr, nullptr},", "};",
                      "static constexpr DeclaredInstructionVariant variants[] = {"]
    semantic_indices = []
    for index, row in enumerate(catalog["variants"]):
        name = identifier(row["logic"])
        family = identifier(row["family"])
        catalog["families"][family]
        rank = row["priority"]
        if type(rank) is not int:
            raise ValueError(f"invalid priority for {name}")
        parent = identifier(row.get("fusion_parent", name))
        bake = "true" if "fusion_parent" in row else "false"
        # Mandatory prefixes select different ISA operations (e.g. MMX vs SSE).
        # Their semantic routing is required by the generic decoder too.
        semantic = is_semantic_variant(row)
        if semantic:
            semantic_indices.append((catalog["families"][family]["opcode"], index))
        if not semantic:
            catalog_lines += ["#if FIBERCPU_STATIC_SPECIALIZATION", f"    #define FIBERCPU_VARIANT_{name} DispatchWrapper<op::{name}>",
                              "#else", f"    #define FIBERCPU_VARIANT_{name} nullptr", "#endif"]
        handler = f"DispatchWrapper<op::{name}>" if semantic else f"FIBERCPU_VARIANT_{name}"
        catalog_lines.append(f'    DeclareVariant<spec::family::{family}, {spec_expression(row["spec"])}>'
                             f'({handler}, "op::{name}", "{family}", "op::{parent}", {bake}, {rank}),')
        if not semantic:
            catalog_lines.append(f"    #undef FIBERCPU_VARIANT_{name}")
    catalog_lines.append("};")
    catalog_lines.append("static constexpr uint16_t semantic_variant_indices[] = {")
    catalog_lines.extend(f"    {index}," for _, index in sorted(semantic_indices))
    catalog_lines.append("};")
    fused_handlers = {name for fused in catalog["handwritten_fusions"] for name in fused["operations"]}
    if fusion_source:
        fused_handlers.update(re.findall(r"DispatchWrapper<op::(\w+)>", fusion_source.read_text()))
    aliases = {(row["logic"], variant_dispatch_base(catalog, row)) for row in catalog["variants"]
               if "section" in row and not is_semantic_variant(row)}
    catalog_lines += ["static constexpr FusionAlias fusion_aliases[] = {", "#if FIBERCPU_STATIC_SPECIALIZATION"]
    for name, base in sorted((name, base) for name, base in aliases
                             if base in fused_handlers and name not in fused_handlers):
        catalog_lines.append(f"    {{DispatchWrapper<op::{identifier(name)}>, DispatchWrapper<op::{identifier(base)}>}},")
    catalog_lines += ["#endif", "    {nullptr, nullptr},", "};"]

    group1_forms = {}
    for opcode, immediate in catalog["group1_immediates"].items():
        name = catalog["fallbacks"][opcode]
        if immediate not in ("Byte", "OperandWidth", "SignExtendedByte"):
            raise ValueError(f"invalid Group1 immediate form for opcode {opcode}")
        previous = group1_forms.setdefault(name, f"Group1Immediate::{immediate}")
        if previous != f"Group1Immediate::{immediate}":
            raise ValueError(f"inconsistent Group1 immediate forms for {name}")

    bindings = ["// Generated by gen_instruction_catalog.py. Included at the kernel's declaration site."]
    binding_groups = {**catalog["bindings"], "FIBERCPU_BIND_GROUP1": group1_bindings(catalog, group1_forms)}
    seen = set()
    for group, rows in binding_groups.items():
        bindings.append(f"#if defined({identifier(group)})")
        for row in rows:
            name = identifier(row["name"])
            arguments = row["arguments"]
            generic = row.get("generic_arguments")
            if name in seen:
                raise ValueError(f"duplicate kernel binding {name}")
            seen.add(name)
            bindings.append(f"FORCE_INLINE LogicFlow {name}(LogicFuncParams) {{")
            if shape := row.get("shape"):
                bindings.append(f"    spec::AssumeInstructionShape<spec::family::{shape['family']}, "
                                f"{spec_expression(shape['spec'])}>(op, operand_modrm);")
            if generic:
                bindings.append("#if FIBERCPU_STATIC_SPECIALIZATION")
            entry = identifier(row["base"]) if "base" in row else kernel_reference(row["kernel"], arguments)
            bindings.append(f"    return {entry}(LogicPassParams);")
            if generic:
                bindings += ["#else", f"    return {kernel_reference(row['kernel'], generic)}(LogicPassParams);", "#endif"]
            bindings.append("}")
        bindings.append("#endif")

    llvm_ops = llvm_operation_names(catalog)
    generic_handlers = {name for opcode, name in catalog["fallbacks"].items() if int(opcode) < 512}
    generic_handlers.update(catalog.get("generic_fallbacks", {}).values())
    generic_handlers.update(row["logic"] for row in catalog["variants"] if is_semantic_variant(row))
    if set(llvm_ops) - generic_handlers:
        raise ValueError("LLVM templates require architectural fallback handlers: " +
                         ", ".join(sorted(set(llvm_ops) - generic_handlers)))
    llvm_operations = []
    shift_exact = catalog.get("shift_bit_llvm_exact", {})
    requirements = {"register": 1, "memory": 2, "exact_only": 4}
    for op_id, name in enumerate(llvm_ops):
        exact = shift_exact.get(name, f"op::{identifier(name)}(LogicPassParams)")
        if name in group1_forms:
            exact = llvm_flag_policy("OpGroup1_Internal", group1_forms[name])
        policy = catalog.get("llvm_policies", {}).get(name, {})
        if "live" in policy or "dead" in policy:
            live, dead = (kernel_reference(policy[kind]["kernel"], policy[kind].get("arguments", []))
                          for kind in ("live", "dead"))
            exact = f"RunLLVMFlagPolicy<{live}, {dead}>(metadata, LogicPassParams)"
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
    training_ids = {(handler.kind, handler.name): op_id for op_id, handler in training.items()}
    always_available = set(catalog["fallbacks"].values()) | set(catalog.get("generic_fallbacks", {}).values())
    always_available.update(row["logic"] for row in catalog["variants"] if is_semantic_variant(row))
    scalar_training = ["// Generated scalar training IDs."]
    fused_training = ["// Generated fusion training IDs."]
    for op_id, handler in sorted(training.items()):
        name = identifier(handler.name)
        if handler.kind == "wrapper":
            if name not in always_available:
                scalar_training.append("#if FIBERCPU_STATIC_SPECIALIZATION")
            scalar_training.append(f'    {{0x{op_id:08x}u, DispatchWrapper<op::{name}>, "op::{name}", '
                                   f'{{DispatchWrapper<op::{name}, OperandSpec::SlotBound<1>>, '
                                   f'DispatchWrapper<op::{name}, OperandSpec::SlotBound<2>>}}}},')
            if name not in always_available:
                scalar_training.append("#endif")
        elif handler.kind == "fused":
            fused_training.append(f"FIBERCPU_TRAINING_FUSION({name}, 0x{op_id:08x}u)")

    handwritten = []
    patterns = []
    for row in catalog["handwritten_fusions"]:
        name = identifier(row["name"])
        ops = [f"op::{identifier(op)}" for op in row["operations"]]
        if len(ops) not in (2, 3, 4):
            raise ValueError(f"invalid fusion width for {name}")
        handwritten.append(f"DEFINE_HANDWRITTEN_SUPEROPCODE_{len(ops)}({name}, {', '.join(ops)})")
        handlers = ", ".join(f"DispatchWrapper<{op}>" for op in ops)
        op_id = training_ids["handwritten", name]
        patterns.append(f"    {{{{{handlers}}}, {name}, {len(ops)}, 0x{op_id:08x}u}},")
    handwritten += ["struct HandwrittenFusion { HandlerFunc operations[4]; HandlerFunc handler; unsigned width; uint32_t training_id; };",
                    "constexpr HandwrittenFusion kHandwrittenFusions[] = {", *patterns, "};"]

    for name, lines in (("instruction_families.h", families), ("instruction_catalog.inc", catalog_lines),
                        ("instruction_kernel_bindings.inc", bindings), ("llvm_block_operations.inc", llvm_operations),
                        ("llvm_block_entries.inc", llvm_entries), ("llvm_op_ids.h", llvm_op_ids),
                        ("handwritten_fusions.inc", handwritten), ("training_scalar_handlers.inc", scalar_training),
                        ("training_fusion_handlers.inc", fused_training)):
        path = output / name
        content = "\n".join(lines) + "\n"
        if not path.exists() or path.read_text() != content:
            path.write_text(content)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--catalog", type=Path, default=CATALOG)
    parser.add_argument("--fusion-source", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    generate(read_catalog(args.catalog), args.output, args.fusion_source)
