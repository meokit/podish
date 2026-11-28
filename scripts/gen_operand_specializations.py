#!/usr/bin/env python3
"""Bake exact operand shapes for the selected trained SuperOpcodes."""

import argparse
import json
from pathlib import Path

from aarch64_baked_words import canonicalize_baked_word
from aarch64_baking import is_branch, pair_key, partial_shape, partial_words
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


def generate_aarch64(plan, budget, pair_budget, sequence_budget, partial_budget=32):
    if plan.get("architecture") != "aarch64" or plan.get("version") not in (1, 2):
        raise ValueError("expected an AArch64 operand plan or execution profile")
    counts = {}
    for row in plan["entries"]:
        word, count = row["word"], row["count"]
        if type(word) is not int or not 0 <= word < 2**32 or type(count) is not int or count <= 0:
            raise ValueError(f"invalid AArch64 profile entry: {row!r}")
        word = canonicalize_baked_word(word)
        counts[word] = counts.get(word, 0) + count
    words = sorted(counts, key=lambda word: (-counts[word], word))[:budget]
    if "partials" in plan:
        shapes = plan["partials"]
        if not isinstance(shapes, list) or any(
            type(shape) is not int or not 0 <= shape < 2**32 or partial_shape(shape) != shape
            for shape in shapes
        ) or len(set(shapes)) != len(shapes):
            raise ValueError("expected unique canonical AArch64 partial shapes in priority order")
        partials = shapes[:partial_budget]
    else:
        partials = partial_words(counts, set(words), partial_budget)
    pairs = {}
    for row in plan.get("pairs", []):
        first, second, count = row["first"], row["second"], row["count"]
        if any(type(w) is not int or not 0 <= w < 2**32 for w in (first, second)) or type(count) is not int or count <= 0:
            raise ValueError(f"invalid AArch64 pair: {row!r}")
        key = pair_key(row)
        mask, canon_first, canon_second = key
        if (type(mask) is not int or not 0 <= mask <= 3 or
                ("dynamic_mask" in row and not mask) or
                (mask and is_branch(canon_first)) or
                any(mask & (1 << i) and partial_shape(word) != word
                    for i, word in enumerate((canon_first, canon_second)))):
            raise ValueError(f"invalid AArch64 partial pair: {row!r}")
        pairs[key] = pairs.get(key, 0) + count
    ranked = sorted(pairs, key=lambda key: (-pairs[key], key))[:pair_budget]
    sequences = {}
    for row in plan.get("sequences", []):
        words_in_row, count = tuple(row["words"]), row["count"]
        if not 3 <= len(words_in_row) <= 24 or any(type(w) is not int or not 0 <= w < 2**32 for w in words_in_row):
            raise ValueError(f"invalid AArch64 sequence: {row!r}")
        if type(count) is not int or count < 1:
            raise ValueError(f"invalid AArch64 sequence count: {count!r}")
        words_in_row = tuple(map(canonicalize_baked_word, words_in_row))
        sequences[words_in_row] = sequences.get(words_in_row, 0) + count
    ranked_sequences = sorted(sequences, key=lambda seq: (-sequences[seq] * (len(seq) - 1), seq))[:sequence_budget]
    return render_aarch64_tables(words, partials, ranked, ranked_sequences)


