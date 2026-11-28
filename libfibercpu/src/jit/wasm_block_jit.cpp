#include "jit/wasm_block_jit.h"

#include <algorithm>
#include <array>
#include <cassert>
#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <limits>
#include <string>
#include <vector>

#include "decoder.h"
#include "dfe_lut.h"
#include "dispatch.h"
#include "exec_utils.h"
#include "jit/wasm_builder.h"
#include "mem/mmu.h"
#include "ops.h"
#include "state.h"

#ifdef __EMSCRIPTEN__
#include <emscripten/em_js.h>
#endif

namespace fiberish {

#ifdef __EMSCRIPTEN__

// ============================================================================
// JS / WebAssembly Runtime Bridge (CoreCLR-compatible wasmTable + wasmMemory)
// ============================================================================

// clang-format off
EM_JS(int, fiber_wasm_table_pool_grow, (int pool_size), {
    var tbl = (typeof wasmTable !== 'undefined')
                  ? wasmTable
                  : (typeof __indirect_function_table !== 'undefined' ? __indirect_function_table : null);
    if (!tbl || typeof wasmMemory === 'undefined') return -1;
    try {
        return tbl.grow(pool_size);
    } catch (e) {
        console.error('[WasmJIT] table.grow failed:', e);
        return -1;
    }
});

EM_JS(int, fiber_wasm_compile_and_install,
      (int table_slot, const uint8_t* ptr, int len, int read_slow_idx, int write_slow_idx), {
          var tbl = (typeof wasmTable !== 'undefined')
                        ? wasmTable
                        : (typeof __indirect_function_table !== 'undefined' ? __indirect_function_table : null);
          if (!tbl) return 0;
          try {
              var bytes = (typeof SharedArrayBuffer !== 'undefined' && HEAPU8.buffer instanceof SharedArrayBuffer)
                              ? HEAPU8.slice(ptr, ptr + len)
                              : HEAPU8.subarray(ptr, ptr + len);
              var mod = new WebAssembly.Module(bytes);
              var imports = {
                  env : {
                      memory : wasmMemory,
                      __indirect_function_table : tbl,
                      read_slow : tbl.get(read_slow_idx),
                      write_slow : tbl.get(write_slow_idx),
                  }
              };
              var inst = new WebAssembly.Instance(mod, imports);
              tbl.set(table_slot, inst.exports.f);
              return 1;
          } catch (e) {
              console.error('[WasmJIT] compile/instantiate failed (len=' + len + '):', e);
              return 0;
          }
      });

EM_JS(void, fiber_wasm_table_clear_slot, (int table_slot), {
    var tbl = (typeof wasmTable !== 'undefined')
                  ? wasmTable
                  : (typeof __indirect_function_table !== 'undefined' ? __indirect_function_table : null);
    if (tbl) tbl.set(table_slot, null);
});

EM_JS(double, fiber_wasm_now_ms, (),
      { return (typeof performance !== 'undefined' && performance.now) ? performance.now() : Date.now(); });
// clang-format on

namespace {

using namespace wasm_jit;

// ============================================================================
// Slow-Path Helpers (Imported directly by JIT Wasm modules)
// ============================================================================

struct Bytes16 {
    std::byte data[16];
};

// Returns (1ULL << 32) | value on success (or 1ULL << 32 with data in state->wasm_jit_scratch for 8/16B),
// or 0 on fault/SMC/stop/eip_dirty. Bit 7 of `size` requests 16-byte alignment (#GP(13) on misalignment).
extern "C" uint64_t FiberWasmJitReadSlow(EmuState* state, uint32_t inst_eip, uint32_t addr, uint32_t size) {
    state->ctx.eip = inst_eip;
    const bool align16 = (size & 0x80u) != 0;
    const uint32_t bytes = size & 0x7Fu;
    if (align16 && (addr & 15u) != 0) {
        state->status = EmuStatus::Fault;
        state->fault_vector = 13;
        return 0;
    }
    RestartableMemoryFaultScope restartable(state);
    uint32_t val = 0;
    bool ok = false;
    switch (bytes) {
        case 1:
            if (auto r = state->mmu.read_slow<uint8_t>(addr)) {
                val = *r;
                ok = true;
            }
            break;
        case 2:
            if (auto r = state->mmu.read_slow<uint16_t>(addr)) {
                val = *r;
                ok = true;
            }
            break;
        case 4:
            if (auto r = state->mmu.read_slow<uint32_t>(addr)) {
                val = *r;
                ok = true;
            }
            break;
        case 8:
            if (auto r = state->mmu.read_slow<uint64_t>(addr)) {
                std::memcpy(state->wasm_jit_scratch.data(), &*r, 8);
                ok = true;
            }
            break;
        case 16:
            if (auto r = state->mmu.read_slow<Bytes16>(addr)) {
                std::memcpy(state->wasm_jit_scratch.data(), r->data, 16);
                ok = true;
            }
            break;
        default:
            break;
    }
    if (!ok || state->status != EmuStatus::Running || state->eip_dirty) return 0;
    return (uint64_t{1} << 32) | val;
}

// Returns 1 on success, or 0 on fault/SMC/stop/eip_dirty.
// For 8/16B writes, caller places bytes in state->wasm_jit_scratch before calling.
// Bit 7 of `size` requests 16-byte alignment (#GP(13) on misalignment).
extern "C" uint32_t FiberWasmJitWriteSlow(EmuState* state, uint32_t inst_eip, uint32_t addr, uint32_t val,
                                          uint32_t size) {
    state->ctx.eip = inst_eip;
    const bool align16 = (size & 0x80u) != 0;
    const uint32_t bytes = size & 0x7Fu;
    if (align16 && (addr & 15u) != 0) {
        state->status = EmuStatus::Fault;
        state->fault_vector = 13;
        return 0;
    }
    RestartableMemoryFaultScope restartable(state);
    bool ok = false;
    switch (bytes) {
        case 1:
            ok = state->mmu.write_slow<uint8_t>(addr, static_cast<uint8_t>(val)).has_value();
            break;
        case 2:
            ok = state->mmu.write_slow<uint16_t>(addr, static_cast<uint16_t>(val)).has_value();
            break;
        case 4:
            ok = state->mmu.write_slow<uint32_t>(addr, val).has_value();
            break;
        case 8: {
            uint64_t v64 = 0;
            std::memcpy(&v64, state->wasm_jit_scratch.data(), 8);
            ok = state->mmu.write_slow<uint64_t>(addr, v64).has_value();
            break;
        }
        case 16: {
            Bytes16 v16{};
            std::memcpy(v16.data, state->wasm_jit_scratch.data(), 16);
            ok = state->mmu.write_slow<Bytes16>(addr, v16).has_value();
            break;
        }
        default:
            break;
    }
    if (!ok || state->status != EmuStatus::Running || state->eip_dirty) return 0;
    return 1;
}

// ============================================================================
// Runtime Configuration, Dynamic Stats & Fixed-Size WasmTablePool
// ============================================================================

struct WasmJitDynamicStats {
    uint64_t jit_block_entries = 0;
    uint64_t jit_self_loop_iters = 0;
    uint64_t jit_insts_executed = 0;
    uint64_t jit_fallback_exits = 0;
    uint64_t jit_slow_reads = 0;
    uint64_t jit_slow_writes = 0;
    uint64_t total_insts_executed = 0;
};

struct WasmJitRuntime {
    bool initialized = false;
    bool enabled = true;
    bool stats_enabled = false;
    bool debug_names = false;
    uint16_t hot_threshold = 64;
    uint32_t requested_pool_size = 1024;

    // WasmTablePool state
    int32_t pool_base = -1;
    uint32_t pool_size = 0;
    uint32_t evict_cursor = 0;
    uint32_t live_slots = 0;
    std::vector<uint32_t> free_list;
    std::vector<BasicBlock*> slot_owners;

    // Compile-time telemetry
    uint64_t compiled_blocks = 0;
    uint64_t skipped_blocks = 0;
    uint64_t compile_failures = 0;
    uint64_t evicted_blocks = 0;
    uint64_t invalidated_blocks = 0;
    uint64_t self_loop_blocks = 0;
    uint64_t partial_prefix_blocks = 0;
    uint64_t full_blocks = 0;
    uint64_t total_wasm_bytes = 0;
    uint64_t total_compiled_insts = 0;
    uint64_t total_block_insts = 0;
    double total_compile_ms = 0.0;

    WasmJitDynamicStats dyn;

    void EnsureInit() {
        if (initialized) return;
        initialized = true;
        if (const char* v = std::getenv("FIBERCPU_WASM_JIT")) {
            enabled = !(v[0] == '0' && v[1] == '\0') && std::strcmp(v, "off") != 0 && std::strcmp(v, "OFF") != 0;
        }
        if (const char* v = std::getenv("FIBERCPU_WASM_JIT_HOT_THRESHOLD")) {
            unsigned long parsed = std::strtoul(v, nullptr, 10);
            if (parsed > 0 && parsed <= 65535) hot_threshold = static_cast<uint16_t>(parsed);
        }
        if (const char* v = std::getenv("FIBERCPU_WASM_JIT_POOL_SIZE")) {
            unsigned long parsed = std::strtoul(v, nullptr, 10);
            if (parsed >= 16 && parsed <= 65536) requested_pool_size = static_cast<uint32_t>(parsed);
        }
        if (const char* v = std::getenv("FIBERCPU_WASM_JIT_STATS")) {
            stats_enabled = v[0] != '\0' && std::strcmp(v, "0") != 0;
        }
        if (const char* v = std::getenv("FIBERCPU_WASM_JIT_DEBUG_NAMES")) {
            debug_names = v[0] != '\0' && std::strcmp(v, "0") != 0;
        }
    }

    bool EnsurePool() {
        if (pool_base >= 0) return true;
        const int base = fiber_wasm_table_pool_grow(static_cast<int>(requested_pool_size));
        if (base < 0) return false;
        pool_base = base;
        pool_size = requested_pool_size;
        slot_owners.assign(pool_size, nullptr);
        free_list.clear();
        free_list.reserve(pool_size);
        for (int32_t i = static_cast<int32_t>(pool_size) - 1; i >= 0; --i) {
            free_list.push_back(static_cast<uint32_t>(i));
        }
        evict_cursor = 0;
        live_slots = 0;
        return true;
    }

    bool IsPoolEntry(HandlerFunc fn, uint32_t& out_slot) const {
        if (pool_base < 0 || !fn) return false;
        const uintptr_t idx = reinterpret_cast<uintptr_t>(fn);
        const uintptr_t base = static_cast<uintptr_t>(pool_base);
        if (idx >= base && idx < base + pool_size) {
            out_slot = static_cast<uint32_t>(idx - base);
            return true;
        }
        return false;
    }

    HandlerFunc TableIndexToHandler(uint32_t slot) const {
        return reinterpret_cast<HandlerFunc>(static_cast<uintptr_t>(pool_base) + slot);
    }

    int32_t AllocSlot(BasicBlock* owner) {
        if (!EnsurePool()) return -1;
        uint32_t slot = 0;
        if (!free_list.empty()) {
            slot = free_list.back();
            free_list.pop_back();
            live_slots++;
        } else {
            // FIFO eviction of oldest slot. Safe because compilation runs only in X86_Run
            // outside any active block execution, and restoring entry = FirstOp()->handler
            // immediately detaches all chained callers on their next dynamic entry load.
            slot = evict_cursor;
            evict_cursor = (evict_cursor + 1) % pool_size;
            BasicBlock* old = slot_owners[slot];
            if (old && old->entry == TableIndexToHandler(slot)) {
                old->entry = old->FirstOp()->handler;
                old->chain.llvm_hot_count = std::max<uint16_t>(hot_threshold, 64);
            }
            fiber_wasm_table_clear_slot(pool_base + static_cast<int>(slot));
            evicted_blocks++;
        }
        slot_owners[slot] = owner;
        return static_cast<int32_t>(slot);
    }

    void ReleaseSlot(uint32_t slot, BasicBlock* expected_owner) {
        if (pool_base < 0 || slot >= pool_size) return;
        if (expected_owner && slot_owners[slot] != expected_owner) return;
        if (!slot_owners[slot]) return;
        slot_owners[slot] = nullptr;
        fiber_wasm_table_clear_slot(pool_base + static_cast<int>(slot));
        free_list.push_back(slot);
        if (live_slots > 0) live_slots--;
    }

