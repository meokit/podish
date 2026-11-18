#define _GNU_SOURCE
#include "drm_gpu.h"
#include "gbm_egl.h"
#include <errno.h>
#include <fcntl.h>
#include <pthread.h>
#include <stdatomic.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include <sys/ioctl.h>
#include <sys/mman.h>

#define GBM_EXPORT __attribute__((visibility("default")))

/* Linux IA-32 DRM UAPI subset for linear dumb buffers. */
struct drm_version {
    int major, minor, patch;
    size_t name_len;
    char* name;
    size_t date_len;
    char* date;
    size_t desc_len;
    char* desc;
};

struct drm_create_dumb {
    uint32_t height, width, bpp, flags, handle, pitch;
    uint64_t size;
};

struct drm_map_dumb {
    uint32_t handle, pad;
    uint64_t offset;
};

struct drm_import_bo {
    int32_t fd;
    uint32_t width, height, pitch, handle;
    uint64_t size;
};

#define DRM_VERSION _IOWR('d', 0x00, struct drm_version)
#define DRM_CREATE_DUMB _IOWR('d', 0xb2, struct drm_create_dumb)
#define DRM_MAP_DUMB _IOWR('d', 0xb3, struct drm_map_dumb)
#define DRM_LOCK_GPU_BO _IOW('d', 0xf1, uint32_t[2])
#define DRM_IMPORT_GBM_BO _IOWR('d', 0xf2, struct drm_import_bo)

_Static_assert(sizeof(struct drm_version) == 36, "IA-32 drm_version");
_Static_assert(sizeof(struct drm_create_dumb) == 32, "IA-32 drm_mode_create_dumb");
_Static_assert(sizeof(struct drm_map_dumb) == 16, "IA-32 drm_mode_map_dumb");
_Static_assert(sizeof(struct drm_import_bo) == 28, "IA-32 Podish GBM import");

#define GBM_FORMAT_XRGB8888 0x34325258u
#define GBM_FORMAT_ARGB8888 0x34325241u
#define GBM_BO_USE_SCANOUT (1u << 0)
#define GBM_BO_USE_CURSOR (1u << 1)
#define GBM_BO_USE_RENDERING (1u << 2)
#define GBM_BO_USE_WRITE (1u << 3)
#define GBM_BO_USE_LINEAR (1u << 4)
#define GBM_BO_TRANSFER_READ 1u
#define GBM_BO_TRANSFER_WRITE 2u
#define GBM_BO_IMPORT_FD 0x5503u
#define GBM_BO_IMPORT_FD_MODIFIER 0x5504u
#define DRM_FORMAT_MOD_LINEAR 0ull

union gbm_bo_handle {
    void* ptr;
    int32_t s32;
    uint32_t u32;
    int64_t s64;
    uint64_t u64;
};

struct gbm_import_fd_data {
    int fd;
    uint32_t width, height, stride, format;
};

struct gbm_import_fd_modifier_data {
    uint32_t width, height, format, num_fds;
    int fds[4], strides[4], offsets[4];
    uint64_t modifier;
};

struct gbm_device {
    int fd;
    uint32_t gpu;
    atomic_uint refs;
    struct gbm_device* next;
};

struct gbm_bo {
    struct gbm_device* device;
    uint32_t width, height, format, stride, handle;
    size_t bytes;
    void* user;
    void (*destroy_user)(struct gbm_bo*, void*);
    int locked;
    int managed;
};

struct gbm_surface {
    struct gbm_device* device;
    uint32_t width, height, format;
    unsigned refs;
    int attached;
    uint32_t ready;
    struct gbm_bo slots[PG_GBM_SLOTS];
    struct gbm_surface* next;
};

static pthread_mutex_t registry_lock = PTHREAD_MUTEX_INITIALIZER;
static struct gbm_device* devices;
static struct gbm_surface* surfaces;

GBM_EXPORT struct gbm_device* gbm_create_device(int fd) {
    char name[6] = {0};
    struct drm_version version = {.name_len = sizeof(name), .name = name};
    if (ioctl(fd, DRM_VERSION, &version) < 0)
        return NULL;
    if (version.name_len != sizeof(name) || memcmp(name, "podish", sizeof(name)) != 0) {
        errno = ENODEV;
        return NULL;
    }
    struct gbm_device* device = malloc(sizeof(*device));
    if (!device)
        return NULL;
    atomic_init(&device->refs, 1);
    device->fd = fd;
    device->gpu = 0;
    ioctl(fd, DRM_QUERY_GPU, &device->gpu);
    pthread_mutex_lock(&registry_lock);
    device->next = devices;
    devices = device;
    pthread_mutex_unlock(&registry_lock);
    return device;
}

