#include <charconv>
#include <cstdio>
#include <string_view>

#include "jit/llvm_block_jit.h"

namespace {

int Usage() {
    std::fprintf(stderr, "usage: fibercpu_llvm_cache --linked PROFILE BUNDLE [--top N] "
                         "[--llvm-opt-level LEVEL | --llvm-pass-pipeline PIPELINE] [--regions JIT_TRACE]\n");
    return 2;
}

}  // namespace

int main(int argc, char** argv) {
    if (argc < 4 || std::string_view(argv[1]) != "--linked") return Usage();

    const char* opt_level = nullptr;
    const char* pipeline = nullptr;
    const char* regions = nullptr;
    uint32_t limit = 0;
    for (int i = 4; i < argc; i += 2) {
        if (i + 1 == argc || !*argv[i + 1]) return Usage();
        const std::string_view option = argv[i];
        const char* value = argv[i + 1];
        if (option == "--top" && !limit) {
            const auto end = value + std::string_view(value).size();
            const auto parsed = std::from_chars(value, end, limit);
            if (parsed.ec != std::errc{} || parsed.ptr != end || !limit) return Usage();
        } else if (option == "--llvm-opt-level" && !opt_level && !pipeline) {
            opt_level = value;
        } else if (option == "--llvm-pass-pipeline" && !pipeline && !opt_level) {
            pipeline = value;
        } else if (option == "--regions" && !regions) {
            regions = value;
        } else {
            return Usage();
        }
    }

    const auto* profile = argv[2];
    const auto* bundle = argv[3];
    const int objects = fiberish::BuildLinkedLLVMBlockCache(profile, bundle, limit, opt_level, pipeline, regions);
    if (objects < 0) {
        std::fprintf(stderr, "Could not build linked block bundle from %s\n", profile);
        return 1;
    }
    std::printf("Compiled %d LLVM block objects into %s\n", objects, bundle);
}
