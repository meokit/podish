#include "bindings.h"
#include <execinfo.h>
#include <unistd.h>
#include <algorithm>
#include <csignal>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <vector>
#include "decoder.h"
#include "dispatch.h"
#include "hooks.h"
#include "logger.h"
#include "mem/mmu.h"
#include "ops.h"
#include "state.h"
#include "superopcodes.h"

#if defined(_WIN32)
#include <windows.h>
#else
#include <dlfcn.h>
#endif

using namespace fiberish;
using MicroTLB = mem::MicroTLB;

struct X86_MmuHandle {
    mem::MmuCore* core = nullptr;
};

extern "C" {

// ----------------------------------------------------------------------------
// Internal Bridge Callbacks
// ----------------------------------------------------------------------------

static bool FaultTraceEnabled() {
    static bool enabled = []() {
        const char* v = std::getenv("FIBERISH_TRACE_DECODE_FAULT");
        return v && v[0] != '\0' && std::strcmp(v, "0") != 0;
    }();
    return enabled;
}

static int InternalFaultBridge(void* opaque, uint32_t addr, int is_write) {
    auto* state = static_cast<EmuState*>(opaque);
    state->fault_vector = 14;  // #PF
    state->fault_addr = addr;
    if (FaultTraceEnabled()) {
        fprintf(stderr, "[Bridge] Fault addr=0x%x write=%d\n", addr, is_write);
    }
    if (state->fault_handler) {
        int res = state->fault_handler(state, addr, is_write, state->fault_userdata);
        if (FaultTraceEnabled()) {
            fprintf(stderr, "[Bridge] Handler returned %d\n", res);
        }
        return res;
    } else {
        // Default behavior if no user handler: Trigger Fault
        state->status = EmuStatus::Fault;
        return 0;
    }
}

static void InternalMemHookBridge(void* opaque, uint32_t addr, uint32_t size, int is_write, uint64_t val) {
    auto* state = static_cast<EmuState*>(opaque);
    if (state->mem_hook) {
        state->mem_hook(state, addr, size, is_write, val, state->mem_userdata);
    }
}

// Invalidate all translated blocks linked to one guest page.
static void X86_InvalidateCodeCacheByPage(EmuState* state, uint32_t page_addr) {
    uint32_t page_idx = page_addr >> 12;
    auto it = state->page_to_blocks.find(page_idx);
    if (it != state->page_to_blocks.end()) {
        // Remove all referenced blocks from the cache
        for (uint32_t eip : it->second) {
            auto block_it = state->block_cache.find(eip);
            if (block_it != state->block_cache.end()) {
                block_it->second->Invalidate();
                state->block_cache.erase(block_it);
            }
        }
        // Crucial: Remove the entire mapping for this page to prevent
        // the vector from growing indefinitely with stale EIPs.
        state->page_to_blocks.erase(it);
    }
}

static void InternalSmcBridge(void* opaque, uint32_t addr) {
    EmuState* state = static_cast<EmuState*>(opaque);
    // Invalidate the page containing 'addr'
    X86_InvalidateCodeCacheByPage(state, addr);
}

static void X86_ResetCodeCache(EmuState* state) {
    if (!state) return;
    state->block_cache.clear();
    state->page_to_blocks.clear();
}

static bool BlockCrossesPage(const BasicBlock* block) {
    if (!block || block->inst_count == 0 || block->end_eip <= block->chain.start_eip) return false;
    return ((block->chain.start_eip ^ (block->end_eip - 1)) & 0xFFFFF000u) != 0;
}

static bool BlockIsConcatCandidateTerminal(const BasicBlock* block) {
    if (!block) return false;
    return block->terminal_kind() == BlockTerminalKind::DirectJmpRel ||
           block->terminal_kind() == BlockTerminalKind::DirectJccRel;
}

static bool BlockFormsSmallLoopWith(const BasicBlock* source, const BasicBlock* target) {
    if (!source || !target) return false;
    if (source->chain.start_eip == target->chain.start_eip) return true;
    return target->branch_target_eip == source->chain.start_eip;
}

static void RegisterBlockPages(EmuState* state, uint32_t cache_eip, const BasicBlock* block) {
    uint32_t page_idx = cache_eip >> 12;
    state->page_to_blocks[page_idx].push_back(cache_eip);
    if (block && block->end_eip > block->chain.start_eip && ((block->end_eip - 1) >> 12) != page_idx) {
        uint32_t page2 = (block->end_eip - 1) >> 12;
        state->page_to_blocks[page2].push_back(cache_eip);
    }
}

static BasicBlock* CacheDecodedBlock(EmuState* state, uint32_t cache_eip, BasicBlock* block) {
    auto [it, inserted] = state->block_cache.insert({cache_eip, block});
    if (inserted) {
        RegisterBlockPages(state, cache_eip, block);
        return block;
    }
    return it->second;
}

static BasicBlock* LookupOrDecodeBlockConcatSuccessor(EmuState* state, uint32_t eip, uint32_t end_eip) {
    auto it = state->block_cache.find(eip);
    if (it != state->block_cache.end()) return it->second;

    const EmuStatus saved_status = state->status;
    const uint8_t saved_fault_vector = state->fault_vector;
    const uint32_t saved_fault_addr = state->fault_addr;
    BasicBlock* block = DecodeBlock(state, eip, end_eip, 0);
    state->status = saved_status;
    state->fault_vector = saved_fault_vector;
    state->fault_addr = saved_fault_addr;
    if (!block) return nullptr;
    return CacheDecodedBlock(state, eip, block);
}

static bool CanBlockConcatWithSuccessor(const BasicBlock* a, const BasicBlock* b, bool remove_source_terminal_inst,
                                        BlockStats* stats) {
    if (!a || !b || !a->chain.is_valid || !b->chain.is_valid || a->inst_count == 0 || b->inst_count == 0) {
        stats->block_concat_reject_target_missing++;
        return false;
    }

    if (BlockCrossesPage(a) || BlockCrossesPage(b) || ((a->chain.start_eip ^ b->chain.start_eip) & 0xFFFFF000u) != 0) {
        stats->block_concat_reject_cross_page++;
        return false;
    }

    if (BlockFormsSmallLoopWith(a, b)) {
        stats->block_concat_reject_loop++;
        return false;
    }

    const uint32_t removed_inst_count = remove_source_terminal_inst ? 1u : 0u;
    const uint32_t concat_inst_count = a->inst_count - removed_inst_count + b->inst_count;

    if (concat_inst_count > 64) {
        stats->block_concat_reject_size_limit++;
        return false;
    }

    return true;
}

static BasicBlock* BuildDirectJmpBlockConcat(EmuState* state, const BasicBlock* a, const BasicBlock* b) {
    const uint32_t concat_inst_count = a->inst_count - 1 + b->inst_count;
    const uint32_t concat_slot_count = concat_inst_count + 1;
    void* mem = state->block_pool.allocate(BasicBlock::CalculateSize(concat_slot_count));
    BasicBlock* concat = new (mem) BasicBlock;
    state->RememberAllocatedBlock(concat);

    concat->chain.start_eip = a->chain.start_eip;
    concat->end_eip = b->end_eip;
    concat->inst_count = concat_inst_count;
    concat->slot_count = concat_slot_count;
    concat->sentinel_slot_index = concat_inst_count;
    concat->branch_target_eip = b->branch_target_eip;
    concat->fallthrough_eip = b->fallthrough_eip;
    concat->set_terminal_kind(b->terminal_kind());
    concat->chain.is_valid = true;
    concat->exec_count = 0;

    DecodedOp* dst = concat->FirstOp();
    const DecodedOp* a_ops = a->FirstOp();
    const DecodedOp* b_ops = b->FirstOp();

    uint32_t out = 0;
    for (uint32_t i = 0; i + 1 < a->inst_count; ++i) {
        dst[out++] = a_ops[i];
    }
    for (uint32_t i = 0; i < b->inst_count; ++i) {
        dst[out++] = b_ops[i];
    }
    dst[out] = *b->Sentinel();

    ApplySuperOpcodesToBlockOps(dst, concat_inst_count);
    concat->entry = concat->FirstOp()->handler;
    return concat;
}

static BasicBlock* BuildJccFallthroughBlockConcat(EmuState* state, const BasicBlock* a, const BasicBlock* b) {
    const uint32_t concat_inst_count = a->inst_count + b->inst_count;
    const uint32_t concat_slot_count = concat_inst_count + 1;
    void* mem = state->block_pool.allocate(BasicBlock::CalculateSize(concat_slot_count));
    BasicBlock* concat = new (mem) BasicBlock;
    state->RememberAllocatedBlock(concat);

    concat->chain.start_eip = a->chain.start_eip;
    concat->end_eip = b->end_eip;
    concat->inst_count = concat_inst_count;
    concat->slot_count = concat_slot_count;
    concat->sentinel_slot_index = concat_inst_count;
    concat->branch_target_eip = b->branch_target_eip;
    concat->fallthrough_eip = b->fallthrough_eip;
    concat->set_terminal_kind(b->terminal_kind());
    concat->chain.is_valid = true;
    concat->exec_count = 0;

    DecodedOp* dst = concat->FirstOp();
    const DecodedOp* a_ops = a->FirstOp();
    const DecodedOp* b_ops = b->FirstOp();

    uint32_t out = 0;
    for (uint32_t i = 0; i < a->inst_count; ++i) {
        dst[out++] = a_ops[i];
    }
    for (uint32_t i = 0; i < b->inst_count; ++i) {
        dst[out++] = b_ops[i];
    }
    dst[out] = *b->Sentinel();

    ApplySuperOpcodesToBlockOps(dst, concat_inst_count);
    concat->entry = concat->FirstOp()->handler;
    return concat;
}

static __attribute__((noinline, cold)) BasicBlock* ResolveBlockForRunSlow(EmuState* state, uint32_t eip,
                                                                          uint32_t end_eip) {
    BasicBlock* new_block = DecodeBlock(state, eip, end_eip, 0);

    if (!new_block) {
        if (FaultTraceEnabled()) {
            fprintf(stderr, "[RunDbg] DecodeBlock returned null eip=%08X status=%d fault_vector=%u fault_addr=%08X\n",
                    eip, (int)state->status, (unsigned)state->fault_vector, state->fault_addr);
        }
        if (state->status == EmuStatus::Running) {
            state->status = EmuStatus::Fault;
        }
        return nullptr;
    }

    if (!BlockIsConcatCandidateTerminal(new_block)) {
        state->block_stats.block_concat_reject_not_concat_terminal++;
        return CacheDecodedBlock(state, eip, new_block);
    }

    state->block_stats.block_concat_attempts++;
    const bool is_direct_jmp = new_block->terminal_kind() == BlockTerminalKind::DirectJmpRel;
    const uint32_t successor_eip = is_direct_jmp ? new_block->branch_target_eip : new_block->fallthrough_eip;
    if (successor_eip == eip) {
        state->block_stats.block_concat_reject_loop++;
        return CacheDecodedBlock(state, eip, new_block);
    }

    BasicBlock* successor_block = LookupOrDecodeBlockConcatSuccessor(state, successor_eip, end_eip);
    if (!successor_block) {
        state->block_stats.block_concat_reject_target_missing++;
        return CacheDecodedBlock(state, eip, new_block);
    }

    if (!CanBlockConcatWithSuccessor(new_block, successor_block, is_direct_jmp, &state->block_stats)) {
        return CacheDecodedBlock(state, eip, new_block);
    }

    BasicBlock* concat_block = is_direct_jmp ? BuildDirectJmpBlockConcat(state, new_block, successor_block)
                                             : BuildJccFallthroughBlockConcat(state, new_block, successor_block);
    state->block_stats.block_concat_success++;
    if (is_direct_jmp) {
        state->block_stats.block_concat_success_direct_jmp++;
    } else {
        state->block_stats.block_concat_success_jcc_fallthrough++;
    }
    return CacheDecodedBlock(state, eip, concat_block);
}

// Signal Handler for safety
void SignalHandler(int sig) {
    void* array[20];
    size_t size;
    size = backtrace(array, 20);
    fprintf(stderr, "\n[CRASH] Signal %d Caught:\n", sig);
    backtrace_symbols_fd(array, size, STDERR_FILENO);
    _exit(1);
}

static bool g_SignalRegistered = false;

static void InitializeDummyInvalidBlock(EmuState* state) {
    BasicBlock& block = state->dummy_invalid_block;
    block.chain = {};
    block.chain.is_valid = false;
    block.end_eip = 0;
    block.inst_count = 0;
    block.slot_count = 0;
    block.sentinel_slot_index = 0;
    block.branch_target_eip = 0;
    block.fallthrough_eip = 0;
    block.exec_count = 0;
    block.entry = nullptr;
}

// ----------------------------------------------------------------------------
// Creation / Destruction
// ----------------------------------------------------------------------------

EmuState* X86_Create() {
    if (!g_SignalRegistered) {
        signal(SIGSEGV, SignalHandler);
        signal(SIGILL, SignalHandler);
        signal(SIGBUS, SignalHandler);
        g_SignalRegistered = true;
    }

    EmuState* state = new EmuState();
    // Zero entire context first
    std::memset(&state->ctx, 0, sizeof(state->ctx));

    // Set default EFLAGS and Mask
    SetStateFlagsCache(state, InitFlagsCache(0x202));  // IF=1, Reserved=1
    state->ctx.eflags_mask = 0x240DD5;

    // Default FPU State
    state->ctx.fpu_cw = 0x037F;
    // Hooks initialized by default constructor of HookManager

    InitializeDummyInvalidBlock(state);
    state->ctx.fpu_sw = 0x0000;
    state->ctx.fpu_tw = 0xFFFF;
    state->ctx.fpu_top = 0;

    // Link pointers
    state->ctx.mmu = &state->mmu;
    state->ctx.hooks = &state->hooks;

    state->mmu.set_fault_callback(InternalFaultBridge, state);
    state->mmu.set_smc_callback(InternalSmcBridge, state);

    state->tsc_start_time = std::chrono::steady_clock::now();

    return state;
}

EmuState* X86_Clone(EmuState* parent, int share_mem) {
    if (!parent) return nullptr;

    EmuState* state = new EmuState();

    // 1. Copy Context (Registers, Segments, etc.)
    state->ctx = parent->ctx;

    // 2. Memory Handling
    if (share_mem) {
        // Shared Memory (CLONE_VM) -> Threads
        state->mmu.attach_core(parent->mmu.core_handle(), true);
    } else {
        // Independent Memory (Fork)
        // Clone MMU with fork semantics: copy owned pages, preserve external mappings.
        state->mmu.attach_core(mem::Mmu::CloneCorePreserveExternal(parent->mmu.core_handle()), false);
    }

    // 3. Link Internal Pointers
    state->ctx.mmu = &state->mmu;
    state->ctx.hooks = &state->hooks;

    // 4. Set Callbacks (Bridge to same handlers, but new 'state' passed)
    state->mmu.set_fault_callback(InternalFaultBridge, state);
    // state->mmu.set_mem_hook(InternalMemHookBridge, state);
    state->mmu.set_smc_callback(InternalSmcBridge, state);

    // Reuse parent's external handlers & userdata
    state->fault_handler = parent->fault_handler;
    state->fault_userdata = parent->fault_userdata;  // This might need care in Go!
    state->mem_hook = parent->mem_hook;
    state->mem_userdata = parent->mem_userdata;

    if (state->mem_hook) {
        state->mmu.set_mem_hook(InternalMemHookBridge, state);
    }

    // Interrupt handlers
    for (int i = 0; i < 256; ++i) {
        state->interrupt_handlers[i] = parent->interrupt_handlers[i];
        state->interrupt_userdata[i] = parent->interrupt_userdata[i];
    }
    // Re-register hooks logic
    // We need to copy the internal C++ std::function hooks too?
    // 'state->hooks' is copy constructed from parent->hooks via `state->ctx = parent->ctx`?
    // Wait, ctx copies pointers. state->hooks is a separate object.

    // Let's copy hooks explicitly
    state->hooks = parent->hooks;

    // Explicitly copy segment bases (though ctx assignment should have done it, being safe)
    for (int i = 0; i < 6; ++i) {
        state->ctx.seg_base[i] = parent->ctx.seg_base[i];
    }

    // Copy Log Callback
    state->log_callback = parent->log_callback;
    state->log_userdata = parent->log_userdata;

    // Initialize Dummy Invalid Block (same as X86_Create)
    // This is CRITICAL for OpExitBlock which assumes next_block is never nullptr
    InitializeDummyInvalidBlock(state);

    return state;
}

void X86_Destroy(EmuState* state) {
    if (state) {
        // Monotonic buffer resource will automatically release all memory
        // allocated from it (BasicBlocks and their vectors) when 'state' is deleted.
        delete state;
    }
}

// ----------------------------------------------------------------------------
// Register Access
// ----------------------------------------------------------------------------

uint32_t X86_RegRead(EmuState* state, int reg_index) {
    if (reg_index >= 0 && reg_index < 8) {
        return state->ctx.regs[reg_index];
    }
    return 0;
}

void X86_RegWrite(EmuState* state, int reg_index, uint32_t val) {
    if (reg_index >= 0 && reg_index < 8) {
        state->ctx.regs[reg_index] = val;
    }
}

uint32_t X86_GetEIP(EmuState* state) { return state->ctx.eip; }

void X86_SetEIP(EmuState* state, uint32_t eip) {
    state->ctx.eip = eip;
    state->eip_dirty = true;
}

uint32_t X86_GetEFLAGS(EmuState* state) { return GetArchitecturalEflags(state); }

void X86_SetEFLAGS(EmuState* state, uint32_t val) { SetArchitecturalEflags(state, val); }

// ----------------------------------------------------------------------------
// XMM Access
// ----------------------------------------------------------------------------

void X86_ReadXMM(EmuState* state, int idx, uint8_t* val) {
    if (idx >= 0 && idx < 8 && val) {
        std::memcpy(val, &state->ctx.xmm[idx], 16);
    }
}

void X86_WriteXMM(EmuState* state, int idx, const uint8_t* val) {
    if (idx >= 0 && idx < 8 && val) {
        std::memcpy(&state->ctx.xmm[idx], val, 16);
    }
}

// ----------------------------------------------------------------------------
// FPU Access
// ----------------------------------------------------------------------------

uint16_t X86_GetFCW(EmuState* state) { return state->ctx.fpu_cw; }
void X86_SetFCW(EmuState* state, uint16_t val) {
    state->ctx.fpu_cw = val;
    f80_sync_to_soft(state->ctx.fpu_cw, state->ctx.fpu_sw);
}
uint16_t X86_GetFSW(EmuState* state) { return state->ctx.fpu_sw; }
void X86_SetFSW(EmuState* state, uint16_t val) {
    state->ctx.fpu_sw = val;
    state->ctx.fpu_top = (val >> 11) & 7;
    f80_sync_to_soft(state->ctx.fpu_cw, state->ctx.fpu_sw);
}
uint16_t X86_GetFTW(EmuState* state) { return state->ctx.fpu_tw; }
void X86_SetFTW(EmuState* state, uint16_t val) { state->ctx.fpu_tw = val; }

void X86_ReadFPUReg(EmuState* state, int idx, uint8_t* val) {
    if (idx >= 0 && idx < 8 && val) {
        int phys_idx = (state->ctx.fpu_top + idx) & 7;
        std::memcpy(val, &state->ctx.fpu_regs[phys_idx], 10);
    }
}

void X86_WriteFPUReg(EmuState* state, int idx, const uint8_t* val) {
    if (idx >= 0 && idx < 8 && val) {
        int phys_idx = (state->ctx.fpu_top + idx) & 7;
        std::memcpy(&state->ctx.fpu_regs[phys_idx], val, 10);
    }
}

// ----------------------------------------------------------------------------
// Segment Base Access
// ----------------------------------------------------------------------------

uint32_t X86_SegBaseRead(EmuState* state, int seg_index) {
    if (seg_index >= 0 && seg_index < 6) {
        return state->ctx.seg_base[seg_index + 1];
    }
    return 0;
}

void X86_SegBaseWrite(EmuState* state, int seg_index, uint32_t base) {
    if (seg_index >= 0 && seg_index < 6) {
        state->ctx.seg_base[seg_index + 1] = base;
    }
}

// ----------------------------------------------------------------------------
// Memory Access
// ----------------------------------------------------------------------------

void X86_MemMap(EmuState* state, uint32_t addr, uint32_t size, uint8_t perms) { state->mmu.mmap(addr, size, perms); }

void X86_ReprotectMappedRange(EmuState* state, uint32_t addr, uint32_t size, uint8_t perms) {
    if (!state) return;
    state->mmu.reprotect_mapped_range(addr, size, perms);
}

void* X86_AllocatePage(EmuState* state, uint32_t addr, uint8_t perms) { return state->mmu.allocate_page(addr, perms); }

int X86_MapExternalPage(EmuState* state, uint32_t addr, void* external_page, uint8_t perms) {
    return state->mmu.map_external_page(addr, static_cast<mem::HostAddr>(external_page), perms) ? 1 : 0;
}

static X86_MmuHandle* X86_NewMmuHandle(mem::MmuCore* core, bool add_ref) {
    if (!core) return nullptr;
    if (add_ref) mem::Mmu::RetainCore(core);
    auto* handle = new X86_MmuHandle();
    handle->core = core;
    return handle;
}

X86_MmuHandle* X86_MmuCreateEmpty() { return X86_NewMmuHandle(mem::Mmu::CreateEmptyCore(), false); }

X86_MmuHandle* X86_MmuCloneSkipExternal(X86_MmuHandle* mmu) {
    if (!mmu || !mmu->core) return nullptr;
    return X86_NewMmuHandle(mem::Mmu::CloneCoreSkipExternal(mmu->core), false);
}

X86_MmuHandle* X86_MmuRetain(X86_MmuHandle* mmu) {
    if (!mmu || !mmu->core) return nullptr;
    return X86_NewMmuHandle(mmu->core, true);
}

void X86_MmuRelease(X86_MmuHandle* mmu) {
    if (!mmu) return;
    mem::Mmu::ReleaseCore(mmu->core);
    mmu->core = nullptr;
    delete mmu;
}

uintptr_t X86_MmuGetIdentity(X86_MmuHandle* mmu) {
    if (!mmu || !mmu->core) return 0;
    return mmu->core->identity;
}

X86_MmuHandle* X86_EngineGetMmu(EmuState* state) {
    if (!state) return nullptr;
    return X86_NewMmuHandle(state->mmu.core_handle(), true);
}

X86_MmuHandle* X86_EngineDetachMmu(EmuState* state) {
    if (!state) return nullptr;
    auto* detached_core = state->mmu.detach_core();
    X86_ResetCodeCache(state);
    return X86_NewMmuHandle(detached_core, false);
}

int X86_EngineAttachMmu(EmuState* state, X86_MmuHandle* mmu) {
    if (!state || !mmu || !mmu->core) return 0;
    state->mmu.attach_core(mmu->core, true);
    X86_ResetCodeCache(state);
    return 1;
}

void X86_MemUnmap(EmuState* state, uint32_t addr, uint32_t size) {
    state->mmu.munmap(addr, size);

    // Also invalidate the derived block cache for this range.
    uint32_t start_page = addr >> 12;
    uint32_t end_page = (addr + size + 0xFFF) >> 12;
    for (uint32_t p = start_page; p < end_page; ++p) {
        auto it = state->page_to_blocks.find(p);
        if (it != state->page_to_blocks.end()) {
            for (uint32_t block_eip : it->second) {
                // Remove from block cache if it exists
                auto block_it = state->block_cache.find(block_eip);
                if (block_it != state->block_cache.end()) {
                    block_it->second->Invalidate();
                    state->block_cache.erase(block_it);
                }
            }
            state->page_to_blocks.erase(it);
        }
    }
}

void X86_MemWrite(EmuState* state, uint32_t addr, const uint8_t* data, uint32_t size) {
    for (uint32_t i = 0; i < size; ++i) {
        // TODO: We need a way to notify the write is failed
        (void)state->mmu.write_no_utlb<uint8_t>(addr + i, data[i]);
    }
}

void X86_MemRead(EmuState* state, uint32_t addr, uint8_t* val, uint32_t size) {
    for (uint32_t i = 0; i < size; ++i) {
        // TODO: We need a way to notify the read is failed
        auto res = state->mmu.read_no_utlb<uint8_t>(addr + i);
        val[i] = res.value_or(0);
    }
}

int X86_MemIsDirty(EmuState* state, uint32_t addr) {
    mem::Property p = state->mmu.get_property(addr);
    return mem::has_property(p, mem::Property::Dirty) ? 1 : 0;
}

void* X86_ResolvePtr(EmuState* state, uint32_t addr, int is_write) {
    if (!state) return nullptr;
    mem::Property perm = is_write ? mem::Property::Write : mem::Property::Read;
    return state->mmu.resolve_safe(addr, perm);
}

size_t X86_CollectMappedPages(EmuState* state, uint32_t addr, uint32_t size, X86_PageMapping* buffer,
                              size_t max_count) {
    if (!state || !buffer || max_count == 0 || size == 0 || !state->mmu.page_dir) return 0;

    constexpr uint64_t kPageSize = static_cast<uint64_t>(mem::PAGE_SIZE);
    constexpr uint64_t kPageMask = static_cast<uint64_t>(~mem::PAGE_MASK);

    const uint64_t start_page = static_cast<uint64_t>(addr) & kPageMask;
    const uint64_t end_exclusive = static_cast<uint64_t>(addr) + static_cast<uint64_t>(size);
    const uint64_t end_page = (end_exclusive + mem::PAGE_MASK) & kPageMask;

    size_t count = 0;
    for (uint64_t page = start_page; page < end_page; page += kPageSize) {
        const uint32_t page_addr = static_cast<uint32_t>(page);
        const uint32_t l1_idx = page_addr >> 22;
        const uint32_t l2_idx = (page_addr >> 12) & 0x3FF;

        auto& chunk = state->mmu.page_dir->l1_directory[l1_idx];
        if (!chunk) continue;

        auto* page_ptr = chunk->pages[l2_idx];
        if (!page_ptr) continue;

        const auto perms = chunk->permissions[l2_idx];
        uint8_t flags = 0;
        if (mem::has_property(perms, mem::Property::Dirty)) flags |= X86_PAGE_FLAG_DIRTY;
        if (mem::has_property(perms, mem::Property::External)) flags |= X86_PAGE_FLAG_EXTERNAL;

        buffer[count].guest_page = page_addr;
        buffer[count].perms = static_cast<uint8_t>(static_cast<uint32_t>(perms));
        buffer[count].flags = flags;
        buffer[count].reserved = 0;
        buffer[count].host_page = page_ptr;
        count++;
        if (count == max_count) break;
    }

    return count;
}

// ----------------------------------------------------------------------------
// Execution
// ----------------------------------------------------------------------------

void X86_Run(EmuState* state, uint32_t end_eip, uint64_t max_insts) {
    state->status = EmuStatus::Running;
    state->block_stats = {};
#ifdef FIBERCPU_ENABLE_HANDLER_PROFILE
    state->handler_exec_counts.clear();
    state->current_block_head = nullptr;
#endif
#ifdef FIBERCPU_ENABLE_JCC_PROFILE
    state->jcc_profile_counts.clear();
#endif
    uint64_t total_run_insts = 0;

    // Reset chaining state for this run
    state->last_block = &state->dummy_invalid_block;
    state->smc_write_to_exec = false;
    state->allow_write_exec_page = false;
    state->intercept_exec_write_for_smc = false;

    // Sync FPU state before starting
    f80_sync_to_soft(state->ctx.fpu_cw, state->ctx.fpu_sw);

    while (state->status == EmuStatus::Running) {
        uint32_t eip = state->ctx.eip;
        const bool smc_single_step = state->smc_write_to_exec;

        if (end_eip != 0 && eip == end_eip) {
            state->status = EmuStatus::Stopped;
            break;
        }
        if (!smc_single_step && max_insts != 0 && total_run_insts >= max_insts) {
            state->status = EmuStatus::Stopped;
            break;
        }
        auto execute_block = [&](BasicBlock* block_ptr) {
            block_ptr->exec_count++;

            if (state->last_block->inst_count > 0) {
                SetNextBlock(state->last_block->Sentinel(), block_ptr);
            }
            state->last_block = block_ptr;
            if (block_ptr->inst_count == 0) return;

            DecodedOp* head = block_ptr->FirstOp();
#ifdef FIBERCPU_ENABLE_HANDLER_PROFILE
            state->current_block_head = head;
#endif

            if (block_ptr->entry) {
                state->mem_op.emplace<0>();
                state->intercept_exec_write_for_smc = true;
                int64_t batch_limit = 100000;
                if (max_insts != 0) {
                    uint64_t remaining_budget = max_insts - total_run_insts;
                    if (remaining_budget < (uint64_t)batch_limit) {
                        batch_limit = (int64_t)remaining_budget;
                    }
                }
                int64_t initial_batch_limit = batch_limit;
                batch_limit -= block_ptr->inst_count;
                if (state->eip_dirty) state->eip_dirty = false;

                MicroTLB utlb;
                uint64_t flags_cache = GetStateFlagsCache(state);
                int64_t remaining =
                    block_ptr->entry(state, head, batch_limit, utlb, std::numeric_limits<uint32_t>::max(), flags_cache);
                state->intercept_exec_write_for_smc = false;
                total_run_insts += (initial_batch_limit - remaining);
#ifdef FIBERCPU_ENABLE_HANDLER_PROFILE
                state->current_block_head = nullptr;
#endif
            } else {
                state->intercept_exec_write_for_smc = false;
                if (!state->hooks.on_invalid_opcode(state)) {
                    state->status = EmuStatus::Fault;
                    state->fault_vector = 6;
                }
            }
        };

        if (smc_single_step) {
            // The previous attempt yielded before the guest instruction committed.
            // Refund that speculative instruction budget before re-executing it.
            if (total_run_insts > 0) {
                total_run_insts--;
            }
            state->smc_write_to_exec = false;
            state->allow_write_exec_page = true;
            state->last_block = &state->dummy_invalid_block;
            BasicBlock* single_block = DecodeBlock(state, eip, end_eip, 1);
            if (!single_block) {
                state->allow_write_exec_page = false;
                if (state->status == EmuStatus::Running) {
                    state->status = EmuStatus::Fault;
                }
                break;
            }
            execute_block(single_block);
            state->allow_write_exec_page = false;
            state->last_block = &state->dummy_invalid_block;
            continue;
        }

        auto it = state->block_cache.find(eip);
        BasicBlock* block_ptr =
            it == state->block_cache.end() ? ResolveBlockForRunSlow(state, eip, end_eip) : it->second;
        if (!block_ptr) {
            break;
        }

        // Skip invalid blocks (shouldn't happen if we erase them on invalidation,
        // but safe to check if we change logic)
        if (!block_ptr->chain.is_valid) {
            // Re-decode? Or Fault?
            // If it's in cache but invalid, it means we messed up invalidation logic (didn't erase).
            // Let's treat it as a miss and re-decode.
            if (it != state->block_cache.end()) {
                state->block_cache.erase(it);
            } else {
                state->block_cache.erase(eip);
            }
            continue;
        }

        execute_block(block_ptr);
        if (state->status != EmuStatus::Running) break;
    }

    // Sync FPU state back
#ifdef FIBERCPU_ENABLE_HANDLER_PROFILE
    state->current_block_head = nullptr;
#endif
    f80_sync_from_soft(&state->ctx.fpu_cw, &state->ctx.fpu_sw);
}

void X86_GetBlockStats(EmuState* state, X86_BlockStats* stats) {
    if (!state || !stats) return;

    const auto& src = state->block_stats;
    stats->block_count = src.block_count;
    stats->total_block_insts = src.total_block_insts;
    std::memcpy(stats->stop_reason_counts, src.stop_reason_counts, sizeof(stats->stop_reason_counts));
    std::memcpy(stats->inst_histogram, src.inst_histogram, sizeof(stats->inst_histogram));
    stats->block_concat_attempts = src.block_concat_attempts;
    stats->block_concat_success = src.block_concat_success;
    stats->block_concat_success_direct_jmp = src.block_concat_success_direct_jmp;
    stats->block_concat_success_jcc_fallthrough = src.block_concat_success_jcc_fallthrough;
    stats->block_concat_reject_not_concat_terminal = src.block_concat_reject_not_concat_terminal;
    stats->block_concat_reject_cross_page = src.block_concat_reject_cross_page;
    stats->block_concat_reject_size_limit = src.block_concat_reject_size_limit;
    stats->block_concat_reject_loop = src.block_concat_reject_loop;
    stats->block_concat_reject_target_missing = src.block_concat_reject_target_missing;
}

void X86_GetBlockExecStats(EmuState* state, X86_BlockExecStats* stats) {
    if (!state || !stats) return;

    std::memset(stats, 0, sizeof(*stats));

    auto try_insert_top = [&](uint32_t start_eip, uint32_t inst_count, uint64_t exec_count) {
        for (auto& slot : stats->top_blocks) {
            if (exec_count > slot.exec_count) {
                for (auto it = stats->top_blocks + 7; it != &slot; --it) {
                    *it = *(it - 1);
                }
                slot = X86_HotBlock{start_eip, inst_count, exec_count};
                break;
            }
        }
    };

    for (const auto& [eip, block] : state->block_cache) {
        (void)eip;
        if (!block || block == &state->dummy_invalid_block || block->inst_count == 0) continue;
        stats->executed_block_entries += block->exec_count;
        stats->executed_inst_total += block->exec_count * block->inst_count;
        stats->exec_weighted_histogram[std::min<uint32_t>(block->inst_count, 64)] += block->exec_count;
        try_insert_top(block->chain.start_eip, block->inst_count, block->exec_count);
    }

    stats->exec_weighted_avg_block_insts =
        stats->executed_block_entries == 0
            ? 0.0
            : static_cast<double>(stats->executed_inst_total) / static_cast<double>(stats->executed_block_entries);
}

size_t X86_GetHandlerProfileCount(EmuState* state) {
#ifdef FIBERCPU_ENABLE_HANDLER_PROFILE
    return state ? state->handler_exec_counts.size() : 0;
#else
    (void)state;
    return 0;
#endif
}

size_t X86_GetHandlerProfileStats(EmuState* state, X86_HandlerProfileEntry* buffer, size_t max_count) {
#ifdef FIBERCPU_ENABLE_HANDLER_PROFILE
    const size_t total = state ? state->handler_exec_counts.size() : 0;
    if (!state || !buffer || max_count == 0) return total;

    size_t i = 0;
    for (const auto& [handler, exec_count] : state->handler_exec_counts) {
        if (i == max_count) break;
        buffer[i].handler = reinterpret_cast<void*>(handler);
        buffer[i].exec_count = exec_count;
        i++;
    }
    return total;
#else
    (void)state;
    (void)buffer;
    (void)max_count;
    return 0;
#endif
}

size_t X86_GetJccProfileCount(EmuState* state) {
#ifdef FIBERCPU_ENABLE_JCC_PROFILE
    return state ? state->jcc_profile_counts.size() : 0;
#else
    (void)state;
    return 0;
#endif
}

size_t X86_GetJccProfileStats(EmuState* state, X86_JccProfileEntry* buffer, size_t max_count) {
#ifdef FIBERCPU_ENABLE_JCC_PROFILE
    const size_t total = state ? state->jcc_profile_counts.size() : 0;
    if (!state || !buffer || max_count == 0) return total;

    size_t i = 0;
    for (const auto& [handler, counters] : state->jcc_profile_counts) {
        if (i == max_count) break;
        buffer[i].handler = reinterpret_cast<void*>(handler);
        buffer[i].taken = counters.taken;
        buffer[i].not_taken = counters.not_taken;
        buffer[i].cache_hit = counters.cache_hit;
        buffer[i].cache_miss = counters.cache_miss;
        i++;
    }
    return total;
#else
    (void)state;
    (void)buffer;
    (void)max_count;
    return 0;
#endif
}

void X86_EmuStop(EmuState* state) {
    if (state) state->status = EmuStatus::Stopped;
}

void X86_EmuFault(EmuState* state) {
    if (state) state->status = EmuStatus::Fault;
}

void X86_EmuYield(EmuState* state) {
    if (state) state->status = EmuStatus::Yield;
}

int X86_Step(EmuState* state) {
    state->status = EmuStatus::Running;
    state->last_block = &state->dummy_invalid_block;
    const bool prev_allow_write_exec_page = state->allow_write_exec_page;
    const bool prev_intercept_exec_write_for_smc = state->intercept_exec_write_for_smc;
    state->allow_write_exec_page = true;
    state->intercept_exec_write_for_smc = true;

    // Sync FPU state before starting
    f80_sync_to_soft(state->ctx.fpu_cw, state->ctx.fpu_sw);

    uint8_t buf[16];
    for (int i = 0; i < 16; ++i) {
        auto res = state->mmu.read_no_utlb<uint8_t>(state->ctx.eip + i);
        buf[i] = res.value_or(0);
        if (state->status != EmuStatus::Running) {
            state->allow_write_exec_page = prev_allow_write_exec_page;
            state->intercept_exec_write_for_smc = prev_intercept_exec_write_for_smc;
            f80_sync_from_soft(&state->ctx.fpu_cw, &state->ctx.fpu_sw);
            return (int)state->status;
        }
    }

    DecodedInstTmp inst;
    uint16_t handler_index = 0;

    if (!DecodeInstruction(buf, &inst, &handler_index)) {
        std::memset(&inst, 0, sizeof(inst));
        inst.head.SetLength(1);
        // 0x10B = UD2
        HandlerFunc ud2 = g_Handlers[0x10B];
        inst.head.handler = ud2;
    }

    inst.head.next_eip = state->ctx.eip + inst.head.GetLength();

    alignas(16) std::byte op_storage[sizeof(DecodedOp) * 2];
    std::memset(op_storage, 0, sizeof(op_storage));
    auto* head = reinterpret_cast<DecodedOp*>(op_storage);
    std::memcpy(head, &inst.head, sizeof(inst.head));

    DecodedOp sentinel{};
    HandlerFunc exit_h = g_ExitHandlersFallthrough[0];
    sentinel.handler = exit_h;
    sentinel.next_eip = head->next_eip;
    SetNextBlock(&sentinel, &state->dummy_invalid_block);
    std::memcpy(head + 1, &sentinel, sizeof(sentinel));

    // Run first op
    HandlerFunc h = head->handler;
#ifdef FIBERCPU_ENABLE_HANDLER_PROFILE
    state->current_block_head = head;
#endif

    if (h) {
        state->mem_op.emplace<0>();
        MicroTLB utlb;
        uint64_t flags_cache = GetStateFlagsCache(state);
        h(state, head, 0, utlb, std::numeric_limits<uint32_t>::max(),
          flags_cache);  // Limit 0 ensures it returns after 1 inst + sentinel
    } else {
        if (!state->hooks.on_invalid_opcode(state)) {
            state->status = EmuStatus::Fault;
            state->fault_vector = 6;
        }
    }

    // Sync FPU state back
#ifdef FIBERCPU_ENABLE_HANDLER_PROFILE
    state->current_block_head = nullptr;
#endif
    state->allow_write_exec_page = prev_allow_write_exec_page;
    state->intercept_exec_write_for_smc = prev_intercept_exec_write_for_smc;
    f80_sync_from_soft(&state->ctx.fpu_cw, &state->ctx.fpu_sw);

    return (int)state->status;
}

int X86_GetStatus(EmuState* state) { return (int)state->status; }

// ----------------------------------------------------------------------------
// Callbacks
// ----------------------------------------------------------------------------

void X86_SetFaultCallback(EmuState* state, fiberish::FaultHandler handler, void* userdata) {
    state->fault_handler = handler;
    state->fault_userdata = userdata;
}

void X86_SetMemHook(EmuState* state, MemHook hook, void* userdata) {
    state->mem_hook = hook;
    state->mem_userdata = userdata;

    if (hook) {
        state->mmu.set_mem_hook(InternalMemHookBridge, state);
    } else {
        state->mmu.set_mem_hook(nullptr, nullptr);
    }
}

void X86_SetInterruptHook(EmuState* state, uint8_t vector, fiberish::InterruptHandler hook, void* userdata) {
    state->interrupt_handlers[vector] = hook;
    state->interrupt_userdata[vector] = userdata;

    state->hooks.set_interrupt_hook(vector, [vector](EmuState* s, uint8_t v) {
        if (s->interrupt_handlers[vector]) {
            bool handled = s->interrupt_handlers[vector](s, (uint32_t)v, s->interrupt_userdata[vector]) != 0;
            return handled;
        }
        return false;
    });
}

int32_t X86_GetFaultVector(EmuState* state) {
    if (!state) return -1;
    return (state->fault_vector == 0xFF) ? -1 : (int32_t)state->fault_vector;
}

void X86_ResetAllCodeCache(EmuState* state) {
    if (state) {
        state->block_cache.clear();
        state->page_to_blocks.clear();
    }
}

void X86_FlushMmuTlb(EmuState* state) {
    if (state) {
        state->mmu.flush_tlb_only();
    }
}

void X86_ResetMemory(EmuState* state) {
    if (state) {
        state->mmu.reset_memory();
        state->block_cache.clear();
        state->page_to_blocks.clear();
    }
}

void X86_ResetCodeCacheByRange(EmuState* state, uint32_t addr, uint32_t size) {
    if (!state || size == 0) return;

    uint32_t start_page = addr >> 12;
    uint32_t end_page = (addr + size - 1) >> 12;

    for (uint32_t p = start_page; p <= end_page; ++p) {
        auto it = state->page_to_blocks.find(p);
        if (it != state->page_to_blocks.end()) {
            for (uint32_t eip : it->second) {
                auto block_it = state->block_cache.find(eip);
                if (block_it != state->block_cache.end()) {
                    block_it->second->Invalidate();
                    state->block_cache.erase(block_it);
                }
            }
            state->page_to_blocks.erase(it);
        }
    }
}

void X86_SetTscFrequency(EmuState* state, uint64_t freq) {
    if (state) state->tsc_frequency = freq;
}

void X86_SetTscMode(EmuState* state, int mode) {
    if (state) state->tsc_mode = mode;
}

void X86_SetTscOffset(EmuState* state, uint64_t offset) {
    if (state) state->tsc_offset = offset;
}

void X86_SetLogCallback(EmuState* state, X86LogCallback callback, void* userdata) {
    if (state) {
        state->log_callback = callback;
        state->log_userdata = userdata;
    }
}

void X86_GetTlbStats(EmuState* state, X86_TlbStats* stats) {
#ifdef ENABLE_TLB_STATS
    if (state && stats) {
        stats->l1_read_hits = state->mmu.stats.l1_read_hits;
        stats->l1_write_hits = state->mmu.stats.l1_write_hits;
        stats->l2_read_hits = state->mmu.stats.l2_read_hits;
        stats->l2_write_hits = state->mmu.stats.l2_write_hits;
        stats->read_misses = state->mmu.stats.read_misses;
        stats->write_misses = state->mmu.stats.write_misses;
        stats->total_reads = state->mmu.stats.total_reads;
        stats->total_writes = state->mmu.stats.total_writes;
    }
#else
    if (stats) std::memset(stats, 0, sizeof(X86_TlbStats));
#endif
}

void X86_ResetTlbStats(EmuState* state) {
#ifdef ENABLE_TLB_STATS
    if (state) state->mmu.stats.reset();
#endif
}

int X86_DumpStats(EmuState* state, char* buffer, size_t buffer_size) {
    if (!state || !buffer || buffer_size == 0) return -1;
#ifdef ENABLE_TLB_STATS
    auto& s = state->mmu.stats;
    int n = snprintf(buffer, buffer_size,
                     "{\"l1_read_hits\":%llu,\"l1_write_hits\":%llu,"
                     "\"l2_read_hits\":%llu,\"l2_write_hits\":%llu,"
                     "\"read_misses\":%llu,\"write_misses\":%llu,"
                     "\"total_reads\":%llu,\"total_writes\":%llu,"
                     "\"all_blocks_count\":%zu,\"block_cache_size\":%zu,\"page_to_blocks_size\":%zu}",
                     (unsigned long long)s.l1_read_hits, (unsigned long long)s.l1_write_hits,
                     (unsigned long long)s.l2_read_hits, (unsigned long long)s.l2_write_hits,
                     (unsigned long long)s.read_misses, (unsigned long long)s.write_misses,
                     (unsigned long long)s.total_reads, (unsigned long long)s.total_writes, state->all_blocks.size(),
                     state->block_cache.size(), state->page_to_blocks.size());
    return n;
#else
    return snprintf(buffer, buffer_size,
                    "{\"all_blocks_count\":%zu,\"block_cache_size\":%zu,\"page_to_blocks_size\":%zu}",
                    state->all_blocks.size(), state->block_cache.size(), state->page_to_blocks.size());
#endif
}

size_t X86_GetBlockCount(EmuState* state) {
    if (!state) return 0;
    return state->all_blocks.size();
}

size_t X86_GetBlockList(EmuState* state, BasicBlock** buffer, size_t max_count) {
    if (!state || !buffer || max_count == 0) return 0;

    size_t count = 0;
    for (BasicBlock* block : state->all_blocks) {
        if (count >= max_count) break;
        buffer[count] = block;
        count++;
    }
    return count;
}

void* X86_GetLibAddress() {
#if defined(_WIN32)
    HMODULE hModule = NULL;
    GetModuleHandleEx(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                      (LPCTSTR)X86_Create, &hModule);
    return (void*)hModule;
#else
    Dl_info info;
    if (dladdr((void*)X86_Create, &info)) {
        return info.dli_fbase;
    }
    return nullptr;
#endif
}

}  // extern "C"
