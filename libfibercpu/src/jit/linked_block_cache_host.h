#pragma once

#include <sys/sysctl.h>
#include <string>

namespace fiberish {

inline std::string LinkedBlockCacheHostCPU() {
    char name[128];
    size_t size = sizeof(name);
    if (sysctlbyname("machdep.cpu.brand_string", name, &size, nullptr, 0) != 0 || size < 2 || size > sizeof(name) ||
        name[size - 1] != '\0')
        return {};
    return {name, size - 1};
}

}  // namespace fiberish
