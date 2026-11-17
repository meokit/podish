#pragma once
// Basic Data Movement
// Auto-generated from ops.cpp refactoring

#include <simde/x86/sse.h>

#include "../dispatch.h"
#include "../exec_utils.h"
#include "../ops.h"
#include "../state.h"
#include "ops_data_mov.h"

namespace fiberish {

// ------------------------------------------------------------------------------------------------
// Basic MOV / XCHG
// ------------------------------------------------------------------------------------------------

enum class AddrNoIndexSpec : uint8_t {
    Dynamic = 0,
    NoIndex,
};

enum class AddrBaseRegSpec : int8_t {
    Dynamic = -1,
    None = -2,
    Eax = EAX,
    Ecx = ECX,
    Edx = EDX,
    Ebx = EBX,
    Esp = ESP,
    Ebp = EBP,
    Esi = ESI,
    Edi = EDI,
};

enum class AddrSegmentSpec : int8_t {
    General = -1,
    None = 0,
    Es = 1,
    Cs = 2,
    Ss = 3,
    Ds = 4,
    Fs = 5,
    Gs = 6,
};

template <AddrNoIndexSpec NoIndexSpec = AddrNoIndexSpec::Dynamic,
          AddrBaseRegSpec BaseRegSpec = AddrBaseRegSpec::Dynamic,
          AddrSegmentSpec SegmentSpec = AddrSegmentSpec::General>
FORCE_INLINE uint32_t ComputeLinearAddressSpecialized(EmuState* state, const DecodedOp* op) {
    const auto* ext = GetExt(op);
    const uint32_t ea_desc = ext->data.ea_desc;
    uint32_t addr = ext->data.disp;

    if constexpr (BaseRegSpec == AddrBaseRegSpec::Dynamic) {
        const uintptr_t regs_base = (uintptr_t)state->ctx.regs;
        addr += *(const uint32_t*)(regs_base + memdesc::BaseOffset(ea_desc));
    } else if constexpr (BaseRegSpec != AddrBaseRegSpec::None) {
        addr += state->ctx.regs[static_cast<int>(BaseRegSpec)];
    }

    if constexpr (NoIndexSpec == AddrNoIndexSpec::Dynamic) {
        const uintptr_t regs_base = (uintptr_t)state->ctx.regs;
        const uint32_t index = *(const uint32_t*)(regs_base + memdesc::IndexOffset(ea_desc));
        addr += index << memdesc::Scale(ea_desc);
    }

    if (op->prefixes.flags.addrsize) addr &= 0xFFFFu;

    if constexpr (SegmentSpec == AddrSegmentSpec::General) {
        addr += GetMemSegmentBase(state, op);
    } else if constexpr (SegmentSpec == AddrSegmentSpec::None) {
        // "NoSegment" means no override, not an architectural zero base.
        // Keep specialized handlers correct if a caller supplies non-flat DS/SS.
        if constexpr (BaseRegSpec == AddrBaseRegSpec::Dynamic) {
            addr += GetMemSegmentBase(state, op);
        } else {
            constexpr int segment = (BaseRegSpec == AddrBaseRegSpec::Esp || BaseRegSpec == AddrBaseRegSpec::Ebp) ? 3 : 4;
            addr += state->ctx.seg_base[segment];
        }
    } else if constexpr (SegmentSpec != AddrSegmentSpec::None) {
        addr += state->ctx.seg_base[static_cast<int>(SegmentSpec)];
    }

    return addr;
}

template <Specialized SrcSpec = Specialized::None, Specialized DstSpec = Specialized::None>
FORCE_INLINE LogicFlow OpMov_EvGv_Reg_Internal(LogicFuncParams) {
    // Specialized: MOV r32, r32
    // Dst = Src
    uint32_t val;

    // Read Source (ModRM.Reg)
    if constexpr (SrcSpec >= Specialized::Reg0 && SrcSpec <= Specialized::Reg7) {
        constexpr uint8_t FixedSrc = (uint8_t)SrcSpec - (uint8_t)Specialized::Reg0;
        if constexpr (FixedSrc == 0)
            val = state->ctx.regs[0];
        else if constexpr (FixedSrc == 1)
            val = state->ctx.regs[1];
        else if constexpr (FixedSrc == 2)
            val = state->ctx.regs[2];
        else if constexpr (FixedSrc == 3)
            val = state->ctx.regs[3];
        else if constexpr (FixedSrc == 4)
            val = state->ctx.regs[4];
        else if constexpr (FixedSrc == 5)
            val = state->ctx.regs[5];
        else if constexpr (FixedSrc == 6)
            val = state->ctx.regs[6];
        else if constexpr (FixedSrc == 7)
            val = state->ctx.regs[7];
    } else {
        uint8_t src = (op->modrm >> 3) & 7;
        val = GetReg(state, src);
    }

    // Write Destination (ModRM.RM)
    if constexpr (DstSpec >= Specialized::Reg0 && DstSpec <= Specialized::Reg7) {
        constexpr uint8_t FixedDst = (uint8_t)DstSpec - (uint8_t)Specialized::Reg0;
        if constexpr (FixedDst == 0)
            state->ctx.regs[0] = val;
        else if constexpr (FixedDst == 1)
            state->ctx.regs[1] = val;
        else if constexpr (FixedDst == 2)
            state->ctx.regs[2] = val;
        else if constexpr (FixedDst == 3)
            state->ctx.regs[3] = val;
        else if constexpr (FixedDst == 4)
            state->ctx.regs[4] = val;
        else if constexpr (FixedDst == 5)
            state->ctx.regs[5] = val;
        else if constexpr (FixedDst == 6)
            state->ctx.regs[6] = val;
        else if constexpr (FixedDst == 7)
            state->ctx.regs[7] = val;
    } else {
        uint8_t dst = op->modrm & 7;
        SetReg(state, dst, val);
    }
    return LogicFlow::Continue;
}

template <Specialized S = Specialized::None, AddrNoIndexSpec NoIndexSpec = AddrNoIndexSpec::Dynamic,
          AddrBaseRegSpec BaseRegSpec = AddrBaseRegSpec::Dynamic,
          AddrSegmentSpec SegmentSpec = AddrSegmentSpec::General>
FORCE_INLINE LogicFlow OpMov_EvGv_Mem_Internal(LogicFuncParams) {
    // Specialized: MOV [mem], r32
    // ModRM.Reg is the Source Register
    uint32_t val;
    if constexpr (S >= Specialized::Reg0 && S <= Specialized::Reg7) {
        constexpr uint8_t FixedReg = (uint8_t)S - (uint8_t)Specialized::Reg0;
        if constexpr (FixedReg == 0)
            val = state->ctx.regs[0];
        else if constexpr (FixedReg == 1)
            val = state->ctx.regs[1];
        else if constexpr (FixedReg == 2)
            val = state->ctx.regs[2];
        else if constexpr (FixedReg == 3)
            val = state->ctx.regs[3];
        else if constexpr (FixedReg == 4)
            val = state->ctx.regs[4];
        else if constexpr (FixedReg == 5)
            val = state->ctx.regs[5];
        else if constexpr (FixedReg == 6)
            val = state->ctx.regs[6];
        else if constexpr (FixedReg == 7)
            val = state->ctx.regs[7];
    } else {
        uint8_t reg = (op->modrm >> 3) & 7;
        val = GetReg(state, reg);
    }

    uint32_t addr = ComputeLinearAddressSpecialized<NoIndexSpec, BaseRegSpec, SegmentSpec>(state, op);
    // Restart on TLB Miss
    if (!WriteMem<uint32_t, OpOnTLBMiss::Retry>(state, addr, val, utlb, op)) return LogicFlow::RetryMemoryOp;
    return LogicFlow::Continue;
}

template <Specialized DstSpec = Specialized::None, Specialized SrcSpec = Specialized::None>
FORCE_INLINE LogicFlow OpMov_GvEv_Reg_Internal(LogicFuncParams) {
    // Specialized: MOV r32, r32
    // Dst = Src
    uint32_t val;

    // Read Source
    if constexpr (SrcSpec >= Specialized::Reg0 && SrcSpec <= Specialized::Reg7) {
        constexpr uint8_t FixedSrc = (uint8_t)SrcSpec - (uint8_t)Specialized::Reg0;
        if constexpr (FixedSrc == 0)
            val = state->ctx.regs[0];
        else if constexpr (FixedSrc == 1)
            val = state->ctx.regs[1];
        else if constexpr (FixedSrc == 2)
            val = state->ctx.regs[2];
        else if constexpr (FixedSrc == 3)
            val = state->ctx.regs[3];
        else if constexpr (FixedSrc == 4)
            val = state->ctx.regs[4];
        else if constexpr (FixedSrc == 5)
            val = state->ctx.regs[5];
        else if constexpr (FixedSrc == 6)
            val = state->ctx.regs[6];
        else if constexpr (FixedSrc == 7)
            val = state->ctx.regs[7];
    } else {
        uint8_t src = op->modrm & 7;
        val = GetReg(state, src);
    }

    // Write Destination
    if constexpr (DstSpec >= Specialized::Reg0 && DstSpec <= Specialized::Reg7) {
        constexpr uint8_t FixedDst = (uint8_t)DstSpec - (uint8_t)Specialized::Reg0;
        if constexpr (FixedDst == 0)
            state->ctx.regs[0] = val;
        else if constexpr (FixedDst == 1)
            state->ctx.regs[1] = val;
        else if constexpr (FixedDst == 2)
            state->ctx.regs[2] = val;
        else if constexpr (FixedDst == 3)
            state->ctx.regs[3] = val;
        else if constexpr (FixedDst == 4)
            state->ctx.regs[4] = val;
        else if constexpr (FixedDst == 5)
            state->ctx.regs[5] = val;
        else if constexpr (FixedDst == 6)
            state->ctx.regs[6] = val;
        else if constexpr (FixedDst == 7)
            state->ctx.regs[7] = val;
    } else {
        uint8_t dst = (op->modrm >> 3) & 7;
        SetReg(state, dst, val);
    }
    return LogicFlow::Continue;
}

template <Specialized S = Specialized::None>
FORCE_INLINE LogicFlow OpMov_OaMa_Internal(LogicFuncParams) {
    // A3: MOV m32, EAX
    uint32_t addr = ComputeLinearAddress(state, op);
    uint32_t val = GetReg(state, EAX);
    // Restart on TLB Miss
    if (!WriteMem<uint32_t, OpOnTLBMiss::Retry>(state, addr, val, utlb, op)) return LogicFlow::RetryMemoryOp;
    return LogicFlow::Continue;
}

template <Specialized S = Specialized::None, AddrNoIndexSpec NoIndexSpec = AddrNoIndexSpec::Dynamic,
          AddrBaseRegSpec BaseRegSpec = AddrBaseRegSpec::Dynamic,
          AddrSegmentSpec SegmentSpec = AddrSegmentSpec::General>
FORCE_INLINE LogicFlow OpMov_GvEv_Mem_Internal(LogicFuncParams) {
    // Specialized: MOV r32, [mem]
    uint32_t addr = ComputeLinearAddressSpecialized<NoIndexSpec, BaseRegSpec, SegmentSpec>(state, op);
    // Restart on TLB Miss
    auto val_res = ReadMem<uint32_t, OpOnTLBMiss::Restart>(state, addr, utlb, op);
    if (!val_res) return LogicFlow::RestartMemoryOp;
    uint32_t val = *val_res;

    if constexpr (S >= Specialized::Reg0 && S <= Specialized::Reg7) {
        constexpr uint8_t FixedReg = (uint8_t)S - (uint8_t)Specialized::Reg0;
        if constexpr (FixedReg == 0)
            state->ctx.regs[0] = val;
        else if constexpr (FixedReg == 1)
            state->ctx.regs[1] = val;
        else if constexpr (FixedReg == 2)
            state->ctx.regs[2] = val;
        else if constexpr (FixedReg == 3)
            state->ctx.regs[3] = val;
        else if constexpr (FixedReg == 4)
            state->ctx.regs[4] = val;
        else if constexpr (FixedReg == 5)
            state->ctx.regs[5] = val;
        else if constexpr (FixedReg == 6)
            state->ctx.regs[6] = val;
        else if constexpr (FixedReg == 7)
            state->ctx.regs[7] = val;
    } else {
        uint8_t reg = (op->modrm >> 3) & 7;
        SetReg(state, reg, val);
    }
    return LogicFlow::Continue;
}

namespace op {

FORCE_INLINE LogicFlow OpMov_EvGv(LogicFuncParams) {
    // MOV r/m16/32, r16/32 (0x89)
    uint8_t reg = (op->modrm >> 3) & 7;
    if (op->prefixes.flags.opsize) {
        uint16_t val = (uint16_t)GetReg(state, reg);
        if (!WriteModRM<uint16_t, OpOnTLBMiss::Retry>(state, op, val, utlb)) return LogicFlow::RetryMemoryOp;
    } else {
        uint32_t val = GetReg(state, reg);
        if (!WriteModRM<uint32_t, OpOnTLBMiss::Retry>(state, op, val, utlb)) return LogicFlow::RetryMemoryOp;
    }
    return LogicFlow::Continue;
}

// Named wrappers for OpMov_EvGv_Reg (0x89 Mod=3) - Specializing Source
#define MOV_EVGV_REG_WRAPPER(RegName, Spec)                        \
    FORCE_INLINE LogicFlow OpMov_EvGv_##RegName(LogicFuncParams) { \
        return OpMov_EvGv_Reg_Internal<Spec>(LogicPassParams);     \
    }

MOV_EVGV_REG_WRAPPER(Eax, Specialized::RegEax)
MOV_EVGV_REG_WRAPPER(Ecx, Specialized::RegEcx)
MOV_EVGV_REG_WRAPPER(Edx, Specialized::RegEdx)
MOV_EVGV_REG_WRAPPER(Ebx, Specialized::RegEbx)
MOV_EVGV_REG_WRAPPER(Esp, Specialized::RegEsp)
MOV_EVGV_REG_WRAPPER(Ebp, Specialized::RegEbp)
MOV_EVGV_REG_WRAPPER(Esi, Specialized::RegEsi)
MOV_EVGV_REG_WRAPPER(Edi, Specialized::RegEdi)

FORCE_INLINE LogicFlow OpMov_EvGv_Reg(LogicFuncParams) { return OpMov_EvGv_Reg_Internal<>(LogicPassParams); }

// Named wrappers for OpMov_EvGv_Mem (Store) - Specializing Source
#define MOV_STORE_WRAPPER(RegName, Spec)                            \
    FORCE_INLINE LogicFlow OpMov_Store_##RegName(LogicFuncParams) { \
        return OpMov_EvGv_Mem_Internal<Spec>(LogicPassParams);      \
    }

