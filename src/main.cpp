#include <cstdio>
#include "common.h"

namespace x86emu {

    void Run(Context* ctx) {
        printf("Starting execution at EIP: 0x%08x\n", ctx->eip);
        // Dispatch loop will go here
    }

}

int main(int argc, char** argv) {
    x86emu::Context ctx = {};
    ctx.eip = 0x1000; // Mock entry point

    printf("x86emu initialized.\n");
    x86emu::Run(&ctx);

    return 0;
}
