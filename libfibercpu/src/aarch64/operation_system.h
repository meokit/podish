#pragma once

#include "system_handlers.h"

namespace fiberish::aarch64::detail {

inline BoundOperation BindSystemOperation(const DecodedInstruction& op) {
    switch (op.kind) {
        case Kind::LoadStoreExclusive:
            return {OperationId::LoadStoreExclusive, BranchForm::None, true};
        case Kind::LoadStoreAtomic:
            return {OperationId::LoadStoreAtomic, BranchForm::None, true};
        case Kind::System:
            switch (DecodeSystemOp(op.word)) {
#define ARM_SYSTEM(Name)                                                                          \
    case SystemOp::Name:                                                                          \
        return {OperationId::System##Name, BranchForm::None,                                      \
                SystemOp::Name == SystemOp::ZeroCacheLine || SystemOp::Name == SystemOp::Refetch, \
                SystemOp::Name == SystemOp::Refetch};
#include "system_operations.def"
#undef ARM_SYSTEM
                case SystemOp::Undefined:
                    break;
            }
            break;
        default:
            break;
    }
    return {};
}

}  // namespace fiberish::aarch64::detail
