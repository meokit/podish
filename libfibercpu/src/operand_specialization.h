#pragma once
#include <ankerl/unordered_dense.h>
#include <algorithm>
#include <array>
#include <bit>
#include <type_traits>
#include <utility>
#include <vector>
#include "dispatch.h"

namespace fiberish {
HandlerFunc LowerOperandHandler(const DecodedInstruction* op);
HandlerFunc CanonicalOperandHandler(HandlerFunc handler);
bool AcceptFusionOperandShape(const DecodedInstruction* op, HandlerFunc handler);
HandlerFunc LowerFusionOperandHandler(const DecodedInstruction* op, HandlerFunc handler);
template <typename Shape>
struct BasicOperandShapeEntry {
    HandlerFunc original;
    HandlerFunc replacement;
    Shape shapes[4];
    uint8_t width;
};
using OperandShapeEntry = BasicOperandShapeEntry<uint64_t>;
using ProfileOperandShapeEntry = BasicOperandShapeEntry<DecodedOperandShape>;
using RegisterOperandSelector = HandlerFunc (*)(const DecodedInstruction*);
enum class PinOperandForm : uint8_t { Register, Opcode, Address };
struct RegisterOperandEntry {
    HandlerFunc original;
    RegisterOperandSelector select;
    PinOperandForm form = PinOperandForm::Register;
};

template <LogicFunc Target, PinWriteRole Role, bool Byte, bool Group1>
HandlerFunc SelectRegisterOperand(const DecodedInstruction* op) {
    if constexpr (Role == PinWriteRole::None) {
        return DispatchWrapper<Target, OperandSpec::Register<PinWriteRole::None, PinWriteClass::Other, Byte>>;
    } else {
        if constexpr (Group1)
            if (((op->modrm >> 3) & 7) == 7)
                return DispatchWrapper<Target, OperandSpec::Register<PinWriteRole::None, PinWriteClass::Other, Byte>>;
        const unsigned field = Role == PinWriteRole::Rm ? op->modrm : op->modrm >> 3;
        const unsigned parent = field & (Byte ? 3 : 7);
#define FIBERCPU_SELECT_PIN(id, name, slot, ...)                                                               \
    if constexpr (!Byte || id < 4) {                                                                           \
        if (parent == id)                                                                                      \
            return DispatchWrapper<Target, OperandSpec::Register<Role, static_cast<PinWriteClass>(id), Byte>>; \
    }
        FIBERCPU_FOR_EACH_PIN(FIBERCPU_SELECT_PIN)
#undef FIBERCPU_SELECT_PIN
        return DispatchWrapper<Target, OperandSpec::Register<Role, PinWriteClass::Other, Byte>>;
    }
}

template <LogicFunc Target, PinWriteRole Role, bool Byte, bool OpcodeRegister>
HandlerFunc SelectEncodedOperand(const DecodedInstruction* op) {
    if constexpr (Role == PinWriteRole::None) {
        return DispatchWrapper<Target, OperandSpec::Encoded<Role, PinWriteClass::Other, Byte, OpcodeRegister>>;
    } else {
        const unsigned field = Role == PinWriteRole::Reg ? op->modrm >> 3 : op->modrm;
        const unsigned parent = field & (Byte ? 3 : 7);
#define FIBERCPU_SELECT_PIN(id, name, slot, ...)                                                                      \
    if constexpr (!Byte || id < 4) {                                                                                  \
        if (parent == id)                                                                                             \
            return DispatchWrapper<Target,                                                                            \
                                   OperandSpec::Encoded<Role, static_cast<PinWriteClass>(id), Byte, OpcodeRegister>>; \
    }
        FIBERCPU_FOR_EACH_PIN(FIBERCPU_SELECT_PIN)
#undef FIBERCPU_SELECT_PIN
        return DispatchWrapper<Target, OperandSpec::Encoded<Role, PinWriteClass::Other, Byte, OpcodeRegister>>;
    }
}

constexpr size_t kBaseDisplacementVariantCount = 1 + kNoRegOffset / sizeof(uint32_t);

template <LogicFunc Target, bool Operand16, size_t... Base>
constexpr auto MakeBaseDisplacementVariants(std::index_sequence<Base...>) {
    return std::array<HandlerFunc, sizeof...(Base)>{
        DispatchWrapper<Target, OperandSpec::BaseDisplacement<Base, Operand16>>...};
}
template <LogicFunc Target, bool Operand16>
inline constexpr auto BaseDisplacementVariants =
    MakeBaseDisplacementVariants<Target, Operand16>(std::make_index_sequence<kBaseDisplacementVariantCount>{});

FORCE_INLINE uint64_t OperandShape(const DecodedInstruction* op) {
    const uint32_t ea = op->meta.flags.has_mem ? GetExt(op)->data.ea_desc : 0;
    // Encoding-only plans train the single-slot shape. Exact decoded plans
    // can also select a two-slot handler using the full immediate and prefixes.
    const uint64_t separate = compact_imm::NeedsSeparateSlot(*op);
    return op->modrm | (uint64_t(op->prefixes.all) << 8) | (uint64_t(ea) << 16) | (separate << 48);
}

template <bool HasRegisters, bool HasEncoded = false, typename Shape = uint64_t>
class OperandIndex {
    using Entry = BasicOperandShapeEntry<Shape>;