    void ResetPool() {
        if (pool_base < 0) return;
        for (uint32_t slot = 0; slot < pool_size; ++slot) {
            BasicBlock* owner = slot_owners[slot];
            if (owner) {
                if (owner->entry == TableIndexToHandler(slot)) {
                    owner->entry = owner->FirstOp()->handler;
                    owner->chain.llvm_hot_count = hot_threshold;
                }
                slot_owners[slot] = nullptr;
                fiber_wasm_table_clear_slot(pool_base + static_cast<int>(slot));
            }
        }
        free_list.clear();
        for (int32_t i = static_cast<int32_t>(pool_size) - 1; i >= 0; --i) {
            free_list.push_back(static_cast<uint32_t>(i));
        }
        evict_cursor = 0;
        live_slots = 0;
    }
};

WasmJitRuntime g_rt;

// ============================================================================
// Structure Layout Verification (Wasm32)
// ============================================================================

static_assert(sizeof(void*) == 4, "Wasm JIT targets wasm32");
static_assert(offsetof(EmuState, ctx) == 0);
static_assert(offsetof(Context, regs) == 0);
static_assert(sizeof(mem::MicroTLB) == 16);
static_assert(offsetof(mem::MicroTLB, tags) == 0);
static_assert(offsetof(mem::MicroTLB, addend) == 8);
static_assert(offsetof(BasicBlock, chain) == 0);
static_assert(offsetof(BasicBlock, entry) == 8);
static_assert(offsetof(BasicBlock, slots) == 32);
static_assert(sizeof(DecodedOp) == 16);
static_assert(offsetof(DecodedOp, handler) == 0);
static_assert(offsetof(DecodedOp, payload) == 8);

static_assert(offsetof(Context, xmm) % 16 == 0);
static_assert(sizeof(Context::xmm[0]) == 16);
static_assert(offsetof(EmuState, wasm_jit_scratch) % 16 == 0);

constexpr uint32_t kOffRegs = 0;
constexpr uint32_t kOffEip = static_cast<uint32_t>(offsetof(Context, eip));
constexpr uint32_t kOffFlagsState = static_cast<uint32_t>(offsetof(Context, flags_state));
constexpr uint32_t kOffSegBase = static_cast<uint32_t>(offsetof(Context, seg_base));
constexpr uint32_t kOffMxcsr = static_cast<uint32_t>(offsetof(Context, mxcsr));
constexpr uint32_t kOffXmm = static_cast<uint32_t>(offsetof(Context, xmm));
constexpr uint32_t kOffLastBlock = static_cast<uint32_t>(offsetof(EmuState, last_block));
constexpr uint32_t kOffWasmJitScratch = static_cast<uint32_t>(offsetof(EmuState, wasm_jit_scratch));
constexpr uint32_t kOffHotBlock = static_cast<uint32_t>(offsetof(EmuState, llvm_hot_block));
constexpr uint32_t kOffReadTlb = static_cast<uint32_t>(offsetof(EmuState, mmu) + mem::Mmu::ReadTlbOffset());
constexpr uint32_t kOffWriteTlb = static_cast<uint32_t>(offsetof(EmuState, mmu) + mem::Mmu::WriteTlbOffset());
constexpr uint32_t kTlbIdxMaskShifted = static_cast<uint32_t>((mem::TLB_INDEX_MASK) << 4);
static_assert(mem::PAGE_SHIFT == 12);

// Handler parameter indices
constexpr uint32_t kParamState = 0;
constexpr uint32_t kParamLimit = 2;
constexpr uint32_t kParamUtlbTags = 3;
constexpr uint32_t kParamUtlbAddend = 4;
constexpr uint32_t kParamFlags = 6;

// Imported function indices
constexpr uint32_t kFuncReadSlow = 0;
constexpr uint32_t kFuncWriteSlow = 1;

// ============================================================================
// Decoded Instruction Representation for Single-Block Compilation
// ============================================================================

struct JitOp {
    DecodedInstruction pub{};  // Published op from BasicBlock::ReadOp(i)
    DecodedInstruction dec{};  // Generic re-decoded op (exact raw_opcode, ea, imm, modrm)
    const DecodedOp* phys = nullptr;
    uint32_t start_eip = 0;
    uint32_t next_eip = 0;
    uint16_t raw_opcode = 0;
    uint32_t live_flags_after = DFE_ALL_FLAGS;
    bool supported = false;
};

constexpr uint32_t WidthMask(uint8_t width) { return width == 8 ? 0xFFu : (width == 16 ? 0xFFFFu : 0xFFFFFFFFu); }

constexpr uint8_t OpWidth(const JitOp& op, bool is_byte_opcode) {
    if (is_byte_opcode) return 8;
    return op.dec.prefixes.flags.opsize ? 16 : 32;
}

bool IsSupportedOpcode(const JitOp& jop) {
    // Reject LOCK and zero-length synthetic fault ops
    if (jop.pub.len == 0) return false;
    if (jop.dec.prefixes.flags.lock) return false;

    const uint16_t op = jop.raw_opcode;
    const bool p66 = jop.dec.prefixes.flags.opsize != 0;
    const bool pf3 = jop.dec.prefixes.flags.rep != 0;
    const bool pf2 = jop.dec.prefixes.flags.repne != 0;

    // On map 0 (legacy opcodes), reject REP/REPNE prefixes.
    if (op < 0x100 && (pf3 || pf2)) return false;

    const uint8_t subop = (jop.dec.modrm >> 3) & 7;
    const bool is_mem = HasMem(&jop.dec);

    // ALU 0x00..0x3D (ADD, OR, ADC, SBB, AND, SUB, XOR, CMP)
    if (op <= 0x3D && (op & 7) <= 5) return true;

    // INC / DEC r16/32 (0x40..0x4F)
    if (op >= 0x40 && op <= 0x4F) return true;

    // PUSH / POP r16/32 (0x50..0x5F)
    if (op >= 0x50 && op <= 0x5F) return true;

    // PUSH imm (0x68, 0x6A), IMUL 3-op (0x69, 0x6B)
    if (op == 0x68 || op == 0x6A || op == 0x69 || op == 0x6B) return true;

    // Jcc rel8 (0x70..0x7F)
    if (op >= 0x70 && op <= 0x7F) return true;

    // Group 1 (0x80..0x83)
    if (op >= 0x80 && op <= 0x83) return true;

    // TEST (0x84, 0x85), XCHG (0x86, 0x87), MOV (0x88..0x8B), LEA (0x8D)
    if ((op >= 0x84 && op <= 0x8B) || op == 0x8D) return true;

    // POP r/m (0x8F /0) - register form only (memory POP [esp] rare)
    if (op == 0x8F && subop == 0 && !is_mem) return true;

    // NOP / XCHG EAX, r (0x90..0x97), CWDE (0x98), CDQ (0x99)
    if (op >= 0x90 && op <= 0x99) return true;

    // MOV moffs (0xA0..0xA3), TEST AL/EAX, imm (0xA8, 0xA9)
    if ((op >= 0xA0 && op <= 0xA3) || op == 0xA8 || op == 0xA9) return true;

    // MOV reg, imm (0xB0..0xBF)
    if (op >= 0xB0 && op <= 0xBF) return true;

    // Group 2 shifts/rotates (0xC0, 0xC1, 0xD0, 0xD1, 0xD2, 0xD3) except RCL(2)/RCR(3)
    if (op == 0xC0 || op == 0xC1 || op == 0xD0 || op == 0xD1 || op == 0xD2 || op == 0xD3) {
        return subop != 2 && subop != 3;
    }

    // RET imm16 (0xC2), RET (0xC3), MOV r/m, imm (0xC6/0, 0xC7/0), LEAVE (0xC9)
    if (op == 0xC2 || op == 0xC3 || op == 0xC9) return true;
    if ((op == 0xC6 || op == 0xC7) && subop == 0) return true;

    // CALL rel (0xE8), JMP rel32 (0xE9), JMP rel8 (0xEB)
    if (op == 0xE8 || op == 0xE9 || op == 0xEB) return true;

    // Group 3 (0xF6, 0xF7): TEST(0,1), NOT(2), NEG(3), MUL(4), IMUL(5). Fallback DIV(6)/IDIV(7).
    if (op == 0xF6 || op == 0xF7) return subop <= 5;

    // Group 4 (0xFE): INC(0), DEC(1)
    if (op == 0xFE) return subop <= 1;

    // Group 5 (0xFF): INC(0), DEC(1), CALL Ev(2), JMP Ev(4), PUSH Ev(6)
    if (op == 0xFF) return subop == 0 || subop == 1 || subop == 2 || subop == 4 || subop == 6;

    // 0x0F Map 1
    if (op < 0x100 || op >= 0x200) return false;
    // Disallow ambiguous multi-prefix (66/F2/F3 combined) encodings on map 1
    if (static_cast<int>(p66) + static_cast<int>(pf3) + static_cast<int>(pf2) > 1) return false;

    const bool no_pfx = !p66 && !pf3 && !pf2;

    // Integer Map 1 opcodes (require no F2/F3 prefix)
    if (!pf2 && !pf3) {
        // Multi-byte NOP (0x11F)
        if (op == 0x11F) return true;
        // CMOVcc (0x140..0x14F)
        if (op >= 0x140 && op <= 0x14F) return true;
        // Jcc rel16/32 (0x180..0x18F)
        if (op >= 0x180 && op <= 0x18F) return true;
        // SETcc (0x190..0x19F)
        if (op >= 0x190 && op <= 0x19F) return true;
        // IMUL Gv, Ev (0x1AF)
        if (op == 0x1AF) return true;
        // MOVZX (0x1B6, 0x1B7), MOVSX (0x1BE, 0x1BF)
        if (op == 0x1B6 || op == 0x1B7 || op == 0x1BE || op == 0x1BF) return true;
        // BSWAP (0x1C8..0x1CF)
        if (op >= 0x1C8 && op <= 0x1CF) return true;
    }

    // Native Wasm SSE / SSE2 / SSE3 opcodes
    switch (op) {
        case 0x110:  // MOVUPS / 66 MOVUPD / F3 MOVSS / F2 MOVSD (load / reg-reg)
        case 0x111:  // MOVUPS / 66 MOVUPD / F3 MOVSS / F2 MOVSD (store / reg-reg)
            return true;
        case 0x112:  // 66 MOVLPD(mem) / MOVLPS(mem) / MOVHLPS(reg) / F2 MOVDDUP / F3 MOVSLDUP
            return p66 ? is_mem : true;
        case 0x113:  // 66 MOVLPD / MOVLPS (store m64, xmm)
            return (p66 || no_pfx) && is_mem;
        case 0x114:  // UNPCKLPS / 66 UNPCKLPD
        case 0x115:  // UNPCKHPS / 66 UNPCKHPD
            return p66 || no_pfx;
        case 0x116:  // 66 MOVHPD(mem) / MOVHPS(mem) / MOVLHPS(reg) / F3 MOVSHDUP
            return p66 ? is_mem : (no_pfx || pf3);
        case 0x117:  // 66 MOVHPD / MOVHPS (store m64, xmm)
            return (p66 || no_pfx) && is_mem;
        case 0x128:  // MOVAPS / 66 MOVAPD (load)
        case 0x129:  // MOVAPS / 66 MOVAPD (store)
            return p66 || no_pfx;
        case 0x12A:  // F2 CVTSI2SD / F3 CVTSI2SS
        case 0x12C:  // F2 CVTTSD2SI / F3 CVTTSS2SI
        case 0x12D:  // F2 CVTSD2SI / F3 CVTSS2SI
            return pf2 || pf3;
        case 0x12E:  // UCOMISS / 66 UCOMISD
        case 0x12F:  // COMISS / 66 COMISD
            return p66 || no_pfx;
        case 0x150:  // MOVMSKPS / 66 MOVMSKPD
            return (p66 || no_pfx) && !is_mem;
        case 0x151:  // SQRTPS / 66 SQRTPD / F3 SQRTSS / F2 SQRTSD
        case 0x158:  // ADDPS / 66 ADDPD / F3 ADDSS / F2 ADDSD
        case 0x159:  // MULPS / 66 MULPD / F3 MULSS / F2 MULSD
        case 0x15A:  // CVTPS2PD / 66 CVTPD2PS / F2 CVTSD2SS / F3 CVTSS2SD
        case 0x15C:  // SUBPS / 66 SUBPD / F3 SUBSS / F2 SUBSD
        case 0x15D:  // MINPS / 66 MINPD / F3 MINSS / F2 MINSD
        case 0x15E:  // DIVPS / 66 DIVPD / F3 DIVSS / F2 DIVSD
        case 0x15F:  // MAXPS / 66 MAXPD / F3 MAXSS / F2 MAXSD
            return true;
        case 0x154:  // ANDPS / 66 ANDPD
        case 0x155:  // ANDNPS / 66 ANDNPD
        case 0x156:  // ORPS / 66 ORPD
        case 0x157:  // XORPS / 66 XORPD
        case 0x1C6:  // SHUFPS / 66 SHUFPD
            return p66 || no_pfx;
        case 0x16E:  // 66 MOVD xmm, r/m32
            return p66;
        case 0x16F:  // 66 MOVDQA / F3 MOVDQU (load)
        case 0x17F:  // 66 MOVDQA / F3 MOVDQU (store)
        case 0x17E:  // 66 MOVD r/m32, xmm / F3 MOVQ xmm, xmm/m64
            return p66 || pf3;
        case 0x170:  // 66 PSHUFD / F2 PSHUFLW / F3 PSHUFHW
            return p66 || pf2 || pf3;
        case 0x173:  // 66 /3 PSRLDQ / 66 /7 PSLLDQ
            return p66 && !is_mem && (subop == 3 || subop == 7);
        case 0x1D6:  // 66 MOVQ xmm/m64, xmm
            return p66;
        case 0x1D7:  // 66 PMOVMSKB r32, xmm
            return p66 && !is_mem;
        case 0x1E6:  // F3 CVTDQ2PD
            return pf3;
        case 0x162:  // 66 PUNPCKLDQ
        case 0x164:  // 66 PCMPGTB
        case 0x165:  // 66 PCMPGTW
        case 0x166:  // 66 PCMPGTD
        case 0x16A:  // 66 PUNPCKHDQ
        case 0x16C:  // 66 PUNPCKLQDQ
        case 0x16D:  // 66 PUNPCKHQDQ
        case 0x174:  // 66 PCMPEQB
        case 0x175:  // 66 PCMPEQW
        case 0x176:  // 66 PCMPEQD
        case 0x1D4:  // 66 PADDQ
        case 0x1DA:  // 66 PMINUB
        case 0x1DB:  // 66 PAND
        case 0x1DE:  // 66 PMAXUB
        case 0x1DF:  // 66 PANDN
        case 0x1EB:  // 66 POR
        case 0x1EF:  // 66 PXOR
        case 0x1F8:  // 66 PSUBB
        case 0x1F9:  // 66 PSUBW
        case 0x1FA:  // 66 PSUBD
        case 0x1FB:  // 66 PSUBQ
        case 0x1FC:  // 66 PADDB
        case 0x1FD:  // 66 PADDW
        case 0x1FE:  // 66 PADDD
            return p66;
        default:
            return false;
    }
}

// ============================================================================
// Flag Liveness & Register Touch Pre-Analysis
// ============================================================================

struct OpTouchInfo {
    uint8_t read_mask = 0;
    uint8_t write_mask = 0;
    uint8_t xmm_read_mask = 0;
    uint8_t xmm_write_mask = 0;
    bool reads_mem = false;
    bool writes_mem = false;
    bool uses_seg_ds_ss = false;
    bool uses_f64_tmp = false;
    bool uses_f32_tmp = false;
    bool needs_default_mxcsr = false;
    uint32_t flag_reads = 0;
    uint32_t flag_writes = 0;
};

uint8_t GprBit(uint8_t reg, uint8_t width) { return static_cast<uint8_t>(1u << (width == 8 ? (reg & 3) : (reg & 7))); }

void AnalyzeEaTouch(const DecodedInstruction& d, OpTouchInfo& info, bool is_memory_access) {
    const uint32_t ea = d.ext.data.ea_desc;
    const uint8_t base_off = memdesc::BaseOffset(ea);
    const uint8_t idx_off = memdesc::IndexOffset(ea);
    if (base_off != kNoRegOffset) info.read_mask |= static_cast<uint8_t>(1u << (base_off >> 2));
    if (idx_off != kNoRegOffset) info.read_mask |= static_cast<uint8_t>(1u << (idx_off >> 2));
    if (is_memory_access) {
        const uint8_t seg = memdesc::ResolvedSegment(ea);
        if (seg >= 1 && seg <= 4) info.uses_seg_ds_ss = true;
    }
}

OpTouchInfo AnalyzeOpTouch(const JitOp& jop) {
    OpTouchInfo info;
    const uint16_t op = jop.raw_opcode;
    const uint8_t modrm = jop.dec.modrm;
    const uint8_t reg = (modrm >> 3) & 7;
    const uint8_t rm = modrm & 7;
    const bool is_mem = HasMem(&jop.dec);
    const uint8_t subop = reg;

    auto touch_rm = [&](uint8_t width, bool rd, bool wr) {
        if (is_mem) {
            AnalyzeEaTouch(jop.dec, info, true);
            if (rd) info.reads_mem = true;
            if (wr) info.writes_mem = true;
        } else {
            const uint8_t b = GprBit(rm, width);
            if (rd || (wr && width < 32)) info.read_mask |= b;
            if (wr) info.write_mask |= b;
        }
    };
    auto touch_reg = [&](uint8_t r, uint8_t width, bool rd, bool wr) {
        const uint8_t b = GprBit(r, width);
        if (rd || (wr && width < 32)) info.read_mask |= b;
        if (wr) info.write_mask |= b;
    };

    if (op <= 0x3D && (op & 7) <= 5) {
        const uint8_t alu_op = static_cast<uint8_t>(op >> 3);
        const uint8_t form = static_cast<uint8_t>(op & 7);
        const uint8_t w = (form == 0 || form == 2 || form == 4) ? 8 : OpWidth(jop, false);
        const bool is_cmp = (alu_op == 7);
        if (form == 0 || form == 1) {
            touch_rm(w, true, !is_cmp);
            touch_reg(reg, w, true, false);
        } else if (form == 2 || form == 3) {
            touch_rm(w, true, false);
            touch_reg(reg, w, true, !is_cmp);
        } else {
            touch_reg(EAX, w, true, !is_cmp);
        }
        if (alu_op == 2 || alu_op == 3) info.flag_reads = CF_MASK;
        info.flag_writes = DFE_ALL_FLAGS;
    } else if (op >= 0x40 && op <= 0x4F) {
        const uint8_t w = OpWidth(jop, false);
        touch_reg(op & 7, w, true, true);
        info.flag_writes = DFE_ALL_FLAGS & ~CF_MASK;
    } else if (op >= 0x50 && op <= 0x57) {
        const uint8_t w = OpWidth(jop, false);
        touch_reg(op & 7, w, true, false);
        touch_reg(ESP, 32, true, true);
        info.writes_mem = true;
        info.uses_seg_ds_ss = true;
    } else if (op >= 0x58 && op <= 0x5F) {
        const uint8_t w = OpWidth(jop, false);
        touch_reg(op & 7, w, false, true);
        touch_reg(ESP, 32, true, true);
        info.reads_mem = true;
        info.uses_seg_ds_ss = true;
    } else if (op == 0x68 || op == 0x6A) {
        touch_reg(ESP, 32, true, true);
        info.writes_mem = true;
        info.uses_seg_ds_ss = true;
    } else if (op == 0x69 || op == 0x6B) {
        const uint8_t w = OpWidth(jop, false);
        touch_rm(w, true, false);
        touch_reg(reg, w, false, true);
        info.flag_writes = CF_MASK | OF_MASK;
    } else if ((op >= 0x70 && op <= 0x7F) || (op >= 0x180 && op <= 0x18F)) {
        info.flag_reads = DFE_ALL_FLAGS;
    } else if (op >= 0x80 && op <= 0x83) {
        const uint8_t w = (op == 0x80 || op == 0x82) ? 8 : OpWidth(jop, false);
        touch_rm(w, true, subop != 7);
        if (subop == 2 || subop == 3) info.flag_reads = CF_MASK;
        info.flag_writes = DFE_ALL_FLAGS;
    } else if (op == 0x84 || op == 0x85) {
        const uint8_t w = OpWidth(jop, op == 0x84);
        touch_rm(w, true, false);
        touch_reg(reg, w, true, false);
        info.flag_writes = DFE_ALL_FLAGS;
    } else if (op == 0x86 || op == 0x87) {
        const uint8_t w = OpWidth(jop, op == 0x86);
        touch_rm(w, true, true);
        touch_reg(reg, w, true, true);
    } else if (op == 0x88 || op == 0x89) {
        const uint8_t w = OpWidth(jop, op == 0x88);
        touch_rm(w, false, true);
        touch_reg(reg, w, true, false);
    } else if (op == 0x8A || op == 0x8B) {
        const uint8_t w = OpWidth(jop, op == 0x8A);
        touch_rm(w, true, false);
        touch_reg(reg, w, false, true);
    } else if (op == 0x8D) {
        const uint8_t w = OpWidth(jop, false);
        AnalyzeEaTouch(jop.dec, info, false);
        touch_reg(reg, w, false, true);
    } else if (op == 0x8F) {
        const uint8_t w = OpWidth(jop, false);
        touch_reg(rm, w, false, true);
        touch_reg(ESP, 32, true, true);
        info.reads_mem = true;
        info.uses_seg_ds_ss = true;
    } else if (op >= 0x91 && op <= 0x97) {
        const uint8_t w = OpWidth(jop, false);
        touch_reg(EAX, w, true, true);
        touch_reg(op & 7, w, true, true);
    } else if (op == 0x98) {
        touch_reg(EAX, 32, true, true);
    } else if (op == 0x99) {
        const uint8_t w = OpWidth(jop, false);
        touch_reg(EAX, w, true, false);
        touch_reg(EDX, w, false, true);
    } else if (op >= 0xA0 && op <= 0xA3) {
        const uint8_t w = OpWidth(jop, op == 0xA0 || op == 0xA2);
        const bool is_load = (op == 0xA0 || op == 0xA1);
        touch_reg(EAX, w, !is_load, is_load);
        if (is_load)
            info.reads_mem = true;
        else
            info.writes_mem = true;
        const uint8_t seg = jop.dec.prefixes.flags.segment;
        if (seg == 0 || seg <= 4) info.uses_seg_ds_ss = true;
    } else if (op == 0xA8 || op == 0xA9) {
        const uint8_t w = OpWidth(jop, op == 0xA8);
        touch_reg(EAX, w, true, false);
        info.flag_writes = DFE_ALL_FLAGS;
    } else if (op >= 0xB0 && op <= 0xBF) {
        const uint8_t w = op < 0xB8 ? 8 : OpWidth(jop, false);
        touch_reg(op & 7, w, false, true);
    } else if (op == 0xC0 || op == 0xC1 || op == 0xD0 || op == 0xD1 || op == 0xD2 || op == 0xD3) {
        const uint8_t w = (op == 0xC0 || op == 0xD0 || op == 0xD2) ? 8 : OpWidth(jop, false);
        touch_rm(w, true, true);
        if (op == 0xD2 || op == 0xD3) {
            touch_reg(ECX, 8, true, false);
            // CL might be 0 -> cannot assume unconditional kill of incoming flags
            info.flag_reads = DFE_ALL_FLAGS;
        } else {
            const uint32_t cnt = (op == 0xD0 || op == 0xD1) ? 1u : (GetImm(&jop.dec) & 31u);
            if (cnt != 0) {
                info.flag_writes = (subop < 4) ? (CF_MASK | (cnt == 1 ? OF_MASK : 0u)) : DFE_ALL_FLAGS;
            }
        }
    } else if (op == 0xC2 || op == 0xC3) {
        touch_reg(ESP, 32, true, true);
        info.reads_mem = true;
        info.uses_seg_ds_ss = true;
    } else if (op == 0xC6 || op == 0xC7) {
        const uint8_t w = OpWidth(jop, op == 0xC6);
        touch_rm(w, false, true);
    } else if (op == 0xC9) {
        const uint8_t w = OpWidth(jop, false);
        touch_reg(EBP, w, true, true);
        touch_reg(ESP, 32, false, true);
        info.reads_mem = true;
        info.uses_seg_ds_ss = true;
    } else if (op == 0xE8) {
        touch_reg(ESP, 32, true, true);
        info.writes_mem = true;
        info.uses_seg_ds_ss = true;
    } else if (op == 0xF6 || op == 0xF7) {
        const uint8_t w = OpWidth(jop, op == 0xF6);
        if (subop <= 1) {
            touch_rm(w, true, false);
            info.flag_writes = DFE_ALL_FLAGS;
        } else if (subop == 2) {
            touch_rm(w, true, true);
        } else if (subop == 3) {
            touch_rm(w, true, true);
            info.flag_writes = DFE_ALL_FLAGS;
        } else {  // MUL / IMUL
            touch_rm(w, true, false);
            touch_reg(EAX, w == 8 ? 16 : w, true, true);
            if (w > 8) touch_reg(EDX, w, false, true);
            info.flag_writes = CF_MASK | OF_MASK;
        }
    } else if (op == 0xFE) {
        touch_rm(8, true, true);
        info.flag_writes = DFE_ALL_FLAGS & ~CF_MASK;
    } else if (op == 0xFF) {
        const uint8_t w = OpWidth(jop, false);
        if (subop <= 1) {
            touch_rm(w, true, true);
            info.flag_writes = DFE_ALL_FLAGS & ~CF_MASK;
        } else if (subop == 2 || subop == 6) {
            touch_rm(w, true, false);
            touch_reg(ESP, 32, true, true);
            info.writes_mem = true;
            info.uses_seg_ds_ss = true;
        } else if (subop == 4) {
            touch_rm(w, true, false);
        }
    } else if (op >= 0x140 && op <= 0x14F) {
        const uint8_t w = OpWidth(jop, false);
        touch_rm(w, true, false);
        touch_reg(reg, w, true, true);
        info.flag_reads = DFE_ALL_FLAGS;
    } else if (op >= 0x190 && op <= 0x19F) {
        touch_rm(8, false, true);
        info.flag_reads = DFE_ALL_FLAGS;
    } else if (op == 0x1AF) {
        const uint8_t w = OpWidth(jop, false);
        touch_rm(w, true, false);
        touch_reg(reg, w, true, true);
        info.flag_writes = CF_MASK | OF_MASK;
    } else if (op == 0x1B6 || op == 0x1B7 || op == 0x1BE || op == 0x1BF) {
        const uint8_t src_w = (op == 0x1B6 || op == 0x1BE) ? 8 : 16;
        const uint8_t dst_w = OpWidth(jop, false);
        touch_rm(src_w, true, false);
        touch_reg(reg, dst_w, false, true);
    } else if (op >= 0x1C8 && op <= 0x1CF) {
        touch_reg(op & 7, 32, true, true);
    } else if (op >= 0x100 && op < 0x200) {
        const bool p66 = jop.dec.prefixes.flags.opsize != 0;
        const bool pf3 = jop.dec.prefixes.flags.rep != 0;
        const bool pf2 = jop.dec.prefixes.flags.repne != 0;
        auto touch_xmm = [&](uint8_t r, bool rd, bool wr) {
            const uint8_t b = static_cast<uint8_t>(1u << (r & 7));
            if (rd) info.xmm_read_mask |= b;
            if (wr) info.xmm_write_mask |= b;
        };
        auto touch_xmm_rm = [&](bool rd, bool wr) {
            if (is_mem) {
                AnalyzeEaTouch(jop.dec, info, true);
                if (rd) info.reads_mem = true;
                if (wr) info.writes_mem = true;
            } else {
                touch_xmm(rm, rd, wr);
            }
        };

        switch (op) {
            case 0x110:  // MOVUPS/MOVUPD/MOVSS/MOVSD (load / reg-reg)
                touch_xmm_rm(true, false);
                // Reg-reg scalar movss/movsd preserves upper bits of dst
                touch_xmm(reg, !is_mem && (pf2 || pf3), true);
                break;
            case 0x111:  // MOVUPS/MOVUPD/MOVSS/MOVSD (store / reg-reg)
                touch_xmm(reg, true, false);
                if (is_mem) {
                    AnalyzeEaTouch(jop.dec, info, true);
                    info.writes_mem = true;
                } else {
                    touch_xmm(rm, pf2 || pf3, true);
                }
                break;
            case 0x112:  // MOVLPD / MOVLPS / MOVHLPS / MOVDDUP / MOVSLDUP
                touch_xmm_rm(true, false);
                touch_xmm(reg, !(pf2 || pf3), true);
                break;
            case 0x113:  // MOVLPD / MOVLPS store
            case 0x117:  // MOVHPD / MOVHPS store
                touch_xmm(reg, true, false);
                AnalyzeEaTouch(jop.dec, info, true);
                info.writes_mem = true;
                break;
            case 0x114:  // UNPCKLPS / UNPCKLPD
            case 0x115:  // UNPCKHPS / UNPCKHPD
            case 0x1C6:  // SHUFPS / SHUFPD
                touch_xmm_rm(true, false);
                touch_xmm(reg, true, true);
                break;
            case 0x116:  // MOVHPD / MOVHPS / MOVLHPS / MOVSHDUP
                touch_xmm_rm(true, false);
                touch_xmm(reg, !pf3, true);
                break;
            case 0x128:  // MOVAPS / MOVAPD load
            case 0x16F:  // MOVDQA / MOVDQU load
            case 0x170:  // PSHUFD / PSHUFLW / PSHUFHW
            case 0x1E6:  // CVTDQ2PD
                touch_xmm_rm(true, false);
                touch_xmm(reg, false, true);
                break;
            case 0x129:  // MOVAPS / MOVAPD store
            case 0x17F:  // MOVDQA / MOVDQU store
            case 0x1D6:  // 66 MOVQ xmm/m64, xmm
                touch_xmm(reg, true, false);
                touch_xmm_rm(false, true);
                break;
            case 0x12A:  // CVTSI2SD / CVTSI2SS
                touch_rm(32, true, false);
                touch_xmm(reg, true, true);
                if (pf3) info.needs_default_mxcsr = true;
                break;
            case 0x12C:  // CVTTSD2SI / CVTTSS2SI
            case 0x12D:  // CVTSD2SI / CVTSS2SI
                touch_xmm_rm(true, false);
                touch_reg(reg, 32, false, true);
                if (pf2)
                    info.uses_f64_tmp = true;
                else
                    info.uses_f32_tmp = true;
                info.needs_default_mxcsr = true;
                break;
            case 0x12E:  // UCOMISS / UCOMISD
            case 0x12F:  // COMISS / COMISD
                touch_xmm_rm(true, false);
                touch_xmm(reg, true, false);
                if (p66)
                    info.uses_f64_tmp = true;
                else
                    info.uses_f32_tmp = true;
                info.flag_writes = DFE_ALL_FLAGS;
                break;
            case 0x150:  // MOVMSKPS / MOVMSKPD
            case 0x1D7:  // PMOVMSKB
                touch_xmm(rm, true, false);
                touch_reg(reg, 32, false, true);
                break;
            case 0x151:  // SQRTPS / SQRTPD / SQRTSS / SQRTSD
                touch_xmm_rm(true, false);
                touch_xmm(reg, pf2 || pf3, true);
                break;
            case 0x157:  // XORPS / XORPD
            case 0x1EF:  // PXOR
                if (!is_mem && reg == rm) {
                    touch_xmm(reg, false, true);
                } else {
                    touch_xmm_rm(true, false);
                    touch_xmm(reg, true, true);
                }
                break;
            case 0x15D:  // MINPS/PD/SS/SD
            case 0x15F:  // MAXPS/PD/SS/SD
                touch_xmm_rm(true, false);
                touch_xmm(reg, true, true);
                if (pf2) info.uses_f64_tmp = true;
                if (pf3) info.uses_f32_tmp = true;
                break;
            case 0x15A:  // CVTPS2PD / CVTPD2PS / CVTSD2SS / CVTSS2SD
                touch_xmm_rm(true, false);
                touch_xmm(reg, pf2 || pf3, true);
                break;
            case 0x16E:  // 66 MOVD xmm, r/m32
                touch_rm(32, true, false);
                touch_xmm(reg, false, true);
                break;
            case 0x17E:  // 66 MOVD r/m32, xmm / F3 MOVQ xmm, xmm/m64
                if (pf3) {
                    touch_xmm_rm(true, false);
                    touch_xmm(reg, false, true);
                } else {
                    touch_xmm(reg, true, false);
                    touch_rm(32, false, true);
                }
                break;
            case 0x173:  // PSRLDQ / PSLLDQ
                touch_xmm(rm, true, true);
                break;
            default:  // Packed/scalar binary ALU & bitwise ops
                touch_xmm_rm(true, false);
                touch_xmm(reg, true, true);
                break;
        }
    }

    return info;
}

// ============================================================================
// Single-Block i386 -> Wasm Compiler
// ============================================================================

enum class LazyFlagKind : uint8_t {
    Clean,   // flags_cache local is up to date
    Add,     // Pending ADD(width, flg_a, flg_b, flg_r)
    Sub,     // Pending SUB/CMP/NEG(width, flg_a, flg_b, flg_r)
    Logic,   // Pending AND/OR/XOR/TEST(width, flg_r)
    IncDec,  // Pending INC/DEC(width, flg_r, is_dec) - CF preserved in flags_cache
};

struct LazyFlagState {
    LazyFlagKind kind = LazyFlagKind::Clean;
    uint8_t width = 32;
    bool is_dec = false;
};

class SingleBlockEmitter {
public:
    SingleBlockEmitter(EmuState* state, BasicBlock* block, std::vector<JitOp>& ops, size_t compiled_count)
        : state_(state), block_(block), ops_(ops), compiled_count_(compiled_count) {}

    bool Emit(std::vector<uint8_t>& out_wasm, bool& out_has_self_loop) {
        w_.ResetAll(7);  // 7 HandlerFunc params

        // 1. Pre-scan register touches, memory ops, and backward flag liveness
        uint8_t touched_mask = 0;
        uint8_t written_mask = 0;
        uint8_t xmm_touched_mask = 0;
        uint8_t xmm_written_mask = 0;
        bool has_reads = false;
        bool has_writes = false;
        bool uses_seg_ds_ss = false;
        bool uses_f64_tmp = false;
        bool uses_f32_tmp = false;
        bool needs_default_mxcsr = false;
        std::vector<OpTouchInfo> touches(compiled_count_);

        for (size_t i = 0; i < compiled_count_; ++i) {
            touches[i] = AnalyzeOpTouch(ops_[i]);
            touched_mask |= touches[i].read_mask | touches[i].write_mask;
            written_mask |= touches[i].write_mask;
            xmm_touched_mask |= touches[i].xmm_read_mask | touches[i].xmm_write_mask;
            xmm_written_mask |= touches[i].xmm_write_mask;
            has_reads |= touches[i].reads_mem;
            has_writes |= touches[i].writes_mem;
            uses_seg_ds_ss |= touches[i].uses_seg_ds_ss;
            uses_f64_tmp |= touches[i].uses_f64_tmp;
            uses_f32_tmp |= touches[i].uses_f32_tmp;
            needs_default_mxcsr |= touches[i].needs_default_mxcsr;
        }

        // Backward intra-block flag liveness across [0 .. compiled_count_).
        // Memory accesses do NOT kill liveness (we materialize lazy flags on the cold slow path),
        // EXCEPT taken Jcc edges expose all flags (live_after |= DFE_ALL_FLAGS).
        uint32_t live = DFE_ALL_FLAGS;
        for (int i = static_cast<int>(compiled_count_) - 1; i >= 0; --i) {
            const uint16_t raw = ops_[i].raw_opcode;
            if ((raw >= 0x70 && raw <= 0x7F) || (raw >= 0x180 && raw <= 0x18F)) {
                live |= DFE_ALL_FLAGS;
            }
            ops_[i].live_flags_after = live;
            live &= ~touches[i].flag_writes;
            live |= touches[i].flag_reads;
        }

        // Check whether any direct branch in the compiled prefix loops back to block->start_eip().
        const uint32_t block_start = block_->start_eip();
        has_self_loop_ = false;
        if (compiled_count_ == block_->inst_count()) {
            for (size_t i = 0; i < compiled_count_; ++i) {
                const uint16_t raw = ops_[i].raw_opcode;
                if (raw == 0xEB || raw == 0xE9 || (raw >= 0x70 && raw <= 0x7F) || (raw >= 0x180 && raw <= 0x18F)) {
                    if (ops_[i].pub.ext.control.target_eip == block_start) {
                        has_self_loop_ = true;
                        break;
                    }
                }
            }
        }
        out_has_self_loop = has_self_loop_;

        // Check if the loop kills all arithmetic flags before any flag read or memory op
        // (allows skipping even register-only flag materialization on the self-loop back-edge!).
        loop_kills_flags_early_ = false;
        if (has_self_loop_) {
            for (size_t i = 0; i < compiled_count_; ++i) {
                if (touches[i].reads_mem || touches[i].writes_mem || touches[i].flag_reads != 0) break;
                if ((touches[i].flag_writes & DFE_ALL_FLAGS) == DFE_ALL_FLAGS) {
                    loop_kills_flags_early_ = true;
                    break;
                }
            }
        }

        // Non-flat DS/SS check: if DS/SS/ES/CS are flat (0) at compile time, guard once at entry.
        assume_flat_ds_ss_ = (state_->ctx.seg_base[1] | state_->ctx.seg_base[2] | state_->ctx.seg_base[3] |
                              state_->ctx.seg_base[4]) == 0;
        uses_seg_ds_ss_ = uses_seg_ds_ss;
        touched_mask_ = touched_mask;
        written_mask_ = written_mask;
        dirty_mask_ = 0;
        xmm_touched_mask_ = xmm_touched_mask;
        xmm_written_mask_ = xmm_written_mask;
        xmm_dirty_mask_ = 0;

        // 2. Allocate Wasm locals
        gpr_local_.fill(0xFFFFFFFFu);
        for (uint8_t r = 0; r < 8; ++r) {
            if (touched_mask_ & (1u << r)) {
                gpr_local_[r] = w_.AddLocal(ValType::I32);
            }
        }
        xmm_local_.fill(0xFFFFFFFFu);
        for (uint8_t r = 0; r < 8; ++r) {
            if (xmm_touched_mask_ & (1u << r)) {
                xmm_local_[r] = w_.AddLocal(ValType::V128);
            }
        }

        loc_tmp0_ = w_.AddLocal(ValType::I32);
        loc_tmp1_ = w_.AddLocal(ValType::I32);
        loc_addr_ = w_.AddLocal(ValType::I32);
        loc_flg_a_ = w_.AddLocal(ValType::I32);
        loc_flg_b_ = w_.AddLocal(ValType::I32);
        loc_flg_r_ = w_.AddLocal(ValType::I32);
        loc_tmp64_ = w_.AddLocal(ValType::I64);
        if (has_reads) loc_rtlb_ = w_.AddLocal(ValType::I32);
        if (has_writes) loc_wtlb_ = w_.AddLocal(ValType::I32);
        if (has_self_loop_) loc_slow_ran_ = w_.AddLocal(ValType::I32);
        if (uses_f64_tmp || xmm_touched_mask_ != 0) {
            loc_f64_0_ = w_.AddLocal(ValType::F64);
            loc_f64_1_ = w_.AddLocal(ValType::F64);
        }
        if (uses_f32_tmp || xmm_touched_mask_ != 0) {
            loc_f32_0_ = w_.AddLocal(ValType::F32);
            loc_f32_1_ = w_.AddLocal(ValType::F32);
        }
        if (xmm_touched_mask_ != 0) {
            loc_v128_0_ = w_.AddLocal(ValType::V128);
        }

        // 3. Prologue: optional non-flat segment guard & default MXCSR guard
        if (uses_seg_ds_ss_ && assume_flat_ds_ss_) {
            // Check (seg_base[3] | seg_base[4]) != 0 once at block entry; fallback to FirstOp()->handler if non-flat.
            w_.LocalGet(kParamState);
            w_.I32Load(kOffSegBase + 3 * 4, 2);
            w_.LocalGet(kParamState);
            w_.I32Load(kOffSegBase + 4 * 4, 2);
            w_.I32Or();
            w_.If();
            EmitTailCallRawHandler(reinterpret_cast<uint32_t>(block_->FirstOp()),
                                   reinterpret_cast<uint32_t>(block_->FirstOp()->handler), 0xFFFFFFFFu);
            w_.End();
        }
        if (needs_default_mxcsr) {
            // Guard default MXCSR control bits (rounding=nearest, DAZ=0, exceptions masked: 0x1F80).
            w_.LocalGet(kParamState);
            w_.I32Load(kOffMxcsr, 2);
            w_.I32Const(0x7FC0);
            w_.I32And();
            w_.I32Const(0x1F80);
            w_.I32Ne();
            w_.If();
            EmitTailCallRawHandler(reinterpret_cast<uint32_t>(block_->FirstOp()),
                                   reinterpret_cast<uint32_t>(block_->FirstOp()->handler), 0xFFFFFFFFu);
            w_.End();
        }

        // Optional dynamic entry counter
        if (g_rt.stats_enabled) {
            EmitIncU64(&g_rt.dyn.jit_block_entries, 1);
        }

        // Precompute SoftTlb base pointers once at entry
        if (has_reads) {
            w_.LocalGet(kParamState);
            w_.I32ConstU(kOffReadTlb);
            w_.I32Add();
            w_.LocalSet(loc_rtlb_);
        }
        if (has_writes) {
            w_.LocalGet(kParamState);
            w_.I32ConstU(kOffWriteTlb);
            w_.I32Add();
            w_.LocalSet(loc_wtlb_);
        }

        // Load touched GPRs and XMMs into Wasm locals
        EmitLoadTouchedGprs();
        EmitLoadTouchedXmms();

        // Open self-loop header if this block loops back to start_eip
        if (has_self_loop_) {
            w_.Loop();
            loop_depth_ = 0;  // depth 0 targets this loop
            // Across loop iterations, any register written anywhere in the block can be dirty.
            dirty_mask_ = written_mask_;
            xmm_dirty_mask_ = xmm_written_mask_;
        }

        // 4. Emit instructions [0 .. compiled_count_)
        bool block_terminated = false;
        for (size_t i = 0; i < compiled_count_; ++i) {
            if (!EmitInstruction(i, block_terminated)) {
                return false;
            }
            if (block_terminated) break;
        }

        // 5. Epilogue (fallthrough to Sentinel OR cold fallback to uncompiled suffix)
        if (!block_terminated) {
            if (compiled_count_ == block_->inst_count()) {
                // Full block completed: chain through Sentinel (LinkSlot = 0)
                const DecodedOp* sentinel = block_->Sentinel();
                EmitBlockExitConst(block_->sentinel_eip, sentinel, 0, 0, compiled_count_);
            } else {
                // Prefix compiled, tail-call uncompiled suffix at LogicalOp(compiled_count_)
                if (g_rt.stats_enabled) {
                    EmitIncU64(&g_rt.dyn.jit_insts_executed, compiled_count_);
                    EmitIncU64(&g_rt.dyn.jit_fallback_exits, 1);
                }
                EmitCommitDirtyGprs();
                EmitCommitDirtyXmms();
                MaterializeFlags(flags_);
                const DecodedOp* next_op = block_->LogicalOp(compiled_count_);
                w_.LocalGet(kParamState);
                w_.I32ConstU(reinterpret_cast<uint32_t>(next_op));
                w_.LocalGet(kParamLimit);
                w_.LocalGet(kParamUtlbTags);
                w_.LocalGet(kParamUtlbAddend);
                w_.I32Const(-1);
                w_.LocalGet(kParamFlags);
                w_.I32ConstU(reinterpret_cast<uint32_t>(next_op));
                w_.I32Load(0, 2);  // next_op->handler
                w_.ReturnCallIndirect(0, 0);
            }
        }

        if (has_self_loop_) {
            w_.End();  // end loop
            w_.Unreachable();
        }

        std::string debug_name;
        if (g_rt.debug_names) {
            char buf[32];
            std::snprintf(buf, sizeof(buf), "blk_%08x", block_->start_eip());
            debug_name = buf;
        }
        out_wasm = w_.BuildModule(debug_name);
        return true;
    }

private:
    // Depth tracking for structured control flow inside self-loop
    void PushBlock(ValType bt = ValType::Void) {
        w_.Block(bt);
        if (has_self_loop_) loop_depth_++;
    }
    void PushIf(ValType bt = ValType::Void) {
        w_.If(bt);
        if (has_self_loop_) loop_depth_++;
    }
    void PopStructured() {
        w_.End();
        if (has_self_loop_) loop_depth_--;
    }

