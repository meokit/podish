#include "block_builder.h"
#include <libkern/OSCacheControl.h>
#include <pthread.h>
#include <sys/mman.h>
#include <unistd.h>
#include <cstdarg>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include "../decoder.h"
#include "../generated/stencils.generated.inc"
#include "../ops.h"
#include "../state.h"
#include "stencil.h"

namespace fiberish {

namespace jit {

namespace {
constexpr uint32_t kBranchOpcodeMask = 0xFC000000;
constexpr uint32_t kBranchLinkOpcode = 0x94000000;
constexpr uint32_t kCallVeneerSize = 8 * sizeof(uint32_t);
constexpr uint32_t kJumpVeneerSize = 6 * sizeof(uint32_t);

bool JitDebugEnabled() {
    static bool enabled = [] {
        const char* value = std::getenv("FIBERCPU_JIT_DEBUG");
        return value != nullptr && value[0] != '\0' && value[0] != '0';
    }();
    return enabled;
}

void JitDebugLog(const char* fmt, ...) {
    if (!JitDebugEnabled()) return;
    FILE* fp = std::fopen("/tmp/fibercpu_jit.log", "a");
    if (!fp) return;
    va_list args;
    va_start(args, fmt);
    std::vfprintf(fp, fmt, args);
    va_end(args);
    std::fclose(fp);
}

uint64_t GetDecodedOpQword(const DecodedOp* op, uint16_t qword_index) {
    if (qword_index >= (sizeof(DecodedOp) / sizeof(uint64_t))) {
        return 0;
    }
    const auto* words = reinterpret_cast<const uint64_t*>(op);
    return words[qword_index];
}

void PatchMovImm64(uint32_t* patch_ptr, uint64_t patch_val) {
    uint32_t imm16_0 = static_cast<uint32_t>(patch_val & 0xFFFF);
    uint32_t imm16_1 = static_cast<uint32_t>((patch_val >> 16) & 0xFFFF);
    uint32_t imm16_2 = static_cast<uint32_t>((patch_val >> 32) & 0xFFFF);
    uint32_t imm16_3 = static_cast<uint32_t>((patch_val >> 48) & 0xFFFF);
    patch_ptr[0] = (patch_ptr[0] & ~(0xFFFFu << 5)) | (imm16_0 << 5);
    patch_ptr[1] = (patch_ptr[1] & ~(0xFFFFu << 5)) | (imm16_1 << 5);
    patch_ptr[2] = (patch_ptr[2] & ~(0xFFFFu << 5)) | (imm16_2 << 5);
    patch_ptr[3] = (patch_ptr[3] & ~(0xFFFFu << 5)) | (imm16_3 << 5);
}

void PatchBranch26(uint32_t* patch_ptr, const uint8_t* target_ptr) {
    const auto patch_addr = reinterpret_cast<const uint8_t*>(patch_ptr);
    intptr_t delta = target_ptr - patch_addr;
    intptr_t imm26 = delta >> 2;
    uint32_t opcode = *patch_ptr & kBranchOpcodeMask;
    *patch_ptr = opcode | (static_cast<uint32_t>(imm26) & 0x03FFFFFFu);
}

void EmitCallVeneer(uint32_t* veneer_ptr, uint64_t target_addr) {
    veneer_ptr[0] = 0xF81F0FFE;  // str x30, [sp, #-16]!
    veneer_ptr[1] = 0xD2800010;  // movz x16, #0
    veneer_ptr[2] = 0xF2A00010;  // movk x16, #0, lsl #16
    veneer_ptr[3] = 0xF2C00010;  // movk x16, #0, lsl #32
    veneer_ptr[4] = 0xF2E00010;  // movk x16, #0, lsl #48
    veneer_ptr[5] = 0xD63F0200;  // blr x16
    veneer_ptr[6] = 0xF84107FE;  // ldr x30, [sp], #16
    veneer_ptr[7] = 0xD65F03C0;  // ret
    PatchMovImm64(veneer_ptr + 1, target_addr);
}

void EmitJumpVeneer(uint32_t* veneer_ptr, uint64_t target_addr) {
    veneer_ptr[0] = 0xD2800010;  // movz x16, #0
    veneer_ptr[1] = 0xF2A00010;  // movk x16, #0, lsl #16
    veneer_ptr[2] = 0xF2C00010;  // movk x16, #0, lsl #32
    veneer_ptr[3] = 0xF2E00010;  // movk x16, #0, lsl #48
    veneer_ptr[4] = 0xD61F0200;  // br x16
    veneer_ptr[5] = 0xD503201F;  // nop
    PatchMovImm64(veneer_ptr, target_addr);
}

}  // namespace

BlockBuilder& BlockBuilder::Get() {
    static BlockBuilder instance;
    return instance;
}

BlockBuilder::BlockBuilder() {
    m_buffer_size = 64 * 1024 * 1024;  // 64MB code cache
    m_code_buffer =
        mmap(nullptr, m_buffer_size, PROT_READ | PROT_WRITE | PROT_EXEC, MAP_ANON | MAP_PRIVATE | MAP_JIT, -1, 0);
    m_buffer_offset = 0;
    InitializeMapping();
}

BlockBuilder::~BlockBuilder() {
    if (m_code_buffer != MAP_FAILED) {
        munmap(m_code_buffer, m_buffer_size);
    }
}

void BlockBuilder::InitializeMapping() {
    for (size_t i = 0; i < generated::handler_to_stencil_count; ++i) {
        m_handler_map[reinterpret_cast<uintptr_t>(generated::handler_to_stencil[i].target)] =
            generated::handler_to_stencil[i].stencil_id;
    }
    m_initialized = true;
}

uint16_t BlockBuilder::LookupStencil(HandlerFunc target) {
    auto it = m_handler_map.find(reinterpret_cast<uintptr_t>(target));
    if (it != m_handler_map.end()) {
        return it->second;
    }
    return 0xFFFF;
}

JitCodeBlock* BlockBuilder::CompileBlock(BasicBlock* bb) {
    if (m_code_buffer == MAP_FAILED) return nullptr;

    if (JitDebugEnabled()) {
        JitDebugLog("[jit] compile block start=%08x insts=%u entry=%p\n", bb->chain.start_eip, bb->inst_count,
                    reinterpret_cast<void*>(bb->entry));
    }

    // Estimate size
    size_t estimated_size = 0;
    size_t branch_reloc_count = 0;
    std::vector<uint16_t> stencil_ids;
    stencil_ids.reserve(bb->inst_count);

    for (uint32_t i = 0; i < bb->inst_count; ++i) {
        DecodedOp* op = bb->FirstOp() + i;
        uint16_t sid = LookupStencil(op->handler);
        if (sid == 0xFFFF) {
            if (JitDebugEnabled()) {
                JitDebugLog("[jit] missing stencil block=%08x op_index=%u handler=%p next_eip=%08x meta=%02x\n",
                            bb->chain.start_eip, i, reinterpret_cast<void*>(op->handler), op->next_eip, op->meta.all);
            }
            return nullptr;  // Stencil not available
        }

        if (JitDebugEnabled()) {
            JitDebugLog("[jit]   op[%u] handler=%p sid=%u code=%u patches=%u branch_relocs=%u\n", i,
                        reinterpret_cast<void*>(op->handler), sid, generated::stencils[sid].code_size,
                        generated::stencils[sid].patch_count, generated::stencils[sid].branch_reloc_count);
        }

        stencil_ids.push_back(sid);
        estimated_size += generated::stencils[sid].code_size;
        branch_reloc_count += generated::stencils[sid].branch_reloc_count;
    }

    // Add extra for trampoline (ret)
    estimated_size += 32;
    estimated_size += branch_reloc_count * kCallVeneerSize;

    if (m_buffer_offset + estimated_size > m_buffer_size) {
        if (JitDebugEnabled()) {
            JitDebugLog("[jit] code cache full block=%08x need=%zu have=%zu/%zu\n", bb->chain.start_eip, estimated_size,
                        m_buffer_offset, m_buffer_size);
        }
        return nullptr;  // Buffer full
    }

    // Switch to RW for Apple Silicon
    pthread_jit_write_protect_np(0);

    uint8_t* start_ptr = (uint8_t*)m_code_buffer + m_buffer_offset;
    uint8_t* current_ptr = start_ptr;
    uint8_t* veneer_ptr = start_ptr;
    for (uint32_t i = 0; i < bb->inst_count; ++i) {
        veneer_ptr += generated::stencils[stencil_ids[i]].code_size;
    }
    veneer_ptr += sizeof(uint32_t);

    for (uint32_t i = 0; i < bb->inst_count; ++i) {
        uint16_t sid = stencil_ids[i];
        const StencilDesc& desc = generated::stencils[sid];
        DecodedOp* op = bb->FirstOp() + i;

        // Copy code
        std::memcpy(current_ptr, desc.code, desc.code_size);

        // Apply Patches
        for (uint16_t p = 0; p < desc.patch_count; ++p) {
            const PatchDesc& patch = desc.patches[p];
            uint32_t* patch_ptr = (uint32_t*)(current_ptr + patch.offset);
            switch (patch.kind) {
                case PatchKind::OpQword64:
                    PatchMovImm64(patch_ptr, GetDecodedOpQword(op, patch.aux));
                    break;
            }
        }

        for (uint16_t r = 0; r < desc.branch_reloc_count; ++r) {
            const BranchRelocDesc& reloc = desc.branch_relocs[r];
            uint32_t* branch_ptr = reinterpret_cast<uint32_t*>(current_ptr + reloc.offset);
            uint8_t* stub_start = veneer_ptr;
            uint32_t* stub_ptr = reinterpret_cast<uint32_t*>(veneer_ptr);
            uint64_t target_addr = reinterpret_cast<uint64_t>(generated::branch_reloc_targets[reloc.target_id]);
            if ((*branch_ptr & kBranchOpcodeMask) == kBranchLinkOpcode) {
                EmitCallVeneer(stub_ptr, target_addr);
                veneer_ptr += kCallVeneerSize;
            } else {
                EmitJumpVeneer(stub_ptr, target_addr);
                veneer_ptr += kJumpVeneerSize;
            }
            PatchBranch26(branch_ptr, stub_start);
        }

        current_ptr += desc.code_size;
    }

    // Add termination (ret)
    // ret = 0xd65f03c0
    *(uint32_t*)current_ptr = 0xd65f03c0;
    current_ptr += 4;

    current_ptr = veneer_ptr;

    m_buffer_offset = (current_ptr - (uint8_t*)m_code_buffer + 15) & ~15;  // Align 16

    // Switch back to RX
    pthread_jit_write_protect_np(1);
    sys_icache_invalidate(start_ptr, current_ptr - start_ptr);

    JitCodeBlock* jcb = new JitCodeBlock();
    jcb->entry = start_ptr;
    jcb->code_size = current_ptr - start_ptr;
    jcb->owner = bb;
    bb->jit_code = start_ptr;

    if (JitDebugEnabled()) {
        JitDebugLog("[jit] compiled block start=%08x code=%p size=%zu branch_relocs=%zu\n", bb->chain.start_eip,
                    reinterpret_cast<void*>(start_ptr), jcb->code_size, branch_reloc_count);
    }

    return jcb;
}

}  // namespace jit
}  // namespace fiberish
