#pragma once

#include "common.h"
#include "decoder.h"
#include "exec_utils.h"
#include "state.h"

namespace fiberish {

// Forward declarations (from ops/ops_groups.h)
void OpUd2(EmuState* state, DecodedOp* op);
void OpUd2(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);

// Template Helper for Group 1 (ALU operations with immediate)
template <typename T, bool UpdateFlags = true, uint8_t FixedSubOp = 0xFF>
void Helper_Group1(EmuState* state, DecodedOp* op, T dest, T src, mem::MicroTLB* utlb) {
    uint8_t subop;
    if constexpr (FixedSubOp != 0xFF) {
        subop = FixedSubOp;
    } else {
        subop = (op->modrm >> 3) & 7;
    }

    T res = 0;

    switch (subop) {
        case 0:
            res = AluAdd<T, UpdateFlags>(state, dest, src);
            break;
        case 1:
            res = AluOr<T, UpdateFlags>(state, dest, src);
            break;
        case 2:
            res = AluAdc<T, UpdateFlags>(state, dest, src);
            break;
        case 3:
            res = AluSbb<T, UpdateFlags>(state, dest, src);
            break;
        case 4:
            res = AluAnd<T, UpdateFlags>(state, dest, src);
            break;
        case 5:
            res = AluSub<T, UpdateFlags>(state, dest, src);
            break;
        case 6:
            res = AluXor<T, UpdateFlags>(state, dest, src);
            break;
        case 7:
            AluSub<T, UpdateFlags>(state, dest, src);
            return;  // CMP (No writeback)
        default:
            OpUd2(state, op);
            return;
    }

    if constexpr (sizeof(T) == 1) {
        WriteModRM8(state, op, (uint8_t)res, utlb);
    } else if constexpr (sizeof(T) == 2) {
        WriteModRM16(state, op, (uint16_t)res, utlb);
    } else {
        WriteModRM32(state, op, (uint32_t)res, utlb);
    }
}

// Template Helper for Group 3 (MUL, DIV, TEST, NOT, NEG)
template <typename T, bool UpdateFlags = true, uint8_t FixedSubOp = 0xFF>
void Helper_Group3(EmuState* state, DecodedOp* op, T val, mem::MicroTLB* utlb) {
    uint8_t subop;
    if constexpr (FixedSubOp != 0xFF) {
        subop = FixedSubOp;
    } else {
        subop = (op->modrm >> 3) & 7;
    }

    switch (subop) {
        case 0:  // TEST imm
        case 1:  // TEST imm
        {
            AluAnd<T, UpdateFlags>(state, val, (T)op->imm);
            break;
        }
        case 2:  // NOT
            if constexpr (sizeof(T) == 1)
                WriteModRM8(state, op, ~val, utlb);
            else if constexpr (sizeof(T) == 2)
                WriteModRM16(state, op, ~val, utlb);
            else
                WriteModRM32(state, op, ~val, utlb);
            break;
        case 3:  // NEG
        {
            T res = AluSub<T, UpdateFlags>(state, (T)0, val);
            if constexpr (UpdateFlags) {
                if (val != 0)
                    state->ctx.eflags |= CF_MASK;
                else
                    state->ctx.eflags &= ~CF_MASK;
            }

            if constexpr (sizeof(T) == 1)
                WriteModRM8(state, op, res, utlb);
            else if constexpr (sizeof(T) == 2)
                WriteModRM16(state, op, res, utlb);
            else
                WriteModRM32(state, op, res, utlb);
            break;
        }
        case 4:  // MUL (Unsigned)
        {
            if constexpr (sizeof(T) == 1) {  // Byte: AX = AL * r/m8
                uint8_t al = GetReg8(state, EAX);
                uint16_t res = (uint16_t)al * (uint16_t)val;

                uint32_t* rax = GetRegPtr(state, EAX);
                *rax = (*rax & 0xFFFF0000) | res;

                if constexpr (UpdateFlags) {
                    if ((res & 0xFF00) != 0)
                        state->ctx.eflags |= (OF_MASK | CF_MASK);
                    else
                        state->ctx.eflags &= ~(OF_MASK | CF_MASK);
                }
            } else if constexpr (sizeof(T) == 2) {  // Word: DX:AX = AX * r/m16
                uint16_t ax = (uint16_t)(GetReg(state, EAX) & 0xFFFF);
                uint32_t res = (uint32_t)ax * (uint32_t)val;

                uint32_t* rax = GetRegPtr(state, EAX);
                uint32_t* rdx = GetRegPtr(state, EDX);
                *rax = (*rax & 0xFFFF0000) | (res & 0xFFFF);
                *rdx = (*rdx & 0xFFFF0000) | ((res >> 16) & 0xFFFF);

                if constexpr (UpdateFlags) {
                    if ((res >> 16) != 0)
                        state->ctx.eflags |= (OF_MASK | CF_MASK);
                    else
                        state->ctx.eflags &= ~(OF_MASK | CF_MASK);
                }
            } else {  // Dword: EDX:EAX = EAX * r/m32
                uint32_t eax = GetReg(state, EAX);
                uint64_t res = (uint64_t)eax * (uint64_t)val;
                SetReg(state, EAX, (uint32_t)res);
                SetReg(state, EDX, (uint32_t)(res >> 32));

                if constexpr (UpdateFlags) {
                    if ((res >> 32) != 0)
                        state->ctx.eflags |= (OF_MASK | CF_MASK);
                    else
                        state->ctx.eflags &= ~(OF_MASK | CF_MASK);
                }
            }
            break;
        }
        case 5:  // IMUL (Signed)
        {
            if constexpr (sizeof(T) == 1) {  // AX = AL * r/m8
                int8_t al = (int8_t)GetReg8(state, EAX);
                int16_t res = (int16_t)al * (int16_t)(int8_t)val;

                uint32_t* rax = GetRegPtr(state, EAX);
                *rax = (*rax & 0xFFFF0000) | (uint16_t)res;

                if constexpr (UpdateFlags) {
                    if (res != (int16_t)(int8_t)res)
                        state->ctx.eflags |= (OF_MASK | CF_MASK);
                    else
                        state->ctx.eflags &= ~(OF_MASK | CF_MASK);
                }
            } else if constexpr (sizeof(T) == 2) {  // Word: DX:AX = AX * r/m16
                int16_t ax = (int16_t)(GetReg(state, EAX) & 0xFFFF);
                int32_t res = (int32_t)ax * (int32_t)(int16_t)val;

                uint32_t* rax = GetRegPtr(state, EAX);
                uint32_t* rdx = GetRegPtr(state, EDX);
                *rax = (*rax & 0xFFFF0000) | (res & 0xFFFF);
                *rdx = (*rdx & 0xFFFF0000) | ((res >> 16) & 0xFFFF);

                if constexpr (UpdateFlags) {
                    if (res != (int32_t)(int16_t)res)
                        state->ctx.eflags |= (OF_MASK | CF_MASK);
                    else
                        state->ctx.eflags &= ~(OF_MASK | CF_MASK);
                }
            } else {  // Dword: EDX:EAX = EAX * r/m32
                int32_t eax = (int32_t)GetReg(state, EAX);
                int64_t res = (int64_t)eax * (int64_t)(int32_t)val;
                SetReg(state, EAX, (uint32_t)res);
                SetReg(state, EDX, (uint32_t)(res >> 32));

                if constexpr (UpdateFlags) {
                    if (res != (int64_t)(int32_t)res)
                        state->ctx.eflags |= (OF_MASK | CF_MASK);
                    else
                        state->ctx.eflags &= ~(OF_MASK | CF_MASK);
                }
            }
            break;
        }
        case 6:  // DIV (Unsigned)
        {
            if constexpr (sizeof(T) == 1) {  // AX / r/m8
                uint16_t ax = (uint16_t)GetReg(state, EAX) & 0xFFFF;
                if (val == 0) {
                    if (!state->hooks.on_interrupt(state, 0)) {
                        state->status = EmuStatus::Fault;
                        state->fault_vector = 0;  // #DE
                    }
                    return;
                }
                uint16_t q = ax / val;
                uint16_t r = ax % val;

                uint32_t* rax = GetRegPtr(state, EAX);
                *rax = (*rax & 0xFFFF0000) | (r << 8) | (q & 0xFF);
            } else if constexpr (sizeof(T) == 2) {  // DX:AX / r/m16
                uint32_t dx_ax = ((uint32_t)(GetReg(state, EDX) & 0xFFFF) << 16) | (GetReg(state, EAX) & 0xFFFF);
                if (val == 0) {
                    if (!state->hooks.on_interrupt(state, 0)) {
                        state->status = EmuStatus::Fault;
                        state->fault_vector = 0;  // #DE
                    }
                    return;
                }
                uint32_t q = dx_ax / val;
                uint32_t r = dx_ax % val;

                if (q > 0xFFFF) { /* Overflow */
                }

                uint32_t* rax = GetRegPtr(state, EAX);
                uint32_t* rdx = GetRegPtr(state, EDX);
                *rax = (*rax & 0xFFFF0000) | (q & 0xFFFF);
                *rdx = (*rdx & 0xFFFF0000) | (r & 0xFFFF);
            } else {  // EDX:EAX / r/m32
                uint64_t edx_eax = ((uint64_t)GetReg(state, EDX) << 32) | GetReg(state, EAX);
                if (val == 0) {
                    if (!state->hooks.on_interrupt(state, 0)) {
                        state->status = EmuStatus::Fault;
                        state->fault_vector = 0;  // #DE
                    }
                    return;
                }
                uint64_t q = edx_eax / val;
                uint64_t r = edx_eax % val;
                SetReg(state, EAX, (uint32_t)q);
                SetReg(state, EDX, (uint32_t)r);
            }
            break;
        }
        case 7:  // IDIV (Signed)
        {
            if constexpr (sizeof(T) == 1) {  // AX / r/m8
                int16_t ax = (int16_t)(GetReg(state, EAX) & 0xFFFF);
                int8_t v = (int8_t)val;
                if (v == 0) {
                    if (!state->hooks.on_interrupt(state, 0)) {
                        state->status = EmuStatus::Fault;
                        state->fault_vector = 0;  // #DE
                    }
                    return;
                }
                int16_t q = ax / v;
                int16_t r = ax % v;

                uint32_t* rax = GetRegPtr(state, EAX);
                *rax = (*rax & 0xFFFF0000) | ((uint8_t)r << 8) | ((uint8_t)q);
            } else if constexpr (sizeof(T) == 2) {  // DX:AX / r/m16
                int32_t dx_ax =
                    (int32_t)(((uint32_t)(GetReg(state, EDX) & 0xFFFF) << 16) | (GetReg(state, EAX) & 0xFFFF));
                int16_t v = (int16_t)val;
                if (v == 0) {
                    if (!state->hooks.on_interrupt(state, 0)) {
                        state->status = EmuStatus::Fault;
                        state->fault_vector = 0;  // #DE
                    }
                    return;
                }
                int32_t q = dx_ax / v;
                int32_t r = dx_ax % v;

                if (q > 32767 || q < -32768) { /* Overflow */
                }

                uint32_t* rax = GetRegPtr(state, EAX);
                uint32_t* rdx = GetRegPtr(state, EDX);
                *rax = (*rax & 0xFFFF0000) | (q & 0xFFFF);
                *rdx = (*rdx & 0xFFFF0000) | (r & 0xFFFF);
            } else {  // EDX:EAX / r/m32
                int64_t edx_eax = ((int64_t)GetReg(state, EDX) << 32) | GetReg(state, EAX);
                int32_t v = (int32_t)val;
                if (v == 0) {
                    if (!state->hooks.on_interrupt(state, 0)) {
                        state->status = EmuStatus::Fault;
                        state->fault_vector = 0;  // #DE
                    }
                    return;
                }
                int64_t q = edx_eax / v;
                int64_t r = edx_eax % v;

                SetReg(state, EAX, (uint32_t)q);
                SetReg(state, EDX, (uint32_t)r);
            }
            break;
        }
        default:
            OpUd2(state, op);
    }
}

}  // namespace fiberish
