#pragma once
#include <ankerl/unordered_dense.h>
#include <algorithm>
#include <array>
#include <bit>
#include <vector>
#include "dispatch.h"

namespace fiberish {
HandlerFunc LowerOperandHandler(const DecodedOp* op);
HandlerFunc CanonicalOperandHandler(HandlerFunc handler);
bool AcceptFusionOperandShape(const DecodedOp* op, HandlerFunc handler);
HandlerFunc LowerFusionOperandHandler(const DecodedOp* op, HandlerFunc handler);
struct OperandShapeEntry {
    HandlerFunc original;
    HandlerFunc replacement;
    uint64_t shapes[4];
    uint8_t width;
};
using RegisterOperandSelector = HandlerFunc (*)(const DecodedOp*);
struct RegisterOperandEntry {
    HandlerFunc original;
    RegisterOperandSelector select;
};

template <LogicFunc Target, PinWriteRole Role, bool Byte, bool Group1>
HandlerFunc SelectRegisterOperand(const DecodedOp* op) {
    if constexpr (Role == PinWriteRole::None) {
        return DispatchWrapper<Target, RegisterOperandPolicy<PinWriteRole::None, PinWriteClass::Other, Byte>>;
    } else {
        if constexpr (Group1)
            if (((op->modrm >> 3) & 7) == 7)
                return DispatchWrapper<Target, RegisterOperandPolicy<PinWriteRole::None, PinWriteClass::Other, Byte>>;
        const unsigned field = Role == PinWriteRole::Rm ? op->modrm : op->modrm >> 3;
        const unsigned parent = field & (Byte ? 3 : 7);
        if (parent == 0) return DispatchWrapper<Target, RegisterOperandPolicy<Role, PinWriteClass::Eax, Byte>>;
        if constexpr (!Byte)
            if (parent == 4) return DispatchWrapper<Target, RegisterOperandPolicy<Role, PinWriteClass::Esp, Byte>>;
        return DispatchWrapper<Target, RegisterOperandPolicy<Role, PinWriteClass::Other, Byte>>;
    }
}

FORCE_INLINE uint64_t OperandShape(const DecodedOp* op) {
    const uint32_t ea = op->meta.flags.has_mem ? GetExt(op)->data.ea_desc : 0;
    return op->modrm | (uint64_t(op->prefixes.all) << 8) | (uint64_t(ea) << 16);
}

template <bool HasRegisters>
class OperandIndex {
    struct Key {
        HandlerFunc handler;
        uint64_t shape;
        bool operator==(const Key&) const = default;
    };
    struct KeyHash {
        uint64_t operator()(const Key& key) const {
            return ankerl::unordered_dense::hash<uint64_t>{}(reinterpret_cast<uintptr_t>(key.handler) ^
                                                             std::rotl(key.shape, 17));
        }
    };
    ankerl::unordered_dense::map<Key, std::vector<const OperandShapeEntry*>, KeyHash> shapes_;
    ankerl::unordered_dense::map<HandlerFunc, RegisterOperandSelector> registers_;
    std::array<uint64_t, 16> bits_{};
    static uint64_t HandlerHash(HandlerFunc handler) {
        return (reinterpret_cast<uintptr_t>(handler) >> 2) * uint64_t{0x9e3779b97f4a7c15ULL};
    }
    void Remember(HandlerFunc handler) {
        const auto h = HandlerHash(handler);
        bits_[(h >> 6) & 15] |= uint64_t{1} << (h & 63);
    }

public:
    OperandIndex(const OperandShapeEntry* shapes, size_t shape_count, const RegisterOperandEntry* registers,
                 size_t register_count) {
        shapes_.reserve(shape_count);
        for (size_t i = 0; i < shape_count; ++i) {
            const auto& entry = shapes[i];
            shapes_[{entry.original, entry.shapes[0]}].push_back(&entry);
            Remember(entry.original);
        }
        if constexpr (HasRegisters) {
            registers_.reserve(register_count);
            for (size_t i = 0; i < register_count; ++i) {
                registers_[registers[i].original] = registers[i].select;
                Remember(registers[i].original);
            }
        }
    }
    // Key by the first operand shape as well as semantic identity. Misses must
    // not scan every observed shape of a common MOV/ALU handler during decode.
    FORCE_INLINE HandlerFunc Find(const DecodedOp* op, HandlerFunc original = nullptr) const {
        const auto handler = original ? original : op->handler;
        const auto h = HandlerHash(handler);
        if (!(bits_[(h >> 6) & 15] & (uint64_t{1} << (h & 63)))) return nullptr;
        const auto found = shapes_.find({handler, OperandShape(op)});
        if (found != shapes_.end()) {
            for (const auto* candidate : found->second) {
                bool match = true;
                for (unsigned i = 1; i < candidate->width; ++i)
                    if (OperandShape(op + i) != candidate->shapes[i]) {
                        match = false;
                        break;
                    }
                if (match) return candidate->replacement;
            }
        }
        if constexpr (HasRegisters) {
            if (op->meta.flags.has_modrm && !op->meta.flags.has_mem && (op->modrm & 0xc0) == 0xc0 &&
                !op->prefixes.flags.lock) {
                const auto reg = registers_.find(handler);
                if (reg != registers_.end()) return reg->second(op);
            }
        }
        return nullptr;
    }
};
}  // namespace fiberish