#define MOV_STORE_ADDRSPEC_WRAPPER(RegName, Spec, Suffix, NoIndexValue, BaseRegValue, SegmentValue)      \
    FORCE_INLINE LogicFlow OpMov_Store_##RegName##_##Suffix(LogicFuncParams) {                           \
        return OpMov_EvGv_Mem_Internal<Spec, NoIndexValue, BaseRegValue, SegmentValue>(LogicPassParams); \
    }

MOV_STORE_WRAPPER(Eax, Specialized::RegEax)
MOV_STORE_WRAPPER(Ecx, Specialized::RegEcx)
MOV_STORE_WRAPPER(Edx, Specialized::RegEdx)
MOV_STORE_WRAPPER(Ebx, Specialized::RegEbx)
MOV_STORE_WRAPPER(Esp, Specialized::RegEsp)
MOV_STORE_WRAPPER(Ebp, Specialized::RegEbp)
MOV_STORE_WRAPPER(Esi, Specialized::RegEsi)
MOV_STORE_WRAPPER(Edi, Specialized::RegEdi)

MOV_STORE_ADDRSPEC_WRAPPER(Eax, Specialized::RegEax, EspBaseNoIndexNoSegment, AddrNoIndexSpec::NoIndex,
                           AddrBaseRegSpec::Esp, AddrSegmentSpec::None)
MOV_STORE_ADDRSPEC_WRAPPER(Ecx, Specialized::RegEcx, EspBaseNoIndexNoSegment, AddrNoIndexSpec::NoIndex,
                           AddrBaseRegSpec::Esp, AddrSegmentSpec::None)
MOV_STORE_ADDRSPEC_WRAPPER(Edx, Specialized::RegEdx, EspBaseNoIndexNoSegment, AddrNoIndexSpec::NoIndex,
                           AddrBaseRegSpec::Esp, AddrSegmentSpec::None)
MOV_STORE_ADDRSPEC_WRAPPER(Ebx, Specialized::RegEbx, EspBaseNoIndexNoSegment, AddrNoIndexSpec::NoIndex,
                           AddrBaseRegSpec::Esp, AddrSegmentSpec::None)
MOV_STORE_ADDRSPEC_WRAPPER(Esp, Specialized::RegEsp, EspBaseNoIndexNoSegment, AddrNoIndexSpec::NoIndex,
                           AddrBaseRegSpec::Esp, AddrSegmentSpec::None)
MOV_STORE_ADDRSPEC_WRAPPER(Ebp, Specialized::RegEbp, EspBaseNoIndexNoSegment, AddrNoIndexSpec::NoIndex,
                           AddrBaseRegSpec::Esp, AddrSegmentSpec::None)
MOV_STORE_ADDRSPEC_WRAPPER(Esi, Specialized::RegEsi, EspBaseNoIndexNoSegment, AddrNoIndexSpec::NoIndex,
                           AddrBaseRegSpec::Esp, AddrSegmentSpec::None)
MOV_STORE_ADDRSPEC_WRAPPER(Edi, Specialized::RegEdi, EspBaseNoIndexNoSegment, AddrNoIndexSpec::NoIndex,
                           AddrBaseRegSpec::Esp, AddrSegmentSpec::None)
MOV_STORE_ADDRSPEC_WRAPPER(Eax, Specialized::RegEax, EaxBaseNoIndexNoSegment, AddrNoIndexSpec::NoIndex,
                           AddrBaseRegSpec::Eax, AddrSegmentSpec::None)
MOV_STORE_ADDRSPEC_WRAPPER(Ecx, Specialized::RegEcx, EaxBaseNoIndexNoSegment, AddrNoIndexSpec::NoIndex,
                           AddrBaseRegSpec::Eax, AddrSegmentSpec::None)
MOV_STORE_ADDRSPEC_WRAPPER(Edx, Specialized::RegEdx, EaxBaseNoIndexNoSegment, AddrNoIndexSpec::NoIndex,
                           AddrBaseRegSpec::Eax, AddrSegmentSpec::None)
MOV_STORE_ADDRSPEC_WRAPPER(Ebx, Specialized::RegEbx, EaxBaseNoIndexNoSegment, AddrNoIndexSpec::NoIndex,
                           AddrBaseRegSpec::Eax, AddrSegmentSpec::None)
MOV_STORE_ADDRSPEC_WRAPPER(Esp, Specialized::RegEsp, EaxBaseNoIndexNoSegment, AddrNoIndexSpec::NoIndex,
                           AddrBaseRegSpec::Eax, AddrSegmentSpec::None)
MOV_STORE_ADDRSPEC_WRAPPER(Ebp, Specialized::RegEbp, EaxBaseNoIndexNoSegment, AddrNoIndexSpec::NoIndex,
                           AddrBaseRegSpec::Eax, AddrSegmentSpec::None)
MOV_STORE_ADDRSPEC_WRAPPER(Esi, Specialized::RegEsi, EaxBaseNoIndexNoSegment, AddrNoIndexSpec::NoIndex,
                           AddrBaseRegSpec::Eax, AddrSegmentSpec::None)
MOV_STORE_ADDRSPEC_WRAPPER(Edi, Specialized::RegEdi, EaxBaseNoIndexNoSegment, AddrNoIndexSpec::NoIndex,
                           AddrBaseRegSpec::Eax, AddrSegmentSpec::None)
MOV_STORE_ADDRSPEC_WRAPPER(Eax, Specialized::RegEax, EsiBaseNoIndexNoSegment, AddrNoIndexSpec::NoIndex,
                           AddrBaseRegSpec::Esi, AddrSegmentSpec::None)
MOV_STORE_ADDRSPEC_WRAPPER(Ecx, Specialized::RegEcx, EsiBaseNoIndexNoSegment, AddrNoIndexSpec::NoIndex,
                           AddrBaseRegSpec::Esi, AddrSegmentSpec::None)
