#include <fcntl.h>
#include <libdrm/virtgpu_drm.h>
#include <linux/sync_file.h>
#include <poll.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/ioctl.h>
#include <unistd.h>
#include <vulkan/vulkan.h>
#include <xf86drm.h>

#define RENDER_NODE "/dev/dri/renderD129"
#define VENUS_CAPSET 4u

/* Trixie's libdrm headers predate the virtgpu parameters added for Venus. */
enum {
    VIRTGPU_QUERY_RESOURCE_BLOB = 3,
    VIRTGPU_QUERY_HOST_VISIBLE = 4,
    VIRTGPU_QUERY_CONTEXT_INIT = 6,
    VIRTGPU_QUERY_SUPPORTED_CAPSETS = 7,
};

static void require(bool ok, const char* message) {
    if (!ok) {
        fprintf(stderr, "%s\n", message);
        exit(1);
    }
}

#define VK_CHECK(call)                                                                                                 \
    do {                                                                                                               \
        VkResult result = (call);                                                                                      \
        if (result != VK_SUCCESS) {                                                                                    \
            fprintf(stderr, "%s returned %d\n", #call, result);                                                        \
            exit(1);                                                                                                   \
        }                                                                                                              \
    } while (0)

static void check_render_node(void) {
    drmDevicePtr nodes[16];
    int count = drmGetDevices2(0, nodes, sizeof(nodes) / sizeof(nodes[0]));
    require(count > 0, "libdrm found no devices through sysfs");
    bool available = false;
    for (int i = 0; i < count; i++) {
        if ((nodes[i]->available_nodes & (1 << DRM_NODE_RENDER)) &&
            strcmp(nodes[i]->nodes[DRM_NODE_RENDER], RENDER_NODE) == 0)
            available = true;
    }
    drmFreeDevices(nodes, count);
    require(available, "libdrm did not discover " RENDER_NODE);

    int fd = open(RENDER_NODE, O_RDWR | O_CLOEXEC);
    require(fd >= 0, "could not open " RENDER_NODE);
    drmVersionPtr version = drmGetVersion(fd);
    require(version && version->version_major == 0 && version->name && strcmp(version->name, "virtio_gpu") == 0,
            "DRM_IOCTL_VERSION did not identify virtio_gpu");
    drmFreeVersion(version);

    const uint64_t params[] = {
        VIRTGPU_PARAM_3D_FEATURES,  VIRTGPU_PARAM_CAPSET_QUERY_FIX, VIRTGPU_QUERY_RESOURCE_BLOB,
        VIRTGPU_QUERY_HOST_VISIBLE, VIRTGPU_QUERY_CONTEXT_INIT,     VIRTGPU_QUERY_SUPPORTED_CAPSETS,
    };
    for (size_t i = 0; i < sizeof(params) / sizeof(params[0]); i++) {
        uint32_t value = 0, expected = 1;
        struct drm_virtgpu_getparam req = {.param = params[i], .value = (uintptr_t)&value};
        if (req.param == VIRTGPU_QUERY_SUPPORTED_CAPSETS)
            expected = 1u << VENUS_CAPSET;
        if (ioctl(fd, DRM_IOCTL_VIRTGPU_GETPARAM, &req) != 0 || !(value & expected)) {
            fprintf(stderr, "VIRTGPU_GETPARAM %llu failed or returned %#x\n", (unsigned long long)req.param, value);
            exit(1);
        }
    }
    uint32_t capset[32] = {0};
    struct drm_virtgpu_get_caps caps = {.cap_set_id = VENUS_CAPSET, .addr = (uintptr_t)capset, .size = sizeof(capset)};
    require(ioctl(fd, DRM_IOCTL_VIRTGPU_GET_CAPS, &caps) == 0 && capset[0] != 0, "could not query the Venus capset");
    close(fd);
    puts("virtio_gpu render node and Venus capset discovered");
}

static VkPhysicalDevice get_venus_device(VkInstance instance) {
    uint32_t count = 0;
    VK_CHECK(vkEnumeratePhysicalDevices(instance, &count, NULL));
    require(count > 0, "Vulkan loader found no physical devices");
    VkPhysicalDevice* devices = calloc(count, sizeof(*devices));
    require(devices != NULL, "could not allocate a device list");
    VK_CHECK(vkEnumeratePhysicalDevices(instance, &count, devices));
    VkPhysicalDevice chosen = VK_NULL_HANDLE;
    for (uint32_t i = 0; i < count; i++) {
        VkPhysicalDeviceDriverProperties driver = {
            .sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_DRIVER_PROPERTIES,
        };
        VkPhysicalDeviceProperties2 props = {
            .sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_PROPERTIES_2,
            .pNext = &driver,
        };
        vkGetPhysicalDeviceProperties2(devices[i], &props);
        printf("Vulkan device %s (%s)\n", props.properties.deviceName, driver.driverName);
        if (driver.driverID == VK_DRIVER_ID_MESA_VENUS &&
            (props.properties.deviceType == VK_PHYSICAL_DEVICE_TYPE_INTEGRATED_GPU ||
             props.properties.deviceType == VK_PHYSICAL_DEVICE_TYPE_DISCRETE_GPU)) {
            chosen = devices[i];
            break;
        }
    }
    free(devices);
    require(chosen != VK_NULL_HANDLE, "could not find a Venus hardware GPU");
    return chosen;
}

static uint32_t graphics_queue_family(VkPhysicalDevice physical) {
    uint32_t count = 0;
    vkGetPhysicalDeviceQueueFamilyProperties(physical, &count, NULL);
    VkQueueFamilyProperties* families = calloc(count, sizeof(*families));
    require(count > 0 && families != NULL, "could not get Vulkan queue families");
    vkGetPhysicalDeviceQueueFamilyProperties(physical, &count, families);
    uint32_t family = 0;
    while (family < count && !(families[family].queueFlags & VK_QUEUE_GRAPHICS_BIT))
        family++;
    free(families);
    require(family < count, "could not find a graphics queue");
    return family;
}

static uint32_t host_memory_type(VkPhysicalDevice physical, uint32_t types) {
    VkPhysicalDeviceMemoryProperties mem;
    vkGetPhysicalDeviceMemoryProperties(physical, &mem);
    const VkMemoryPropertyFlags required = VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT | VK_MEMORY_PROPERTY_HOST_COHERENT_BIT;
    for (uint32_t i = 0; i < mem.memoryTypeCount; i++) {
        if ((types & (1u << i)) && (mem.memoryTypes[i].propertyFlags & required) == required)
            return i;
    }
    require(false, "could not find a host-visible coherent Vulkan memory type");
    return 0;
}

static void check_sync_file(VkDevice device, VkSemaphore semaphore) {
    PFN_vkGetSemaphoreFdKHR get_fd = (PFN_vkGetSemaphoreFdKHR)vkGetDeviceProcAddr(device, "vkGetSemaphoreFdKHR");
    require(get_fd != NULL, "vkGetSemaphoreFdKHR is unavailable");
    VkSemaphoreGetFdInfoKHR info = {
        .sType = VK_STRUCTURE_TYPE_SEMAPHORE_GET_FD_INFO_KHR,
        .semaphore = semaphore,
        .handleType = VK_EXTERNAL_SEMAPHORE_HANDLE_TYPE_SYNC_FD_BIT,
    };
    int fd = -1;
    VK_CHECK(get_fd(device, &info, &fd));
    require(fd >= 0, "Venus did not export a sync_file descriptor");
    struct sync_file_info fence_info = {0};
    require(ioctl(fd, SYNC_IOC_FILE_INFO, &fence_info) == 0 && fence_info.num_fences == 1,
            "SYNC_IOC_FILE_INFO did not identify the exported fence");

    struct pollfd poll_fd = {.fd = fd, .events = POLLIN};
    require(poll(&poll_fd, 1, 10000) == 1 && (poll_fd.revents & POLLIN), "exported sync_file did not become readable");
    fence_info = (struct sync_file_info){0};
    require(ioctl(fd, SYNC_IOC_FILE_INFO, &fence_info) == 0 && fence_info.status == 1,
            "the exported sync_file did not report a signaled fence");
    close(fd);
    puts("Venus exported SYNC_FD and signaled its pollable sync_file");
}

static void check_queue(VkPhysicalDevice physical) {
    uint32_t family = graphics_queue_family(physical);
    float priority = 1.0f;
    VkDeviceQueueCreateInfo queue_info = {
        .sType = VK_STRUCTURE_TYPE_DEVICE_QUEUE_CREATE_INFO,
        .queueFamilyIndex = family,
        .queueCount = 1,
        .pQueuePriorities = &priority,
    };
    const char* extensions[] = {VK_KHR_EXTERNAL_SEMAPHORE_FD_EXTENSION_NAME};
    VkDeviceCreateInfo dev_info = {
        .sType = VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO,
        .queueCreateInfoCount = 1,
        .pQueueCreateInfos = &queue_info,
        .enabledExtensionCount = 1,
        .ppEnabledExtensionNames = extensions,
    };
    VkDevice device;
    VK_CHECK(vkCreateDevice(physical, &dev_info, NULL, &device));
    VkQueue queue;
    vkGetDeviceQueue(device, family, 0, &queue);

    enum { BUFFER_BYTES = 8192 };
    const uint32_t patterns[2] = {0x11335577u, 0x22446688u};
    VkBufferCreateInfo buffer_info = {
        .sType = VK_STRUCTURE_TYPE_BUFFER_CREATE_INFO,
        .size = BUFFER_BYTES,
        .usage = VK_BUFFER_USAGE_TRANSFER_DST_BIT,
        .sharingMode = VK_SHARING_MODE_EXCLUSIVE,
    };
    VkBuffer buffer;
    VK_CHECK(vkCreateBuffer(device, &buffer_info, NULL, &buffer));
    VkMemoryRequirements req;
    vkGetBufferMemoryRequirements(device, buffer, &req);
    VkMemoryAllocateInfo alloc_info = {
        .sType = VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO,
        .allocationSize = req.size,
        .memoryTypeIndex = host_memory_type(physical, req.memoryTypeBits),
    };
    VkDeviceMemory memory;
    VK_CHECK(vkAllocateMemory(device, &alloc_info, NULL, &memory));
    VK_CHECK(vkBindBufferMemory(device, buffer, memory, 0));
    void* mapping = NULL;
    VK_CHECK(vkMapMemory(device, memory, 0, VK_WHOLE_SIZE, 0, &mapping));
    memset(mapping, 0, BUFFER_BYTES);

    VkCommandPoolCreateInfo pool_info = {
        .sType = VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO,
        .queueFamilyIndex = family,
    };
    VkCommandPool pool;
    VK_CHECK(vkCreateCommandPool(device, &pool_info, NULL, &pool));
    VkCommandBufferAllocateInfo cmd_info = {
        .sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO,
        .commandPool = pool,
        .level = VK_COMMAND_BUFFER_LEVEL_PRIMARY,
        .commandBufferCount = 1,
    };
    VkCommandBuffer cmd;
    VK_CHECK(vkAllocateCommandBuffers(device, &cmd_info, &cmd));
    VkCommandBufferBeginInfo begin = {
        .sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO,
        .flags = VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT,
    };
    VK_CHECK(vkBeginCommandBuffer(cmd, &begin));
    vkCmdFillBuffer(cmd, buffer, 0, BUFFER_BYTES / 2, patterns[0]);
    vkCmdFillBuffer(cmd, buffer, BUFFER_BYTES / 2, BUFFER_BYTES / 2, patterns[1]);
    VkMemoryBarrier barrier = {
        .sType = VK_STRUCTURE_TYPE_MEMORY_BARRIER,
        .srcAccessMask = VK_ACCESS_TRANSFER_WRITE_BIT,
        .dstAccessMask = VK_ACCESS_HOST_READ_BIT,
    };
    vkCmdPipelineBarrier(cmd, VK_PIPELINE_STAGE_TRANSFER_BIT, VK_PIPELINE_STAGE_HOST_BIT, 0, 1, &barrier, 0, NULL, 0,
                         NULL);
    VK_CHECK(vkEndCommandBuffer(cmd));
    VkFenceCreateInfo fence_info = {.sType = VK_STRUCTURE_TYPE_FENCE_CREATE_INFO};
    VkFence fence;
    VK_CHECK(vkCreateFence(device, &fence_info, NULL, &fence));
    VkExportSemaphoreCreateInfo export_info = {
        .sType = VK_STRUCTURE_TYPE_EXPORT_SEMAPHORE_CREATE_INFO,
        .handleTypes = VK_EXTERNAL_SEMAPHORE_HANDLE_TYPE_SYNC_FD_BIT,
    };
    VkSemaphoreCreateInfo semaphore_info = {
        .sType = VK_STRUCTURE_TYPE_SEMAPHORE_CREATE_INFO,
        .pNext = &export_info,
    };
    VkSemaphore semaphore;
    VK_CHECK(vkCreateSemaphore(device, &semaphore_info, NULL, &semaphore));
    VkSubmitInfo submit = {
        .sType = VK_STRUCTURE_TYPE_SUBMIT_INFO,
        .commandBufferCount = 1,
        .pCommandBuffers = &cmd,
        .signalSemaphoreCount = 1,
        .pSignalSemaphores = &semaphore,
    };
    VK_CHECK(vkQueueSubmit(queue, 1, &submit, fence));
    check_sync_file(device, semaphore);
    VK_CHECK(vkWaitForFences(device, 1, &fence, VK_TRUE, 10000000000ull));

    const uint32_t* words = mapping;
    for (size_t i = 0; i < BUFFER_BYTES / sizeof(*words); i++)
        require(words[i] == patterns[i >= BUFFER_BYTES / (2 * sizeof(*words))], "GPU readback pattern mismatch");
    puts("Venus queue submission, fence and mapped GPU readback passed");

    VK_CHECK(vkQueueWaitIdle(queue));
    vkDestroySemaphore(device, semaphore, NULL);
    vkDestroyFence(device, fence, NULL);
    vkDestroyCommandPool(device, pool, NULL);
    vkUnmapMemory(device, memory);
    vkDestroyBuffer(device, buffer, NULL);
    vkFreeMemory(device, memory, NULL);
    vkDestroyDevice(device, NULL);
}

int main(void) {
    check_render_node();
    VkApplicationInfo app = {.sType = VK_STRUCTURE_TYPE_APPLICATION_INFO, .apiVersion = VK_API_VERSION_1_2};
    VkInstanceCreateInfo info = {.sType = VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO, .pApplicationInfo = &app};
    VkInstance instance;
    VK_CHECK(vkCreateInstance(&info, NULL, &instance));
    check_queue(get_venus_device(instance));
    vkDestroyInstance(instance, NULL);
    return 0;
}
