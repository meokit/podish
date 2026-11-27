#pragma once

#include <cstdint>
#include <functional>
#include <memory>
#include <span>
#include <vector>

#include <llvm/ADT/STLFunctionalExtras.h>
#include <llvm/ADT/StringRef.h>

#include "aarch64/aarch64.h"

namespace fiberish {
class AsyncSignals;
struct EmuState;
}  // namespace fiberish

namespace llvm {
class Module;
}

namespace fiberish::arm_jit {

struct Step {
    uint32_t word;
    uint8_t slots;  // In 16-byte units. Folded NOPs occupy zero slots.
};

struct Member {
    uint16_t offset;
    std::vector<Step> steps;

    unsigned SlotCount() const {
        unsigned count = 0;
        for (const auto& step : steps) count += step.slots;
        return count;
    }
};

bool CanLower(uint32_t word);
bool Enabled(const EmuState& state);
unsigned WorkerLimit();
bool CanQueue();
void TraceRegion(uint64_t page, size_t members);
bool Queue(std::weak_ptr<AsyncSignals> signals, std::function<void()> work);
std::vector<aarch64::detail::HandlerFunc> Compile(std::span<const Member> members, uint64_t epoch,
                                                  const AsyncSignals* signals = nullptr);
std::unique_ptr<llvm::Module> Prepare(std::span<const Member> members, llvm::StringRef name, llvm::StringRef pipeline,
                                      bool fold_operand_loads, llvm::function_ref<bool()> should_cancel = {});

}  // namespace fiberish::arm_jit
