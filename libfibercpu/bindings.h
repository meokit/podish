#ifndef EMU86_BINDINGS_H
#define EMU86_BINDINGS_H

#ifdef __cplusplus
#include <cstdint>
namespace x86emu {
struct EmuState;
}
typedef x86emu::EmuState EmuState;
#else
#include <stdint.h>
typedef struct EmuState EmuState;
#endif

#ifdef __cplusplus
extern "C" {
#endif

// Creation / Destruction
EmuState* X86_Create();
// share_mem=1: Threads (CLONE_VM), share_mem=0: Fork (Copy Memory)
EmuState* X86_Clone(EmuState* parent, int share_mem);
void X86_Destroy(EmuState* state);

// Register Access
uint32_t X86_RegRead(EmuState* state, int reg_index);
void X86_RegWrite(EmuState* state, int reg_index, uint32_t val);

uint32_t X86_GetEIP(EmuState* state);
void X86_SetEIP(EmuState* state, uint32_t eip);

uint32_t X86_GetEFLAGS(EmuState* state);
void X86_SetEFLAGS(EmuState* state, uint32_t val);

// XMM Access (128-bit)
void X86_ReadXMM(EmuState* state, int idx, uint8_t* val);
void X86_WriteXMM(EmuState* state, int idx, const uint8_t* val);

// FPU Access
uint16_t X86_GetFCW(EmuState* state);
void X86_SetFCW(EmuState* state, uint16_t val);
uint16_t X86_GetFSW(EmuState* state);
void X86_SetFSW(EmuState* state, uint16_t val);
uint16_t X86_GetFTW(EmuState* state);
void X86_SetFTW(EmuState* state, uint16_t val);
void X86_ReadFPUReg(EmuState* state, int idx, uint8_t* val);
void X86_WriteFPUReg(EmuState* state, int idx, const uint8_t* val);

// Segment Base Access
uint32_t X86_SegBaseRead(EmuState* state, int seg_index);
void X86_SegBaseWrite(EmuState* state, int seg_index, uint32_t base);

// Memory Access
void X86_MemMap(EmuState* state, uint32_t addr, uint32_t size, uint8_t perms);
void X86_MemUnmap(EmuState* state, uint32_t addr, uint32_t size);
void X86_MemWrite(EmuState* state, uint32_t addr, const uint8_t* data, uint32_t size);
void X86_MemRead(EmuState* state, uint32_t addr, uint8_t* val, uint32_t size);
int X86_MemIsDirty(EmuState* state, uint32_t addr);

// Execution
void X86_Run(EmuState* state, uint32_t end_eip, uint64_t max_insts);
void X86_EmuStop(EmuState* state);
void X86_EmuFault(EmuState* state);
void X86_EmuYield(EmuState* state);
int X86_Step(EmuState* state);
int X86_GetStatus(EmuState* state);

// Callbacks
typedef void (*FaultHandler)(EmuState* state, uint32_t addr, int is_write, void* userdata);
typedef void (*MemHook)(EmuState* state, uint32_t addr, uint32_t size, int is_write, uint64_t val, void* userdata);
typedef int (*InterruptHandler)(EmuState* state, uint32_t vector, void* userdata);

void X86_SetFaultCallback(EmuState* state, FaultHandler handler, void* userdata);
void X86_SetMemHook(EmuState* state, MemHook hook, void* userdata);
void X86_SetInterruptHook(EmuState* state, uint8_t vector, InterruptHandler hook, void* userdata);

// Cache Control
void X86_FlushCache(EmuState* state);
void X86_InvalidateRange(EmuState* state, uint32_t addr, uint32_t size);

// Diagnostics
int32_t X86_GetFaultVector(EmuState* state);
void X86_DumpJccStats();

#ifdef __cplusplus
}
#endif

#endif  // EMU86_BINDINGS_H