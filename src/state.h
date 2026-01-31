#pragma once

#include "common.h"
#include "mmu.h"
#include "hooks.h"
#include <ankerl/unordered_dense.h>
#include "decoder.h" // For BasicBlock definition

namespace x86emu {

struct EmuState {
    Context ctx;
    SoftMMU mmu;
    HookManager hooks;
    EmuStatus status = EmuStatus::Stopped;
    // Simple Block Cache
    // High performance dense map
    ankerl::unordered_dense::map<uint32_t, BasicBlock> block_cache;

    // Fault Info
    uint8_t fault_vector = 0;
    uint32_t fault_addr = 0;
    // We avoid std::string here to keep it POD-like if possible, but Context is struct.
    // Let's use simple fixed buffer or just rely on external logging for now?
    // User asked for "fields to record Fault information".
    // Let's stick to vector/addr. Msg might be tricky with ownership.
    // Actually, simple static string or status code is better. 
    // Status is already EmuStatus.
};

}
