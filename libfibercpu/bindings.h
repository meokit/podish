#ifndef EMU86_BINDINGS_H
#define EMU86_BINDINGS_H

#ifdef __cplusplus
#include <cstddef>
#include <cstdint>
namespace fiberish {
struct EmuState;
struct BasicBlock;
}  // namespace fiberish
typedef fiberish::EmuState EmuState;
typedef fiberish::BasicBlock BasicBlock;
struct X86_MmuHandle;
#else
#include <stddef.h>
#include <stdint.h>
typedef struct EmuState EmuState;
typedef struct BasicBlock BasicBlock;
typedef struct X86_MmuHandle X86_MmuHandle;
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef struct {
    uint32_t guest_page;
    uint8_t perms;
    uint8_t flags;
    uint16_t reserved;
    void* host_page;
} X86_PageMapping;

enum { X86_PAGE_FLAG_DIRTY = 1 << 0, X86_PAGE_FLAG_EXTERNAL = 1 << 1 };

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
// DEPRECATED: Use ResolvePtr + direct memcpy instead. These are slow (byte-by-byte) and for testing only.
void X86_MemWrite(EmuState* state, uint32_t addr, const uint8_t* data, uint32_t size);
void X86_MemRead(EmuState* state, uint32_t addr, uint8_t* val, uint32_t size);
int X86_MemIsDirty(EmuState* state, uint32_t addr);
// Returns physical address (pointer) if valid, or NULL if not mapped/no-perm
void* X86_ResolvePtr(EmuState* state, uint32_t addr, int is_write);
// Collect present page mappings in [addr, addr + size), one record per mapped page.
size_t X86_CollectMappedPages(EmuState* state, uint32_t addr, uint32_t size, X86_PageMapping* buffer, size_t max_count);
// Allocate a single page with given permissions, returns host pointer to page
void* X86_AllocatePage(EmuState* state, uint32_t addr, uint8_t perms);
// Map external memory to guest address (caller owns memory), returns 1 on success
int X86_MapExternalPage(EmuState* state, uint32_t addr, void* external_page, uint8_t perms);
// MMU handle management (intrusive refcount in native core).
X86_MmuHandle* X86_MmuCreateEmpty();
X86_MmuHandle* X86_MmuCloneSkipExternal(X86_MmuHandle* mmu);
X86_MmuHandle* X86_MmuRetain(X86_MmuHandle* mmu);
void X86_MmuRelease(X86_MmuHandle* mmu);
uintptr_t X86_MmuGetIdentity(X86_MmuHandle* mmu);
// Engine/MMU binding APIs.
X86_MmuHandle* X86_EngineGetMmu(EmuState* state);
X86_MmuHandle* X86_EngineDetachMmu(EmuState* state);
int X86_EngineAttachMmu(EmuState* state, X86_MmuHandle* mmu);

// Execution
void X86_Run(EmuState* state, uint32_t end_eip, uint64_t max_insts);
void X86_EmuStop(EmuState* state);
void X86_EmuFault(EmuState* state);
void X86_EmuYield(EmuState* state);
int X86_Step(EmuState* state);
int X86_GetStatus(EmuState* state);

// Callbacks
typedef int (*FaultHandler)(EmuState* state, uint32_t addr, int is_write, void* userdata);
typedef void (*MemHook)(EmuState* state, uint32_t addr, uint32_t size, int is_write, uint64_t val, void* userdata);
typedef int (*InterruptHandler)(EmuState* state, uint32_t vector, void* userdata);

void X86_SetFaultCallback(EmuState* state, FaultHandler handler, void* userdata);
void X86_SetMemHook(EmuState* state, MemHook hook, void* userdata);
void X86_SetInterruptHook(EmuState* state, uint8_t vector, InterruptHandler hook, void* userdata);

// Cache Control
void X86_FlushCache(EmuState* state);
void X86_ResetMemory(EmuState* state);
void X86_InvalidateRange(EmuState* state, uint32_t addr, uint32_t size);

// Diagnostics
int32_t X86_GetFaultVector(EmuState* state);

// TSC Control
void X86_SetTscFrequency(EmuState* state, uint64_t freq);
void X86_SetTscMode(EmuState* state, int mode);
void X86_SetTscOffset(EmuState* state, uint64_t offset);

// Logging
// Matches Microsoft.Extensions.Logging.LogLevel
typedef void (*X86LogCallback)(int level, const char* message, void* userdata);
void X86_SetLogCallback(EmuState* state, X86LogCallback callback, void* userdata);

// TLB Statistics
typedef struct {
    uint64_t l1_read_hits;
    uint64_t l1_write_hits;
    uint64_t l2_read_hits;
    uint64_t l2_write_hits;
    uint64_t read_misses;
    uint64_t write_misses;
    uint64_t total_reads;
    uint64_t total_writes;
} X86_TlbStats;

void X86_GetTlbStats(EmuState* state, X86_TlbStats* stats);
void X86_ResetTlbStats(EmuState* state);
int X86_DumpStats(EmuState* state, char* buffer, size_t buffer_size);

// Block Coverage
// Returns pointer to internal BasicBlock structures
// C# must match the struct layout to read them.
size_t X86_GetBlockCount(EmuState* state);
size_t X86_GetBlockList(EmuState* state, BasicBlock** buffer, size_t max_count);

// Returns the base address of the fibercpu library (for symbol resolution)
void* X86_GetLibAddress();

#ifdef __cplusplus
}
#endif

#endif  // EMU86_BINDINGS_H
