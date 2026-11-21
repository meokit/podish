#pragma once

#include <ankerl/unordered_dense.h>
#include <array>
#include <bit>
#include <span>
#include <vector>

#include "dispatch.h"

namespace fiberish {

HandlerFunc CanonicalOperandHandler(HandlerFunc handler);
HandlerFunc UnfuseOperandHandler(HandlerFunc handler);
HandlerFunc LowerFusionOperandHandler(const DecodedInstruction* op, HandlerFunc handler);
unsigned BakedFusionWidth(HandlerFunc handler);

struct ProfileOperandShapeEntry {
    HandlerFunc original;
    HandlerFunc replacement;
    DecodedOperandShape shapes[4];
    uint8_t width;
    HandlerFunc first = nullptr;
};

class OperandIndex {
    struct Key {
        HandlerFunc handler;
        DecodedOperandShape shape;
        bool operator==(const Key&) const = default;
    };
    struct KeyHash {
        uint64_t operator()(const Key& key) const {
            const uint64_t layout = (uint64_t(key.shape.ea_desc) << 32) | key.shape.encoding;
            const uint64_t values = (uint64_t(key.shape.displacement) << 32) | key.shape.immediate;
            const auto handler = reinterpret_cast<uintptr_t>(key.handler);
            auto hash = ankerl::unordered_dense::hash<uint64_t>{};
            return hash(handler ^ std::rotl(layout, 17) ^ hash(values));
        }
    };
    ankerl::unordered_dense::map<Key, std::vector<const ProfileOperandShapeEntry*>, KeyHash> shapes_;
    std::array<uint64_t, 16> bits_{};

    static uint64_t HandlerHash(HandlerFunc handler) {
        return (reinterpret_cast<uintptr_t>(handler) >> 2) * uint64_t{0x9e3779b97f4a7c15ULL};
    }

public:
    explicit OperandIndex(std::span<const ProfileOperandShapeEntry> shapes) {
        shapes_.reserve(shapes.size());
        for (const auto& entry : shapes) {
            shapes_[{entry.original, entry.shapes[0]}].push_back(&entry);
            const auto hash = HandlerHash(entry.original);
            bits_[(hash >> 6) & 15] |= uint64_t{1} << (hash & 63);
        }
    }

    FORCE_INLINE HandlerFunc Find(const DecodedInstruction* op, HandlerFunc original) const {
        const auto hash = HandlerHash(original);
        if (!(bits_[(hash >> 6) & 15] & (uint64_t{1} << (hash & 63)))) return nullptr;
        const auto found = shapes_.find({original, DecodedOperandShape::Read(op)});
        if (found == shapes_.end()) return nullptr;
        for (const auto* candidate : found->second) {
            unsigned index = 1;
            while (index < candidate->width && DecodedOperandShape::Read(op + index) == candidate->shapes[index])
                ++index;
            if (index == candidate->width) return candidate->replacement;
        }
        return nullptr;
    }
};

}  // namespace fiberish
