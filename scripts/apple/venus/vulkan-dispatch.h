#ifndef PODISH_VULKAN_DISPATCH_H
#define PODISH_VULKAN_DISPATCH_H

#include <string.h>
#include <vulkan/vulkan.h>

#ifdef __cplusplus
extern "C" {
#endif

VKAPI_ATTR PFN_vkVoidFunction VKAPI_CALL vk_icdGetInstanceProcAddr(VkInstance instance, const char* name);

static inline VKAPI_ATTR VkResult VKAPI_CALL podish_vk_enumerate_instance_layers(uint32_t* count,
                                                                                 VkLayerProperties* properties) {
    (void)properties;
    *count = 0;
    return VK_SUCCESS;
}

static inline VKAPI_ATTR PFN_vkVoidFunction VKAPI_CALL podish_vk_get_instance_proc_addr(VkInstance instance,
                                                                                        const char* name) {
    if (strcmp(name, "vkGetInstanceProcAddr") == 0)
        return (PFN_vkVoidFunction)podish_vk_get_instance_proc_addr;
    if (strcmp(name, "vkEnumerateInstanceLayerProperties") == 0)
        return (PFN_vkVoidFunction)podish_vk_enumerate_instance_layers;

    return vk_icdGetInstanceProcAddr(instance, name);
}

#ifdef __cplusplus
}
#endif

#endif
