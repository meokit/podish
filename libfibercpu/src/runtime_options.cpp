#include "bindings.h"
#include "runtime_options.h"

#include <algorithm>
#include <charconv>
#include <cstdlib>
#include <cstring>
#include <iterator>
#include <limits>
#include <utility>

#if FIBERCPU_LLVM_JIT
#include "jit/llvm_block_jit.h"
#endif
#if FIBERCPU_DYNAMIC_OPERANDS || FIBERCPU_LLVM_JIT
#include "jit/executable_arena.h"
#endif

using namespace fiberish;

namespace {

std::string_view Trim(std::string_view text) {
    constexpr std::string_view whitespace = " \t\r\n\v\f";
    const auto first = text.find_first_not_of(whitespace);
    if (first == text.npos) return {};
    return text.substr(first, text.find_last_not_of(whitespace) - first + 1);
}

bool ReadSize(std::string_view text, size_t& bytes) {
    text = Trim(text);
    if (text.empty()) return false;
    auto [end, ec] = std::from_chars(text.data(), text.data() + text.size(), bytes);
    if (ec != std::errc{} || !bytes) return false;

    std::string suffix(Trim(text.substr(end - text.data())));
    for (auto& c : suffix)
        if (c >= 'A' && c <= 'Z') c += 'a' - 'A';
    size_t scale;
    if (suffix.empty() || suffix == "b") scale = 1;
    else if (suffix == "k" || suffix == "kb" || suffix == "ki" || suffix == "kib") scale = size_t{1} << 10;
    else if (suffix == "m" || suffix == "mb" || suffix == "mi" || suffix == "mib") scale = size_t{1} << 20;
    else if (suffix == "g" || suffix == "gb" || suffix == "gi" || suffix == "gib") scale = size_t{1} << 30;
    else return false;
    if (bytes > std::numeric_limits<size_t>::max() / scale) return false;
    bytes *= scale;
    return true;
}

template <typename T>
bool ReadNumber(std::string_view text, T& result) {
    if (text.empty()) return false;
    const auto* end = text.data() + text.size();
    auto parsed = std::from_chars(text.data(), end, result);
    return parsed.ec == std::errc{} && parsed.ptr == end;
}

void WriteError(char* buffer, size_t size, std::string_view message) {
    if (!buffer || !size) return;
    const size_t n = std::min(size - 1, message.size());
    if (n) std::memcpy(buffer, message.data(), n);
    buffer[n] = '\0';
}

}  // namespace

X86_RunContext* X86_CreateRun(const char* args, size_t size, char* error, size_t error_size) {
    WriteError(error, error_size, {});
    const auto fail = [&](std::string_view message) -> X86_RunContext* {
        WriteError(error, error_size, message);
        return nullptr;
    };
    if (size && !args) return fail("Missing CPU options buffer");

    // Keep only the final value of each key before validating values. This is
    // also how a start-time value overrides an option saved by run.
    enum Key { BlockSize, ArenaSize, Mode, Threshold, Workers, Pipeline, Count };
    constexpr std::string_view names[] = {
        "BlockCacheSize", "JitArenaSize", "JitMode", "JitHotThreshold",
        "JitWorkers", "LlvmPipeline",
    };
    static_assert(std::size(names) == Count);
    std::optional<std::string_view> values[Count];
    for (std::string_view remaining(args ? args : "", size); !remaining.empty();) {
        const size_t end = remaining.find('\0');
        if (end == remaining.npos) return fail("Unterminated CPU option");
        const auto arg = remaining.substr(0, end);
        remaining.remove_prefix(end + 1);
        const size_t equals = arg.find('=');
        if (equals == arg.npos || equals == 0) return fail("CPU options must use Name=Value");
        const auto name = arg.substr(0, equals);
        const auto* key = std::find(std::begin(names), std::end(names), name);
        if (key == std::end(names)) return fail("Unknown CPU option '" + std::string(name) + "'");
        values[key - names] = arg.substr(equals + 1);
    }

    auto options = std::make_shared<CpuRunOptions>();
    auto run = std::make_unique<X86_RunContext>();
    SharedJitOptions shared;
    if (values[BlockSize] && !ReadSize(*values[BlockSize], options->block_cache_bytes))
        return fail("BlockCacheSize requires a positive size in bytes, optionally suffixed with k, m or g");
    if (values[ArenaSize] && !ReadSize(*values[ArenaSize], shared.arena_bytes))
        return fail("JitArenaSize requires a positive size in bytes, optionally suffixed with k, m or g");
    if (values[Mode]) {
        const auto mode = *values[Mode];
        if (mode == "off") options->jit_mode = JitMode::Off;
        else if (mode == "generic") options->jit_mode = JitMode::Generic;
        else if (mode == "exact") options->jit_mode = JitMode::Exact;
        else return fail("JitMode requires off, generic or exact");
    }
    if (values[Threshold]) {
        uint16_t threshold = 0;
        if (!ReadNumber(*values[Threshold], threshold) || !threshold)
            return fail("JitHotThreshold requires an integer in 1..65535");
        options->hot_threshold = threshold;
    }
    if (values[Workers]) {
        unsigned workers = 0;
        if (!ReadNumber(*values[Workers], workers) || workers > 8)
            return fail("JitWorkers requires an integer in 0..8");
        shared.workers = workers;
    }
    if (values[Pipeline]) {
        if (values[Pipeline]->empty()) return fail("LlvmPipeline requires an optimization level or pass pipeline");
        shared.pipeline = values[Pipeline];
    }
    const bool llvm_requested = (options->jit_mode && options->jit_mode != JitMode::Off) ||
                                options->hot_threshold || shared.workers || shared.pipeline;
#if FIBERCPU_LLVM_JIT
    if (llvm_requested && !LLVMBlockJitSupported())
        return fail("LLVM CPU options require LLVM block JIT support on this host");
    if (shared.workers || shared.pipeline || shared.arena_bytes) {
        std::string diagnostic;
        if (!ConfigureLLVMBlockJit(shared, diagnostic)) return fail(diagnostic);
    }
#else
    if (llvm_requested) return fail("LLVM CPU options require a build with LLVM block JIT");
#if FIBERCPU_DYNAMIC_OPERANDS
    if (shared.arena_bytes && !ExecutableArena::Shared(shared.arena_bytes))
        return fail("JitArenaSize conflicts with the existing arena or could not be allocated");
#else
    if (shared.arena_bytes) return fail("JitArenaSize requires an executable arena in this build");
#endif
#endif
    run->options = std::move(options);
    return run.release();
}

void X86_DestroyRun(X86_RunContext* run) { delete run; }