    // ------------------------------------------------------------------------
    // Stats Helpers
    // ------------------------------------------------------------------------

    void EmitIncU64(const uint64_t* counter_ptr, uint64_t delta) {
        const uint32_t addr = reinterpret_cast<uint32_t>(counter_ptr);
        w_.I32ConstU(addr);
        w_.I32ConstU(addr);
        w_.I64Load(0, 3);
        w_.I64Const(static_cast<int64_t>(delta));
        w_.I64Add();
        w_.I64Store(0, 3);
    }

    // ------------------------------------------------------------------------
    // GPR Local Management
    // ------------------------------------------------------------------------

    void EmitLoadTouchedGprs() {
        for (uint8_t r = 0; r < 8; ++r) {
            if (touched_mask_ & (1u << r)) {
                w_.LocalGet(kParamState);
                w_.I32Load(kOffRegs + r * 4, 2);
                w_.LocalSet(gpr_local_[r]);
            }
        }
    }

    void EmitCommitDirtyGprs() {
        const uint8_t mask = has_self_loop_ ? written_mask_ : dirty_mask_;
        for (uint8_t r = 0; r < 8; ++r) {
            if (mask & (1u << r)) {
                w_.LocalGet(kParamState);
                w_.LocalGet(gpr_local_[r]);
                w_.I32Store(kOffRegs + r * 4, 2);
            }
        }
    }

    void MarkDirty(uint8_t r) { dirty_mask_ |= static_cast<uint8_t>(1u << (r & 7)); }

    void EmitLoadTouchedXmms() {
        for (uint8_t r = 0; r < 8; ++r) {
            if (xmm_touched_mask_ & (1u << r)) {
                w_.LocalGet(kParamState);
                w_.V128Load(kOffXmm + r * 16, 4);
                w_.LocalSet(xmm_local_[r]);
            }
        }
    }

    void EmitCommitDirtyXmms() {
        const uint8_t mask = has_self_loop_ ? xmm_written_mask_ : xmm_dirty_mask_;
        for (uint8_t r = 0; r < 8; ++r) {
            if (mask & (1u << r)) {
                w_.LocalGet(kParamState);
                w_.LocalGet(xmm_local_[r]);
                w_.V128Store(kOffXmm + r * 16, 4);
            }
        }
    }

    void MarkXmmDirty(uint8_t r) { xmm_dirty_mask_ |= static_cast<uint8_t>(1u << (r & 7)); }

    void EmitReadReg(uint8_t reg, uint8_t width) {
        if (width == 8) {
            w_.LocalGet(gpr_local_[reg & 3]);
            if (reg >= 4) {
                w_.I32Const(8);
                w_.I32ShrU();
            }
            w_.I32Const(0xFF);
            w_.I32And();
        } else if (width == 16) {
            w_.LocalGet(gpr_local_[reg & 7]);
            w_.I32Const(0xFFFF);
            w_.I32And();
        } else {
            w_.LocalGet(gpr_local_[reg & 7]);
        }
    }

    // Expects value on top of Wasm stack (low `width` bits valid, high bits may be dirty).
    void EmitWriteRegFromStack(uint8_t reg, uint8_t width) {
        if (width == 32) {
            w_.LocalSet(gpr_local_[reg & 7]);
            MarkDirty(reg & 7);
        } else if (width == 16) {
            w_.I32Const(0xFFFF);
            w_.I32And();
            w_.LocalGet(gpr_local_[reg & 7]);
            w_.I32ConstU(0xFFFF0000u);
            w_.I32And();
            w_.I32Or();
            w_.LocalSet(gpr_local_[reg & 7]);
            MarkDirty(reg & 7);
        } else {
            const uint8_t parent = reg & 3;
            w_.I32Const(0xFF);
            w_.I32And();
            if (reg >= 4) {
                w_.I32Const(8);
                w_.I32Shl();
                w_.LocalGet(gpr_local_[parent]);
                w_.I32ConstU(0xFFFF00FFu);
                w_.I32And();
            } else {
                w_.LocalGet(gpr_local_[parent]);
                w_.I32ConstU(0xFFFFFF00u);
                w_.I32And();
            }
            w_.I32Or();
            w_.LocalSet(gpr_local_[parent]);
            MarkDirty(parent);
        }
    }

    // ------------------------------------------------------------------------
    // Effective & Linear Address Calculation
    // ------------------------------------------------------------------------

    void EmitComputeEA(const DecodedInstruction& d) {
        const uint32_t ea = d.ext.data.ea_desc;
        const uint8_t base_off = memdesc::BaseOffset(ea);
        const uint8_t idx_off = memdesc::IndexOffset(ea);
        const uint8_t scale = memdesc::Scale(ea);
        const uint32_t disp = d.ext.data.disp;

        bool has_term = false;
        if (base_off != kNoRegOffset) {
            w_.LocalGet(gpr_local_[base_off >> 2]);
            has_term = true;
        }
        if (idx_off != kNoRegOffset) {
            w_.LocalGet(gpr_local_[idx_off >> 2]);
            if (scale != 0) {
                w_.I32Const(scale);
                w_.I32Shl();
            }
            if (has_term) w_.I32Add();
            has_term = true;
        }
        if (disp != 0 || !has_term) {
            w_.I32ConstU(disp);
            if (has_term) w_.I32Add();
        }
        if (d.prefixes.flags.addrsize) {
            w_.I32Const(0xFFFF);
            w_.I32And();
        }
    }

    void EmitAddSegment(uint8_t resolved_seg) {
        if (resolved_seg >= 1 && resolved_seg <= 4 && assume_flat_ds_ss_) return;
        w_.LocalGet(kParamState);
        w_.I32Load(kOffSegBase + resolved_seg * 4, 2);
        w_.I32Add();
    }

    void EmitComputeLinearAddress(const DecodedInstruction& d) {
        EmitComputeEA(d);
        EmitAddSegment(memdesc::ResolvedSegment(d.ext.data.ea_desc));
    }

    // ------------------------------------------------------------------------
    // Inline SoftTlb Fast-Path + Slow-Path Fallback (Exact Fault/SMC Precision)
    // ------------------------------------------------------------------------

    // Reads `width` bits (8/16/32) from guest address in `addr_local`, leaves zero-extended i32 on stack.
    // On TLB miss/cross-page/mem_hook, commits GPRs+flags, calls FiberWasmJitReadSlow;
    // on fault returns `instr_limit` immediately; on success reloads GPRs+flags and yields value!
    void EmitGuestRead(uint32_t addr_local, uint8_t width, uint32_t inst_eip) {
        const uint32_t size = width >> 3;

        // Result block (produces i32 value)
        PushBlock(ValType::I32);

        // Compute TLB entry pointer: loc_tmp0_ = loc_rtlb_ + ((addr >> 8) & kTlbIdxMaskShifted)
        w_.LocalGet(loc_rtlb_);
        w_.LocalGet(addr_local);
        w_.I32Const(8);
        w_.I32ShrU();
        w_.I32ConstU(kTlbIdxMaskShifted);
        w_.I32And();
        w_.I32Add();
        w_.LocalTee(loc_tmp0_);

        // Check tag match (+ page-cross if size > 1)
        w_.I32Load(0, 2);  // entry.ReadTag()
        w_.LocalGet(addr_local);
        w_.I32ConstU(~0xFFFu);
        w_.I32And();
        w_.I32Xor();
        if (size > 1) {
            w_.LocalGet(addr_local);
            w_.LocalGet(addr_local);
            w_.I32Const(static_cast<int32_t>(size - 1));
            w_.I32Add();
            w_.I32Xor();
            w_.I32ConstU(~0xFFFu);
            w_.I32And();
            w_.I32Or();
        }
        w_.I32Eqz();
        PushIf();
        // Fast path: host = entry.addend + addr
        w_.LocalGet(loc_tmp0_);
        w_.I32Load(8, 2);
        w_.LocalGet(addr_local);
        w_.I32Add();
        if (size == 1)
            w_.I32Load8U(0);
        else if (size == 2)
            w_.I32Load16U(0, 0);
        else
            w_.I32Load(0, 0);
        w_.Br(1);  // break out of ValType::I32 block with value!
        PopStructured();

        // Slow path: commit architectural state BEFORE calling slow helper
        if (g_rt.stats_enabled) {
            EmitIncU64(&g_rt.dyn.jit_slow_reads, 1);
        }
        EmitCommitDirtyGprs();
        EmitCommitDirtyXmms();
        LazyFlagState saved_flags = flags_;
        MaterializeFlags(saved_flags);
        w_.LocalGet(kParamState);
        w_.LocalGet(kParamFlags);
        w_.I64Store(kOffFlagsState, 3);
        if (has_self_loop_) {
            w_.I32Const(1);
            w_.LocalSet(loc_slow_ran_);
        }
        // Invalidate micro-TLB params so downstream interpreter cannot see stale L1
        w_.I64Const(-1);
        w_.LocalSet(kParamUtlbTags);
        w_.I64Const(0);
        w_.LocalSet(kParamUtlbAddend);

        // Call FiberWasmJitReadSlow(state, inst_eip, addr, size) -> i64 ((ok << 32) | val)
        w_.LocalGet(kParamState);
        w_.I32ConstU(inst_eip);
        w_.LocalGet(addr_local);
        w_.I32Const(static_cast<int32_t>(size));
        w_.Call(kFuncReadSlow);
        w_.LocalTee(loc_tmp64_);
        w_.I64Const(32);
        w_.I64ShrU();
        w_.I64Eqz();
        PushIf();
        // Fault / yield / stop: state->ctx.eip, regs, xmm, flags_state already committed!
        w_.LocalGet(kParamLimit);
        w_.Return();
        PopStructured();

        // Reload GPRs, XMMs & flags_state in case fault callback modified architectural state
        EmitLoadTouchedGprs();
        EmitLoadTouchedXmms();
        w_.LocalGet(kParamState);
        w_.I64Load(kOffFlagsState, 3);
        w_.LocalSet(kParamFlags);
        w_.LocalGet(loc_tmp64_);
        w_.I32WrapI64();

        PopStructured();  // end ValType::I32 block
        // Note: on fast path, flags_ is still valid; on slow path, flags_ was materialized into kParamFlags,
        // and flg_a/flg_b/flg_r locals are still intact so re-materializing or reading pending flags matches!
    }

    // Writes `width` bits from `val_local` to guest address in `addr_local`.
    // Must be called BEFORE updating GPRs/flags for this instruction so SMC/fault exits are exact!
    void EmitGuestWrite(uint32_t addr_local, uint32_t val_local, uint8_t width, uint32_t inst_eip) {
        const uint32_t size = width >> 3;

        PushBlock(ValType::Void);

        // Compute write_tlb entry pointer
        w_.LocalGet(loc_wtlb_);
        w_.LocalGet(addr_local);
        w_.I32Const(8);
        w_.I32ShrU();
        w_.I32ConstU(kTlbIdxMaskShifted);
        w_.I32And();
        w_.I32Add();
        w_.LocalTee(loc_tmp0_);

        // Check entry.WriteTag() (offset 4) + page-cross
        w_.I32Load(4, 2);
        w_.LocalGet(addr_local);
        w_.I32ConstU(~0xFFFu);
        w_.I32And();
        w_.I32Xor();
        if (size > 1) {
            w_.LocalGet(addr_local);
            w_.LocalGet(addr_local);
            w_.I32Const(static_cast<int32_t>(size - 1));
            w_.I32Add();
            w_.I32Xor();
            w_.I32ConstU(~0xFFFu);
            w_.I32And();
            w_.I32Or();
        }
        w_.I32Eqz();
        PushIf();
        // Fast path: host = entry.addend + addr
        w_.LocalGet(loc_tmp0_);
        w_.I32Load(8, 2);
        w_.LocalGet(addr_local);
        w_.I32Add();
        w_.LocalGet(val_local);
        if (size == 1)
            w_.I32Store8(0);
        else if (size == 2)
            w_.I32Store16(0, 0);
        else
            w_.I32Store(0, 0);
        w_.Br(1);  // done!
        PopStructured();

        // Slow path: commit pre-instruction architectural state
        if (g_rt.stats_enabled) {
            EmitIncU64(&g_rt.dyn.jit_slow_writes, 1);
        }
        EmitCommitDirtyGprs();
        EmitCommitDirtyXmms();
        LazyFlagState saved_flags = flags_;
        MaterializeFlags(saved_flags);
        w_.LocalGet(kParamState);
        w_.LocalGet(kParamFlags);
        w_.I64Store(kOffFlagsState, 3);
        if (has_self_loop_) {
            w_.I32Const(1);
            w_.LocalSet(loc_slow_ran_);
        }
        w_.I64Const(-1);
        w_.LocalSet(kParamUtlbTags);
        w_.I64Const(0);
        w_.LocalSet(kParamUtlbAddend);

        w_.LocalGet(kParamState);
        w_.I32ConstU(inst_eip);
        w_.LocalGet(addr_local);
        w_.LocalGet(val_local);
        w_.I32Const(static_cast<int32_t>(size));
        w_.Call(kFuncWriteSlow);
        w_.I32Eqz();
        PushIf();
        // Fault / SMC / yield: return instr_limit with exact pre-instruction state!
        w_.LocalGet(kParamLimit);
        w_.Return();
        PopStructured();

        // Reload GPRs, XMMs & flags_state in case fault callback modified state
        EmitLoadTouchedGprs();
        EmitLoadTouchedXmms();
        w_.LocalGet(kParamState);
        w_.I64Load(kOffFlagsState, 3);
        w_.LocalSet(kParamFlags);

        PopStructured();
    }

    // ------------------------------------------------------------------------
    // 4B/8B/16B FP & SIMD128 Inline SoftTlb Fast-Path + Slow-Path Helpers
    // ------------------------------------------------------------------------

    void EmitTlbProbeCheck(uint32_t tlb_base_local, uint32_t tag_offset, uint32_t addr_local, uint32_t size,
                           bool align16) {
        w_.LocalGet(tlb_base_local);
        w_.LocalGet(addr_local);
        w_.I32Const(8);
        w_.I32ShrU();
        w_.I32ConstU(kTlbIdxMaskShifted);
        w_.I32And();
        w_.I32Add();
        w_.LocalTee(loc_tmp0_);

        w_.I32Load(tag_offset, 2);
        w_.LocalGet(addr_local);
        w_.I32ConstU(~0xFFFu);
        w_.I32And();
        w_.I32Xor();
        if (align16) {
            // (addr & 15) == 0 guarantees both 16-byte alignment AND no 4 KiB page cross.
            w_.LocalGet(addr_local);
            w_.I32Const(15);
            w_.I32And();
            w_.I32Or();
        } else if (size > 1) {
            w_.LocalGet(addr_local);
            w_.LocalGet(addr_local);
            w_.I32Const(static_cast<int32_t>(size - 1));
            w_.I32Add();
            w_.I32Xor();
            w_.I32ConstU(~0xFFFu);
            w_.I32And();
            w_.I32Or();
        }
        w_.I32Eqz();
    }

    void EmitFastHostAddr(uint32_t addr_local) {
        w_.LocalGet(loc_tmp0_);
        w_.I32Load(8, 2);
        w_.LocalGet(addr_local);
        w_.I32Add();
    }

    void EmitSlowPathPrepare(bool is_write) {
        if (g_rt.stats_enabled) {
            EmitIncU64(is_write ? &g_rt.dyn.jit_slow_writes : &g_rt.dyn.jit_slow_reads, 1);
        }
        EmitCommitDirtyGprs();
        EmitCommitDirtyXmms();
        LazyFlagState saved_flags = flags_;
        MaterializeFlags(saved_flags);
        w_.LocalGet(kParamState);
        w_.LocalGet(kParamFlags);
        w_.I64Store(kOffFlagsState, 3);
        if (has_self_loop_) {
            w_.I32Const(1);
            w_.LocalSet(loc_slow_ran_);
        }
        w_.I64Const(-1);
        w_.LocalSet(kParamUtlbTags);
        w_.I64Const(0);
        w_.LocalSet(kParamUtlbAddend);
    }

    void EmitSlowPathReload() {
        EmitLoadTouchedGprs();
        EmitLoadTouchedXmms();
        w_.LocalGet(kParamState);
        w_.I64Load(kOffFlagsState, 3);
        w_.LocalSet(kParamFlags);
    }

    void EmitSlowReadCallAndCheck(uint32_t addr_local, uint32_t size_arg, uint32_t inst_eip) {
        w_.LocalGet(kParamState);
        w_.I32ConstU(inst_eip);
        w_.LocalGet(addr_local);
        w_.I32Const(static_cast<int32_t>(size_arg));
        w_.Call(kFuncReadSlow);
        w_.LocalTee(loc_tmp64_);
        w_.I64Const(32);
        w_.I64ShrU();
        w_.I64Eqz();
        PushIf();
        w_.LocalGet(kParamLimit);
        w_.Return();
        PopStructured();
        EmitSlowPathReload();
    }

    void EmitGuestReadF64(uint32_t addr_local, uint32_t inst_eip) {
        PushBlock(ValType::F64);
        EmitTlbProbeCheck(loc_rtlb_, 0, addr_local, 8, false);
        PushIf();
        EmitFastHostAddr(addr_local);
        w_.F64Load(0, 0);
        w_.Br(1);
        PopStructured();

        EmitSlowPathPrepare(false);
        EmitSlowReadCallAndCheck(addr_local, 8, inst_eip);
        w_.LocalGet(kParamState);
        w_.F64Load(kOffWasmJitScratch, 3);
        PopStructured();
    }

    void EmitGuestReadF32(uint32_t addr_local, uint32_t inst_eip) {
        PushBlock(ValType::F32);
        EmitTlbProbeCheck(loc_rtlb_, 0, addr_local, 4, false);
        PushIf();
        EmitFastHostAddr(addr_local);
        w_.F32Load(0, 0);
        w_.Br(1);
        PopStructured();

        EmitSlowPathPrepare(false);
        EmitSlowReadCallAndCheck(addr_local, 4, inst_eip);
        w_.LocalGet(loc_tmp64_);
        w_.I32WrapI64();
        w_.F32ReinterpretI32();
        PopStructured();
    }

    void EmitGuestReadV128(uint32_t addr_local, bool align16, uint32_t inst_eip) {
        PushBlock(ValType::V128);
        EmitTlbProbeCheck(loc_rtlb_, 0, addr_local, 16, align16);
        PushIf();
        EmitFastHostAddr(addr_local);
        w_.V128Load(0, align16 ? 4 : 0);
        w_.Br(1);
        PopStructured();

        EmitSlowPathPrepare(false);
        EmitSlowReadCallAndCheck(addr_local, align16 ? (16u | 0x80u) : 16u, inst_eip);
        w_.LocalGet(kParamState);
        w_.V128Load(kOffWasmJitScratch, 4);
        PopStructured();
    }

    void EmitGuestReadV128Load64Zero(uint32_t addr_local, uint32_t inst_eip) {
        PushBlock(ValType::V128);
        EmitTlbProbeCheck(loc_rtlb_, 0, addr_local, 8, false);
        PushIf();
        EmitFastHostAddr(addr_local);
        w_.V128Load64Zero(0, 0);
        w_.Br(1);
        PopStructured();

        EmitSlowPathPrepare(false);
        EmitSlowReadCallAndCheck(addr_local, 8, inst_eip);
        w_.LocalGet(kParamState);
        w_.V128Load64Zero(kOffWasmJitScratch, 3);
        PopStructured();
    }

    void EmitGuestReadV128Load64Splat(uint32_t addr_local, uint32_t inst_eip) {
        PushBlock(ValType::V128);
        EmitTlbProbeCheck(loc_rtlb_, 0, addr_local, 8, false);
        PushIf();
        EmitFastHostAddr(addr_local);
        w_.V128Load64Splat(0, 0);
        w_.Br(1);
        PopStructured();

        EmitSlowPathPrepare(false);
        EmitSlowReadCallAndCheck(addr_local, 8, inst_eip);
        w_.LocalGet(kParamState);
        w_.V128Load64Splat(kOffWasmJitScratch, 3);
        PopStructured();
    }

    void EmitGuestReadV128Load64Lane(uint32_t addr_local, uint32_t dst_xmm_local, uint8_t lane, uint32_t inst_eip) {
        PushBlock(ValType::V128);
        EmitTlbProbeCheck(loc_rtlb_, 0, addr_local, 8, false);
        PushIf();
        EmitFastHostAddr(addr_local);
        w_.LocalGet(dst_xmm_local);
        w_.V128Load64Lane(lane, 0, 0);
        w_.Br(1);
        PopStructured();

        EmitSlowPathPrepare(false);
        EmitSlowReadCallAndCheck(addr_local, 8, inst_eip);
        w_.LocalGet(kParamState);
        w_.LocalGet(dst_xmm_local);
        w_.V128Load64Lane(lane, kOffWasmJitScratch, 3);
        PopStructured();
    }

    void EmitGuestReadV128Load32Zero(uint32_t addr_local, uint32_t inst_eip) {
        PushBlock(ValType::V128);
        EmitTlbProbeCheck(loc_rtlb_, 0, addr_local, 4, false);
        PushIf();
        EmitFastHostAddr(addr_local);
        w_.V128Load32Zero(0, 0);
        w_.Br(1);
        PopStructured();

        EmitSlowPathPrepare(false);
        EmitSlowReadCallAndCheck(addr_local, 4, inst_eip);
        w_.V128ConstZero();
        w_.LocalGet(loc_tmp64_);
        w_.I32WrapI64();
        w_.I32x4ReplaceLane(0);
        PopStructured();
    }

    void EmitGuestWriteV128Lane64(uint32_t addr_local, uint32_t src_xmm_local, uint8_t lane, uint32_t inst_eip) {
        PushBlock(ValType::Void);
        EmitTlbProbeCheck(loc_wtlb_, 4, addr_local, 8, false);
        PushIf();
        EmitFastHostAddr(addr_local);
        w_.LocalGet(src_xmm_local);
        w_.V128Store64Lane(lane, 0, 0);
        w_.Br(1);
        PopStructured();

        EmitSlowPathPrepare(true);
        w_.LocalGet(kParamState);
        w_.LocalGet(src_xmm_local);
        w_.V128Store64Lane(lane, kOffWasmJitScratch, 3);
        w_.LocalGet(kParamState);
        w_.I32ConstU(inst_eip);
        w_.LocalGet(addr_local);
        w_.I32Const(0);
        w_.I32Const(8);
        w_.Call(kFuncWriteSlow);
        w_.I32Eqz();
        PushIf();
        w_.LocalGet(kParamLimit);
        w_.Return();
        PopStructured();
        EmitSlowPathReload();
        PopStructured();
    }