GBM_EXPORT void gbm_device_destroy(struct gbm_device* device) {
    if (atomic_fetch_sub(&device->refs, 1) != 1)
        return;
    pthread_mutex_lock(&registry_lock);
    struct gbm_device** link = &devices;
    while (*link != device)
        link = &(*link)->next;
    *link = device->next;
    pthread_mutex_unlock(&registry_lock);
    free(device);
}

GBM_EXPORT int gbm_device_get_fd(struct gbm_device* device) {
    return device->fd;
}
GBM_EXPORT const char* gbm_device_get_backend_name(struct gbm_device* device) {
    (void)device;
    return "podish";
}

GBM_EXPORT int gbm_device_is_format_supported(struct gbm_device* device, uint32_t format, uint32_t usage) {
    unsigned allowed = GBM_BO_USE_SCANOUT | GBM_BO_USE_LINEAR | GBM_BO_USE_WRITE | GBM_BO_USE_CURSOR;
    if (device->gpu)
        allowed |= GBM_BO_USE_RENDERING;
    return (format == GBM_FORMAT_XRGB8888 || format == GBM_FORMAT_ARGB8888) && !(usage & ~allowed) &&
           (!(usage & GBM_BO_USE_RENDERING) || !(usage & (GBM_BO_USE_CURSOR | GBM_BO_USE_WRITE)));
}

GBM_EXPORT int gbm_device_get_format_modifier_plane_count(struct gbm_device* device, uint32_t format,
                                                          uint64_t modifier) {
    return modifier == 0 && gbm_device_is_format_supported(device, format, 0) ? 1 : -1;
}

GBM_EXPORT struct gbm_bo* gbm_bo_create(struct gbm_device* device, uint32_t width, uint32_t height, uint32_t format,
                                        uint32_t usage) {
    const int managed = (usage & GBM_BO_USE_RENDERING) != 0;
    if (!gbm_device_is_format_supported(device, format, usage) ||
        (managed && (!(device->gpu & 2) || (usage & GBM_BO_USE_LINEAR)))) {
        errno = EINVAL;
        return NULL;
    }
    struct gbm_bo* bo = calloc(1, sizeof(*bo));
    if (!bo)
        return NULL;
    struct drm_create_dumb request = {.height = height, .width = width, .bpp = 32};
    uint32_t texture[5] = {width, height, format};
    int created =
        managed ? ioctl(device->fd, DRM_CREATE_GPU_BO, texture) : ioctl(device->fd, DRM_CREATE_DUMB, &request);
    if (created < 0) {
        int error = errno;
        free(bo);
        errno = error;
        return NULL;
    }
    *bo = (struct gbm_bo){.device = device,
                          .width = width,
                          .height = height,
                          .format = format,
                          .stride = managed ? texture[4] : request.pitch,
                          .handle = managed ? texture[3] : request.handle,
                          .bytes = managed ? (size_t)texture[4] * height : (size_t)request.size,
                          .managed = managed};
    atomic_fetch_add(&device->refs, 1);
    return bo;
}

GBM_EXPORT struct gbm_bo* gbm_bo_create_with_modifiers2(struct gbm_device* device, uint32_t width, uint32_t height,
                                                        uint32_t format, const uint64_t* modifiers, unsigned count,
                                                        uint32_t usage) {
    if (modifiers) {
        if (device->gpu & 2)
            for (unsigned i = 0; i < count; i++)
                if (modifiers[i] == DRM_FORMAT_MOD_INVALID)
                    return gbm_bo_create(device, width, height, format, usage | GBM_BO_USE_RENDERING);
        for (unsigned i = 0; i < count; i++) {
            if (modifiers[i] == DRM_FORMAT_MOD_LINEAR)
                return gbm_bo_create(device, width, height, format, usage | GBM_BO_USE_LINEAR);
        }
    }
    errno = EINVAL;
    return NULL;
}

GBM_EXPORT struct gbm_bo* gbm_bo_create_with_modifiers(struct gbm_device* device, uint32_t width, uint32_t height,
                                                       uint32_t format, const uint64_t* modifiers, unsigned count) {
    return gbm_bo_create_with_modifiers2(device, width, height, format, modifiers, count, 0);
}

