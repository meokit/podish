#pragma once

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* Colors are premultiplied RGBA. Geometry is in drawable pixels. */
struct podish_compositor_draw {
    float rect[4];
    float color[4];
    float title_color[4];
    float uv_origin[2];
    float uv_basis[4];
    float params[4];
    float mode;
    float padding;
};

void* podish_compositor_create(void* metal_layer, int width, int height);
void podish_compositor_destroy(void* ctx);
int podish_compositor_resize(void* ctx, int width, int height);
int podish_compositor_begin(void* ctx);
uint64_t podish_compositor_end(void* ctx);
uint64_t podish_compositor_completed(void* ctx);
const char* podish_compositor_last_error(void);

/* 0: XRGB8888, 1: ARGB8888, 2: RGBA8888, as packed guest pixels in memory. */
void* podish_compositor_texture_create(void* ctx, int width, int height, int format, int nearest);
int podish_compositor_texture_update(void* ctx, void* texture, int x, int y, int width, int height, const void* pixels,
                                     int pitch);
void podish_compositor_texture_destroy(void* ctx, void* texture);
void* podish_compositor_texture_import_fd(void* ctx, int host_fd, uint64_t backing_size, uint64_t offset, int stride,
                                          int width, int height, int format);
void* podish_compositor_texture_import_metal(void* ctx, void* metal_texture, int width, int height, int format);
void podish_compositor_draw(void* ctx, void* texture, const struct podish_compositor_draw* args);

#ifdef __cplusplus
}
#endif
