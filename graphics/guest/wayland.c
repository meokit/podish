#define _GNU_SOURCE
#include "wayland.h"
#include "transport.h"
#include <dlfcn.h>
#include <errno.h>
#include <pthread.h>
#include <stdlib.h>
#include <sys/mman.h>
#include <sys/syscall.h>
#include <unistd.h>
#include <wayland-egl-backend.h>

struct wl_interface;
struct wl_proxy;
struct wl_event_queue;
struct wl_display;

/* Resolve Wayland only when creating a Wayland surface. fbdev has no Wayland dependency. */
static struct {
    struct wl_proxy* (*marshal)(struct wl_proxy*, uint32_t, const struct wl_interface*, uint32_t version,
                                uint32_t flags, ...);
    uint32_t (*version)(struct wl_proxy*);
    int (*listen)(struct wl_proxy*, void (**)(void), void*);
    struct wl_proxy* (*wrap)(void*);
    void (*set_queue)(struct wl_proxy*, struct wl_event_queue*);
    void (*destroy)(struct wl_proxy*);
    void (*destroy_wrapper)(void*);
    struct wl_event_queue* (*create_queue)(struct wl_display*);
    void (*destroy_queue)(struct wl_event_queue*);
    int (*roundtrip)(struct wl_display*, struct wl_event_queue*);
    int (*dispatch)(struct wl_display*, struct wl_event_queue*);
    int (*flush)(struct wl_display*);
    const struct wl_interface *registry, *shm, *pool, *buffer;
    int ready;
} wl;

static pthread_once_t load_once = PTHREAD_ONCE_INIT;

static void load_wayland(void) {
    void* lib = dlopen("libwayland-client.so.0", RTLD_NOW | RTLD_LOCAL);
    if (!lib)
        return;
#define LOAD(member, symbol)                                                                                           \
    do {                                                                                                               \
        wl.member = dlsym(lib, symbol);                                                                                \
        if (!wl.member)                                                                                                \
            goto fail;                                                                                                 \
    } while (0)
    LOAD(marshal, "wl_proxy_marshal_flags");
    LOAD(version, "wl_proxy_get_version");
    LOAD(listen, "wl_proxy_add_listener");
    LOAD(wrap, "wl_proxy_create_wrapper");
    LOAD(set_queue, "wl_proxy_set_queue");
    LOAD(destroy, "wl_proxy_destroy");
    LOAD(destroy_wrapper, "wl_proxy_wrapper_destroy");
    LOAD(create_queue, "wl_display_create_queue");
    LOAD(destroy_queue, "wl_event_queue_destroy");
    LOAD(roundtrip, "wl_display_roundtrip_queue");
    LOAD(dispatch, "wl_display_dispatch_queue");
    LOAD(flush, "wl_display_flush");
    LOAD(registry, "wl_registry_interface");
    LOAD(shm, "wl_shm_interface");
    LOAD(pool, "wl_shm_pool_interface");
    LOAD(buffer, "wl_buffer_interface");
#undef LOAD
    wl.ready = 1;
    return;
fail:
    dlclose(lib);
}

struct slot {
    struct wl_proxy* buffer;
    void* pixels;
    size_t bytes;
    int busy;
};

struct pg_wayland {
    struct wl_egl_window* window;
    struct wl_display* display;
    struct wl_event_queue* queue;
    struct wl_proxy* surface;
    struct wl_proxy* shm;
    struct slot slots[2];
    int width, height;
};

static void global(void* data, struct wl_proxy* registry, uint32_t name, const char* interface, uint32_t version) {
    struct pg_wayland* window = data;
    (void)version;
    if (!window->shm && strcmp(interface, "wl_shm") == 0)
        window->shm = wl.marshal(registry, 0, wl.shm, 1, 0, name, "wl_shm", 1u, NULL);
}
static void global_remove(void* data, struct wl_proxy* registry, uint32_t name) {
    (void)data;
    (void)registry;
    (void)name;
}

static const struct {
    void (*global)(void*, struct wl_proxy*, uint32_t, const char*, uint32_t);
    void (*remove)(void*, struct wl_proxy*, uint32_t);
} registry_listener = {global, global_remove};

static void released(void* data, struct wl_proxy* buffer) {
    (void)buffer;
    ((struct slot*)data)->busy = 0;
}

static const struct {
    void (*release)(void*, struct wl_proxy*);
} buffer_listener = {released};

static void destroyed(void* data) {
    struct pg_wayland* window = data;
    window->window = NULL;
}

struct pg_wayland* pg_wayland_create(void* display, struct wl_egl_window* native) {
    if (!display || !native || native->version != WL_EGL_WINDOW_VERSION || !native->surface || native->driver_private)
        return NULL;

    pthread_once(&load_once, load_wayland);
    if (!wl.ready)
        return NULL;
    struct pg_wayland* window = calloc(1, sizeof(*window));
    if (!window)
        return NULL;
    window->display = display;
    window->queue = wl.create_queue(display);
    if (!window->queue)
        goto fail;

