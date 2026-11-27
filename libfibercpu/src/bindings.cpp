#include "bindings.h"
#include "optimization_config.h"
#if !defined(__EMSCRIPTEN__) && !defined(_WIN32)
#include <execinfo.h>
#include <unistd.h>
#endif
#include <algorithm>
#include <array>
#include <cfenv>
#include <csignal>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <fstream>
#include <limits>
#include <mutex>
#include <utility>
#include "decoder.h"
#include "dispatch.h"
#if FIBERCPU_BLOCK_COMPILATION
#include "jit/block_recipe.h"
#endif
#if FIBERCPU_DYNAMIC_OPERANDS || FIBERCPU_LLVM_JIT
#include "jit/executable_arena.h"
#endif
#if FIBERCPU_LLVM_JIT
#include "jit/llvm_block_jit.h"
#endif
#if FIBERCPU_LINKED_BLOCK_CACHE
#include "jit/linked_block_cache.h"
#endif
#if FIBERCPU_DYNAMIC_OPERANDS
#include "dynamic_operand_specialization.h"
#endif
#include "hooks.h"
#include "instruction_variants.h"
#include "logger.h"
#include "mem/mmu.h"
#include "operand_specialization.h"
#include "ops.h"
#include "runtime_options.h"
#include "state.h"
#include "superopcodes.h"
#if FIBERCPU_GUEST_AARCH64
#include "aarch64/aarch64.h"
#include "aarch64_engine.h"
#endif

#if defined(__SSE__) || defined(_M_X64) || (defined(_M_IX86_FP) && _M_IX86_FP >= 1)
#include <xmmintrin.h>
#define FIBERCPU_HOST_SSE 1
#endif

#if defined(_WIN32)
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <Windows.h>
#else
#include <dlfcn.h>
#include <signal.h>
#endif

using namespace fiberish;
struct X86_MmuHandle {
    mem::MmuCore* core = nullptr;
};

// SoftFloat's state is already TLS, but it must not retain a previous engine's
// rounding/precision/flags on a reused worker thread. Stack-scoped restoration
// also preserves an outer engine when a callback executes another engine.
class ScopedFloatEnvironment {
public:
    explicit ScopedFloatEnvironment(EmuState* state) : state_(state), soft_environment_(f80_save_environment()) {
#if defined(FIBERCPU_HOST_SSE)
        host_mxcsr_ = _mm_getcsr();
#endif
        host_environment_saved_ = std::fegetenv(&host_environment_) == 0;
        if (host_environment_saved_) std::fesetenv(FE_DFL_ENV);
#if defined(FIBERCPU_HOST_SSE)
        // Native SSE/SIMDe helpers must neither inherit host rounding/DAZ/FTZ
        // nor deliver the host's unmasked floating-point exceptions.
        _mm_setcsr(0x1F80);
#endif
        f80_sync_to_soft(state_->ctx.fpu_cw, state_->ctx.fpu_sw);
    }

    ~ScopedFloatEnvironment() {
        f80_sync_from_soft(&state_->ctx.fpu_cw, &state_->ctx.fpu_sw);
        f80_restore_environment(soft_environment_);
        if (host_environment_saved_) std::fesetenv(&host_environment_);
#if defined(FIBERCPU_HOST_SSE)
        // Some CRT fenv implementations do not preserve every MXCSR bit.
        _mm_setcsr(host_mxcsr_);
#endif
    }

    ScopedFloatEnvironment(const ScopedFloatEnvironment&) = delete;
    ScopedFloatEnvironment& operator=(const ScopedFloatEnvironment&) = delete;

private:
    EmuState* state_;
    uint32_t soft_environment_;
    std::fenv_t host_environment_{};
    bool host_environment_saved_ = false;
#if defined(FIBERCPU_HOST_SSE)
    uint32_t host_mxcsr_ = 0;
#endif
};

// Guest execution and all its callbacks share one host thread. Do not let a
// callback run another engine (or Step) while block pointers are borrowed.
static thread_local bool g_native_execution_active = false;

void BasicBlock::Invalidate() {
    if (!is_valid()) return;
#if FIBERCPU_LLVM_JIT && FIBERCPU_GUEST_I386
    if (chain.jit_execution_profile) {
        TraceLLVMJitRetirement(*this);
        chain.jit_execution_profile = false;
    }
#endif
    set_valid(false);
    ++invalidation_revision;
    // No unlinking needed because chained exits validate the cached block header on dispatch.
}

class ScopedNativeExecution {
public:
    explicit ScopedNativeExecution(EmuState* state) : state_(state) {
        if (!state_ || g_native_execution_active || state_->native_execution_active) return;
        g_native_execution_active = true;
        state_->native_execution_active = true;
        state_->mmu.begin_execution();
        entered_ = true;
    }

    ~ScopedNativeExecution() {
        if (!entered_) return;
        state_->clear_pending_mem_op();
        state_->mmu.end_execution();
        state_->native_execution_active = false;
        g_native_execution_active = false;
    }

    explicit operator bool() const { return entered_; }

private:
    EmuState* state_;
    bool entered_ = false;
};

#if FIBERCPU_GUEST_AARCH64
template <class Mapping>
static size_t CollectArmMappedPages(EmuState* state, uint64_t address, uint64_t size, Mapping* buffer,
                                    size_t capacity) {
    if (!state || !buffer || !capacity || !size) return 0;
    size_t count = 0;
    state->mmu.visit_arm_mappings(address, size, [&](uint64_t page, mem::HostAddr backing, mem::Property perms) {
        auto& slot = buffer[count++];
        slot.guest_page = static_cast<decltype(slot.guest_page)>(page);
        slot.host_page = backing;
        slot.perms = static_cast<uint8_t>(perms & mem::Property::RWX);
        slot.flags = mem::has_property(perms, mem::Property::Dirty) ? X86_PAGE_FLAG_DIRTY : 0;
        slot.reserved = 0;
        return count < capacity;
    });
    return count;
}
#endif

