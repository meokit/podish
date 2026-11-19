#include <cstdio>
#include <cstring>

#include "bindings.h"

int main(int argc, char** argv) {
    if (argc != 4 || std::strcmp(argv[1], "--linked") != 0) {
        std::fprintf(stderr, "usage: fibercpu_llvm_cache --linked PROFILE BUNDLE\n");
        return 2;
    }
    const auto* profile = argv[2];
    const auto* bundle = argv[3];
    const int objects = X86_BuildLinkedLLVMBlockCache(profile, bundle);
    if (objects < 0) {
        std::fprintf(stderr, "Could not build linked block bundle from %s\n", profile);
        return 1;
    }
    std::printf("Compiled %d LLVM block objects into %s\n", objects, bundle);
}
