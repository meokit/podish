#!/usr/bin/env python3
"""Bake exact operand shapes for the selected trained SuperOpcodes."""

import argparse
import json
from itertools import groupby
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

# The Boolean selects whether a true relation takes the guest branch.
COMPARE_BRANCHES = {
    f"OpJcc_{condition}_Rel{width}": f"BranchOnCompare<CompareRelation::{relation}, {taken}>"
    for condition, relation, taken in (
        ("B", "UnsignedLess", "true"), ("AE", "UnsignedLess", "false"),
        ("BE", "UnsignedLessEqual", "true"), ("A", "UnsignedLessEqual", "false"),
        ("L", "SignedLess", "true"), ("GE", "SignedLess", "false"),
        ("LE", "SignedLessEqual", "true"), ("G", "SignedLessEqual", "false"),
    )
    for width in (8, 32)
}

BYTE_PRODUCERS = {
    "OpTest_EbGb", "OpTest_AlImm", "OpCmp_EbGb", "OpCmp_GbEb", "OpCmp_AlImm", "OpGroup1_EbIb_Generic",
    "OpGroup2_Eb1", "OpGroup2_EbIb",
}


def deferred_branch(name, operand, policy, consumer):
    width = "uint8_t" if name in BYTE_PRODUCERS else f"FusedOperandWidth<{policy}>"
    if operand.meta & NO_FLAGS:
        return None
    zero = consumer in ZERO_BRANCHES
    branch = f"BranchOnZeroResult<{ZERO_BRANCHES[consumer]}>" if zero else COMPARE_BRANCHES.get(consumer)
    if not branch:
        return None
    extension = (operand.modrm >> 3) & 7
    if name in ("OpCmp_AlImm", "OpCmp_EaxImm"):
        return f"SubtractionResultFlags<{width}>", "OpCompareAccumulatorDeferred", branch
    if name in ("OpCmp_EbGb", "OpCmp_EvGv", "OpCmp_GbEb", "OpCmp_GvEv"):
        rm_destination = "true" if name in ("OpCmp_EbGb", "OpCmp_EvGv") else "false"
        return f"SubtractionResultFlags<{width}>", f"(OpCompareModRMDeferred<{policy}, {rm_destination}>)", branch
    if extension == 7 and name in ("OpGroup1_EbIb_Generic", "OpGroup1_EvIb_Generic", "OpGroup1_EvIz_Generic"):
        extend = "true" if name == "OpGroup1_EvIb_Generic" else "false"
        return f"SubtractionResultFlags<{width}>", f"(OpCompareImmediateDeferred<{policy}, {extend}>)", branch
    if not zero:
        return None
    if name in ("OpTest_EbGb", "OpTest_EvGv"):
        return f"LogicalResultFlags<{width}>", f"OpTestModRMDeferred<{policy}>", branch
    if name in ("OpTest_AlImm", "OpTest_EaxImm"):
        return f"LogicalResultFlags<{width}>", "OpTestAccumulatorDeferred", branch
    if extension == 5 and operand.modrm >= 0xc0:
        if name in ("OpGroup2_Eb1", "OpGroup2_Ev1"):
            count = 1
        elif name in ("OpGroup2_EbIb", "OpGroup2_EvIb"):
            count = operand.imm & 31
        else:
            count = 0
        if count:
            return f"RightShiftResultFlags<{width}>", f"(OpRightShiftDeferred<{policy}, {count}>)", branch
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
        if supports_exact_operands(catalog, operations[-2], operands[-2]):
            deferred = deferred_branch(operations[-2], operands[-2], f"SlotPolicy{len(operations) - 2}", operations[-1])
        for position, (name, operand) in enumerate(zip(operations, operands)):
            if deferred and position == len(operations) - 2:
                call = calls[position][0]
                result_type, producer, _ = deferred
                body = body.replace(call, f"{result_type} deferred_flags;\n    "
                                    + call[:-1].replace(f"op::{name}", producer) + ", deferred_flags)")
            elif deferred and position == len(operations) - 1:
                call = calls[position][0]
                _, _, branch = deferred
                branch = f"({branch})"
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


def aarch64_imm12_family(word):
    return (word & 0x1f800000) == 0x11000000 or (word & 0x3b000000) == 0x39000000


def aarch64_imm12_shapes(counts, exact_words, budget):
    # Both encoding families put the unsigned immediate in bits [21:10].
    # Keep shift, transfer size, operation, and both register indices fixed.
    imm_mask = 0xfff << 10
    total = {}
    distinct = {}
    for word, count in counts.items():
        if not aarch64_imm12_family(word):
            continue
        shape = word & ~imm_mask
        distinct[shape] = distinct.get(shape, 0) + 1
        if word not in exact_words:
            total[shape] = total.get(shape, 0) + count
    eligible = [shape for shape in total if distinct[shape] >= 2]
    return sorted(eligible, key=lambda shape: (-total[shape], shape))[:budget]