    void EmitGuestWriteV128Lane32(uint32_t addr_local, uint32_t src_xmm_local, uint8_t lane, uint32_t inst_eip) {
        PushBlock(ValType::Void);
        EmitTlbProbeCheck(loc_wtlb_, 4, addr_local, 4, false);
        PushIf();
        EmitFastHostAddr(addr_local);
        w_.LocalGet(src_xmm_local);
        w_.V128Store32Lane(lane, 0, 0);
        w_.Br(1);
        PopStructured();

        EmitSlowPathPrepare(true);
        w_.LocalGet(kParamState);
        w_.I32ConstU(inst_eip);
        w_.LocalGet(addr_local);
        w_.LocalGet(src_xmm_local);
        w_.I32x4ExtractLane(lane);
        w_.I32Const(4);
        w_.Call(kFuncWriteSlow);
        w_.I32Eqz();
        PushIf();
        w_.LocalGet(kParamLimit);
        w_.Return();
        PopStructured();
        EmitSlowPathReload();
        PopStructured();
    }

    void EmitGuestWriteV128(uint32_t addr_local, uint32_t src_xmm_local, bool align16, uint32_t inst_eip) {
        PushBlock(ValType::Void);
        EmitTlbProbeCheck(loc_wtlb_, 4, addr_local, 16, align16);
        PushIf();
        EmitFastHostAddr(addr_local);
        w_.LocalGet(src_xmm_local);
        w_.V128Store(0, align16 ? 4 : 0);
        w_.Br(1);
        PopStructured();

        EmitSlowPathPrepare(true);
        w_.LocalGet(kParamState);
        w_.LocalGet(src_xmm_local);
        w_.V128Store(kOffWasmJitScratch, 4);
        w_.LocalGet(kParamState);
        w_.I32ConstU(inst_eip);
        w_.LocalGet(addr_local);
        w_.I32Const(0);
        w_.I32Const(static_cast<int32_t>(align16 ? (16u | 0x80u) : 16u));
        w_.Call(kFuncWriteSlow);
        w_.I32Eqz();
        PushIf();
        w_.LocalGet(kParamLimit);
        w_.Return();
        PopStructured();
        EmitSlowPathReload();
        PopStructured();
    }

    // ------------------------------------------------------------------------
    // Sign/Zero Extension Helpers
    // ------------------------------------------------------------------------

    void EmitSignExtendOnStack(uint8_t width) {
        if (width == 8)
            w_.I32Extend8S();
        else if (width == 16)
            w_.I32Extend16S();
    }

    void EmitMaskOnStack(uint8_t width) {
        if (width == 8) {
            w_.I32Const(0xFF);
            w_.I32And();
        } else if (width == 16) {
            w_.I32Const(0xFFFF);
            w_.I32And();
        }
    }

    // ------------------------------------------------------------------------
    // Lazy Flags Materialization & Direct Condition Evaluation (Fusion!)
    // ------------------------------------------------------------------------

    // Updates kParamFlags from `flags_bits_on_stack` (i32) and `pf_byte_local` (low 8 bits),
    // clearing `clear_mask` (either ARITHMETIC_FLAGS_MASK = 0x8D5 or 0x8D4 for INC/DEC).
    void EmitPackFlagsCache(uint32_t clear_mask, uint32_t pf_local) {
        // Stack input: new_arith_flags (i32)
        // Compute new low32 = (i32.wrap_i64(kParamFlags) & ~clear_mask) | new_arith_flags
        w_.LocalGet(kParamFlags);
        w_.I32WrapI64();
        w_.I32ConstU(~clear_mask);
        w_.I32And();
        w_.I32Or();
        w_.I64ExtendI32U();

        // Preserve upper bits except pf_state (bits 40..47) and low32 (bits 0..31)
        w_.LocalGet(kParamFlags);
        w_.I64ConstU(~(0xFFFFFFFFull | FLAGS_CACHE_PF_STATE_MASK));
        w_.I64And();
        w_.I64Or();

        // Insert new pf_state = (pf_local & 0xFF) << 40
        w_.LocalGet(pf_local);
        w_.I32Const(0xFF);
        w_.I32And();
        w_.I64ExtendI32U();
        w_.I64Const(40);
        w_.I64Shl();
        w_.I64Or();
        w_.LocalSet(kParamFlags);
    }

    void EmitSfBitFromLocal(uint32_t r_local, uint8_t width) {
        w_.LocalGet(r_local);
        if (width == 16) {
            w_.I32Const(8);
            w_.I32ShrU();
        } else if (width == 32) {
            w_.I32Const(24);
            w_.I32ShrU();
        }
        w_.I32Const(SF_MASK);
        w_.I32And();
    }

    void EmitOfBitShift(uint8_t width) {
        // Input on stack: bit (width-1) holds OF bit; shift to bit 11 (OF_MASK = 0x800).
        if (width == 32) {
            w_.I32Const(20);
            w_.I32ShrU();
        } else if (width == 16) {
            w_.I32Const(4);
            w_.I32ShrU();
        } else {
            w_.I32Const(4);
            w_.I32Shl();
        }
        w_.I32Const(OF_MASK);
        w_.I32And();
    }

    // Materialize `st` into `kParamFlags` without mutating `flags_` (caller updates `flags_` if desired).
    void MaterializeFlags(const LazyFlagState& st) {
        if (st.kind == LazyFlagKind::Clean) return;

        if (st.kind == LazyFlagKind::Logic) {
            // ZF | SF, CF=0, OF=0, AF=0
            w_.I32Const(ZF_MASK);
            w_.I32Const(0);
            w_.LocalGet(loc_flg_r_);
            w_.I32Eqz();
            w_.Select();
            EmitSfBitFromLocal(loc_flg_r_, st.width);
            w_.I32Or();
            EmitPackFlagsCache(ARITHMETIC_FLAGS_MASK, loc_flg_r_);
            return;
        }

        if (st.kind == LazyFlagKind::Sub || st.kind == LazyFlagKind::Add) {
            // ZF
            w_.I32Const(ZF_MASK);
            w_.I32Const(0);
            w_.LocalGet(loc_flg_r_);
            w_.I32Eqz();
            w_.Select();

            // SF
            EmitSfBitFromLocal(loc_flg_r_, st.width);
            w_.I32Or();

            // CF: Sub -> a < b; Add -> r < a
            if (st.kind == LazyFlagKind::Sub) {
                w_.LocalGet(loc_flg_a_);
                w_.LocalGet(loc_flg_b_);
                w_.I32LtU();
            } else {
                w_.LocalGet(loc_flg_r_);
                w_.LocalGet(loc_flg_a_);
                w_.I32LtU();
            }
            w_.I32Or();

            // OF: Sub -> (a ^ b) & (a ^ r); Add -> ~(a ^ b) & (a ^ r)
            w_.LocalGet(loc_flg_a_);
            w_.LocalGet(loc_flg_b_);
            w_.I32Xor();
            if (st.kind == LazyFlagKind::Add) {
                w_.I32Const(-1);
                w_.I32Xor();
            }
            w_.LocalGet(loc_flg_a_);
            w_.LocalGet(loc_flg_r_);
            w_.I32Xor();
            w_.I32And();
            EmitOfBitShift(st.width);
            w_.I32Or();

            // AF: (a ^ b ^ r) & 0x10
            w_.LocalGet(loc_flg_a_);
            w_.LocalGet(loc_flg_b_);
            w_.I32Xor();
            w_.LocalGet(loc_flg_r_);
            w_.I32Xor();
            w_.I32Const(AF_MASK);
            w_.I32And();
            w_.I32Or();

            EmitPackFlagsCache(ARITHMETIC_FLAGS_MASK, loc_flg_r_);
            return;
        }

        if (st.kind == LazyFlagKind::IncDec) {
            // ZF
            w_.I32Const(ZF_MASK);
            w_.I32Const(0);
            w_.LocalGet(loc_flg_r_);
            w_.I32Eqz();
            w_.Select();

            // SF
            EmitSfBitFromLocal(loc_flg_r_, st.width);
            w_.I32Or();

            // OF
            const uint32_t sign_bit = 1u << (st.width - 1);
            const uint32_t of_val = st.is_dec ? (sign_bit - 1u) : sign_bit;
            w_.I32Const(OF_MASK);
            w_.I32Const(0);
            w_.LocalGet(loc_flg_r_);
            w_.I32ConstU(of_val);
            w_.I32Eq();
            w_.Select();
            w_.I32Or();

            // AF: INC -> (r & 0xF) == 0; DEC -> (r & 0xF) == 0xF
            w_.I32Const(AF_MASK);
            w_.I32Const(0);
            w_.LocalGet(loc_flg_r_);
            w_.I32Const(0xF);
            w_.I32And();
            if (st.is_dec) {
                w_.I32Const(0xF);
                w_.I32Eq();
            } else {
                w_.I32Eqz();
            }
            w_.Select();
            w_.I32Or();

            // Preserve CF!
            EmitPackFlagsCache(ARITHMETIC_FLAGS_MASK & ~CF_MASK, loc_flg_r_);
            return;
        }
    }

    void FlushFlags() {
        if (flags_.kind != LazyFlagKind::Clean) {
            MaterializeFlags(flags_);
            flags_.kind = LazyFlagKind::Clean;
        }
    }

    // Evaluates x86 condition code `cond` (0..15) directly from lazy flags when possible!
    // Pushes 0 or 1 (i32) onto the Wasm stack.
    void EmitCondition(uint8_t cond) {
        const uint8_t base = cond & 0xEu;
        const bool invert = (cond & 1u) != 0;

        // Fast direct fusion for PendingSub (CMP / SUB / NEG)
        if (flags_.kind == LazyFlagKind::Sub) {
            const uint8_t w = flags_.width;
            switch (base) {
                case 4:  // E / NE
                    w_.LocalGet(loc_flg_a_);
                    w_.LocalGet(loc_flg_b_);
                    if (invert)
                        w_.I32Ne();
                    else
                        w_.I32Eq();
                    return;
                case 2:  // B / AE (unsigned < / >=)
                    w_.LocalGet(loc_flg_a_);
                    w_.LocalGet(loc_flg_b_);
                    if (invert)
                        w_.I32GeU();
                    else
                        w_.I32LtU();
                    return;
                case 6:  // BE / A (unsigned <= / >)
                    w_.LocalGet(loc_flg_a_);
                    w_.LocalGet(loc_flg_b_);
                    if (invert)
                        w_.I32GtU();
                    else
                        w_.I32LeU();
                    return;
                case 12:  // L / GE (signed < / >=)
                    w_.LocalGet(loc_flg_a_);
                    EmitSignExtendOnStack(w);
                    w_.LocalGet(loc_flg_b_);
                    EmitSignExtendOnStack(w);
                    if (invert)
                        w_.I32GeS();
                    else
                        w_.I32LtS();
                    return;
                case 14:  // LE / G (signed <= / >)
                    w_.LocalGet(loc_flg_a_);
                    EmitSignExtendOnStack(w);
                    w_.LocalGet(loc_flg_b_);
                    EmitSignExtendOnStack(w);
                    if (invert)
                        w_.I32GtS();
                    else
                        w_.I32LeS();
                    return;
                case 8:  // S / NS
                    w_.LocalGet(loc_flg_r_);
                    EmitSignExtendOnStack(w);
                    w_.I32Const(0);
                    if (invert)
                        w_.I32GeS();
                    else
                        w_.I32LtS();
                    return;
                default:
                    break;  // O/NO/P/NP: fall through to materialize
            }
        }

        // Fast direct fusion for PendingLogic (TEST / AND / OR / XOR)
        if (flags_.kind == LazyFlagKind::Logic) {
            const uint8_t w = flags_.width;
            switch (base) {
                case 0:  // O / NO (OF is always 0)
                case 2:  // B / AE (CF is always 0)
                    w_.I32Const(invert ? 1 : 0);
                    return;
                case 4:  // E / NE
                case 6:  // BE / A (CF=0, so BE == E)
                    w_.LocalGet(loc_flg_r_);
                    if (invert) {
                        w_.I32Const(0);
                        w_.I32Ne();
                    } else {
                        w_.I32Eqz();
                    }
                    return;
                case 8:   // S / NS
                case 12:  // L / GE (OF=0, so L == S)
                    w_.LocalGet(loc_flg_r_);
                    EmitSignExtendOnStack(w);
                    w_.I32Const(0);
                    if (invert)
                        w_.I32GeS();
                    else
                        w_.I32LtS();
                    return;
                case 14:  // LE / G (OF=0, so LE == (sext(r) <= 0))
                    w_.LocalGet(loc_flg_r_);
                    EmitSignExtendOnStack(w);
                    w_.I32Const(0);
                    if (invert)
                        w_.I32GtS();
                    else
                        w_.I32LeS();
                    return;
                case 10:  // P / NP
                    w_.LocalGet(loc_flg_r_);
                    w_.I32Const(0xFF);
                    w_.I32And();
                    w_.I32Popcnt();
                    w_.I32Const(1);
                    w_.I32And();
                    if (!invert) w_.I32Eqz();
                    return;
                default:
                    break;
            }
        }

        // Fast direct fusion for PendingAdd / PendingIncDec on E/NE/S/NS (and B/AE for Add)
        if (flags_.kind == LazyFlagKind::Add || flags_.kind == LazyFlagKind::IncDec) {
            const uint8_t w = flags_.width;
            if (base == 4) {  // E / NE
                w_.LocalGet(loc_flg_r_);
                if (invert) {
                    w_.I32Const(0);
                    w_.I32Ne();
                } else {
                    w_.I32Eqz();
                }
                return;
            }
            if (base == 8) {  // S / NS
                w_.LocalGet(loc_flg_r_);
                EmitSignExtendOnStack(w);
                w_.I32Const(0);
                if (invert)
                    w_.I32GeS();
                else
                    w_.I32LtS();
                return;
            }
            if (flags_.kind == LazyFlagKind::Add && base == 2) {  // B / AE
                w_.LocalGet(loc_flg_r_);
                w_.LocalGet(loc_flg_a_);
                if (invert)
                    w_.I32GeU();
                else
                    w_.I32LtU();
                return;
            }
        }

        // General case: materialize into kParamFlags and test bits directly
        FlushFlags();
        if (base == 10) {  // P / NP
            w_.LocalGet(kParamFlags);
            w_.I64Const(40);
            w_.I64ShrU();
            w_.I32WrapI64();
            w_.I32Const(0xFF);
            w_.I32And();
            w_.I32Popcnt();
            w_.I32Const(1);
            w_.I32And();
            if (!invert) w_.I32Eqz();
            return;
        }

        w_.LocalGet(kParamFlags);
        w_.I32WrapI64();
        switch (base) {
            case 0:  // O
                w_.I32Const(OF_MASK);
                w_.I32And();
                if (invert)
                    w_.I32Eqz();
                else {
                    w_.I32Const(0);
                    w_.I32Ne();
                }
                break;
            case 2:  // B (CF)
                w_.I32Const(CF_MASK);
                w_.I32And();
                if (invert) w_.I32Eqz();
                break;
            case 4:  // E (ZF)
                w_.I32Const(ZF_MASK);
                w_.I32And();
                if (invert)
                    w_.I32Eqz();
                else {
                    w_.I32Const(0);
                    w_.I32Ne();
                }
                break;
            case 6:  // BE (CF | ZF)
                w_.I32Const(CF_MASK | ZF_MASK);
                w_.I32And();
                if (invert)
                    w_.I32Eqz();
                else {
                    w_.I32Const(0);
                    w_.I32Ne();
                }
                break;
            case 8:  // S (SF)
                w_.I32Const(SF_MASK);
                w_.I32And();
                if (invert)
                    w_.I32Eqz();
                else {
                    w_.I32Const(0);
                    w_.I32Ne();
                }
                break;
            case 12:  // L (SF != OF)
                w_.LocalTee(loc_tmp0_);
                w_.I32Const(7);
                w_.I32ShrU();
                w_.LocalGet(loc_tmp0_);
                w_.I32Const(11);
                w_.I32ShrU();
                w_.I32Xor();
                w_.I32Const(1);
                w_.I32And();
                if (invert) w_.I32Eqz();
                break;
            case 14:  // LE (ZF || (SF != OF))
                w_.LocalTee(loc_tmp0_);
                w_.I32Const(7);
                w_.I32ShrU();
                w_.LocalGet(loc_tmp0_);
                w_.I32Const(11);
                w_.I32ShrU();
                w_.I32Xor();
                w_.LocalGet(loc_tmp0_);
                w_.I32Const(6);
                w_.I32ShrU();
                w_.I32Or();
                w_.I32Const(1);
                w_.I32And();
                if (invert) w_.I32Eqz();
                break;
            default:
                w_.Drop();
                w_.I32Const(0);
                break;
        }
    }

    // ------------------------------------------------------------------------
    // Block Exit, Self-Loop Back-Edge & Direct Chaining (TryChainBlock)
    // ------------------------------------------------------------------------

    void EmitTailCallRawHandler(uint32_t op_addr, uint32_t handler_idx, uint32_t branch_val) {
        w_.LocalGet(kParamState);
        w_.I32ConstU(op_addr);
        w_.LocalGet(kParamLimit);
        w_.LocalGet(kParamUtlbTags);
        w_.LocalGet(kParamUtlbAddend);
        w_.I32ConstU(branch_val);
        w_.LocalGet(kParamFlags);
        w_.I32ConstU(handler_idx);
        w_.ReturnCallIndirect(0, 0);
    }

    // Emit block exit for either a constant `target_eip` (is_const_target=true) or dynamic target in `target_local`.
    void EmitBlockExit(bool is_const_target, uint32_t const_target_eip, uint32_t target_local, const DecodedOp* exit_op,
                       uint32_t link_slot, uint32_t suffix_refund, uint32_t executed_insts) {
        // Fast self-loop back-edge!
        if (has_self_loop_ && is_const_target && const_target_eip == block_->start_eip()) {
            // Check budget after refunding suffix
            if (suffix_refund != 0) {
                w_.LocalGet(kParamLimit);
                w_.I64Const(static_cast<int64_t>(suffix_refund));
                w_.I64Add();
                w_.LocalSet(kParamLimit);
            }
            // If budget > 0 and no slow-path ran (so block could not have been invalidated), loop directly!
            w_.LocalGet(kParamLimit);
            w_.I64Const(0);
            w_.I64GtS();
            w_.LocalGet(loc_slow_ran_);
            w_.I32Eqz();
            w_.I32And();
            PushIf();
            if (g_rt.stats_enabled) {
                EmitIncU64(&g_rt.dyn.jit_self_loop_iters, 1);
                EmitIncU64(&g_rt.dyn.jit_insts_executed, executed_insts);
            }
            if (!loop_kills_flags_early_) {
                MaterializeFlags(flags_);
            }
            // Prepay next iteration's block->inst_count()
            w_.LocalGet(kParamLimit);
            w_.I64Const(static_cast<int64_t>(block_->inst_count()));
            w_.I64Sub();
            w_.LocalSet(kParamLimit);
            w_.Br(loop_depth_);  // Jump to self-loop header with ALL GPRs in Wasm locals!
            PopStructured();

            // Budget exhausted or slow_ran: fall through to standard exit (suffix already refunded!)
            suffix_refund = 0;
        }

        if (g_rt.stats_enabled) {
            EmitIncU64(&g_rt.dyn.jit_insts_executed, executed_insts);
        }

        EmitCommitDirtyGprs();
        EmitCommitDirtyXmms();
        MaterializeFlags(flags_);

        if (suffix_refund != 0) {
            w_.LocalGet(kParamLimit);
            w_.I64Const(static_cast<int64_t>(suffix_refund));
            w_.I64Add();
            w_.LocalSet(kParamLimit);
        }

        // Check budget <= 0
        w_.LocalGet(kParamLimit);
        w_.I64Const(0);
        w_.I64LeS();
        PushIf();
        w_.LocalGet(kParamState);
        if (is_const_target)
            w_.I32ConstU(const_target_eip);
        else
            w_.LocalGet(target_local);
        w_.I32Store(kOffEip, 2);
        w_.LocalGet(kParamState);
        w_.LocalGet(kParamFlags);
        w_.I64Store(kOffFlagsState, 3);
        w_.LocalGet(kParamLimit);
        w_.Return();
        PopStructured();

        // Direct TryChainBlock:
        // next_block = (exit_op + link_slot)->payload (low 32 bits)
        const uint32_t exit_op_addr = reinterpret_cast<uint32_t>(exit_op);
        w_.I32ConstU(exit_op_addr);
        w_.I32Load(link_slot * 16 + 8, 2);
        w_.LocalTee(loc_tmp0_);  // loc_tmp0_ = next_block

        // Load 8-byte BasicBlockChainPrefix: low32=start_eip, bit 40=valid, bits 32..39=inst_count, bits 48..63=hot
        w_.I64Load(0, 3);
        w_.LocalTee(loc_tmp64_);

        // Check valid (bit 40) AND start_eip == target_eip in one 64-bit mask+compare!
        w_.I64ConstU(0x00000100FFFFFFFFull);
        w_.I64And();
        if (is_const_target) {
            w_.I64ConstU(0x0000010000000000ull | static_cast<uint64_t>(const_target_eip));
        } else {
            w_.LocalGet(target_local);
            w_.I64ExtendI32U();
            w_.I64ConstU(0x0000010000000000ull);
            w_.I64Or();
        }
        w_.I64Eq();
        PushIf();
        {
            // Check llvm_hot_count (bits 48..63)
            w_.LocalGet(loc_tmp64_);
            w_.I64Const(48);
            w_.I64ShrU();
            w_.I32WrapI64();
            w_.LocalTee(loc_tmp1_);
            PushIf();
            {
                w_.LocalGet(loc_tmp1_);
                w_.I32Const(1);
                w_.I32GtU();
                PushIf();
                {
                    // hot_count > 1: decrement in place
                    w_.LocalGet(loc_tmp0_);
                    w_.LocalGet(loc_tmp1_);
                    w_.I32Const(1);
                    w_.I32Sub();
                    w_.I32Store16(6, 1);
                }
                w_.Else();
                {
                    // hot_count == 1: publish to state->llvm_hot_block if empty
                    w_.LocalGet(kParamState);
                    w_.I32Load(kOffHotBlock, 2);
                    w_.I32Eqz();
                    PushIf();
                    w_.LocalGet(loc_tmp0_);
                    w_.I32Const(0);
                    w_.I32Store16(6, 1);
                    w_.LocalGet(kParamState);
                    w_.LocalGet(loc_tmp0_);
                    w_.I32Store(kOffHotBlock, 2);
                    PopStructured();
                }
                PopStructured();
            }
            PopStructured();

            // state->last_block = next_block
            w_.LocalGet(kParamState);
            w_.LocalGet(loc_tmp0_);
            w_.I32Store(kOffLastBlock, 2);

            // instr_limit -= inst_count (bits 32..39)
            w_.LocalGet(kParamLimit);
            w_.LocalGet(loc_tmp64_);
            w_.I64Const(32);
            w_.I64ShrU();
            w_.I64Const(0xFF);
            w_.I64And();
            w_.I64Sub();
            w_.LocalSet(kParamLimit);

            // return_call_indirect next_block->entry(state, next_block + 32, instr_limit, utlb, -1, flags)
            w_.LocalGet(kParamState);
            w_.LocalGet(loc_tmp0_);
            w_.I32Const(32);
            w_.I32Add();
            w_.LocalGet(kParamLimit);
            w_.LocalGet(kParamUtlbTags);
            w_.LocalGet(kParamUtlbAddend);
            w_.I32Const(-1);
            w_.LocalGet(kParamFlags);
            w_.LocalGet(loc_tmp0_);
            w_.I32Load(8, 2);  // next_block->entry
            w_.ReturnCallIndirect(0, 0);
        }
        PopStructured();

        // Chain miss -> tail-call slow resolver!
        const uint32_t slow_resolver_idx = link_slot == 0
                                               ? reinterpret_cast<uint32_t>(&ResolveBranchTargetSlowLink)
                                               : reinterpret_cast<uint32_t>(&ResolveBranchTargetSlowDoubleSlot);
        w_.LocalGet(kParamState);
        w_.I32ConstU(exit_op_addr);
        w_.LocalGet(kParamLimit);
        w_.LocalGet(kParamUtlbTags);
        w_.LocalGet(kParamUtlbAddend);
        if (is_const_target)
            w_.I32ConstU(const_target_eip);
        else
            w_.LocalGet(target_local);
        w_.LocalGet(kParamFlags);
        w_.I32ConstU(slow_resolver_idx);
        w_.ReturnCallIndirect(0, 0);
    }

    void EmitBlockExitConst(uint32_t target_eip, const DecodedOp* exit_op, uint32_t link_slot, uint32_t suffix_refund,
                            uint32_t executed_insts) {
        EmitBlockExit(true, target_eip, 0, exit_op, link_slot, suffix_refund, executed_insts);
    }

    void EmitBlockExitDynamic(uint32_t target_local, const DecodedOp* exit_op, uint32_t link_slot,
                              uint32_t suffix_refund, uint32_t executed_insts) {
        EmitBlockExit(false, 0, target_local, exit_op, link_slot, suffix_refund, executed_insts);
    }

    // ------------------------------------------------------------------------
    // ALU Core (ADD, OR, ADC, SBB, AND, SUB, XOR, CMP)
    // ------------------------------------------------------------------------

