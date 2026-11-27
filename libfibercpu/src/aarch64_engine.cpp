#include "aarch64_engine.h"
#include <algorithm>
#include <cstring>
#include <limits>
#include "aarch64/aarch64.h"
#include "aarch64/baked.h"
#include "bindings.h"
#include "mem/mmu_impl.h"
#include "runtime_options.h"
#include "state.h"
#if FIBERCPU_LLVM_JIT
#include "jit/llvm_arm_jit.h"
#include "jit/llvm_block_jit.h"
#endif

namespace fiberish {
namespace {
using aarch64::Access;
using aarch64::Vector128;

template <class T, bool Write>
bool Transfer(EmuState& engine, uint64_t address, void* bytes) {
    if constexpr (Write) {
        T value;
        std::memcpy(&value, bytes, sizeof value);
        return engine.mmu.write_arm_no_utlb<T>(address, value).has_value();
    } else {
        auto value = engine.mmu.read_arm_no_utlb<T>(address);
        if (!value) return false;
        std::memcpy(bytes, &*value, sizeof(T));
        return true;
    }
}

template <bool Write>
bool TransferBytes(EmuState& engine, uint64_t address, void* bytes, size_t size) {
    if (!mem::ArmAddressRangeValid(address, size)) return false;
    switch (size) {
        case 1:
            return Transfer<uint8_t, Write>(engine, address, bytes);
        case 2:
            return Transfer<uint16_t, Write>(engine, address, bytes);
        case 4:
            return Transfer<uint32_t, Write>(engine, address, bytes);
        case 8:
            return Transfer<uint64_t, Write>(engine, address, bytes);
        case 16:
            return Transfer<Vector128, Write>(engine, address, bytes);
        default: {
            if (size == 0 || size > 64) return false;
            // Validate every destination before a multi-register transfer can modify guest memory.
            const auto last = address + size - 1;
            if (mem::ArmPageTag(address) != mem::ArmPageTag(last)) {
                constexpr auto perm = Write ? mem::Property::Write : mem::Property::Read;
                if (!engine.mmu.resolve_arm_ptr(address, perm) || !engine.mmu.resolve_arm_ptr(last, perm)) return false;
            }
            auto* data = static_cast<uint8_t*>(bytes);
            uint32_t offset = 0;
            for (; offset + 16 <= size; offset += 16)
                if (!Transfer<Vector128, Write>(engine, address + offset, data + offset)) return false;
            for (; offset < size; ++offset)
                if (!Transfer<uint8_t, Write>(engine, address + offset, data + offset)) return false;
            return true;
        }
    }
}

bool Read(void* opaque, uint64_t address, void* dest, size_t size, Access access) {
    auto& engine = *static_cast<EmuState*>(opaque);
    if (access != Access::Execute) return TransferBytes<false>(engine, address, dest, size);
    if (!mem::ArmAddressRangeValid(address, size) || size != 4 || (address & 3)) return false;
    auto source = engine.mmu.translate_arm_exec(&engine, address);
    if (!source) return false;
    std::memcpy(dest, source, 4);
    return true;
}

inline void CopyFast(void* dest, const void* source, size_t size) {
    switch (size) {
        case 1:
            std::memcpy(dest, source, 1);
            break;
        case 2:
            std::memcpy(dest, source, 2);
            break;
        case 4:
            std::memcpy(dest, source, 4);
            break;
        case 8:
            std::memcpy(dest, source, 8);
            break;
        case 16:
            std::memcpy(dest, source, 16);
            break;
        case 32:
            std::memcpy(dest, source, 32);
            break;
        default:
            std::memcpy(dest, source, size);
            break;
    }
}

bool IsFastSpan(uint64_t address, size_t size) {
    return address < mem::ArmTaskSize && size && size <= 64 &&
           size <= aarch64::detail::PageBytes - (address & aarch64::detail::PageMask);
}

bool ReadFast(void* opaque, uint64_t address, void* dest, size_t size) {
    if (!IsFastSpan(address, size)) return false;
    auto& engine = *static_cast<EmuState*>(opaque);
    const auto* source = engine.mmu.try_arm_cached_data_page<false>(address);
    if (!source) return false;
    CopyFast(dest, source, size);
    return true;
}

bool WriteFast(void* opaque, uint64_t address, const void* source, size_t size) {
    if (!IsFastSpan(address, size)) return false;
    auto& engine = *static_cast<EmuState*>(opaque);
    auto* dest = engine.mmu.try_arm_cached_data_page<true>(address);
    if (!dest) return false;
    CopyFast(dest, source, size);
    return true;
}

const uint8_t* ExecutablePage(void* opaque, uint64_t address) {
    auto& engine = *static_cast<EmuState*>(opaque);
    if (engine.mem_hook || engine.mmu.has_arm_mem_hook() || address >= mem::ArmTaskSize ||
        (address & aarch64::detail::PageMask))
        return nullptr;
    return reinterpret_cast<const uint8_t*>(engine.mmu.try_arm_executable_page(address));
}

aarch64::detail::BasicBlock* LookupCachedBlock(void* opaque, uint64_t pc, const uint8_t* executable) {
    if (!executable) executable = ExecutablePage(opaque, pc & ~aarch64::detail::PageMask);
    if (!executable) return nullptr;
    return static_cast<EmuState*>(opaque)->mmu.lookup_arm_block(pc, executable);
}

void* AllocateBlock(void* opaque, size_t size, uint64_t pc) {
    return static_cast<EmuState*>(opaque)->mmu.try_allocate_arm_block_bytes(size, pc);
}

void CacheBlock(void* opaque, aarch64::detail::BasicBlock* block) {
    auto& engine = *static_cast<EmuState*>(opaque);
    engine.mmu.cache_arm_block(block);
#if FIBERCPU_LLVM_JIT
    InitializeLLVMArmBlock(engine, *block);
#endif
}

bool Write(void* opaque, uint64_t address, const void* source, size_t size) {
    return TransferBytes<true>(*static_cast<EmuState*>(opaque), address, const_cast<void*>(source), size);
}

uint64_t* ArmSlot(aarch64::State& cpu, int index) {
    if (index >= 0 && index < 31) return &cpu.x[index];
    switch (index) {
        case 31:
            return &cpu.sp;
        case 32:
            return &cpu.pc;
        case 33:
            return &cpu.tpidr_el0;
    }
    if (index >= 64 && index < 128) {
        auto& vec = cpu.v[(index - 64) / 2];
        return (index & 1) ? &vec.hi : &vec.lo;
    }
    return nullptr;
}

// An engine entry may end halfway through a block (budget, fault or debugger).
// Commit its retired prefix before control returns to the caller.
class ArmCapture {
    std::shared_ptr<ArmInstructionProfile> counts;
    std::vector<uint32_t> block;
    uint64_t next_pc = 0;

public:
    explicit ArmCapture(const std::shared_ptr<ArmInstructionProfile>& profile) : counts(profile) {
        block.reserve(aarch64::detail::BlockMax);
    }
    void FlushBlock() {
        if (block.empty()) return;
        std::lock_guard guard(counts->mutex);
        ++counts->blocks[block];
        auto start = block.begin();
        bool pair_start = false;
        for (auto it = start; it != block.end(); ++it) {
            const uint32_t word = *it;
            const bool baked = aarch64::CanBake(word);
            if (baked) {
                ++counts->words[word];
                if (pair_start) ++counts->pairs[(uint64_t{it[-1]} << 32) | word];
            }
            const auto kind = aarch64::detail::DecodeFields(word).kind;
            pair_start = kind >= aarch64::Kind::Address && kind <= aarch64::Kind::Multiply;
            if (!aarch64::CanFollowInBakedSequence(word) || it + 1 == block.end()) {
                const auto end = baked ? it + 1 : it;
                if (end - start >= 3) ++counts->sequences[std::vector<uint32_t>(start, end)];
                start = it + 1;
            }
        }
        block.clear();
    }

