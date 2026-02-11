#pragma once

#include "common.h"
#include "decoder.h"
#include "exec_utils.h"
#include "state.h"

namespace fiberish {

// Template Helper for Group 1 (ALU operations with immediate)
template <typename T, bool UpdateFlags = true, uint8_t FixedSubOp = 0xFF>
LogicFlow Helper_Group1(EmuState* state, DecodedOp* op, T dest, T src, mem::MicroTLB* utlb) {
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
            return LogicFlow::Continue;  // CMP (No writeback)
        default:
            if (!state->hooks.on_invalid_opcode(state)) {
                state->status = EmuStatus::Fault;
                state->fault_vector = 6;
            }
            if (state->status == EmuStatus::Fault) return LogicFlow::ExitOnCurrentEIP;
            return LogicFlow::Continue;
    }

    if constexpr (sizeof(T) == 1) {
        if (!WriteModRM<uint8_t, OpOnTLBMiss::Retry>(state, op, (uint8_t)res, utlb)) return LogicFlow::RetryMemoryOp;
    } else if constexpr (sizeof(T) == 2) {
        if (!WriteModRM<uint16_t, OpOnTLBMiss::Retry>(state, op, (uint16_t)res, utlb)) return LogicFlow::RetryMemoryOp;
    } else {
        if (!WriteModRM<uint32_t, OpOnTLBMiss::Retry>(state, op, (uint32_t)res, utlb)) return LogicFlow::RetryMemoryOp;
    }
    return LogicFlow::Continue;
}