GBM_EXPORT struct gbm_bo* gbm_bo_import(struct gbm_device* device, uint32_t type, void* buffer, uint32_t usage) {
    if (!buffer || (usage & GBM_BO_USE_RENDERING)) {
        errno = EINVAL;
        return NULL;
    }

    struct gbm_import_fd_data info;
    if (type == GBM_BO_IMPORT_FD) {
        info = *(const struct gbm_import_fd_data*)buffer;
    } else if (type == GBM_BO_IMPORT_FD_MODIFIER) {
        const struct gbm_import_fd_modifier_data* planes = buffer;
        if (planes->num_fds != 1 || planes->offsets[0] != 0 ||
            (planes->modifier != DRM_FORMAT_MOD_LINEAR && planes->modifier != DRM_FORMAT_MOD_INVALID)) {
            errno = EINVAL;
            return NULL;
        }
        info = (struct gbm_import_fd_data){planes->fds[0], planes->width, planes->height, planes->strides[0],
                                           planes->format};
    } else {
        errno = EINVAL;
        return NULL;
    }
    if (!gbm_device_is_format_supported(device, info.format, usage)) {
        errno = EINVAL;
        return NULL;
    }

    struct gbm_bo* bo = calloc(1, sizeof(*bo));
    if (!bo)
        return NULL;
    struct drm_import_bo request = {.fd = info.fd, .width = info.width, .height = info.height, .pitch = info.stride};
    if (ioctl(device->fd, DRM_IMPORT_GBM_BO, &request) < 0) {
        int error = errno;
        free(bo);
        errno = error;
        return NULL;
    }
    *bo = (struct gbm_bo){.device = device,
                          .width = info.width,
                          .height = info.height,
                          .format = info.format,
                          .stride = info.stride,
                          .handle = request.handle,
                          .bytes = (size_t)request.size};
    atomic_fetch_add(&device->refs, 1);
    return bo;
}

GBM_EXPORT uint32_t gbm_bo_get_width(struct gbm_bo* bo) {
    return bo->width;
}
GBM_EXPORT uint32_t gbm_bo_get_height(struct gbm_bo* bo) {
    return bo->height;
}
GBM_EXPORT uint32_t gbm_bo_get_stride(struct gbm_bo* bo) {
    return bo->stride;
}
GBM_EXPORT uint32_t gbm_bo_get_format(struct gbm_bo* bo) {
    return bo->format;
}
GBM_EXPORT uint32_t gbm_bo_get_bpp(struct gbm_bo* bo) {
    (void)bo;
    return 32;
}
GBM_EXPORT struct gbm_device* gbm_bo_get_device(struct gbm_bo* bo) {
    return bo->device;
}
GBM_EXPORT union gbm_bo_handle gbm_bo_get_handle(struct gbm_bo* bo) {
    return (union gbm_bo_handle){.u64 = bo->handle};
}
GBM_EXPORT union gbm_bo_handle gbm_bo_get_handle_for_plane(struct gbm_bo* bo, int plane) {
    if (plane == 0)
        return gbm_bo_get_handle(bo);
    errno = EINVAL;
    return (union gbm_bo_handle){.s32 = -1};
}

GBM_EXPORT int gbm_bo_get_fd_for_plane(struct gbm_bo* bo, int plane) {
    if (plane != 0) {
        errno = EINVAL;
        return -1;
    }
    struct drm_prime_handle request = {.handle = bo->handle, .flags = O_CLOEXEC | O_RDWR, .fd = -1};
    return ioctl(bo->device->fd, DRM_PRIME_HANDLE_TO_FD, &request) < 0 ? -1 : request.fd;
}

GBM_EXPORT int gbm_bo_get_fd(struct gbm_bo* bo) {
    return gbm_bo_get_fd_for_plane(bo, 0);
}

GBM_EXPORT uint64_t gbm_bo_get_modifier(struct gbm_bo* bo) {
    return bo->managed ? DRM_FORMAT_MOD_INVALID : DRM_FORMAT_MOD_LINEAR;
}
GBM_EXPORT int gbm_bo_get_plane_count(struct gbm_bo* bo) {
    (void)bo;
    return 1;
}
GBM_EXPORT uint32_t gbm_bo_get_stride_for_plane(struct gbm_bo* bo, int plane) {
    if (plane == 0)
        return bo->stride;
    errno = EINVAL;
    return 0;
}
GBM_EXPORT uint32_t gbm_bo_get_offset(struct gbm_bo* bo, int plane) {
    (void)bo;
    if (plane != 0)
        errno = EINVAL;
    return 0;
}

