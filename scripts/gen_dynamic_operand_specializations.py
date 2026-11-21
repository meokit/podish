#!/usr/bin/env python3
"""Package LLVM-generated machine code for decoded operand plans."""

import argparse
import json
import re
import struct
from collections import Counter, defaultdict
from pathlib import Path

from gen_instruction_catalog import (
    is_semantic_variant, llvm_operation_fingerprint, llvm_operation_names, read_catalog, variant_criteria,
)
from operand_specialization_data import (
    FUSION_CALL, Operand, generated_fusions, instruction_targets, resolve_training_plan, training_plan_match,
)
from operand_stencil_arm64 import HELPERS, PAGE_RELOCATIONS, MachO, immediate_mask

WRAPPER = re.compile(r"DispatchWrapper<op::(\w+)>")


def shape_key(item):
    original, kind, width = item["original"], item["kind"], item["width"]
    if type(width) is not int or (kind, width) not in {("wrapper", 1), ("fused", 2), ("fused", 3), ("fused", 4)}:
        raise ValueError("invalid handler kind/width: " + original)
    shapes = tuple(Operand(**fields) for fields in item["operands"])
    if len(shapes) != width:
        raise ValueError("invalid operand count: " + original)
    return original, shapes


def llvm_operation(catalog, targets, name, shape):
    # Native decoding gives unprefixed MOV four internal dispatch indexes.
    opcode_aliases = {512: 0x89, 513: 0x89, 514: 0x8b, 515: 0x8b}
    operations = set()
    for opcode in targets.get(name, ()):
        opcode = opcode_aliases.get(opcode, opcode)
        if opcode >= 512:
            continue
        generic = catalog.get("generic_fallbacks", {}).get(str(opcode), catalog["fallbacks"].get(str(opcode)))
        # A mandatory prefix selects the generic operation's ISA semantics.
        # These are the prefix bits of DecodedOp::prefixes, not operand size.
        prefixes = {"P66": 64, "F2": 4, "F3": 2}
        matches = []
        for variant in catalog["variants"]:
            if (not is_semantic_variant(variant) or
                    catalog["families"][variant["family"]]["opcode"] != opcode):
                continue
            spec = variant["spec"]
            if set(spec) != {"mandatory"} or len(spec["mandatory"]) != 1:
                raise ValueError("unsupported semantic variant selector: " + variant["logic"])
            mandatory = spec["mandatory"][0]
            bit = prefixes[mandatory.removesuffix("Only")] if mandatory != "None" else 0
            mask = 70 if mandatory.endswith("Only") or mandatory == "None" else bit
            if shape.prefixes & mask == bit:
                matches.append((-bin(mask).count("1"), variant["priority"], variant["logic"]))
        operations.add(min(matches, key=lambda match: match[:2])[2] if matches else generic)
    if len(operations) == 1:
        operation, = operations
        if operation in catalog["llvm_block_operations"]:
            return operation
    return None


def emit_plan(args):
    catalog = read_catalog(args.src / "instruction_catalog.json")
    targets = instruction_targets(catalog)
    op_names = llvm_operation_names(catalog)
    op_ids = {name: i for i, name in enumerate(op_names)}
    fusions = generated_fusions(args.fusions.read_text()) if args.fusions else {}
    records, requests = [], [llvm_operation_fingerprint(op_names) + "\n"]
    missing, missing_weight = Counter(), Counter()
    source = json.loads(args.plan.read_text())
    if training_plan_match(source) != "decoded":
        raise ValueError("dynamic operand templates need a decoded version 3 operand plan")
    plan = resolve_training_plan(source, catalog, fusions)
    for item in plan:
        original, shapes = shape_key(item)
        if type(item["dispatch_count"]) is not int or item["dispatch_count"] < 0:
            raise ValueError("invalid dispatch count: " + original)
        if item["kind"] == "wrapper":
            match = WRAPPER.fullmatch(original)
            names = (match[1],) if match else ()
        else:
            names = tuple(match[1] for match in FUSION_CALL.finditer(fusions.get(original, "")))
        operations = tuple(llvm_operation(catalog, targets, name, shape) for name, shape in zip(names, shapes))
        if len(names) != len(shapes) or not all(operations):
            missing["llvm_operation_absent"] += 1
            missing_weight["llvm_operation_absent"] += item["dispatch_count"]
            continue
        records.append({**item, "llvm_operations": operations})
        fields = ((op_ids[name], shape.encoding, shape.ea_desc, shape.disp, shape.imm)
                  for name, shape in zip(operations, shapes))
        requests.append(" ".join(str(value) for step in fields for value in step) + "\n")
    args.requests.write_text("".join(requests))
    args.manifest.write_text(json.dumps({
        "records": records,
        "missing": missing,
        "missing_dispatch_weight": missing_weight,
        "dispatch_weight": sum(row["dispatch_count"] for row in plan),
    }))


