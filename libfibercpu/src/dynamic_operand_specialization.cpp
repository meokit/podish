#include "dynamic_operand_specialization.h"

#include <ankerl/unordered_dense.h>
#include <algorithm>
#include <atomic>
#include <cstdlib>
#include <cstring>
#include <mutex>
#include <span>
#include <tuple>
#include <vector>

#include "dispatch.h"
#include "instruction_variants.h"
#include "jit/executable_arena.h"
#include "superopcodes.h"

namespace fiberish {
namespace {

struct DynamicOperandEntry {
    DecodedOperandShape shape;
    uint32_t recipe, tail;
};
struct OperandFamily {
    HandlerFunc handler;
    uint32_t first, count;
    uint8_t width;
    SpecCriteria criteria;
};

struct ImmediatePatch {
    uint32_t word;
    uint32_t mask;
};
struct HelperPatch {
    uint32_t word;
    uint32_t target;
};
enum class DataRelocation : uint8_t { Page, PageOffset };
struct DataPatch {
    uint32_t word;
    uint32_t offset;
    uint32_t helper;
    DataRelocation kind;
    uint8_t scale;
};
struct CodeTemplate {
    uint32_t code, words, entry;
    uint32_t patches, patch_count;
    uint32_t helpers, helper_count;
    uint32_t relocations, relocation_count;
    uint32_t constants, constant_bytes;
};
struct CodeRecipe {
    uint32_t stencil, payload;
};

}  // namespace
}  // namespace fiberish

#include "dynamic_operand_bundle.inc"

namespace fiberish {
namespace {

bool DynamicOperandsEnabled() {
    static const bool enabled = [] {
        const char* value = std::getenv("FIBERCPU_DYNAMIC_SPECIALIZATION");
        return !value || std::strcmp(value, "0") != 0;
    }();
    return enabled;
}

class ExecutableOperands {
    struct Published {
        HandlerFunc original;
        uint32_t recipe;
    };
    std::mutex mutex_;
    std::array<std::atomic<HandlerFunc>, kRecipeCount> handlers_{};
    ankerl::unordered_dense::map<HandlerFunc, Published> canonical_;
    const std::unique_ptr<ExecutableArena::Region> region_;
    size_t used_ = 0;

    static std::unique_ptr<ExecutableArena::Region> ReserveRegion() {
        if (kArenaSize == 0 || !DynamicOperandsEnabled()) return {};
        auto arena = ExecutableArena::Shared();
        return arena ? arena->Reserve(kArenaSize, ExecutableArena::Lifetime::Permanent) : nullptr;
    }

public:
    ExecutableOperands() : region_(ReserveRegion()) {}

    HandlerFunc Find(uint32_t recipe_id, HandlerFunc original) {
        if (!region_) return nullptr;
        auto& published = handlers_[recipe_id];
        if (auto handler = published.load(std::memory_order_acquire)) return handler;

        std::lock_guard lock(mutex_);
        if (auto handler = published.load(std::memory_order_relaxed)) return handler;

        const auto& recipe = kRecipes[recipe_id];
        const auto& stencil = kTemplates[recipe.stencil];
        const size_t code_size = stencil.words * sizeof(uint32_t);
        const size_t veneer_offset = (code_size + 15) & ~size_t{15};
        const size_t constants_offset = veneer_offset + 16 * stencil.helper_count;
        const size_t allocation = constants_offset + ((stencil.constant_bytes + 15) & ~size_t{15});
        if (allocation > kArenaSize - used_) return nullptr;
        auto* dst = region_->Data() + used_;
        const auto handler = reinterpret_cast<HandlerFunc>(dst + stencil.entry);
        canonical_.emplace(handler, Published{original, recipe_id});

        {
            ScopedJitWrite write;
            std::memcpy(dst, kCode + stencil.code, code_size);
            auto* words = reinterpret_cast<uint32_t*>(dst);
            for (uint32_t i = 0; i < stencil.patch_count; ++i) {
                const auto& patch = kPatches[stencil.patches + i];
                words[patch.word] = (words[patch.word] & ~patch.mask) | kPayload[recipe.payload + i];
            }
            for (uint32_t i = 0; i < stencil.helper_count; ++i) {
                const auto& fixup = kHelpers[stencil.helpers + i];
                const size_t veneer_word = veneer_offset / sizeof(uint32_t) + 4 * i;
                // Branch to a nearby ABI veneer, which loads and transfers to the
                // helper without changing the link register or handler arguments.
                words[fixup.word] |= static_cast<uint32_t>(veneer_word - fixup.word);
                words[veneer_word] = 0x58000050;      // ldr x16, #8
                words[veneer_word + 1] = 0xd61f0200;  // br x16
                const auto target = kHelperTargets[fixup.target];
                std::memcpy(words + veneer_word + 2, &target, sizeof(target));
            }
            if (stencil.constant_bytes) {
                auto* constants = dst + constants_offset;
                std::memcpy(constants, kConstantPool + stencil.constants, stencil.constant_bytes);
                for (uint32_t i = 0; i < stencil.relocation_count; ++i) {
                    const auto& fixup = kDataRelocs[stencil.relocations + i];
                    auto* slot = constants + fixup.offset;
                    if (fixup.helper != UINT32_MAX) std::memcpy(slot, &kHelperTargets[fixup.helper], sizeof(uintptr_t));
                    const auto target = reinterpret_cast<uintptr_t>(slot);
                    if (fixup.kind == DataRelocation::Page) {  // ADRP uses architectural 4 KiB pages.
                        const auto address = reinterpret_cast<uintptr_t>(words + fixup.word);
                        const auto pages = static_cast<uint32_t>((target >> 12) - (address >> 12));
                        words[fixup.word] |= ((pages & 3) << 29) | (((pages >> 2) & 0x7ffff) << 5);
                    } else {
                        words[fixup.word] |= static_cast<uint32_t>(((target & 4095) >> fixup.scale) << 10);
                    }
                }
            }
        }
        FlushJitInstructions(dst, allocation);
        used_ += allocation;
        published.store(handler, std::memory_order_release);
        return handler;
    }