def render_aarch64_tables(words, partials, pairs, sequences):
    def scalar_rows(words, dynamic=False):
        for word in sorted(words):
            literal = f"0x{word:08x}u"
            template = f"{literal}, true" if dynamic else literal
            yield f"    {{{literal}, ScalarHandlers<{template}>}},"

    pair_rows = [[] for _ in range(4)]
    for mask, a, b in sorted(pairs):
        fusion = "BakedFusion<" if not mask else f"PartialBakedFusion<{mask}, "
        pair_rows[mask].append(f"    {{0x{a:08x}{b:08x}ull, aarch64::{fusion}0x{a:08x}u, 0x{b:08x}u>}},")
    names = ("BakedPairs", "BakedFirstDynamic", "BakedSecondDynamic", "BakedBothDynamic")
    pair_tables = "\n".join(
        f"constexpr std::array<PairBinding, {len(rows)}> {name} = {{{{\n"
        + "\n".join(rows) + "\n}};"
        for name, rows in zip(names, pair_rows, strict=True)
    )
    word_pool = []
    offset = 0
    sequence_rows = []
    for seq in sorted(sequences, key=lambda seq: (seq[0], -len(seq), seq)):
        literals = ", ".join(f"0x{word:08x}u" for word in seq)
        tail = ", ".join(f"0x{word:08x}u" for word in seq[1:])
        word_pool.append(f"    {tail},")
        sequence_rows.append(
            f"    {{0x{seq[0]:08x}u, {offset}, {len(seq)}, aarch64::BakedFusion<{literals}>}},")
        offset += len(seq) - 1
    newline = "\n"
    return f'''#include <algorithm>
#include <array>
#include "aarch64/baked.h"

namespace fiberish {{
namespace {{

using aarch64::detail::HandlerFunc;

struct ScalarVariants {{
    HandlerFunc normal;
    HandlerFunc omit_nzcv;
}};
template <uint32_t Word, bool DynamicImmediate = false>
constexpr ScalarVariants ScalarHandlers{{
    aarch64::BakedBinding<Word, DynamicImmediate>(false), aarch64::BakedBinding<Word, DynamicImmediate>(true),
}};
struct ScalarBinding {{
    uint32_t word;
    ScalarVariants handlers;
}};
constexpr std::array<ScalarBinding, {len(words)}> BakedExact = {{{{
{newline.join(scalar_rows(words))}
}}}};
constexpr std::array<ScalarBinding, {len(partials)}> BakedPartial = {{{{
{newline.join(scalar_rows(partials, dynamic=True))}
}}}};

struct PairBinding {{
    uint64_t key;
    aarch64::BakedFusionBinding handlers;
}};
{pair_tables}

template <size_t N>
HandlerFunc FindPair(const std::array<PairBinding, N>& entries, uint32_t a, uint32_t b, uint32_t dead) {{
    const uint64_t key = (uint64_t{{a}} << 32) | b;
    const auto pair = std::ranges::lower_bound(entries, key, {{}}, &PairBinding::key);
    return pair != entries.end() && pair->key == key ? pair->handlers.Select(dead, 2) : nullptr;
}}

HandlerFunc FindPartialPair(const aarch64::DecodedInstruction& a, const aarch64::DecodedInstruction& b,
                            uint32_t dead) {{
    const uint32_t ca = aarch64::detail::CanonicalizeBakedWord(a);
    const uint32_t cb = aarch64::detail::CanonicalizeBakedWord(b);
    const auto am = aarch64::PartialImmediateMask(a);
    const auto bm = aarch64::PartialImmediateMask(b);
    if (am) {{
        if (const auto match = FindPair(BakedFirstDynamic, ca & ~am, cb, dead)) return match;
    }}
    if (bm) {{
        if (const auto match = FindPair(BakedSecondDynamic, ca, cb & ~bm, dead)) return match;
    }}
    return am && bm ? FindPair(BakedBothDynamic, ca & ~am, cb & ~bm, dead) : nullptr;
}}

struct SequenceBinding {{
    uint32_t first;
    uint32_t tail_offset;
    unsigned length;
    aarch64::BakedFusionBinding handlers;
}};
constexpr std::array<uint32_t, {offset}> BakedSequenceTails = {{{{
{newline.join(word_pool)}
}}}};
// Within each first word, try the longest sequence first.
constexpr std::array<SequenceBinding, {len(sequences)}> BakedSequences = {{{{
{newline.join(sequence_rows)}
}}}};

}}  // namespace

aarch64::detail::HandlerFunc FindBakedArmHandler(const aarch64::DecodedInstruction& op, bool omit_nzcv) {{
    const auto word = aarch64::detail::CanonicalizeBakedWord(op);
    const auto exact = std::ranges::lower_bound(BakedExact, word, {{}}, &ScalarBinding::word);
    if (exact != BakedExact.end() && exact->word == word)
        return omit_nzcv ? exact->handlers.omit_nzcv : exact->handlers.normal;

    const auto immediate = aarch64::PartialImmediateMask(op);
    if (!immediate) return nullptr;
    const uint32_t shape = op.word & ~immediate;
    const auto partial = std::ranges::lower_bound(BakedPartial, shape, {{}}, &ScalarBinding::word);
    if (partial != BakedPartial.end() && partial->word == shape)
        return omit_nzcv ? partial->handlers.omit_nzcv : partial->handlers.normal;
    return nullptr;
}}

aarch64::detail::HandlerFunc FindBakedArmPair(uint32_t a, uint32_t b, uint32_t dead) {{
    return FindPair(BakedPairs, a, b, dead);
}}

decltype(aarch64::Profile::resolve_partial_pair) GetBakedArmPartialPairResolver() {{
    if constexpr (BakedFirstDynamic.empty() && BakedSecondDynamic.empty() && BakedBothDynamic.empty())
        return nullptr;
    return FindPartialPair;
}}

aarch64::detail::HandlerFunc FindBakedArmSequence(const uint32_t* words, unsigned count,
                                                unsigned* matched, uint32_t dead) {{
    if (count < 3) return nullptr;
    auto seq = std::ranges::lower_bound(BakedSequences, words[0], {{}}, &SequenceBinding::first);
    for (; seq != BakedSequences.end() && seq->first == words[0]; ++seq) {{
        const auto* tail = BakedSequenceTails.data() + seq->tail_offset;
        if (count >= seq->length && std::equal(tail, tail + (seq->length - 1), words + 1)) {{
            *matched = seq->length;
            return seq->handlers.Select(dead, seq->length);
        }}
    }}
    return nullptr;
}}
}}  // namespace fiberish
'''


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