    static FORCE_INLINE Shape ReadShape(const DecodedInstruction* op) {
        if constexpr (std::is_same_v<Shape, uint64_t>) {
            return OperandShape(op);
        } else {
            return Shape::Read(op);
        }
    }

    struct Key {
        HandlerFunc handler;
        Shape shape;
        bool operator==(const Key&) const = default;
    };
    struct KeyHash {
        uint64_t operator()(const Key& key) const {
            const auto handler = reinterpret_cast<uintptr_t>(key.handler);
            if constexpr (std::is_same_v<Shape, uint64_t>) {
                return ankerl::unordered_dense::hash<uint64_t>{}(handler ^ std::rotl(key.shape, 17));
            } else {
                const uint64_t layout = (uint64_t(key.shape.ea_desc) << 32) | key.shape.encoding;
                const uint64_t values = (uint64_t(key.shape.displacement) << 32) | key.shape.immediate;
                auto hash = ankerl::unordered_dense::hash<uint64_t>{};
                return hash(handler ^ std::rotl(layout, 17) ^ hash(values));
            }
        }
    };
    ankerl::unordered_dense::map<Key, std::vector<const Entry*>, KeyHash> shapes_;
    ankerl::unordered_dense::map<HandlerFunc, RegisterOperandSelector> registers_;
    std::array<uint64_t, 16> bits_{};
    struct NoEncodedOperands {};
    [[no_unique_address]] std::conditional_t<
        HasEncoded, ankerl::unordered_dense::map<HandlerFunc, RegisterOperandEntry>, NoEncodedOperands> encoded_;
    static uint64_t HandlerHash(HandlerFunc handler) {
        return (reinterpret_cast<uintptr_t>(handler) >> 2) * uint64_t{0x9e3779b97f4a7c15ULL};
    }
    void Remember(HandlerFunc handler) {
        const auto h = HandlerHash(handler);
        bits_[(h >> 6) & 15] |= uint64_t{1} << (h & 63);
    }

public:
    OperandIndex(const Entry* shapes, size_t shape_count, const RegisterOperandEntry* registers,
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
                if constexpr (HasEncoded) {
                    if (registers[i].form != PinOperandForm::Register) {
                        encoded_[registers[i].original] = registers[i];
                        Remember(registers[i].original);
                        continue;
                    }
                }
                registers_[registers[i].original] = registers[i].select;
                Remember(registers[i].original);
            }
        }
    }
    // Key by the first operand shape as well as semantic identity. Misses must
    // not scan every observed shape of a common MOV/ALU handler during decode.
    FORCE_INLINE HandlerFunc Find(const DecodedInstruction* op, HandlerFunc original = nullptr) const {
        const auto handler = original ? original : op->handler;
        const auto h = HandlerHash(handler);
        if (!(bits_[(h >> 6) & 15] & (uint64_t{1} << (h & 63)))) return nullptr;
        const auto found = shapes_.find({handler, ReadShape(op)});
        if (found != shapes_.end()) {
            for (const auto* candidate : found->second) {
                bool match = true;
                for (unsigned i = 1; i < candidate->width; ++i)
                    if (ReadShape(op + i) != candidate->shapes[i]) {
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
        if constexpr (HasEncoded) {
            if (!op->prefixes.flags.lock) {
                const auto entry = encoded_.find(handler);
                if (entry != encoded_.end()) {
                    const auto& binding = entry->second;
                    const bool matches = binding.form == PinOperandForm::Opcode
                                             ? !op->meta.flags.has_modrm
                                             : op->meta.flags.has_modrm && (op->modrm & 0xc0) != 0xc0;
                    if (matches) return binding.select(op);
                }
            }
        }
        return nullptr;
    }
};
}  // namespace fiberish
