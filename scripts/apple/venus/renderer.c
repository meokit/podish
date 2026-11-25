#include "vkr_renderer.h"
#include <TargetConditionals.h>
#include <stdbool.h>
#include <sys/stat.h>

#if TARGET_OS_IOS
void podish_venus_observe_gpu_availability(void);
#endif

int64_t podish_venus_shared_fd_size(int fd) {
    // Darwin shm_open descriptors support fstat and mmap but cannot seek.
    struct stat info;
    return fstat(fd, &info) == 0 ? info.st_size : -1;
}

size_t podish_vkr_get_capset(void* capset, uint32_t flags) {
    return vkr_get_capset(capset, flags);
}

bool podish_vkr_renderer_init(uint32_t flags, const struct vkr_renderer_callbacks* callbacks) {
    if (!vkr_renderer_init(flags, callbacks))
        return false;
#if TARGET_OS_IOS
    podish_venus_observe_gpu_availability();
#endif
    return true;
}

void podish_vkr_renderer_fini(void) {
    vkr_renderer_fini();
}

bool podish_vkr_renderer_create_context(uint32_t context, uint32_t flags, uint32_t name_length, const char* name) {
    return vkr_renderer_create_context(context, flags, name_length, name);
}

void podish_vkr_renderer_destroy_context(uint32_t context) {
    vkr_renderer_destroy_context(context);
}

bool podish_vkr_renderer_submit_cmd(uint32_t context, void* command, uint32_t size) {
    return vkr_renderer_submit_cmd(context, command, size);
}

bool podish_vkr_renderer_submit_fence(uint32_t context, uint32_t flags, uint64_t ring, uint64_t fence) {
    return vkr_renderer_submit_fence(context, flags, ring, fence);
}

bool podish_vkr_renderer_create_resource(uint32_t context, uint32_t resource, uint64_t blob, uint64_t size,
                                         uint32_t flags, enum virgl_resource_fd_type* fd_type, int* fd,
                                         uint32_t* map_info, struct virgl_resource_vulkan_info* vulkan_info) {
    return vkr_renderer_create_resource(context, resource, blob, size, flags, fd_type, fd, map_info, vulkan_info);
}

bool podish_vkr_renderer_import_resource(uint32_t context, uint32_t resource, enum virgl_resource_fd_type fd_type,
                                         int fd, uint64_t size) {
    return vkr_renderer_import_resource(context, resource, fd_type, fd, size);
}

void podish_vkr_renderer_destroy_resource(uint32_t context, uint32_t resource) {
    vkr_renderer_destroy_resource(context, resource);
}