MOV_STORE_ADDRSPEC_WRAPPER(Edx, Specialized::RegEdx, EsiBaseNoIndexNoSegment, AddrNoIndexSpec::NoIndex,
                           AddrBaseRegSpec::Esi, AddrSegmentSpec::None)
MOV_STORE_ADDRSPEC_WRAPPER(Ebx, Specialized::RegEbx, EsiBaseNoIndexNoSegment, AddrNoIndexSpec::NoIndex,
                           AddrBaseRegSpec::Esi, AddrSegmentSpec::None)
MOV_STORE_ADDRSPEC_WRAPPER(Esp, Specialized::RegEsp, EsiBaseNoIndexNoSegment, AddrNoIndexSpec::NoIndex,
                           AddrBaseRegSpec::Esi, AddrSegmentSpec::None)
MOV_STORE_ADDRSPEC_WRAPPER(Ebp, Specialized::RegEbp, EsiBaseNoIndexNoSegment, AddrNoIndexSpec::NoIndex,
                           AddrBaseRegSpec::Esi, AddrSegmentSpec::None)
MOV_STORE_ADDRSPEC_WRAPPER(Esi, Specialized::RegEsi, EsiBaseNoIndexNoSegment, AddrNoIndexSpec::NoIndex,
                           AddrBaseRegSpec::Esi, AddrSegmentSpec::None)
MOV_STORE_ADDRSPEC_WRAPPER(Edi, Specialized::RegEdi, EsiBaseNoIndexNoSegment, AddrNoIndexSpec::NoIndex,
                           AddrBaseRegSpec::Esi, AddrSegmentSpec::None)

FORCE_INLINE LogicFlow OpMov_EvGv_Mem(LogicFuncParams) { return OpMov_EvGv_Mem_Internal<>(LogicPassParams); }

FORCE_INLINE LogicFlow OpMov_GvEv(LogicFuncParams) {
    // MOV r16/32, r/m16/32 (0x8B)
    uint8_t reg = (op->modrm >> 3) & 7;
    if (op->prefixes.flags.opsize) {
        auto val_res = ReadModRM<uint16_t, OpOnTLBMiss::Restart>(state, op, utlb);
        if (!val_res) return LogicFlow::RestartMemoryOp;
        uint16_t val = *val_res;
        SetReg(state, reg, (GetReg(state, reg) & 0xFFFF0000) | val);
    } else {
        auto val_res = ReadModRM<uint32_t, OpOnTLBMiss::Restart>(state, op, utlb);
        if (!val_res) return LogicFlow::RestartMemoryOp;
        uint32_t val = *val_res;
        SetReg(state, reg, val);
    }
    return LogicFlow::Continue;
}

// Named wrappers for profiling visibility
FORCE_INLINE LogicFlow OpMov_Ebp_Esp(LogicFuncParams) {
    return OpMov_GvEv_Reg_Internal<Specialized::RegEbp, Specialized::RegEsp>(LogicPassParams);
}
FORCE_INLINE LogicFlow OpMov_Eax_Eax(LogicFuncParams) {
    return OpMov_GvEv_Reg_Internal<Specialized::RegEax, Specialized::RegEax>(LogicPassParams);
}
FORCE_INLINE LogicFlow OpMov_Ecx_Eax(LogicFuncParams) {
    return OpMov_GvEv_Reg_Internal<Specialized::RegEcx, Specialized::RegEax>(LogicPassParams);
}
FORCE_INLINE LogicFlow OpMov_Edx_Eax(LogicFuncParams) {
    return OpMov_GvEv_Reg_Internal<Specialized::RegEdx, Specialized::RegEax>(LogicPassParams);
}
FORCE_INLINE LogicFlow OpMov_Esi_Eax(LogicFuncParams) {
    return OpMov_GvEv_Reg_Internal<Specialized::RegEsi, Specialized::RegEax>(LogicPassParams);
}
FORCE_INLINE LogicFlow OpMov_Edi_Eax(LogicFuncParams) {
    return OpMov_GvEv_Reg_Internal<Specialized::RegEdi, Specialized::RegEax>(LogicPassParams);
}

// Generic fallback
FORCE_INLINE LogicFlow OpMov_GvEv_Reg(LogicFuncParams) { return OpMov_GvEv_Reg_Internal<>(LogicPassParams); }

// Named wrappers for OpMov_GvEv_Mem (Load) - Specializing Destination
#define MOV_LOAD_WRAPPER(RegName, Spec)                            \
    FORCE_INLINE LogicFlow OpMov_Load_##RegName(LogicFuncParams) { \
        return OpMov_GvEv_Mem_Internal<Spec>(LogicPassParams);     \
    }

#define MOV_LOAD_ADDRSPEC_WRAPPER(RegName, Spec, Suffix, NoIndexValue, BaseRegValue, SegmentValue)       \
    FORCE_INLINE LogicFlow OpMov_Load_##RegName##_##Suffix(LogicFuncParams) {                            \
        return OpMov_GvEv_Mem_Internal<Spec, NoIndexValue, BaseRegValue, SegmentValue>(LogicPassParams); \
    }

MOV_LOAD_WRAPPER(Eax, Specialized::RegEax)
MOV_LOAD_WRAPPER(Ecx, Specialized::RegEcx)
MOV_LOAD_WRAPPER(Edx, Specialized::RegEdx)
MOV_LOAD_WRAPPER(Ebx, Specialized::RegEbx)
MOV_LOAD_WRAPPER(Esp, Specialized::RegEsp)
MOV_LOAD_WRAPPER(Ebp, Specialized::RegEbp)
MOV_LOAD_WRAPPER(Esi, Specialized::RegEsi)
MOV_LOAD_WRAPPER(Edi, Specialized::RegEdi)

MOV_LOAD_ADDRSPEC_WRAPPER(Eax, Specialized::RegEax, EspBaseNoIndexNoSegment, AddrNoIndexSpec::NoIndex,
                          AddrBaseRegSpec::Esp, AddrSegmentSpec::None)
MOV_LOAD_ADDRSPEC_WRAPPER(Ecx, Specialized::RegEcx, EspBaseNoIndexNoSegment, AddrNoIndexSpec::NoIndex,
                          AddrBaseRegSpec::Esp, AddrSegmentSpec::None)
MOV_LOAD_ADDRSPEC_WRAPPER(Edx, Specialized::RegEdx, EspBaseNoIndexNoSegment, AddrNoIndexSpec::NoIndex,
                          AddrBaseRegSpec::Esp, AddrSegmentSpec::None)
MOV_LOAD_ADDRSPEC_WRAPPER(Ebx, Specialized::RegEbx, EspBaseNoIndexNoSegment, AddrNoIndexSpec::NoIndex,
                          AddrBaseRegSpec::Esp, AddrSegmentSpec::None)
MOV_LOAD_ADDRSPEC_WRAPPER(Esp, Specialized::RegEsp, EspBaseNoIndexNoSegment, AddrNoIndexSpec::NoIndex,
                          AddrBaseRegSpec::Esp, AddrSegmentSpec::None)
MOV_LOAD_ADDRSPEC_WRAPPER(Ebp, Specialized::RegEbp, EspBaseNoIndexNoSegment, AddrNoIndexSpec::NoIndex,
                          AddrBaseRegSpec::Esp, AddrSegmentSpec::None)
MOV_LOAD_ADDRSPEC_WRAPPER(Esi, Specialized::RegEsi, EspBaseNoIndexNoSegment, AddrNoIndexSpec::NoIndex,
                          AddrBaseRegSpec::Esp, AddrSegmentSpec::None)
MOV_LOAD_ADDRSPEC_WRAPPER(Edi, Specialized::RegEdi, EspBaseNoIndexNoSegment, AddrNoIndexSpec::NoIndex,
                          AddrBaseRegSpec::Esp, AddrSegmentSpec::None)
MOV_LOAD_ADDRSPEC_WRAPPER(Eax, Specialized::RegEax, EaxBaseNoIndexNoSegment, AddrNoIndexSpec::NoIndex,
                          AddrBaseRegSpec::Eax, AddrSegmentSpec::None)
MOV_LOAD_ADDRSPEC_WRAPPER(Ecx, Specialized::RegEcx, EaxBaseNoIndexNoSegment, AddrNoIndexSpec::NoIndex,
                          AddrBaseRegSpec::Eax, AddrSegmentSpec::None)
MOV_LOAD_ADDRSPEC_WRAPPER(Edx, Specialized::RegEdx, EaxBaseNoIndexNoSegment, AddrNoIndexSpec::NoIndex,
                          AddrBaseRegSpec::Eax, AddrSegmentSpec::None)
MOV_LOAD_ADDRSPEC_WRAPPER(Ebx, Specialized::RegEbx, EaxBaseNoIndexNoSegment, AddrNoIndexSpec::NoIndex,
                          AddrBaseRegSpec::Eax, AddrSegmentSpec::None)
MOV_LOAD_ADDRSPEC_WRAPPER(Esp, Specialized::RegEsp, EaxBaseNoIndexNoSegment, AddrNoIndexSpec::NoIndex,
                          AddrBaseRegSpec::Eax, AddrSegmentSpec::None)
MOV_LOAD_ADDRSPEC_WRAPPER(Ebp, Specialized::RegEbp, EaxBaseNoIndexNoSegment, AddrNoIndexSpec::NoIndex,
                          AddrBaseRegSpec::Eax, AddrSegmentSpec::None)
MOV_LOAD_ADDRSPEC_WRAPPER(Esi, Specialized::RegEsi, EaxBaseNoIndexNoSegment, AddrNoIndexSpec::NoIndex,
                          AddrBaseRegSpec::Eax, AddrSegmentSpec::None)
MOV_LOAD_ADDRSPEC_WRAPPER(Edi, Specialized::RegEdi, EaxBaseNoIndexNoSegment, AddrNoIndexSpec::NoIndex,
                          AddrBaseRegSpec::Eax, AddrSegmentSpec::None)
MOV_LOAD_ADDRSPEC_WRAPPER(Eax, Specialized::RegEax, EsiBaseNoIndexNoSegment, AddrNoIndexSpec::NoIndex,
                          AddrBaseRegSpec::Esi, AddrSegmentSpec::None)
MOV_LOAD_ADDRSPEC_WRAPPER(Ecx, Specialized::RegEcx, EsiBaseNoIndexNoSegment, AddrNoIndexSpec::NoIndex,
                          AddrBaseRegSpec::Esi, AddrSegmentSpec::None)