// Template Helper for Group 3 (MUL, DIV, TEST, NOT, NEG)
template <typename T, bool UpdateFlags = true, uint8_t FixedSubOp = 0xFF>
LogicFlow Helper_Group3(EmuState* state, DecodedOp* op, T val, mem::MicroTLB* utlb) {
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
            if constexpr (sizeof(T) == 1) {
                if (!WriteModRM<uint8_t, OpOnTLBMiss::Retry>(state, op, ~val, utlb)) return LogicFlow::RetryMemoryOp;
            } else if constexpr (sizeof(T) == 2) {
                if (!WriteModRM<uint16_t, OpOnTLBMiss::Retry>(state, op, ~val, utlb)) return LogicFlow::RetryMemoryOp;
            } else {
                if (!WriteModRM<uint32_t, OpOnTLBMiss::Retry>(state, op, ~val, utlb)) return LogicFlow::RetryMemoryOp;
            }
            return LogicFlow::Continue;
        case 3:  // NEG
        {
            T res = AluSub<T, UpdateFlags>(state, (T)0, val);
            if constexpr (UpdateFlags) {
                if (val != 0)
                    state->ctx.eflags |= CF_MASK;
                else
                    state->ctx.eflags &= ~CF_MASK;
            }

            if constexpr (sizeof(T) == 1) {
                if (!WriteModRM<uint8_t, OpOnTLBMiss::Retry>(state, op, (uint8_t)res, utlb))
                    return LogicFlow::RetryMemoryOp;
            } else if constexpr (sizeof(T) == 2) {
                if (!WriteModRM<uint16_t, OpOnTLBMiss::Retry>(state, op, (uint16_t)res, utlb))
                    return LogicFlow::RetryMemoryOp;
            } else {
                if (!WriteModRM<uint32_t, OpOnTLBMiss::Retry>(state, op, (uint32_t)res, utlb))
                    return LogicFlow::RetryMemoryOp;
            }
            return LogicFlow::Continue;
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
                        state->fault_vector = 0;  // #DE
                        state->status = EmuStatus::Fault;
                        return LogicFlow::ExitOnCurrentEIP;
                    }
                    return LogicFlow::Continue;
                }
                uint16_t q = ax / val;
                uint16_t r = ax % val;

                if (q > 0xFF) {
                    if (!state->hooks.on_interrupt(state, 0)) {
                        state->fault_vector = 0;  // #DE
                        state->status = EmuStatus::Fault;
                        return LogicFlow::ExitOnCurrentEIP;
                    }
                    return LogicFlow::Continue;
                }

                uint32_t* rax = GetRegPtr(state, EAX);
                *rax = (*rax & 0xFFFF0000) | (r << 8) | (q & 0xFF);
            } else if constexpr (sizeof(T) == 2) {  // DX:AX / r/m16
                uint32_t dx_ax = ((uint32_t)(GetReg(state, EDX) & 0xFFFF) << 16) | (GetReg(state, EAX) & 0xFFFF);
                if (val == 0) {
                    if (!state->hooks.on_interrupt(state, 0)) {
                        state->fault_vector = 0;  // #DE
                        state->status = EmuStatus::Fault;
                        return LogicFlow::ExitOnCurrentEIP;
                    }
                    return LogicFlow::Continue;
                }
                uint32_t q = dx_ax / val;
                uint32_t r = dx_ax % val;

                if (q > 0xFFFF) {
                    if (!state->hooks.on_interrupt(state, 0)) {
                        state->fault_vector = 0;  // #DE
                        state->status = EmuStatus::Fault;
                        return LogicFlow::ExitOnCurrentEIP;
                    }
                    return LogicFlow::Continue;
                }

                uint32_t* rax = GetRegPtr(state, EAX);
                uint32_t* rdx = GetRegPtr(state, EDX);
                *rax = (*rax & 0xFFFF0000) | (q & 0xFFFF);
                *rdx = (*rdx & 0xFFFF0000) | (r & 0xFFFF);
            } else {  // EDX:EAX / r/m32
                uint64_t edx_eax = ((uint64_t)GetReg(state, EDX) << 32) | GetReg(state, EAX);
                if (val == 0) {
                    if (!state->hooks.on_interrupt(state, 0)) {
                        state->fault_vector = 0;  // #DE
                        state->status = EmuStatus::Fault;
                        return LogicFlow::ExitOnCurrentEIP;
                    }
                    return LogicFlow::Continue;
                }
                uint64_t q = edx_eax / val;
                uint64_t r = edx_eax % val;

                if (q > 0xFFFFFFFF) {
                    if (!state->hooks.on_interrupt(state, 0)) {
                        state->fault_vector = 0;  // #DE
                        state->status = EmuStatus::Fault;
                        return LogicFlow::ExitOnCurrentEIP;
                    }
                    return LogicFlow::Continue;
                }

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
                        state->fault_vector = 0;  // #DE
                        state->status = EmuStatus::Fault;
                        return LogicFlow::ExitOnCurrentEIP;
                    }
                    return LogicFlow::Continue;
                }

                int16_t q = ax / v;
                int16_t r = ax % v;

                if (q > 127 || q < -128) {
                    if (!state->hooks.on_interrupt(state, 0)) {
                        state->fault_vector = 0;  // #DE
                        state->status = EmuStatus::Fault;
                        return LogicFlow::ExitOnCurrentEIP;
                    }
                    return LogicFlow::Continue;
                }

                uint32_t* rax = GetRegPtr(state, EAX);
                *rax = (*rax & 0xFFFF0000) | ((uint8_t)r << 8) | ((uint8_t)q);
            } else if constexpr (sizeof(T) == 2) {  // DX:AX / r/m16
                int32_t dx_ax =
                    (int32_t)(((uint32_t)(GetReg(state, EDX) & 0xFFFF) << 16) | (GetReg(state, EAX) & 0xFFFF));
                int16_t v = (int16_t)val;
                if (v == 0) {
                    if (!state->hooks.on_interrupt(state, 0)) {
                        state->fault_vector = 0;  // #DE
                        state->status = EmuStatus::Fault;
                        return LogicFlow::ExitOnCurrentEIP;
                    }
                    return LogicFlow::Continue;
                }
                int32_t q = dx_ax / v;
                int32_t r = dx_ax % v;

                if (q > 32767 || q < -32768) {
                    if (!state->hooks.on_interrupt(state, 0)) {
                        state->fault_vector = 0;  // #DE
                        state->status = EmuStatus::Fault;
                        return LogicFlow::ExitOnCurrentEIP;
                    }
                    return LogicFlow::Continue;
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
                        state->fault_vector = 0;  // #DE
                        state->status = EmuStatus::Fault;
                        return LogicFlow::ExitOnCurrentEIP;
                    }
                    return LogicFlow::Continue;
                }
                int64_t q = edx_eax / v;
                int64_t r = edx_eax % v;

                if (q > 2147483647LL || q < -2147483648LL) {
                    if (!state->hooks.on_interrupt(state, 0)) {
                        state->fault_vector = 0;  // #DE
                        state->status = EmuStatus::Fault;
                        return LogicFlow::ExitOnCurrentEIP;
                    }
                    return LogicFlow::Continue;
                }

                SetReg(state, EAX, (uint32_t)q);
                SetReg(state, EDX, (uint32_t)r);
            }
            break;
        }
        default:
            if (!state->hooks.on_invalid_opcode(state)) {
                state->status = EmuStatus::Fault;
                state->fault_vector = 6;
            }
            if (state->status == EmuStatus::Fault) return LogicFlow::ExitOnCurrentEIP;
            return LogicFlow::Continue;
    }
    return LogicFlow::Continue;
}

