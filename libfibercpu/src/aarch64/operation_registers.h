#pragma once

#include "register_decoder.h"

namespace fiberish::aarch64::detail {

inline BoundOperation BindRegisterOperation(const DecodedInstruction& op) {
    if (op.kind == Kind::ScalarFloat) {
        switch (DecodeFloatFamily(op.word)) {
#define ARM_BindFloat(Name) \
    case FloatFamily::Name: \
        return {OperationId::Float##Name, BranchForm::None, true};
#define ARM_BindVector(Name)
#define ARM_BindByteVector(Name)
#define ARM_REGISTER(Category, Name) ARM_Bind##Category(Name)
#include "operation_registers.def"
#undef ARM_REGISTER
#undef ARM_BindByteVector
#undef ARM_BindVector
#undef ARM_BindFloat
            case FloatFamily::Undefined:
                return {};
        }
    }
    if (op.kind == Kind::Vector) {
        const auto family = DecodeVectorFamily(op.word);
        switch (DecodeByteVectorOp(op.word, family)) {
#define ARM_BindFloat(Name)
#define ARM_BindVector(Name)
#define ARM_BindByteVector(Name) \
    case ByteVectorOp::Name:     \
        return {OperationId::ByteVector##Name, BranchForm::None, false};
#define ARM_REGISTER(Category, Name) ARM_Bind##Category(Name)
#include "operation_registers.def"
#undef ARM_REGISTER
#undef ARM_BindByteVector
#undef ARM_BindVector
#undef ARM_BindFloat
            case ByteVectorOp::None:
                break;
        }
        switch (family) {
#define ARM_BindFloat(Name)
#define ARM_BindVector(Name) \
    case VectorFamily::Name: \
        return {OperationId::Vector##Name, BranchForm::None, true};
#define ARM_BindByteVector(Name)
#define ARM_REGISTER(Category, Name) ARM_Bind##Category(Name)
#include "operation_registers.def"
#undef ARM_REGISTER
#undef ARM_BindByteVector
#undef ARM_BindVector
#undef ARM_BindFloat
            case VectorFamily::Undefined:
                return {};
        }
    }
    return {};
}

}  // namespace fiberish::aarch64::detail