def read_objects(path, count):
    with path.open("rb") as stream:
        for _ in range(count):
            header = stream.read(4)
            if len(header) != 4:
                raise ValueError(f"{path}: missing LLVM object")
            size, = struct.unpack("<I", header)
            data = stream.read(size)
            if not size or len(data) != size:
                raise ValueError(f"{path}: truncated LLVM object")
            yield data
        if stream.read(1):
            raise ValueError(f"{path}: unexpected trailing LLVM object")


def emit_array(text, name, typename, values, columns=1, format_value=str):
    text.append(f"constexpr {typename} {name}[] = {{")
    if not values:
        text.append("    {},")
    for i in range(0, len(values), columns):
        text.append("    " + ", ".join(format_value(value) for value in values[i:i + columns]) + ",")
    text.append("};")


def aggregate(row):
    return "{" + ", ".join(map(str, row)) + "}"


def word_hex(word):
    return f"0x{word:08x}u"


def shape_order(key):
    first = key[0][0]
    return first.encoding, first.ea_desc, first.disp, first.imm


def build_bundle(args):
    manifest = json.loads(args.manifest.read_text())
    records = manifest["records"]
    groups = defaultdict(list)
    excluded = Counter(manifest["missing"])
    excluded_weight = Counter(manifest["missing_dispatch_weight"])
    for index, obj in enumerate(read_objects(args.objects, len(records))):
        item = records[index]
        if obj == b"-":
            reason = "llvm_shape_unsupported"
        else:
            binding_bytes = 2 * item["width"]
            binding = tuple(struct.iter_unpack("<BB", obj[:binding_bytes]))
            if (len(binding) != item["width"] or any(
                    modrm != operand["modrm"] or slots not in (1, 2)
                    for (modrm, slots), operand in zip(binding, item["operands"]))):
                raise ValueError(f"LLVM stencil record {index} disagrees with its runtime binding")
            entry, words, helpers, data, constants, reason = MachO(args.objects, obj[binding_bytes:]).handler(
                "_FiberOperandStencil")
        if reason:
            excluded[reason] += 1
            excluded_weight[reason] += item["dispatch_count"]
            continue
        fixed = tuple(word & ~immediate_mask(word) for word in words)
        groups[item["original"], tuple(item["llvm_operations"]), binding, entry, fixed, helpers, data, constants].append(
            (item, words))

    # Keep an unmodified exemplar for each shape. Store only the fields that
    # vary within that shape, and compare every reconstructed body with Clang's.
    templates, code, patches, helper_slots, recipes, payload, entries = [], [], [], [], [], [], []
    data_slots, constant_pool = [], bytearray()
    recipe_names = []
    helper_ids = {name: i for i, name in enumerate(HELPERS)}
    raw_words = accepted_weight = immediate_variants = 0
    variants_per_handler = Counter()
    templates_per_handler = Counter()
    for (original, _, _, entry, _, helpers, data, constants), variants in groups.items():
        exemplar = variants[0][1]
        fields = []
        for i, word in enumerate(exemplar):
            changed = 0
            for _, candidate in variants:
                changed |= word ^ candidate[i]
            if changed:
                mask = immediate_mask(word)
                if changed & ~mask:
                    raise ValueError(f"{original}: non-immediate bits changed")
                fields.append((i, mask))

        template = len(templates)
        templates.append((len(code), len(exemplar), entry, len(patches), len(fields), len(helper_slots), len(helpers),
                          len(data_slots), len(data), len(constant_pool), len(constants)))
        templates_per_handler[original] += 1
        code.extend(exemplar)
        patches.extend(fields)
        helper_slots.extend((slot, helper_ids.setdefault(helper, len(helper_ids))) for slot, helper in helpers)
        data_slots.extend((slot, kind, offset, scale, helper_ids.setdefault(helper, len(helper_ids)) if helper else None)
                          for slot, kind, offset, scale, helper in data)
        constant_pool.extend(constants)
        recipe_ids = {}
        for item, words in variants:
            patch = tuple(words[i] & mask for i, mask in fields)
            restored = list(exemplar)
            for (i, mask), value in zip(fields, patch):
                restored[i] = (restored[i] & ~mask) | value
            if tuple(restored) != words:
                raise ValueError(f"{original}: failed machine code round trip")
            if patch not in recipe_ids:
                recipe_ids[patch] = len(recipes)
                recipes.append((template, len(payload)))
                identity = original + ("()" if item["kind"] == "fused" else "")
                recipe_names.append(json.dumps(f"DynamicOperand_{len(recipes) - 1} {identity}"))
                payload.extend(patch)
            entries.append((item, recipe_ids[patch]))
            variants_per_handler[original] += 1
            raw_words += len(words)
            accepted_weight += item["dispatch_count"]
        immediate_variants += len(recipe_ids) - 1

    # Bound arena allocation once. Equal compiler bodies for the same handler
    # share a recipe and executable address even if several profile keys match.
    arena_size = 0
    for template, _ in recipes:
        _, words, _, _, _, _, helpers, _, _, _, constants = templates[template]
        arena_size += ((words * 4 + 15) & ~15) + helpers * 16 + ((constants + 15) & ~15)

    code_header = [f'#include "ops/{header.name}"' for header in sorted((args.src / "ops").glob("*_impl.h"))]
    code_header.append('#include "instruction_families.h"')
    code_header += ["namespace fiberish {", "#ifdef __arm64e__", '#error "Unsupported arm64e handler ABI"', "#endif"]
    originals = dict.fromkeys(item["original"] for item, _ in entries)
    for original in originals:
        if not original.startswith("DispatchWrapper"):
            code_header.append(f"ATTR_PRESERVE_NONE int64_t {original}(EmuState*, DecodedOp*, int64_t, "
                               "mem::MicroTlbAbiWord, mem::MicroTlbAbiWord, uint32_t, uint64_t FIBERCPU_PIN_PARAMS);")
    code_header.append("namespace {")
    target_addresses = [f"reinterpret_cast<uintptr_t>({helper})" for helper in HELPERS]
    for name, i in helper_ids.items():
        if name in HELPERS:
            continue
        code_header.append(f'extern "C" void OperandRelocTarget_{i}() __asm__("{name}");')
        target_addresses.append(f"reinterpret_cast<uintptr_t>(OperandRelocTarget_{i})")
    emit_array(code_header, "kCode", "uint32_t", code, 8, word_hex)
    emit_array(code_header, "kPatches", "ImmediatePatch", patches, 1,
               lambda patch: f"{{{patch[0]}, {word_hex(patch[1])}}}")
    emit_array(code_header, "kHelpers", "HelperPatch", helper_slots, 4, aggregate)
    code_header.append("const uintptr_t kHelperTargets[] = {")
    code_header.extend(f"    {target}," for target in target_addresses)
    code_header.append("};")
    emit_array(code_header, "kDataRelocs", "DataPatch", data_slots, 4, lambda row:
               f"{{{row[0]}, {row[2]}, {'UINT32_MAX' if row[4] is None else row[4]}, "
               f"DataRelocation::{'Page' if row[1] in PAGE_RELOCATIONS else 'PageOffset'}, {row[3]}}}")
    emit_array(code_header, "kConstantPool", "uint8_t", constant_pool, 16, hex)
    emit_array(code_header, "kTemplates", "CodeTemplate", templates, 1, aggregate)
    emit_array(code_header, "kRecipes", "CodeRecipe", recipes, 4, aggregate)
    emit_array(code_header, "kRecipeNames", "const char*", recipe_names, 1)
    emit_array(code_header, "kPayload", "uint32_t", payload, 8, word_hex)
    # Only canonical identities need a runtime index. The keys of each handler
    # are pre-sorted on disk for binary search with no per-key initialization.
    keys_by_handler = defaultdict(list)
    for item, recipe in entries:
        original, shapes = shape_key(item)
        keys_by_handler[original].append((shapes, recipe))
    entry_lines = []
    family_lines = []
    tails = []
    for original, keys in keys_by_handler.items():
        width = len(keys[0][0])
        family_lines.append((original, len(entry_lines), len(keys), width))
        for shapes, recipe in sorted(keys, key=shape_order):
            entry_lines.append(f"{{{shapes[0].cpp()}, {recipe}, {len(tails) if width > 1 else 0}}}")
            tails.extend(shapes[1:])
    emit_array(code_header, "kEntries", "DynamicOperandEntry", entry_lines)
    catalog = read_catalog(args.src / "instruction_catalog.json")
    base_handlers = set(catalog["fallbacks"].values())
    variants = defaultdict(list)
    for row in catalog["variants"]:
        variants[row["logic"]].append(row)
    code_header.append("#if FIBERCPU_STATIC_SPECIALIZATION")
    emit_array(code_header, "kFamilies", "OperandFamily",
               [f"{{{original}, {first}, {count}, {width}, {{.any_flags = true}}}}"
                for original, first, count, width in family_lines])
    code_header.append("constexpr size_t kFamilyCount = " + str(len(family_lines)) + ";")
    code_header.append("#else")
    dynamic_only = []
    for original, first, count, width in family_lines:
        match = WRAPPER.fullmatch(original)
        if not match:
            continue
        name = match[1]
        if name in base_handlers:
            dynamic_only.append(f"{{{original}, {first}, {count}, 1, {{.any_flags = true}}}}")
        for row in variants[name]:
            dynamic_only.append(f"{{{original}, {first}, {count}, 1, {variant_criteria(row)}}}")
    emit_array(code_header, "kFamilies", "OperandFamily", list(dict.fromkeys(dynamic_only)))
    code_header.append("constexpr size_t kFamilyCount = " + str(len(set(dynamic_only))) + ";")
    code_header.append("#endif")
    emit_array(code_header, "kTailShapes", "DecodedOperandShape", tails, 1, Operand.cpp)
    code_header += [f"constexpr size_t kRecipeCount = {len(recipes)};",
                    f"constexpr size_t kArenaSize = {arena_size};", "} // namespace", "} // namespace fiberish"]
    args.output.write_text("\n".join(code_header) + "\n")

    report = {
        "input_variants": sum(variants_per_handler.values()) + sum(excluded.values()),
        "accepted_variants": len(entries),
        "accepted_fused_variants": sum(item["kind"] == "fused" for item, _ in entries),
        "handlers": len(templates_per_handler),
        "templates": len(templates),
        "recipes": len(recipes),
        "immediate_variants": immediate_variants,
        "excluded_variants": excluded,
        "dispatch_weight": manifest["dispatch_weight"],
        "accepted_dispatch_weight": accepted_weight,
        "excluded_dispatch_weight": excluded_weight,
        "raw_code_bytes": raw_words * 4,
        "template_code_bytes": len(code) * 4,
        "immediate_data_bytes": len(payload) * 4 + len(patches) * 8,
        "branch_relocations": len(helper_slots),
        "constant_relocations": len(data_slots),
        "max_executable_bytes": arena_size,
        "by_handler": {name: {"variants": count, "templates": templates_per_handler[name]}
                       for name, count in sorted(variants_per_handler.items())},
    }
    args.report.write_text(json.dumps(report, indent=2) + "\n")
    print(f"Dynamic operands: {len(entries)} keys, {len(templates)} templates, "
          f"{immediate_variants} immediate variants, {sum(excluded.values())} fallbacks")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    subcommands = parser.add_subparsers(dest="mode", required=True)
    plan = subcommands.add_parser("plan")
    plan.add_argument("--src", type=Path, required=True)
    plan.add_argument("--plan", type=Path, required=True)
    plan.add_argument("--fusions", type=Path)
    plan.add_argument("--requests", type=Path, required=True)
    plan.add_argument("--manifest", type=Path, required=True)
    bundle = subcommands.add_parser("bundle")
    bundle.add_argument("--src", type=Path, required=True)
    bundle.add_argument("--manifest", type=Path, required=True)
    bundle.add_argument("--output", type=Path, required=True)
    bundle.add_argument("--report", type=Path, required=True)
    bundle.add_argument("objects", type=Path)
    args = parser.parse_args()
    if args.mode == "plan":
        emit_plan(args)
    else:
        build_bundle(args)


if __name__ == "__main__":
    main()