    static void Record(void* context, uint64_t pc, uint32_t word, bool can_follow) {
        auto& capture = *static_cast<ArmCapture*>(context);
        if (pc != capture.next_pc) capture.FlushBlock();
        capture.block.push_back(word);
        capture.next_pc = pc + 4;
        if (!can_follow || capture.block.size() == aarch64::detail::BlockMax ||
            (capture.next_pc & aarch64::detail::PageMask) == 0)
            capture.FlushBlock();
    }
};
}  // namespace

#if FIBERCPU_AARCH64_BAKED
aarch64::detail::HandlerBinding FindBakedArmHandler(const aarch64::DecodedInstruction&, bool omit_nzcv);
aarch64::ScalarHandlerFunc FindBakedArmPair(uint32_t first, uint32_t second, uint32_t dead,
                                            aarch64::detail::HandlerFunc&);
aarch64::ScalarHandlerFunc FindBakedArmSequence(const uint32_t* words, unsigned count, unsigned* matched, uint32_t dead,
                                                aarch64::detail::HandlerFunc&);
#endif

int RunArmEngine(EmuState& engine, uint64_t end_pc, uint64_t budget, bool step, bool block_budget) {
    if (!engine.arm) AArch64_Reset(&engine);
    auto& cpu = *engine.arm;
    engine.status = EmuStatus::Running;
    engine.fault_vector = 0xff;
    engine.arm_fault_address = 0;
    RestartableMemoryFaultScope restartable(&engine);
    if (!step) cpu.ClearExclusive();
    aarch64::Memory memory{
        &engine,           Read,          Write,     ExecutablePage, ReadFast, WriteFast, engine.mmu.arm_data_tlb(),
        LookupCachedBlock, AllocateBlock, CacheBlock};
#ifdef ENABLE_TLB_STATS
    memory.stats = &engine.mmu.stats;
    memory.dispatch_stats = &engine.mmu.dispatch_stats;
#endif
    aarch64::Profile profile{};
#if FIBERCPU_AARCH64_BAKED
    profile.resolve = FindBakedArmHandler;
    profile.resolve_pair = FindBakedArmPair;
    profile.resolve_sequence = FindBakedArmSequence;
    profile.trusted_baking = true;
#endif
    const auto& options = engine.mmu.code_cache().run_options;
    std::optional<ArmCapture> capture;
    if (options && options->arm_profile) {
        profile.context = &capture.emplace(options->arm_profile);
        profile.record = ArmCapture::Record;
    }
#if FIBERCPU_LLVM_JIT
    memory.retain_region_context = LLVMPageRegionsEnabled(engine);
    memory.observe_hot_block = [](void* opaque, aarch64::detail::BasicBlock& block) {
        ObserveLLVMArmHotBlock(*static_cast<EmuState*>(opaque), block);
    };
#endif
    uint64_t remaining = budget ? budget : UINT64_MAX;
    while (engine.status == EmuStatus::Running && remaining && (!end_pc || cpu.pc != end_pc)) {
        if (engine.mmu.code_cache_flush_pending()) {
            engine.status = EmuStatus::CachePressure;
            break;
        }
#if FIBERCPU_LLVM_JIT
        PollLLVMArmCompilations(engine);
#endif
        const auto mode = block_budget ? aarch64::BudgetMode::Block : aarch64::BudgetMode::Exact;
        auto result = aarch64::Run(cpu, memory, end_pc ? 1 : remaining, profile, mode);
        remaining -= std::min(remaining, result.executed);
        const bool cache_pressure = result.reason == aarch64::Stop::CachePressure;
        if (cache_pressure && remaining) {
            // As with x86's stack decoder, retire one instruction even when a single
            // block is larger than the configured budget.
            auto uncached = memory;
            uncached.executable_page = nullptr;
            result = aarch64::Run(cpu, uncached, 1, profile);
            remaining -= result.executed;
        }
        if (engine.status != EmuStatus::Running) break;
        switch (result.reason) {
            case aarch64::Stop::Syscall:
                cpu.ClearExclusive();
                if (!engine.hooks.on_interrupt(&engine, 0x80)) engine.status = EmuStatus::Fault;
                break;
            case aarch64::Stop::Budget:
                break;
            default:
                engine.arm_fault_address = result.address;
                engine.fault_addr = static_cast<uint32_t>(result.address);
                engine.fault_vector = result.reason == aarch64::Stop::Fault        ? 14
                                      : result.reason == aarch64::Stop::Alignment  ? 11
                                      : result.reason == aarch64::Stop::Breakpoint ? 3
                                                                                   : 6;
                engine.status = EmuStatus::Fault;
                break;
        }
        if (cache_pressure && engine.status == EmuStatus::Running && (!end_pc || cpu.pc != end_pc))
            engine.status = EmuStatus::CachePressure;
    }
    if (capture) capture->FlushBlock();
    if (!step && engine.status == EmuStatus::Running) engine.status = EmuStatus::Stopped;
    return step ? static_cast<int>(engine.status) : 1;
}
}  // namespace fiberish

