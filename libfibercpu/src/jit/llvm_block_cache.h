#pragma once

#include <cstdint>
#include <string_view>

#include <llvm/ADT/SmallVector.h>
#include <llvm/Support/Endian.h>
#include <llvm/Support/raw_ostream.h>

#include "jit/block_recipe.h"

namespace fiberish::llvm_block_cache {

inline constexpr std::string_view profile_magic = "FIBJPR08";
inline constexpr std::string_view weighted_profile_magic = "FIBJPR09";
inline constexpr std::string_view linked_magic = "FIBJLC05";
inline constexpr uint8_t region_tag = 0xff;

inline bool ReadU32(std::string_view& bytes, uint32_t& value) {
    if (bytes.size() < sizeof(value)) return false;
    value = llvm::support::endian::read32le(bytes.data());
    bytes.remove_prefix(sizeof(value));
    return true;
}

inline bool ReadRecord(std::string_view& bytes, std::string_view& record) {
    uint32_t size;
    if (!ReadU32(bytes, size) || size == 0 || size > bytes.size()) return false;
    record = bytes.substr(0, size);
    bytes.remove_prefix(size);
    return true;
}

inline bool WriteRecord(llvm::raw_ostream& out, std::string_view record) {
    if (record.empty() || record.size() > UINT32_MAX) return false;
    char size[4];
    llvm::support::endian::write32le(size, static_cast<uint32_t>(record.size()));
    out.write(size, sizeof(size));
    out.write(record.data(), record.size());
    return true;
}

inline void WriteU32(llvm::raw_ostream& out, uint32_t value) {
    char bytes[4];
    llvm::support::endian::write32le(bytes, value);
    out.write(bytes, sizeof(bytes));
}

// Key records encode the exit, exact bit and step count, followed by each
// bound operation and (in exact mode) operand and relative EIP fields.
inline auto EncodeKey(block_recipe::BlockKeyView key) {
    llvm::SmallVector<char, 3 + 28 * block_recipe::kMaxOps> bytes;
    bytes.push_back(static_cast<char>(key.exit));
    bytes.push_back(key.IsExact());
    bytes.push_back(static_cast<char>(key.operations.size()));
    const auto append = [&](auto value) {
        for (unsigned shift = 0; shift < sizeof(value) * 8; shift += 8)
            bytes.push_back(static_cast<char>(value >> shift));
    };
    for (size_t i = 0; i < key.operations.size(); ++i) {
        const auto& operation = key.operations[i];
        append(static_cast<uint16_t>(operation.id));
        append(operation.shape.modrm);
        append(operation.shape.slots);
        if (key.IsExact()) {
            const auto& shape = key.operands[i];
            append(shape.encoding);
            append(shape.ea_desc);
            append(shape.displacement);
            append(shape.immediate);
            append(key.eips[i].next_offset);
            append(key.eips[i].branch_width);
        }
    }
    return bytes;
}

inline bool DecodeKey(std::string_view bytes, block_recipe::Plan& plan, bool& exact) {
    if (bytes.size() < 3 || static_cast<uint8_t>(bytes[0]) > static_cast<uint8_t>(LLVMBlockExit::Branch) ||
        static_cast<uint8_t>(bytes[1]) > 1)
        return false;
    exact = bytes[1] != 0;
    const size_t count = static_cast<uint8_t>(bytes[2]);
    plan.exit = static_cast<LLVMBlockExit>(bytes[0]);
    bytes.remove_prefix(3);
    if (!count || count > block_recipe::kMaxOps || bytes.size() != count * (exact ? 28 : 4)) return false;

    for (size_t i = 0; i < count; ++i) {
        const uint16_t id = llvm::support::endian::read16le(bytes.data());
        if (id >= block_recipe::kOpIdCount) return false;
        const BoundOperandShape binding{uint8_t(bytes[2]), uint8_t(bytes[3])};
        if (!binding.slots || binding.slots > 2) return false;
        plan.operations[i] = {block_recipe::OpId{id}, binding};
        bytes.remove_prefix(4);
        if (exact) {
            auto& shape = plan.operands[i];
            auto& eip = plan.eips[i];
            if (!ReadU32(bytes, shape.encoding) || !ReadU32(bytes, shape.ea_desc) ||
                !ReadU32(bytes, shape.displacement) || !ReadU32(bytes, shape.immediate) ||
                !ReadU32(bytes, eip.next_offset) || !ReadU32(bytes, eip.branch_width) ||
                eip.branch_width > uint32_t(RelativeBranchWidth::Dword) || binding != shape.Binding())
                return false;
        }
    }
    plan.covered = count;
    return true;
}

}  // namespace fiberish::llvm_block_cache