GBM_EXPORT void gbm_bo_set_user_data(struct gbm_bo* bo, void* data, void (*destroy)(struct gbm_bo*, void*)) {
    bo->user = data;
    bo->destroy_user = destroy;
}

GBM_EXPORT void* gbm_bo_get_user_data(struct gbm_bo* bo) {
    return bo->user;
}

GBM_EXPORT void gbm_bo_destroy(struct gbm_bo* bo) {
    if (bo->bytes == 0)
        return; // The GBM surface owns this BO.
    if (bo->destroy_user)
        bo->destroy_user(bo, bo->user);
    uint32_t close_request[2] = {bo->handle, 0};
    ioctl(bo->device->fd, DRM_GEM_CLOSE, close_request);
    gbm_device_destroy(bo->device);
    free(bo);
}

GBM_EXPORT void* gbm_bo_map(struct gbm_bo* bo, uint32_t x, uint32_t y, uint32_t width, uint32_t height, uint32_t flags,
                            uint32_t* stride, void** map_data) {
    if (!stride || !map_data || flags == 0 || (flags & ~(GBM_BO_TRANSFER_READ | GBM_BO_TRANSFER_WRITE)) || width == 0 ||
        height == 0 || (uint64_t)x + width > bo->width || (uint64_t)y + height > bo->height) {
        errno = EINVAL;
        return NULL;
    }
    if (bo->bytes == 0 || bo->managed) {
        errno = ENOTSUP;
        return NULL;
    }
    struct drm_map_dumb request = {.handle = bo->handle};
    if (ioctl(bo->device->fd, DRM_MAP_DUMB, &request) < 0)
        return NULL;
    int protection = (flags & GBM_BO_TRANSFER_READ ? PROT_READ : 0) | (flags & GBM_BO_TRANSFER_WRITE ? PROT_WRITE : 0);
    void* pixels = mmap64(NULL, bo->bytes, protection, MAP_SHARED, bo->device->fd, (off64_t)request.offset);
    if (pixels == MAP_FAILED)
        return NULL;
    *stride = bo->stride;
    *map_data = pixels;
    return (char*)pixels + y * bo->stride + x * 4;
}

GBM_EXPORT void gbm_bo_unmap(struct gbm_bo* bo, void* map_data) {
    munmap(map_data, bo->bytes);
}

GBM_EXPORT int gbm_bo_write(struct gbm_bo* bo, const void* data, size_t count) {
    if (count > (size_t)bo->stride * bo->height || (count != 0 && !data)) {
        errno = EINVAL;
        return -1;
    }
    if (count == 0)
        return 0;

    void* map;
    uint32_t stride;
    void* pixels = gbm_bo_map(bo, 0, 0, bo->width, bo->height, GBM_BO_TRANSFER_WRITE, &stride, &map);
    if (!pixels)
        return -1;
    memcpy(pixels, data, count);
    gbm_bo_unmap(bo, map);
    return 0;
}

GBM_EXPORT struct gbm_surface* gbm_surface_create(struct gbm_device* device, uint32_t width, uint32_t height,
                                                  uint32_t format, uint32_t flags) {
    if (!(device->gpu & 1) || !(flags & GBM_BO_USE_RENDERING) ||
        !gbm_device_is_format_supported(device, format, flags) || !width || !height || width > 4096 || height > 4096) {
        errno = EINVAL;
        return NULL;
    }
    struct gbm_surface* surface = calloc(1, sizeof(*surface));
    if (!surface)
        return NULL;
    surface->device = device;
    surface->width = width;
    surface->height = height;
    surface->format = format;
    surface->refs = 1;
    atomic_fetch_add(&device->refs, 1);
    pthread_mutex_lock(&registry_lock);
    surface->next = surfaces;
    surfaces = surface;
    pthread_mutex_unlock(&registry_lock);
    return surface;
}

static void finish_slot(struct gbm_surface* surface, struct gbm_bo* bo) {
    if (bo->locked) {
        uint32_t request[2] = {bo->handle, 0};
        ioctl(surface->device->fd, DRM_LOCK_GPU_BO, request);
    }
    if (bo->destroy_user)
        bo->destroy_user(bo, bo->user);
    *bo = (struct gbm_bo){0};
}

