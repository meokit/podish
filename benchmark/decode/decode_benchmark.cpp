#include "bindings.h"
#include "state.h"
#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <vector>

using namespace fiberish;

int main(int argc, char** argv) {
    const int batches = argc > 1 ? std::atoi(argv[1]) : 100;
    if (batches <= 0) {
        std::fprintf(stderr, "Usage: decode_benchmark [positive batch count]\n");
        return 1;
    }
    const char* scenario = argc > 2 ? argv[2] : "mixed";
    std::vector<unsigned char> code;
    unsigned expected = 0;
    auto append = [&](std::initializer_list<unsigned char> bytes) {
        code.insert(code.end(), bytes);
        ++expected;
    };
    if (std::strcmp(scenario, "mixed") == 0) {
        append({0x55});
        append({0x89, 0xe5});
        append({0x83, 0xec, 0x10});
        append({0x8b, 0x45, 0x08});
        append({0x8b, 0x4d, 0x0c});
        append({0x01, 0xc8});
        append({0x8d, 0x54, 0x80, 0x04});
        append({0x83, 0xfa, 0x20});
        append({0x75, 0x02});
    } else if (std::strcmp(scenario, "fusion") == 0) {
        append({0xc1, 0xe8, 0x03});
        append({0x89, 0xf2});
        append({0x0f, 0xb6, 0x1c, 0x77});
        append({0x8b, 0x0c, 0x77});
    } else if (std::strcmp(scenario, "handwritten") == 0) {
        append({0x8b, 0x04, 0x24});
        append({0xc3});
    } else if (std::strcmp(scenario, "register") == 0) {
        for (int i = 0; i < 32; ++i)
            append({0x01, 0xc8});
    } else if (std::strcmp(scenario, "memory") == 0) {
        for (int i = 0; i < 32; ++i)
            append({0x8b, 0x54, 0x88, 0x20});
    } else if (std::strcmp(scenario, "prefix") == 0) {
        for (int i = 0; i < 32; ++i)
            append({0x66, 0x67, 0x8b, 0x40, 0x10});
    } else {
        std::fprintf(stderr, "Unknown scenario: %s\n", scenario);
        return 1;
    }
    constexpr uint32_t address = 0x1000;
    constexpr int per_batch = 1000;
    auto* state = X86_Create();
    state->status = EmuStatus::Running;

    auto* page = state->mmu.allocate_page(address, static_cast<uint8_t>(mem::Property::RX));
    if (!page)
        return 1;
    std::memcpy(page, code.data(), code.size());
    uint64_t instructions = 0, bytes = 0;
    double seconds = 0;
    for (int batch = -1; batch < batches; ++batch) {
        state->mmu.reset_code_cache(); // Outside timing; bound block-pool memory.
        uint64_t batch_instructions = 0, batch_bytes = 0;
        auto start = std::chrono::steady_clock::now();
        for (int i = 0; i < per_batch; ++i) {
            auto* block = DecodeBlock(state, address, address + code.size(), 0);
            if (!block || block->end_eip != address + code.size() || block->inst_count() != expected) {
                std::fprintf(stderr, "Unexpected decoded block: end=%x count=%u\n", block ? block->end_eip : 0,
                             block ? block->inst_count() : 0);
                std::fprintf(stderr, "iteration=%d batch=%d pending=%d\n", i, batch,
                             state->mmu.code_cache_flush_pending());
                X86_Destroy(state);
                return 1;
            }
            batch_instructions += block->inst_count();
            batch_bytes += block->end_eip - address;
        }
        auto end = std::chrono::steady_clock::now();
        if (batch >= 0) {
            seconds += std::chrono::duration<double>(end - start).count();
            instructions += batch_instructions;
            bytes += batch_bytes;
        }
    }
    const double blocks = static_cast<double>(batches) * per_batch;
    std::printf(
        "scenario=%s blocks=%.0f instructions=%llu bytes=%llu seconds=%.6f ns/block=%.2f M inst/s=%.2f MiB/s=%.2f\n",
        scenario, blocks, (unsigned long long)instructions, (unsigned long long)bytes, seconds, seconds * 1e9 / blocks,
        instructions / seconds / 1e6, bytes / seconds / (1024 * 1024));
    X86_Destroy(state);
}
