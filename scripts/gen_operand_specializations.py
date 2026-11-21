#!/usr/bin/env python3
"""Bake exact operand shapes for the selected trained SuperOpcodes."""

import argparse
import json
from pathlib import Path

from gen_instruction_catalog import read_catalog, supports_exact_operands
from operand_specialization_data import (
    FUSION_CALL, NO_FLAGS, Operand, generated_fusions, resolve_training_plan, specialize_fusion, training_plan_match,
)
from scalar_modrm import pin_keep


ZERO_BRANCHES = {
    "OpJcc_E_Rel8": "true",
    "OpJcc_E_Rel32": "true",
    "OpJcc_NE_Rel8": "false",
    "OpJcc_NE_Rel32": "false",
}

BYTE_PRODUCERS = {
    "OpTest_EbGb", "OpTest_AlImm", "OpCmp_EbGb", "OpCmp_GbEb", "OpCmp_AlImm", "OpGroup1_EbIb_Generic",
    "OpGroup2_Eb1", "OpGroup2_EbIb",
}


def deferred_zero_producer(name, operand, policy):
    width = "uint8_t" if name in BYTE_PRODUCERS else f"FusedOperandWidth<{policy}>"
    if operand.meta & NO_FLAGS:
        return None
    if name in ("OpTest_EbGb", "OpTest_EvGv"):
        return f"LogicalResultFlags<{width}>", f"OpTestModRMDeferred<{policy}>"
    if name in ("OpTest_AlImm", "OpTest_EaxImm"):
        return f"LogicalResultFlags<{width}>", "OpTestAccumulatorDeferred"
    if name in ("OpCmp_AlImm", "OpCmp_EaxImm"):
        return f"SubtractionResultFlags<{width}>", "OpCompareAccumulatorDeferred"
    if name in ("OpCmp_EbGb", "OpCmp_EvGv", "OpCmp_GbEb", "OpCmp_GvEv"):
        rm_destination = "true" if name in ("OpCmp_EbGb", "OpCmp_EvGv") else "false"
        return f"SubtractionResultFlags<{width}>", f"(OpCompareModRMDeferred<{policy}, {rm_destination}>)"
    extension = (operand.modrm >> 3) & 7
    if extension == 7 and name in ("OpGroup1_EbIb_Generic", "OpGroup1_EvIb_Generic", "OpGroup1_EvIz_Generic"):
        extend = "true" if name == "OpGroup1_EvIb_Generic" else "false"
        return f"SubtractionResultFlags<{width}>", f"(OpCompareImmediateDeferred<{policy}, {extend}>)"
    if extension == 5 and operand.modrm >= 0xc0:
        if name in ("OpGroup2_Eb1", "OpGroup2_Ev1"):
            count = 1
        elif name in ("OpGroup2_EbIb", "OpGroup2_EvIb"):
            count = operand.imm & 31
        else:
            count = 0
        if count:
            return f"RightShiftResultFlags<{width}>", f"(OpRightShiftDeferred<{policy}, {count}>)"
    return None


def generate(source, plan, catalog, budget, pin_mode, pin_mask):
    if training_plan_match(plan) != "decoded":
        raise ValueError("expected decoded version 3 fusion operand shapes")
    fusions = generated_fusions(source)
    fusion_plan = {**plan, "entries": [row for row in plan["entries"] if row["kind"] == "fused"]}
    rows = resolve_training_plan(fusion_plan, catalog, fusions)
    keep_for = pin_keep(catalog, pin_mode, pin_mask)
    code = ['// Generated fusion operand lowering.', '#include "operand_specialization.h"',
            '#include "baked_operand_kernels.h"', 'namespace fiberish {']
    entries = []
    for index, row in enumerate(rows[:budget]):
        original = row["original"]
        clone = f"OperandClone_{index:03d}_{original}"
        operands = [Operand(**fields) for fields in row["operands"]]
        if len(operands) != row["width"]:
            raise ValueError(f"invalid decoded operand count for {original}")
        shapes = [operand.cpp() for operand in operands]
        body = fusions[original]
        calls = list(FUSION_CALL.finditer(body))
        operations = [match[1] for match in calls]
        policies = [f"OperandSpec::Profile<{shape}>" for shape in shapes]
        body = specialize_fusion(body, original, clone, policies)
        deferred = None
        if operations[-1] in ZERO_BRANCHES and supports_exact_operands(catalog, operations[-2], operands[-2]):
            deferred = deferred_zero_producer(operations[-2], operands[-2], f"SlotPolicy{len(operations) - 2}")
        for position, (name, operand) in enumerate(zip(operations, operands)):
            if deferred and position == len(operations) - 2:
                call = calls[position][0]
                result_type, producer = deferred
                body = body.replace(call, f"{result_type} deferred_flags;\n    "
                                    + call[:-1].replace(f"op::{name}", producer) + ", deferred_flags)")
            elif deferred and position == len(operations) - 1:
                call = calls[position][0]
                branch = f"BranchOnZeroResult<{ZERO_BRANCHES[name]}>"
                body = body.replace(call, call[:-1].replace(f"op::{name}", branch) + ", deferred_flags)")
            elif supports_exact_operands(catalog, name, operand):
                body = body.replace(f"(SlotPolicy{position}, op::{name},",
                                    f"(SlotPolicy{position}, baked_operand::{name}<SlotPolicy{position}>,")
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
unsigned BakedFusionWidth(HandlerFunc handler) {
    const auto* entry = FindBakedFusion(handler);
    return entry ? entry->width : 1;
}
}  // namespace fiberish
""",
    ]
    return "\n".join(code)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("source", "plan", "catalog", "output"):
        parser.add_argument(f"--{name}", type=Path, required=True)
    parser.add_argument("--budget", type=int, choices=range(1025), default=768, metavar="0..1024")
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
