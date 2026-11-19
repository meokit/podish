#pragma once

#include <cstddef>
#include <cstdint>
#include <memory>
#include <optional>
#include <string>
#include <string_view>

namespace fiberish {

enum class JitMode { Off, Generic, Exact, LegacyProfile, Profile };
struct WeightedProfile;

struct CpuRunOptions {
    size_t block_cache_bytes = 0;
    std::optional<JitMode> jit_mode;
    std::optional<uint16_t> hot_threshold;
    std::shared_ptr<WeightedProfile> profile;
};

struct SharedJitOptions {
    std::optional<unsigned> workers;
    std::optional<std::string_view> pipeline;
    size_t arena_bytes = 0;
};

}  // namespace fiberish

struct X86_RunContext {
    std::shared_ptr<const fiberish::CpuRunOptions> options;
    std::string profile_path;
};