// Template Helper for Group 4 (INC, DEC)
template <typename T, bool UpdateFlags = true, uint8_t FixedSubOp = 0xFF>
LogicFlow Helper_Group4(EmuState* state, DecodedOp* op, T val, mem::MicroTLB* utlb) {
    uint8_t subop;
    if constexpr (FixedSubOp != 0xFF) {
        subop = FixedSubOp;
    } else {
        subop = (op->modrm >> 3) & 7;
    }

    uint32_t old_cf = state->ctx.eflags & CF_MASK;

    switch (subop) {
        case 0:  // INC
        {
            T res = AluAdd<T, UpdateFlags>(state, val, (T)1);
            if constexpr (UpdateFlags) state->ctx.eflags = (state->ctx.eflags & ~CF_MASK) | old_cf;

            if constexpr (sizeof(T) == 1) {
                if (!WriteModRM<uint8_t, OpOnTLBMiss::Retry>(state, op, (uint8_t)res, utlb))
                    return LogicFlow::RetryMemoryOp;
            } else if constexpr (sizeof(T) == 2) {
                if (!WriteModRM<uint16_t, OpOnTLBMiss::Retry>(state, op, (uint16_t)res, utlb))
                    return LogicFlow::RetryMemoryOp;
            } else {
                if (!WriteModRM<uint32_t, OpOnTLBMiss::Retry>(state, op, (uint32_t)res, utlb))
                    return LogicFlow::RetryMemoryOp;
            }
            break;
        }
        case 1:  // DEC
        {
            T res = AluSub<T, UpdateFlags>(state, val, (T)1);
            if constexpr (UpdateFlags) state->ctx.eflags = (state->ctx.eflags & ~CF_MASK) | old_cf;

            if constexpr (sizeof(T) == 1) {
                if (!WriteModRM<uint8_t, OpOnTLBMiss::Retry>(state, op, (uint8_t)res, utlb))
                    return LogicFlow::RetryMemoryOp;
            } else if constexpr (sizeof(T) == 2) {
                if (!WriteModRM<uint16_t, OpOnTLBMiss::Retry>(state, op, (uint16_t)res, utlb))
                    return LogicFlow::RetryMemoryOp;
            } else {
                if (!WriteModRM<uint32_t, OpOnTLBMiss::Retry>(state, op, (uint32_t)res, utlb))
                    return LogicFlow::RetryMemoryOp;
            }
            break;
        }
        default:
            if (!state->hooks.on_invalid_opcode(state)) {
                state->status = EmuStatus::Fault;
                state->fault_vector = 6;
            }
            if (state->status == EmuStatus::Fault) return LogicFlow::ExitOnCurrentEIP;
    }
    return LogicFlow::Continue;
}

