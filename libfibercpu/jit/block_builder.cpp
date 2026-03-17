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
constexpr uint32_t kHandlerTailVeneerSize = 10 * sizeof(uint32_t);

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

const char* JitDumpDir() {
    static const char* dir = std::getenv("FIBERCPU_JIT_DUMP_DIR");
    return (dir && dir[0] != '\0') ? dir : nullptr;
}

bool ParseHexOrDec(const char* text, uint32_t* out) {
    if (!text || !text[0]) return false;
    char* end = nullptr;
    unsigned long value = std::strtoul(text, &end, 0);
    if (end == text || *end != '\0') return false;
    *out = static_cast<uint32_t>(value);
    return true;
}

bool ShouldDumpBlock(uint32_t start_eip) {
    const char* filter = std::getenv("FIBERCPU_JIT_DUMP_EIP");
    if (!filter || filter[0] == '\0') return true;
    uint32_t expected = 0;
    return ParseHexOrDec(filter, &expected) && expected == start_eip;
}

bool IsContinueTargetName(const char* target_name) {
    return target_name && std::strstr(target_name, "JitContinueTarget") != nullptr &&
           std::strstr(target_name, "JitContinueSkipOneTarget") == nullptr;
}

bool CanTrimTrailingContinue(const StencilDesc& desc, uint32_t inst_index, uint32_t inst_count) {
    if (inst_index + 1 >= inst_count) return false;
    if (desc.branch_reloc_count != 1) return false;
    const BranchRelocDesc& reloc = desc.branch_relocs[0];
    if (reloc.offset + sizeof(uint32_t) != desc.code_size) return false;
    return IsContinueTargetName(generated::branch_reloc_target_names[reloc.target_id]);
}

struct VeneerKey {
    uint64_t target_addr;
    uint64_t aux_addr;
    uint8_t kind;

    bool operator==(const VeneerKey& other) const = default;
};

struct VeneerKeyHash {
    size_t operator()(const VeneerKey& key) const {
        uint64_t hash = key.target_addr;
        hash ^= key.aux_addr + 0x9e3779b97f4a7c15ull + (hash << 6) + (hash >> 2);
        hash ^= static_cast<uint64_t>(key.kind) + 0x9e3779b97f4a7c15ull + (hash << 6) + (hash >> 2);
        return static_cast<size_t>(hash);
    }
};

void DumpJitBlock(uint32_t start_eip, const uint8_t* code, size_t size) {
    const char* dump_dir = JitDumpDir();
    if (!dump_dir || !ShouldDumpBlock(start_eip)) return;

    char path[1024];
    std::snprintf(path, sizeof(path), "%s/jit_%08x.bin", dump_dir, start_eip);
    FILE* fp = std::fopen(path, "wb");
    if (!fp) {
        if (JitDebugEnabled()) {
            JitDebugLog("[jit] dump failed start=%08x path=%s\n", start_eip, path);
        }
        return;
    }
    std::fwrite(code, 1, size, fp);
    std::fclose(fp);
    if (JitDebugEnabled()) {
        JitDebugLog("[jit] dumped block start=%08x path=%s size=%zu\n", start_eip, path, size);
    }
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

void EmitHandlerTailVeneer(uint32_t* veneer_ptr, uint64_t op_addr, uint64_t handler_addr) {
    veneer_ptr[0] = 0xD2800001;  // movz x1, #0
    veneer_ptr[1] = 0xF2A00001;  // movk x1, #0, lsl #16
    veneer_ptr[2] = 0xF2C00001;  // movk x1, #0, lsl #32
    veneer_ptr[3] = 0xF2E00001;  // movk x1, #0, lsl #48
    veneer_ptr[4] = 0xD2800010;  // movz x16, #0
    veneer_ptr[5] = 0xF2A00010;  // movk x16, #0, lsl #16
    veneer_ptr[6] = 0xF2C00010;  // movk x16, #0, lsl #32
    veneer_ptr[7] = 0xF2E00010;  // movk x16, #0, lsl #48
    veneer_ptr[8] = 0xD61F0200;  // br x16
    veneer_ptr[9] = 0xD503201F;  // nop
    PatchMovImm64(veneer_ptr, op_addr);
    PatchMovImm64(veneer_ptr + 4, handler_addr);
}

}  // namespace

ATTR_PRESERVE_NONE int64_t JitContinueTarget(EmuState*, DecodedOp*, int64_t, mem::MicroTLB, uint32_t, uint64_t) {
    std::abort();
}