    // Inputs: dest in `loc_flg_a_` (zero-extended to width), src in `loc_flg_b_` (zero-extended to width).
    // Output: result in `loc_flg_r_` (zero-extended to width).
    // If `defer_flags` is true, computes new flags into `out_lazy_flags` WITHOUT modifying `kParamFlags` yet
    // (used for memory RMW so a faulting write leaves old flags untouched!).
    // Otherwise updates `flags_` / `kParamFlags` immediately.
    bool EmitAluCompute(uint8_t alu_op, uint8_t width, uint32_t live_flags, bool is_mem_rmw) {
        const bool need_flags = (live_flags & DFE_ALL_FLAGS) != 0;
        switch (alu_op) {
            case 0:  // ADD
                w_.LocalGet(loc_flg_a_);
                w_.LocalGet(loc_flg_b_);
                w_.I32Add();
                EmitMaskOnStack(width);
                w_.LocalSet(loc_flg_r_);
                if (!is_mem_rmw && need_flags) {
                    flags_ = {LazyFlagKind::Add, width, false};
                }
                return true;
            case 5:  // SUB
            case 7:  // CMP
                w_.LocalGet(loc_flg_a_);
                w_.LocalGet(loc_flg_b_);
                w_.I32Sub();
                EmitMaskOnStack(width);
                w_.LocalSet(loc_flg_r_);
                if (!is_mem_rmw && need_flags) {
                    flags_ = {LazyFlagKind::Sub, width, false};
                }
                return true;
            case 1:  // OR
            case 4:  // AND
            case 6:  // XOR
                w_.LocalGet(loc_flg_a_);
                w_.LocalGet(loc_flg_b_);
                if (alu_op == 1)
                    w_.I32Or();
                else if (alu_op == 4)
                    w_.I32And();
                else
                    w_.I32Xor();
                EmitMaskOnStack(width);
                w_.LocalSet(loc_flg_r_);
                if (!is_mem_rmw && need_flags) {
                    flags_ = {LazyFlagKind::Logic, width, false};
                }
                return true;
            case 2:  // ADC
            case 3:  // SBB
            {
                // Read incoming CF into loc_tmp1_ (0 or 1)
                EmitCondition(2);  // CF
                w_.LocalSet(loc_tmp1_);

                // wres (i64) = i64(a) +/- i64(b) +/- i64(cf_in)
                w_.LocalGet(loc_flg_a_);
                w_.I64ExtendI32U();
                w_.LocalGet(loc_flg_b_);
                w_.I64ExtendI32U();
                if (alu_op == 2)
                    w_.I64Add();
                else
                    w_.I64Sub();
                w_.LocalGet(loc_tmp1_);
                w_.I64ExtendI32U();
                if (alu_op == 2)
                    w_.I64Add();
                else
                    w_.I64Sub();
                w_.LocalTee(loc_tmp64_);
                w_.I32WrapI64();
                EmitMaskOnStack(width);
                w_.LocalSet(loc_flg_r_);

                if (!is_mem_rmw && need_flags) {
                    EmitCommitAdcSbbFlags(alu_op == 3, width);
                }
                return true;
            }
            default:
                return false;
        }
    }

    // Commits ADC/SBB flags assuming a=loc_flg_a_, b=loc_flg_b_, r=loc_flg_r_, cf_in=loc_tmp1_, wres=loc_tmp64_.
    void EmitCommitAdcSbbFlags(bool is_sbb, uint8_t width) {
        // ZF
        w_.I32Const(ZF_MASK);
        w_.I32Const(0);
        w_.LocalGet(loc_flg_r_);
        w_.I32Eqz();
        w_.Select();

        // SF
        EmitSfBitFromLocal(loc_flg_r_, width);
        w_.I32Or();

        // CF
        if (!is_sbb) {
            w_.LocalGet(loc_tmp64_);
            w_.I64Const(width);
            w_.I64ShrU();
            w_.I64Const(0);
            w_.I64Ne();
        } else {
            w_.LocalGet(loc_flg_a_);
            w_.I64ExtendI32U();
            w_.LocalGet(loc_flg_b_);
            w_.I64ExtendI32U();
            w_.LocalGet(loc_tmp1_);
            w_.I64ExtendI32U();
            w_.I64Add();
            w_.I64LtU();
        }
        w_.I32Or();

        // OF
        w_.LocalGet(loc_flg_a_);
        w_.LocalGet(loc_flg_b_);
        w_.I32Xor();
        if (!is_sbb) {
            w_.I32Const(-1);
            w_.I32Xor();
        }
        w_.LocalGet(loc_flg_a_);
        w_.LocalGet(loc_flg_r_);
        w_.I32Xor();
        w_.I32And();
        EmitOfBitShift(width);
        w_.I32Or();

        // AF
        w_.LocalGet(loc_flg_a_);
        w_.LocalGet(loc_flg_b_);
        w_.I32Xor();
        w_.LocalGet(loc_flg_r_);
        w_.I32Xor();
        w_.I32Const(AF_MASK);
        w_.I32And();
        w_.I32Or();

        EmitPackFlagsCache(ARITHMETIC_FLAGS_MASK, loc_flg_r_);
        flags_.kind = LazyFlagKind::Clean;
    }

    // ------------------------------------------------------------------------
    // Per-Instruction Translation
    // ------------------------------------------------------------------------

    bool EmitInstruction(size_t idx, bool& terminated) {
        const JitOp& jop = ops_[idx];
        const uint16_t op = jop.raw_opcode;
        const uint8_t modrm = jop.dec.modrm;
        const uint8_t reg = (modrm >> 3) & 7;
        const uint8_t rm = modrm & 7;
        const bool is_mem = HasMem(&jop.dec);
        const uint8_t subop = reg;
        const uint32_t live = jop.live_flags_after;
        const uint32_t inst_eip = jop.start_eip;

        // 1. Standard ALU 0x00..0x3D
        if (op <= 0x3D && (op & 7) <= 5) {
            const uint8_t alu_op = static_cast<uint8_t>(op >> 3);
            const uint8_t form = static_cast<uint8_t>(op & 7);
            const uint8_t w = (form == 0 || form == 2 || form == 4) ? 8 : OpWidth(jop, false);
            const bool is_cmp = (alu_op == 7);

            // If this is a memory write (form 0/1, !is_cmp, is_mem), keep old flags until write succeeds!
            const bool is_mem_rmw = (form == 0 || form == 1) && !is_cmp && is_mem;
            const bool need_flags = (live & DFE_ALL_FLAGS) != 0;

            // Flush pending flags before overwriting loc_flg_a_/b_ if ADC/SBB reads CF, or if mem access may fault!
            if (flags_.kind != LazyFlagKind::Clean && (is_mem || alu_op == 2 || alu_op == 3)) {
                FlushFlags();
            }

            if (form == 0 || form == 1) {  // r/m, reg
                if (is_mem) {
                    EmitComputeLinearAddress(jop.dec);
                    w_.LocalSet(loc_addr_);
                    EmitGuestRead(loc_addr_, w, inst_eip);
                } else {
                    EmitReadReg(rm, w);
                }
                w_.LocalSet(loc_flg_a_);
                EmitReadReg(reg, w);
                w_.LocalSet(loc_flg_b_);

                EmitAluCompute(alu_op, w, live, is_mem_rmw);
                if (!is_cmp) {
                    if (is_mem) {
                        EmitGuestWrite(loc_addr_, loc_flg_r_, w, inst_eip);
                        // Now write succeeded: commit new flags!
                        if (need_flags) {
                            if (alu_op == 0)
                                flags_ = {LazyFlagKind::Add, w, false};
                            else if (alu_op == 5)
                                flags_ = {LazyFlagKind::Sub, w, false};
                            else if (alu_op == 1 || alu_op == 4 || alu_op == 6)
                                flags_ = {LazyFlagKind::Logic, w, false};
                            else if (alu_op == 2 || alu_op == 3)
                                EmitCommitAdcSbbFlags(alu_op == 3, w);
                        }
                    } else {
                        w_.LocalGet(loc_flg_r_);
                        EmitWriteRegFromStack(rm, w);
                    }
                }
            } else if (form == 2 || form == 3) {  // reg, r/m
                if (is_mem) {
                    EmitComputeLinearAddress(jop.dec);
                    w_.LocalSet(loc_addr_);
                    EmitGuestRead(loc_addr_, w, inst_eip);
                    w_.LocalSet(loc_flg_b_);
                    EmitReadReg(reg, w);
                    w_.LocalSet(loc_flg_a_);
                } else {
                    EmitReadReg(reg, w);
                    w_.LocalSet(loc_flg_a_);
                    EmitReadReg(rm, w);
                    w_.LocalSet(loc_flg_b_);
                }
                EmitAluCompute(alu_op, w, live, false);
                if (!is_cmp) {
                    w_.LocalGet(loc_flg_r_);
                    EmitWriteRegFromStack(reg, w);
                }
            } else {  // AL/AX/EAX, imm
                EmitReadReg(EAX, w);
                w_.LocalSet(loc_flg_a_);
                w_.I32ConstU(GetImm(&jop.dec) & WidthMask(w));
                w_.LocalSet(loc_flg_b_);
                EmitAluCompute(alu_op, w, live, false);
                if (!is_cmp) {
                    w_.LocalGet(loc_flg_r_);
                    EmitWriteRegFromStack(EAX, w);
                }
            }
            return true;
        }

        // 2. Group 1 (0x80..0x83): ALU r/m, imm
        if (op >= 0x80 && op <= 0x83) {
            const uint8_t w = (op == 0x80 || op == 0x82) ? 8 : OpWidth(jop, false);
            const bool is_cmp = (subop == 7);
            const bool is_mem_rmw = !is_cmp && is_mem;
            const bool need_flags = (live & DFE_ALL_FLAGS) != 0;
            uint32_t imm = GetImm(&jop.dec);
            if (op == 0x83) imm = static_cast<uint32_t>(static_cast<int32_t>(static_cast<int8_t>(imm)));
            imm &= WidthMask(w);

            if (flags_.kind != LazyFlagKind::Clean && (is_mem || subop == 2 || subop == 3)) FlushFlags();

            if (is_mem) {
                EmitComputeLinearAddress(jop.dec);
                w_.LocalSet(loc_addr_);
                EmitGuestRead(loc_addr_, w, inst_eip);
            } else {
                EmitReadReg(rm, w);
            }
            w_.LocalSet(loc_flg_a_);
            w_.I32ConstU(imm);
            w_.LocalSet(loc_flg_b_);

            EmitAluCompute(subop, w, live, is_mem_rmw);
            if (!is_cmp) {
                if (is_mem) {
                    EmitGuestWrite(loc_addr_, loc_flg_r_, w, inst_eip);
                    if (need_flags) {
                        if (subop == 0)
                            flags_ = {LazyFlagKind::Add, w, false};
                        else if (subop == 5)
                            flags_ = {LazyFlagKind::Sub, w, false};
                        else if (subop == 1 || subop == 4 || subop == 6)
                            flags_ = {LazyFlagKind::Logic, w, false};
                        else if (subop == 2 || subop == 3)
                            EmitCommitAdcSbbFlags(subop == 3, w);
                    }
                } else {
                    w_.LocalGet(loc_flg_r_);
                    EmitWriteRegFromStack(rm, w);
                }
            }
            return true;
        }

        // 3. INC / DEC r16/32 (0x40..0x4F)
        if (op >= 0x40 && op <= 0x4F) {
            const uint8_t w = OpWidth(jop, false);
            const bool is_dec = op >= 0x48;
            const uint8_t r = op & 7;
            const bool need_flags = (live & (DFE_ALL_FLAGS & ~CF_MASK)) != 0;
            if ((live & CF_MASK) != 0 && flags_.kind != LazyFlagKind::Clean && flags_.kind != LazyFlagKind::IncDec) {
                FlushFlags();
            }
            EmitReadReg(r, w);
            w_.I32Const(1);
            if (is_dec)
                w_.I32Sub();
            else
                w_.I32Add();
            EmitMaskOnStack(w);
            if (need_flags) {
                w_.LocalTee(loc_flg_r_);
                EmitWriteRegFromStack(r, w);
                flags_ = {LazyFlagKind::IncDec, w, is_dec};
            } else {
                EmitWriteRegFromStack(r, w);
            }
            return true;
        }

        // 4. PUSH r16/32 (0x50..0x57)
        if (op >= 0x50 && op <= 0x57) {
            const uint8_t w = OpWidth(jop, false);
            const uint32_t sz = w >> 3;
            EmitReadReg(op & 7, w);
            w_.LocalSet(loc_tmp1_);
            w_.LocalGet(gpr_local_[ESP]);
            w_.I32Const(static_cast<int32_t>(sz));
            w_.I32Sub();
            EmitAddSegment(3);  // SS
            w_.LocalSet(loc_addr_);
            EmitGuestWrite(loc_addr_, loc_tmp1_, w, inst_eip);
            w_.LocalGet(gpr_local_[ESP]);
            w_.I32Const(static_cast<int32_t>(sz));
            w_.I32Sub();
            w_.LocalSet(gpr_local_[ESP]);
            MarkDirty(ESP);
            return true;
        }

        // 5. POP r16/32 (0x58..0x5F) & POP r (0x8F /0 reg)
        if ((op >= 0x58 && op <= 0x5F) || op == 0x8F) {
            const uint8_t w = OpWidth(jop, false);
            const uint32_t sz = w >> 3;
            const uint8_t dst = op == 0x8F ? rm : static_cast<uint8_t>(op & 7);
            w_.LocalGet(gpr_local_[ESP]);
            EmitAddSegment(3);  // SS
            w_.LocalSet(loc_addr_);
            EmitGuestRead(loc_addr_, w, inst_eip);
            w_.LocalSet(loc_tmp1_);
            // Update ESP first, then write dst (so POP ESP overwrites ESP with popped value!)
            w_.LocalGet(gpr_local_[ESP]);
            w_.I32Const(static_cast<int32_t>(sz));
            w_.I32Add();
            w_.LocalSet(gpr_local_[ESP]);
            MarkDirty(ESP);
            w_.LocalGet(loc_tmp1_);
            EmitWriteRegFromStack(dst, w);
            return true;
        }

        // 6. PUSH imm (0x68, 0x6A)
        if (op == 0x68 || op == 0x6A) {
            const uint8_t w = OpWidth(jop, false);
            const uint32_t sz = w >> 3;
            uint32_t imm = GetImm(&jop.dec);
            if (op == 0x6A) imm = static_cast<uint32_t>(static_cast<int32_t>(static_cast<int8_t>(imm)));
            w_.I32ConstU(imm & WidthMask(w));
            w_.LocalSet(loc_tmp1_);
            w_.LocalGet(gpr_local_[ESP]);
            w_.I32Const(static_cast<int32_t>(sz));
            w_.I32Sub();
            EmitAddSegment(3);
            w_.LocalSet(loc_addr_);
            EmitGuestWrite(loc_addr_, loc_tmp1_, w, inst_eip);
            w_.LocalGet(gpr_local_[ESP]);
            w_.I32Const(static_cast<int32_t>(sz));
            w_.I32Sub();
            w_.LocalSet(gpr_local_[ESP]);
            MarkDirty(ESP);
            return true;
        }

        // 7. IMUL 2-op (0x1AF) and 3-op (0x69, 0x6B)
        if (op == 0x69 || op == 0x6B || op == 0x1AF) {
            const uint8_t w = OpWidth(jop, false);
            const bool need_flags = (live & (CF_MASK | OF_MASK)) != 0;
            if (is_mem && flags_.kind != LazyFlagKind::Clean && need_flags) FlushFlags();

            if (is_mem) {
                EmitComputeLinearAddress(jop.dec);
                w_.LocalSet(loc_addr_);
                EmitGuestRead(loc_addr_, w, inst_eip);
            } else {
                EmitReadReg(rm, w);
            }
            EmitSignExtendOnStack(w);
            w_.LocalSet(loc_tmp1_);  // signed rhs

            if (op == 0x1AF) {
                EmitReadReg(reg, w);
                EmitSignExtendOnStack(w);
            } else {
                uint32_t imm = GetImm(&jop.dec);
                if (op == 0x6B)
                    imm = static_cast<uint32_t>(static_cast<int32_t>(static_cast<int8_t>(imm)));
                else if (w == 16)
                    imm = static_cast<uint32_t>(static_cast<int32_t>(static_cast<int16_t>(imm)));
                w_.I32ConstU(imm);
            }

            if (!need_flags) {
                w_.LocalGet(loc_tmp1_);
                w_.I32Mul();
                EmitWriteRegFromStack(reg, w);
            } else {
                if ((live & ~(CF_MASK | OF_MASK)) != 0) FlushFlags();
                w_.I64ExtendI32S();
                w_.LocalGet(loc_tmp1_);
                w_.I64ExtendI32S();
                w_.I64Mul();
                w_.LocalTee(loc_tmp64_);
                w_.I32WrapI64();
                w_.LocalTee(loc_tmp1_);
                EmitWriteRegFromStack(reg, w);

                // Check overflow: product != sext(result)
                w_.LocalGet(loc_tmp64_);
                w_.LocalGet(loc_tmp1_);
                EmitSignExtendOnStack(w);
                w_.I64ExtendI32S();
                w_.I64Ne();
                // Set or clear CF|OF while preserving other flags & parity state
                w_.LocalSet(loc_tmp0_);
                w_.LocalGet(kParamFlags);
                w_.I64ConstU(~static_cast<uint64_t>(CF_MASK | OF_MASK));
                w_.I64And();
                w_.I64Const(CF_MASK | OF_MASK);
                w_.I64Const(0);
                w_.LocalGet(loc_tmp0_);
                w_.Select();
                w_.I64Or();
                w_.LocalSet(kParamFlags);
                flags_.kind = LazyFlagKind::Clean;
            }
            return true;
        }

        // 8. Jcc (0x70..0x7F, 0x180..0x18F)
        if ((op >= 0x70 && op <= 0x7F) || (op >= 0x180 && op <= 0x18F)) {
            const uint8_t cond = static_cast<uint8_t>(op & 0xF);
            const uint32_t target = jop.pub.ext.control.target_eip;
            const uint32_t suffix = static_cast<uint32_t>(block_->inst_count() - 1 - idx);
            EmitCondition(cond);
            const LazyFlagState saved_flags = flags_;
            const uint8_t saved_dirty = dirty_mask_;
            const uint8_t saved_xmm_dirty = xmm_dirty_mask_;
            PushIf();
            EmitBlockExitConst(target, jop.phys, 1, suffix, static_cast<uint32_t>(idx + 1));
            PopStructured();
            flags_ = saved_flags;
            dirty_mask_ = saved_dirty;
            xmm_dirty_mask_ = saved_xmm_dirty;
            return true;
        }

        // 9. TEST (0x84, 0x85, 0xA8, 0xA9)
        if (op == 0x84 || op == 0x85 || op == 0xA8 || op == 0xA9) {
            const uint8_t w = OpWidth(jop, op == 0x84 || op == 0xA8);
            const bool need_flags = (live & DFE_ALL_FLAGS) != 0;
            if (op == 0xA8 || op == 0xA9) {
                EmitReadReg(EAX, w);
                w_.I32ConstU(GetImm(&jop.dec) & WidthMask(w));
            } else {
                if (is_mem && flags_.kind != LazyFlagKind::Clean) FlushFlags();
                if (is_mem) {
                    EmitComputeLinearAddress(jop.dec);
                    w_.LocalSet(loc_addr_);
                    EmitGuestRead(loc_addr_, w, inst_eip);
                } else {
                    EmitReadReg(rm, w);
                }
                EmitReadReg(reg, w);
            }
            w_.I32And();
            if (need_flags) {
                w_.LocalSet(loc_flg_r_);
                flags_ = {LazyFlagKind::Logic, w, false};
            } else {
                w_.Drop();
            }
            return true;
        }

        // 10. XCHG r/m, r (0x86, 0x87)
        if (op == 0x86 || op == 0x87) {
            const uint8_t w = OpWidth(jop, op == 0x86);
            if (is_mem) {
                EmitComputeLinearAddress(jop.dec);
                w_.LocalSet(loc_addr_);
                EmitGuestRead(loc_addr_, w, inst_eip);
                w_.I64ExtendI32U();
                w_.LocalSet(loc_tmp64_);  // save mem_val without touching flg_*!
                EmitReadReg(reg, w);
                w_.LocalSet(loc_tmp1_);  // reg_val to store
                EmitGuestWrite(loc_addr_, loc_tmp1_, w, inst_eip);
                w_.LocalGet(loc_tmp64_);
                w_.I32WrapI64();
                EmitWriteRegFromStack(reg, w);
            } else {
                if (rm != reg) {
                    EmitReadReg(rm, w);
                    w_.LocalSet(loc_tmp1_);
                    EmitReadReg(reg, w);
                    EmitWriteRegFromStack(rm, w);
                    w_.LocalGet(loc_tmp1_);
                    EmitWriteRegFromStack(reg, w);
                }
            }
            return true;
        }

        // 11. MOV r/m, r (0x88, 0x89) and MOV r, r/m (0x8A, 0x8B)
        if (op == 0x88 || op == 0x89) {
            const uint8_t w = OpWidth(jop, op == 0x88);
            if (is_mem) {
                EmitComputeLinearAddress(jop.dec);
                w_.LocalSet(loc_addr_);
                EmitReadReg(reg, w);
                w_.LocalSet(loc_tmp1_);
                EmitGuestWrite(loc_addr_, loc_tmp1_, w, inst_eip);
            } else {
                EmitReadReg(reg, w);
                EmitWriteRegFromStack(rm, w);
            }
            return true;
        }
        if (op == 0x8A || op == 0x8B) {
            const uint8_t w = OpWidth(jop, op == 0x8A);
            if (is_mem) {
                EmitComputeLinearAddress(jop.dec);
                w_.LocalSet(loc_addr_);
                EmitGuestRead(loc_addr_, w, inst_eip);
            } else {
                EmitReadReg(rm, w);
            }
            EmitWriteRegFromStack(reg, w);
            return true;
        }

        // 12. LEA (0x8D)
        if (op == 0x8D) {
            const uint8_t w = OpWidth(jop, false);
            EmitComputeEA(jop.dec);
            EmitWriteRegFromStack(reg, w);
            return true;
        }

        // 13. NOP (0x90, 0x11F) & XCHG EAX, r (0x91..0x97)
        if (op == 0x90 || op == 0x11F) {
            return true;
        }
        if (op >= 0x91 && op <= 0x97) {
            const uint8_t w = OpWidth(jop, false);
            const uint8_t r = op & 7;
            EmitReadReg(EAX, w);
            w_.LocalSet(loc_tmp1_);
            EmitReadReg(r, w);
            EmitWriteRegFromStack(EAX, w);
            w_.LocalGet(loc_tmp1_);
            EmitWriteRegFromStack(r, w);
            return true;
        }

        // 14. CBW/CWDE (0x98) and CWD/CDQ (0x99)
        if (op == 0x98) {
            if (jop.dec.prefixes.flags.opsize) {
                EmitReadReg(EAX, 8);
                w_.I32Extend8S();
                EmitWriteRegFromStack(EAX, 16);
            } else {
                EmitReadReg(EAX, 16);
                w_.I32Extend16S();
                EmitWriteRegFromStack(EAX, 32);
            }
            return true;
        }
        if (op == 0x99) {
            if (jop.dec.prefixes.flags.opsize) {
                EmitReadReg(EAX, 16);
                w_.I32Extend16S();
                w_.I32Const(31);
                w_.I32ShrS();
                EmitWriteRegFromStack(EDX, 16);
            } else {
                w_.LocalGet(gpr_local_[EAX]);
                w_.I32Const(31);
                w_.I32ShrS();
                w_.LocalSet(gpr_local_[EDX]);
                MarkDirty(EDX);
            }
            return true;
        }

        // 15. MOV AL/AX/EAX, moffs (0xA0..0xA3)
        if (op >= 0xA0 && op <= 0xA3) {
            const uint8_t w = OpWidth(jop, op == 0xA0 || op == 0xA2);
            const uint8_t seg = jop.dec.prefixes.flags.segment ? jop.dec.prefixes.flags.segment : 4;
            w_.I32ConstU(GetImm(&jop.dec));
            EmitAddSegment(seg);
            w_.LocalSet(loc_addr_);
            if (op == 0xA0 || op == 0xA1) {
                EmitGuestRead(loc_addr_, w, inst_eip);
                EmitWriteRegFromStack(EAX, w);
            } else {
                EmitReadReg(EAX, w);
                w_.LocalSet(loc_tmp1_);
                EmitGuestWrite(loc_addr_, loc_tmp1_, w, inst_eip);
            }
            return true;
        }

        // 16. MOV reg, imm (0xB0..0xBF)
        if (op >= 0xB0 && op <= 0xBF) {
            const uint8_t w = op < 0xB8 ? 8 : OpWidth(jop, false);
            w_.I32ConstU(GetImm(&jop.dec) & WidthMask(w));
            EmitWriteRegFromStack(op & 7, w);
            return true;
        }

        // 17. MOV r/m, imm (0xC6 /0, 0xC7 /0)
        if (op == 0xC6 || op == 0xC7) {
            const uint8_t w = OpWidth(jop, op == 0xC6);
            const uint32_t imm = GetImm(&jop.dec) & WidthMask(w);
            if (is_mem) {
                EmitComputeLinearAddress(jop.dec);
                w_.LocalSet(loc_addr_);
                w_.I32ConstU(imm);
                w_.LocalSet(loc_tmp1_);
                EmitGuestWrite(loc_addr_, loc_tmp1_, w, inst_eip);
            } else {
                w_.I32ConstU(imm);
                EmitWriteRegFromStack(rm, w);
            }
            return true;
        }

        // 18. Group 2 Shifts & Rotates (0xC0, 0xC1, 0xD0, 0xD1, 0xD2, 0xD3)
        if (op == 0xC0 || op == 0xC1 || op == 0xD0 || op == 0xD1 || op == 0xD2 || op == 0xD3) {
            return EmitShiftRotate(jop, op, subop, rm, is_mem, live, inst_eip);
        }

        // 19. LEAVE (0xC9)
        if (op == 0xC9) {
            const uint8_t w = OpWidth(jop, false);
            const uint32_t sz = w >> 3;
            w_.LocalGet(gpr_local_[EBP]);
            EmitAddSegment(3);
            w_.LocalSet(loc_addr_);
            EmitGuestRead(loc_addr_, w, inst_eip);
            w_.LocalSet(loc_tmp1_);
            w_.LocalGet(gpr_local_[EBP]);
            w_.I32Const(static_cast<int32_t>(sz));
            w_.I32Add();
            w_.LocalSet(gpr_local_[ESP]);
            MarkDirty(ESP);
            w_.LocalGet(loc_tmp1_);
            EmitWriteRegFromStack(EBP, w);
            return true;
        }

        // 20. RET (0xC3) and RET imm16 (0xC2)
        if (op == 0xC2 || op == 0xC3) {
            const uint8_t w = OpWidth(jop, false);
            const uint32_t sz = w >> 3;
            w_.LocalGet(gpr_local_[ESP]);
            EmitAddSegment(3);
            w_.LocalSet(loc_addr_);
            EmitGuestRead(loc_addr_, w, inst_eip);
            w_.LocalSet(loc_tmp1_);  // return target EIP
            const uint32_t esp_add = sz + (op == 0xC2 ? (GetImm(&jop.dec) & 0xFFFFu) : 0u);
            w_.LocalGet(gpr_local_[ESP]);
            w_.I32ConstU(esp_add);
            w_.I32Add();
            w_.LocalSet(gpr_local_[ESP]);
            MarkDirty(ESP);
            const uint32_t link_slot = (op == 0xC3) ? 0u : 1u;
            const uint32_t suffix = static_cast<uint32_t>(block_->inst_count() - 1 - idx);
            EmitBlockExitDynamic(loc_tmp1_, jop.phys, link_slot, suffix, static_cast<uint32_t>(idx + 1));
            terminated = true;
            return true;
        }

        // 21. CALL rel (0xE8)
        if (op == 0xE8) {
            const uint8_t w = OpWidth(jop, false);
            const uint32_t sz = w >> 3;
            w_.I32ConstU(block_->sentinel_eip & WidthMask(w));
            w_.LocalSet(loc_tmp1_);
            w_.LocalGet(gpr_local_[ESP]);
            w_.I32Const(static_cast<int32_t>(sz));
            w_.I32Sub();
            EmitAddSegment(3);
            w_.LocalSet(loc_addr_);
            EmitGuestWrite(loc_addr_, loc_tmp1_, w, inst_eip);
            w_.LocalGet(gpr_local_[ESP]);
            w_.I32Const(static_cast<int32_t>(sz));
            w_.I32Sub();
            w_.LocalSet(gpr_local_[ESP]);
            MarkDirty(ESP);
            const uint32_t target = jop.pub.ext.control.target_eip & WidthMask(w);
            const uint32_t suffix = static_cast<uint32_t>(block_->inst_count() - 1 - idx);
            EmitBlockExitConst(target, jop.phys, 1, suffix, static_cast<uint32_t>(idx + 1));
            terminated = true;
            return true;
        }

        // 22. JMP rel (0xE9, 0xEB)
        if (op == 0xE9 || op == 0xEB) {
            const uint32_t target = jop.pub.ext.control.target_eip;
            const uint32_t suffix = static_cast<uint32_t>(block_->inst_count() - 1 - idx);
            EmitBlockExitConst(target, jop.phys, 1, suffix, static_cast<uint32_t>(idx + 1));
            terminated = true;
            return true;
        }

        // 23. Group 3 (0xF6, 0xF7): TEST(0,1), NOT(2), NEG(3), MUL(4), IMUL(5)
        if (op == 0xF6 || op == 0xF7) {
            const uint8_t w = OpWidth(jop, op == 0xF6);
            const bool need_flags = (live & DFE_ALL_FLAGS) != 0;
            if (is_mem && flags_.kind != LazyFlagKind::Clean && subop != 2) FlushFlags();

            if (is_mem) {
                EmitComputeLinearAddress(jop.dec);
                w_.LocalSet(loc_addr_);
                EmitGuestRead(loc_addr_, w, inst_eip);
            } else {
                EmitReadReg(rm, w);
            }

            if (subop <= 1) {  // TEST r/m, imm
                w_.I32ConstU(GetImm(&jop.dec) & WidthMask(w));
                w_.I32And();
                if (need_flags) {
                    w_.LocalSet(loc_flg_r_);
                    flags_ = {LazyFlagKind::Logic, w, false};
                } else {
                    w_.Drop();
                }
            } else if (subop == 2) {  // NOT
                w_.I32Const(-1);
                w_.I32Xor();
                EmitMaskOnStack(w);
                if (is_mem) {
                    w_.LocalSet(loc_tmp1_);
                    EmitGuestWrite(loc_addr_, loc_tmp1_, w, inst_eip);
                } else {
                    EmitWriteRegFromStack(rm, w);
                }
            } else if (subop == 3) {  // NEG: 0 - val
                w_.LocalSet(loc_flg_b_);
                w_.I32Const(0);
                w_.LocalSet(loc_flg_a_);
                w_.I32Const(0);
                w_.LocalGet(loc_flg_b_);
                w_.I32Sub();
                EmitMaskOnStack(w);
                w_.LocalSet(loc_flg_r_);
                if (is_mem) {
                    EmitGuestWrite(loc_addr_, loc_flg_r_, w, inst_eip);
                } else {
                    w_.LocalGet(loc_flg_r_);
                    EmitWriteRegFromStack(rm, w);
                }
                if (need_flags) flags_ = {LazyFlagKind::Sub, w, false};
            } else {  // MUL (4) / IMUL (5)
                const bool is_signed = (subop == 5);
                w_.LocalSet(loc_tmp1_);  // rhs (zero-extended)
                const bool need_cf_of = (live & (CF_MASK | OF_MASK)) != 0;
                if (need_cf_of && (live & ~(CF_MASK | OF_MASK)) != 0) FlushFlags();

                if (w == 8) {
                    EmitReadReg(EAX, 8);
                    if (is_signed) {
                        w_.I32Extend8S();
                        w_.LocalGet(loc_tmp1_);
                        w_.I32Extend8S();
                    } else {
                        w_.LocalGet(loc_tmp1_);
                    }
                    w_.I32Mul();
                    w_.LocalTee(loc_tmp1_);
                    EmitWriteRegFromStack(EAX, 16);
                    if (need_cf_of) {
                        if (is_signed) {
                            w_.LocalGet(loc_tmp1_);
                            w_.I32Extend16S();
                            w_.LocalGet(loc_tmp1_);
                            w_.I32Extend8S();
                            w_.I32Ne();
                        } else {
                            w_.LocalGet(loc_tmp1_);
                            w_.I32Const(0xFF00);
                            w_.I32And();
                            w_.I32Const(0);
                            w_.I32Ne();
                        }
                        EmitSetCfOfFromStack();
                    }
                } else if (w == 16) {
                    EmitReadReg(EAX, 16);
                    if (is_signed) {
                        w_.I32Extend16S();
                        w_.LocalGet(loc_tmp1_);
                        w_.I32Extend16S();
                    } else {
                        w_.LocalGet(loc_tmp1_);
                    }
                    w_.I32Mul();
                    w_.LocalTee(loc_tmp1_);
                    EmitWriteRegFromStack(EAX, 16);
                    w_.LocalGet(loc_tmp1_);
                    w_.I32Const(16);
                    w_.I32ShrU();
                    EmitWriteRegFromStack(EDX, 16);
                    if (need_cf_of) {
                        if (is_signed) {
                            w_.LocalGet(loc_tmp1_);
                            w_.LocalGet(loc_tmp1_);
                            w_.I32Extend16S();
                            w_.I32Ne();
                        } else {
                            w_.LocalGet(loc_tmp1_);
                            w_.I32Const(16);
                            w_.I32ShrU();
                            w_.I32Const(0);
                            w_.I32Ne();
                        }
                        EmitSetCfOfFromStack();
                    }
                } else {
                    w_.LocalGet(gpr_local_[EAX]);
                    if (is_signed) {
                        w_.I64ExtendI32S();
                        w_.LocalGet(loc_tmp1_);
                        w_.I64ExtendI32S();
                    } else {
                        w_.I64ExtendI32U();
                        w_.LocalGet(loc_tmp1_);
                        w_.I64ExtendI32U();
                    }
                    w_.I64Mul();
                    w_.LocalTee(loc_tmp64_);
                    w_.I32WrapI64();
                    w_.LocalSet(gpr_local_[EAX]);
                    MarkDirty(EAX);
                    w_.LocalGet(loc_tmp64_);
                    w_.I64Const(32);
                    w_.I64ShrU();
                    w_.I32WrapI64();
                    w_.LocalSet(gpr_local_[EDX]);
                    MarkDirty(EDX);
                    if (need_cf_of) {
                        if (is_signed) {
                            w_.LocalGet(loc_tmp64_);
                            w_.LocalGet(gpr_local_[EAX]);
                            w_.I64ExtendI32S();
                            w_.I64Ne();
                        } else {
                            w_.LocalGet(gpr_local_[EDX]);
                            w_.I32Const(0);
                            w_.I32Ne();
                        }
                        EmitSetCfOfFromStack();
                    }
                }
            }
            return true;
        }

        // 24. Group 4 (0xFE): INC/DEC r/m8, and Group 5 (0xFF): INC(0), DEC(1), CALL(2), JMP(4), PUSH(6)
        if (op == 0xFE || op == 0xFF) {
            const uint8_t w = (op == 0xFE) ? 8 : OpWidth(jop, false);
            if (subop <= 1) {  // INC / DEC
                const bool is_dec = (subop == 1);
                const bool need_flags = (live & (DFE_ALL_FLAGS & ~CF_MASK)) != 0;
                if (is_mem && flags_.kind != LazyFlagKind::Clean) FlushFlags();
                if ((live & CF_MASK) != 0 && flags_.kind != LazyFlagKind::Clean &&
                    flags_.kind != LazyFlagKind::IncDec) {
                    FlushFlags();
                }

                if (is_mem) {
                    EmitComputeLinearAddress(jop.dec);
                    w_.LocalSet(loc_addr_);
                    EmitGuestRead(loc_addr_, w, inst_eip);
                } else {
                    EmitReadReg(rm, w);
                }
                w_.I32Const(1);
                if (is_dec)
                    w_.I32Sub();
                else
                    w_.I32Add();
                EmitMaskOnStack(w);
                if (is_mem) {
                    w_.LocalSet(loc_flg_r_);
                    EmitGuestWrite(loc_addr_, loc_flg_r_, w, inst_eip);
                    if (need_flags) flags_ = {LazyFlagKind::IncDec, w, is_dec};
                } else {
                    if (need_flags) {
                        w_.LocalTee(loc_flg_r_);
                        EmitWriteRegFromStack(rm, w);
                        flags_ = {LazyFlagKind::IncDec, w, is_dec};
                    } else {
                        EmitWriteRegFromStack(rm, w);
                    }
                }
                return true;
            }
            if (subop == 6) {  // PUSH Ev
                const uint32_t sz = w >> 3;
                if (is_mem) {
                    EmitComputeLinearAddress(jop.dec);
                    w_.LocalSet(loc_addr_);
                    EmitGuestRead(loc_addr_, w, inst_eip);
                } else {
                    EmitReadReg(rm, w);
                }
                w_.LocalSet(loc_tmp1_);
                w_.LocalGet(gpr_local_[ESP]);
                w_.I32Const(static_cast<int32_t>(sz));
                w_.I32Sub();
                EmitAddSegment(3);
                w_.LocalSet(loc_addr_);
                EmitGuestWrite(loc_addr_, loc_tmp1_, w, inst_eip);
                w_.LocalGet(gpr_local_[ESP]);
                w_.I32Const(static_cast<int32_t>(sz));
                w_.I32Sub();
                w_.LocalSet(gpr_local_[ESP]);
                MarkDirty(ESP);
                return true;
            }
            if (subop == 2 || subop == 4) {  // CALL Ev / JMP Ev (indirect!)
                const uint32_t sz = w >> 3;
                if (is_mem) {
                    EmitComputeLinearAddress(jop.dec);
                    w_.LocalSet(loc_addr_);
                    EmitGuestRead(loc_addr_, w, inst_eip);
                } else {
                    EmitReadReg(rm, w);
                }
                if (subop == 2) {
                    // Save target in loc_tmp64_ while pushing return EIP
                    w_.I64ExtendI32U();
                    w_.LocalSet(loc_tmp64_);
                    w_.I32ConstU(block_->sentinel_eip & WidthMask(w));
                    w_.LocalSet(loc_tmp1_);
                    w_.LocalGet(gpr_local_[ESP]);
                    w_.I32Const(static_cast<int32_t>(sz));
                    w_.I32Sub();
                    EmitAddSegment(3);
                    w_.LocalSet(loc_addr_);
                    EmitGuestWrite(loc_addr_, loc_tmp1_, w, inst_eip);
                    w_.LocalGet(gpr_local_[ESP]);
                    w_.I32Const(static_cast<int32_t>(sz));
                    w_.I32Sub();
                    w_.LocalSet(gpr_local_[ESP]);
                    MarkDirty(ESP);
                    w_.LocalGet(loc_tmp64_);
                    w_.I32WrapI64();
                }
                w_.LocalSet(loc_tmp1_);
                // Indirect CALL/JMP chains through Sentinel (LinkSlot = 0)
                EmitBlockExitDynamic(loc_tmp1_, block_->Sentinel(), 0, 0, static_cast<uint32_t>(idx + 1));
                terminated = true;
                return true;
            }
        }

        // 25. CMOVcc (0x140..0x14F)
        if (op >= 0x140 && op <= 0x14F) {
            const uint8_t w = OpWidth(jop, false);
            const uint8_t cond = static_cast<uint8_t>(op & 0xF);
            // Architectural requirement: memory operand is read unconditionally (may fault even if cond is false).
            if (is_mem) {
                EmitComputeLinearAddress(jop.dec);
                w_.LocalSet(loc_addr_);
                EmitGuestRead(loc_addr_, w, inst_eip);
            } else {
                EmitReadReg(rm, w);
            }
            w_.LocalSet(loc_tmp1_);  // true value
            EmitReadReg(reg, w);     // false value? Wasm select: (val_true, val_false, cond)
            // Stack order for select: true_val, false_val, cond
            w_.Drop();
            w_.LocalGet(loc_tmp1_);
            EmitReadReg(reg, w);
            EmitCondition(cond);
            w_.Select();
            EmitWriteRegFromStack(reg, w);
            return true;
        }

        // 26. SETcc (0x190..0x19F)
        if (op >= 0x190 && op <= 0x19F) {
            const uint8_t cond = static_cast<uint8_t>(op & 0xF);
            EmitCondition(cond);
            if (is_mem) {
                w_.LocalSet(loc_tmp1_);
                EmitComputeLinearAddress(jop.dec);
                w_.LocalSet(loc_addr_);
                EmitGuestWrite(loc_addr_, loc_tmp1_, 8, inst_eip);
            } else {
                EmitWriteRegFromStack(rm, 8);
            }
            return true;
        }

        // 27. MOVZX (0x1B6, 0x1B7) and MOVSX (0x1BE, 0x1BF)
        if (op == 0x1B6 || op == 0x1B7 || op == 0x1BE || op == 0x1BF) {
            const uint8_t src_w = (op == 0x1B6 || op == 0x1BE) ? 8 : 16;
            const uint8_t dst_w = OpWidth(jop, false);
            const bool is_signed = (op == 0x1BE || op == 0x1BF);
            if (is_mem) {
                EmitComputeLinearAddress(jop.dec);
                w_.LocalSet(loc_addr_);
                EmitGuestRead(loc_addr_, src_w, inst_eip);
            } else {
                EmitReadReg(rm, src_w);
            }
            if (is_signed) EmitSignExtendOnStack(src_w);
            EmitWriteRegFromStack(reg, dst_w);
            return true;
        }

        // 28. BSWAP r32 (0x1C8..0x1CF)
        if (op >= 0x1C8 && op <= 0x1CF) {
            const uint8_t r = op & 7;
            // ((v & 0xFF) << 24) | ((v & 0xFF00) << 8) | ((v >> 8) & 0xFF00) | (v >>> 24)
            w_.LocalGet(gpr_local_[r]);
            w_.I32Const(24);
            w_.I32Shl();
            w_.LocalGet(gpr_local_[r]);
            w_.I32Const(0xFF00);
            w_.I32And();
            w_.I32Const(8);
            w_.I32Shl();
            w_.I32Or();
            w_.LocalGet(gpr_local_[r]);
            w_.I32Const(8);
            w_.I32ShrU();
            w_.I32Const(0xFF00);
            w_.I32And();
            w_.I32Or();
            w_.LocalGet(gpr_local_[r]);
            w_.I32Const(24);
            w_.I32ShrU();
            w_.I32Or();
            w_.LocalSet(gpr_local_[r]);
            MarkDirty(r);
            return true;
        }

        // 29. Native Wasm SSE / SSE2 / SSE3 (0x100..0x1FF)
        if (op >= 0x100 && op < 0x200) {
            return EmitSseInstruction(jop, op, reg, rm, subop, is_mem, live, inst_eip);
        }

        return false;
    }