    Published Lookup(HandlerFunc handler) {
        const auto address = reinterpret_cast<uintptr_t>(handler);
        if (!region_ || address - reinterpret_cast<uintptr_t>(region_->Data()) >= kArenaSize)
            return {handler, UINT32_MAX};
        std::lock_guard lock(mutex_);
        const auto found = canonical_.find(handler);
        return found == canonical_.end() ? Published{handler, UINT32_MAX} : found->second;
    }

    HandlerFunc ForId(uint32_t id) const { return handlers_[id].load(std::memory_order_acquire); }
};

ExecutableOperands& Executable() {
    static ExecutableOperands code;
    return code;
}

}  // namespace

HandlerFunc LowerDynamicOperandHandler(const DecodedInstruction* op, HandlerFunc original, unsigned remaining) {
    if (!DynamicOperandsEnabled() || !remaining) return nullptr;
    const auto semantic_id = [](HandlerFunc handler) {
        if (const auto scalar = FindScalarTrainingId(handler)) return scalar;
        return FindFusionTrainingId(handler);
    };
    const auto id = semantic_id(original);
    if (!id) return nullptr;
    static const auto index = [semantic_id] {
        ankerl::unordered_dense::map<uint32_t, std::vector<const OperandFamily*>> families;
        families.reserve(kFamilyCount);
        for (const auto& family : std::span(kFamilies, kFamilyCount)) {
            if (const auto family_id = semantic_id(family.handler)) families[family_id].push_back(&family);
        }
        for (auto& [handler, choices] : families) {
            std::stable_sort(choices.begin(), choices.end(), [](const auto* a, const auto* b) {
                return a->criteria.GetScore() > b->criteria.GetScore();
            });
        }
        return families;
    }();
    const auto found = index.find(id);
    if (found == index.end()) return nullptr;

    const auto shape = DecodedOperandShape::Read(op);
    const auto baseline = BoundOperandShape::Read(*op);
    const auto order = [](const DecodedOperandShape& s) {
        return std::tie(s.encoding, s.ea_desc, s.displacement, s.immediate);
    };
    for (const auto* candidate : found->second) {
        const auto& family = *candidate;
        if (family.width > remaining || !family.criteria.Matches(op)) continue;
        const auto keys = std::span(kEntries + family.first, family.count);
        auto entry = std::ranges::lower_bound(keys, order(shape), {},
                                              [&](const DynamicOperandEntry& key) { return order(key.shape); });
        for (; entry != keys.end() && entry->shape == shape; ++entry) {
            if (baseline != entry->shape.Binding()) continue;
            bool match = true;
            for (unsigned i = 1; i < family.width; ++i) {
                if (DecodedOperandShape::Read(op + i) != kTailShapes[entry->tail + i - 1]) {
                    match = false;
                    break;
                }
            }
            if (match) return Executable().Find(entry->recipe, original);
        }
    }
    return nullptr;
}

HandlerFunc CanonicalDynamicOperandHandler(HandlerFunc handler) { return Executable().Lookup(handler).original; }
size_t DynamicOperandCount() { return kRecipeCount; }
int32_t DynamicOperandId(HandlerFunc handler) {
    const auto id = Executable().Lookup(handler).recipe;
    return id == UINT32_MAX ? -1 : static_cast<int32_t>(id);
}
HandlerFunc DynamicOperandForId(uint32_t id) { return id < kRecipeCount ? Executable().ForId(id) : nullptr; }
const char* DynamicOperandName(uint32_t id) { return id < kRecipeCount ? kRecipeNames[id] : nullptr; }

#ifdef FIBERCPU_VALIDATE_SPECIALIZATION_INDEX
bool ValidateDynamicOperandSpecializations() {
    for (const auto& family : std::span(kFamilies, kFamilyCount)) {
        for (const auto& entry : std::span(kEntries + family.first, family.count)) {
            DecodedInstruction ops[4]{};
            for (unsigned i = 0; i < family.width; ++i) {
                const auto& shape = i == 0 ? entry.shape : kTailShapes[entry.tail + i - 1];
                ops[i].modrm = uint8_t(shape.encoding);
                ops[i].prefixes.all = uint8_t(shape.encoding >> 8);
                ops[i].meta.all = uint8_t(shape.encoding >> 16);
                ops[i].len = uint8_t(shape.encoding >> 24);
                ops[i].ext.data = {shape.immediate, shape.ea_desc, shape.displacement, 0};
            }
            if (!family.criteria.Matches(ops)) continue;
            const auto handler = LowerDynamicOperandHandler(ops, family.handler, family.width);
            if (!handler || handler == family.handler || CanonicalDynamicOperandHandler(handler) != family.handler ||
                LowerDynamicOperandHandler(ops, family.handler, family.width) != handler ||
                LowerDynamicOperandHandler(ops, family.handler, family.width - 1)) {
                return false;
            }
            ops[0].len = 0;
            if (LowerDynamicOperandHandler(ops, family.handler, family.width)) return false;
        }
    }
    return true;
}
#endif

}  // namespace fiberish