static void free_surface(struct gbm_surface* surface) {
    for (unsigned i = 0; i < PG_GBM_SLOTS; ++i)
        finish_slot(surface, &surface->slots[i]);
    gbm_device_destroy(surface->device);
    free(surface);
}

GBM_EXPORT void gbm_surface_destroy(struct gbm_surface* surface) {
    pthread_mutex_lock(&registry_lock);
    struct gbm_surface** link = &surfaces;
    while (*link != surface)
        link = &(*link)->next;
    *link = surface->next;
    unsigned remaining = --surface->refs;
    pthread_mutex_unlock(&registry_lock);
    if (remaining == 0)
        free_surface(surface);
}

GBM_EXPORT int podish_gbm_is_device(void* candidate) {
    int found = 0;
    pthread_mutex_lock(&registry_lock);
    for (struct gbm_device* device = devices; device; device = device->next)
        if (device == candidate && device->gpu) {
            found = (int)device->gpu;
            break;
        }
    pthread_mutex_unlock(&registry_lock);
    return found;
}

GBM_EXPORT int podish_gbm_begin(void* device, struct gbm_surface* candidate, struct pg_gbm_geometry* geometry) {
    struct gbm_surface* found = NULL;
    pthread_mutex_lock(&registry_lock);
    for (struct gbm_surface* surface = surfaces; surface; surface = surface->next) {
        if (surface != candidate || surface->device != device || surface->attached)
            continue;
        surface->refs++;
        surface->attached = 1;
        found = surface;
        break;
    }
    pthread_mutex_unlock(&registry_lock);
    if (!found)
        return 0;
    for (unsigned i = 0; i < PG_GBM_SLOTS; ++i)
        finish_slot(found, &found->slots[i]);
    *geometry = (struct pg_gbm_geometry){found->device->fd, found->width, found->height};
    return 1;
}

GBM_EXPORT void podish_gbm_set_buffers(struct gbm_surface* surface, const uint32_t* handles) {
    for (unsigned i = 0; i < PG_GBM_SLOTS; ++i)
        surface->slots[i] = (struct gbm_bo){.device = surface->device,
                                            .width = surface->width,
                                            .height = surface->height,
                                            .format = surface->format,
                                            .stride = surface->width * 4,
                                            .handle = handles[i]};
}

GBM_EXPORT void podish_gbm_end(struct gbm_surface* surface) {
    pthread_mutex_lock(&registry_lock);
    surface->ready = 0;
    surface->attached = 0;
    unsigned remaining = --surface->refs;
    pthread_mutex_unlock(&registry_lock);
    if (remaining == 0)
        free_surface(surface);
}

GBM_EXPORT void podish_gbm_publish(struct gbm_surface* surface, uint32_t handle) {
    pthread_mutex_lock(&registry_lock);
    surface->ready = handle;
    pthread_mutex_unlock(&registry_lock);
}

GBM_EXPORT struct gbm_bo* gbm_surface_lock_front_buffer(struct gbm_surface* surface) {
    struct gbm_bo* result = NULL;
    pthread_mutex_lock(&registry_lock);
    for (unsigned i = 0; i < PG_GBM_SLOTS && surface->ready; ++i) {
        struct gbm_bo* bo = &surface->slots[i];
        if (bo->handle != surface->ready || bo->locked)
            continue;
        uint32_t request[2] = {bo->handle, 1};
        if (ioctl(surface->device->fd, DRM_LOCK_GPU_BO, request) == 0) {
            bo->locked = 1;
            surface->ready = 0;
            result = bo;
        }
        break;
    }
    pthread_mutex_unlock(&registry_lock);
    return result;
}

GBM_EXPORT void gbm_surface_release_buffer(struct gbm_surface* surface, struct gbm_bo* bo) {
    pthread_mutex_lock(&registry_lock);
    for (unsigned i = 0; i < PG_GBM_SLOTS; ++i) {
        if (bo != &surface->slots[i] || !bo->locked)
            continue;
        uint32_t request[2] = {bo->handle, 0};
        ioctl(surface->device->fd, DRM_LOCK_GPU_BO, request);
        bo->locked = 0;
        break;
    }
    pthread_mutex_unlock(&registry_lock);
}