void AArch64_Reset(EmuState* state) {
    state->mmu.enable_arm_address_space();
    if (!state->arm)
        state->arm = std::make_shared<fiberish::aarch64::State>();
    else {
        auto* frame = state->arm->active_run;
        *state->arm = {};
        state->arm->active_run = frame;
    }
}

uint32_t AArch64_ReadSyscall(EmuState* state, uint64_t* arguments) {
    if (!state->arm) {
        std::fill_n(arguments, 6, uint64_t{0});
        return 0;
    }
    std::copy_n(state->arm->x.begin(), 6, arguments);
    return static_cast<uint32_t>(state->arm->x[8]);
}

uint64_t AArch64_RegRead(EmuState* state, int index) {
    if (index == 37) return state->arm_fault_address;
    if (!state->arm) return 0;
    auto& cpu = *state->arm;
    if (auto slot = fiberish::ArmSlot(cpu, index)) return *slot;
    switch (index) {
        case 34:
            return cpu.nzcv;
        case 35:
            return cpu.fpcr;
        case 36:
            return cpu.fpsr;
        default:
            return 0;
    }
}

void AArch64_RegWrite(EmuState* state, int index, uint64_t value) {
    if (!state->arm) AArch64_Reset(state);
    auto& cpu = *state->arm;
    if (auto slot = fiberish::ArmSlot(cpu, index))
        *slot = value;
    else if (index == 34)
        cpu.nzcv = value & 0xf0000000;
    else if (index == 35)
        cpu.fpcr = static_cast<uint32_t>(value);
    else if (index == 36)
        cpu.fpsr = static_cast<uint32_t>(value);
}
