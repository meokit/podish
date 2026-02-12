import json
import os
import glob

def generate_trie_node(blocks, depth=0):
    """递归生成嵌套的 if-else 查找逻辑，解决常量值报错问题"""
    relevant_blocks = [b for b in blocks if len(b['ops']) > depth]
    exact_match = [b for b in blocks if len(b['ops']) == depth]

    indent = "    " * (depth + 2)
    code = ""

    if relevant_blocks:
        # 按照当前深度的 Op 符号进行分组
        groups = {}
        for b in relevant_blocks:
            sym = b['ops'][depth]['symbol']
            groups.setdefault(sym, []).append(b)
        
        # 逐个生成 if / else if
        first = True
        for sym, sub_blocks in groups.items():
            prefix = "if" if first else "else if"
            target = f"(void*)DispatchWrapper<fiberish::op::{sym}>"
            
            code += f"{indent}{prefix} (handlers[{depth}] == {target}) {{\n"
            code += generate_trie_node(sub_blocks, depth + 1)
            code += f"{indent}}}\n"
            first = False
        
        # 处理如果不匹配的情况
        code += f"{indent}else {{ return nullptr; }}\n"
        
    elif exact_match:
        idx = exact_match[0]['index']
        code += f"{indent}return JitBlock_{idx};\n"
    else:
        code += f"{indent}return nullptr;\n"
    
    return code

def main():
    if not os.path.exists('blocks_analysis.json'):
        print("blocks_analysis.json not found")
        return

    with open('blocks_analysis.json', 'r') as f:
        data = json.load(f)
    
    blocks = []
    for i, b in enumerate(data['blocks']):
        b['index'] = i
        blocks.append(b)

    # 2. 生成 CPP
    impl_includes = ""
    ops_dir = os.path.join("libfibercpu", "ops")
    if os.path.exists(ops_dir):
        for file_path in sorted(glob.glob(os.path.join(ops_dir, "*_impl.h"))):
            impl_includes += f'#include "../ops/{os.path.basename(file_path)}"\n'

    # 生成查找树
    trie_code = generate_trie_node(blocks)

    cpp = f"""#include "jit_ops.h"
#include "../ops.h"
#include "../dispatch.h"
#include "../state.h"
{impl_includes}

namespace fiberish {{

// Flow Handler
FORCE_INLINE int64_t JitHandleFlow(LogicFlow flow, EmuState* state, DecodedOp* op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch) {{
    switch (flow) {{
        case LogicFlow::ExitOnCurrentEIP: if (!state->eip_dirty) state->sync_eip_to_op_start(reinterpret_cast<ShimOp*>(op)); return instr_limit;
        case LogicFlow::ExitOnNextEIP: if (!state->eip_dirty) state->sync_eip_to_op_end(reinterpret_cast<ShimOp*>(op)); return instr_limit;
        case LogicFlow::ExitWithoutSyncEIP: return instr_limit;
        case LogicFlow::RestartMemoryOp: return MemoryOpRestart(state, op, instr_limit, utlb, branch);
        case LogicFlow::RetryMemoryOp: return MemoryOpRetry(state, op, instr_limit, utlb, branch);
        default: return instr_limit;
    }}
}}

// Forward Declarations
{" ".join([f"ATTR_PRESERVE_NONE int64_t JitBlock_{i}(EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch);" for i in range(len(blocks))])}

HandlerFunc FindJitBlock(const std::vector<void*>& handlers) {{
    if (handlers.empty()) return nullptr;
{trie_code}
}}

"""
    # 3. 块实现
    for b in blocks:
        i = b['index']
        ops = b['ops']
        cpp += f"// Block {i} | Exec: {b['exec_count']}\n"
        cpp += f"ATTR_PRESERVE_NONE int64_t JitBlock_{i}(EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch) {{\n"
        for j, op in enumerate(ops):
            sym = op['symbol']
            cpp += f"    {{ auto flow = fiberish::op::{sym}(state, reinterpret_cast<ShimOp*>(&op[{j}]), &utlb, op[{j}].imm, &branch);\n"
            cpp += f"      if (flow != LogicFlow::Continue) return JitHandleFlow(flow, state, &op[{j}], instr_limit, utlb, branch); }}\n"
        cpp += f"    ATTR_MUSTTAIL return ExitBlock(state, op + {len(ops)}, instr_limit, utlb, branch);\n"
        cpp += "}\n\n"

    cpp += "} // namespace fiberish\n"

    with open('libfibercpu/generated/jit_ops.cpp', 'w') as f:
        f.write(cpp)

if __name__ == "__main__":
    main()