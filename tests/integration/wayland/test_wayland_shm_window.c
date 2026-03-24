#define _GNU_SOURCE

#include <errno.h>
#include <fcntl.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/mman.h>
#include <sys/stat.h>
#include <sys/syscall.h>
#include <sys/types.h>
#include <unistd.h>
#include <wayland-client.h>

#include "xdg-shell-client-protocol.h"

struct app_state {
    struct wl_display* display;
    struct wl_registry* registry;
    struct wl_compositor* compositor;
    struct wl_shm* shm;
    struct xdg_wm_base* wm_base;
    struct wl_surface* surface;
    struct xdg_surface* xdg_surface;
    struct xdg_toplevel* xdg_toplevel;
    struct wl_shm_pool* pool;
    struct wl_buffer* buffer;
    void* pixels;
    size_t pixels_len;
    bool configured;
    bool saw_compositor;
    bool saw_shm;
    bool saw_wm_base;
};

static int create_memfd(const char* name) {
#ifdef SYS_memfd_create
    return (int)syscall(SYS_memfd_create, name, 0);
#else
    errno = ENOSYS;
    return -1;
#endif
}

static void paint_pixels(uint32_t* pixels, int width, int height) {
    for (int y = 0; y < height; y++) {
        for (int x = 0; x < width; x++) {
            uint8_t r = (uint8_t)(x * 255 / (width > 1 ? width - 1 : 1));
            uint8_t g = (uint8_t)(y * 255 / (height > 1 ? height - 1 : 1));
            uint8_t b = 0x80;
            pixels[y * width + x] = 0xff000000u | ((uint32_t)r << 16) | ((uint32_t)g << 8) | b;
        }
    }
}

static void shm_format(void* data, struct wl_shm* wl_shm, uint32_t format) {
    (void)data;
    (void)wl_shm;
    (void)format;
}

static const struct wl_shm_listener shm_listener = {
    .format = shm_format,
};

static void wm_base_ping(void* data, struct xdg_wm_base* wm_base, uint32_t serial) {
    (void)data;
    xdg_wm_base_pong(wm_base, serial);
}

static const struct xdg_wm_base_listener wm_base_listener = {
    .ping = wm_base_ping,
};

static void xdg_surface_configure(void* data, struct xdg_surface* xdg_surface, uint32_t serial) {
    struct app_state* app = data;
    xdg_surface_ack_configure(xdg_surface, serial);
    app->configured = true;
}

static const struct xdg_surface_listener xdg_surface_listener = {
    .configure = xdg_surface_configure,
};

static void xdg_toplevel_configure(void* data, struct xdg_toplevel* xdg_toplevel, int32_t width, int32_t height,
                                   struct wl_array* states) {
    (void)data;
    (void)xdg_toplevel;
    (void)width;
    (void)height;
    (void)states;
}

static void xdg_toplevel_close(void* data, struct xdg_toplevel* xdg_toplevel) {
    (void)data;
    (void)xdg_toplevel;
}

static const struct xdg_toplevel_listener xdg_toplevel_listener = {
    .configure = xdg_toplevel_configure,
    .close = xdg_toplevel_close,
};

static void registry_global(void* data, struct wl_registry* registry, uint32_t name, const char* interface,
                            uint32_t version) {
    struct app_state* app = data;

    if (strcmp(interface, wl_compositor_interface.name) == 0) {
        uint32_t bind_version = version < 4 ? version : 4;
        app->compositor = wl_registry_bind(registry, name, &wl_compositor_interface, bind_version);
        app->saw_compositor = true;
        return;
    }

    if (strcmp(interface, wl_shm_interface.name) == 0) {
        app->shm = wl_registry_bind(registry, name, &wl_shm_interface, 1);
        wl_shm_add_listener(app->shm, &shm_listener, app);
        app->saw_shm = true;
        return;
    }

    if (strcmp(interface, xdg_wm_base_interface.name) == 0) {
        app->wm_base = wl_registry_bind(registry, name, &xdg_wm_base_interface, 1);
        xdg_wm_base_add_listener(app->wm_base, &wm_base_listener, app);
        app->saw_wm_base = true;
    }
}

static void registry_global_remove(void* data, struct wl_registry* registry, uint32_t name) {
    (void)data;
    (void)registry;
    (void)name;
}

static const struct wl_registry_listener registry_listener = {
    .global = registry_global,
    .global_remove = registry_global_remove,
};

