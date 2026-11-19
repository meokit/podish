#!/usr/bin/env python3
"""Compile-probe input and verified runtime templates for decoded operand plans."""

import argparse
import json
import re
from collections import Counter, defaultdict
from pathlib import Path

from gen_instruction_catalog import is_semantic_variant, read_catalog, variant_criteria
from operand_specialization_data import Operand, generated_fusions, instruction_targets, specialize_profile_fusion
from operand_stencil_arm64 import HELPERS, MachO, immediate_mask

WRAPPER = re.compile(r"DispatchWrapper<op::(\w+)>")


def read_plan(path):
    plan = json.loads(path.read_text())
    if (not isinstance(plan, dict) or type(plan.get("version")) is not int or plan["version"] != 2
            or not isinstance(plan.get("entries"), list)):
        raise ValueError("expected a version 2 decoded operand plan")
    return plan["entries"]


def shape_key(item):
    original, kind, width = item["original"], item["kind"], item["width"]
    if type(width) is not int or (kind, width) not in {("wrapper", 1), ("fused", 2), ("fused", 3), ("fused", 4)}:
        raise ValueError("invalid handler kind/width: " + original)
    shapes = tuple(Operand(**fields) for fields in item["operands"])
    if len(shapes) != width:
        raise ValueError("invalid operand count: " + original)
    return original, shapes


def emit_probes(args):
    targets = instruction_targets(read_catalog(args.src / "instruction_catalog.json"))
    fusions = generated_fusions(args.fusions.read_text()) if args.fusions else {}
    preamble = ['#include "ops.h"', '#include "superopcodes.h"']
    preamble.extend(f'#include "ops/{header.name}"' for header in sorted((args.src / "ops").glob("*_impl.h")))
    preamble.append("namespace fiberish {")

    records = [[] for _ in range(args.shards)]
    probes = [[] for _ in records]
    bodies = [[] for _ in records]
    missing = Counter()
    missing_weight = Counter()
    seen = set()
    plan = read_plan(args.plan)
    for i, item in enumerate(plan):
        original, shapes = shape_key(item)
        if type(item["dispatch_count"]) is not int or item["dispatch_count"] < 0:
            raise ValueError("invalid dispatch count: " + original)
        if (original, shapes) in seen:
            raise ValueError("duplicate operand shape for " + original)
        seen.add((original, shapes))
        if item["kind"] == "wrapper":
            match = WRAPPER.fullmatch(original)
            if not match or match[1] not in targets:
                reason = "wrapper_absent"
            else:
                target = f"DispatchWrapper<op::{match[1]}, OperandSpec::Profile<{shapes[0].cpp()}>>"
                reason = None
        elif original not in fusions:
            reason = "fusion_absent"
        else:
            target = f"FiberOperandProbe_{i}"
            reason = None
        if reason:
            missing[reason] += 1
            missing_weight[reason] += item["dispatch_count"]
            continue
        shard = i % args.shards
        if item["kind"] == "fused":
            bodies[shard].append(specialize_profile_fusion(fusions[original], original, target, shapes))
        records[shard].append(item)
        probes[shard].append(f"    {target},")
    for i, pointers in enumerate(probes):
        content = preamble + bodies[i]
        if pointers:
            content += ['[[gnu::used, gnu::section("__DATA,__fiber_probes")]]',
                        'static const HandlerFunc probes[] = {', *pointers, '};']
        args.output.joinpath(f"probe_{i}.cpp").write_text("\n".join([*content, "}"]) + "\n")
    args.output.joinpath("probes.json").write_text(json.dumps({
        "records": records,
        "missing": missing,
        "missing_dispatch_weight": missing_weight,
        "dispatch_weight": sum(row["dispatch_count"] for row in plan),
    }))


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
    if len(records) != len(args.objects):
        raise ValueError("probe object count differs from manifest")
    groups = defaultdict(list)
    excluded = Counter(manifest["missing"])
    excluded_weight = Counter(manifest["missing_dispatch_weight"])
    for rows, path in zip(records, args.objects):
        for item, (words, helpers, data, constants, reason) in zip(rows, MachO(path).probe_handlers(len(rows)), strict=True):
            if reason:
                excluded[reason] += 1
                excluded_weight[reason] += item["dispatch_count"]
                continue
            fixed = tuple(word & ~immediate_mask(word) for word in words)
            groups[item["original"], fixed, helpers, data, constants].append((item, words))

    # Keep an unmodified exemplar for each shape. Store only the fields that
    # vary within that shape, and compare every reconstructed body with Clang's.
    templates, code, patches, helper_slots, recipes, payload, entries = [], [], [], [], [], [], []
    data_slots, constant_pool = [], bytearray()
    recipe_names = []
    helper_ids = {name: i for i, name in enumerate(HELPERS)}
    raw_words = accepted_weight = immediate_variants = 0
    variants_per_handler = Counter()
    templates_per_handler = Counter()
    for (original, _, helpers, data, constants), variants in groups.items():
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
        templates.append((len(code), len(exemplar), len(patches), len(fields), len(helper_slots), len(helpers),
                          len(data_slots), len(data), len(constant_pool), len(constants)))
        templates_per_handler[original] += 1
        code.extend(exemplar)
        patches.extend(fields)
        helper_slots.extend((slot, helper_ids.setdefault(helper, len(helper_ids))) for slot, helper in helpers)
        data_slots.extend(data)
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
        _, words, _, _, _, helpers, _, _, _, constants = templates[template]
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
    emit_array(code_header, "kDataRelocs", "DataPatch", data_slots, 4,
               lambda row: f"{{{row[0]}, {row[2]}, DataRelocation::{('Page' if row[1] == 3 else 'PageOffset')}, {row[3]}}}")
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
            opcode = catalog["families"][row["family"]]["opcode"]
            if is_semantic_variant(row):
                dynamic_only.append(f"{{{original}, {first}, {count}, 1, {variant_criteria(row)}}}")
            elif base := catalog["fallbacks"].get(str(opcode)):
                dynamic_only.append(f"{{DispatchWrapper<op::{base}>, {first}, {count}, 1, {variant_criteria(row)}}}")
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
    probes = subcommands.add_parser("probes")
    probes.add_argument("--src", type=Path, required=True)
    probes.add_argument("--plan", type=Path, required=True)
    probes.add_argument("--fusions", type=Path)
    probes.add_argument("--shards", type=int, required=True, choices=range(1, 65))
    probes.add_argument("--output", type=Path, required=True)
    bundle = subcommands.add_parser("bundle")
    bundle.add_argument("--src", type=Path, required=True)
    bundle.add_argument("--manifest", type=Path, required=True)
    bundle.add_argument("--output", type=Path, required=True)
    bundle.add_argument("--report", type=Path, required=True)
    bundle.add_argument("objects", nargs="+", type=Path)
    args = parser.parse_args()
    if args.mode == "probes":
        emit_probes(args)
    else:
        build_bundle(args)


if __name__ == "__main__":
    main()
