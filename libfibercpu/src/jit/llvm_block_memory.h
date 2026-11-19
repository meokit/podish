#pragma once

#include <memory>
#include <vector>

#include <llvm/ExecutionEngine/JITLink/JITLinkMemoryManager.h>
#include <llvm/Support/raw_ostream.h>

namespace fiberish {

class ExecutableArena;

struct CodeBudgetExceeded : llvm::ErrorInfo<CodeBudgetExceeded> {
    static inline char ID = 0;
    void log(llvm::raw_ostream& out) const override { out << "JIT arena budget exceeded"; }
    std::error_code convertToErrorCode() const override { return llvm::inconvertibleErrorCode(); }
};

class BlockMemoryManager final : public llvm::jitlink::JITLinkMemoryManager {
public:
    explicit BlockMemoryManager(size_t page_size);
    ~BlockMemoryManager() override;

    bool IsAvailable() const { return bool(arena_); }
    void allocate(const llvm::jitlink::JITLinkDylib* dylib, llvm::jitlink::LinkGraph& graph,
                  OnAllocatedFunction on_allocated) override;
    void deallocate(std::vector<FinalizedAlloc> allocations, OnDeallocatedFunction on_deallocated) override;

    // LLJIT links synchronously under the link mutex.
    void BeginLink() { needs_flush_ = false; }
    bool NeedsFlush() const { return needs_flush_; }

private:
    struct Slab;
    struct PackedAllocation;
    class PendingAllocation;

    const size_t page_size_;
    const std::shared_ptr<ExecutableArena> arena_;
    std::weak_ptr<Slab> current_slab_;
    bool needs_flush_ = false;
};

}  // namespace fiberish
