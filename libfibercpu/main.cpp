#include <cstdio>
#include "common.h"
#include "hooks.h"
#include "mmu.h"

namespace x86emu {

void Run(Context* ctx) {
    printf("Starting execution at EIP: 0x%08x\n", ctx->eip);

    // Cast back to concrete types for usage (in the loop)
    SoftMMU* mmu = static_cast<SoftMMU*>(ctx->mmu);
    HookManager* hooks = static_cast<HookManager*>(ctx->hooks);

    // Verification/Mock Logic
    try {
        // Map code region (mock)
        mmu->mmap(0x1000, 4096, MemoryPerms::READ | MemoryPerms::EXEC);
        // Write some "code" (NOPs or infinite loop: EB FE)
        mmu->write<uint8_t>(0x1000, 0xEB);
        mmu->write<uint8_t>(0x1001, 0xFE);

        // Test Read
        uint8_t opcode = mmu->read<uint8_t>(ctx->eip);
        printf("Read opcode at Entry: 0x%02X\n", opcode);

        // Test Hooks
        hooks->register_interrupt(0x80, [](Context* c) {
            printf("Syscall Hook Hit!\n");
            return true;
        });

        if (hooks->handle_interrupt(ctx, 0x80)) {
            printf("Interrupt handled.\n");
        };

    } catch (const std::exception& e) {
        printf("Exception: %s\n", e.what());
    }
}

}  // namespace x86emu

int main(int argc, char** argv) {
    x86emu::SoftMMU mmu;
    x86emu::HookManager hooks;
    x86emu::Context ctx = {};

    // Link environment
    ctx.mmu = &mmu;
    ctx.hooks = &hooks;
    ctx.eip = 0x1000;

    printf("x86emu initialized.\n");
    x86emu::Run(&ctx);

    return 0;
}
