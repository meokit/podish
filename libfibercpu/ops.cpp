#include "ops.h"
#include <algorithm>
#include <unordered_map>
#include <vector>
#include "dispatch.h"

namespace fiberish {

template <bool restart>
ATTR_PRESERVE_NONE int64_t MemoryOpGeneric(EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit,
                                           mem::MicroTLB utlb) {
    bool fault = false;

    auto execute_mem_op = [&]<typename T>(uint32_t addr, T* value, bool is_write) {
        if (!is_write) {
            auto res = state->mmu.read<T>(addr, &utlb, op);
            if (!res) {
                fault = true;
            } else {
                *value = *res;
            }
        } else {
            if (!state->mmu.write<T>(addr, *value, &utlb, op)) fault = true;
        }
    };

    auto perform = [&](uint32_t addr, uint32_t size, std::byte* value, bool is_write) {
        struct TSize10 {
            std::byte data[10];
        };
        struct TSize16 {
            std::byte data[16];
        };

        switch (size) {
            case 1:
                execute_mem_op(addr, reinterpret_cast<uint8_t*>(value), is_write);
                break;
            case 2:
                execute_mem_op(addr, reinterpret_cast<uint16_t*>(value), is_write);
                break;
            case 4:
                execute_mem_op(addr, reinterpret_cast<uint32_t*>(value), is_write);
                break;
            case 8:
                execute_mem_op(addr, reinterpret_cast<uint64_t*>(value), is_write);
                break;
            case 10:
                execute_mem_op(addr, reinterpret_cast<TSize10*>(value), is_write);
                break;  // 80-bit extended precision
            case 16:
                execute_mem_op(addr, reinterpret_cast<TSize16*>(value), is_write);
                break;
            default:
                __builtin_unreachable();
        }
    };

    std::visit(
        [&](auto&& arg) {
            using T = std::decay_t<decltype(arg)>;
            if constexpr (std::is_same_v<T, MemNoOp>) {
                // No-op
            } else if constexpr (std::is_same_v<T, MemReadOperation>) {
                perform(arg.addr, arg.size, arg.data.data(), false);
                if (!fault) {
                    arg.done = true;
                }
            } else if constexpr (std::is_same_v<T, MemWriteOperation>) {
                perform(arg.addr, arg.size, arg.data.data(), true);
                if (!fault) {
                    arg.done = true;
                }
            }
        },
        state->mem_op);

    // Handle fault or restart
    if (fault) {
        // Break calling chain
        // Note: eip will be synced on read or write
        state->mem_op = MemNoOp{};
        return instr_limit;
    }

    if constexpr (restart) {
        // Don't clear mem_op, it's pending for restart
        ATTR_MUSTTAIL return op->handler(state, op, instr_limit, utlb);
    }

    state->mem_op = MemNoOp{};
    ATTR_MUSTTAIL return (op + 1)->handler(state, op + 1, instr_limit, utlb);
}

ATTR_PRESERVE_NONE int64_t MemoryOpRestart(EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit,
                                           mem::MicroTLB utlb) {
    ATTR_MUSTTAIL return MemoryOpGeneric<true>(state, op, instr_limit, utlb);
}

ATTR_PRESERVE_NONE int64_t MemoryOpRetry(EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit,
                                         mem::MicroTLB utlb) {
    ATTR_MUSTTAIL return MemoryOpGeneric<false>(state, op, instr_limit, utlb);
}

// Sentinel Handler
template <int I>
ATTR_PRESERVE_NONE int64_t OpExitBlock(EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit,
                                       mem::MicroTLB utlb) {
    auto* last_op = op - 1;

    if (last_op->branch_target != std::numeric_limits<uint32_t>::max()) {
        state->ctx.eip = last_op->branch_target;
        last_op->branch_target = std::numeric_limits<uint32_t>::max();
    } else {
        state->ctx.eip = last_op->next_eip;
    }

    // Basic Block Chaining
    // Optim: If next_block is dummy, is_valid is false, so we skip.
    // If next_block is real but invalidated, is_valid is false, so we skip.
    if (op->next_block->is_valid && op->next_block->start_eip == state->ctx.eip) {
        // Check instruction limit before chaining
        if (instr_limit > 0) {
            // Subtract the NEXT block's size from the limit
            instr_limit -= op->next_block->inst_count;

            state->last_block = op->next_block;
            // ops is now flexible array member, essentially ops[0]
            DecodedOp* next_head = &op->next_block->ops[0];

            // Direct Relative Dispatch
            auto handler = next_head->handler;
            if (handler != nullptr) {
                ATTR_MUSTTAIL return handler(state, next_head, instr_limit, utlb);
            }
        }
    }
    // Returns to X86_Run loop.
    return instr_limit;
}

// Instantiate variants to reduce BTB pressure
#define INSTANTIATE_EXIT(i) OpExitBlock<i>
HandlerFunc g_ExitHandlers[32] = {
    INSTANTIATE_EXIT(0),  INSTANTIATE_EXIT(1),  INSTANTIATE_EXIT(2),  INSTANTIATE_EXIT(3),  INSTANTIATE_EXIT(4),
    INSTANTIATE_EXIT(5),  INSTANTIATE_EXIT(6),  INSTANTIATE_EXIT(7),  INSTANTIATE_EXIT(8),  INSTANTIATE_EXIT(9),
    INSTANTIATE_EXIT(10), INSTANTIATE_EXIT(11), INSTANTIATE_EXIT(12), INSTANTIATE_EXIT(13), INSTANTIATE_EXIT(14),
    INSTANTIATE_EXIT(15), INSTANTIATE_EXIT(16), INSTANTIATE_EXIT(17), INSTANTIATE_EXIT(18), INSTANTIATE_EXIT(19),
    INSTANTIATE_EXIT(20), INSTANTIATE_EXIT(21), INSTANTIATE_EXIT(22), INSTANTIATE_EXIT(23), INSTANTIATE_EXIT(24),
    INSTANTIATE_EXIT(25), INSTANTIATE_EXIT(26), INSTANTIATE_EXIT(27), INSTANTIATE_EXIT(28), INSTANTIATE_EXIT(29),
    INSTANTIATE_EXIT(30), INSTANTIATE_EXIT(31)};

// Global dispatch table
// This is initialized by HandlerInit static constructor below
// Anchor variable to calculate offsets against
static uint8_t g_HandlerBaseMarker = 0;
void* g_HandlerBase = (void*)&g_HandlerBaseMarker;

HandlerFunc g_Handlers[1024] = {nullptr};

// Static initialization of dispatch table
struct HandlerInit {
    HandlerInit() {
        // 1. Clear All
        for (int i = 0; i < 1024; ++i) {
            g_Handlers[i] = nullptr;
        }

        // 2. Register all modular operations
        RegisterAluOps();
        RegisterCompareOps();
        RegisterControlOps();
        RegisterDataMovOps();
        RegisterDoubleShiftOps();
        RegisterFpuOps();
        RegisterGroupOps();
        RegisterMulDivOps();
        RegisterShiftBitOps();
        RegisterSseCvtOps();
        RegisterSseFpOps();
        RegisterSseIntOps();
        RegisterSseMovOps();
    }
};

// Static instance to trigger initialization
static HandlerInit _init;

// Specialization Registry
// Array of vectors for O(1) opcode lookup
static std::vector<SpecializedEntry> g_OpSpecializations[1024];
// Lookup Cache to accelerate finding specialized handlers
// Thread-local for lock-free access
static thread_local ankerl::unordered_dense::map<uint64_t, HandlerFunc> g_SpecCache;

void RegisterSpecializedHandler(uint16_t opcode, SpecCriteria criteria, HandlerFunc handler) {
    if (opcode >= 1024) return;
    auto& list = g_OpSpecializations[opcode];
    list.push_back({opcode, criteria, handler});

    // Sort by specificity (Descending Score)
    // Most specific first
    std::sort(list.begin(), list.end(), [](const SpecializedEntry& a, const SpecializedEntry& b) {
        return a.criteria.GetScore() > b.criteria.GetScore();
    });

    // Clear cache on registration?
    // Since cache is thread_local and registration happens at startup (main thread),
    // clearing main thread's cache is fine but others start empty anyway.
    // We can skip explicit clear here as it's unlikely to have stale entries during static init.
}

HandlerFunc FindSpecializedHandler(uint16_t handler_index, DecodedOp* op) {
    if (handler_index >= 1024) return nullptr;

    // Construct Cache Key
    // Layout:
    // [0-9]   Opcode (10 bits)
    // [10-17] ModRM (8 bits)
    // [18-33] Prefixes (16 bits)
    // [34]    HasModRM (1 bit)
    // [35]    NoFlags (1 bit)
    uint64_t key = handler_index;

    if (op->meta.flags.has_modrm) {
        key |= ((uint64_t)op->modrm << 10);
        key |= (1ULL << 34);
    }
    key |= ((uint64_t)op->prefixes.all << 18);
    if (op->meta.flags.no_flags) key |= (1ULL << 35);

    // Check Cache
    auto it = g_SpecCache.find(key);
    if (it != g_SpecCache.end()) return it->second;

    // Lookup
    const auto& list = g_OpSpecializations[handler_index];
    HandlerFunc found = nullptr;

    for (const auto& entry : list) {
        // Opcode check implicit by array index

        // 1. Prefix Check always
        if (entry.criteria.prefix_mask) {
            if ((op->prefixes.all & entry.criteria.prefix_mask) != entry.criteria.prefix_val) continue;
        }

        // 2. ModRM Check
        if (op->meta.flags.has_modrm) {
            // Standard check
            if (!entry.criteria.Matches(op->modrm, op->prefixes.all, op->meta.flags.no_flags)) continue;
        } else {
            // No ModRM in instruction.
            // If criteria REQUIRES ModRM specific values (mask != 0), then it's a mismatch.
            if (entry.criteria.mod_mask || entry.criteria.reg_mask || entry.criteria.rm_mask) continue;

            // Still check no_flags even without ModRM
            if (entry.criteria.no_flags != op->meta.flags.no_flags) continue;
        }

        found = entry.handler;
        break;  // Found the most specific match due to sorting
    }

    // Update Cache
    g_SpecCache[key] = found;
    return found;
}

}  // namespace fiberish
