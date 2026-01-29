#pragma once

#include "common.h"
#include "mmu.h"
#include "hooks.h"
#include <unordered_map>
#include "decoder.h" // For BasicBlock definition

namespace x86emu {

enum class EmuStatus {
    Running,
    Stopped,
    Fault
};

struct EmuState {
    Context ctx;
    SoftMMU mmu;
    HookManager hooks;
    EmuStatus status = EmuStatus::Stopped;
    
    // Simple Block Cache
    std::unordered_map<uint32_t, BasicBlock> block_cache;
};

}