    bool EmitSseInstruction(const JitOp& jop, uint16_t op, uint8_t reg, uint8_t rm, uint8_t subop, bool is_mem,
                            uint32_t live, uint32_t inst_eip) {
        const bool p66 = jop.dec.prefixes.flags.opsize != 0;
        const bool pf3 = jop.dec.prefixes.flags.rep != 0;
        const bool pf2 = jop.dec.prefixes.flags.repne != 0;

        auto read_src_v128_to_tmp = [&](bool align16) -> uint32_t {
            if (!is_mem) return xmm_local_[rm];
            EmitComputeLinearAddress(jop.dec);
            w_.LocalSet(loc_addr_);
            EmitGuestReadV128(loc_addr_, align16, inst_eip);
            w_.LocalSet(loc_v128_0_);
            return loc_v128_0_;
        };
        auto read_src_f64_to_tmp = [&](uint32_t dst_f64_local) {
            if (is_mem) {
                EmitComputeLinearAddress(jop.dec);
                w_.LocalSet(loc_addr_);
                EmitGuestReadF64(loc_addr_, inst_eip);
            } else {
                w_.LocalGet(xmm_local_[rm]);
                w_.F64x2ExtractLane(0);
            }
            w_.LocalSet(dst_f64_local);
        };
        auto read_src_f32_to_tmp = [&](uint32_t dst_f32_local) {
            if (is_mem) {
                EmitComputeLinearAddress(jop.dec);
                w_.LocalSet(loc_addr_);
                EmitGuestReadF32(loc_addr_, inst_eip);
            } else {
                w_.LocalGet(xmm_local_[rm]);
                w_.F32x4ExtractLane(0);
            }
            w_.LocalSet(dst_f32_local);
        };

        switch (op) {
            // MOVUPS / MOVUPD / MOVSS / MOVSD (load / reg-reg)
            case 0x110: {
                if (pf2) {  // MOVSD
                    if (is_mem) {
                        EmitComputeLinearAddress(jop.dec);
                        w_.LocalSet(loc_addr_);
                        EmitGuestReadV128Load64Zero(loc_addr_, inst_eip);
                    } else {
                        w_.LocalGet(xmm_local_[reg]);
                        w_.LocalGet(xmm_local_[rm]);
                        w_.F64x2ExtractLane(0);
                        w_.F64x2ReplaceLane(0);
                    }
                } else if (pf3) {  // MOVSS
                    if (is_mem) {
                        EmitComputeLinearAddress(jop.dec);
                        w_.LocalSet(loc_addr_);
                        EmitGuestReadV128Load32Zero(loc_addr_, inst_eip);
                    } else {
                        w_.LocalGet(xmm_local_[reg]);
                        w_.LocalGet(xmm_local_[rm]);
                        w_.F32x4ExtractLane(0);
                        w_.F32x4ReplaceLane(0);
                    }
                } else {  // MOVUPS / MOVUPD
                    if (is_mem) {
                        EmitComputeLinearAddress(jop.dec);
                        w_.LocalSet(loc_addr_);
                        EmitGuestReadV128(loc_addr_, false, inst_eip);
                    } else {
                        w_.LocalGet(xmm_local_[rm]);
                    }
                }
                w_.LocalSet(xmm_local_[reg]);
                MarkXmmDirty(reg);
                return true;
            }

            // MOVUPS / MOVUPD / MOVSS / MOVSD (store / reg-reg)
            case 0x111: {
                if (pf2) {  // MOVSD
                    if (is_mem) {
                        EmitComputeLinearAddress(jop.dec);
                        w_.LocalSet(loc_addr_);
                        EmitGuestWriteV128Lane64(loc_addr_, xmm_local_[reg], 0, inst_eip);
                    } else {
                        w_.LocalGet(xmm_local_[rm]);
                        w_.LocalGet(xmm_local_[reg]);
                        w_.F64x2ExtractLane(0);
                        w_.F64x2ReplaceLane(0);
                        w_.LocalSet(xmm_local_[rm]);
                        MarkXmmDirty(rm);
                    }
                } else if (pf3) {  // MOVSS
                    if (is_mem) {
                        EmitComputeLinearAddress(jop.dec);
                        w_.LocalSet(loc_addr_);
                        EmitGuestWriteV128Lane32(loc_addr_, xmm_local_[reg], 0, inst_eip);
                    } else {
                        w_.LocalGet(xmm_local_[rm]);
                        w_.LocalGet(xmm_local_[reg]);
                        w_.F32x4ExtractLane(0);
                        w_.F32x4ReplaceLane(0);
                        w_.LocalSet(xmm_local_[rm]);
                        MarkXmmDirty(rm);
                    }
                } else {  // MOVUPS / MOVUPD
                    if (is_mem) {
                        EmitComputeLinearAddress(jop.dec);
                        w_.LocalSet(loc_addr_);
                        EmitGuestWriteV128(loc_addr_, xmm_local_[reg], false, inst_eip);
                    } else {
                        w_.LocalGet(xmm_local_[reg]);
                        w_.LocalSet(xmm_local_[rm]);
                        MarkXmmDirty(rm);
                    }
                }
                return true;
            }

            // MOVAPS / MOVAPD / MOVDQA / MOVDQU (load)
            case 0x128:
            case 0x16F: {
                const bool align16 = (op == 0x128) || p66;
                if (is_mem) {
                    EmitComputeLinearAddress(jop.dec);
                    w_.LocalSet(loc_addr_);
                    EmitGuestReadV128(loc_addr_, align16, inst_eip);
                } else {
                    w_.LocalGet(xmm_local_[rm]);
                }
                w_.LocalSet(xmm_local_[reg]);
                MarkXmmDirty(reg);
                return true;
            }

            // MOVAPS / MOVAPD / MOVDQA / MOVDQU (store)
            case 0x129:
            case 0x17F: {
                const bool align16 = (op == 0x129) || p66;
                if (is_mem) {
                    EmitComputeLinearAddress(jop.dec);
                    w_.LocalSet(loc_addr_);
                    EmitGuestWriteV128(loc_addr_, xmm_local_[reg], align16, inst_eip);
                } else {
                    w_.LocalGet(xmm_local_[reg]);
                    w_.LocalSet(xmm_local_[rm]);
                    MarkXmmDirty(rm);
                }
                return true;
            }

            // MOVLPD / MOVLPS / MOVHLPS / MOVDDUP / MOVSLDUP
            case 0x112: {
                if (pf2) {  // MOVDDUP
                    if (is_mem) {
                        EmitComputeLinearAddress(jop.dec);
                        w_.LocalSet(loc_addr_);
                        EmitGuestReadV128Load64Splat(loc_addr_, inst_eip);
                    } else {
                        static constexpr uint8_t kDupLo64[16] = {0, 1, 2, 3, 4, 5, 6, 7, 0, 1, 2, 3, 4, 5, 6, 7};
                        w_.LocalGet(xmm_local_[rm]);
                        w_.LocalGet(xmm_local_[rm]);
                        w_.I8x16Shuffle(kDupLo64);
                    }
                } else if (pf3) {  // MOVSLDUP
                    const uint32_t src = read_src_v128_to_tmp(false);
                    static constexpr uint8_t kSldup[16] = {0, 1, 2, 3, 0, 1, 2, 3, 8, 9, 10, 11, 8, 9, 10, 11};
                    w_.LocalGet(src);
                    w_.LocalGet(src);
                    w_.I8x16Shuffle(kSldup);
                } else if (is_mem) {  // MOVLPD / MOVLPS load
                    EmitComputeLinearAddress(jop.dec);
                    w_.LocalSet(loc_addr_);
                    EmitGuestReadV128Load64Lane(loc_addr_, xmm_local_[reg], 0, inst_eip);
                } else {  // MOVHLPS reg, reg: dst[0]=src[1], dst[1]=dst[1]
                    static constexpr uint8_t kMovhlps[16] = {8,  9,  10, 11, 12, 13, 14, 15,
                                                             24, 25, 26, 27, 28, 29, 30, 31};
                    w_.LocalGet(xmm_local_[rm]);
                    w_.LocalGet(xmm_local_[reg]);
                    w_.I8x16Shuffle(kMovhlps);
                }
                w_.LocalSet(xmm_local_[reg]);
                MarkXmmDirty(reg);
                return true;
            }

            // MOVLPD / MOVLPS store
            case 0x113: {
                EmitComputeLinearAddress(jop.dec);
                w_.LocalSet(loc_addr_);
                EmitGuestWriteV128Lane64(loc_addr_, xmm_local_[reg], 0, inst_eip);
                return true;
            }

            // MOVHPD / MOVHPS / MOVLHPS / MOVSHDUP
            case 0x116: {
                if (pf3) {  // MOVSHDUP
                    const uint32_t src = read_src_v128_to_tmp(false);
                    static constexpr uint8_t kShdup[16] = {4, 5, 6, 7, 4, 5, 6, 7, 12, 13, 14, 15, 12, 13, 14, 15};
                    w_.LocalGet(src);
                    w_.LocalGet(src);
                    w_.I8x16Shuffle(kShdup);
                } else if (is_mem) {  // MOVHPD / MOVHPS load
                    EmitComputeLinearAddress(jop.dec);
                    w_.LocalSet(loc_addr_);
                    EmitGuestReadV128Load64Lane(loc_addr_, xmm_local_[reg], 1, inst_eip);
                } else {  // MOVLHPS reg, reg: dst[0]=dst[0], dst[1]=src[0]
                    static constexpr uint8_t kMovlhps[16] = {0, 1, 2, 3, 4, 5, 6, 7, 16, 17, 18, 19, 20, 21, 22, 23};
                    w_.LocalGet(xmm_local_[reg]);
                    w_.LocalGet(xmm_local_[rm]);
                    w_.I8x16Shuffle(kMovlhps);
                }
                w_.LocalSet(xmm_local_[reg]);
                MarkXmmDirty(reg);
                return true;
            }

            // MOVHPD / MOVHPS store
            case 0x117: {
                EmitComputeLinearAddress(jop.dec);
                w_.LocalSet(loc_addr_);
                EmitGuestWriteV128Lane64(loc_addr_, xmm_local_[reg], 1, inst_eip);
                return true;
            }

            // UNPCKLPS / UNPCKLPD / PUNPCKLQDQ
            case 0x114:
            case 0x16C: {
                const uint32_t src = read_src_v128_to_tmp(true);
                static constexpr uint8_t kUnpcklPd[16] = {0, 1, 2, 3, 4, 5, 6, 7, 16, 17, 18, 19, 20, 21, 22, 23};
                static constexpr uint8_t kUnpcklPs[16] = {0, 1, 2, 3, 16, 17, 18, 19, 4, 5, 6, 7, 20, 21, 22, 23};
                w_.LocalGet(xmm_local_[reg]);
                w_.LocalGet(src);
                w_.I8x16Shuffle((op == 0x16C || p66) ? kUnpcklPd : kUnpcklPs);
                w_.LocalSet(xmm_local_[reg]);
                MarkXmmDirty(reg);
                return true;
            }

            // UNPCKHPS / UNPCKHPD / PUNPCKHQDQ
            case 0x115:
            case 0x16D: {
                const uint32_t src = read_src_v128_to_tmp(true);
                static constexpr uint8_t kUnpckhPd[16] = {8, 9, 10, 11, 12, 13, 14, 15, 24, 25, 26, 27, 28, 29, 30, 31};
                static constexpr uint8_t kUnpckhPs[16] = {8, 9, 10, 11, 24, 25, 26, 27, 12, 13, 14, 15, 28, 29, 30, 31};
                w_.LocalGet(xmm_local_[reg]);
                w_.LocalGet(src);
                w_.I8x16Shuffle((op == 0x16D || p66) ? kUnpckhPd : kUnpckhPs);
                w_.LocalSet(xmm_local_[reg]);
                MarkXmmDirty(reg);
                return true;
            }

            // PUNPCKLDQ / PUNPCKHDQ
            case 0x162:
            case 0x16A: {
                const uint32_t src = read_src_v128_to_tmp(true);
                static constexpr uint8_t kUnpcklDq[16] = {0, 1, 2, 3, 16, 17, 18, 19, 4, 5, 6, 7, 20, 21, 22, 23};
                static constexpr uint8_t kUnpckhDq[16] = {8, 9, 10, 11, 24, 25, 26, 27, 12, 13, 14, 15, 28, 29, 30, 31};
                w_.LocalGet(xmm_local_[reg]);
                w_.LocalGet(src);
                w_.I8x16Shuffle(op == 0x162 ? kUnpcklDq : kUnpckhDq);
                w_.LocalSet(xmm_local_[reg]);
                MarkXmmDirty(reg);
                return true;
            }

            // SHUFPS / SHUFPD
            case 0x1C6: {
                const uint32_t src = read_src_v128_to_tmp(true);
                const uint8_t imm8 = static_cast<uint8_t>(GetImm(&jop.dec));
                uint8_t lanes[16];
                if (p66) {  // SHUFPD
                    const uint8_t s0 = static_cast<uint8_t>((imm8 & 1u) * 8u);
                    const uint8_t s1 = static_cast<uint8_t>(16u + (((imm8 >> 1) & 1u) * 8u));
                    for (uint8_t k = 0; k < 8; ++k) {
                        lanes[k] = static_cast<uint8_t>(s0 + k);
                        lanes[8 + k] = static_cast<uint8_t>(s1 + k);
                    }
                } else {  // SHUFPS
                    const uint8_t s[4] = {
                        static_cast<uint8_t>((imm8 & 3u) * 4u),
                        static_cast<uint8_t>(((imm8 >> 2) & 3u) * 4u),
                        static_cast<uint8_t>(16u + (((imm8 >> 4) & 3u) * 4u)),
                        static_cast<uint8_t>(16u + (((imm8 >> 6) & 3u) * 4u)),
                    };
                    for (uint8_t i = 0; i < 4; ++i)
                        for (uint8_t k = 0; k < 4; ++k) lanes[i * 4 + k] = static_cast<uint8_t>(s[i] + k);
                }
                w_.LocalGet(xmm_local_[reg]);
                w_.LocalGet(src);
                w_.I8x16Shuffle(lanes);
                w_.LocalSet(xmm_local_[reg]);
                MarkXmmDirty(reg);
                return true;
            }

            // PSHUFD / PSHUFLW / PSHUFHW
            case 0x170: {
                const uint32_t src = read_src_v128_to_tmp(true);
                const uint8_t imm8 = static_cast<uint8_t>(GetImm(&jop.dec));
                uint8_t lanes[16];
                if (p66) {  // PSHUFD
                    for (uint8_t i = 0; i < 4; ++i) {
                        const uint8_t sel = static_cast<uint8_t>(((imm8 >> (i * 2)) & 3u) * 4u);
                        for (uint8_t k = 0; k < 4; ++k) lanes[i * 4 + k] = static_cast<uint8_t>(sel + k);
                    }
                } else if (pf2) {  // PSHUFLW
                    for (uint8_t i = 0; i < 4; ++i) {
                        const uint8_t sel = static_cast<uint8_t>(((imm8 >> (i * 2)) & 3u) * 2u);
                        lanes[i * 2] = sel;
                        lanes[i * 2 + 1] = static_cast<uint8_t>(sel + 1u);
                    }
                    for (uint8_t b = 8; b < 16; ++b) lanes[b] = b;
                } else {  // F3: PSHUFHW
                    for (uint8_t b = 0; b < 8; ++b) lanes[b] = b;
                    for (uint8_t i = 0; i < 4; ++i) {
                        const uint8_t sel = static_cast<uint8_t>(8u + (((imm8 >> (i * 2)) & 3u) * 2u));
                        lanes[8 + i * 2] = sel;
                        lanes[8 + i * 2 + 1] = static_cast<uint8_t>(sel + 1u);
                    }
                }
                w_.LocalGet(src);
                w_.LocalGet(src);
                w_.I8x16Shuffle(lanes);
                w_.LocalSet(xmm_local_[reg]);
                MarkXmmDirty(reg);
                return true;
            }

            // PSRLDQ (66 0F 73 /3) & PSLLDQ (66 0F 73 /7)
            case 0x173: {
                const uint8_t imm8 = static_cast<uint8_t>(GetImm(&jop.dec));
                if (imm8 == 0) return true;
                if (imm8 >= 16) {
                    w_.V128ConstZero();
                } else {
                    uint8_t lanes[16];
                    if (subop == 3) {  // PSRLDQ
                        for (uint8_t i = 0; i < 16; ++i)
                            lanes[i] = (i + imm8 < 16) ? static_cast<uint8_t>(i + imm8) : 16u;
                    } else {  // PSLLDQ (subop == 7)
                        for (uint8_t i = 0; i < 16; ++i) lanes[i] = (i >= imm8) ? static_cast<uint8_t>(i - imm8) : 16u;
                    }
                    w_.LocalGet(xmm_local_[rm]);
                    w_.V128ConstZero();
                    w_.I8x16Shuffle(lanes);
                }
                w_.LocalSet(xmm_local_[rm]);
                MarkXmmDirty(rm);
                return true;
            }

            // 66 MOVD xmm, r/m32
            case 0x16E: {
                if (is_mem) {
                    EmitComputeLinearAddress(jop.dec);
                    w_.LocalSet(loc_addr_);
                    EmitGuestReadV128Load32Zero(loc_addr_, inst_eip);
                } else {
                    w_.V128ConstZero();
                    EmitReadReg(rm, 32);
                    w_.I32x4ReplaceLane(0);
                }
                w_.LocalSet(xmm_local_[reg]);
                MarkXmmDirty(reg);
                return true;
            }

            // 66 MOVD r/m32, xmm / F3 MOVQ xmm, xmm/m64
            case 0x17E: {
                if (pf3) {  // MOVQ xmm, xmm/m64
                    if (is_mem) {
                        EmitComputeLinearAddress(jop.dec);
                        w_.LocalSet(loc_addr_);
                        EmitGuestReadV128Load64Zero(loc_addr_, inst_eip);
                    } else {
                        static constexpr uint8_t kZeroHi64[16] = {0,  1,  2,  3,  4,  5,  6,  7,
                                                                  16, 17, 18, 19, 20, 21, 22, 23};
                        w_.LocalGet(xmm_local_[rm]);
                        w_.V128ConstZero();
                        w_.I8x16Shuffle(kZeroHi64);
                    }
                    w_.LocalSet(xmm_local_[reg]);
                    MarkXmmDirty(reg);
                } else {  // 66 MOVD r/m32, xmm
                    if (is_mem) {
                        EmitComputeLinearAddress(jop.dec);
                        w_.LocalSet(loc_addr_);
                        EmitGuestWriteV128Lane32(loc_addr_, xmm_local_[reg], 0, inst_eip);
                    } else {
                        w_.LocalGet(xmm_local_[reg]);
                        w_.I32x4ExtractLane(0);
                        EmitWriteRegFromStack(rm, 32);
                    }
                }
                return true;
            }

            // 66 MOVQ xmm/m64, xmm
            case 0x1D6: {
                if (is_mem) {
                    EmitComputeLinearAddress(jop.dec);
                    w_.LocalSet(loc_addr_);
                    EmitGuestWriteV128Lane64(loc_addr_, xmm_local_[reg], 0, inst_eip);
                } else {
                    static constexpr uint8_t kZeroHi64[16] = {0, 1, 2, 3, 4, 5, 6, 7, 16, 17, 18, 19, 20, 21, 22, 23};
                    w_.LocalGet(xmm_local_[reg]);
                    w_.V128ConstZero();
                    w_.I8x16Shuffle(kZeroHi64);
                    w_.LocalSet(xmm_local_[rm]);
                    MarkXmmDirty(rm);
                }
                return true;
            }

            // MOVMSKPS / MOVMSKPD / PMOVMSKB
            case 0x150:
            case 0x1D7: {
                w_.LocalGet(xmm_local_[rm]);
                if (op == 0x1D7)
                    w_.I8x16Bitmask();
                else if (p66)
                    w_.I64x2Bitmask();
                else
                    w_.I32x4Bitmask();
                EmitWriteRegFromStack(reg, 32);
                return true;
            }

            // CVTSI2SD (F2) / CVTSI2SS (F3)
            case 0x12A: {
                if (is_mem) {
                    EmitComputeLinearAddress(jop.dec);
                    w_.LocalSet(loc_addr_);
                    EmitGuestRead(loc_addr_, 32, inst_eip);
                } else {
                    EmitReadReg(rm, 32);
                }
                w_.LocalSet(loc_tmp1_);
                w_.LocalGet(xmm_local_[reg]);
                w_.LocalGet(loc_tmp1_);
                if (pf2) {
                    w_.F64ConvertI32S();
                    w_.F64x2ReplaceLane(0);
                } else {
                    w_.F32ConvertI32S();
                    w_.F32x4ReplaceLane(0);
                }
                w_.LocalSet(xmm_local_[reg]);
                MarkXmmDirty(reg);
                return true;
            }

            // CVTTSD2SI / CVTTSS2SI (0x12C) & CVTSD2SI / CVTSS2SI (0x12D)
            case 0x12C:
            case 0x12D: {
                const bool round_nearest = (op == 0x12D);
                if (pf2) {
                    read_src_f64_to_tmp(loc_f64_0_);
                    if (round_nearest) {
                        w_.LocalGet(loc_f64_0_);
                        w_.F64Nearest();
                        w_.LocalSet(loc_f64_0_);
                    }
                    // Valid range [-2147483648.0, 2147483648.0): exact truncation; NaN/overflow -> 0x80000000
                    w_.LocalGet(loc_f64_0_);
                    w_.I32TruncSatF64S();
                    w_.I32ConstU(0x80000000u);
                    w_.LocalGet(loc_f64_0_);
                    w_.F64Const(-2147483648.0);
                    w_.F64Ge();
                    w_.LocalGet(loc_f64_0_);
                    w_.F64Const(2147483648.0);
                    w_.F64Lt();
                    w_.I32And();
                    w_.Select();
                } else {  // F3: single
                    read_src_f32_to_tmp(loc_f32_0_);
                    if (round_nearest) {
                        w_.LocalGet(loc_f32_0_);
                        w_.F32Nearest();
                        w_.LocalSet(loc_f32_0_);
                    }
                    w_.LocalGet(loc_f32_0_);
                    w_.I32TruncSatF32S();
                    w_.I32ConstU(0x80000000u);
                    w_.LocalGet(loc_f32_0_);
                    w_.F32Const(-2147483648.0f);
                    w_.F32Ge();
                    w_.LocalGet(loc_f32_0_);
                    w_.F32Const(2147483648.0f);
                    w_.F32Lt();
                    w_.I32And();
                    w_.Select();
                }
                EmitWriteRegFromStack(reg, 32);
                return true;
            }

            // CVTPS2PD (none) / CVTPD2PS (66) / CVTSD2SS (F2) / CVTSS2SD (F3)
            case 0x15A: {
                if (pf2) {  // CVTSD2SS
                    read_src_f64_to_tmp(loc_f64_0_);
                    w_.LocalGet(xmm_local_[reg]);
                    w_.LocalGet(loc_f64_0_);
                    w_.F32DemoteF64();
                    w_.F32x4ReplaceLane(0);
                } else if (pf3) {  // CVTSS2SD
                    read_src_f32_to_tmp(loc_f32_0_);
                    w_.LocalGet(xmm_local_[reg]);
                    w_.LocalGet(loc_f32_0_);
                    w_.F64PromoteF32();
                    w_.F64x2ReplaceLane(0);
                } else if (p66) {  // CVTPD2PS (16B aligned)
                    const uint32_t src = read_src_v128_to_tmp(true);
                    w_.LocalGet(src);
                    w_.F32x4DemoteF64x2Zero();
                } else {  // CVTPS2PD (8B unaligned)
                    if (is_mem) {
                        EmitComputeLinearAddress(jop.dec);
                        w_.LocalSet(loc_addr_);
                        EmitGuestReadV128Load64Zero(loc_addr_, inst_eip);
                    } else {
                        w_.LocalGet(xmm_local_[rm]);
                    }
                    w_.F64x2PromoteLowF32x4();
                }
                w_.LocalSet(xmm_local_[reg]);
                MarkXmmDirty(reg);
                return true;
            }

            // F3 CVTDQ2PD (8B unaligned)
            case 0x1E6: {
                if (is_mem) {
                    EmitComputeLinearAddress(jop.dec);
                    w_.LocalSet(loc_addr_);
                    EmitGuestReadV128Load64Zero(loc_addr_, inst_eip);
                } else {
                    w_.LocalGet(xmm_local_[rm]);
                }
                w_.F64x2ConvertLowI32x4S();
                w_.LocalSet(xmm_local_[reg]);
                MarkXmmDirty(reg);
                return true;
            }

            // UCOMISS/UCOMISD (0x12E) & COMISS/COMISD (0x12F)
            case 0x12E:
            case 0x12F: {
                const bool need_flags = (live & DFE_ALL_FLAGS) != 0;
                if (is_mem && flags_.kind != LazyFlagKind::Clean && need_flags) FlushFlags();

                if (p66) {
                    read_src_f64_to_tmp(loc_f64_1_);
                    if (!need_flags) return true;
                    w_.LocalGet(xmm_local_[reg]);
                    w_.F64x2ExtractLane(0);
                    w_.LocalSet(loc_f64_0_);

                    // ord = (a <= b) | (a > b); 1 => PF=0 (pf_state=1), 0 => unordered PF=1 (pf_state=0)
                    w_.LocalGet(loc_f64_0_);
                    w_.LocalGet(loc_f64_1_);
                    w_.F64Le();
                    w_.LocalGet(loc_f64_0_);
                    w_.LocalGet(loc_f64_1_);
                    w_.F64Gt();
                    w_.I32Or();
                    w_.LocalSet(loc_tmp1_);  // ord (also pf_state!)

                    // select(select(CF, select(ZF, 0, a==b), a<b), ZF|PF|CF, ord)
                    w_.I32Const(CF_MASK);
                    w_.I32Const(ZF_MASK);
                    w_.I32Const(0);
                    w_.LocalGet(loc_f64_0_);
                    w_.LocalGet(loc_f64_1_);
                    w_.F64Eq();
                    w_.Select();
                    w_.LocalGet(loc_f64_0_);
                    w_.LocalGet(loc_f64_1_);
                    w_.F64Lt();
                    w_.Select();
                    w_.I32Const(ZF_MASK | PF_MASK | CF_MASK);
                    w_.LocalGet(loc_tmp1_);
                    w_.Select();
                } else {
                    read_src_f32_to_tmp(loc_f32_1_);
                    if (!need_flags) return true;
                    w_.LocalGet(xmm_local_[reg]);
                    w_.F32x4ExtractLane(0);
                    w_.LocalSet(loc_f32_0_);

                    w_.LocalGet(loc_f32_0_);
                    w_.LocalGet(loc_f32_1_);
                    w_.F32Le();
                    w_.LocalGet(loc_f32_0_);
                    w_.LocalGet(loc_f32_1_);
                    w_.F32Gt();
                    w_.I32Or();
                    w_.LocalSet(loc_tmp1_);

                    w_.I32Const(CF_MASK);
                    w_.I32Const(ZF_MASK);
                    w_.I32Const(0);
                    w_.LocalGet(loc_f32_0_);
                    w_.LocalGet(loc_f32_1_);
                    w_.F32Eq();
                    w_.Select();
                    w_.LocalGet(loc_f32_0_);
                    w_.LocalGet(loc_f32_1_);
                    w_.F32Lt();
                    w_.Select();
                    w_.I32Const(ZF_MASK | PF_MASK | CF_MASK);
                    w_.LocalGet(loc_tmp1_);
                    w_.Select();
                }
                EmitPackFlagsCache(ARITHMETIC_FLAGS_MASK, loc_tmp1_);
                flags_.kind = LazyFlagKind::Clean;
                return true;
            }

            // SQRT (PS / PD / SS / SD)
            case 0x151: {
                if (pf2) {
                    read_src_f64_to_tmp(loc_f64_0_);
                    w_.LocalGet(xmm_local_[reg]);
                    w_.LocalGet(loc_f64_0_);
                    w_.F64Sqrt();
                    w_.F64x2ReplaceLane(0);
                } else if (pf3) {
                    read_src_f32_to_tmp(loc_f32_0_);
                    w_.LocalGet(xmm_local_[reg]);
                    w_.LocalGet(loc_f32_0_);
                    w_.F32Sqrt();
                    w_.F32x4ReplaceLane(0);
                } else {
                    const uint32_t src = read_src_v128_to_tmp(true);
                    w_.LocalGet(src);
                    if (p66)
                        w_.F64x2Sqrt();
                    else
                        w_.F32x4Sqrt();
                }
                w_.LocalSet(xmm_local_[reg]);
                MarkXmmDirty(reg);
                return true;
            }

            // ADD / MUL / SUB / DIV (PS / PD / SS / SD)
            case 0x158:
            case 0x159:
            case 0x15C:
            case 0x15E: {
                if (pf2) {
                    read_src_f64_to_tmp(loc_f64_1_);
                    w_.LocalGet(xmm_local_[reg]);
                    w_.LocalGet(xmm_local_[reg]);
                    w_.F64x2ExtractLane(0);
                    w_.LocalGet(loc_f64_1_);
                    if (op == 0x158)
                        w_.F64Add();
                    else if (op == 0x159)
                        w_.F64Mul();
                    else if (op == 0x15C)
                        w_.F64Sub();
                    else
                        w_.F64Div();
                    w_.F64x2ReplaceLane(0);
                } else if (pf3) {
                    read_src_f32_to_tmp(loc_f32_1_);
                    w_.LocalGet(xmm_local_[reg]);
                    w_.LocalGet(xmm_local_[reg]);
                    w_.F32x4ExtractLane(0);
                    w_.LocalGet(loc_f32_1_);
                    if (op == 0x158)
                        w_.F32Add();
                    else if (op == 0x159)
                        w_.F32Mul();
                    else if (op == 0x15C)
                        w_.F32Sub();
                    else
                        w_.F32Div();
                    w_.F32x4ReplaceLane(0);
                } else {
                    const uint32_t src = read_src_v128_to_tmp(true);
                    w_.LocalGet(xmm_local_[reg]);
                    w_.LocalGet(src);
                    if (p66) {
                        if (op == 0x158)
                            w_.F64x2Add();
                        else if (op == 0x159)
                            w_.F64x2Mul();
                        else if (op == 0x15C)
                            w_.F64x2Sub();
                        else
                            w_.F64x2Div();
                    } else {
                        if (op == 0x158)
                            w_.F32x4Add();
                        else if (op == 0x159)
                            w_.F32x4Mul();
                        else if (op == 0x15C)
                            w_.F32x4Sub();
                        else
                            w_.F32x4Div();
                    }
                }
                w_.LocalSet(xmm_local_[reg]);
                MarkXmmDirty(reg);
                return true;
            }

            // MIN (0x15D) / MAX (0x15F) — exact x86 NaN & signed-zero semantics (returns second operand on NaN/eq)
            case 0x15D:
            case 0x15F: {
                const bool is_min = (op == 0x15D);
                if (pf2) {
                    read_src_f64_to_tmp(loc_f64_1_);
                    w_.LocalGet(xmm_local_[reg]);
                    w_.F64x2ExtractLane(0);
                    w_.LocalSet(loc_f64_0_);
                    w_.LocalGet(xmm_local_[reg]);
                    w_.LocalGet(loc_f64_0_);
                    w_.LocalGet(loc_f64_1_);
                    w_.LocalGet(loc_f64_0_);
                    w_.LocalGet(loc_f64_1_);
                    if (is_min)
                        w_.F64Lt();
                    else
                        w_.F64Gt();
                    w_.Select();
                    w_.F64x2ReplaceLane(0);
                } else if (pf3) {
                    read_src_f32_to_tmp(loc_f32_1_);
                    w_.LocalGet(xmm_local_[reg]);
                    w_.F32x4ExtractLane(0);
                    w_.LocalSet(loc_f32_0_);
                    w_.LocalGet(xmm_local_[reg]);
                    w_.LocalGet(loc_f32_0_);
                    w_.LocalGet(loc_f32_1_);
                    w_.LocalGet(loc_f32_0_);
                    w_.LocalGet(loc_f32_1_);
                    if (is_min)
                        w_.F32Lt();
                    else
                        w_.F32Gt();
                    w_.Select();
                    w_.F32x4ReplaceLane(0);
                } else {
                    // Wasm pmin(b, a) = (a < b) ? a : b; pmax(b, a) = (b < a) ? a : b = (a > b) ? a : b
                    const uint32_t src = read_src_v128_to_tmp(true);
                    w_.LocalGet(src);
                    w_.LocalGet(xmm_local_[reg]);
                    if (p66) {
                        if (is_min)
                            w_.F64x2Pmin();
                        else
                            w_.F64x2Pmax();
                    } else {
                        if (is_min)
                            w_.F32x4Pmin();
                        else
                            w_.F32x4Pmax();
                    }
                }
                w_.LocalSet(xmm_local_[reg]);
                MarkXmmDirty(reg);
                return true;
            }

            // Bitwise v128: ANDPS/PD, PAND, ANDNPS/PD, PANDN, ORPS/PD, POR, XORPS/PD, PXOR
            case 0x154:
            case 0x1DB:
            case 0x155:
            case 0x1DF:
            case 0x156:
            case 0x1EB:
            case 0x157:
            case 0x1EF: {
                if ((op == 0x157 || op == 0x1EF) && !is_mem && reg == rm) {
                    w_.V128ConstZero();
                    w_.LocalSet(xmm_local_[reg]);
                    MarkXmmDirty(reg);
                    return true;
                }
                const uint32_t src = read_src_v128_to_tmp(true);
                if (op == 0x155 || op == 0x1DF) {
                    // x86 ANDN: dst = (~dst) & src; Wasm v128.andnot(a, b) = a & ~b => push src first, dst second!
                    w_.LocalGet(src);
                    w_.LocalGet(xmm_local_[reg]);
                    w_.V128AndNot();
                } else {
                    w_.LocalGet(xmm_local_[reg]);
                    w_.LocalGet(src);
                    if (op == 0x154 || op == 0x1DB)
                        w_.V128And();
                    else if (op == 0x156 || op == 0x1EB)
                        w_.V128Or();
                    else
                        w_.V128Xor();
                }
                w_.LocalSet(xmm_local_[reg]);
                MarkXmmDirty(reg);
                return true;
            }

            // Packed integer SSE2 arithmetic & comparisons (all 66 prefix, 16B aligned)
            case 0x164:
            case 0x165:
            case 0x166:
            case 0x174:
            case 0x175:
            case 0x176:
            case 0x1D4:
            case 0x1DA:
            case 0x1DE:
            case 0x1F8:
            case 0x1F9:
            case 0x1FA:
            case 0x1FB:
            case 0x1FC:
            case 0x1FD:
            case 0x1FE: {
                const uint32_t src = read_src_v128_to_tmp(true);
                w_.LocalGet(xmm_local_[reg]);
                w_.LocalGet(src);
                switch (op) {
                    case 0x164:
                        w_.I8x16GtS();
                        break;
                    case 0x165:
                        w_.I16x8GtS();
                        break;
                    case 0x166:
                        w_.I32x4GtS();
                        break;
                    case 0x174:
                        w_.I8x16Eq();
                        break;
                    case 0x175:
                        w_.I16x8Eq();
                        break;
                    case 0x176:
                        w_.I32x4Eq();
                        break;
                    case 0x1D4:
                        w_.I64x2Add();
                        break;
                    case 0x1DA:
                        w_.I8x16MinU();
                        break;
                    case 0x1DE:
                        w_.I8x16MaxU();
                        break;
                    case 0x1F8:
                        w_.I8x16Sub();
                        break;
                    case 0x1F9:
                        w_.I16x8Sub();
                        break;
                    case 0x1FA:
                        w_.I32x4Sub();
                        break;
                    case 0x1FB:
                        w_.I64x2Sub();
                        break;
                    case 0x1FC:
                        w_.I8x16Add();
                        break;
                    case 0x1FD:
                        w_.I16x8Add();
                        break;
                    case 0x1FE:
                        w_.I32x4Add();
                        break;
                    default:
                        return false;
                }
                w_.LocalSet(xmm_local_[reg]);
                MarkXmmDirty(reg);
                return true;
            }

            default:
                return false;
        }
    }

