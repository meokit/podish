#pragma once

#include <cstddef>
#include <cstdint>
#include <map>
#include <memory>
#include <mutex>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

namespace fiberish {

enum class JitMode { Off, Generic, Exact, Region, LegacyProfile, Profile };
struct WeightedProfile;

struct ArmInstructionProfile {
    std::mutex mutex;
    std::map<uint32_t, uint64_t> words;
    std::map<uint64_t, uint64_t> pairs;
    std::map<std::vector<uint32_t>, uint64_t> sequences;
    std::string path;
};

struct CpuRunOptions {
    std::shared_ptr<ArmInstructionProfile> arm_profile;
    size_t block_cache_bytes = 0;
    std::optional<JitMode> jit_mode;
    std::optional<uint16_t> hot_threshold;
    std::shared_ptr<WeightedProfile> profile;
};

struct SharedJitOptions {
    std::optional<unsigned> workers;
    std::optional<std::string_view> pipeline;
    std::optional<bool> fold_operand_loads;
    size_t arena_bytes = 0;
};

}  // namespace fiberish

struct X86_RunContext {
    std::shared_ptr<const fiberish::CpuRunOptions> options;
    std::string profile_path;
};
