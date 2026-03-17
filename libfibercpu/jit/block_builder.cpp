#include "block_builder.h"
#include <inttypes.h>
#include <libkern/OSCacheControl.h>
#include <pthread.h>
#include <sys/mman.h>
#include <unistd.h>
#include <cstdarg>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <memory_resource>
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
constexpr uint32_t kAddImmMask = 0x7F000000;
constexpr uint32_t kAddImmOpcode = 0x91000000;
constexpr uint32_t kAdrpMask = 0x9F000000;
constexpr uint32_t kAdrpOpcode = 0x90000000;
constexpr uint32_t kCallVeneerSize = 6 * sizeof(uint32_t);
constexpr uint32_t kJumpVeneerSize = 6 * sizeof(uint32_t);
constexpr uint32_t kHandlerTailVeneerSize = 10 * sizeof(uint32_t);

class JitCodeResource final : public std::pmr::memory_resource {
public:
    JitCodeResource(void* buffer, size_t size) : m_begin(static_cast<std::byte*>(buffer)), m_end(m_begin + size) {}

    void* try_allocate(size_t bytes, size_t alignment) {
        uintptr_t current = reinterpret_cast<uintptr_t>(m_current);
        uintptr_t aligned = (current + alignment - 1) & ~(static_cast<uintptr_t>(alignment) - 1);
        std::byte* next = reinterpret_cast<std::byte*>(aligned + bytes);
        if (next < reinterpret_cast<std::byte*>(aligned) || next > m_end) {
            return nullptr;
        }
        m_current = next;
        return reinterpret_cast<void*>(aligned);
    }

protected:
    void* do_allocate(size_t bytes, size_t alignment) override {
        void* ptr = try_allocate(bytes, alignment);
        if (!ptr) std::abort();
        return ptr;
    }

    void do_deallocate(void*, size_t, size_t) override {}

    bool do_is_equal(const std::pmr::memory_resource& other) const noexcept override { return this == &other; }

private:
    std::byte* m_begin;
    std::byte* m_current = m_begin;
    std::byte* m_end;
};

bool JitDebugEnabled() {
#ifdef FIBERCPU_ENABLE_JIT_DEBUG_LOG
    static bool enabled = [] {
        const char* value = std::getenv("FIBERCPU_JIT_DEBUG");
        return value != nullptr && value[0] != '\0' && value[0] != '0';
    }();
    return enabled;
#else
    return false;
#endif
}

void JitDebugLog(const char* fmt, ...) {
#ifdef FIBERCPU_ENABLE_JIT_DEBUG_LOG
    if (!JitDebugEnabled()) return;
    FILE* fp = std::fopen("/tmp/fibercpu_jit.log", "a");
    if (!fp) return;
    va_list args;
    va_start(args, fmt);
    std::vfprintf(fp, fmt, args);
    va_end(args);
    std::fclose(fp);
#else
    (void)fmt;
#endif
}

const char* JitDumpDir() {
    static const char* dir = std::getenv("FIBERCPU_JIT_DUMP_DIR");
    return (dir && dir[0] != '\0') ? dir : nullptr;
}

