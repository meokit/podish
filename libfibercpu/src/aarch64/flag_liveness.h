#pragma once

#include "integer.h"

namespace fiberish::aarch64::detail {

struct FlagEffects {
    uint32_t reads;
    uint32_t writes;
};

constexpr uint32_t ConditionFlags(unsigned condition) {
    constexpr std::array<uint32_t, 8> inputs{
        Zero, Carry, Negative, Overflow, Carry | Zero, Negative | Overflow, Negative | Overflow | Zero, 0};
    return inputs[condition >> 1];
}

// A fault or a callback can expose the flags in effect before the instruction.
// Only explicitly known, non-faulting integer operations can erase prior writes.
constexpr FlagEffects NZCVEffects(const DecodedInstruction& op) {
    const uint32_t writes = WritesIntegerNZCV(op) ? FlagMask : 0;
    switch (op.kind) {
        case Kind::AddSubImmediate:
        case Kind::AddSubShifted:
        case Kind::AddSubExtended:
        case Kind::LogicalImmediate:
        case Kind::LogicalShifted:
            return {0, writes};
        case Kind::AddSubCarry:
            return {Carry, writes};
        case Kind::ConditionalCompare:
            return {ConditionFlags(op.shift), FlagMask};
        case Kind::ConditionalSelect:
        case Kind::BranchCondition:
            return {ConditionFlags(op.shift), 0};
        case Kind::Address:
        case Kind::MoveWide:
        case Kind::Bitfield:
        case Kind::Extract:
        case Kind::DataOneSource:
        case Kind::DataTwoSource:
        case Kind::Multiply:
        case Kind::BranchImmediate:
        case Kind::BranchRegister:
        case Kind::CompareBranch:
        case Kind::TestBranch:
            return {0, 0};
        default:
            return {FlagMask, 0};
    }
}

constexpr bool OmitNZCV(const DecodedInstruction& op, uint32_t& live) {
    const auto [reads, writes] = NZCVEffects(op);
    const bool dead = writes != 0 && (writes & live) == 0;
    live = (live & ~writes) | reads;
    return dead;
}

}  // namespace fiberish::aarch64::detail