MOV_LOAD_ADDRSPEC_WRAPPER(Edx, Specialized::RegEdx, EsiBaseNoIndexNoSegment, AddrNoIndexSpec::NoIndex,
                          AddrBaseRegSpec::Esi, AddrSegmentSpec::None)
MOV_LOAD_ADDRSPEC_WRAPPER(Ebx, Specialized::RegEbx, EsiBaseNoIndexNoSegment, AddrNoIndexSpec::NoIndex,
                          AddrBaseRegSpec::Esi, AddrSegmentSpec::None)
MOV_LOAD_ADDRSPEC_WRAPPER(Esp, Specialized::RegEsp, EsiBaseNoIndexNoSegment, AddrNoIndexSpec::NoIndex,
                          AddrBaseRegSpec::Esi, AddrSegmentSpec::None)
MOV_LOAD_ADDRSPEC_WRAPPER(Ebp, Specialized::RegEbp, EsiBaseNoIndexNoSegment, AddrNoIndexSpec::NoIndex,
                          AddrBaseRegSpec::Esi, AddrSegmentSpec::None)
MOV_LOAD_ADDRSPEC_WRAPPER(Esi, Specialized::RegEsi, EsiBaseNoIndexNoSegment, AddrNoIndexSpec::NoIndex,
                          AddrBaseRegSpec::Esi, AddrSegmentSpec::None)
MOV_LOAD_ADDRSPEC_WRAPPER(Edi, Specialized::RegEdi, EsiBaseNoIndexNoSegment, AddrNoIndexSpec::NoIndex,
                          AddrBaseRegSpec::Esi, AddrSegmentSpec::None)

FORCE_INLINE LogicFlow OpMov_GvEv_Mem(LogicFuncParams) { return OpMov_GvEv_Mem_Internal<>(LogicPassParams); }