extern "C" {

// ----------------------------------------------------------------------------
// Internal Bridge Callbacks
// ----------------------------------------------------------------------------

static bool FaultTraceEnabled() {
    static const bool enabled = []() {
        const char* v = std::getenv("FIBERISH_TRACE_DECODE_FAULT");
        return v && v[0] != '\0' && std::strcmp(v, "0") != 0;
    }();
    return enabled;
}

static bool FatalSignalHandlerEnabled() {
    static const bool enabled = []() {
        const char* disable_vars[] = {
            "FIBERCPU_DISABLE_FATAL_SIGNAL_HANDLER",
            "FIBERISH_DISABLE_FATAL_SIGNAL_HANDLER",
        };
        for (const char* name : disable_vars) {
            const char* value = std::getenv(name);
            if (value && value[0] != '\0' && std::strcmp(value, "0") != 0) {
                return false;
            }
        }
        return true;
    }();
    return enabled;
}

static int InternalFaultBridge(void* opaque, uint32_t addr, int requested) {
    auto* state = static_cast<EmuState*>(opaque);
    const uint8_t previous_vector = state->fault_vector;
    const uint32_t previous_address = state->fault_addr;
    state->fault_vector = 14;  // #PF
    state->fault_addr = addr;
    const auto property = static_cast<mem::Property>(requested);
    const bool write = mem::has_property(property, mem::Property::Write);
    if (FaultTraceEnabled()) {
        fprintf(stderr, "[Bridge] Fault addr=0x%x write=%d\n", addr, write);
    }
    if (state->fault_handler) {
        int access = write ? 1 : 0;
        if (state->extended_fault_callback) {
            const bool exec = mem::has_property(property, mem::Property::Exec);
            if (exec) access |= 2;
            // A replay can repeat an earlier data hook from the same instruction.
            if (exec || (state->can_restart_memory_fault && !state->mem_hook)) access |= 4;
        }
        int res = state->fault_handler(state, addr, access, state->fault_userdata);
        if (res && state->status != EmuStatus::Fault) {
            state->fault_vector = previous_vector;
            state->fault_addr = previous_address;
        }
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
    state->mmu.invalidate_code_cache_page(page_addr);
}

static void InternalSmcBridge(void* opaque, uint32_t addr) {
    EmuState* state = static_cast<EmuState*>(opaque);
    // Invalidate the page containing 'addr'
    X86_InvalidateCodeCacheByPage(state, addr);
}

#if FIBERCPU_GUEST_I386
static bool BlockCrossesPage(const BasicBlock* block) {
    if (!block || block->inst_count() == 0 || block->end_eip <= block->start_eip()) return false;
    return ((block->start_eip() ^ (block->end_eip - 1)) & 0xFFFFF000u) != 0;
}

static bool BlockIsConcatCandidateTerminal(const BasicBlock* block) {
    if (!block) return false;
    return block->terminal_kind() == BlockTerminalKind::DirectJmpRel ||
           block->terminal_kind() == BlockTerminalKind::DirectJccRel;
}

static bool BlockFormsSmallLoopWith(const BasicBlock* source, const BasicBlock* target) {
    if (!source || !target) return false;
    if (source->start_eip() == target->start_eip()) return true;
    return target->BranchTargetEip() == source->start_eip();
}

static BasicBlock* CacheDecodedBlock(EmuState* state, uint32_t cache_eip, BasicBlock* block) {
    return state->mmu.cache_decoded_block(cache_eip, block);
}

static BasicBlock* LookupOrDecodeBlockConcatSuccessor(EmuState* state, uint32_t eip, uint32_t end_eip) {
    BasicBlock* cached_block = state->mmu.lookup_cached_block(eip);
    if (cached_block && cached_block->is_valid()) return cached_block;

    const EmuStatus saved_status = state->status;
    const uint8_t saved_fault_vector = state->fault_vector;
    const uint32_t saved_fault_addr = state->fault_addr;
    BasicBlock* block = DecodeBlock(state, eip, end_eip, 0, cached_block);
    state->status = saved_status;
    state->fault_vector = saved_fault_vector;
    state->fault_addr = saved_fault_addr;
    if (!block) return nullptr;
    return CacheDecodedBlock(state, eip, block);
}

static bool CanBlockConcatWithSuccessor(const BasicBlock* a, const BasicBlock* b, bool remove_source_terminal_inst,
                                        BlockStats* stats) {
    if (!a || !b || !a->is_valid() || !b->is_valid() || a->inst_count() == 0 || b->inst_count() == 0) {
        stats->block_concat_reject_target_missing++;
        return false;
    }

    if (BlockCrossesPage(a) || BlockCrossesPage(b) || ((a->start_eip() ^ b->start_eip()) & 0xFFFFF000u) != 0) {
        stats->block_concat_reject_cross_page++;
        return false;
    }

    if (BlockFormsSmallLoopWith(a, b)) {
        stats->block_concat_reject_loop++;
        return false;
    }

    const uint32_t removed_inst_count = remove_source_terminal_inst ? 1u : 0u;
    const uint32_t concat_inst_count = a->inst_count() - removed_inst_count + b->inst_count();

    if (concat_inst_count > 64) {
        stats->block_concat_reject_size_limit++;
        return false;
    }

    return true;
}

static BasicBlock* BuildBlockConcat(EmuState* state, const BasicBlock* a, const BasicBlock* b, bool remove_terminal) {
    std::array<DecodedInstruction, 65> staged_ops;
    const uint32_t source_count = a->inst_count() - (remove_terminal ? 1u : 0u);
    const uint32_t concat_count = source_count + b->inst_count();
    const uint32_t start_eip = a->start_eip();
    auto ops = std::span(staged_ops).first(concat_count + 1);

    for (uint32_t i = 0; i < source_count; ++i) {
        ops[i] = a->ReadOp(i);
    }
    for (uint32_t i = 0; i < b->inst_count(); ++i) {
        ops[source_count + i] = b->ReadOp(i);
    }
    ops.back() = b->ReadOp(b->inst_count());

    const bool retain_scalar_handlers = a->HasUnfusedHandlers() && b->HasUnfusedHandlers();
    auto unfused = BlockHandlerStorage::None;
    if (retain_scalar_handlers) unfused = std::max(a->unfused_storage, b->unfused_storage);
#if FIBERCPU_BLOCK_COMPILATION
    if (retain_scalar_handlers) {
        for (uint32_t i = 0; i < source_count; ++i) {
            ops[i].handler = a->UnfusedHandler(i);
            ops[i].meta.flags.operand_lowered = 0;
        }
        for (uint32_t i = 0; i < b->inst_count(); ++i) {
            auto& op = ops[source_count + i];
            op.handler = b->UnfusedHandler(i);
            op.meta.flags.operand_lowered = 0;
        }
    }
#endif
    const auto layout = BlockSlotLayout::Inspect(start_eip, ops);
    const bool retain_region_context = a->chain.has_region_context && b->chain.has_region_context;
    const bool retain_exec_count = a->chain.has_exec_count;
    void* mem = state->mmu.try_allocate_block_bytes(
        BasicBlock::CalculateSize(layout, unfused, retain_region_context, retain_exec_count), start_eip);
    if (!mem) return nullptr;
    // Static scalar lowering depends on each instruction's original operands.
    // In this build the published slots already carry the correct handlers.
#if !FIBERCPU_ENABLE_SUPEROPCODES && !FIBERCPU_DYNAMIC_OPERANDS
    const bool preserve_handlers = !retain_scalar_handlers;
#else
    constexpr bool preserve_handlers = false;
#endif
    if (!preserve_handlers) ApplySuperOpcodesToBlockOps(ops.data(), concat_count, !retain_scalar_handlers);
    auto* concat = new (mem) BasicBlock;
    state->mmu.remember_allocated_block(concat);

    concat->set_start_eip(start_eip);
    concat->set_valid(true);
    concat->end_eip = b->end_eip;
    concat->set_inst_count(concat_count);
    concat->set_terminal_kind(b->terminal_kind());
    concat->unfused_storage = unfused;
    concat->chain.has_region_context = retain_region_context;
    concat->chain.has_exec_count = retain_exec_count;
    concat->WriteOps(layout, ops, state->mmu.invalid_code_block(), preserve_handlers);
    if (retain_region_context) concat->SetRegionContext(nullptr);
    if (retain_exec_count) concat->ExecCountStorage() = 0;

#if FIBERCPU_BLOCK_COMPILATION
    if (retain_scalar_handlers) {
        if (unfused == BlockHandlerStorage::Indexed) {
            auto* indices = concat->UnfusedIndices();
            std::copy_n(a->UnfusedIndices(), source_count, indices);
            std::copy_n(b->UnfusedIndices(), b->inst_count(), indices + source_count);
        } else {
            auto* handlers = reinterpret_cast<HandlerFunc*>(concat->UnfusedFooter());
            for (uint32_t i = 0; i < source_count; ++i) handlers[i] = a->UnfusedHandler(i);
            for (uint32_t i = 0; i < b->inst_count(); ++i) handlers[source_count + i] = b->UnfusedHandler(i);
        }
        concat->chain.weighted_profile = a->chain.weighted_profile;
        concat->chain.llvm_hot_count =
            concat->chain.weighted_profile ? block_recipe::HotThreshold(*state) : a->chain.llvm_hot_count;
    }
#endif
    return concat;
}

static __attribute__((noinline, cold)) BasicBlock* ResolveBlockForRunSlow(EmuState* state, uint32_t eip,
                                                                          uint32_t end_eip, BasicBlock* cached_block,
                                                                          bool* use_single_step) {
    const uint64_t reset_request_epoch = state->mmu.code_cache().reset_request_epoch;
    BasicBlock* new_block = DecodeBlock(state, eip, end_eip, 0, cached_block, use_single_step);

    if (!new_block) {
        if (*use_single_step) return nullptr;
        if (FaultTraceEnabled()) {
            fprintf(stderr, "[RunDbg] DecodeBlock returned null eip=%08X status=%d fault_vector=%u fault_addr=%08X\n",
                    eip, (int)state->status, (unsigned)state->fault_vector, state->fault_addr);
        }
        if (state->status == EmuStatus::Running) {
            state->status = EmuStatus::Fault;
        }
        return nullptr;
    }

    if (new_block->entry != new_block->FirstOp()->handler) {
        return CacheDecodedBlock(state, eip, new_block);
    }
    if (!BlockIsConcatCandidateTerminal(new_block)) {
        state->block_stats.block_concat_reject_not_concat_terminal++;
        return CacheDecodedBlock(state, eip, new_block);
    }

    state->block_stats.block_concat_attempts++;
    const bool is_direct_jmp = new_block->terminal_kind() == BlockTerminalKind::DirectJmpRel;
    const uint32_t successor_eip = is_direct_jmp ? new_block->BranchTargetEip() : new_block->end_eip;
    if (successor_eip == eip) {
        state->block_stats.block_concat_reject_loop++;
        return CacheDecodedBlock(state, eip, new_block);
    }

    const uint64_t new_block_generation = state->mmu.code_cache().generation;
    BasicBlock* successor_block = LookupOrDecodeBlockConcatSuccessor(state, successor_eip, end_eip);
    if (state->mmu.code_cache().reset_request_epoch != reset_request_epoch) {
        *use_single_step = true;
        return nullptr;
    }
    if (!successor_block) {
        state->block_stats.block_concat_reject_target_missing++;
        return CacheDecodedBlock(state, eip, new_block);
    }
    if (state->mmu.code_cache().generation != new_block_generation) {
        *use_single_step = true;
        return nullptr;
    }

    if (!CanBlockConcatWithSuccessor(new_block, successor_block, is_direct_jmp, &state->block_stats)) {
        return CacheDecodedBlock(state, eip, new_block);
    }

    BasicBlock* concat_block = BuildBlockConcat(state, new_block, successor_block, is_direct_jmp);
    // The budget warning itself invokes a host log callback. An explicit
    // reset from that callback must not publish the pre-reset source block.
    if (state->mmu.code_cache().reset_request_epoch != reset_request_epoch) {
        *use_single_step = true;
        return nullptr;
    }
    if (!concat_block) return CacheDecodedBlock(state, eip, new_block);
    state->block_stats.block_concat_success++;
    if (is_direct_jmp) {
        state->block_stats.block_concat_success_direct_jmp++;
    } else {
        state->block_stats.block_concat_success_jcc_fallthrough++;
    }
    return CacheDecodedBlock(state, eip, concat_block);
}

#endif

// Signal Handler for safety
#if !defined(__EMSCRIPTEN__) && !defined(_WIN32)
static void SignalHandler(int sig, siginfo_t* info, void* /*ucontext*/) {
    void* array[20];
    size_t size;
    size = backtrace(array, 20);
    void* fault_addr = info ? info->si_addr : nullptr;
    fprintf(stderr, "\n[CRASH] Signal %d caught", sig);
    if (fault_addr) fprintf(stderr, " at host addr=%p", fault_addr);
    fprintf(stderr, ":\n");
    backtrace_symbols_fd(array, size, STDERR_FILENO);
    _exit(1);
}

static void RegisterFatalSignalHandler(int sig) {
    struct sigaction sa{};
    sa.sa_sigaction = SignalHandler;
    sigemptyset(&sa.sa_mask);
    sa.sa_flags = SA_SIGINFO | SA_RESTART;
    sigaction(sig, &sa, nullptr);
}
#endif

// ----------------------------------------------------------------------------
// Creation / Destruction
// ----------------------------------------------------------------------------

EmuState* X86_Create() { return X86_CreateWithOptions(0); }

EmuState* X86_CreateForRun(const X86_RunContext* run) {
    if (!run) return nullptr;
    auto* state = X86_Create();
    auto& cache = state->mmu.code_cache();
    cache.SetBlockBudgetBytes(run->options->block_cache_bytes);
    cache.run_options = run->options;
    return state;
}

EmuState* X86_CreateWithOptions(uint32_t options) {
    if (!(options & X86_CREATE_NO_FATAL_SIGNAL_HANDLERS) && FatalSignalHandlerEnabled()) {
#if !defined(__EMSCRIPTEN__) && !defined(_WIN32)
        static std::once_flag signal_registration;
        std::call_once(signal_registration, []() {
            RegisterFatalSignalHandler(SIGSEGV);
            RegisterFatalSignalHandler(SIGILL);
            RegisterFatalSignalHandler(SIGBUS);
        });
#endif
    }

    EmuState* state = new EmuState();
    // Zero entire context first
    state->ctx = {};

    // Set default EFLAGS and Mask
    SetStateFlagsCache(state, InitFlagsCache(0x202));  // IF=1, Reserved=1
    state->ctx.eflags_mask = 0x240DD5;

    // Default FPU State
    state->ctx.fpu_cw = 0x037F;
    // Hooks initialized by default constructor of HookManager

    state->ctx.fpu_sw = 0x0000;
    state->ctx.fpu_tw = 0xFFFF;
    state->ctx.fpu_top = 0;
    state->ctx.mxcsr = 0x1F80;

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
#if FIBERCPU_GUEST_AARCH64
    if (parent->arm) {
        state->arm = std::make_shared<aarch64::State>(*parent->arm);
        state->arm->active_run = nullptr;  // The invocation frame belongs to the parent's native stack.
        state->arm->ClearExclusive();
    }
    if (parent->arm_mmu) {
        state->mmu.set_arm_fault_callback(parent->arm_mmu->fault_handler, parent->arm_mmu->fault_userdata);
        state->mmu.set_arm_mem_hook(parent->arm_mmu->mem_hook, parent->arm_mmu->mem_userdata);
    }
#endif

    // 2. Memory Handling
    if (share_mem) {
        // Shared Memory (CLONE_VM) -> Threads
        (void)state->mmu.attach_core(parent->mmu.core_handle(), true);
    } else {
        // Independent Memory (Fork)
        // The child borrows the same backing pages through its own page table.
        (void)state->mmu.attach_core(mem::Mmu::CloneCoreMappings(parent->mmu.core_handle()), false);
    }

    // 3. Set Callbacks (Bridge to same handlers, but new 'state' passed)
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

    return state;
}

int X86_Destroy(EmuState* state) {
    if (state && state->native_execution_active) return 0;
    if (state) {
        // MmuCore owns the block pool/code cache lifetime and will release it
        // when the last attached engine or external MMU handle goes away.
        delete state;
    }
    return 1;
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

uint32_t X86_GetMXCSR(EmuState* state) { return state->ctx.mxcsr; }
void X86_SetMXCSR(EmuState* state, uint32_t val) { state->ctx.mxcsr = val; }

// ----------------------------------------------------------------------------
// FPU Access
// ----------------------------------------------------------------------------

uint16_t X86_GetFCW(EmuState* state) { return state->ctx.fpu_cw; }
void X86_SetFCW(EmuState* state, uint16_t val) { state->ctx.fpu_cw = val; }
uint16_t X86_GetFSW(EmuState* state) { return state->ctx.fpu_sw; }
void X86_SetFSW(EmuState* state, uint16_t val) {
    state->ctx.fpu_sw = val;
    state->ctx.fpu_top = (val >> 11) & 7;
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

#if FIBERCPU_GUEST_AARCH64
static uint64_t CompatRangeSize(uint32_t address, uint32_t size) {
    return std::min(static_cast<uint64_t>(size), (uint64_t{1} << 32) - address);
}

int AArch64_MemMapHostPage(EmuState* state, uint64_t address, void* host_page, uint8_t perms) {
    return state && state->mmu.map_arm_host_page(address, static_cast<mem::HostAddr>(host_page), perms) ? 1 : 0;
}

void AArch64_MemUnmap(EmuState* state, uint64_t address, uint64_t size) {
    if (!state) return;
    state->mmu.enable_arm_address_space();
    state->mmu.unmap_arm_range(address, size);
}

void AArch64_MemProtect(EmuState* state, uint64_t address, uint64_t size, uint8_t perms) {
    if (!state) return;
    state->mmu.enable_arm_address_space();
    state->mmu.protect_arm_mapped_range(address, size, perms);
}

void AArch64_MemWrite(EmuState* state, uint64_t address, const uint8_t* data, uint32_t size) {
    if (!state || !data || !size || !mem::ArmAddressRangeValid(address, size)) return;
    state->mmu.enable_arm_address_space();
    for (uint32_t i = 0; i < size; ++i) (void)state->mmu.write_arm_no_utlb<uint8_t>(address + i, data[i]);
}

void AArch64_MemRead(EmuState* state, uint64_t address, uint8_t* data, uint32_t size) {
    if (!state || !data || !size || !mem::ArmAddressRangeValid(address, size)) return;
    state->mmu.enable_arm_address_space();
    for (uint32_t i = 0; i < size; ++i) data[i] = state->mmu.read_arm_no_utlb<uint8_t>(address + i).value_or(0);
}

int AArch64_MemIsDirty(EmuState* state, uint64_t address) {
    if (!state) return 0;
    state->mmu.enable_arm_address_space();
    return mem::has_property(state->mmu.get_arm_property(address), mem::Property::Dirty) ? 1 : 0;
}

int AArch64_MemHasSlowWrite(EmuState* state, uint64_t address) {
    if (!state) return 0;
    state->mmu.enable_arm_address_space();
    return mem::has_property(state->mmu.get_arm_property(address), mem::Property::ForceWriteSlow) ? 1 : 0;
}

void* AArch64_ResolvePtrForRead(EmuState* state, uint64_t address) {
    if (!state) return nullptr;
    state->mmu.enable_arm_address_space();
    return state->mmu.resolve_arm_safe_for_read(address);
}

void* AArch64_ResolvePtrForWrite(EmuState* state, uint64_t address) {
    if (!state) return nullptr;
    state->mmu.enable_arm_address_space();
    return state->mmu.resolve_arm_safe_for_write(address);
}

size_t AArch64_CollectMappedPages(EmuState* state, uint64_t address, uint64_t size, AArch64_PageMapping* buffer,
                                  size_t max_count) {
    if (state) state->mmu.enable_arm_address_space();
    return CollectArmMappedPages(state, address, size, buffer, max_count);
}

void AArch64_SetExtendedFaultCallback(EmuState* state, AArch64_FaultHandler handler, void* userdata) {
    if (state && (handler || state->arm_mmu)) state->mmu.set_arm_fault_callback(handler, userdata);
}

void AArch64_SetMemHook(EmuState* state, AArch64_MemHook hook, void* userdata) {
    if (state && (hook || state->arm_mmu)) state->mmu.set_arm_mem_hook(hook, userdata);
}

uint64_t AArch64_GetFaultAddress(EmuState* state) { return state ? state->arm_fault_address : 0; }

void AArch64_ResetCodeCacheByRange(EmuState* state, uint64_t address, uint64_t size) {
    if (!state) return;
    state->mmu.enable_arm_address_space();
    state->mmu.invalidate_arm_code_cache_range(address, size);
}
#endif

int X86_MemMapHostPage(EmuState* state, uint32_t addr, void* host_page, uint8_t perms) {
#if FIBERCPU_GUEST_AARCH64
    if (state && state->mmu.has_arm_address_space()) return AArch64_MemMapHostPage(state, addr, host_page, perms);
#endif
    return state && state->mmu.map_host_page(addr, static_cast<mem::HostAddr>(host_page), perms) ? 1 : 0;
}

void X86_MemProtect(EmuState* state, uint32_t addr, uint32_t size, uint8_t perms) {
#if FIBERCPU_GUEST_AARCH64
    if (state && state->mmu.has_arm_address_space())
        return AArch64_MemProtect(state, addr, CompatRangeSize(addr, size), perms);
#endif
    if (state) state->mmu.protect_mapped_range(addr, size, perms);
}

static X86_MmuHandle* X86_NewMmuHandle(mem::MmuCore* core, bool add_ref) {
    if (!core) return nullptr;
    if (add_ref) mem::Mmu::RetainCore(core);
    auto* handle = new X86_MmuHandle();
    handle->core = core;
    return handle;
}

X86_MmuHandle* X86_MmuCreateEmpty() { return X86_NewMmuHandle(mem::Mmu::CreateEmptyCore(), false); }

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
    if (!detached_core) return nullptr;
    return X86_NewMmuHandle(detached_core, false);
}

int X86_EngineAttachMmu(EmuState* state, X86_MmuHandle* mmu) {
    if (!state || !mmu || !mmu->core) return 0;
    return state->mmu.attach_core(mmu->core, true) ? 1 : 0;
}

// execve commits a new address space from inside a running guest syscall. The
// old core is kept referenced until the current native run unwinds.
int X86_EngineAttachMmuForExec(EmuState* state, X86_MmuHandle* mmu) {
    if (!state || !mmu || !mmu->core) return 0;
    const auto* previous = state->mmu.core_handle();
    // The current dispatch already holds the execution lock for this run.
    if (previous && previous->code_cache.run_options != mmu->core->code_cache.run_options) return 0;
    return state->mmu.attach_core_for_exec(mmu->core, true) ? 1 : 0;
}

void X86_MemUnmap(EmuState* state, uint32_t addr, uint32_t size) {
    if (!state || size == 0) return;
#if FIBERCPU_GUEST_AARCH64
    if (state->mmu.has_arm_address_space()) return AArch64_MemUnmap(state, addr, CompatRangeSize(addr, size));
#endif
    auto host_page_bases = state->mmu.collect_host_page_bases_for_guest_range(addr, size);
    state->mmu.munmap(addr, size);
    state->mmu.invalidate_code_cache_host_pages(host_page_bases);
}

void X86_MemWrite(EmuState* state, uint32_t addr, const uint8_t* data, uint32_t size) {
    if (!state || !data || size == 0) return;
#if FIBERCPU_GUEST_AARCH64
    if (state->mmu.has_arm_address_space())
        return AArch64_MemWrite(state, addr, data, static_cast<uint32_t>(CompatRangeSize(addr, size)));
#endif

    state->mmu.sync_runtime_tlb_shootdowns();
    [[maybe_unused]] auto runtime_tlb_batch = state->mmu.scoped_runtime_tlb_publish_batch();
    for (uint32_t i = 0; i < size; ++i) {
        // TODO: We need a way to notify the write is failed
        (void)state->mmu.write_no_utlb<uint8_t>(addr + i, data[i]);
    }
}

void X86_MemRead(EmuState* state, uint32_t addr, uint8_t* val, uint32_t size) {
    if (!state || !val || size == 0) return;
#if FIBERCPU_GUEST_AARCH64
    if (state->mmu.has_arm_address_space())
        return AArch64_MemRead(state, addr, val, static_cast<uint32_t>(CompatRangeSize(addr, size)));
#endif

    state->mmu.sync_runtime_tlb_shootdowns();
    for (uint32_t i = 0; i < size; ++i) {
        // TODO: We need a way to notify the read is failed
        auto res = state->mmu.read_no_utlb<uint8_t>(addr + i);
        val[i] = res.value_or(0);
    }
}

int X86_MemIsDirty(EmuState* state, uint32_t addr) {
#if FIBERCPU_GUEST_AARCH64
    if (state && state->mmu.has_arm_address_space()) return AArch64_MemIsDirty(state, addr);
#endif
    mem::Property p = state->mmu.get_property(addr);
    return mem::has_property(p, mem::Property::Dirty) ? 1 : 0;
}

int X86_MemHasSlowWrite(EmuState* state, uint32_t addr) {
#if FIBERCPU_GUEST_AARCH64
    if (state && state->mmu.has_arm_address_space()) return AArch64_MemHasSlowWrite(state, addr);
#endif
    mem::Property p = state->mmu.get_property(addr);
    return mem::has_property(p, mem::Property::ForceWriteSlow) ? 1 : 0;
}

void* X86_ResolvePtrForRead(EmuState* state, uint32_t addr) {
    if (!state) return nullptr;
#if FIBERCPU_GUEST_AARCH64
    if (state->mmu.has_arm_address_space()) return AArch64_ResolvePtrForRead(state, addr);
#endif
    return state->mmu.resolve_safe_for_read(addr);
}

void* X86_ResolvePtrForWrite(EmuState* state, uint32_t addr) {
    if (!state) return nullptr;
#if FIBERCPU_GUEST_AARCH64
    if (state->mmu.has_arm_address_space()) return AArch64_ResolvePtrForWrite(state, addr);
#endif
    return state->mmu.resolve_safe_for_write(addr);
}

size_t X86_CollectMappedPages(EmuState* state, uint32_t addr, uint32_t size, X86_PageMapping* buffer,
                              size_t max_count) {
#if FIBERCPU_GUEST_AARCH64
    if (state && state->mmu.has_arm_address_space())
        return CollectArmMappedPages(state, addr, CompatRangeSize(addr, size), buffer, max_count);
#endif
    auto* page_dir = state ? state->mmu.page_directory() : nullptr;
    if (!state || !buffer || max_count == 0 || size == 0 || !page_dir) return 0;

    constexpr uint64_t kPageSize = static_cast<uint64_t>(mem::PAGE_SIZE);
    constexpr uint64_t kPageMask = static_cast<uint64_t>(~mem::PAGE_MASK);

    const uint64_t start_page = static_cast<uint64_t>(addr) & kPageMask;
    const uint64_t end_exclusive = std::min(uint64_t{addr} + size, uint64_t{1} << 32);
    const uint64_t end_page = (end_exclusive + mem::PAGE_MASK) & kPageMask;

    size_t count = 0;
    for (uint64_t page = start_page; page < end_page; page += kPageSize) {
        const uint32_t page_addr = static_cast<uint32_t>(page);
        const uint32_t l1_idx = page_addr >> 22;
        const uint32_t l2_idx = (page_addr >> 12) & 0x3FF;

        auto& chunk = page_dir->l1_directory[l1_idx];
        if (!chunk) continue;

        auto* page_ptr = chunk->pages[l2_idx];
        if (!page_ptr) continue;

        const auto perms = chunk->permissions[l2_idx];
        uint8_t flags = 0;
        if (mem::has_property(perms, mem::Property::Dirty)) flags |= X86_PAGE_FLAG_DIRTY;

        buffer[count].guest_page = page_addr;
        buffer[count].perms = static_cast<uint8_t>(perms & mem::Property::RWX);
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

// Unlike DecodeBlock(..., 1), this has no block-pool allocation. Keep the
// caller's status, counters, and SMC policy intact (X86_Step resets those at
// its public API boundary, so it cannot be called from inside X86_Run).
#if FIBERCPU_GUEST_I386
static bool ExecuteSingleInstructionOnStack(EmuState* state) {
#ifdef FIBERCPU_ENABLE_SEQUENCE_PROFILE
    state->sequence_expected_op = nullptr;
    state->sequence_history_length = 0;
#endif
    uint8_t buf[15] = {};
    DecodedInstTmp inst{};
    uint16_t handler_index = 0;
    const auto decode_result = FetchAndDecodeInstruction(state, state->ctx.eip, buf, &inst, &handler_index);
    if (decode_result == InstructionDecodeResult::FetchFault) return false;
    if (decode_result != InstructionDecodeResult::Success) {
        inst = {};
        inst.head.SetLength(0);
        inst.head.handler = g_Handlers[0x10B];
        inst.head.next_eip = state->ctx.eip;
    } else {
        FinalizeDecodedInstruction(state, state->ctx.eip, handler_index, &inst.head);
    }

    std::array<DecodedInstruction, 2> ops{inst.head, {}};
    if (inst.head.len) ApplySuperOpcodesToBlockOps(ops.data(), 1);
    const uint8_t subop = (inst.head.modrm >> 3) & 7;
    const bool indirect_branch = handler_index == 0xFF && (subop == 2 || subop == 4);
    ops.back().handler = indirect_branch ? g_ExitHandlersBranch[0] : g_ExitHandlersFallthrough[0];
    ops.back().next_eip = inst.head.next_eip;
    SetExtKind(&ops.back(), ExtKind::Link);

    alignas(BasicBlock) std::byte block_storage[BasicBlock::CalculateSize({2, 1, 1})];
    auto* scratch = new (block_storage) BasicBlock;
    scratch->set_start_eip(state->ctx.eip);
    scratch->WriteOps(BlockSlotLayout::Inspect(state->ctx.eip, ops), ops, state->mmu.invalid_code_block());
    auto* head = scratch->FirstOp();
    auto* previous_block = state->last_block;
    state->last_block = scratch;

#ifdef FIBERCPU_ENABLE_HANDLER_PROFILE
    state->current_block_head = head;
#endif
    if (head->handler) {
        state->clear_pending_mem_op();
        uint64_t utlb_tags = mem::InvalidMicroTlbAbiTags();
        uint64_t utlb_addend = 0;
        uint64_t flags_cache = GetStateFlagsCache(state);
        head->handler(state, head, 0, utlb_tags, utlb_addend, std::numeric_limits<uint32_t>::max(),
                      flags_cache FIBERCPU_PIN_INIT);
    } else if (!state->hooks.on_invalid_opcode(state)) {
        state->status = EmuStatus::Fault;
        state->fault_vector = 6;
    }
#ifdef FIBERCPU_ENABLE_HANDLER_PROFILE
    state->current_block_head = nullptr;
#endif
    state->clear_pending_mem_op();
    state->last_block = previous_block;
    return true;
}

int X86_Run(EmuState* state, uint32_t end_eip, uint64_t max_insts) {
    if (!state || g_native_execution_active || state->native_execution_active) return 0;
#if FIBERCPU_GUEST_AARCH64
    if (state->mmu.has_arm_address_space()) [[unlikely]]
        state->mmu.enable_i386_address_space();
#endif
#if FIBERCPU_LLVM_JIT
    ScopedLLVMJitExecution jit_execution(*state);
#endif
    state->mmu.flush_pending_code_cache_before_run();
    ScopedNativeExecution execution(state);
#ifdef FIBERCPU_ENABLE_SEQUENCE_PROFILE
    state->sequence_expected_op = nullptr;
    state->sequence_history_length = 0;
#endif
    ScopedFloatEnvironment float_environment(state);
    state->status = EmuStatus::Running;
    state->fault_vector = 0xFF;
    state->fault_addr = 0;
    state->eip_dirty = false;
    state->clear_pending_mem_op();
    state->block_stats = {};
#if defined(FIBERCPU_ENABLE_HANDLER_PROFILE)
    state->handler_exec_counts.clear();
    state->current_block_head = nullptr;
#endif
    uint64_t total_run_insts = 0;
    uint32_t total_rep_iterations = 0;
    state->rep_iterations = 0;
    constexpr uint32_t rep_slice_limit = 65536;

    // Reset chaining state for this run
    state->last_block = state->mmu.invalid_code_block();
#if FIBERCPU_BLOCK_COMPILATION
    state->llvm_hot_block = nullptr;
    const auto compile_hot_block = [&] {
#if FIBERCPU_LLVM_JIT
        PollLLVMBlockCompilations(state);
#endif
        auto* block = std::exchange(state->llvm_hot_block, nullptr);
        if (!block) return;
#if FIBERCPU_LINKED_BLOCK_CACHE
        if (!block->chain.weighted_profile && TryLinkedBlockCache(*state, *block)) return;
#endif
#if FIBERCPU_LLVM_JIT
        CompileHotLLVMBlock(state, block);
#endif
    };
#endif
    state->smc_write_to_exec = false;
    state->allow_write_exec_page = false;
    state->intercept_exec_write_for_smc = false;

    state->mmu.sync_runtime_tlb_shootdowns();
    [[maybe_unused]] auto runtime_tlb_batch = state->mmu.scoped_runtime_tlb_publish_batch();

    while (state->status == EmuStatus::Running) {
        uint32_t eip = state->ctx.eip;
        const bool smc_single_step = state->smc_write_to_exec;

        if (end_eip != 0 && eip == end_eip) {
            state->status = EmuStatus::Stopped;
            break;
        }
        if (!smc_single_step && max_insts != 0) {
            if (total_rep_iterations >= rep_slice_limit) {
                state->status = EmuStatus::Yield;
                break;
            }
            if (total_run_insts >= max_insts) {
                state->status = EmuStatus::Stopped;
                break;
            }
        }
        if (state->mmu.code_cache_flush_pending() && !smc_single_step) {
            state->status = EmuStatus::CachePressure;
            break;
        }
        // REP exits the block after charging its iterations. Keep this out
        // of the dispatch ABI and cap long strings independently of the
        // (much larger) ordinary interpreter scheduling quantum.
        const auto previous_rep_iterations = state->rep_iterations;
        state->rep_iterations = 0;
        state->rep_allowance = max_insts == 0
                                   ? rep_slice_limit
                                   : static_cast<uint32_t>(std::min<uint64_t>(
                                         rep_slice_limit - std::min(rep_slice_limit - 1, total_rep_iterations),
                                         std::max<uint64_t>(1, max_insts - std::min(max_insts, total_run_insts))));
        const auto charge_rep = [&] {
            if (max_insts != 0) total_rep_iterations += state->rep_iterations;
            return state->rep_iterations == 0 ? 0u : state->rep_iterations - 1;
        };

        auto execute_block = [&](BasicBlock* block_ptr) {
#ifdef FIBERCPU_ENABLE_SEQUENCE_PROFILE
            state->sequence_expected_op = nullptr;
            state->sequence_history_length = 0;
#endif
#if defined(FIBERCPU_ENABLE_BLOCK_PROFILE) || defined(FIBERCPU_ENABLE_HANDLER_PROFILE)
            block_ptr->ExecCountStorage()++;
#endif

            if (state->last_block->inst_count() > 0) {
                state->last_block->SetFallthroughLink(block_ptr);
            }
            state->last_block = block_ptr;
            if (block_ptr->inst_count() == 0) return;

#if FIBERCPU_BLOCK_COMPILATION
            CountLLVMBlockEntry(state, block_ptr, block_ptr->chain);
            compile_hot_block();
#endif
            DecodedOp* head = block_ptr->FirstOp();
#ifdef FIBERCPU_ENABLE_HANDLER_PROFILE
            state->current_block_head = head;
#endif

            if (block_ptr->entry) {
                state->clear_pending_mem_op();
                state->intercept_exec_write_for_smc = true;
                int64_t batch_limit = state->mmu.code_cache_flush_pending() ? block_ptr->inst_count() : 100000;
                if (max_insts != 0) {
                    uint64_t remaining_budget = max_insts - total_run_insts;
                    if (remaining_budget < (uint64_t)batch_limit) {
                        batch_limit = (int64_t)remaining_budget;
                    }
                }
                int64_t initial_batch_limit = batch_limit;
                batch_limit -= block_ptr->inst_count();
                if (state->eip_dirty) state->eip_dirty = false;

                uint64_t utlb_tags = mem::InvalidMicroTlbAbiTags();
                uint64_t utlb_addend = 0;
                uint64_t flags_cache = GetStateFlagsCache(state);
                int64_t remaining =
                    block_ptr->entry(state, head, batch_limit, utlb_tags, utlb_addend,
                                     std::numeric_limits<uint32_t>::max(), flags_cache FIBERCPU_PIN_INIT);
                state->intercept_exec_write_for_smc = false;
                total_run_insts += initial_batch_limit - remaining + charge_rep();
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
            if (total_run_insts > 0 && previous_rep_iterations == 0) {
                total_run_insts--;
            }
            state->smc_write_to_exec = false;
            state->allow_write_exec_page = true;
            state->last_block = state->mmu.invalid_code_block();
            state->intercept_exec_write_for_smc = true;
            const bool executed = ExecuteSingleInstructionOnStack(state);
            state->intercept_exec_write_for_smc = false;
            state->allow_write_exec_page = false;
            state->last_block = state->mmu.invalid_code_block();
            if (executed) total_run_insts += 1 + charge_rep();
            if (state->status != EmuStatus::Running) break;
            if (state->mmu.code_cache_flush_pending()) {
                state->status = EmuStatus::CachePressure;
                break;
            }
            continue;
        }

        BasicBlock* block_ptr = state->mmu.lookup_cached_block(eip);
        bool use_single_step = false;
#ifdef ENABLE_TLB_STATS
        auto& dispatch_stats = state->mmu.dispatch_stats;
        ++(block_ptr && block_ptr->is_valid() ? dispatch_stats.block_lookup_hits : dispatch_stats.block_lookup_misses);
#endif
        if (!block_ptr || !block_ptr->is_valid()) {
            block_ptr = ResolveBlockForRunSlow(state, eip, end_eip, block_ptr, &use_single_step);
        }
        if (!block_ptr) {
            if (use_single_step && state->status == EmuStatus::Running) {
                state->last_block = state->mmu.invalid_code_block();
                state->allow_write_exec_page = true;
                state->intercept_exec_write_for_smc = true;
                const bool executed = ExecuteSingleInstructionOnStack(state);
                state->intercept_exec_write_for_smc = false;
                state->allow_write_exec_page = false;
                if (executed) total_run_insts += 1 + charge_rep();
                if (state->status == EmuStatus::Running) {
                    state->status =
                        end_eip != 0 && state->ctx.eip == end_eip ? EmuStatus::Stopped : EmuStatus::CachePressure;
                }
            }
            break;
        }

        execute_block(block_ptr);
#if FIBERCPU_BLOCK_COMPILATION
        compile_hot_block();
#endif
        if (state->status != EmuStatus::Running) break;
        if (state->mmu.code_cache_flush_pending() && (end_eip == 0 || state->ctx.eip != end_eip) &&
            (max_insts == 0 || total_run_insts < max_insts)) {
            state->status = EmuStatus::CachePressure;
            break;
        }
    }

    // Sync FPU state back
#ifdef FIBERCPU_ENABLE_HANDLER_PROFILE
    state->current_block_head = nullptr;
#endif
    return 1;
}

#else
int X86_Run(EmuState*, uint32_t, uint64_t) { return 0; }
#endif

#if FIBERCPU_GUEST_AARCH64
static int RunArm(EmuState* state, uint64_t end_pc, uint64_t budget, bool step, bool block_budget) {
    if (!state || g_native_execution_active || state->native_execution_active) return step ? -1 : 0;
    if (!state->mmu.has_arm_address_space()) [[unlikely]]
        state->mmu.enable_arm_address_space();
#if FIBERCPU_LLVM_JIT
    ScopedLLVMJitExecution jit_execution(*state);
#endif
    state->mmu.flush_pending_code_cache_before_run();
    ScopedNativeExecution execution(state);
    if (!execution) return step ? -1 : 0;
    return RunArmEngine(*state, end_pc, budget, step, block_budget);
}

int AArch64_Run(EmuState* state, uint64_t end_pc, uint64_t budget, int step) {
    return RunArm(state, end_pc, budget, step != 0, false);
}

int AArch64_RunSlice(EmuState* state, uint64_t budget) { return RunArm(state, 0, budget, false, true); }
#endif

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

    const BasicBlock* invalid_block = state->mmu.invalid_code_block();
    for (const auto& entry : state->mmu.code_cache().block_entries) {
        const BasicBlock* block = entry.block;
        if (!block || block == invalid_block || !block->is_valid() || block->inst_count() == 0) continue;
        const auto exec_count = block->ExecCount();
        stats->executed_block_entries += exec_count;
        stats->executed_inst_total += exec_count * block->inst_count();
        stats->exec_weighted_histogram[std::min<uint32_t>(block->inst_count(), 64)] += exec_count;
        try_insert_top(block->start_eip(), block->inst_count(), exec_count);
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

size_t X86_DebugHostPageSetUniqueCount(const void* const* host_pages, size_t count) {
    mem::HostPageSet unique_pages;
    if (!host_pages || count == 0) return 0;

    for (size_t i = 0; i < count; ++i) {
        unique_pages.push_unique(reinterpret_cast<uintptr_t>(host_pages[i]));
    }
    return unique_pages.size();
}

uint32_t X86_GetBuildFeatures(void) {
    uint32_t features = FIBERCPU_ENABLE_SUPEROPCODES ? 1u : 0u;
#ifdef FIBERCPU_ENABLE_SEQUENCE_PROFILE
    features |= 2u;
#endif
#ifdef FIBERCPU_ENABLE_BLOCK_PROFILE
    features |= 4u;
#endif
#ifdef FIBERCPU_ENABLE_HANDLER_PROFILE
    features |= 8u;
#endif
    features |=
        (FIBERCPU_OPERAND_MODE << 4) | ((FIBERCPU_GPR_PIN_MODE & 3) << 8) | ((FIBERCPU_GPR_PIN_MODE >> 2) << 11);
#if FIBERCPU_LOWER_OPERANDS
    features |= 1u << 10;
#endif
#if FIBERCPU_STATIC_SPECIALIZATION
    features |= 1u << 12;
#endif
#if FIBERCPU_DYNAMIC_OPERANDS
    features |= 1u << 13;
#endif
#if FIBERCPU_LLVM_JIT
    features |= 1u << 14;
#endif
    return features;
}

const char* X86_GetOptimizationConfig(void) { return FIBERCPU_OPTIMIZATION_CONFIG; }

const char* X86_GetSuperOpcodeSetHash(void) { return FIBERCPU_SUPEROPCODE_SET_SHA256; }

size_t X86_GetSequenceProfile(EmuState* state, X86_SequenceProfileEntry* buffer, size_t capacity) {
#ifdef FIBERCPU_ENABLE_SEQUENCE_PROFILE
    if (!state) return 0;
    size_t i = 0;
    for (const auto& [pair, count] : state->sequence_counts) {
        if (!buffer || i == capacity) break;
        buffer[i++] = {reinterpret_cast<void*>(pair.first), reinterpret_cast<void*>(pair.second), count};
    }
    return state->sequence_counts.size();
#else
    (void)state;
    (void)buffer;
    (void)capacity;
    return 0;
#endif
}

void X86_ResetSequenceProfile(EmuState* state) {
#ifdef FIBERCPU_ENABLE_SEQUENCE_PROFILE
    if (state) {
        state->sequence_counts.clear();
        state->ngram_counts.clear();
        state->sequence_expected_op = nullptr;
        state->sequence_history_length = 0;
    }
#else
    (void)state;
#endif
}

size_t X86_GetNGramProfile(EmuState* state, X86_NGramProfileEntry* buffer, size_t capacity) {
#ifdef FIBERCPU_ENABLE_SEQUENCE_PROFILE
    if (!state) return 0;
    size_t i = 0;
    for (const auto& [key, count] : state->ngram_counts) {
        if (!buffer || i == capacity) break;
        buffer[i++] = {reinterpret_cast<void*>(key.handlers[0]), reinterpret_cast<void*>(key.handlers[1]),
                       reinterpret_cast<void*>(key.handlers[2]), reinterpret_cast<void*>(key.handlers[3]), count};
    }
    return state->ngram_counts.size();
#else
    (void)state;
    (void)buffer;
    (void)capacity;
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

#if FIBERCPU_GUEST_I386
int X86_Step(EmuState* state) {
#if FIBERCPU_LLVM_JIT || FIBERCPU_GUEST_AARCH64
    if (!state || g_native_execution_active || state->native_execution_active) return -1;
#endif
#if FIBERCPU_GUEST_AARCH64
    if (state->mmu.has_arm_address_space()) [[unlikely]]
        state->mmu.enable_i386_address_space();
#endif
#if FIBERCPU_LLVM_JIT
    ScopedLLVMJitExecution jit_execution(*state);
    state->mmu.flush_pending_code_cache_before_run();
#endif
    ScopedNativeExecution execution(state);
    if (!execution) return -1;
#ifdef FIBERCPU_ENABLE_SEQUENCE_PROFILE
    state->sequence_expected_op = nullptr;
    state->sequence_history_length = 0;
#endif
    ScopedFloatEnvironment float_environment(state);
    state->status = EmuStatus::Running;
    state->fault_vector = 0xFF;
    state->fault_addr = 0;
    state->eip_dirty = false;
    state->clear_pending_mem_op();
    state->smc_write_to_exec = false;
    state->last_block = state->mmu.invalid_code_block();
    const bool prev_allow_write_exec_page = state->allow_write_exec_page;
    const bool prev_intercept_exec_write_for_smc = state->intercept_exec_write_for_smc;
    state->allow_write_exec_page = true;
    state->intercept_exec_write_for_smc = true;

    state->mmu.sync_runtime_tlb_shootdowns();
    [[maybe_unused]] auto runtime_tlb_batch = state->mmu.scoped_runtime_tlb_publish_batch();

    state->rep_allowance = std::numeric_limits<uint32_t>::max();
    state->rep_iterations = 0;
    (void)ExecuteSingleInstructionOnStack(state);
    state->allow_write_exec_page = prev_allow_write_exec_page;
    state->intercept_exec_write_for_smc = prev_intercept_exec_write_for_smc;

    return (int)state->status;
}

#else
int X86_Step(EmuState*) { return -1; }
#endif

int X86_GetStatus(EmuState* state) { return (int)state->status; }

// ----------------------------------------------------------------------------
// Callbacks
// ----------------------------------------------------------------------------

void X86_SetFaultCallback(EmuState* state, fiberish::FaultHandler handler, void* userdata) {
    state->fault_handler = handler;
    state->fault_userdata = userdata;
    state->extended_fault_callback = false;
}

void X86_SetExtendedFaultCallback(EmuState* state, fiberish::FaultHandler handler, void* userdata) {
    X86_SetFaultCallback(state, handler, userdata);
    state->extended_fault_callback = true;
}

void X86_SetMemHook(EmuState* state, MemHook hook, void* userdata) {
    state->mem_hook = hook;
    state->mem_userdata = userdata;
#if FIBERCPU_GUEST_AARCH64
    state->mmu.flush_arm_tlb_for_hook();
#endif

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

uint32_t X86_GetFaultAddress(EmuState* state) { return state ? state->fault_addr : 0; }

uint64_t X86_DebugGetFloatEnvironment(void) {
    uint64_t environment = f80_save_environment();
#if defined(FIBERCPU_HOST_SSE)
    environment |= static_cast<uint64_t>(_mm_getcsr()) << 32;
#endif
    return environment;
}

void X86_DebugSetFloatEnvironment(uint64_t environment) {
    f80_restore_environment(static_cast<uint32_t>(environment));
#if defined(FIBERCPU_HOST_SSE)
    _mm_setcsr(static_cast<uint32_t>(environment >> 32));
#endif
}

void X86_ResetAllCodeCache(EmuState* state) {
    if (state) state->mmu.reset_code_cache();
}

int X86_ResetMemory(EmuState* state) { return state && state->mmu.reset_memory() ? 1 : 0; }

int X86_IsExecuting(EmuState* state) { return state && state->native_execution_active ? 1 : 0; }

void X86_ResetCodeCacheByRange(EmuState* state, uint32_t addr, uint32_t size) {
    if (!state) return;
#if FIBERCPU_GUEST_AARCH64
    if (state->mmu.has_arm_address_space())
        return AArch64_ResetCodeCacheByRange(state, addr, CompatRangeSize(addr, size));
#endif
    state->mmu.invalidate_code_cache_range(addr, size);
}

void X86_InvalidateCodeCacheHostPages(EmuState* state, const void* const* host_pages, size_t count) {
    if (!state || !host_pages || count == 0) return;

    mem::HostPageSet page_bases;
    for (size_t i = 0; i < count; ++i) {
        if (!host_pages[i]) continue;
        page_bases.push_unique(reinterpret_cast<uintptr_t>(host_pages[i]));
    }

    if (page_bases.empty()) return;
    state->mmu.invalidate_code_cache_host_pages(page_bases);
}

int X86_ConfigureCodeCache(EmuState* state, uint64_t block_bytes, uint64_t arena_bytes) {
    constexpr auto max_size = std::numeric_limits<size_t>::max();
    if (!state || block_bytes > max_size || arena_bytes > max_size) return 0;

    auto* core = state->mmu.core_handle();
    if (!core || core->execution_active || core->code_cache.requested_block_bytes) return 0;
    if (arena_bytes) {
#if FIBERCPU_DYNAMIC_OPERANDS || FIBERCPU_LLVM_JIT
        if (!ExecutableArena::Shared(static_cast<size_t>(arena_bytes))) return 0;
#else
        return 0;
#endif
    }

    core->code_cache.SetBlockBudgetBytes(static_cast<size_t>(block_bytes));
    return 1;
}

void X86_SetCodeCacheBudgetBytes(EmuState* state, uint64_t bytes) {
    if (!state) return;

    const size_t requested_bytes = bytes > static_cast<uint64_t>(std::numeric_limits<size_t>::max())
                                       ? std::numeric_limits<size_t>::max()
                                       : static_cast<size_t>(bytes);
    state->mmu.set_code_cache_budget_bytes(requested_bytes);
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
    if (state) {
        state->mmu.stats.reset();
        state->mmu.dispatch_stats = {};
    }
#endif
}

int X86_DumpStats(EmuState* state, char* buffer, size_t buffer_size) {
    if (!state || !buffer || buffer_size == 0) return -1;
    const auto& code_cache = state->mmu.code_cache();
#if FIBERCPU_DYNAMIC_OPERANDS || FIBERCPU_LLVM_JIT
    const auto arena = ExecutableArena::SharedUsage();
#else
    constexpr struct {
        size_t capacity = 0;
        size_t total = 0;
        size_t llvm = 0;
    } arena;
#endif
#ifdef ENABLE_TLB_STATS
    auto& s = state->mmu.stats;
    const auto& dispatch_stats = state->mmu.dispatch_stats;
    int n = snprintf(
        buffer, buffer_size,
        "{\"l1_read_hits\":%llu,\"l1_write_hits\":%llu,"
        "\"l2_read_hits\":%llu,\"l2_write_hits\":%llu,"
        "\"read_misses\":%llu,\"write_misses\":%llu,"
        "\"total_reads\":%llu,\"total_writes\":%llu,"
        "\"direct_links\":%llu,\"resolved_links\":%llu,\"cross_page_links\":%llu,\"link_misses\":%llu,"
        "\"block_lookup_hits\":%llu,\"block_lookup_misses\":%llu,\"block_allocations\":%llu,"
        "\"block_allocated_bytes\":%llu,\"memory_replays\":%llu,"
        "\"all_blocks_count\":%zu,\"block_cache_size\":%zu,\"page_to_blocks_size\":%zu,"
        "\"code_cache_bytes_requested\":%llu,\"code_cache_limit_bytes\":%llu,"
        "\"code_cache_flush_count\":%llu,\"code_cache_generation\":%llu,\"llvm_code_cache_bytes\":%zu,"
        "\"jit_arena_used_bytes\":%zu,\"jit_arena_capacity_bytes\":%zu}",
        (unsigned long long)s.l1_read_hits, (unsigned long long)s.l1_write_hits, (unsigned long long)s.l2_read_hits,
        (unsigned long long)s.l2_write_hits, (unsigned long long)s.read_misses, (unsigned long long)s.write_misses,
        (unsigned long long)s.total_reads, (unsigned long long)s.total_writes,
        (unsigned long long)dispatch_stats.direct_links, (unsigned long long)dispatch_stats.resolved_links,
        (unsigned long long)dispatch_stats.cross_page_links, (unsigned long long)dispatch_stats.link_misses,
        (unsigned long long)dispatch_stats.block_lookup_hits, (unsigned long long)dispatch_stats.block_lookup_misses,
        (unsigned long long)dispatch_stats.block_allocations, (unsigned long long)dispatch_stats.block_allocated_bytes,
        (unsigned long long)dispatch_stats.memory_replays, code_cache.AllocatedBlockCount(),
        code_cache.CountValidBlocks(), code_cache.CachedPageCount(),
        (unsigned long long)code_cache.requested_block_bytes, (unsigned long long)code_cache.block_budget_bytes,
        (unsigned long long)code_cache.flush_count, (unsigned long long)code_cache.generation, arena.llvm, arena.total,
        arena.capacity);
    return n;
#else
    return snprintf(buffer, buffer_size,
                    "{\"all_blocks_count\":%zu,\"block_cache_size\":%zu,\"page_to_blocks_size\":%zu,"
                    "\"code_cache_bytes_requested\":%llu,\"code_cache_limit_bytes\":%llu,"
                    "\"code_cache_flush_count\":%llu,\"code_cache_generation\":%llu,\"llvm_code_cache_bytes\":%zu,"
                    "\"jit_arena_used_bytes\":%zu,\"jit_arena_capacity_bytes\":%zu}",
                    code_cache.AllocatedBlockCount(), code_cache.CountValidBlocks(), code_cache.CachedPageCount(),
                    (unsigned long long)code_cache.requested_block_bytes,
                    (unsigned long long)code_cache.block_budget_bytes, (unsigned long long)code_cache.flush_count,
                    (unsigned long long)code_cache.generation, arena.llvm, arena.total, arena.capacity);
#endif
}

size_t X86_GetBlockCount(EmuState* state) {
    if (!state || !state->mmu.code_cache_epoch_current()) return 0;
    return state->mmu.code_cache().all_blocks.size();
}

size_t X86_GetBlockList(EmuState* state, BasicBlock** buffer, size_t max_count) {
    if (!state || !buffer || max_count == 0 || !state->mmu.code_cache_epoch_current()) return 0;

    size_t count = 0;
    for (BasicBlock* block : state->mmu.code_cache().all_blocks) {
        if (count >= max_count) break;
        buffer[count] = block;
        count++;
    }
    return count;
}

int X86_ReadBlockDump(const BasicBlock* block, X86_BlockDumpHeader* header, X86_BlockDumpOp* ops, uint32_t capacity) {
#if FIBERCPU_GUEST_I386
    static_assert(sizeof(X86_BlockDumpHeader) == 24);
    static_assert(sizeof(X86_BlockDumpOp) == 32);
    if (!block || !header) return 0;
    const auto count = block->inst_count();
    if (count > capacity || (count && !ops)) return 0;
    *header = {block->ExecCount(), block->start_eip(), block->end_eip, count, 0};
    for (uint32_t i = 0; i < count; ++i) {
        const auto op = block->ReadOp(i);
        const auto memory = HasMem(&op) ? (uint64_t{op.ext.data.ea_desc} << 32) | op.ext.data.disp : 0;
        ops[i] = {reinterpret_cast<uintptr_t>(op.handler),
                  memory,
                  op.next_eip,
                  GetImm(&op),
                  op.len,
                  op.modrm,
                  op.prefixes.all,
                  op.meta.all,
                  0};
    }
    return 1;
#else
    return 0;
#endif
}

#if FIBERCPU_GUEST_I386
int32_t X86_GetOpIdForHandler(void* handler) {
    auto fn = reinterpret_cast<HandlerFunc>(handler);
#if FIBERCPU_DYNAMIC_OPERANDS
    fn = CanonicalDynamicOperandHandler(fn);
#endif
    return static_cast<int32_t>(FindOpcodeIndexForHandler(CanonicalBoundHandler(fn)));
}

uint32_t X86_GetTrainingHandlerId(void* handler) {
    auto fn = reinterpret_cast<HandlerFunc>(handler);
    if (!fn) return 0;
#if FIBERCPU_DYNAMIC_OPERANDS
    fn = CanonicalDynamicOperandHandler(fn);
#endif
#if FIBERCPU_LOWER_OPERANDS
    fn = CanonicalOperandHandler(fn);
#endif
    if (const auto id = FindScalarTrainingId(fn)) return id;
    return FindFusionTrainingId(fn);
}

uint32_t X86_GetTrainingOperationIdAt(uint32_t index) { return TrainingOperationIdAt(index); }

const char* X86_GetTrainingOperationName(uint32_t id) { return FindTrainingOperationName(id); }

#else
int32_t X86_GetOpIdForHandler(void*) { return -1; }
uint32_t X86_GetTrainingHandlerId(void*) { return 0; }
uint32_t X86_GetTrainingOperationIdAt(uint32_t) { return 0; }
const char* X86_GetTrainingOperationName(uint32_t) { return nullptr; }
int32_t X86_GetHandlerCount() { return 0; }
int32_t X86_GetHandlerId(void*) { return -1; }
void* X86_GetHandlerById(int32_t) { return nullptr; }
const char* X86_GetHandlerSymbolById(int32_t) { return nullptr; }
const char* X86_GetInstructionCatalogHash() { return nullptr; }
int32_t X86_GetInstructionVariantCount() { return 0; }
int32_t X86_GetInstructionVariant(int32_t, X86_InstructionVariant*) { return 0; }
#endif

uint64_t X86_GetLLVMJitBlockCount() {
#if FIBERCPU_LLVM_JIT
    return LLVMJitBlockCount();
#else
    return 0;
#endif
}

uint64_t X86_GetLLVMJitLiveBlockCount() {
#if FIBERCPU_LLVM_JIT
    return LLVMJitLiveBlockCount();
#else
    return 0;
#endif
}

uint64_t X86_GetLinkedBlockHitCount() {
#if FIBERCPU_LINKED_BLOCK_CACHE
    return LinkedBlockHitCount();
#else
    return 0;
#endif
}

uint64_t X86_GetLinkedPageRegionHitCount() {
#if FIBERCPU_LINKED_BLOCK_CACHE
    return LinkedPageRegionHitCount();
#else
    return 0;
#endif
}

int X86_FinishRun(const X86_RunContext* run, char* error, size_t error_size) {
    if (error && error_size) error[0] = '\0';
    if (!run) return 0;
#if FIBERCPU_GUEST_AARCH64
    if (const auto& profile = run->options->arm_profile) {
        if (g_native_execution_active) return 0;
        std::lock_guard guard(profile->mutex);
        std::ofstream output(profile->path);
        output << R"({"version":2,"architecture":"aarch64","entries":[)";
        bool first = true;
        for (const auto& [word, count] : profile->words) {
            if (!first) output << ',';
            first = false;
            output << R"({"word":)" << word << R"(,"count":)" << count << '}';
        }
        output << R"(],"pairs":[)";
        first = true;
        for (const auto& [key, count] : profile->pairs) {
            if (!first) output << ',';
            first = false;
            output << R"({"first":)" << (key >> 32) << R"(,"second":)" << static_cast<uint32_t>(key) << R"(,"count":)"
                   << count << '}';
        }
        auto write_sequences = [&](const char* name, const auto& histogram) {
            output << "],\"" << name << "\":[";
            bool separator = false;
            for (const auto& [words, count] : histogram) {
                if (separator) output << ',';
                separator = true;
                output << R"({"words":[)";
                for (size_t i = 0; i < words.size(); ++i) {
                    if (i) output << ',';
                    output << words[i];
                }
                output << R"(],"count":)" << count << '}';
            }
        };
        write_sequences("sequences", profile->sequences);
        write_sequences("blocks", profile->blocks);
        output << "]}\n";
        output.close();
        if (!output) {
            if (error && error_size) std::snprintf(error, error_size, "Cannot write %s", profile->path.c_str());
            return 0;
        }
    }
#endif
#if FIBERCPU_LLVM_JIT && FIBERCPU_GUEST_I386
    if (run->options->profile) {
        std::string diagnostic;
        if (g_native_execution_active) {
            diagnostic = "finish the run outside its CPU execution callbacks";
        } else if (WriteWeightedLLVMProfile(*run->options->profile, run->profile_path.c_str(), diagnostic)) {
            return 1;
        }
        if (error && error_size)
            std::snprintf(error, error_size, "WeightedProfilePath '%s': %s", run->profile_path.c_str(),
                          diagnostic.c_str());
        return 0;
    }
#endif
    return 1;
}

int X86_BuildLinkedLLVMBlockCache(const char* profile_path, const char* cache_path) {
    return X86_BuildLinkedLLVMBlockCacheWithOptions(profile_path, cache_path, 0, nullptr, nullptr);
}

int X86_BuildLinkedLLVMBlockCacheWithOptions(const char* profile_path, const char* cache_path, uint32_t limit,
                                             const char* opt_level, const char* pass_pipeline) {
#if FIBERCPU_LLVM_JIT && FIBERCPU_GUEST_I386
    return BuildLinkedLLVMBlockCache(profile_path, cache_path, limit, opt_level, pass_pipeline);
#else
    (void)profile_path;
    (void)cache_path;
    (void)limit;
    (void)opt_level;
    (void)pass_pipeline;
    return -1;
#endif
}

void* X86_GetLibAddress() {
#if defined(__EMSCRIPTEN__)
    return nullptr;
#elif defined(_WIN32)
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