def generate_aarch64(plan, budget, pair_budget, sequence_budget, partial_budget=32):
    if plan.get("architecture") != "aarch64" or plan.get("version") not in (1, 2):
        raise ValueError("expected an AArch64 operand plan or execution profile")
    counts = {}
    for row in plan["entries"]:
        word, count = row["word"], row["count"]
        if type(word) is not int or not 0 <= word < 2**32 or type(count) is not int or count <= 0:
            raise ValueError(f"invalid AArch64 profile entry: {row!r}")
        counts[word] = counts.get(word, 0) + count
    words = sorted(counts, key=lambda word: (-counts[word], word))[:budget]
    cases = [f"    case 0x{word:08x}u: return aarch64::BakedBinding<0x{word:08x}u>(omit_nzcv);"
             for word in sorted(words)]
    if "partials" in plan:
        shapes = plan["partials"]
        if not isinstance(shapes, list) or any(
            type(shape) is not int or not 0 <= shape < 2**32 or shape & (0xfff << 10)
            or not aarch64_imm12_family(shape) for shape in shapes
        ) or len(set(shapes)) != len(shapes):
            raise ValueError("expected unique AArch64 imm12 shapes in priority order")
        partials = shapes[:partial_budget]
    else:
        partials = aarch64_imm12_shapes(counts, set(words), partial_budget)
    partial_cases = [f"    case 0x{word:08x}u: return aarch64::BakedBinding<0x{word:08x}u, true>(omit_nzcv);"
                     for word in sorted(partials)]
    pairs = {}
    for row in plan.get("pairs", []):
        first, second, count = row["first"], row["second"], row["count"]
        if any(type(w) is not int or not 0 <= w < 2**32 for w in (first, second)) or type(count) is not int or count <= 0:
            raise ValueError(f"invalid AArch64 pair: {row!r}")
        key = (first, second)
        pairs[key] = pairs.get(key, 0) + count
    ranked = sorted(pairs, key=lambda key: (-pairs[key], key))[:pair_budget]
    pair_cases = [f"    case 0x{a:08x}{b:08x}ull: return aarch64::BindBakedFusion<0x{a:08x}u, 0x{b:08x}u>(dead);"
                  for a, b in sorted(ranked)]
    sequences = {}
    for row in plan.get("sequences", []):
        words_in_row, count = tuple(row["words"]), row["count"]
        if not 3 <= len(words_in_row) <= 24 or any(type(w) is not int or not 0 <= w < 2**32 for w in words_in_row):
            raise ValueError(f"invalid AArch64 sequence: {row!r}")
        if type(count) is not int or count < 1:
            raise ValueError(f"invalid AArch64 sequence count: {count!r}")
        sequences[words_in_row] = sequences.get(words_in_row, 0) + count
    ranked_sequences = sorted(sequences, key=lambda seq: (-sequences[seq] * (len(seq) - 1), seq))[:sequence_budget]
    sequence_cases = []
    for first, group in groupby(sorted(ranked_sequences, key=lambda seq: (seq[0], -len(seq), seq)), lambda seq: seq[0]):
        sequence_cases.append(f"    case 0x{first:08x}u:")
        for seq in group:
            literals = ", ".join(f"0x{word:08x}u" for word in seq)
            sequence_cases.extend(["        {", f"            static constexpr std::array recipe{{{literals}}};",
                f"            if (count >= {len(seq)} && std::equal(recipe.begin()+1, recipe.end(), words+1)) {{",
                f"                *matched = {len(seq)};",
                f"                return aarch64::BindBakedFusion<{literals}>(dead);", "            }", "        }"])
        sequence_cases.append("        break;")
    return "\n".join(['#include <algorithm>', '#include "aarch64/baked.h"', 'namespace fiberish {',
                      'aarch64::detail::HandlerFunc FindBakedArmHandler(const aarch64::DecodedInstruction& op, bool omit_nzcv) {',
                      '    switch (op.word) {', *cases, '    }',
                      '    switch (op.word & ~(0xfffu << 10)) {', *partial_cases, '    }', '    return nullptr;', '}',
                      'aarch64::detail::HandlerFunc FindBakedArmPair(uint32_t a, uint32_t b, uint32_t dead) {',
                      '    switch ((uint64_t{a} << 32) | b) {', *pair_cases,
                      '    default: return nullptr;', '    }', '}',
                      'aarch64::detail::HandlerFunc FindBakedArmSequence(const uint32_t* words, unsigned count, unsigned* matched, uint32_t dead) {',
                      '    switch (words[0]) {', *sequence_cases, '    }', '    return nullptr;', '}', '}', ''])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--sequence-budget", type=int, default=128)
    parser.add_argument("--partial-budget", type=int, default=32)
    parser.add_argument("--pair-budget", type=int, default=128)
    parser.add_argument("--guest", choices=("i386", "aarch64"), default="i386")
    parser.add_argument("--source", type=Path)
    parser.add_argument("--catalog", type=Path)
    for name in ("plan", "output"):
        parser.add_argument(f"--{name}", type=Path, required=True)
    parser.add_argument("--budget", type=int, default=768)
    parser.add_argument("--pin-specialization", type=int, choices=range(3), default=0)
    parser.add_argument("--pin-mask", type=int, default=0)
    args = parser.parse_args()
    try:
        plan = json.loads(args.plan.read_text())
        if args.guest == "aarch64":
            if min(args.budget, args.pair_budget, args.sequence_budget, args.partial_budget) < 0:
                parser.error("AArch64 budgets must be nonnegative")
            content = generate_aarch64(plan, args.budget, args.pair_budget, args.sequence_budget, args.partial_budget)
        else:
            if not 0 <= args.budget <= 1024:
                parser.error("i386 budget must be in 0..1024")
            if not args.source or not args.catalog:
                parser.error("i386 generation requires --source and --catalog")
            content = generate(args.source.read_text(), plan, read_catalog(args.catalog),
                               args.budget, args.pin_specialization, args.pin_mask)
        args.output.write_text(content)
    except (ValueError, OSError) as error:
        parser.error(str(error))


if __name__ == "__main__":
    main()
