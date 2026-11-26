#pragma once

#include "block_operands.h"

namespace fiberish::aarch64::detail {

enum class OperationId : uint8_t {
    Unsupported,
    Nop,
#define ARM_INTEGER(Name) Name,
#define ARM_BRANCH(Name, Form) Name,
#define ARM_MEMORY(Name, FallbackWidth) Name,
#define ARM_EXTRA(Name) Name,
#include "operation_catalog.def"
#undef ARM_EXTRA
#undef ARM_MEMORY
#undef ARM_BRANCH
#undef ARM_INTEGER
};

enum class BranchForm : uint8_t { None, Direct, Conditional, Indirect };
enum class BlockExitLink : uint8_t { Direct, Fallthrough, Indirect };

// The execution kernel completes the whole operation or leaves its effects
// untouched for replay. A branch writes the target through the kernel ABI.
enum class OperationFlow : uint32_t { Continue, Branch, Replay, Refetch };

struct BoundOperation {
    OperationId id = OperationId::Unsupported;
    BranchForm branch = BranchForm::None;
    bool may_exit = false;
    bool refetch = false;
};

inline BoundOperation BindRegisterOperation(const DecodedInstruction& op);
inline BoundOperation BindSystemOperation(const DecodedInstruction& op);

}  // namespace fiberish::aarch64::detail

#include "operation_registers.h"
#include "operation_system.h"

namespace fiberish::aarch64::detail {

inline BoundOperation BindOperation(const DecodedInstruction& op) {
    if (op.word == NopWord) return {OperationId::Nop};
    switch (op.kind) {
#define ARM_INTEGER(Name) \
    case Kind::Name:      \
        return {OperationId::Name};
#define ARM_BRANCH(Name, Form) \
    case Kind::Name:           \
        return {OperationId::Name, BranchForm::Form};
#define ARM_MEMORY(Name, FallbackWidth) \
    case Kind::Name:                    \
        return {OperationId::Name, BranchForm::None, true};
#define ARM_EXTRA(Name)
#include "operation_catalog.def"
#undef ARM_EXTRA
#undef ARM_MEMORY
#undef ARM_BRANCH
#undef ARM_INTEGER
        case Kind::ScalarFloat:
        case Kind::Vector:
            return BindRegisterOperation(op);
        case Kind::System:
        case Kind::LoadStoreAtomic:
        case Kind::LoadStoreExclusive:
            return BindSystemOperation(op);
        default:
            return {};
    }
}

inline const char* KernelName(OperationId id) {
    switch (id) {
#define ARM_EXTRA(Name)     \
    case OperationId::Name: \
        return "FiberArmJit" #Name;
#define ARM_INTEGER(Name) ARM_EXTRA(Name)
#define ARM_BRANCH(Name, Form) ARM_EXTRA(Name)
#define ARM_MEMORY(Name, FallbackWidth) ARM_EXTRA(Name)
#include "operation_catalog.def"
#undef ARM_MEMORY
#undef ARM_BRANCH
#undef ARM_INTEGER
#undef ARM_EXTRA
        case OperationId::Unsupported:
        case OperationId::Nop:
            return nullptr;
    }
    return nullptr;
}

}  // namespace fiberish::aarch64::detail