    struct wl_proxy* wrapper = wl.wrap(display);
    if (!wrapper)
        goto fail;
    wl.set_queue(wrapper, window->queue);
    struct wl_proxy* registry = wl.marshal(wrapper, 1, wl.registry, wl.version(wrapper), 0, NULL);
    wl.destroy_wrapper(wrapper);
    if (!registry)
        goto fail;
    wl.listen(registry, (void (**)(void))&registry_listener, window);
    int status = wl.roundtrip(display, window->queue);
    wl.destroy(registry);
    if (status < 0 || !window->shm)
        goto fail;

    window->surface = wl.wrap(native->surface);
    if (!window->surface)
        goto fail;
    wl.set_queue(window->surface, window->queue);
    window->window = native;
    native->driver_private = window;
    native->destroy_window_callback = destroyed;
    return window;
fail:
    pg_wayland_destroy(window);
    return NULL;
}

static void clear_slot(struct slot* slot) {
    if (slot->buffer)
        wl.marshal(slot->buffer, 0, NULL, wl.version(slot->buffer), 1);
    if (slot->pixels)
        munmap(slot->pixels, slot->bytes);
    *slot = (struct slot){0};
}

void pg_wayland_destroy(struct pg_wayland* window) {
    if (!window)
        return;
    if (window->window) {
        window->window->driver_private = NULL;
        window->window->destroy_window_callback = NULL;
    }
    for (int i = 0; i < 2; ++i)
        clear_slot(&window->slots[i]);
    if (window->shm)
        wl.destroy(window->shm);
    if (window->surface)
        wl.destroy_wrapper(window->surface);
    if (window->queue)
        wl.destroy_queue(window->queue);
    free(window);
}

int pg_wayland_size(struct pg_wayland* window, int* width, int* height) {
    if (!window->window)
        return -1;
    *width = window->window->width;
    *height = window->window->height;
    return 0;
}

static int alloc_slot(struct pg_wayland* window, struct slot* slot, int width, int height) {
    const size_t bytes = (size_t)width * height * 4;
    int fd = (int)syscall(SYS_memfd_create, "podish-egl", 1u /* MFD_CLOEXEC */);
    if (fd < 0)
        return -1;
    if (ftruncate(fd, (off_t)bytes) < 0) {
        close(fd);
        return -1;
    }
    void* pixels = mmap(NULL, bytes, PROT_READ | PROT_WRITE, MAP_SHARED, fd, 0);
    if (pixels == MAP_FAILED) {
        close(fd);
        return -1;
    }
    struct wl_proxy* pool = wl.marshal(window->shm, 0, wl.pool, 1, 0, NULL, fd, (int)bytes);
    close(fd);
    if (!pool) {
        munmap(pixels, bytes);
        return -1;
    }
    struct wl_proxy* buffer =
        wl.marshal(pool, 0, wl.buffer, 1, 0, NULL, 0, width, height, width * 4, 1u /* XRGB8888 */);
    wl.marshal(pool, 1, NULL, wl.version(pool), 1);
    if (!buffer) {
        munmap(pixels, bytes);
        return -1;
    }
    *slot = (struct slot){buffer, pixels, bytes, 0};
    wl.listen(buffer, (void (**)(void))&buffer_listener, slot);
    return 0;
}

int pg_wayland_present(struct pg_wayland* window, uint32_t surface, int width, int height) {
    if (!window->window)
        return -0x300B;
    if (window->width != width || window->height != height) {
        for (int i = 0; i < 2; ++i)
            clear_slot(&window->slots[i]);
        window->width = width;
        window->height = height;
    }
    struct slot* slot;
    for (;;) {
        slot = !window->slots[0].busy ? &window->slots[0] : !window->slots[1].busy ? &window->slots[1] : NULL;
        if (slot)
            break;
        if (wl.dispatch(window->display, window->queue) < 0)
            return -0x300B;
    }
    if (!slot->buffer && alloc_slot(window, slot, width, height) < 0)
        return -0x3003;
    int result = pg_gl(PG_FRAME, surface, pg_ptr(slot->pixels), (uint32_t)slot->bytes);
    if (result < 0)
        return result;

    uint32_t version = wl.version(window->surface);
    int dx = window->window->dx;
    int dy = window->window->dy;
    if (version >= 5 && (dx || dy))
        wl.marshal(window->surface, 10, NULL, version, 0, dx, dy); /* wl_surface.offset */
    wl.marshal(window->surface, 1, NULL, version, 0, slot->buffer, version >= 5 ? 0 : dx, version >= 5 ? 0 : dy);
    if (version >= 4)
        wl.marshal(window->surface, 9, NULL, version, 0, 0, 0, width, height); /* damage_buffer */
    else
        wl.marshal(window->surface, 2, NULL, version, 0, 0, 0, INT32_MAX, INT32_MAX);
    wl.marshal(window->surface, 6, NULL, version, 0); /* commit */
    slot->busy = 1;
    window->window->dx = 0;
    window->window->dy = 0;
    window->window->attached_width = width;
    window->window->attached_height = height;
    if (wl.flush(window->display) < 0 && errno != EAGAIN)
        return -0x300B;
    return 0;
}
