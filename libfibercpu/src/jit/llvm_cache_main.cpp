#include <cstdio>
#include <cstring>

#include "bindings.h"

int main(int argc, char** argv) {
    const bool linked = argc == 4 && std::strcmp(argv[1], "--linked") == 0;
    if (argc != 3 && !linked) {
        std::fprintf(stderr, "usage: fibercpu_llvm_cache [--linked] PROFILE CACHE\n");
        return 2;
    }
    const auto* profile = argv[linked ? 2 : 1];
    const auto* cache = argv[linked ? 3 : 2];
    const int objects = linked ? X86_BuildLinkedLLVMBlockCache(profile, cache) : X86_BuildLLVMJitCache(profile, cache);
    if (objects < 0) {
        std::fprintf(stderr, "Could not build LLVM cache from %s\n", profile);
        return 1;
    }
    std::printf("Compiled %d LLVM block objects into %s\n", objects, cache);
}
