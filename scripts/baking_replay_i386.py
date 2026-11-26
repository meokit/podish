"""Map unfused BLK2 operations to the bindings the native i386 decoder can select."""

from functools import lru_cache

from collect_baked_fusions import sequences, unfused_blocks
from gen_instruction_catalog import read_catalog
from nested_baking_candidates import PARTIAL_FIELDS, modrm_key
from operand_specialization_data import (
    HAS_MODRM, NO_FLAGS, OPSIZE_PREFIX, Operand, data_operand_slots,
    relative_control_operations, training_handlers,
)
from scalar_modrm import scalar_modrm_plans


def layouts(pools, ids, cases, captures):
    """Yield (case ID, block entry count, priority-ordered matches at each op)."""
    catalog = read_catalog()
    handlers = training_handlers(catalog, {})
    relative_names = relative_control_operations(catalog)
    relative = {i for i, h in handlers.items() if h.name in relative_names}
    modrm_plans = scalar_modrm_plans(catalog)
    masks = {i: modrm_plans[h.name].memory_mask for i, h in handlers.items()}
    policies = catalog.get("scalar_policies", {})
    policy_args = {i: policies[h.name]["arguments"] for i, h in handlers.items() if h.name in policies}

    def rows(kind):
        return zip(pools.get(kind, ()), ids.get(kind, ()), strict=True)

    def shape(op_id, fields):
        return Operand(**fields).for_specialization(op_id in relative)

    exact = {(r["originalId"], shape(r["originalId"], r["operands"][0])): i for r, i in rows("exact")}
    fusion = {}
    for row, index in rows("fusion"):
        ops = tuple(row["sequence"])
        operands = tuple(shape(op_id, fields) for op_id, fields in zip(ops, row["operands"], strict=True))
        fusion[ops, operands] = index
    modrm = {modrm_key(r): i for r, i in rows("modrm")}
    policy = {(*modrm_key(r), r["width"], r["flags"]): i for r, i in rows("policies")}

    # FindPartialScalar probes field masks 1 (disp), 2 (imm), then 3 (both).
    # A dynamic immediate can change the slot width. Keep that width in the
    # lookup key even when the immediate's value is erased.
    partial = []
    for kind, fields in PARTIAL_FIELDS.items():
        table = {}
        for r, index in rows(kind):
            data, = r["operands"]
            op = Operand(**{**data, **dict.fromkeys(fields, 0)})
            table[r["originalId"], r["slots"], op.encoding, op.ea_desc, op.disp, op.imm] = index
        if table:
            partial.append(("disp" in fields, "imm" in fields, table))

    @lru_cache(maxsize=131072)
    def scalar_matches(op_id, op):
        if not op_id or op is None:
            return ()
        op = op.for_specialization(op_id in relative)
        found = []
        index = exact.get((op_id, op))
        if index is not None:
            found.append(index)
        slots = data_operand_slots(op)
        for omit_disp, omit_imm, table in partial:
            key = op_id, slots, op.encoding, op.ea_desc, 0 if omit_disp else op.disp, 0 if omit_imm else op.imm
            index = table.get(key)
            if index is not None:
                found.append(index)
        if op.meta & HAS_MODRM:
            mask = masks[op_id]
            key = op_id, slots, op.modrm & mask if op.modrm < 0xc0 or not mask else op.modrm
            if op_id in policy_args:
                args = policy_args[op_id]
                width = (16 if op.prefixes & OPSIZE_PREFIX else 32) if "$width" in args else None
                flags = ("dead" if op.meta & NO_FLAGS else "live") if "$flags" in args else None
                index = policy.get((*key, width, flags))
                if index is not None:
                    found.append(index)
            index = modrm.get(key)
            if index is not None:
                found.append(index)
        return tuple(found)

    for case in cases:
        case_id = case["id"]
        for count, ops in unfused_blocks(captures / case_id / "blocks.bin", handlers):
            matches = [scalar_matches(op_id, op) for op_id, op in ops]
            if fusion:
                # sequences() enumerates increasing widths at each start and
                # stops at control flow. Native lookup tries longer matches first.
                for start, key in sequences(ops, handlers, relative):
                    index = fusion.get(key)
                    if index is not None:
                        matches[start] = (index, *matches[start])
            yield case_id, count, tuple(matches)
