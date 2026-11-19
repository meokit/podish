#pragma once

#include <limits>
#include <memory>
#include <vector>

#include <llvm/ExecutionEngine/JITLink/JITLinkMemoryManager.h>
#include <llvm/Support/raw_ostream.h>

namespace fiberish {

class ExecutableArena;

struct CodeBudgetExceeded : llvm::ErrorInfo<CodeBudgetExceeded> {
    static inline char ID = 0;
    void log(llvm::raw_ostream& out) const override { out << "LLVM block code budget exceeded"; }
    std::error_code convertToErrorCode() const override { return llvm::inconvertibleErrorCode(); }
};

class BlockMemoryManager final : public llvm::jitlink::JITLinkMemoryManager {
public:
    explicit BlockMemoryManager(size_t page_size);
    ~BlockMemoryManager() override;

    bool IsAvailable() const { return bool(arena_); }
    size_t MinimumAllocationSize() const;
    void allocate(const llvm::jitlink::JITLinkDylib* dylib, llvm::jitlink::LinkGraph& graph,
                  OnAllocatedFunction on_allocated) override;
    void deallocate(std::vector<FinalizedAlloc> allocations, OnDeallocatedFunction on_deallocated) override;

    // LLJIT links synchronously under the compiler mutex.
    size_t allocated_bytes = 0;
    size_t blocked_bytes = 0;
    size_t budget = std::numeric_limits<size_t>::max();

private:
    struct Slab;
    struct PackedAllocation;
    class PendingAllocation;

    bool FitsBudget(size_t bytes);

    const size_t page_size_;
    const std::shared_ptr<ExecutableArena> arena_;
    std::weak_ptr<Slab> current_slab_;
};

}  // namespace fiberish