    void EmitSetCfOfFromStack() {
        // Stack top: 0 or 1 (i32) -> sets or clears CF | OF in kParamFlags
        w_.LocalSet(loc_tmp0_);
        w_.LocalGet(kParamFlags);
        w_.I64ConstU(~static_cast<uint64_t>(CF_MASK | OF_MASK));
        w_.I64And();
        w_.I64Const(CF_MASK | OF_MASK);
        w_.I64Const(0);
        w_.LocalGet(loc_tmp0_);
        w_.Select();
        w_.I64Or();
        w_.LocalSet(kParamFlags);
        flags_.kind = LazyFlagKind::Clean;
    }

    bool EmitShiftRotate(const JitOp& jop, uint16_t op, uint8_t subop, uint8_t rm, bool is_mem, uint32_t live,
                         uint32_t inst_eip) {
        const uint8_t w = (op == 0xC0 || op == 0xD0 || op == 0xD2) ? 8 : OpWidth(jop, false);
        const bool is_cl = (op == 0xD2 || op == 0xD3);
        const bool is_rot = (subop == 0 || subop == 1);
        const bool need_flags = (live & DFE_ALL_FLAGS) != 0;
        const uint32_t const_cnt = is_cl ? 0u : ((op == 0xD0 || op == 0xD1) ? 1u : (GetImm(&jop.dec) & 31u));

        // Flush pending flags if mem access might fault, or if CL might be 0 (preserving flags), or if rotate
        // (preserves SF/ZF/PF/AF)
        if (flags_.kind != LazyFlagKind::Clean && (is_mem || is_cl || is_rot || const_cnt == 0)) {
            FlushFlags();
        }

        if (is_mem) {
            EmitComputeLinearAddress(jop.dec);
            w_.LocalSet(loc_addr_);
            EmitGuestRead(loc_addr_, w, inst_eip);
        } else {
            EmitReadReg(rm, w);
        }

        if (!is_cl && const_cnt == 0) {
            w_.Drop();
            return true;
        }

        w_.LocalSet(loc_tmp1_);  // dest (zero-extended)

        auto emit_body = [&](bool known_cnt, uint32_t cnt_val, uint32_t cnt_local) {
            // Compute result -> loc_flg_r_, and optionally flags
            if (is_rot) {
                if (w == 32) {
                    w_.LocalGet(loc_tmp1_);
                    if (known_cnt)
                        w_.I32Const(static_cast<int32_t>(cnt_val));
                    else
                        w_.LocalGet(cnt_local);
                    if (subop == 0)
                        w_.I32Rotl();
                    else
                        w_.I32Rotr();
                    w_.LocalSet(loc_flg_r_);
                } else {
                    // 8-bit or 16-bit rotate: shift count modulo w
                    // Effective c = count & (w - 1)
                    if (known_cnt) {
                        const uint32_t eff = cnt_val & (w - 1u);
                        if (eff == 0) {
                            w_.LocalGet(loc_tmp1_);
                        } else if (subop == 0) {
                            w_.LocalGet(loc_tmp1_);
                            w_.I32Const(static_cast<int32_t>(eff));
                            w_.I32Shl();
                            w_.LocalGet(loc_tmp1_);
                            w_.I32Const(static_cast<int32_t>(w - eff));
                            w_.I32ShrU();
                            w_.I32Or();
                            EmitMaskOnStack(w);
                        } else {
                            w_.LocalGet(loc_tmp1_);
                            w_.I32Const(static_cast<int32_t>(eff));
                            w_.I32ShrU();
                            w_.LocalGet(loc_tmp1_);
                            w_.I32Const(static_cast<int32_t>(w - eff));
                            w_.I32Shl();
                            w_.I32Or();
                            EmitMaskOnStack(w);
                        }
                        w_.LocalSet(loc_flg_r_);
                    } else {
                        // Variable count for 8/16-bit: eff = cnt & (w - 1)
                        w_.LocalGet(cnt_local);
                        w_.I32Const(w - 1);
                        w_.I32And();
                        w_.LocalTee(loc_flg_a_);
                        w_.I32Eqz();
                        PushIf(ValType::I32);
                        w_.LocalGet(loc_tmp1_);
                        w_.Else();
                        if (subop == 0) {
                            w_.LocalGet(loc_tmp1_);
                            w_.LocalGet(loc_flg_a_);
                            w_.I32Shl();
                            w_.LocalGet(loc_tmp1_);
                            w_.I32Const(w);
                            w_.LocalGet(loc_flg_a_);
                            w_.I32Sub();
                            w_.I32ShrU();
                            w_.I32Or();
                            EmitMaskOnStack(w);
                        } else {
                            w_.LocalGet(loc_tmp1_);
                            w_.LocalGet(loc_flg_a_);
                            w_.I32ShrU();
                            w_.LocalGet(loc_tmp1_);
                            w_.I32Const(w);
                            w_.LocalGet(loc_flg_a_);
                            w_.I32Sub();
                            w_.I32Shl();
                            w_.I32Or();
                            EmitMaskOnStack(w);
                        }
                        PopStructured();
                        w_.LocalSet(loc_flg_r_);
                    }
                }
                // Write result before updating flags (for memory fault safety)
                if (is_mem) {
                    EmitGuestWrite(loc_addr_, loc_flg_r_, w, inst_eip);
                } else {
                    w_.LocalGet(loc_flg_r_);
                    EmitWriteRegFromStack(rm, w);
                }
                if (need_flags) {
                    // CF: ROL -> res & 1; ROR -> (res >> (w-1)) & 1
                    w_.LocalGet(loc_flg_r_);
                    if (subop == 1) {
                        w_.I32Const(w - 1);
                        w_.I32ShrU();
                    }
                    w_.I32Const(1);
                    w_.I32And();
                    w_.LocalSet(loc_flg_a_);  // new_cf

                    // Clear CF (and OF if count == 1)
                    if (known_cnt) {
                        uint32_t clr = CF_MASK | (cnt_val == 1 ? OF_MASK : 0u);
                        w_.LocalGet(kParamFlags);
                        w_.I64ConstU(~static_cast<uint64_t>(clr));
                        w_.I64And();
                        w_.LocalGet(loc_flg_a_);
                        if (cnt_val == 1) {
                            // ROL: of = ((res >> (w-1)) & 1) ^ cf
                            // ROR: of = ((res >> (w-1)) ^ (res >> (w-2))) & 1
                            w_.LocalGet(loc_flg_r_);
                            w_.I32Const(w - 1);
                            w_.I32ShrU();
                            if (subop == 0) {
                                w_.LocalGet(loc_flg_a_);
                            } else {
                                w_.LocalGet(loc_flg_r_);
                                w_.I32Const(w - 2);
                                w_.I32ShrU();
                            }
                            w_.I32Xor();
                            w_.I32Const(1);
                            w_.I32And();
                            w_.I32Const(11);
                            w_.I32Shl();
                            w_.I32Or();
                        }
                        w_.I64ExtendI32U();
                        w_.I64Or();
                        w_.LocalSet(kParamFlags);
                    } else {
                        // Variable CL: always update CF, update OF only if cnt == 1
                        w_.LocalGet(kParamFlags);
                        w_.I64ConstU(~static_cast<uint64_t>(CF_MASK));
                        w_.I64And();
                        w_.LocalGet(loc_flg_a_);
                        w_.I64ExtendI32U();
                        w_.I64Or();
                        w_.LocalSet(kParamFlags);

                        w_.LocalGet(cnt_local);
                        w_.I32Const(1);
                        w_.I32Eq();
                        PushIf();
                        w_.LocalGet(kParamFlags);
                        w_.I64ConstU(~static_cast<uint64_t>(OF_MASK));
                        w_.I64And();
                        w_.LocalGet(loc_flg_r_);
                        w_.I32Const(w - 1);
                        w_.I32ShrU();
                        if (subop == 0) {
                            w_.LocalGet(loc_flg_a_);
                        } else {
                            w_.LocalGet(loc_flg_r_);
                            w_.I32Const(w - 2);
                            w_.I32ShrU();
                        }
                        w_.I32Xor();
                        w_.I32Const(1);
                        w_.I32And();
                        w_.I32Const(11);
                        w_.I32Shl();
                        w_.I64ExtendI32U();
                        w_.I64Or();
                        w_.LocalSet(kParamFlags);
                        PopStructured();
                    }
                    flags_.kind = LazyFlagKind::Clean;
                }
                return;
            }

            // Shifts: SHL(4,6), SHR(5), SAR(7)
            const bool is_shl = (subop == 4 || subop == 6);
            const bool is_shr = (subop == 5);
            if (is_shl) {
                w_.LocalGet(loc_tmp1_);
                if (known_cnt)
                    w_.I32Const(static_cast<int32_t>(cnt_val));
                else
                    w_.LocalGet(cnt_local);
                w_.I32Shl();
                EmitMaskOnStack(w);
                w_.LocalSet(loc_flg_r_);
            } else if (is_shr) {
                w_.LocalGet(loc_tmp1_);
                if (known_cnt)
                    w_.I32Const(static_cast<int32_t>(cnt_val));
                else
                    w_.LocalGet(cnt_local);
                w_.I32ShrU();
                w_.LocalSet(loc_flg_r_);
            } else {  // SAR
                w_.LocalGet(loc_tmp1_);
                EmitSignExtendOnStack(w);
                if (known_cnt)
                    w_.I32Const(static_cast<int32_t>(cnt_val));
                else
                    w_.LocalGet(cnt_local);
                w_.I32ShrS();
                EmitMaskOnStack(w);
                w_.LocalSet(loc_flg_r_);
            }

            // Write result first (for memory fault safety)
            if (is_mem) {
                EmitGuestWrite(loc_addr_, loc_flg_r_, w, inst_eip);
            } else {
                w_.LocalGet(loc_flg_r_);
                EmitWriteRegFromStack(rm, w);
            }

            if (need_flags) {
                // ZF | SF
                w_.I32Const(ZF_MASK);
                w_.I32Const(0);
                w_.LocalGet(loc_flg_r_);
                w_.I32Eqz();
                w_.Select();
                EmitSfBitFromLocal(loc_flg_r_, w);
                w_.I32Or();

                // CF
                if (is_shl) {
                    if (known_cnt) {
                        if (cnt_val <= w) {
                            w_.LocalGet(loc_tmp1_);
                            w_.I32Const(static_cast<int32_t>(w - cnt_val));
                            w_.I32ShrU();
                            w_.I32Const(1);
                            w_.I32And();
                        } else {
                            w_.I32Const(0);
                        }
                    } else {
                        w_.LocalGet(cnt_local);
                        w_.I32Const(w);
                        w_.I32LeU();
                        PushIf(ValType::I32);
                        w_.LocalGet(loc_tmp1_);
                        w_.I32Const(w);
                        w_.LocalGet(cnt_local);
                        w_.I32Sub();
                        w_.I32ShrU();
                        w_.I32Const(1);
                        w_.I32And();
                        w_.Else();
                        w_.I32Const(0);
                        PopStructured();
                    }
                } else if (is_shr) {
                    if (known_cnt) {
                        w_.LocalGet(loc_tmp1_);
                        w_.I32Const(static_cast<int32_t>(cnt_val - 1));
                        w_.I32ShrU();
                        w_.I32Const(1);
                        w_.I32And();
                    } else {
                        w_.LocalGet(loc_tmp1_);
                        w_.LocalGet(cnt_local);
                        w_.I32Const(1);
                        w_.I32Sub();
                        w_.I32ShrU();
                        w_.I32Const(1);
                        w_.I32And();
                    }
                } else {  // SAR
                    if (known_cnt) {
                        if (cnt_val >= w) {
                            w_.LocalGet(loc_tmp1_);
                            w_.I32Const(w - 1);
                            w_.I32ShrU();
                            w_.I32Const(1);
                            w_.I32And();
                        } else {
                            w_.LocalGet(loc_tmp1_);
                            w_.I32Const(static_cast<int32_t>(cnt_val - 1));
                            w_.I32ShrU();
                            w_.I32Const(1);
                            w_.I32And();
                        }
                    } else {
                        w_.LocalGet(cnt_local);
                        w_.I32Const(w);
                        w_.I32GeU();
                        PushIf(ValType::I32);
                        w_.LocalGet(loc_tmp1_);
                        w_.I32Const(w - 1);
                        w_.I32ShrU();
                        w_.I32Const(1);
                        w_.I32And();
                        w_.Else();
                        w_.LocalGet(loc_tmp1_);
                        w_.LocalGet(cnt_local);
                        w_.I32Const(1);
                        w_.I32Sub();
                        w_.I32ShrU();
                        w_.I32Const(1);
                        w_.I32And();
                        PopStructured();
                    }
                }
                w_.LocalTee(loc_flg_a_);  // save CF for SHL OF
                w_.I32Or();

                // OF (for count == 1 on SHL/SHR; SAR always clears OF=0!)
                if (is_shl) {
                    if (known_cnt) {
                        if (cnt_val == 1) {
                            w_.LocalGet(loc_flg_r_);
                            w_.I32Const(w - 1);
                            w_.I32ShrU();
                            w_.LocalGet(loc_flg_a_);
                            w_.I32Xor();
                            w_.I32Const(1);
                            w_.I32And();
                            w_.I32Const(11);
                            w_.I32Shl();
                            w_.I32Or();
                        }
                    } else {
                        w_.LocalGet(cnt_local);
                        w_.I32Const(1);
                        w_.I32Eq();
                        PushIf(ValType::I32);
                        w_.LocalGet(loc_flg_r_);
                        w_.I32Const(w - 1);
                        w_.I32ShrU();
                        w_.LocalGet(loc_flg_a_);
                        w_.I32Xor();
                        w_.I32Const(1);
                        w_.I32And();
                        w_.I32Const(11);
                        w_.I32Shl();
                        w_.Else();
                        w_.I32Const(0);
                        PopStructured();
                        w_.I32Or();
                    }
                } else if (is_shr) {
                    if (known_cnt) {
                        if (cnt_val == 1) {
                            w_.LocalGet(loc_tmp1_);
                            w_.I32Const(w - 1);
                            w_.I32ShrU();
                            w_.I32Const(1);
                            w_.I32And();
                            w_.I32Const(11);
                            w_.I32Shl();
                            w_.I32Or();
                        }
                    } else {
                        w_.LocalGet(cnt_local);
                        w_.I32Const(1);
                        w_.I32Eq();
                        PushIf(ValType::I32);
                        w_.LocalGet(loc_tmp1_);
                        w_.I32Const(w - 1);
                        w_.I32ShrU();
                        w_.I32Const(1);
                        w_.I32And();
                        w_.I32Const(11);
                        w_.I32Shl();
                        w_.Else();
                        w_.I32Const(0);
                        PopStructured();
                        w_.I32Or();
                    }
                }

                EmitPackFlagsCache(ARITHMETIC_FLAGS_MASK, loc_flg_r_);
                flags_.kind = LazyFlagKind::Clean;
            }
        };

        if (is_cl) {
            w_.LocalGet(gpr_local_[ECX]);
            w_.I32Const(31);
            w_.I32And();
            w_.LocalTee(loc_flg_b_);
            PushIf();
            emit_body(false, 0, loc_flg_b_);
            PopStructured();
        } else {
            emit_body(true, const_cnt, 0);
        }
        return true;
    }

