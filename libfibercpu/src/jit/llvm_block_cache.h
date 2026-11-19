#pragma once

#include <cstdint>
#include <string>
#include <string_view>

#include <llvm/Support/Endian.h>
#include <llvm/Support/raw_ostream.h>

namespace fiberish::llvm_block_cache {

inline constexpr std::string_view profile_magic = "FIBJPR01";
inline constexpr std::string_view cache_magic = "FIBJOC01";

inline void AppendU32(std::string& key, uint32_t value) {
    char bytes[4];
    llvm::support::endian::write32le(bytes, value);
    key.append(bytes, sizeof(bytes));
}

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

}  // namespace fiberish::llvm_block_cache