const char* JitProfileMapDir() {
    static const char* dir = [] {
        const char* explicit_dir = std::getenv("FIBERCPU_JIT_PROFILE_MAP_DIR");
        if (explicit_dir && explicit_dir[0] != '\0') return explicit_dir;
        return JitDumpDir();
    }();
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

bool IsInterestingRelocTarget(const char* target_name) {
    if (!target_name) return false;
    return std::strstr(target_name, "JitContinueTarget") != nullptr ||
           std::strstr(target_name, "JitContinueSkipOneTarget") != nullptr ||
           std::strstr(target_name, "JitExitOnCurrentEipTarget") != nullptr ||
           std::strstr(target_name, "JitExitOnNextEipTarget") != nullptr ||
           std::strstr(target_name, "JitExitDefaultTarget") != nullptr ||
           std::strstr(target_name, "JitFaultExitTarget") != nullptr ||
           std::strstr(target_name, "ResolveBranchTarget") != nullptr;
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

void WriteJitProfileMap(uint32_t start_eip, const uint8_t* code, size_t size,
                        const std::vector<uint8_t*>& stencil_starts, const std::vector<uint16_t>& stencil_ids) {
    const char* map_dir = JitProfileMapDir();
    if (!map_dir) return;

    char path[1024];
    std::snprintf(path, sizeof(path), "%s/jit_%08x.map.json", map_dir, start_eip);
    FILE* fp = std::fopen(path, "w");
    if (!fp) return;

    std::fprintf(fp, "{\n");
    std::fprintf(fp, "  \"guest_block_start_eip\": %u,\n", start_eip);
    std::fprintf(fp, "  \"runtime_start\": %" PRIu64 ",\n", static_cast<uint64_t>(reinterpret_cast<uintptr_t>(code)));
    std::fprintf(fp, "  \"code_size\": %zu,\n", size);
    std::fprintf(fp, "  \"ops\": [\n");
    for (size_t i = 0; i < stencil_ids.size(); ++i) {
        const uint64_t runtime_addr = static_cast<uint64_t>(reinterpret_cast<uintptr_t>(stencil_starts[i]));
        const uint64_t offset = static_cast<uint64_t>(stencil_starts[i] - code);
        std::fprintf(
            fp, "    {\"index\": %zu, \"name\": \"%s\", \"runtime_start\": %" PRIu64 ", \"offset\": %" PRIu64 "}%s\n",
            i, generated::stencil_names[stencil_ids[i]], runtime_addr, offset,
            (i + 1 == stencil_ids.size()) ? "" : ",");
    }
    std::fprintf(fp, "  ]\n");
    std::fprintf(fp, "}\n");
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

void PatchAdrp(uint32_t* patch_ptr, const uint8_t* target_ptr) {
    uint32_t inst = *patch_ptr;
    if ((inst & kAdrpMask) != kAdrpOpcode) {
        JitDebugLog("[jit] expected adrp, got 0x%08x at %p\n", inst, reinterpret_cast<void*>(patch_ptr));
        std::abort();
    }
    const uint64_t pc_page = reinterpret_cast<uint64_t>(patch_ptr) & ~0xFFFull;
    const uint64_t target_page = reinterpret_cast<uint64_t>(target_ptr) & ~0xFFFull;
    const int64_t page_delta = static_cast<int64_t>(target_page) - static_cast<int64_t>(pc_page);
    const int64_t imm = page_delta >> 12;
    const uint32_t immlo = static_cast<uint32_t>(imm & 0x3);
    const uint32_t immhi = static_cast<uint32_t>((imm >> 2) & 0x7FFFF);
    inst &= ~((0x3u << 29) | (0x7FFFFu << 5));
    inst |= (immlo << 29) | (immhi << 5);
    *patch_ptr = inst;
}

void PatchAddPageOffset(uint32_t* patch_ptr, const uint8_t* target_ptr) {
    uint32_t inst = *patch_ptr;
    if ((inst & kAddImmMask) != kAddImmOpcode) {
        JitDebugLog("[jit] expected add imm, got 0x%08x at %p\n", inst, reinterpret_cast<void*>(patch_ptr));
        std::abort();
    }
    const uint32_t pageoff = static_cast<uint32_t>(reinterpret_cast<uint64_t>(target_ptr) & 0xFFFu);
    inst &= ~(0xFFFu << 10);
    inst |= (pageoff << 10);
    *patch_ptr = inst;
}

void PatchUnsignedOffset(uint32_t* patch_ptr, const uint8_t* target_ptr, uint8_t shift) {
    uint32_t inst = *patch_ptr;
    const uint32_t pageoff = static_cast<uint32_t>(reinterpret_cast<uint64_t>(target_ptr) & 0xFFFu);
    if ((pageoff & ((1u << shift) - 1u)) != 0) {
        JitDebugLog("[jit] pageoff alignment mismatch shift=%u addr=%p at %p\n", shift,
                    reinterpret_cast<const void*>(target_ptr), reinterpret_cast<void*>(patch_ptr));
        std::abort();
    }
    const uint32_t imm12 = pageoff >> shift;
    inst &= ~(0xFFFu << 10);
    inst |= (imm12 << 10);
    *patch_ptr = inst;
}

void RewriteGotLoadToAdd(uint32_t* patch_ptr, const uint8_t* target_ptr) {
    uint32_t inst = *patch_ptr;
    const uint32_t rn = (inst >> 5) & 0x1F;
    const uint32_t rd = inst & 0x1F;
    uint32_t add_inst = kAddImmOpcode | (rn << 5) | rd;
    const uint32_t pageoff = static_cast<uint32_t>(reinterpret_cast<uint64_t>(target_ptr) & 0xFFFu);
    add_inst |= (pageoff << 10);
    *patch_ptr = add_inst;
}

void EmitCallVeneer(uint32_t* veneer_ptr, uint64_t target_addr) {
    veneer_ptr[0] = 0xD2800010;  // movz x16, #0
    veneer_ptr[1] = 0xF2A00010;  // movk x16, #0, lsl #16
    veneer_ptr[2] = 0xF2C00010;  // movk x16, #0, lsl #32
    veneer_ptr[3] = 0xF2E00010;  // movk x16, #0, lsl #48
    veneer_ptr[4] = 0xD61F0200;  // br x16
    veneer_ptr[5] = 0xD503201F;  // nop
    PatchMovImm64(veneer_ptr, target_addr);
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

ATTR_PRESERVE_NONE int64_t JitContinueTarget(EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit,
                                             mem::MicroTLB utlb, uint32_t branch, uint64_t flags_cache) {
    (void)state;
    (void)op;
    (void)instr_limit;
    (void)utlb;
    (void)branch;
    (void)flags_cache;
    std::abort();
}

ATTR_PRESERVE_NONE int64_t JitContinueSkipOneTarget(EmuState* RESTRICT state, DecodedOp* RESTRICT op,
                                                    int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
                                                    uint64_t flags_cache) {
    (void)state;
    (void)op;
    (void)instr_limit;
    (void)utlb;
    (void)branch;
    (void)flags_cache;
    std::abort();
}

ATTR_PRESERVE_NONE int64_t JitExitOnCurrentEipTarget(EmuState* state, DecodedOp* op, int64_t instr_limit, mem::MicroTLB,
                                                     uint32_t, uint64_t flags_cache) {
    CommitFlagsCache(state, flags_cache);
    if (!state->eip_dirty) state->sync_eip_to_op_start(op);
    return instr_limit;
}

ATTR_PRESERVE_NONE int64_t JitExitOnNextEipTarget(EmuState* state, DecodedOp* op, int64_t instr_limit, mem::MicroTLB,
                                                  uint32_t, uint64_t flags_cache) {
    CommitFlagsCache(state, flags_cache);
    if (!state->eip_dirty) state->sync_eip_to_op_end(op);
    return instr_limit;
}

ATTR_PRESERVE_NONE int64_t JitExitDefaultTarget(EmuState* state, DecodedOp*, int64_t instr_limit, mem::MicroTLB,
                                                uint32_t, uint64_t flags_cache) {
    CommitFlagsCache(state, flags_cache);
    return instr_limit;
}

ATTR_PRESERVE_NONE int64_t JitFaultExitTarget(EmuState*, DecodedOp*, int64_t instr_limit, mem::MicroTLB, uint32_t,
                                              uint64_t) {
    return instr_limit;
}

BlockBuilder& BlockBuilder::Get() {
    static BlockBuilder instance;
    return instance;
}

BlockBuilder::BlockBuilder() {
    m_buffer_size = 64 * 1024 * 1024;  // 64MB code cache
    m_code_buffer =
        mmap(nullptr, m_buffer_size, PROT_READ | PROT_WRITE | PROT_EXEC, MAP_ANON | MAP_PRIVATE | MAP_JIT, -1, 0);
    m_code_pool = nullptr;
    if (m_code_buffer != MAP_FAILED) {
        m_code_pool = new JitCodeResource(m_code_buffer, m_buffer_size);
    }
    InitializeMapping();
}

BlockBuilder::~BlockBuilder() {
    delete m_code_pool;
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
    if (m_code_buffer == MAP_FAILED || m_code_pool == nullptr) return nullptr;

    if (JitDebugEnabled()) {
        JitDebugLog("[jit] compile block start=%08x insts=%u entry=%p\n", bb->chain.start_eip, bb->inst_count,
                    reinterpret_cast<void*>(bb->entry));
    }
    const bool log_reloc_detail = JitDebugEnabled() && ShouldDumpBlock(bb->chain.start_eip);
    if (log_reloc_detail) {
        DecodedOp* sentinel = bb->Sentinel();
        JitDebugLog(
            "[jit] sentinel block=%08x slot_index=%u slot_count=%u sentinel=%p handler=%p next_eip=%08x meta=%02x "
            "next_block=%p\n",
            bb->chain.start_eip, bb->sentinel_slot_index, bb->slot_count, reinterpret_cast<void*>(sentinel),
            reinterpret_cast<void*>(sentinel->handler), sentinel->next_eip, sentinel->meta.all,
            reinterpret_cast<void*>(GetNextBlock(sentinel)));
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
            JitDebugLog("[jit]   op[%u] handler=%p sid=%u name=%s code=%u patches=%u branch_relocs=%u\n", i,
                        reinterpret_cast<void*>(op->handler), sid, generated::stencil_names[sid],
                        generated::stencils[sid].code_size, generated::stencils[sid].patch_count,
                        generated::stencils[sid].branch_reloc_count);
        }

        stencil_ids.push_back(sid);
        const StencilDesc& desc = generated::stencils[sid];
        estimated_size += desc.code_size;
        branch_reloc_count += desc.branch_reloc_count;
    }

    // Add extra for trampoline (ret)
    estimated_size += 32;
    estimated_size += branch_reloc_count * kHandlerTailVeneerSize;

    // Switch to RW for Apple Silicon
    pthread_jit_write_protect_np(0);
    auto* code_pool = static_cast<JitCodeResource*>(m_code_pool);
    uint8_t* start_ptr = static_cast<uint8_t*>(code_pool->try_allocate(estimated_size, 16));
    if (!start_ptr) {
        pthread_jit_write_protect_np(1);
        if (JitDebugEnabled()) {
            JitDebugLog("[jit] code cache full block=%08x need=%zu pool=%p size=%zu\n", bb->chain.start_eip,
                        estimated_size, m_code_buffer, m_buffer_size);
        }
        return nullptr;
    }
    std::vector<uint8_t*> stencil_starts(bb->inst_count);
    uint8_t* layout_ptr = start_ptr;
    for (uint32_t i = 0; i < bb->inst_count; ++i) {
        stencil_starts[i] = layout_ptr;
        layout_ptr += generated::stencils[stencil_ids[i]].code_size;
    }
    uint8_t* current_ptr = start_ptr;
    uint8_t* veneer_ptr = layout_ptr;
    veneer_ptr += sizeof(uint32_t);
    ankerl::unordered_dense::map<VeneerKey, uint8_t*, VeneerKeyHash> veneer_cache;
    size_t veneer_count = 0;
    size_t veneer_bytes = 0;
    for (uint32_t i = 0; i < bb->inst_count; ++i) {
        uint16_t sid = stencil_ids[i];
        const StencilDesc& desc = generated::stencils[sid];
        DecodedOp* op = bb->FirstOp() + i;
        const uint32_t copy_size = desc.code_size;

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

        for (uint16_t a = 0; a < desc.addr_reloc_count; ++a) {
            const AddrRelocDesc& reloc = desc.addr_relocs[a];
            uint32_t* page21_ptr = reinterpret_cast<uint32_t*>(current_ptr + reloc.page21_offset);
            uint32_t* pageoff12_ptr = reinterpret_cast<uint32_t*>(current_ptr + reloc.pageoff12_offset);
            const uint8_t* target_ptr =
                reinterpret_cast<const uint8_t*>(generated::addr_reloc_targets[reloc.target_id]);
            PatchAdrp(page21_ptr, target_ptr);
            switch (reloc.kind) {
                case AddrRelocKind::PageOffset:
                    if ((*pageoff12_ptr & kAddImmMask) == kAddImmOpcode) {
                        PatchAddPageOffset(pageoff12_ptr, target_ptr);
                    } else {
                        PatchUnsignedOffset(pageoff12_ptr, target_ptr, reloc.pageoff_shift);
                    }
                    break;
                case AddrRelocKind::GotLoadToAddr:
                    RewriteGotLoadToAdd(pageoff12_ptr, target_ptr);
                    break;
            }
            if (log_reloc_detail) {
                JitDebugLog("[jit]   addr_reloc op[%u] name=%s page21=%p pageoff=%p target=%p kind=%u shift=%u\n", i,
                            generated::addr_reloc_target_names[reloc.target_id], reinterpret_cast<void*>(page21_ptr),
                            reinterpret_cast<void*>(pageoff12_ptr), reinterpret_cast<const void*>(target_ptr),
                            static_cast<unsigned>(reloc.kind), reloc.pageoff_shift);
            }
        }

        for (uint16_t r = 0; r < desc.branch_reloc_count; ++r) {
            const BranchRelocDesc& reloc = desc.branch_relocs[r];
            uint32_t* branch_ptr = reinterpret_cast<uint32_t*>(current_ptr + reloc.offset);
            uint64_t target_addr = reinterpret_cast<uint64_t>(generated::branch_reloc_targets[reloc.target_id]);
            const char* target_name = generated::branch_reloc_target_names[reloc.target_id];

            if (target_name && (std::strstr(target_name, "JitContinueTarget") ||
                                std::strstr(target_name, "JitContinueSkipOneTarget"))) {
                const uint32_t skip = (std::strstr(target_name, "JitContinueSkipOneTarget") != nullptr) ? 2u : 1u;
                const uint32_t target_index = i + skip;
                if (target_index < bb->inst_count) {
                    PatchBranch26(branch_ptr, stencil_starts[target_index]);
                    if (log_reloc_detail) {
                        JitDebugLog(
                            "[jit]   reloc op[%u] name=%s branch=%p block_target_op=%u runtime_op=%p target=%p\n", i,
                            target_name, reinterpret_cast<void*>(branch_ptr), target_index,
                            reinterpret_cast<void*>(bb->FirstOp() + target_index),
                            reinterpret_cast<void*>(stencil_starts[target_index]));
                    }
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
                        veneer_count++;
                        veneer_bytes += kHandlerTailVeneerSize;
                    }
                    PatchBranch26(branch_ptr, stub_start);
                    if (log_reloc_detail) {
                        JitDebugLog(
                            "[jit]   reloc op[%u] name=%s branch=%p handler_tail runtime_op=%p handler=%p veneer=%p\n",
                            i, target_name, reinterpret_cast<void*>(branch_ptr), reinterpret_cast<void*>(target_op),
                            reinterpret_cast<void*>(target_op->handler), reinterpret_cast<void*>(stub_start));
                    }
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
                        veneer_bytes += kCallVeneerSize;
                    } else {
                        EmitJumpVeneer(stub_ptr, target_addr);
                        veneer_ptr += kJumpVeneerSize;
                        veneer_bytes += kJumpVeneerSize;
                    }
                    veneer_count++;
                }
                PatchBranch26(branch_ptr, stub_start);
                if (log_reloc_detail && IsInterestingRelocTarget(target_name)) {
                    JitDebugLog("[jit]   reloc op[%u] name=%s branch=%p target=%p veneer=%p is_call=%d\n", i,
                                target_name ? target_name : "<null>", reinterpret_cast<void*>(branch_ptr),
                                reinterpret_cast<void*>(target_addr), reinterpret_cast<void*>(stub_start),
                                static_cast<int>(is_call));
                }
            }
        }

        current_ptr += copy_size;
    }

    // Add termination (ret)
    // ret = 0xd65f03c0
    *(uint32_t*)current_ptr = 0xd65f03c0;
    current_ptr += 4;

    current_ptr = veneer_ptr;
    void* jcb_mem = code_pool->try_allocate(sizeof(JitCodeBlock), alignof(JitCodeBlock));
    if (!jcb_mem) {
        if (JitDebugEnabled()) {
            JitDebugLog("[jit] metadata alloc failed block=%08x size=%zu\n", bb->chain.start_eip, sizeof(JitCodeBlock));
        }
        return nullptr;
    }
    JitCodeBlock* jcb = new (jcb_mem) JitCodeBlock();
    jcb->entry = start_ptr;
    jcb->code_size = current_ptr - start_ptr;
    jcb->owner = bb;
    bb->jit_code = jcb;

    // Switch back to RX
    pthread_jit_write_protect_np(1);
    sys_icache_invalidate(start_ptr, current_ptr - start_ptr);

    DumpJitBlock(bb->chain.start_eip, start_ptr, jcb->code_size);
    WriteJitProfileMap(bb->chain.start_eip, start_ptr, jcb->code_size, stencil_starts, stencil_ids);

    if (JitDebugEnabled()) {
        JitDebugLog("[jit] compiled block start=%08x code=%p size=%zu branch_relocs=%zu veneers=%zu veneer_bytes=%zu\n",
                    bb->chain.start_eip, reinterpret_cast<void*>(start_ptr), jcb->code_size, branch_reloc_count,
                    veneer_count, veneer_bytes);
    }

    return jcb;
}

}  // namespace jit
}  // namespace fiberish
