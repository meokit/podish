#include "jit/llvm_block_memory.h"

#include "jit/executable_arena.h"

#include <algorithm>
#include <cstring>
#include <utility>

#include <llvm/ExecutionEngine/JITLink/JITLink.h>
#include <llvm/ExecutionEngine/Orc/Shared/AllocationActions.h>
#include <llvm/Support/MathExtras.h>
#include <llvm/Support/Memory.h>

namespace fiberish {
namespace {

constexpr size_t kBlockAlignment = 16;
constexpr size_t kSlabBytes = 256 * 1024;

struct DataSegment {
    std::unique_ptr<ExecutableArena::Region> region;
    unsigned protection;
    bool finalize_only;

    llvm::sys::MemoryBlock Mapping() const { return {region->Data(), region->Size()}; }
};

}  // namespace

struct BlockMemoryManager::Slab {
    std::unique_ptr<ExecutableArena::Region> region;
    size_t used = 0;
};

struct BlockMemoryManager::PackedAllocation {
    std::shared_ptr<Slab> slab;
    std::vector<DataSegment> data;
    std::vector<llvm::orc::shared::WrapperFunctionCall> dealloc_actions;
};

class BlockMemoryManager::PendingAllocation final : public JITLinkMemoryManager::InFlightAlloc {
public:
    PendingAllocation(llvm::jitlink::LinkGraph& graph, std::shared_ptr<Slab> slab, uint8_t* address, size_t bytes,
                      size_t alignment, std::vector<DataSegment> data)
        : graph_(graph),
          slab_(std::move(slab)),
          address_(address),
          bytes_(bytes),
          working_(bytes ? bytes + alignment - 1 : 0),
          aligned_working_(bytes ? llvm::alignAddr(working_.data(), llvm::Align(alignment)) : 0),
          data_(std::move(data)) {}

    char* WorkingMemory() const { return reinterpret_cast<char*>(aligned_working_); }

    void abandon(OnAbandonedFunction on_abandoned) override {
        slab_.reset();
        data_.clear();
        on_abandoned(llvm::Error::success());
    }

