#pragma once
#include "state.h"
#include "decoder.h"
#include <cstdint>

extern "C" {

    x86emu::EmuState* X86_Create();
    void X86_Destroy(x86emu::EmuState* state);
    x86emu::Context* X86_GetContext(x86emu::EmuState* state);
    
    // Callbacks
    using PyFaultHandler = void(*)(uint32_t addr, int is_write);
    using PyMemHook = void(*)(uint32_t addr, uint32_t size, int is_write, uint64_t val);

    void X86_SetFaultCallback(x86emu::EmuState* state, PyFaultHandler handler);
    void X86_SetMemHook(x86emu::EmuState* state, PyMemHook hook);
    
    void X86_MemMap(x86emu::EmuState* state, uint32_t addr, uint32_t size, uint8_t perms);
    void X86_MemWrite(x86emu::EmuState* state, uint32_t addr, const uint8_t* data, uint32_t size);
    void X86_MemRead(x86emu::EmuState* state, uint32_t addr, uint8_t* val, uint32_t size);
    
    // Interrupt Hook: int hook(uint32_t vector) -> return 1 (handled), 0 (fault)
    using PyInterruptHook = int(*)(uint32_t vector);
    void X86_SetInterruptHook(x86emu::EmuState* state, uint8_t vector, PyInterruptHook hook);

    
    void X86_Run(x86emu::EmuState* state);
    void X86_EmuStop(x86emu::EmuState* state);
    int X86_Step(x86emu::EmuState* state);
    
    void X86_Decode(const uint8_t* bytes, x86emu::DecodedOp* op_out);

}