static int create_buffer(struct app_state* app, int width, int height) {
    const int stride = width * 4;
    const int size = stride * height;

    int fd = create_memfd("podish-wayland-shm");
    if (fd < 0) {
        perror("memfd_create");
        return -1;
    }

    if (ftruncate(fd, size) != 0) {
        perror("ftruncate");
        close(fd);
        return -1;
    }

    void* pixels = mmap(NULL, (size_t)size, PROT_READ | PROT_WRITE, MAP_SHARED, fd, 0);
    if (pixels == MAP_FAILED) {
        perror("mmap");
        close(fd);
        return -1;
    }

    paint_pixels((uint32_t*)pixels, width, height);

    app->pool = wl_shm_create_pool(app->shm, fd, size);
    app->buffer = wl_shm_pool_create_buffer(app->pool, 0, width, height, stride, WL_SHM_FORMAT_ARGB8888);
    app->pixels = pixels;
    app->pixels_len = (size_t)size;

    close(fd);
    return 0;
}

static void destroy_app(struct app_state* app) {
    if (app->buffer != NULL) {
        wl_buffer_destroy(app->buffer);
    }
    if (app->pool != NULL) {
        wl_shm_pool_destroy(app->pool);
    }
    if (app->pixels != NULL && app->pixels_len > 0) {
        munmap(app->pixels, app->pixels_len);
    }
    if (app->xdg_toplevel != NULL) {
        xdg_toplevel_destroy(app->xdg_toplevel);
    }
    if (app->xdg_surface != NULL) {
        xdg_surface_destroy(app->xdg_surface);
    }
    if (app->surface != NULL) {
        wl_surface_destroy(app->surface);
    }
    if (app->wm_base != NULL) {
        xdg_wm_base_destroy(app->wm_base);
    }
    if (app->shm != NULL) {
        wl_shm_destroy(app->shm);
    }
    if (app->compositor != NULL) {
        wl_compositor_destroy(app->compositor);
    }
    if (app->registry != NULL) {
        wl_registry_destroy(app->registry);
    }
    if (app->display != NULL) {
        wl_display_disconnect(app->display);
    }
}

int main(void) {
    struct app_state app = {0};

    app.display = wl_display_connect(NULL);
    if (app.display == NULL) {
        fprintf(stderr, "failed to connect to wayland display\n");
        return 1;
    }
    printf("CONNECTED\n");

    app.registry = wl_display_get_registry(app.display);
    wl_registry_add_listener(app.registry, &registry_listener, &app);

    if (wl_display_roundtrip(app.display) < 0) {
        fprintf(stderr, "roundtrip after registry failed\n");
        destroy_app(&app);
        return 1;
    }

    if (!app.saw_compositor || !app.saw_shm || !app.saw_wm_base) {
        fprintf(stderr, "missing required globals compositor=%d shm=%d wm_base=%d\n", app.saw_compositor, app.saw_shm,
                app.saw_wm_base);
        destroy_app(&app);
        return 1;
    }
    printf("GLOBALS_OK\n");

    app.surface = wl_compositor_create_surface(app.compositor);
    app.xdg_surface = xdg_wm_base_get_xdg_surface(app.wm_base, app.surface);
    xdg_surface_add_listener(app.xdg_surface, &xdg_surface_listener, &app);
    app.xdg_toplevel = xdg_surface_get_toplevel(app.xdg_surface);
    xdg_toplevel_add_listener(app.xdg_toplevel, &xdg_toplevel_listener, &app);
    xdg_toplevel_set_title(app.xdg_toplevel, "podish-wayland-smoke");
    xdg_toplevel_set_app_id(app.xdg_toplevel, "podish.wayland.smoke");
    wl_surface_commit(app.surface);
    printf("SURFACE_CREATED\n");

    while (!app.configured) {
        if (wl_display_dispatch(app.display) < 0) {
            fprintf(stderr, "dispatch while waiting for configure failed\n");
            destroy_app(&app);
            return 1;
        }
    }
    printf("CONFIGURED\n");

    if (create_buffer(&app, 64, 64) != 0) {
        destroy_app(&app);
        return 1;
    }

    wl_surface_attach(app.surface, app.buffer, 0, 0);
    wl_surface_damage_buffer(app.surface, 0, 0, 64, 64);
    wl_surface_commit(app.surface);
    printf("BUFFER_COMMITTED\n");

    if (wl_display_roundtrip(app.display) < 0) {
        fprintf(stderr, "final roundtrip failed\n");
        destroy_app(&app);
        return 1;
    }

    printf("SUCCESS\n");
    destroy_app(&app);
    return 0;
}
