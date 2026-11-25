#include <atomic>

#include "vulkan-dispatch.h"

#import <Foundation/Foundation.h>
#import <Metal/Metal.h>
#import <TargetConditionals.h>
#if TARGET_OS_IOS
#import <UIKit/UIKit.h>
#endif

extern "C" int podish_venus_abi_version(void)
{
    return 2;
}

#if TARGET_OS_IOS
static std::atomic_bool gpuAllowed{true};

extern "C" void podish_venus_observe_gpu_availability(void)
{
    @autoreleasepool {
        static dispatch_once_t once;
        dispatch_once(&once, ^{
            NSNotificationCenter *notifications = NSNotificationCenter.defaultCenter;
            [notifications addObserverForName:UIApplicationDidEnterBackgroundNotification
                                       object:nil
                                        queue:nil
                                   usingBlock:^(NSNotification *) { gpuAllowed.store(false); }];
            [notifications addObserverForName:UIApplicationWillEnterForegroundNotification
                                       object:nil
                                        queue:nil
                                   usingBlock:^(NSNotification *) { gpuAllowed.store(true); }];
        });
    }
}
#endif

extern "C" bool podish_venus_gpu_allowed(void)
{
#if TARGET_OS_IOS
    return gpuAllowed.load();
#else
    return true;
#endif
}

extern "C" int podish_venus_probe(void)
{
    @autoreleasepool {
        if (@available(macOS 26.0, iOS 26.0, *)) {
            id<MTLDevice> device = MTLCreateSystemDefaultDevice();
            const bool supported = [device supportsFamily:MTLGPUFamilyMetal4];
#if !__has_feature(objc_arc)
            [device release];
#endif
            if (!supported)
                return 0;
        } else {
            return 0;
        }

        auto createInstance = reinterpret_cast<PFN_vkCreateInstance>(
            podish_vk_get_instance_proc_addr(VK_NULL_HANDLE, "vkCreateInstance"));
        if (!createInstance)
            return 0;

        const VkApplicationInfo application = {
            .sType = VK_STRUCTURE_TYPE_APPLICATION_INFO,
            .apiVersion = VK_API_VERSION_1_2,
        };
        const VkInstanceCreateInfo info = {
            .sType = VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO,
            .pApplicationInfo = &application,
        };
        VkInstance instance = VK_NULL_HANDLE;
        if (createInstance(&info, nullptr, &instance) != VK_SUCCESS)
            return 0;

        auto enumeratePhysicalDevices = reinterpret_cast<PFN_vkEnumeratePhysicalDevices>(
            podish_vk_get_instance_proc_addr(instance, "vkEnumeratePhysicalDevices"));
        auto destroyInstance = reinterpret_cast<PFN_vkDestroyInstance>(
            podish_vk_get_instance_proc_addr(instance, "vkDestroyInstance"));
        uint32_t deviceCount = 0;
        const bool ready = enumeratePhysicalDevices &&
            enumeratePhysicalDevices(instance, &deviceCount, nullptr) == VK_SUCCESS && deviceCount != 0;
        if (destroyInstance)
            destroyInstance(instance, nullptr);
        return ready;
    }
}
