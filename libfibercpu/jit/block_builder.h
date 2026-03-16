#pragma once

#include <ankerl/unordered_dense.h>
#include <vector>
#include "stencil.h"

namespace fiberish {

struct BasicBlock;

namespace jit {

struct JitCodeBlock {
    void* entry;
    size_t code_size;
    BasicBlock* owner;
};

class BlockBuilder {
public:
    static BlockBuilder& Get();

    // Compiles a BasicBlock into a JitCodeBlock.
    // Returns nullptr if the block cannot be JITed (e.g. unsupported ops).
    JitCodeBlock* CompileBlock(BasicBlock* bb);

private:
    BlockBuilder();
    ~BlockBuilder();

    void InitializeMapping();
    uint16_t LookupStencil(HandlerFunc target);

    void* m_code_buffer;
    size_t m_buffer_size;
    size_t m_buffer_offset;

    ankerl::unordered_dense::map<uintptr_t, uint16_t> m_handler_map;
    bool m_initialized = false;
};

}  // namespace jit
}  // namespace fiberish
