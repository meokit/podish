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
        printf("[MMU] Constructing. Addressing Page Directory %p\n", (void*)page_directory.data());
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
        printf("[MMU] mmap 0x%x size 0x%x\n", addr, size);
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
            return T{}; // Return 0 on fault
        }
        T val;
        if ((addr & PAGE_MASK) + sizeof(T) > PAGE_SIZE) {
            val = read_cross_page<T>(addr);
        } else {
            val = *reinterpret_cast<T*>(ptr);
        }
        
        if (mem_hook) {
            mem_hook(mem_hook_opaque, addr, sizeof(T), 0, (uint64_t)val);
        }
        return val;
    }

    // Generic Write
    template<typename T>
    void write(uint32_t addr, T val) {
        // printf("[MMU] Write PADDR=0x%08X Size=%lu\n", addr, sizeof(T));
        
        if (mem_hook) {
            mem_hook(mem_hook_opaque, addr, sizeof(T), 1, (uint64_t)val);
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
    }
    // Warning: This does not trigger fault callback if null, 
    // assumes caller checks or is safe.
    // Decoder normally reads via `read<uint8_t>` or checks bounds.
    const uint8_t* get_ptr(uint32_t addr) {
        return translate(addr);
    }

    void set_status_ptr(EmuStatus* status) {
        emu_status = status;
    }

private:
    using PageTable = std::array<uint8_t*, 1024>; 
    std::array<PageTable*, 1024> page_directory;

    FaultHandler fault_handler = nullptr;
    void* fault_opaque = nullptr;
    
    MemHook mem_hook = nullptr;
    void* mem_hook_opaque = nullptr;
    
    EmuStatus* emu_status = nullptr;

    void handle_fault(uint32_t addr, int is_write) {
        if (fault_handler) {
            fault_handler(fault_opaque, addr, is_write);
        } else {
            fprintf(stderr, "[MMU] Segfault at 0x%08X (Write=%d) (No Handler)\n", addr, is_write);
            if (emu_status) *emu_status = EmuStatus::Fault;
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
