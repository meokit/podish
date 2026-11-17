#include "bindings.h"
#include "state.h"
#include <cstdio>
#include <cstring>

using namespace fiberish;

int main() {
    constexpr uint32_t address = 0x1000;
    auto* state = X86_Create();
    state->status = EmuStatus::Running;
    auto* page = state->mmu.allocate_page(address, static_cast<uint8_t>(mem::Property::RX));
    if (!page)
        return 1;
    std::memset(page, 0x40, 4096); // inc eax; no control flow or absorbed NOPs
    auto check = [&](uint32_t limit, uint64_t max, uint32_t count, uint32_t end) {
        state->mmu.reset_code_cache();
        auto* block = DecodeBlock(state, address, limit, max);
        if (!block || block->inst_count() != count || block->slot_count != count + 1 || block->end_eip != end) {
            std::fprintf(stderr, "Unexpected block for limit=%x max=%llu\n", limit, (unsigned long long)max);
            return false;
        }
        return true;
    };
    bool ok = check(0, 0, 64, address + 64) && check(0, 1, 1, address + 1) && check(address + 17, 0, 17, address + 17);
    // Invalid 15-byte prefix sequence at the last available decoded-op slot.
    std::memset(page + 63, 0x66, 15);
    ok = check(0, 0, 64, address + 64) && ok;
    X86_Destroy(state);
    return ok ? 0 : 1;
}
