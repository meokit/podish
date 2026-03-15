import argparse
import glob
import json
import os


def read_analysis(input_path):
    with open(input_path, "r", encoding="utf-8") as f:
        return json.load(f)


def get_imm_value(op):
    imm = op.get("imm")
    if isinstance(imm, int):
        return f"0x{imm:x}"
    if isinstance(imm, str):
        return imm
    imm_hex = op.get("imm_hex")
    if isinstance(imm_hex, str):
        return imm_hex
    return "0"


def generate_trie_node(blocks, depth=0):
    relevant_blocks = [b for b in blocks if len(b["ops"]) > depth]
    exact_match = [b for b in blocks if len(b["ops"]) == depth]

    indent = "    " * (depth + 1)
    code = ""

    if relevant_blocks:
        groups = {}
        for block in relevant_blocks:
            op = block["ops"][depth]
            key = (op["symbol"], get_imm_value(op))
            groups.setdefault(key, []).append(block)

        first = True
        for (symbol, imm_value), sub_blocks in groups.items():
            prefix = "if" if first else "else if"
            target = f"(void*)DispatchWrapper<fiberish::op::{symbol}>"
            code += (
                f"{indent}{prefix} ((void*)ops[{depth}].handler == {target} && "
                f"GetImm(&ops[{depth}]) == {imm_value}) {{\n"
            )
            code += generate_trie_node(sub_blocks, depth + 1)
            code += f"{indent}}}\n"
            first = False

        code += f"{indent}else {{ return nullptr; }}\n"
    elif exact_match:
        code += f"{indent}return JitBlock_{exact_match[0]['index']};\n"
    else:
        code += f"{indent}return nullptr;\n"

    return code


def build_cpp(blocks):
    impl_includes = ""
    ops_dir = os.path.join("libfibercpu", "ops")
    if os.path.exists(ops_dir):
        for file_path in sorted(glob.glob(os.path.join(ops_dir, "*_impl.h"))):
            impl_includes += f'#include "../ops/{os.path.basename(file_path)}"\n'

    trie_code = generate_trie_node(blocks)
    declarations = "\n".join(
        f"ATTR_PRESERVE_NONE int64_t JitBlock_{i}(EmuState* RESTRICT state, DecodedOp* RESTRICT op, "
        f"int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch, uint64_t flags_cache);"
        for i in range(len(blocks))
    )

    cpp = f"""#include "jit_ops.h"
#include "../ops.h"
#include "../dispatch.h"
#include "../state.h"
{impl_includes}

namespace fiberish {{

static FORCE_INLINE ATTR_PRESERVE_NONE int64_t JitHandleFlow(LogicFlow flow, EmuState* state, DecodedOp* op,
                                                             int64_t instr_limit, mem::MicroTLB utlb,
                                                             uint32_t branch, uint64_t flags_cache) {{
    switch (flow) {{
        case LogicFlow::Continue:
            if (auto* next_op = NextOp(op)) {{
                ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
            }}
            __builtin_unreachable();
        case LogicFlow::ContinueSkipOne:
            if (auto* next_op = NextOp(NextOp(op))) {{
                ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
            }}
            __builtin_unreachable();
        case LogicFlow::ExitOnCurrentEIP:
            RecordBlockHandlersThrough(state, op);
            CommitFlagsCache(state, flags_cache);
            if (!state->eip_dirty) state->sync_eip_to_op_start(op);
            return instr_limit;
        case LogicFlow::ExitOnNextEIP:
            RecordBlockHandlersThrough(state, op);
            CommitFlagsCache(state, flags_cache);
            if (!state->eip_dirty) state->sync_eip_to_op_end(op);
            return instr_limit;
        case LogicFlow::ExitWithoutSyncEIP:
            RecordBlockHandlersThrough(state, op);
            CommitFlagsCache(state, flags_cache);
            return instr_limit;
        case LogicFlow::RestartMemoryOp:
            RecordBlockHandlersThrough(state, op);
            ATTR_MUSTTAIL return MemoryOpRestart(state, op, instr_limit, utlb, branch, flags_cache);
        case LogicFlow::RetryMemoryOp:
            RecordBlockHandlersThrough(state, op);
            ATTR_MUSTTAIL return MemoryOpRetry(state, op, instr_limit, utlb, branch, flags_cache);
        case LogicFlow::ExitToBranch:
            RecordBlockHandlersThrough(state, op);
            ATTR_MUSTTAIL return ResolveBranchTarget(state, op, instr_limit, utlb, branch, flags_cache);
        case LogicFlow::ExitToNextOpBranch:
            RecordBlockHandlersThrough(state, op);
            ATTR_MUSTTAIL return ResolveBranchTarget(state, NextOp(op), instr_limit, utlb, branch, flags_cache);
        default:
            CommitFlagsCache(state, flags_cache);
            return instr_limit;
    }}
}}

{declarations}

HandlerFunc FindJitBlock(const DecodedOp* ops) {{
    if (!ops) return nullptr;
{trie_code}
}}

"""

    for block in blocks:
        index = block["index"]
        ops = block["ops"]
        cpp += f"// Block {index} | Exec: {block['exec_count']}\n"
        cpp += (
            f"ATTR_PRESERVE_NONE int64_t JitBlock_{index}(EmuState* RESTRICT state, DecodedOp* RESTRICT op, "
            f"int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch, uint64_t flags_cache) {{\n"
        )

        if ops:
            cpp += "    LogicFlow err_flow;\n"
            cpp += "    DecodedOp* err_op;\n"

        for offset, op in enumerate(ops):
            symbol = op["symbol"]
            imm_value = get_imm_value(op)
            cpp += (
                f"    {{ auto flow = fiberish::op::{symbol}(state, &op[{offset}], &utlb, {imm_value}, &branch, "
                f"flags_cache);\n"
            )
            cpp += (
                f"      if (flow != LogicFlow::Continue) [[unlikely]] {{ err_flow = flow; err_op = &op[{offset}]; "
                f"goto handle_flow; }} }}\n"
            )

        cpp += f"    ATTR_MUSTTAIL return ExitBlock(state, op + {len(ops)}, instr_limit, utlb, branch, flags_cache);\n"

        if ops:
            cpp += "handle_flow:\n"
            cpp += "    return JitHandleFlow(err_flow, state, err_op, instr_limit, utlb, branch, flags_cache);\n"

        cpp += "}\n\n"

    cpp += "}  // namespace fiberish\n"
    return cpp


def main():
    parser = argparse.ArgumentParser(description="Generate JIT block matcher from block analysis JSON")
    parser.add_argument("--input", "-i", default="blocks_analysis.json", help="Path to blocks analysis JSON")
    parser.add_argument("--output", "-o", default="libfibercpu/generated/jit_ops.cpp",
                        help="Path to generated jit_ops.cpp")
    args = parser.parse_args()

    if not os.path.exists(args.input):
        print(f"{args.input} not found")
        return

    data = read_analysis(args.input)
    blocks = data.get("blocks", [])
    for index, block in enumerate(blocks):
        block["index"] = index

    cpp = build_cpp(blocks)
    os.makedirs(os.path.dirname(args.output), exist_ok=True)
    with open(args.output, "w", encoding="utf-8") as f:
        f.write(cpp)


if __name__ == "__main__":
    main()
