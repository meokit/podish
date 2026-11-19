#include <cstdio>

#include "bindings.h"

int main(int argc, char** argv) {
    if (argc != 3) {
        std::fprintf(stderr, "usage: fibercpu_llvm_cache PROFILE CACHE\n");
        return 2;
    }
    const int objects = X86_BuildLLVMJitCache(argv[1], argv[2]);
    if (objects < 0) {
        std::fprintf(stderr, "Could not build LLVM cache from %s\n", argv[1]);
        return 1;
    }
    std::printf("Compiled %d LLVM block objects into %s\n", objects, argv[2]);
}
