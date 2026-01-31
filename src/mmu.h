#pragma once

#include <cstdint>
#include "common.h"
#include <array>
#include <vector>
#include <cstring>
#include <stdexcept>
#include <iostream>

namespace x86emu {

// 4KB Page Size
constexpr uint32_t PAGE_SHIFT = 12;
constexpr uint32_t PAGE_SIZE = 1 << PAGE_SHIFT; // 4096
constexpr uint32_t PAGE_MASK = PAGE_SIZE - 1;

// 2-Level Page Table (1024 entries * 1024 entries)
// Level 1: Directory (10 bits)
// Level 2: Table (10 bits)
// Offset: 12 bits
constexpr uint32_t DIR_SHIFT = 22;
constexpr uint32_t TBL_SHIFT = 12;
constexpr uint32_t ENTRY_MASK = 0x3FF;

struct MemoryPerms {
    static constexpr uint8_t READ = 1;
    static constexpr uint8_t WRITE = 2;
    static constexpr uint8_t EXEC = 4;
};

class SoftMMU {
public:
    SoftMMU() {
        // Initialize directory to null
        page_directory.fill(nullptr);
    }

    ~SoftMMU() {
        // Cleanup allocated pages
        for (auto* table : page_directory) {
            if (table) {
                for (auto* page : *table) {
                    if (page) delete[] page;
                }
                delete table;
            }
        }
    }

    // Allocate/Map a region
    void mmap(uint32_t addr, uint32_t size, uint8_t perms) {
        // Align to page boundaries
        uint32_t start = addr & ~PAGE_MASK;
        uint32_t end = (addr + size + PAGE_MASK) & ~PAGE_MASK;

        for (uint32_t curr = start; curr < end; curr += PAGE_SIZE) {
            allocate_page(curr);
            // TODO: Store perms if needed
        }
    }

    // Fault Callback Signature
    // opaque: passed back to user (Context*)
    // addr: faulting address
    // is_write: 1 if write, 0 if read
    using FaultHandler = void(*)(void* opaque, uint32_t addr, int is_write);

    void set_fault_callback(FaultHandler handler, void* opaque) {
        fault_handler = handler;
        fault_opaque = opaque;
    }

    // Memory Hook Signature
    // opaque: User data (e.g Context*)
    // addr: Virtual Address
    // size: Access Size (1, 2, 4, 8)
    // is_write: 1=Write, 0=Read
    // val: Value written (or read)
    using MemHook = void(*)(void* opaque, uint32_t addr, uint32_t size, int is_write, uint64_t val);

    void set_mem_hook(MemHook hook, void* opaque) {
        mem_hook = hook;
        mem_hook_opaque = opaque;
    }

    // Generic Read
    template <typename T>
    T read(uint32_t addr) {
        // printf("[MMU] Read 0x%08X\n", addr); // Too verbose?
        uint8_t* ptr = translate(addr);
        if (!ptr) {
            handle_fault(addr, 0);
            // Retry translation after fault handler
            ptr = translate(addr);
            if (!ptr) {
                 return T{}; // Still failed
            }
        }
        T val;
        if ((addr & PAGE_MASK) + sizeof(T) > PAGE_SIZE) {
            val = read_cross_page<T>(addr);
        } else {
            val = *reinterpret_cast<T*>(ptr);
        }
        
        if (mem_hook) {
            uint64_t hook_val = 0;
            if constexpr (sizeof(T) <= 8) std::memcpy(&hook_val, &val, sizeof(T));
            else std::memcpy(&hook_val, &val, 8);
            mem_hook(mem_hook_opaque, addr, sizeof(T), 0, hook_val);
        }
        return val;
    }

    // Generic Write
    template<typename T>
    void write(uint32_t addr, T val) {
        // printf("[MMU] Write PADDR=0x%08X Size=%lu\n", addr, sizeof(T));
        
        if (mem_hook) {
            uint64_t hook_val = 0;
            if constexpr (sizeof(T) <= 8) std::memcpy(&hook_val, &val, sizeof(T));
            else std::memcpy(&hook_val, &val, 8);
            mem_hook(mem_hook_opaque, addr, sizeof(T), 1, hook_val);
        }
        
        uint8_t* ptr = translate(addr);
        if (ptr) {
            if ((addr & PAGE_MASK) + sizeof(T) <= PAGE_SIZE) {
                *reinterpret_cast<T*>(ptr) = val;
                return;
            } else {
                write_cross_page<T>(addr, val);
                return;
            }
        }
        
        // Fault
        handle_fault(addr, 1);
        
        // Retry
        ptr = translate(addr);
        if (ptr) {
            if ((addr & PAGE_MASK) + sizeof(T) <= PAGE_SIZE) {
                *reinterpret_cast<T*>(ptr) = val;
                return;
            } else {
                write_cross_page<T>(addr, val);
                return;
            }
        }
    }

