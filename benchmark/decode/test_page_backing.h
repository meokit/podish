#pragma once

#include "bindings.h"

#include <array>
#include <cstddef>
#include <memory>
#include <vector>

class TestPageBacking {
public:
    std::byte* MapPage(EmuState* state, uint32_t address, uint8_t permissions) {
        auto page = std::make_unique<std::array<std::byte, 4096>>();
        auto* backing = page->data();
        pages_.push_back(std::move(page));
        if (!X86_MemMapHostPage(state, address, backing, permissions)) {
            pages_.pop_back();
            return nullptr;
        }
        return backing;
    }

private:
    std::vector<std::unique_ptr<std::array<std::byte, 4096>>> pages_;
};