ATTR_PRESERVE_NONE int64_t JitContinueSkipOneTarget(EmuState*, DecodedOp*, int64_t, mem::MicroTLB, uint32_t, uint64_t) {
    std::abort();
}

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
    std::vector<uint32_t> emitted_sizes;
    stencil_ids.reserve(bb->inst_count);
    emitted_sizes.reserve(bb->inst_count);

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
            JitDebugLog("[jit]   op[%u] handler=%p sid=%u name=%s code=%u patches=%u branch_relocs=%u\n", i,
                        reinterpret_cast<void*>(op->handler), sid, generated::stencil_names[sid],
                        generated::stencils[sid].code_size, generated::stencils[sid].patch_count,
                        generated::stencils[sid].branch_reloc_count);
        }

        stencil_ids.push_back(sid);
        const StencilDesc& desc = generated::stencils[sid];
        uint32_t emitted_size = desc.code_size;
        uint16_t effective_reloc_count = desc.branch_reloc_count;
        if (CanTrimTrailingContinue(desc, i, bb->inst_count)) {
            emitted_size -= sizeof(uint32_t);
            effective_reloc_count -= 1;
        }
        emitted_sizes.push_back(emitted_size);
        estimated_size += emitted_size;
        branch_reloc_count += effective_reloc_count;
    }

    // Add extra for trampoline (ret)
    estimated_size += 32;
    estimated_size += branch_reloc_count * kHandlerTailVeneerSize;

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
    std::vector<uint8_t*> stencil_starts(bb->inst_count);
    uint8_t* layout_ptr = start_ptr;
    for (uint32_t i = 0; i < bb->inst_count; ++i) {
        stencil_starts[i] = layout_ptr;
        layout_ptr += emitted_sizes[i];
    }
    uint8_t* current_ptr = start_ptr;
    uint8_t* veneer_ptr = layout_ptr;
    veneer_ptr += sizeof(uint32_t);
    ankerl::unordered_dense::map<VeneerKey, uint8_t*, VeneerKeyHash> veneer_cache;

    for (uint32_t i = 0; i < bb->inst_count; ++i) {
        uint16_t sid = stencil_ids[i];
        const StencilDesc& desc = generated::stencils[sid];
        DecodedOp* op = bb->FirstOp() + i;
        const bool trim_trailing_continue = CanTrimTrailingContinue(desc, i, bb->inst_count);
        const uint32_t copy_size = emitted_sizes[i];

        // Copy code
        std::memcpy(current_ptr, desc.code, copy_size);

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
            if (trim_trailing_continue && reloc.offset + sizeof(uint32_t) == desc.code_size) {
                continue;
            }
            uint32_t* branch_ptr = reinterpret_cast<uint32_t*>(current_ptr + reloc.offset);
            uint64_t target_addr = reinterpret_cast<uint64_t>(generated::branch_reloc_targets[reloc.target_id]);
            const char* target_name = generated::branch_reloc_target_names[reloc.target_id];

            if (target_name && (std::strstr(target_name, "JitContinueTarget") ||
                                std::strstr(target_name, "JitContinueSkipOneTarget"))) {
                const uint32_t skip = (std::strstr(target_name, "JitContinueSkipOneTarget") != nullptr) ? 2u : 1u;
                const uint32_t target_index = i + skip;
                if (target_index < bb->inst_count) {
                    PatchBranch26(branch_ptr, stencil_starts[target_index]);
                } else {
                    DecodedOp* target_op = bb->FirstOp() + target_index;
                    VeneerKey key{
                        .target_addr = reinterpret_cast<uint64_t>(target_op->handler),
                        .aux_addr = reinterpret_cast<uint64_t>(target_op),
                        .kind = 2,
                    };
                    uint8_t*& stub_start = veneer_cache[key];
                    if (stub_start == nullptr) {
                        stub_start = veneer_ptr;
                        uint32_t* stub_ptr = reinterpret_cast<uint32_t*>(veneer_ptr);
                        EmitHandlerTailVeneer(stub_ptr, reinterpret_cast<uint64_t>(target_op),
                                              reinterpret_cast<uint64_t>(target_op->handler));
                        veneer_ptr += kHandlerTailVeneerSize;
                    }
                    PatchBranch26(branch_ptr, stub_start);
                }
            } else {
                const bool is_call = ((*branch_ptr & kBranchOpcodeMask) == kBranchLinkOpcode);
                VeneerKey key{
                    .target_addr = target_addr,
                    .aux_addr = 0,
                    .kind = static_cast<uint8_t>(is_call ? 0 : 1),
                };
                uint8_t*& stub_start = veneer_cache[key];
                if (stub_start == nullptr) {
                    stub_start = veneer_ptr;
                    uint32_t* stub_ptr = reinterpret_cast<uint32_t*>(veneer_ptr);
                    if (is_call) {
                        EmitCallVeneer(stub_ptr, target_addr);
                        veneer_ptr += kCallVeneerSize;
                    } else {
                        EmitJumpVeneer(stub_ptr, target_addr);
                        veneer_ptr += kJumpVeneerSize;
                    }
                }
                PatchBranch26(branch_ptr, stub_start);
            }
        }

        current_ptr += copy_size;
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
    DumpJitBlock(bb->chain.start_eip, start_ptr, jcb->code_size);

    if (JitDebugEnabled()) {
        JitDebugLog("[jit] compiled block start=%08x code=%p size=%zu branch_relocs=%zu\n", bb->chain.start_eip,
                    reinterpret_cast<void*>(start_ptr), jcb->code_size, branch_reloc_count);
    }

    return jcb;
}

}  // namespace jit
}  // namespace fiberish
