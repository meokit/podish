#!/usr/bin/env python3
"""Bake exact operand shapes for the selected trained SuperOpcodes."""

import argparse
import json
from pathlib import Path

from gen_instruction_catalog import read_catalog
from operand_specialization_data import (
    FUSION_CALL, Operand, generated_fusions, resolve_training_plan, specialize_fusion, training_plan_match,
)
from scalar_modrm import pin_keep


def generate(source, plan, catalog, budget, pin_mode, pin_mask):
    if training_plan_match(plan) != "decoded":
        raise ValueError("expected decoded version 3 fusion operand shapes")
    fusions = generated_fusions(source)
    fusion_plan = {**plan, "entries": [row for row in plan["entries"] if row["kind"] == "fused"]}
    rows = resolve_training_plan(fusion_plan, catalog, fusions)
    keep_for = pin_keep(catalog, pin_mode, pin_mask)
    code = ['// Generated fusion operand lowering.', '#include "operand_specialization.h"', 'namespace fiberish {']
    entries = []
    for index, row in enumerate(rows[:budget]):
        original = row["original"]
        clone = f"OperandClone_{index:03d}_{original}"
        operands = [Operand(**fields) for fields in row["operands"]]
        if len(operands) != row["width"]:
            raise ValueError(f"invalid decoded operand count for {original}")
        shapes = [operand.cpp() for operand in operands]
        body = fusions[original]
        operations = [match[1] for match in FUSION_CALL.finditer(body)]
        policies = [f"OperandSpec::Profile<{shape}>" for shape in shapes]
        body = specialize_fusion(body, original, clone, policies)
        keep = (1 << pin_mask.bit_count()) - 1
        for name, operand in zip(operations, operands):
            keep &= keep_for(name, operand.modrm)
        tail = body.rfind("flags_cache FIBERCPU_PIN_RELOAD")
        if tail >= 0:
            policy = f"OperandKeep_{index:03d}"
            code.append(f"struct {policy} {{ static constexpr unsigned keep_mask = {keep}; }};")
            body = body[:tail] + body[tail:].replace("FIBERCPU_PIN_RELOAD", f"FIBERCPU_PIN_FORWARD({policy})", 1)
        code.append(body)
        first = f"DispatchWrapper<op::{operations[0]}>"
        entries.append(f"    {{{original}_handlers[0], {clone}, {{{', '.join(shapes)}}}, {len(shapes)}, {first}}},")
    code += [
        "namespace {",
        f"const std::array<ProfileOperandShapeEntry, {len(entries)}> shapes = {{{{",
        *entries,
        "}};",
        """const ProfileOperandShapeEntry* FindBakedFusion(HandlerFunc handler) {
    static const auto originals = [] {
        ankerl::unordered_dense::map<HandlerFunc, const ProfileOperandShapeEntry*> result;
        for (const auto& entry : shapes) {
            result.emplace(entry.replacement, &entry);
            result.emplace(entry.original, &entry);
        }
        return result;
    }();
    const auto found = originals.find(handler);
    return found == originals.end() ? nullptr : found->second;
}
}  // namespace

HandlerFunc LowerFusionOperandHandler(const DecodedInstruction* op, HandlerFunc original) {
    static const OperandIndex index(shapes);
    return index.Find(op, original);
}
HandlerFunc CanonicalOperandHandler(HandlerFunc handler) {
    const auto* entry = FindBakedFusion(handler);
    return entry ? entry->original : handler;
}
HandlerFunc UnfuseOperandHandler(HandlerFunc handler) {
    const auto* entry = FindBakedFusion(handler);
    return entry ? entry->first : handler;
}
}  // namespace fiberish
""",
    ]
    return "\n".join(code)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("source", "plan", "catalog", "output"):
        parser.add_argument(f"--{name}", type=Path, required=True)
    parser.add_argument("--budget", type=int, choices=range(1025), default=256, metavar="0..1024")
    parser.add_argument("--pin-specialization", type=int, choices=range(3), default=0)
    parser.add_argument("--pin-mask", type=int, default=0)
    args = parser.parse_args()
    try:
        content = generate(args.source.read_text(), json.loads(args.plan.read_text()), read_catalog(args.catalog),
                           args.budget, args.pin_specialization, args.pin_mask)
        args.output.write_text(content)
    except (ValueError, OSError) as error:
        parser.error(str(error))


if __name__ == "__main__":
    main()