// Template Helper for Group 5 (INC, DEC, CALL, JMP, PUSH)
template <typename T, bool UpdateFlags = true, uint8_t FixedSubOp = 0xFF>
LogicFlow Helper_Group5(EmuState* state, DecodedOp* op, T val, mem::MicroTLB* utlb) {
    uint8_t subop;
    if constexpr (FixedSubOp != 0xFF) {
        subop = FixedSubOp;
    } else {
        subop = (op->modrm >> 3) & 7;
    }

    switch (subop) {
        case 0:  // INC Ev
        {
            uint32_t old_cf = state->ctx.eflags & CF_MASK;
            T res = AluAdd<T, UpdateFlags>(state, val, 1);
            if constexpr (UpdateFlags) state->ctx.eflags = (state->ctx.eflags & ~CF_MASK) | old_cf;

            if constexpr (sizeof(T) == 2) {
                if (!WriteModRM<uint16_t, OpOnTLBMiss::Blocking>(state, op, res, utlb))
                    return LogicFlow::ExitOnCurrentEIP;
            } else {
                if (!WriteModRM<uint32_t, OpOnTLBMiss::Blocking>(state, op, res, utlb))
                    return LogicFlow::ExitOnCurrentEIP;
            }
            break;
        }
        case 1:  // DEC Ev
        {
            uint32_t old_cf = state->ctx.eflags & CF_MASK;
            T res = AluSub<T, UpdateFlags>(state, val, 1);
            if constexpr (UpdateFlags) state->ctx.eflags = (state->ctx.eflags & ~CF_MASK) | old_cf;

            if constexpr (sizeof(T) == 2) {
                if (!WriteModRM<uint16_t, OpOnTLBMiss::Blocking>(state, op, res, utlb))
                    return LogicFlow::ExitOnCurrentEIP;
            } else {
                if (!WriteModRM<uint32_t, OpOnTLBMiss::Blocking>(state, op, res, utlb))
                    return LogicFlow::ExitOnCurrentEIP;
            }
            break;
        }
        case 2:  // CALL Ev (Near Indirect)
        case 4:  // JMP Ev (Near Indirect)
        case 6:  // PUSH Ev
        {
            // subop 2, 4, 6 do not update flags by themselves
            if (subop == 2) {  // Call
                if constexpr (sizeof(T) == 2) {
                    uint32_t esp = GetReg(state, ESP) - 2;
                    if (!WriteMem<uint16_t, OpOnTLBMiss::Blocking>(state, esp, (uint16_t)op->next_eip, utlb, op))
                        return LogicFlow::ExitOnCurrentEIP;
                    SetReg(state, ESP, esp);
                    op->branch_target = val & 0xFFFF;
                } else {
                    uint32_t esp = GetReg(state, ESP) - 4;
                    if (!WriteMem<uint32_t, OpOnTLBMiss::Blocking>(state, esp, op->next_eip, utlb, op))
                        return LogicFlow::ExitOnCurrentEIP;
                    SetReg(state, ESP, esp);
                    op->branch_target = val;
                }
            } else if (subop == 4) {  // Jmp
                if constexpr (sizeof(T) == 2)
                    op->branch_target = val & 0xFFFF;
                else
                    op->branch_target = val;
            } else if (subop == 6) {  // Push
                if constexpr (sizeof(T) == 2) {
                    uint32_t esp = GetReg(state, ESP) - 2;
                    if (!WriteMem<uint16_t, OpOnTLBMiss::Blocking>(state, esp, (uint16_t)val, utlb, op))
                        return LogicFlow::ExitOnCurrentEIP;
                    SetReg(state, ESP, esp);
                } else {
                    uint32_t esp = GetReg(state, ESP) - 4;
                    if (!WriteMem<uint32_t, OpOnTLBMiss::Blocking>(state, esp, val, utlb, op))
                        return LogicFlow::ExitOnCurrentEIP;
                    SetReg(state, ESP, esp);
                }
            }
            break;
        }
        default:
            if (!state->hooks.on_invalid_opcode(state)) {
                state->status = EmuStatus::Fault;
                state->fault_vector = 6;
            }
            if (state->status == EmuStatus::Fault) return LogicFlow::ExitOnCurrentEIP;
            break;
    }
    return LogicFlow::Continue;
}

}  // namespace fiberish