    // Block Copy (Page-by-Page)
    // Returns number of bytes copied.
    uint32_t copy_block(uint32_t src_addr, uint32_t dst_addr, uint32_t size) {
        // If hooks are present, fall back to slow byte-wise copy to ensure hooks trigger.
        if (mem_hook) {
            for (uint32_t i = 0; i < size; ++i) {
                uint8_t val = this->read<uint8_t>(src_addr + i);
                if (emu_status && *emu_status != EmuStatus::Running) return i;
                this->write<uint8_t>(dst_addr + i, val);
                if (emu_status && *emu_status != EmuStatus::Running) return i;
            }
            return size;
        }

        uint32_t bytes_done = 0;
        uint32_t bytes_left = size;
        uint32_t curr_src = src_addr;
        uint32_t curr_dst = dst_addr;

        while (bytes_left > 0) {
            // Calculate chunk size valid for both pages
            uint32_t src_page_rem = PAGE_SIZE - (curr_src & PAGE_MASK);
            uint32_t dst_page_rem = PAGE_SIZE - (curr_dst & PAGE_MASK);
            uint32_t chunk = (bytes_left < src_page_rem) ? bytes_left : src_page_rem;
            if (dst_page_rem < chunk) chunk = dst_page_rem;

            // Translate
            uint8_t* p_src = translate(curr_src);
            if (!p_src) {
                handle_fault(curr_src, 0); // Read Fault
                return bytes_done;
            }
            
            uint8_t* p_dst = translate(curr_dst);
            if (!p_dst) {
                handle_fault(curr_dst, 1); // Write Fault
                return bytes_done;
            }

            // Perform Copy
            std::memcpy(p_dst, p_src, chunk);

            bytes_done += chunk;
            bytes_left -= chunk;
            curr_src += chunk;
            curr_dst += chunk;
        }
        return bytes_done;
    }

    // Warning: This does not trigger fault callback if null, 
    // assumes caller checks or is safe.
    // Decoder normally reads via `read<uint8_t>` or checks bounds.
    const uint8_t* get_ptr(uint32_t addr) {
        return translate(addr);
    }

    void set_status_ptr(EmuStatus* status, uint8_t* vector) {
        emu_status = status;
        emu_fault_vector = vector;
    }

private:
    using PageTable = std::array<uint8_t*, 1024>; 
    std::array<PageTable*, 1024> page_directory;

    FaultHandler fault_handler = nullptr;
    void* fault_opaque = nullptr;
    
    MemHook mem_hook = nullptr;
    void* mem_hook_opaque = nullptr;
    
    EmuStatus* emu_status = nullptr;
    uint8_t* emu_fault_vector = nullptr;

    void handle_fault(uint32_t addr, int is_write) {
        if (fault_handler) {
            fault_handler(fault_opaque, addr, is_write);
        } else {
            fprintf(stderr, "[MMU] Segfault at 0x%08X (Write=%d) (No Handler)\n", addr, is_write);
            if (emu_status) *emu_status = EmuStatus::Fault;
            if (emu_fault_vector) *emu_fault_vector = 14; // #PF
        }
    }

    uint8_t* translate(uint32_t addr) {
        uint32_t dir_idx = addr >> DIR_SHIFT;
        uint32_t tbl_idx = (addr >> TBL_SHIFT) & ENTRY_MASK;
        uint32_t offset = addr & PAGE_MASK;

        PageTable* table = page_directory[dir_idx];
        if (!table) return nullptr;

        uint8_t* page = (*table)[tbl_idx];
        if (!page) return nullptr;

        // printf("[MMU] Translate 0x%08X -> Host %p\n", addr, (void*)(page + offset));
        // fflush(stdout);
        return page + offset;
    }

    void allocate_page(uint32_t addr) {
        uint32_t dir_idx = addr >> DIR_SHIFT;
        uint32_t tbl_idx = (addr >> TBL_SHIFT) & ENTRY_MASK;

        if (!page_directory[dir_idx]) {
            page_directory[dir_idx] = new PageTable();
            page_directory[dir_idx]->fill(nullptr);
        }

        PageTable* table = page_directory[dir_idx];
        if (!(*table)[tbl_idx]) {
            (*table)[tbl_idx] = new uint8_t[PAGE_SIZE];
            std::memset((*table)[tbl_idx], 0, PAGE_SIZE);
        }
    }

    // Slow path for unaligned access processing boundaries
    template <typename T>
    T read_cross_page(uint32_t addr) {
        T value;
        uint8_t* dest = reinterpret_cast<uint8_t*>(&value);
        for (size_t i = 0; i < sizeof(T); ++i) {
            dest[i] = this->read<uint8_t>(addr + i);
        }
        return value;
    }

    template <typename T>
    void write_cross_page(uint32_t addr, T value) {
        uint8_t* src = reinterpret_cast<uint8_t*>(&value);
        for (size_t i = 0; i < sizeof(T); ++i) {
            this->write<uint8_t>(addr + i, src[i]);
        }
    }
};

} // namespace x86emu
