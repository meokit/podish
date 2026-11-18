#ifndef PODISH_DRM_GPU_H
#define PODISH_DRM_GPU_H

#include <stdint.h>
#include <sys/ioctl.h>

struct drm_prime_handle {
    uint32_t handle, flags;
    int32_t fd;
};

#define DRM_PRIME_HANDLE_TO_FD _IOWR('d', 0x2d, struct drm_prime_handle)
#define DRM_GEM_CLOSE _IOW('d', 0x09, uint32_t[2])
#define DRM_QUERY_GPU _IOR('d', 0xf0, uint32_t)
#define DRM_CREATE_GPU_BO _IOWR('d', 0xf3, uint32_t[5])
#define DRM_FORMAT_MOD_INVALID 0x00ffffffffffffffull

#endif