FORCE_INLINE LogicFlow OpXchg_EbGb(LogicFuncParams) {
    // 86: XCHG r/m8, r8
    auto val_res = ReadModRM<uint8_t, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!val_res) return LogicFlow::RestartMemoryOp;
    uint8_t rm_val = *val_res;

    uint8_t reg = (op->modrm >> 3) & 7;
    uint8_t reg_val = GetReg8(state, reg);

    // The store must not use the retry mechanism: the register write below has
    // to happen exactly once, after the store succeeds. If it faults (page
    // fault or SMC interception), exit on the instruction start so the whole
    // instruction is re-executed instead of resuming after it.
    if (!WriteModRM<uint8_t, OpOnTLBMiss::Blocking>(state, op, reg_val, utlb)) return LogicFlow::ExitOnCurrentEIP;
    SetReg8(state, reg, rm_val);
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpMov_EbGb(LogicFuncParams) {
    // MOV r/m8, r8 (0x88)
    // Store 8-bit Reg into ModRM
    uint8_t reg = (op->modrm >> 3) & 7;
    uint8_t val = GetReg8(state, reg);
    if (!WriteModRM<uint8_t, OpOnTLBMiss::Retry>(state, op, val, utlb)) return LogicFlow::RetryMemoryOp;
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpMov_GbEb(LogicFuncParams) {
    // MOV r8, r/m8 (0x8A)
    // Load 8-bit ModRM into Reg
    auto val_res = ReadModRM<uint8_t, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!val_res) return LogicFlow::RestartMemoryOp;
    uint8_t val = *val_res;
    uint8_t reg = (op->modrm >> 3) & 7;

    // Write to 8-bit register
    uint32_t* rptr = GetRegPtr(state, reg & 3);
    uint32_t curr = *rptr;
    if (reg < 4) {
        curr = (curr & 0xFFFFFF00) | val;
    } else {
        curr = (curr & 0xFFFF00FF) | (val << 8);
    }
    *rptr = curr;
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpMov_EbIb(LogicFuncParams) {
    // MOV r/m8, imm8 (0xC6)
    uint8_t val = (uint8_t)imm;
    if (!WriteModRM<uint8_t, OpOnTLBMiss::Retry>(state, op, val, utlb)) return LogicFlow::RetryMemoryOp;
    return LogicFlow::Continue;
}

template <AddrNoIndexSpec NoIndexSpec = AddrNoIndexSpec::Dynamic,
          AddrBaseRegSpec BaseRegSpec = AddrBaseRegSpec::Dynamic,
          AddrSegmentSpec SegmentSpec = AddrSegmentSpec::General>
FORCE_INLINE LogicFlow OpMov_EvIz_Mem_Internal(LogicFuncParams) {
    const uint32_t addr = ComputeLinearAddressSpecialized<NoIndexSpec, BaseRegSpec, SegmentSpec>(state, op);
    if (op->prefixes.flags.opsize) {
        if (!WriteMem<uint16_t, OpOnTLBMiss::Retry>(state, addr, (uint16_t)imm, utlb, op))
            return LogicFlow::RetryMemoryOp;
    } else {
        if (!WriteMem<uint32_t, OpOnTLBMiss::Retry>(state, addr, (uint32_t)imm, utlb, op))
            return LogicFlow::RetryMemoryOp;
    }
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpMov_EvIz(LogicFuncParams) {
    // MOV r/m16/32, imm16/32 (0xC7)
    if (op->prefixes.flags.opsize) {
        if (!WriteModRM<uint16_t, OpOnTLBMiss::Retry>(state, op, (uint16_t)imm, utlb)) return LogicFlow::RetryMemoryOp;
    } else {
        if (!WriteModRM<uint32_t, OpOnTLBMiss::Retry>(state, op, (uint32_t)imm, utlb)) return LogicFlow::RetryMemoryOp;
    }
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpMov_EvIz_EspBaseNoIndexNoSegment(LogicFuncParams) {
    return OpMov_EvIz_Mem_Internal<AddrNoIndexSpec::NoIndex, AddrBaseRegSpec::Esp, AddrSegmentSpec::None>(
        LogicPassParams);
}

FORCE_INLINE LogicFlow OpMov_EvIz_EaxBaseNoIndexNoSegment(LogicFuncParams) {
    return OpMov_EvIz_Mem_Internal<AddrNoIndexSpec::NoIndex, AddrBaseRegSpec::Eax, AddrSegmentSpec::None>(
        LogicPassParams);
}

FORCE_INLINE LogicFlow OpMov_EvIz_EsiBaseNoIndexNoSegment(LogicFuncParams) {
    return OpMov_EvIz_Mem_Internal<AddrNoIndexSpec::NoIndex, AddrBaseRegSpec::Esi, AddrSegmentSpec::None>(
        LogicPassParams);
}

FORCE_INLINE LogicFlow OpMov_RegImm(LogicFuncParams) {
    // B8+reg: MOV r16/32, imm16/32
    uint8_t reg = op->modrm & 7;
    if (op->prefixes.flags.opsize) {
        SetReg(state, reg, (GetReg(state, reg) & 0xFFFF0000) | (uint16_t)imm);
    } else {
        SetReg(state, reg, imm);
    }
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpMov_RegImm8(LogicFuncParams) {
    // B0+reg: MOV r8, imm8
    uint8_t reg = op->modrm & 7;
    uint32_t val = imm & 0xFF;

    uint32_t* rptr = GetRegPtr(state, reg & 3);
    uint32_t curr = *rptr;

    if (reg < 4) {
        curr = (curr & 0xFFFFFF00) | val;
    } else {
        curr = (curr & 0xFFFF00FF) | (val << 8);
    }
    *rptr = curr;
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpMov_Moffs_Load_Byte(LogicFuncParams) {
    // A0: MOV AL, moffs8
    uint32_t offset = imm;
    uint32_t linear = offset + GetSegmentBase(state, op);
    auto val_res = ReadMem<uint8_t, OpOnTLBMiss::Restart>(state, linear, utlb, op);
    if (!val_res) return LogicFlow::RestartMemoryOp;
    uint32_t* rptr = GetRegPtr(state, EAX);
    *rptr = (*rptr & 0xFFFFFF00) | *val_res;
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpMov_Moffs_Load_Word(LogicFuncParams) {
    // A1: MOV EAX, moffs32
    uint32_t offset = imm;
    uint32_t linear = offset + GetSegmentBase(state, op);
    if (op->prefixes.flags.opsize) {
        auto val_res = ReadMem<uint16_t, OpOnTLBMiss::Restart>(state, linear, utlb, op);
        if (!val_res) return LogicFlow::RestartMemoryOp;
        SetReg(state, EAX, (GetReg(state, EAX) & 0xFFFF0000) | *val_res);
    } else {
        auto val_res = ReadMem<uint32_t, OpOnTLBMiss::Restart>(state, linear, utlb, op);
        if (!val_res) return LogicFlow::RestartMemoryOp;
        SetReg(state, EAX, *val_res);
    }
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpMov_Moffs_Store_Byte(LogicFuncParams) {
    // A2: MOV moffs8, AL
    uint32_t offset = imm;
    uint32_t linear = offset + GetSegmentBase(state, op);
    uint8_t val = GetReg8(state, EAX);
    if (!WriteMem<uint8_t, OpOnTLBMiss::Retry>(state, linear, val, utlb, op)) return LogicFlow::RetryMemoryOp;
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpMov_Moffs_Store_Word(LogicFuncParams) {
    // A3: MOV moffs32, EAX
    uint32_t offset = imm;
    uint32_t linear = offset + GetSegmentBase(state, op);
    if (op->prefixes.flags.opsize) {
        uint16_t val = (uint16_t)GetReg(state, EAX);
        if (!WriteMem<uint16_t, OpOnTLBMiss::Retry>(state, linear, val, utlb, op)) return LogicFlow::RetryMemoryOp;
    } else {
        uint32_t val = GetReg(state, EAX);
        if (!WriteMem<uint32_t, OpOnTLBMiss::Retry>(state, linear, val, utlb, op)) return LogicFlow::RetryMemoryOp;
    }
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpMov_Rm_Sreg(LogicFuncParams) {
    // 8C: MOV r/m16, Sreg
    // The memory destination is always 16 bits; only a register destination is
    // written as a full 32-bit GPR (zero-extended selector).
    uint16_t val = 0;
    const bool register_destination = ((op->modrm >> 6) & 3) == 3;
    if (register_destination) {
        if (!WriteModRM<uint32_t, OpOnTLBMiss::Retry>(state, op, (uint32_t)val, utlb)) return LogicFlow::RetryMemoryOp;
    } else {
        if (!WriteModRM<uint16_t, OpOnTLBMiss::Retry>(state, op, val, utlb)) return LogicFlow::RetryMemoryOp;
    }
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpMov_Sreg_Rm(LogicFuncParams) {
    // 8E: MOV Sreg, r/m16
    auto val_res = ReadModRM<uint16_t, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!val_res) return LogicFlow::RestartMemoryOp;
    (void)*val_res;
    return LogicFlow::Continue;
}

template <Specialized RegSpec = Specialized::None, Specialized SizeSpec = Specialized::None>
FORCE_INLINE LogicFlow OpMovzx_Byte_Impl(LogicFuncParams) {
    // 0F B6: MOVZX r16/32, r/m8
    auto val_res = ReadModRM<uint8_t, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!val_res) return LogicFlow::RestartMemoryOp;
    uint8_t val = *val_res;

    if constexpr (SizeSpec == Specialized::Opsize16) {
        if constexpr (RegSpec >= Specialized::Reg0 && RegSpec <= Specialized::Reg7) {
            constexpr uint8_t FixedReg = (uint8_t)RegSpec - (uint8_t)Specialized::Reg0;
            uint32_t current = state->ctx.regs[FixedReg];
            state->ctx.regs[FixedReg] = (current & 0xFFFF0000) | (uint16_t)val;
        } else {
            uint8_t reg = (op->modrm >> 3) & 7;
            SetReg(state, reg, (GetReg(state, reg) & 0xFFFF0000) | (uint16_t)val);
        }
    } else if constexpr (SizeSpec == Specialized::Opsize32) {
        if constexpr (RegSpec >= Specialized::Reg0 && RegSpec <= Specialized::Reg7) {
            constexpr uint8_t FixedReg = (uint8_t)RegSpec - (uint8_t)Specialized::Reg0;
            state->ctx.regs[FixedReg] = (uint32_t)val;
        } else {
            uint8_t reg = (op->modrm >> 3) & 7;
            SetReg(state, reg, (uint32_t)val);
        }
    } else if (op->prefixes.flags.opsize) {
        uint8_t reg = (op->modrm >> 3) & 7;
        SetReg(state, reg, (GetReg(state, reg) & 0xFFFF0000) | (uint16_t)val);
    } else {
        uint8_t reg = (op->modrm >> 3) & 7;
        SetReg(state, reg, (uint32_t)val);
    }
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpMovzx_Byte(LogicFuncParams) { return OpMovzx_Byte_Impl<>(LogicPassParams); }
FORCE_INLINE LogicFlow OpMovzx_Byte_32_Eax(LogicFuncParams) {
    return OpMovzx_Byte_Impl<Specialized::RegEax, Specialized::Opsize32>(LogicPassParams);
}
FORCE_INLINE LogicFlow OpMovzx_Byte_32_Ecx(LogicFuncParams) {
    return OpMovzx_Byte_Impl<Specialized::RegEcx, Specialized::Opsize32>(LogicPassParams);
}
FORCE_INLINE LogicFlow OpMovzx_Byte_32_Edx(LogicFuncParams) {
    return OpMovzx_Byte_Impl<Specialized::RegEdx, Specialized::Opsize32>(LogicPassParams);
}

FORCE_INLINE LogicFlow OpMovzx_Word(LogicFuncParams) {
    // 0F B7: MOVZX r32, r/m16
    auto val_res = ReadModRM<uint16_t, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!val_res) return LogicFlow::RestartMemoryOp;
    uint16_t val = *val_res;
    uint8_t reg = (op->modrm >> 3) & 7;
    if (op->prefixes.flags.opsize) {
        SetReg(state, reg, (GetReg(state, reg) & 0xFFFF0000) | val);
    } else {
        SetReg(state, reg, (uint32_t)val);
    }
    return LogicFlow::Continue;
}

template <Specialized RegSpec = Specialized::None, Specialized SizeSpec = Specialized::None>
FORCE_INLINE LogicFlow OpMovsx_Byte_Impl(LogicFuncParams) {
    // 0F BE: MOVSX r16/32, r/m8
    auto val_res = ReadModRM<uint8_t, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!val_res) return LogicFlow::RestartMemoryOp;
    int8_t val = (int8_t)*val_res;

    if constexpr (SizeSpec == Specialized::Opsize16) {
        if constexpr (RegSpec >= Specialized::Reg0 && RegSpec <= Specialized::Reg7) {
            constexpr uint8_t FixedReg = (uint8_t)RegSpec - (uint8_t)Specialized::Reg0;
            uint32_t current = state->ctx.regs[FixedReg];
            state->ctx.regs[FixedReg] = (current & 0xFFFF0000) | (uint16_t)(int16_t)val;
        } else {
            uint8_t reg = (op->modrm >> 3) & 7;
            SetReg(state, reg, (GetReg(state, reg) & 0xFFFF0000) | (uint16_t)(int16_t)val);
        }
    } else if constexpr (SizeSpec == Specialized::Opsize32) {
        if constexpr (RegSpec >= Specialized::Reg0 && RegSpec <= Specialized::Reg7) {
            constexpr uint8_t FixedReg = (uint8_t)RegSpec - (uint8_t)Specialized::Reg0;
            state->ctx.regs[FixedReg] = (uint32_t)(int32_t)val;
        } else {
            uint8_t reg = (op->modrm >> 3) & 7;
            SetReg(state, reg, (uint32_t)(int32_t)val);
        }
    } else if (op->prefixes.flags.opsize) {
        uint8_t reg = (op->modrm >> 3) & 7;
        SetReg(state, reg, (GetReg(state, reg) & 0xFFFF0000) | (uint16_t)(int16_t)val);
    } else {
        uint8_t reg = (op->modrm >> 3) & 7;
        SetReg(state, reg, (uint32_t)(int32_t)val);
    }
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpMovsx_Byte(LogicFuncParams) { return OpMovsx_Byte_Impl<>(LogicPassParams); }
FORCE_INLINE LogicFlow OpMovsx_Byte_32_Eax(LogicFuncParams) {
    return OpMovsx_Byte_Impl<Specialized::RegEax, Specialized::Opsize32>(LogicPassParams);
}
FORCE_INLINE LogicFlow OpMovsx_Byte_32_Ecx(LogicFuncParams) {
    return OpMovsx_Byte_Impl<Specialized::RegEcx, Specialized::Opsize32>(LogicPassParams);
}
FORCE_INLINE LogicFlow OpMovsx_Byte_32_Edx(LogicFuncParams) {
    return OpMovsx_Byte_Impl<Specialized::RegEdx, Specialized::Opsize32>(LogicPassParams);
}

template <Specialized RegSpec = Specialized::None, Specialized SizeSpec = Specialized::None>
FORCE_INLINE LogicFlow OpMovsx_Word_Impl(LogicFuncParams) {
    // 0F BF: MOVSX r32, r/m16
    auto val_res = ReadModRM<uint16_t, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!val_res) return LogicFlow::RestartMemoryOp;
    int16_t val = (int16_t)*val_res;

    if constexpr (SizeSpec == Specialized::Opsize32) {
        if constexpr (RegSpec >= Specialized::Reg0 && RegSpec <= Specialized::Reg7) {
            constexpr uint8_t FixedReg = (uint8_t)RegSpec - (uint8_t)Specialized::Reg0;
            state->ctx.regs[FixedReg] = (uint32_t)(int32_t)val;
        } else {
            uint8_t reg = (op->modrm >> 3) & 7;
            state->ctx.regs[reg] = (uint32_t)(int32_t)val;
        }
    } else if constexpr (SizeSpec == Specialized::Opsize16) {
        if constexpr (RegSpec >= Specialized::Reg0 && RegSpec <= Specialized::Reg7) {
            constexpr uint8_t FixedReg = (uint8_t)RegSpec - (uint8_t)Specialized::Reg0;
            uint32_t current = state->ctx.regs[FixedReg];
            state->ctx.regs[FixedReg] = (current & 0xFFFF0000) | (uint16_t)val;
        } else {
            uint8_t reg = (op->modrm >> 3) & 7;
            SetReg(state, reg, (GetReg(state, reg) & 0xFFFF0000) | (uint16_t)val);
        }
    } else if (op->prefixes.flags.opsize) {
        uint8_t reg = (op->modrm >> 3) & 7;
        SetReg(state, reg, (GetReg(state, reg) & 0xFFFF0000) | (uint16_t)val);
    } else {
        uint8_t reg = (op->modrm >> 3) & 7;
        SetReg(state, reg, (uint32_t)(int32_t)val);
    }
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpMovsx_Word(LogicFuncParams) { return OpMovsx_Word_Impl<>(LogicPassParams); }

#define MOVSX_WORD_32_REG_WRAPPER(RegName, Spec)                                \
    FORCE_INLINE LogicFlow OpMovsx_Word_32_##RegName(LogicFuncParams) {         \
        return OpMovsx_Word_Impl<Spec, Specialized::Opsize32>(LogicPassParams); \
    }

MOVSX_WORD_32_REG_WRAPPER(Eax, Specialized::RegEax)
MOVSX_WORD_32_REG_WRAPPER(Ecx, Specialized::RegEcx)
MOVSX_WORD_32_REG_WRAPPER(Edx, Specialized::RegEdx)
MOVSX_WORD_32_REG_WRAPPER(Ebx, Specialized::RegEbx)
MOVSX_WORD_32_REG_WRAPPER(Ebp, Specialized::RegEbp)
MOVSX_WORD_32_REG_WRAPPER(Edi, Specialized::RegEdi)

#undef MOVSX_WORD_32_REG_WRAPPER

template <Specialized SizeSpec, Specialized RegSpec = Specialized::None>
FORCE_INLINE LogicFlow OpLea_Impl(LogicFuncParams) {
    uint32_t offset = ComputeEA(state, op);

    if constexpr (SizeSpec == Specialized::Opsize16) {
        if constexpr (RegSpec >= Specialized::Reg0 && RegSpec <= Specialized::Reg7) {
            constexpr uint8_t FixedReg = (uint8_t)RegSpec - (uint8_t)Specialized::Reg0;
            uint32_t current = state->ctx.regs[FixedReg];
            state->ctx.regs[FixedReg] = (current & 0xFFFF0000) | (offset & 0xFFFF);
        } else {
            uint8_t reg = (op->modrm >> 3) & 7;
            SetReg(state, reg, (GetReg(state, reg) & 0xFFFF0000) | (offset & 0xFFFF));
        }
    } else {
        if constexpr (RegSpec >= Specialized::Reg0 && RegSpec <= Specialized::Reg7) {
            constexpr uint8_t FixedReg = (uint8_t)RegSpec - (uint8_t)Specialized::Reg0;
            state->ctx.regs[FixedReg] = offset;
        } else {
            uint8_t reg = (op->modrm >> 3) & 7;
            SetReg(state, reg, offset);
        }
    }
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpLea_16(LogicFuncParams) { return OpLea_Impl<Specialized::Opsize16>(LogicPassParams); }
FORCE_INLINE LogicFlow OpLea_32(LogicFuncParams) { return OpLea_Impl<Specialized::Opsize32>(LogicPassParams); }
FORCE_INLINE LogicFlow OpLea_32_Eax(LogicFuncParams) {
    return OpLea_Impl<Specialized::Opsize32, Specialized::RegEax>(LogicPassParams);
}
FORCE_INLINE LogicFlow OpLea_32_Ecx(LogicFuncParams) {
    return OpLea_Impl<Specialized::Opsize32, Specialized::RegEcx>(LogicPassParams);
}
FORCE_INLINE LogicFlow OpLea_32_Edx(LogicFuncParams) {
    return OpLea_Impl<Specialized::Opsize32, Specialized::RegEdx>(LogicPassParams);
}

FORCE_INLINE LogicFlow OpLea(LogicFuncParams) {
    // 8D: LEA r16/32, m
    if (op->prefixes.flags.opsize) {
        return OpLea_16(LogicPassParams);
    }
    return OpLea_32(LogicPassParams);
}

// ------------------------------------------------------------------------------------------------
// Stack Operations (Simple)
// ------------------------------------------------------------------------------------------------

template <Specialized RegSpec, Specialized SizeSpec>
FORCE_INLINE LogicFlow OpPush_Reg_Internal(LogicFuncParams) {
    constexpr uint8_t FixedReg = static_cast<uint8_t>(RegSpec) - static_cast<uint8_t>(Specialized::Reg0);
    if constexpr (SizeSpec == Specialized::Opsize16) {
        if (!Push<uint16_t, true>(state, static_cast<uint16_t>(GetReg(state, FixedReg)), utlb, op))
            return LogicFlow::RestartMemoryOp;
    } else {
        if (!Push<uint32_t, true>(state, GetReg(state, FixedReg), utlb, op)) return LogicFlow::RestartMemoryOp;
    }
    return LogicFlow::Continue;
}

template <Specialized RegSpec, Specialized SizeSpec>
FORCE_INLINE LogicFlow OpPop_Reg_Internal(LogicFuncParams) {
    constexpr uint8_t FixedReg = static_cast<uint8_t>(RegSpec) - static_cast<uint8_t>(Specialized::Reg0);
    if constexpr (SizeSpec == Specialized::Opsize16) {
        auto val_res = Pop<uint16_t, true>(state, utlb, op);
        if (!val_res) return LogicFlow::RestartMemoryOp;
        SetReg(state, FixedReg, (GetReg(state, FixedReg) & 0xFFFF0000) | *val_res);
    } else {
        auto val_res = Pop<uint32_t, true>(state, utlb, op);
        if (!val_res) return LogicFlow::RestartMemoryOp;
        SetReg(state, FixedReg, *val_res);
    }
    return LogicFlow::Continue;
}

#define STACK_REG_WRAPPERS(RegName, Spec)                                         \
    FORCE_INLINE LogicFlow OpPush_Reg32_##RegName(LogicFuncParams) {              \
        return OpPush_Reg_Internal<Spec, Specialized::Opsize32>(LogicPassParams); \
    }                                                                             \
    FORCE_INLINE LogicFlow OpPush_Reg16_##RegName(LogicFuncParams) {              \
        return OpPush_Reg_Internal<Spec, Specialized::Opsize16>(LogicPassParams); \
    }                                                                             \
    FORCE_INLINE LogicFlow OpPop_Reg32_##RegName(LogicFuncParams) {               \
        return OpPop_Reg_Internal<Spec, Specialized::Opsize32>(LogicPassParams);  \
    }                                                                             \
    FORCE_INLINE LogicFlow OpPop_Reg16_##RegName(LogicFuncParams) {               \
        return OpPop_Reg_Internal<Spec, Specialized::Opsize16>(LogicPassParams);  \
    }

STACK_REG_WRAPPERS(Eax, Specialized::RegEax)
STACK_REG_WRAPPERS(Ecx, Specialized::RegEcx)
STACK_REG_WRAPPERS(Edx, Specialized::RegEdx)
STACK_REG_WRAPPERS(Ebx, Specialized::RegEbx)
STACK_REG_WRAPPERS(Esp, Specialized::RegEsp)
STACK_REG_WRAPPERS(Ebp, Specialized::RegEbp)
STACK_REG_WRAPPERS(Esi, Specialized::RegEsi)
STACK_REG_WRAPPERS(Edi, Specialized::RegEdi)

#undef STACK_REG_WRAPPERS

FORCE_INLINE LogicFlow OpPush_Reg(LogicFuncParams) {
    // Generic fallback; opcode-specialized handlers should handle the hot path.
    uint8_t reg = op->modrm & 7;
    if (op->prefixes.flags.opsize) {
        if (!Push<uint16_t, true>(state, static_cast<uint16_t>(GetReg(state, reg)), utlb, op))
            return LogicFlow::RestartMemoryOp;
    } else {
        if (!Push<uint32_t, true>(state, GetReg(state, reg), utlb, op)) return LogicFlow::RestartMemoryOp;
    }
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPush_Imm32(LogicFuncParams) {
    // 68: PUSH imm16/32. Operand size controls both immediate and stack width.
    if (op->prefixes.flags.opsize) {
        if (!Push<uint16_t, true>(state, static_cast<uint16_t>(imm), utlb, op)) return LogicFlow::RestartMemoryOp;
    } else if (!Push<uint32_t, true>(state, imm, utlb, op)) {
        return LogicFlow::RestartMemoryOp;
    }
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPush_Imm8(LogicFuncParams) {
    // 6A: PUSH imm8
    int32_t val = (int32_t)(int8_t)imm;
    if (op->prefixes.flags.opsize) {
        if (!Push<uint16_t, true>(state, static_cast<uint16_t>(val), utlb, op)) return LogicFlow::RestartMemoryOp;
    } else if (!Push<uint32_t, true>(state, val, utlb, op)) {
        return LogicFlow::RestartMemoryOp;
    }
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPop_Reg(LogicFuncParams) {
    // Generic fallback; opcode-specialized handlers should handle the hot path.
    uint8_t reg = op->modrm & 7;
    if (op->prefixes.flags.opsize) {
        auto val_res = Pop<uint16_t, true>(state, utlb, op);
        if (!val_res) return LogicFlow::RestartMemoryOp;
        SetReg(state, reg, (GetReg(state, reg) & 0xFFFF0000) | *val_res);
    } else {
        auto val_res = Pop<uint32_t, true>(state, utlb, op);
        if (!val_res) return LogicFlow::RestartMemoryOp;
        SetReg(state, reg, *val_res);
    }
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPop_Ev(LogicFuncParams) {
    // 8F: POP r/m16/32
    if (op->prefixes.flags.opsize) {
        auto val_res = Pop<uint16_t, true>(state, utlb, op);
        if (!val_res) return LogicFlow::RestartMemoryOp;
        if (!WriteModRM<uint16_t, OpOnTLBMiss::Retry>(state, op, *val_res, utlb)) return LogicFlow::RetryMemoryOp;
    } else {
        auto val_res = Pop<uint32_t, true>(state, utlb, op);
        if (!val_res) return LogicFlow::RestartMemoryOp;
        if (!WriteModRM<uint32_t, OpOnTLBMiss::Retry>(state, op, *val_res, utlb)) return LogicFlow::RetryMemoryOp;
    }
    return LogicFlow::Continue;
}

// ------------------------------------------------------------------------------------------------
// Stack Operations (Complex) - Use Blocking (fail_on_tlb_miss=false)
// ------------------------------------------------------------------------------------------------

template <typename T>
FORCE_INLINE LogicFlow PushaWithWidth(LogicFuncParams) {
    const uint32_t sp = GetReg(state, ESP);
    const uint32_t ss = state->ctx.seg_base[3];
    const uint32_t values[] = {GetReg(state, EAX), GetReg(state, ECX), GetReg(state, EDX), GetReg(state, EBX),
                               sp, GetReg(state, EBP), GetReg(state, ESI), GetReg(state, EDI)};
    for (uint32_t i = 0; i < 8; ++i) {
        if (!WriteMem<T, OpOnTLBMiss::Blocking>(state, ss + sp - (i + 1) * sizeof(T), static_cast<T>(values[i]),
                                               utlb, op))
            return LogicFlow::ExitOnCurrentEIP;
    }
    SetReg(state, ESP, sp - 8 * sizeof(T));
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPusha(LogicFuncParams) {
    return op->prefixes.flags.opsize ? PushaWithWidth<uint16_t>(LogicPassParams)
                                    : PushaWithWidth<uint32_t>(LogicPassParams);
}

template <typename T>
FORCE_INLINE LogicFlow PopaWithWidth(LogicFuncParams) {
    const uint32_t sp = GetReg(state, ESP);
    const uint32_t ss = state->ctx.seg_base[3];
    constexpr uint8_t regs[] = {EDI, ESI, EBP, ESP, EBX, EDX, ECX, EAX};
    T values[8]{};
    for (uint32_t i = 0; i < 8; ++i) {
        if (i == 3) continue; // The saved SP slot is not read or restored.
        auto value = ReadMem<T, OpOnTLBMiss::Blocking>(state, ss + sp + i * sizeof(T), utlb, op);
        if (!value) return LogicFlow::ExitOnCurrentEIP;
        values[i] = *value;
    }
    for (uint32_t i = 0; i < 8; ++i) {
        if (i != 3) WriteRegGeneric<T>(state, regs[i], values[i]);
    }
    SetReg(state, ESP, sp + 8 * sizeof(T));
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPopa(LogicFuncParams) {
    return op->prefixes.flags.opsize ? PopaWithWidth<uint16_t>(LogicPassParams)
                                    : PopaWithWidth<uint32_t>(LogicPassParams);
}

template <typename T>
FORCE_INLINE LogicFlow EnterWithWidth(LogicFuncParams) {
    const uint16_t size = static_cast<uint16_t>(imm);
    const uint8_t level = (imm >> 16) & 31;
    const uint32_t ss = state->ctx.seg_base[3];
    const uint32_t sp = GetReg(state, ESP);
    const uint32_t bp = GetReg(state, EBP);
    uint32_t cursor = sp - sizeof(T);
    if (!WriteMem<T, OpOnTLBMiss::Blocking>(state, ss + cursor, static_cast<T>(bp), utlb, op))
        return LogicFlow::ExitOnCurrentEIP;
    const uint32_t frame = cursor;
    uint32_t previous = static_cast<T>(bp);
    if (level != 0) {
        for (uint8_t i = 1; i < level; ++i) {
            previous = static_cast<T>(previous - sizeof(T));
            auto value = ReadMem<T, OpOnTLBMiss::Blocking>(state, ss + previous, utlb, op);
            if (!value) return LogicFlow::ExitOnCurrentEIP;
            cursor -= sizeof(T);
            if (!WriteMem<T, OpOnTLBMiss::Blocking>(state, ss + cursor, *value, utlb, op))
                return LogicFlow::ExitOnCurrentEIP;
        }
        cursor -= sizeof(T);
        if (!WriteMem<T, OpOnTLBMiss::Blocking>(state, ss + cursor, static_cast<T>(frame), utlb, op))
            return LogicFlow::ExitOnCurrentEIP;
    }
    WriteRegGeneric<T>(state, EBP, static_cast<T>(frame));
    SetReg(state, ESP, cursor - size);
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpEnter(LogicFuncParams) {
    return op->prefixes.flags.opsize ? EnterWithWidth<uint16_t>(LogicPassParams)
                                    : EnterWithWidth<uint32_t>(LogicPassParams);
}

FORCE_INLINE LogicFlow OpLeave(LogicFuncParams) {
    // C9: LEAVE
    // MOV ESP, EBP
    // POP EBP

    uint32_t ebp = GetReg(state, EBP);
    // Read old EBP from stack at EBP
    const uint32_t addr = state->ctx.seg_base[3] + ebp;
    if (op->prefixes.flags.opsize) {
        auto value = ReadMem<uint16_t, OpOnTLBMiss::Blocking>(state, addr, utlb, op);
        if (!value) return LogicFlow::ExitOnCurrentEIP;
        SetReg(state, ESP, ebp + 2);
        WriteRegGeneric<uint16_t>(state, EBP, *value);
        return LogicFlow::Continue;
    }
    auto val_res = ReadMem<uint32_t, OpOnTLBMiss::Blocking>(state, addr, utlb, op);
    if (!val_res) return LogicFlow::ExitOnCurrentEIP;

    SetReg(state, ESP, ebp + 4);
    SetReg(state, EBP, *val_res);
    return LogicFlow::Continue;
}

// ------------------------------------------------------------------------------------------------
// Flag/Misc Operations (LAHF, SAHF, XCHG)
// ------------------------------------------------------------------------------------------------
FORCE_INLINE LogicFlow OpLahf(LogicFuncParams) {
    // 9F: LAHF
    uint8_t ah = 0x02;
    if (ReadSF(flags_cache)) ah |= SF_MASK;
    if (ReadZF(flags_cache)) ah |= ZF_MASK;
    if (EvaluatePF(flags_cache)) ah |= PF_MASK;
    if (ReadAF(flags_cache)) ah |= AF_MASK;
    if (ReadCF(flags_cache)) ah |= CF_MASK;
    uint32_t eax = GetReg(state, EAX);
    eax = (eax & 0xFFFF00FF) | (ah << 8);
    SetReg(state, EAX, eax);
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpSahf(LogicFuncParams) {
    // 9E: SAHF
    uint32_t eax = GetReg(state, EAX);
    uint8_t ah = (eax >> 8) & 0xFF;
    uint32_t flags = GetFlags32NoPF(flags_cache);
    flags = (flags & ~0xFF) | (ah & 0xD5) | 0x02;
    SetFlags32AndSyncParityState(flags_cache, flags);
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpXchg_Reg(LogicFuncParams) {
    // 90+reg: XCHG EAX, r16/32
    uint8_t reg = op->modrm & 7;
    if (reg == 0) return LogicFlow::Continue;  // NOP

    if (op->prefixes.flags.opsize) {
        uint16_t val_eax = (uint16_t)GetReg(state, EAX);
        uint16_t val_reg = (uint16_t)GetReg(state, reg);
        SetReg(state, EAX, (GetReg(state, EAX) & 0xFFFF0000) | val_reg);
        SetReg(state, reg, (GetReg(state, reg) & 0xFFFF0000) | val_eax);
    } else {
        uint32_t val_eax = GetReg(state, EAX);
        uint32_t val_reg = GetReg(state, reg);
        SetReg(state, EAX, val_reg);
        SetReg(state, reg, val_eax);
    }
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpXchg_EvGv(LogicFuncParams) {
    // XCHG r/m16/32, r16/32 (0x87)
    uint8_t reg = (op->modrm >> 3) & 7;
    if (op->prefixes.flags.opsize) {
        auto rm_val_res = ReadModRM<uint16_t, OpOnTLBMiss::Blocking>(state, op, utlb);
        if (!rm_val_res) return LogicFlow::ExitOnCurrentEIP;
        uint16_t rm_val = *rm_val_res;
        uint16_t reg_val = (uint16_t)GetReg(state, reg);

        if (!WriteModRM<uint16_t, OpOnTLBMiss::Blocking>(state, op, reg_val, utlb)) return LogicFlow::ExitOnCurrentEIP;
        SetReg(state, reg, (GetReg(state, reg) & 0xFFFF0000) | rm_val);
    } else {
        auto rm_val_res = ReadModRM<uint32_t, OpOnTLBMiss::Blocking>(state, op, utlb);
        if (!rm_val_res) return LogicFlow::ExitOnCurrentEIP;
        uint32_t rm_val = *rm_val_res;
        uint32_t reg_val = GetReg(state, reg);

        if (!WriteModRM<uint32_t, OpOnTLBMiss::Blocking>(state, op, reg_val, utlb)) return LogicFlow::ExitOnCurrentEIP;
        SetReg(state, reg, rm_val);
    }
    return LogicFlow::Continue;
}

// ------------------------------------------------------------------------------------------------
// String Operations (LODS, SCAS, CMPS) - Use Blocking (fail_on_tlb_miss=false)
// ------------------------------------------------------------------------------------------------

// Address-size override (0x67) selects SI/DI/CX instead of ESI/EDI/ECX and
// wraps offsets at 0xFFFF while preserving the upper halves. String
// destinations always use ES, independent of any segment override.
FORCE_INLINE uint32_t GetStringOffset(EmuState* state, int reg, bool addr16) {
    const uint32_t value = GetReg(state, reg);
    return addr16 ? (value & 0xFFFFu) : value;
}

FORCE_INLINE void SetStringOffset(EmuState* state, int reg, uint32_t value, bool addr16) {
    if (addr16) {
        const uint32_t current = GetReg(state, reg);
        SetReg(state, reg, (current & 0xFFFF0000u) | (value & 0xFFFFu));
    } else {
        SetReg(state, reg, value);
    }
}

FORCE_INLINE uint32_t GetStringCount(EmuState* state, bool addr16) {
    const uint32_t value = GetReg(state, ECX);
    return addr16 ? (value & 0xFFFFu) : value;
}

FORCE_INLINE void SetStringCount(EmuState* state, uint32_t value, bool addr16) {
    if (addr16) {
        const uint32_t current = GetReg(state, ECX);
        SetReg(state, ECX, (current & 0xFFFF0000u) | (value & 0xFFFFu));
    } else {
        SetReg(state, ECX, value);
    }
}

FORCE_INLINE uint32_t StringDestinationAddress(EmuState* state, uint32_t offset) {
    return offset + state->ctx.seg_base[1];  // ES
}

template <typename T>
bool Helper_Movs(LogicFuncParams) {
    const bool addr16 = op->prefixes.flags.addrsize;
    const bool df = TestFlagBits(flags_cache, 0x400);  // DF
    const int32_t step = df ? -static_cast<int32_t>(sizeof(T)) : static_cast<int32_t>(sizeof(T));

    auto perform = [&]() -> bool {
        const uint32_t esi = GetStringOffset(state, ESI, addr16);
        const uint32_t edi = GetStringOffset(state, EDI, addr16);

        const uint32_t src_addr = esi + GetSegmentBase(state, op);
        auto val_res = ReadMem<T, OpOnTLBMiss::Blocking>(state, src_addr, utlb, op);
        if (!val_res) return false;  // Fault
        T val = *val_res;

        if (!WriteMem<T, OpOnTLBMiss::Blocking>(state, StringDestinationAddress(state, edi), val, utlb, op))
            return false;  // Fault

        SetStringOffset(state, ESI, esi + step, addr16);
        SetStringOffset(state, EDI, edi + step, addr16);
        return true;
    };

    if (op->prefixes.flags.rep) {
        while (GetStringCount(state, addr16) > 0) {
            if (!perform()) return false;

            if (state->status != EmuStatus::Running) return false;
            SetStringCount(state, GetStringCount(state, addr16) - 1, addr16);
        }
    } else {
        if (!perform()) return false;
    }
    return true;
}

FORCE_INLINE LogicFlow OpMovs_Byte(LogicFuncParams) {
    if (!Helper_Movs<uint8_t>(LogicPassParams)) return LogicFlow::ExitOnCurrentEIP;
    return LogicFlow::Continue;
}
FORCE_INLINE LogicFlow OpMovs_Word(LogicFuncParams) {
    bool res;
    if (op->prefixes.flags.opsize)
        res = Helper_Movs<uint16_t>(LogicPassParams);
    else
        res = Helper_Movs<uint32_t>(LogicPassParams);

    if (!res) return LogicFlow::ExitOnCurrentEIP;
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpStos_Byte(LogicFuncParams) {
    const bool addr16 = op->prefixes.flags.addrsize;
    bool df = TestFlagBits(flags_cache, 0x400);
    int32_t step = df ? -1 : 1;
    auto perform = [&]() -> bool {
        uint32_t edi = GetStringOffset(state, EDI, addr16);
        uint8_t val = (uint8_t)GetReg(state, EAX);
        if (!WriteMem<uint8_t, OpOnTLBMiss::Blocking>(state, StringDestinationAddress(state, edi), val, utlb, op))
            return false;
        SetStringOffset(state, EDI, edi + step, addr16);
        return true;
    };
    if (op->prefixes.flags.rep) {
        while (GetStringCount(state, addr16) > 0) {
            if (!perform()) return LogicFlow::ExitOnCurrentEIP;
            if (state->status != EmuStatus::Running) break;
            SetStringCount(state, GetStringCount(state, addr16) - 1, addr16);
        }
    } else {
        if (!perform()) return LogicFlow::ExitOnCurrentEIP;
    }
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpStos_Word(LogicFuncParams) {
    const bool addr16 = op->prefixes.flags.addrsize;
    bool df = TestFlagBits(flags_cache, 0x400);
    int32_t step = (op->prefixes.flags.opsize ? 2 : 4);
    if (df) step = -step;

    auto perform = [&]() -> bool {
        uint32_t edi = GetStringOffset(state, EDI, addr16);
        const uint32_t destination = StringDestinationAddress(state, edi);
        if (op->prefixes.flags.opsize) {
            uint16_t val = (uint16_t)GetReg(state, EAX);
            if (!WriteMem<uint16_t, OpOnTLBMiss::Blocking>(state, destination, val, utlb, op)) return false;
        } else {
            uint32_t val = GetReg(state, EAX);
            if (!WriteMem<uint32_t, OpOnTLBMiss::Blocking>(state, destination, val, utlb, op)) return false;
        }
        SetStringOffset(state, EDI, edi + step, addr16);
        return true;
    };
    if (op->prefixes.flags.rep) {
        while (GetStringCount(state, addr16) > 0) {
            if (!perform()) return LogicFlow::ExitOnCurrentEIP;
            if (state->status != EmuStatus::Running) break;
            SetStringCount(state, GetStringCount(state, addr16) - 1, addr16);
        }
    } else {
        if (!perform()) return LogicFlow::ExitOnCurrentEIP;
    }
    return LogicFlow::Continue;
}

template <typename T>
bool Helper_Lods(LogicFuncParams) {
    const bool addr16 = op->prefixes.flags.addrsize;
    bool df = TestFlagBits(flags_cache, 0x400);  // DF
    int32_t step = df ? -((int32_t)sizeof(T)) : (int32_t)sizeof(T);

    auto perform = [&]() -> bool {
        uint32_t esi = GetStringOffset(state, ESI, addr16);
        uint32_t src_addr = esi + GetSegmentBase(state, op);
        auto val_res = ReadMem<T, OpOnTLBMiss::Blocking>(state, src_addr, utlb, op);
        if (!val_res) return false;
        T val = *val_res;

        if constexpr (sizeof(T) == 1) {
            uint32_t* rptr = GetRegPtr(state, EAX);
            *rptr = (*rptr & 0xFFFFFF00) | val;
        } else if constexpr (sizeof(T) == 2) {
            SetReg(state, EAX, (GetReg(state, EAX) & 0xFFFF0000) | val);
        } else {
            SetReg(state, EAX, val);
        }

        SetStringOffset(state, ESI, esi + step, addr16);
        return true;
    };

    if (op->prefixes.flags.rep) {
        while (GetStringCount(state, addr16) > 0) {
            if (!perform()) return false;
            if (state->status != EmuStatus::Running) return false;
            SetStringCount(state, GetStringCount(state, addr16) - 1, addr16);
        }
    } else {
        if (!perform()) return false;
    }
    return true;
}

FORCE_INLINE LogicFlow OpLods_Byte(LogicFuncParams) {
    if (!Helper_Lods<uint8_t>(LogicPassParams)) return LogicFlow::ExitOnCurrentEIP;
    return LogicFlow::Continue;
}
FORCE_INLINE LogicFlow OpLods_Word(LogicFuncParams) {
    bool res;
    if (op->prefixes.flags.opsize)
        res = Helper_Lods<uint16_t>(LogicPassParams);
    else
        res = Helper_Lods<uint32_t>(LogicPassParams);

    if (!res) return LogicFlow::ExitOnCurrentEIP;
    return LogicFlow::Continue;
}

template <typename T>
bool Helper_Scas(LogicFuncParams) {
    const bool addr16 = op->prefixes.flags.addrsize;
    bool df = TestFlagBits(flags_cache, 0x400);  // DF
    int32_t step = df ? -((int32_t)sizeof(T)) : (int32_t)sizeof(T);

    auto perform = [&]() -> bool {
        uint32_t edi = GetStringOffset(state, EDI, addr16);
        // ES:[EDI]
        auto mem_val_res =
            ReadMem<T, OpOnTLBMiss::Blocking>(state, StringDestinationAddress(state, edi), utlb, op);
        if (!mem_val_res) return false;
        T mem_val = *mem_val_res;

        T acc;
        if constexpr (sizeof(T) == 1)
            acc = (T)GetReg(state, EAX);
        else if constexpr (sizeof(T) == 2)
            acc = (T)(GetReg(state, EAX) & 0xFFFF);
        else
            acc = (T)GetReg(state, EAX);

        AluSub<T>(state, flags_cache, acc, mem_val);  // CMP uses Sub.

        SetStringOffset(state, EDI, edi + step, addr16);
        return true;
    };

    if (op->prefixes.flags.rep || op->prefixes.flags.repne) {
        while (GetStringCount(state, addr16) > 0) {
            if (!perform()) return false;
            if (state->status != EmuStatus::Running) return false;

            SetStringCount(state, GetStringCount(state, addr16) - 1, addr16);

            bool zf = TestFlagBits(flags_cache, ZF_MASK);
            if (op->prefixes.flags.rep) {
                if (!zf) break;
            }  // REPE: Stop if ZF=0
            else if (op->prefixes.flags.repne) {
                if (zf) break;
            }  // REPNE: Stop if ZF=1
        }
    } else {
        if (!perform()) return false;
    }
    return true;
}

FORCE_INLINE LogicFlow OpScas_Byte(LogicFuncParams) {
    if (!Helper_Scas<uint8_t>(LogicPassParams)) return LogicFlow::ExitOnCurrentEIP;
    return LogicFlow::Continue;
}
FORCE_INLINE LogicFlow OpScas_Word(LogicFuncParams) {
    bool res;
    if (op->prefixes.flags.opsize)
        res = Helper_Scas<uint16_t>(LogicPassParams);
    else
        res = Helper_Scas<uint32_t>(LogicPassParams);

    if (!res) return LogicFlow::ExitOnCurrentEIP;
    return LogicFlow::Continue;
}

template <typename T>
bool Helper_Cmps(LogicFuncParams) {
    const bool addr16 = op->prefixes.flags.addrsize;
    bool df = TestFlagBits(flags_cache, 0x400);  // DF
    int32_t step = df ? -((int32_t)sizeof(T)) : (int32_t)sizeof(T);

    auto perform = [&]() -> bool {
        uint32_t esi = GetStringOffset(state, ESI, addr16);
        uint32_t edi = GetStringOffset(state, EDI, addr16);

        uint32_t src_addr = esi + GetSegmentBase(state, op);
        auto src_val_res = ReadMem<T, OpOnTLBMiss::Blocking>(state, src_addr, utlb, op);
        if (!src_val_res) return false;
        T src_val = *src_val_res;

        auto dst_val_res = ReadMem<T, OpOnTLBMiss::Blocking>(state, StringDestinationAddress(state, edi), utlb,
                                                             op);  // ES:EDI
        if (!dst_val_res) return false;
        T dst_val = *dst_val_res;

        AluSub<T>(state, flags_cache, src_val, dst_val);

        SetStringOffset(state, ESI, esi + step, addr16);
        SetStringOffset(state, EDI, edi + step, addr16);
        return true;
    };

    if (op->prefixes.flags.rep || op->prefixes.flags.repne) {
        while (GetStringCount(state, addr16) > 0) {
            if (!perform()) return false;
            if (state->status != EmuStatus::Running) return false;

            SetStringCount(state, GetStringCount(state, addr16) - 1, addr16);

            bool zf = TestFlagBits(flags_cache, ZF_MASK);
            if (op->prefixes.flags.rep) {
                if (!zf) break;
            }  // REPE: Stop if ZF=0
            else if (op->prefixes.flags.repne) {
                if (zf) break;
            }  // REPNE: Stop if ZF=1
        }
    } else {
        if (!perform()) return false;
    }
    return true;
}

FORCE_INLINE LogicFlow OpCmps_Byte(LogicFuncParams) {
    if (!Helper_Cmps<uint8_t>(LogicPassParams)) return LogicFlow::ExitOnCurrentEIP;
    return LogicFlow::Continue;
}
FORCE_INLINE LogicFlow OpCmps_Word(LogicFuncParams) {
    bool res;
    if (op->prefixes.flags.opsize)
        res = Helper_Cmps<uint16_t>(LogicPassParams);
    else
        res = Helper_Cmps<uint32_t>(LogicPassParams);

    if (!res) return LogicFlow::ExitOnCurrentEIP;
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpXlat(LogicFuncParams) {
    // D7: XLAT/XLATB
    // AL = [DS:EBX + AL]
    uint32_t ebx = GetReg(state, EBX);
    uint32_t al = GetReg(state, EAX) & 0xFF;
    // Segment override is possible, but default is DS.
    // op->mem.base_offset is not set up for this specific instruction by decoder usually,
    // so we rely on GetSegmentBase(state, op) and manual calculation.
    // XLAT implies DS unless overridden.
    uint32_t addr = ebx + al + GetSegmentBase(state, op);

    auto val_res = ReadMem<uint8_t, OpOnTLBMiss::Restart>(state, addr, utlb, op);
    if (!val_res) return LogicFlow::RestartMemoryOp;

    uint32_t eax = GetReg(state, EAX);
    eax = (eax & 0xFFFFFF00) | *val_res;
    SetReg(state, EAX, eax);
    return LogicFlow::Continue;
}

}  // namespace op

}  // namespace fiberish