    void finalize(OnFinalizedFunction on_finalized) override {
        for (const auto& segment : data_) {
            if (auto error = llvm::sys::Memory::protectMappedMemory(segment.Mapping(), segment.protection)) {
                slab_.reset();
                data_.clear();
                return on_finalized(llvm::errorCodeToError(error));
            }
        }
        if (bytes_) {
            {
                ScopedJitWrite write;
                std::memcpy(address_, WorkingMemory(), bytes_);
            }
            FlushJitInstructions(address_, bytes_);
        }

        llvm::orc::shared::runFinalizeActions(
            graph_.allocActions(), [this, on_finalized = std::move(on_finalized)](auto actions) mutable {
                if (!actions) {
                    slab_.reset();
                    data_.clear();
                    return on_finalized(actions.takeError());
                }
                auto allocation = std::make_unique<PackedAllocation>();
                allocation->slab = std::move(slab_);
                allocation->dealloc_actions = std::move(*actions);
                std::erase_if(data_, [](const DataSegment& segment) { return segment.finalize_only; });
                allocation->data = std::move(data_);
                on_finalized(FinalizedAlloc(llvm::orc::ExecutorAddr::fromPtr(allocation.release())));
            });
    }

private:
    llvm::jitlink::LinkGraph& graph_;
    std::shared_ptr<Slab> slab_;
    uint8_t* const address_;
    const size_t bytes_;
    std::vector<char> working_;
    const uintptr_t aligned_working_;
    std::vector<DataSegment> data_;
};

BlockMemoryManager::BlockMemoryManager(size_t page_size) : page_size_(page_size), arena_(ExecutableArena::Shared()) {}

BlockMemoryManager::~BlockMemoryManager() = default;

void BlockMemoryManager::allocate(const llvm::jitlink::JITLinkDylib*, llvm::jitlink::LinkGraph& graph,
                                  OnAllocatedFunction on_allocated) {
    using llvm::jitlink::BasicLayout;
    using llvm::jitlink::JITLinkError;
    using llvm::orc::MemLifetime;
    using llvm::orc::MemProt;
    using llvm::sys::Memory;

    // Mach-O JITLink initially marks non-instruction sections writable.
    // Group immutable sections separately so they can be sealed after fixups.
    for (auto& section : graph.sections()) {
        const auto name = section.getName();
        if (name == "__TEXT,__const" || name == "__TEXT,__cstring" ||
            name.starts_with("__TEXT,__literal") || name == "__DATA,__const" ||
            name.starts_with("__DATA_CONST,"))
            section.setMemProt(MemProt::Read);
    }
    BasicLayout layout(graph);
    BasicLayout::Segment* text = nullptr;
    size_t text_bytes = 0;
    size_t alignment = kBlockAlignment;
    size_t data_bytes = 0;
    for (auto& [group, segment] : layout.segments()) {
        if (segment.Alignment > page_size_)
            return on_allocated(llvm::make_error<JITLinkError>("LLVM block segment alignment exceeds page size"));
        const size_t bytes = std::max<size_t>(1, segment.ContentSize + segment.ZeroFillSize);
        const auto protection = llvm::orc::toSysMemoryProtectionFlags(group.getMemProt());
        if (protection & Memory::MF_EXEC) {
            if (protection != (Memory::MF_READ | Memory::MF_EXEC) || group.getMemLifetime() != MemLifetime::Standard)
                return on_allocated(
                    llvm::make_error<JITLinkError>("unsupported executable segment protection or lifetime"));
            text = &segment;
            alignment = std::max<size_t>(alignment, segment.Alignment.value());
            text_bytes = llvm::alignTo(bytes, alignment);
        } else {
            data_bytes += llvm::alignTo(bytes, page_size_);
        }
    }

    const size_t slab_bytes = text_bytes ? llvm::alignTo(std::max(kSlabBytes, text_bytes), page_size_) : 0;
    const auto on_budget_exhausted = [&] {
        needs_flush_ = arena_->FitsFreshGeneration(slab_bytes, data_bytes);
        on_allocated(llvm::make_error<CodeBudgetExceeded>());
    };
    std::shared_ptr<Slab> slab;
    uint8_t* address = nullptr;
    size_t offset = 0;
    if (text_bytes) {
        slab = current_slab_.lock();
        if (slab) offset = llvm::alignTo(slab->used, alignment);
        if (!slab || offset + text_bytes > slab->region->Size()) {
            auto region = arena_->Reserve(slab_bytes, ExecutableArena::Lifetime::Generation);
            if (!region) return on_budget_exhausted();
            slab = std::make_shared<Slab>(Slab{std::move(region)});
            current_slab_ = slab;
            offset = 0;
        }
        address = slab->region->Data() + offset;
    }

    // Every data segment has a page-aligned reservation in the arena's nearby
    // data range. Set the final protection after JITLink applies its fixups.
    std::vector<DataSegment> data;
    for (auto& [group, segment] : layout.segments()) {
        if (&segment == text) continue;
        const size_t bytes = std::max<size_t>(1, segment.ContentSize + segment.ZeroFillSize);
        auto region = arena_->ReserveData(bytes);
        if (!region) return on_budget_exhausted();
        if (auto error = Memory::protectMappedMemory({region->Data(), region->Size()},
                                                      Memory::MF_READ | Memory::MF_WRITE))
            return on_allocated(llvm::errorCodeToError(error));
        // BasicLayout assigns zero-fill addresses without writing those bytes.
        // The underlying pages may still contain data from an older block.
        std::memset(region->Data(), 0, bytes);
        segment.WorkingMem = reinterpret_cast<char*>(region->Data());
        segment.Addr = llvm::orc::ExecutorAddr::fromPtr(region->Data());
        data.push_back({std::move(region),
                        llvm::orc::toSysMemoryProtectionFlags(group.getMemProt()),
                        group.getMemLifetime() == MemLifetime::Finalize});
    }
    auto pending = std::make_unique<PendingAllocation>(graph, slab, address, text_bytes, alignment, std::move(data));
    if (text) {
        text->Addr = llvm::orc::ExecutorAddr::fromPtr(address);
        text->WorkingMem = pending->WorkingMemory();
    }
    if (auto error = layout.apply()) return on_allocated(std::move(error));
    if (slab) slab->used = offset + text_bytes;
    on_allocated(std::move(pending));
}

void BlockMemoryManager::deallocate(std::vector<FinalizedAlloc> allocations, OnDeallocatedFunction on_deallocated) {
    llvm::Error result = llvm::Error::success();
    for (auto it = allocations.rbegin(); it != allocations.rend(); ++it) {
        auto& allocation = *it;
        if (!allocation) continue;
        std::unique_ptr<PackedAllocation> packed(allocation.release().toPtr<PackedAllocation*>());
        for (auto action = packed->dealloc_actions.rbegin(); action != packed->dealloc_actions.rend(); ++action)
            result = llvm::joinErrors(std::move(result), action->runWithSPSRetErrorMerged());
        // Dropping the last ORC handle (or in-flight job) releases the slab.
    }
    on_deallocated(std::move(result));
}

}  // namespace fiberish
