#pragma once

#include <stdbool.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#define PODISH_ZINK_EXPORT __attribute__((visibility("default")))

/* Lifecycle calls are serialized by the VirGL renderer worker. */
PODISH_ZINK_EXPORT int podish_zink_initialize(void* vk_get_instance_proc_addr);
PODISH_ZINK_EXPORT void podish_zink_shutdown(void);
PODISH_ZINK_EXPORT void* podish_zink_create_context(bool shared, int major, int minor, bool compatibility);
PODISH_ZINK_EXPORT void podish_zink_destroy_context(void* context);
PODISH_ZINK_EXPORT int podish_zink_make_current(void* context);
PODISH_ZINK_EXPORT void* podish_zink_get_proc_address(const char* name);
PODISH_ZINK_EXPORT void* podish_zink_get_egl_display(void);

/* Borrowed MTLTexture while the shared GL texture remains alive. */
PODISH_ZINK_EXPORT void* podish_zink_export_texture(uint32_t texture);

#ifdef __cplusplus
}
#endif
