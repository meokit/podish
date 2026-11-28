#pragma once

#include "decoder.h"

#ifndef FIBERCPU_AARCH64_PIN_TOUCH_SPLIT
#define FIBERCPU_AARCH64_PIN_TOUCH_SPLIT 1
#endif

namespace fiberish::aarch64::detail {

inline constexpr bool SplitPinTouches = FIBERCPU_AARCH64_PIN_COUNT > 0 && FIBERCPU_AARCH64_PIN_TOUCH_SPLIT;

#define A64_PIN_BIT(id, slot) | (uint32_t{1} << id)
inline constexpr uint32_t PinnedGprMask = 0 FIBERCPU_AARCH64_FOR_EACH_PIN(A64_PIN_BIT);
#undef A64_PIN_BIT

constexpr bool IsPinnedGpr(unsigned reg, bool use_sp = false) {
    return ((PinnedGprMask >> reg) & 1) && (reg != 31 || use_sp);
}

constexpr bool InstructionTouchesPinnedReg([[maybe_unused]] const DecodedInstruction& op) {
#if FIBERCPU_AARCH64_PIN_COUNT == 0
    return false;
#else
    switch (op.kind) {
        case Kind::AddSubImmediate:
        case Kind::AddSubExtended:
            return IsPinnedGpr(op.rn, true) || IsPinnedGpr(op.rd, !(op.operation & 1)) ||
                   (op.kind == Kind::AddSubExtended && IsPinnedGpr(op.rm));
        case Kind::LogicalImmediate:
            return IsPinnedGpr(op.rn) || IsPinnedGpr(op.rd, (op.operation & 3) != 3);
        case Kind::AddSubShifted:
        case Kind::AddSubCarry:
        case Kind::LogicalShifted:
        case Kind::Extract:
        case Kind::ConditionalSelect:
        case Kind::DataTwoSource:
            return IsPinnedGpr(op.rd) || IsPinnedGpr(op.rn) || IsPinnedGpr(op.rm);
        case Kind::Multiply:
            return IsPinnedGpr(op.rd) || IsPinnedGpr(op.rn) || IsPinnedGpr(op.rm) ||
                   ((op.operation != 2 && op.operation != 6) && IsPinnedGpr(op.ra));
        case Kind::Bitfield:
        case Kind::DataOneSource:
            return IsPinnedGpr(op.rd) || IsPinnedGpr(op.rn);
        case Kind::ConditionalCompare:
            return IsPinnedGpr(op.rn) || (!op.mode && IsPinnedGpr(op.rm));
        case Kind::Address:
        case Kind::MoveWide:
        case Kind::CompareBranch:
        case Kind::TestBranch:
            return IsPinnedGpr(op.rd);
        case Kind::BranchImmediate:
            return op.operation && IsPinnedGpr(30);
        case Kind::BranchRegister:
            return IsPinnedGpr(op.rn) || (op.operation && IsPinnedGpr(30));
        case Kind::LoadStoreImmediate:
        case Kind::LoadStoreRegister:
        case Kind::LoadStorePair:
        case Kind::LoadLiteral:
            // The base is read in every addressing mode, including pre/post-index writeback.
            if (op.kind != Kind::LoadLiteral && IsPinnedGpr(op.rn, true)) return true;
            if (op.kind == Kind::LoadStoreRegister && IsPinnedGpr(op.rm)) return true;
            return op.operation != Prefetch && !Field(op.word, 26, 1) &&
                   (IsPinnedGpr(op.rd) || (op.kind == Kind::LoadStorePair && IsPinnedGpr(op.ra)));
        case Kind::BranchCondition:
        case Kind::Undefined:
        case Kind::Syscall:
        case Kind::Breakpoint:
            return false;
        default:
            // Atomics, system, FP and SIMD handlers keep their existing pin-aware paths.
            return true;
    }
#endif
}

}  // namespace fiberish::aarch64::detail