    EmuState* state_;
    BasicBlock* block_;
    std::vector<JitOp>& ops_;
    size_t compiled_count_;

    WasmCodeEmitter w_;
    std::array<uint32_t, 8> gpr_local_{};
    std::array<uint32_t, 8> xmm_local_{};
    uint8_t touched_mask_ = 0;
    uint8_t written_mask_ = 0;
    uint8_t dirty_mask_ = 0;
    uint8_t xmm_touched_mask_ = 0;
    uint8_t xmm_written_mask_ = 0;
    uint8_t xmm_dirty_mask_ = 0;

    uint32_t loc_tmp0_ = 0;
    uint32_t loc_tmp1_ = 0;
    uint32_t loc_addr_ = 0;
    uint32_t loc_flg_a_ = 0;
    uint32_t loc_flg_b_ = 0;
    uint32_t loc_flg_r_ = 0;
    uint32_t loc_tmp64_ = 0;
    uint32_t loc_rtlb_ = 0;
    uint32_t loc_wtlb_ = 0;
    uint32_t loc_slow_ran_ = 0;
    uint32_t loc_f64_0_ = 0;
    uint32_t loc_f64_1_ = 0;
    uint32_t loc_f32_0_ = 0;
    uint32_t loc_f32_1_ = 0;
    uint32_t loc_v128_0_ = 0;

    LazyFlagState flags_;
    bool has_self_loop_ = false;
    bool loop_kills_flags_early_ = false;
    bool assume_flat_ds_ss_ = true;
    bool uses_seg_ds_ss_ = false;
    uint32_t loop_depth_ = 0;
};

}  // namespace

// ============================================================================
// Public Wasm JIT Interface
// ============================================================================

bool WasmJitEnabled() {
    g_rt.EnsureInit();
    return g_rt.enabled;
}

void SetWasmJitEnabled(bool enabled) {
    g_rt.EnsureInit();
    g_rt.enabled = enabled;
}

void SetWasmJitHotThreshold(uint16_t threshold) {
    g_rt.EnsureInit();
    g_rt.hot_threshold = threshold;
}

void SetWasmJitPoolSize(uint32_t pool_size) {
    g_rt.EnsureInit();
    if (g_rt.pool_base < 0 && pool_size >= 16 && pool_size <= 65536) {
        g_rt.requested_pool_size = pool_size;
    }
}

void SetWasmJitStatsEnabled(bool enabled) {
    g_rt.EnsureInit();
    g_rt.stats_enabled = enabled;
}

uint16_t WasmJitHotThreshold(const EmuState&) {
    g_rt.EnsureInit();
    return g_rt.enabled ? g_rt.hot_threshold : 0;
}

void CompileHotWasmBlock(EmuState* state, BasicBlock* block) {
    g_rt.EnsureInit();
    if (!g_rt.enabled || !state || !block) return;
    if (state->mmu.code_cache_flush_pending() || !block->is_valid()) return;
    if (block->inst_count() == 0) return;

    // Already compiled?
    uint32_t existing_slot = 0;
    if (g_rt.IsPoolEntry(block->entry, existing_slot)) return;
    if (block->entry != block->FirstOp()->handler) return;
    if (state->mmu.lookup_cached_block(block->start_eip()) != block) return;

    const double t0 = fiber_wasm_now_ms();

    const size_t total_ops = block->inst_count();
    std::vector<JitOp> ops;
    ops.reserve(total_ops);

    size_t compiled_count = 0;
    for (size_t i = 0; i < total_ops; ++i) {
        JitOp jop;
        jop.pub = block->ReadOp(i);
        jop.phys = block->LogicalOp(i);
        jop.next_eip = jop.pub.next_eip;
        jop.start_eip = jop.pub.next_eip - jop.pub.len;

        if (jop.pub.len == 0) break;
        uint8_t buf[255];
        auto bytes = std::span(buf, jop.pub.len);
        if (!CopyDecodedInstructionBytes(state, jop.pub, bytes)) break;

        DecodedInstTmp inst{};
        uint16_t h_idx = 0;
        uint16_t raw = 0;
        if (DecodeInstructionBounded(bytes.data(), bytes.size(), &inst, &h_idx, &raw, DecodeHandlerMode::Generic) !=
            InstructionDecodeResult::Success) {
            break;
        }
        jop.dec = inst.head;
        jop.raw_opcode = raw;
        if (!IsSupportedOpcode(jop)) break;

        jop.supported = true;
        ops.push_back(jop);
        compiled_count++;
    }

    // Require either full block coverage (>=1 op) or a meaningful prefix (>=2 ops)
    if (compiled_count == 0 || (compiled_count < total_ops && compiled_count < 2)) {
        g_rt.skipped_blocks++;
        block->chain.llvm_hot_count = 0;
        return;
    }

    std::vector<uint8_t> wasm_bytes;
    bool has_self_loop = false;
    SingleBlockEmitter emitter(state, block, ops, compiled_count);
    if (!emitter.Emit(wasm_bytes, has_self_loop)) {
        g_rt.compile_failures++;
        block->chain.llvm_hot_count = 0;
        return;
    }

    const int32_t slot = g_rt.AllocSlot(block);
    if (slot < 0) {
        g_rt.compile_failures++;
        block->chain.llvm_hot_count = 0;
        return;
    }

    const int read_slow_idx = static_cast<int>(reinterpret_cast<uintptr_t>(&FiberWasmJitReadSlow));
    const int write_slow_idx = static_cast<int>(reinterpret_cast<uintptr_t>(&FiberWasmJitWriteSlow));
    const int ok = fiber_wasm_compile_and_install(g_rt.pool_base + slot, wasm_bytes.data(),
                                                  static_cast<int>(wasm_bytes.size()), read_slow_idx, write_slow_idx);
    const double t1 = fiber_wasm_now_ms();

    if (!ok) {
        g_rt.ReleaseSlot(static_cast<uint32_t>(slot), block);
        g_rt.compile_failures++;
        block->chain.llvm_hot_count = 0;
        return;
    }

    block->entry = g_rt.TableIndexToHandler(static_cast<uint32_t>(slot));
    block->chain.llvm_hot_count = 0;

    g_rt.compiled_blocks++;
    if (has_self_loop) g_rt.self_loop_blocks++;
    if (compiled_count == total_ops)
        g_rt.full_blocks++;
    else
        g_rt.partial_prefix_blocks++;
    g_rt.total_wasm_bytes += wasm_bytes.size();
    g_rt.total_compiled_insts += compiled_count;
    g_rt.total_block_insts += total_ops;
    g_rt.total_compile_ms += (t1 - t0);
}

void WasmJitOnBlockInvalidate(BasicBlock& block) {
    uint32_t slot = 0;
    if (g_rt.IsPoolEntry(block.entry, slot)) {
        g_rt.ReleaseSlot(slot, &block);
        block.entry = block.FirstOp()->handler;
        block.chain.llvm_hot_count = g_rt.enabled ? g_rt.hot_threshold : 0;
        g_rt.invalidated_blocks++;
    }
}

void WasmJitRetireCodeCache(const mem::CodeCacheState& cache) {
    for (BasicBlock* block : cache.all_blocks) {
        if (!block) continue;
        uint32_t slot = 0;
        if (g_rt.IsPoolEntry(block->entry, slot)) {
            g_rt.ReleaseSlot(slot, block);
            block->entry = nullptr;
            g_rt.invalidated_blocks++;
        }
    }
}

void WasmJitRecordExecutedInsts(uint64_t insts) {
    if (g_rt.stats_enabled) {
        g_rt.dyn.total_insts_executed += insts;
    }
}

void WasmJitResetPool() { g_rt.ResetPool(); }

void WasmJitResetStats() {
    g_rt.compiled_blocks = 0;
    g_rt.skipped_blocks = 0;
    g_rt.compile_failures = 0;
    g_rt.evicted_blocks = 0;
    g_rt.invalidated_blocks = 0;
    g_rt.self_loop_blocks = 0;
    g_rt.partial_prefix_blocks = 0;
    g_rt.full_blocks = 0;
    g_rt.total_wasm_bytes = 0;
    g_rt.total_compiled_insts = 0;
    g_rt.total_block_insts = 0;
    g_rt.total_compile_ms = 0.0;
    g_rt.dyn = {};
}

int WasmJitDumpStats(char* buffer, size_t buffer_size) {
    if (!buffer || buffer_size == 0) return -1;
    const double avg_bytes =
        g_rt.compiled_blocks ? static_cast<double>(g_rt.total_wasm_bytes) / g_rt.compiled_blocks : 0.0;
    const double avg_ms = g_rt.compiled_blocks ? g_rt.total_compile_ms / g_rt.compiled_blocks : 0.0;
    const double jit_inst_pct = g_rt.dyn.total_insts_executed
                                    ? (100.0 * static_cast<double>(g_rt.dyn.jit_insts_executed) /
                                       static_cast<double>(g_rt.dyn.total_insts_executed))
                                    : 0.0;
    return std::snprintf(
        buffer, buffer_size,
        "{\"enabled\":%d,\"stats_enabled\":%d,\"hot_threshold\":%u,\"pool_size\":%u,\"live_slots\":%u,"
        "\"compiled_blocks\":%llu,\"full_blocks\":%llu,\"partial_prefix_blocks\":%llu,\"self_loop_blocks\":%llu,"
        "\"skipped_blocks\":%llu,\"compile_failures\":%llu,\"evicted_blocks\":%llu,\"invalidated_blocks\":%llu,"
        "\"total_wasm_bytes\":%llu,\"avg_wasm_bytes\":%.1f,\"total_compile_ms\":%.3f,\"avg_compile_ms\":%.4f,"
        "\"total_compiled_insts\":%llu,\"total_block_insts\":%llu,"
        "\"dyn_jit_block_entries\":%llu,\"dyn_jit_self_loop_iters\":%llu,\"dyn_jit_insts\":%llu,"
        "\"dyn_total_insts\":%llu,\"dyn_jit_inst_pct\":%.2f,\"dyn_fallback_exits\":%llu,"
        "\"dyn_slow_reads\":%llu,\"dyn_slow_writes\":%llu}",
        g_rt.enabled ? 1 : 0, g_rt.stats_enabled ? 1 : 0, static_cast<unsigned>(g_rt.hot_threshold), g_rt.pool_size,
        g_rt.live_slots, static_cast<unsigned long long>(g_rt.compiled_blocks),
        static_cast<unsigned long long>(g_rt.full_blocks), static_cast<unsigned long long>(g_rt.partial_prefix_blocks),
        static_cast<unsigned long long>(g_rt.self_loop_blocks), static_cast<unsigned long long>(g_rt.skipped_blocks),
        static_cast<unsigned long long>(g_rt.compile_failures), static_cast<unsigned long long>(g_rt.evicted_blocks),
        static_cast<unsigned long long>(g_rt.invalidated_blocks),
        static_cast<unsigned long long>(g_rt.total_wasm_bytes), avg_bytes, g_rt.total_compile_ms, avg_ms,
        static_cast<unsigned long long>(g_rt.total_compiled_insts),
        static_cast<unsigned long long>(g_rt.total_block_insts),
        static_cast<unsigned long long>(g_rt.dyn.jit_block_entries),
        static_cast<unsigned long long>(g_rt.dyn.jit_self_loop_iters),
        static_cast<unsigned long long>(g_rt.dyn.jit_insts_executed),
        static_cast<unsigned long long>(g_rt.dyn.total_insts_executed), jit_inst_pct,
        static_cast<unsigned long long>(g_rt.dyn.jit_fallback_exits),
        static_cast<unsigned long long>(g_rt.dyn.jit_slow_reads),
        static_cast<unsigned long long>(g_rt.dyn.jit_slow_writes));
}

#else  // !__EMSCRIPTEN__

bool WasmJitEnabled() { return false; }
void SetWasmJitEnabled(bool) {}
void SetWasmJitHotThreshold(uint16_t) {}
void SetWasmJitPoolSize(uint32_t) {}
void SetWasmJitStatsEnabled(bool) {}
uint16_t WasmJitHotThreshold(const EmuState&) { return 0; }
void CompileHotWasmBlock(EmuState*, BasicBlock*) {}
void WasmJitOnBlockInvalidate(BasicBlock&) {}
void WasmJitRetireCodeCache(const mem::CodeCacheState&) {}
void WasmJitRecordExecutedInsts(uint64_t) {}
void WasmJitResetPool() {}
void WasmJitResetStats() {}
int WasmJitDumpStats(char* buffer, size_t buffer_size) {
    if (!buffer || buffer_size == 0) return -1;
    return std::snprintf(buffer, buffer_size, "{\"enabled\":0}");
}

#endif  // __EMSCRIPTEN__

}  // namespace fiberish
