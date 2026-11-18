#define _GNU_SOURCE
#include "gbm_egl.h"
#include "transport.h"
#include "wayland.h"
#define EGLAPI PODISH_EXPORT
#include <EGL/egl.h>
#include <dlfcn.h>
#include <pthread.h>
#include <stdlib.h>

#define PG_PLATFORM_GBM 0x31D7
#define PG_GBM_ARGB8888 0x34325241
#define PG_GBM_XRGB8888 0x34325258
#define PG_PLATFORM_WAYLAND 0x31D8
#define PG_PLATFORM_DEVICE 0x313F
#define PG_DEVICE_ATTRIBUTE 0x322C
#define PG_DRM_RENDER_NODE_FILE 0x3377
#define PG_BAD_DEVICE 0x322B
#define PG_DEVICE ((void*)(uintptr_t)1)
#define PG_PLATFORM_SURFACELESS 0x31DD
#define PG_CONFIG ((EGLConfig)(uintptr_t)1)
#define PG_MAX_SIZE 4096
#define PG_LINUX_DMA_BUF 0x3270
#define PG_DMA_FOURCC 0x3271
#define PG_DMA_FD 0x3272
#define PG_DMA_OFFSET 0x3273
#define PG_DMA_PITCH 0x3274
#define PG_IMAGE_PRESERVED 0x30D2

enum surface_kind { PBUFFER, FBDEV, WAYLAND, FBDEV_PBUFFER, GBM };
struct context {
    uint32_t id;
    struct context* next;
};
struct surface {
    uint32_t id;
    enum surface_kind kind;
    int width, height;
    int references, deleted;
    struct pg_wayland* window;
    void* gbm_window;
    struct surface* next;
};
struct image {
    uint32_t id;
    struct image* next;
};
struct display {
    EGLenum platform;
    void* native;
    int managed_gbm;
    int initialized;
    struct context* contexts;
    struct surface* surfaces;
    struct image* images;
    struct display* next;
};

static pthread_mutex_t lock = PTHREAD_MUTEX_INITIALIZER;
static struct display* displays;
static _Thread_local EGLint error = EGL_SUCCESS;
static _Thread_local struct display* current;
static _Thread_local uint32_t current_context;
static _Thread_local struct surface *current_read, *current_draw;
static pthread_key_t thread_binding_key;
static pthread_once_t thread_binding_once = PTHREAD_ONCE_INIT;
static int thread_binding_ready;

static struct {
    int (*is_device)(void*);
    int (*begin)(void*, void*, struct pg_gbm_geometry*);
    void (*buffers)(void*, const uint32_t*);
    void (*end)(void*);
    void (*publish)(void*, uint32_t);
    int ready;
} gbm;
static pthread_once_t gbm_once = PTHREAD_ONCE_INIT;

static void load_gbm(void) {
    void* lib = dlopen("libgbm.so.1", RTLD_NOW | RTLD_LOCAL);
    if (!lib)
        return;
    gbm.is_device = dlsym(lib, "podish_gbm_is_device");
    gbm.begin = dlsym(lib, "podish_gbm_begin");
    gbm.buffers = dlsym(lib, "podish_gbm_set_buffers");
    gbm.end = dlsym(lib, "podish_gbm_end");
    gbm.publish = dlsym(lib, "podish_gbm_publish");
    gbm.ready = gbm.is_device && gbm.begin && gbm.buffers && gbm.end && gbm.publish;
    if (!gbm.ready)
        dlclose(lib);
}

static int gbm_device(void* native) {
    pthread_once(&gbm_once, load_gbm);
    return gbm.ready ? gbm.is_device(native) : 0;
}

static void release_binding(void* bound) {
    (void)bound;
    eglReleaseThread();
}

static void register_binding_key(void) {
    thread_binding_ready = pthread_key_create(&thread_binding_key, release_binding) == 0;
}

static void unlock(pthread_mutex_t** guard) {
    pthread_mutex_unlock(*guard);
}
#define WITH_LOCK                                                                                                      \
    pthread_mutex_t* guard __attribute__((cleanup(unlock))) = &lock;                                                   \
    pthread_mutex_lock(guard)

static int fail(EGLint value) {
    error = value;
    return EGL_FALSE;
}

static int called(int result) {
    if (result >= 0)
        return EGL_TRUE;
    if (result <= -EGL_NOT_INITIALIZED && result >= -0x300E)
        return fail(-result);
    return fail(EGL_BAD_ACCESS);
}

static struct display* lookup(EGLDisplay handle, int needs_init) {
    for (struct display* d = displays; d; d = d->next) {
        if (d != handle)
            continue;
        if (needs_init && !d->initialized) {
            fail(EGL_NOT_INITIALIZED);
            return NULL;
        }
        return d;
    }
    fail(EGL_BAD_DISPLAY);
    return NULL;
}

static struct surface* lookup_surface(struct display* d, EGLSurface handle) {
    uint32_t id = pg_ptr(handle);
    for (struct surface* s = d->surfaces; s; s = s->next)
        if (s->id == id && !s->deleted)
            return s;
    fail(EGL_BAD_SURFACE);
    return NULL;
}

static struct context** lookup_context(struct display* d, EGLContext handle) {
    uint32_t id = pg_ptr(handle);
    for (struct context** c = &d->contexts; *c; c = &(*c)->next)
        if ((*c)->id == id)
            return c;
    fail(EGL_BAD_CONTEXT);
    return NULL;
}

static void collect_surface(struct display* d, struct surface* s) {
    if (!s || !s->deleted || s->references)
        return;
    struct surface** link = &d->surfaces;
    while (*link != s)
        link = &(*link)->next;
    *link = s->next;
    pg_wayland_destroy(s->window);
    if (s->gbm_window)
        gbm.end(s->gbm_window);
    free(s);
}

static void replace_current(struct display* d, uint32_t ctx, struct surface* draw, struct surface* read) {
    if (draw)
        draw->references++;
    if (read)
        read->references++;
    if (current_draw)
        current_draw->references--;
    if (current_read)
        current_read->references--;
    if (current_read != current_draw)
        collect_surface(current, current_read);
    collect_surface(current, current_draw);
    current = ctx ? d : NULL;
    current_context = ctx;
    current_draw = draw;
    current_read = read;
    if (ctx)
        pthread_once(&thread_binding_once, register_binding_key);
    if (thread_binding_ready)
        pthread_setspecific(thread_binding_key, current);
}

static EGLDisplay get_display(EGLenum platform, void* native) {
    WITH_LOCK;
    for (struct display* d = displays; d; d = d->next)
        if (d->platform == platform && d->native == native)
            return d;
    struct display* d = calloc(1, sizeof(*d));
    if (!d) {
        fail(EGL_BAD_ALLOC);
        return EGL_NO_DISPLAY;
    }
    d->platform = platform;
    d->native = native;
    d->managed_gbm = platform == PG_PLATFORM_GBM && (gbm_device(native) & 2);
    d->next = displays;
    displays = d;
    return d;
}

EGLDisplay eglGetDisplay(EGLNativeDisplayType native) {
    if (native && getenv("PODISH_KMSDRM") && gbm_device(native))
        return get_display(PG_PLATFORM_GBM, native);
    return get_display(native ? PG_PLATFORM_WAYLAND : 0, native);
}

EGLDisplay eglGetPlatformDisplay(EGLenum platform, void* native, const EGLAttrib* attribs) {
    if (attribs && attribs[0] != EGL_NONE) {
        fail(EGL_BAD_ATTRIBUTE);
        return EGL_NO_DISPLAY;
    }
    switch (platform) {
    case PG_PLATFORM_GBM:
        if (gbm_device(native))
            return get_display(platform, native);
        break;
    case PG_PLATFORM_WAYLAND:
        if (native)
            return get_display(platform, native);
        break;
    case PG_PLATFORM_SURFACELESS:
        if (!native)
            return get_display(platform, NULL);
        break;
    case PG_PLATFORM_DEVICE:
        if (native == PG_DEVICE)
            return get_display(PG_PLATFORM_SURFACELESS, NULL);
        break;
    }
    fail(EGL_BAD_PARAMETER);
    return EGL_NO_DISPLAY;
}

PODISH_EXPORT EGLDisplay eglGetPlatformDisplayEXT(EGLenum platform, void* native, const EGLint* attribs) {
    return eglGetPlatformDisplay(platform, native, attribs);
}

PODISH_EXPORT EGLBoolean eglQueryDevicesEXT(EGLint max, void** devices, EGLint* count) {
    if (!count || (devices && max < 1))
        return fail(EGL_BAD_PARAMETER);
    if (devices)
        devices[0] = PG_DEVICE;
    *count = 1;
    return EGL_TRUE;
}

PODISH_EXPORT const char* eglQueryDeviceStringEXT(void* device, EGLint name) {
    if (device != PG_DEVICE)
        return (fail(PG_BAD_DEVICE), NULL);
    if (name == EGL_EXTENSIONS)
        return getenv("PODISH_KMSDRM") ? "EGL_EXT_device_drm_render_node" : "";
    if (name == PG_DRM_RENDER_NODE_FILE && getenv("PODISH_KMSDRM"))
        return "/dev/dri/renderD128";
    return (fail(EGL_BAD_PARAMETER), NULL);
}

PODISH_EXPORT EGLBoolean eglQueryDeviceAttribEXT(void* device, EGLint attribute, EGLAttrib* value) {
    (void)attribute;
    (void)value;
    return fail(device == PG_DEVICE ? EGL_BAD_ATTRIBUTE : PG_BAD_DEVICE);
}

PODISH_EXPORT EGLBoolean eglQueryDisplayAttribEXT(EGLDisplay display, EGLint attribute, EGLAttrib* value) {
    WITH_LOCK;
    if (!lookup(display, 1))
        return EGL_FALSE;
    if (attribute != PG_DEVICE_ATTRIBUTE)
        return fail(EGL_BAD_ATTRIBUTE);
    if (!value)
        return fail(EGL_BAD_PARAMETER);
    *value = (EGLAttrib)PG_DEVICE;
    return EGL_TRUE;
}

EGLint eglGetError(void) {
    EGLint previous = error;
    error = EGL_SUCCESS;
    return previous;
}

EGLBoolean eglInitialize(EGLDisplay display, EGLint* major, EGLint* minor) {
    WITH_LOCK;
    struct display* d = lookup(display, 0);
    if (!d)
        return EGL_FALSE;
    if (!d->initialized) {
        if (pg_gl(PG_INIT, PG_ABI) != PG_ABI)
            return fail(EGL_NOT_INITIALIZED);
        d->initialized = 1;
    }
    if (major)
        *major = 1;
    if (minor)
        *minor = 4;
    return EGL_TRUE;
}

EGLBoolean eglTerminate(EGLDisplay display) {
    WITH_LOCK;
    struct display* d = lookup(display, 0);
    if (!d)
        return EGL_FALSE;
    while (d->images) {
        struct image* image = d->images;
        d->images = image->next;
        pg_gl(PG_DESTROY_IMAGE, image->id);
        free(image);
    }
    while (d->contexts) {
        struct context* c = d->contexts;
        d->contexts = c->next;
        pg_gl(PG_DESTROY_CONTEXT, c->id);
        free(c);
    }
    for (struct surface *s = d->surfaces, *next; s; s = next) {
        next = s->next;
        if (!s->deleted)
            pg_gl(PG_DESTROY_SURFACE, s->id);
        s->deleted = 1;
        collect_surface(d, s);
    }
    d->initialized = 0;
    return EGL_TRUE;
}

const char* eglQueryString(EGLDisplay display, EGLint name) {
#define PG_CLIENT_EXTENSIONS                                                                                           \
    "EGL_EXT_client_extensions EGL_EXT_platform_base EGL_EXT_platform_wayland "                                        \
    "EGL_KHR_platform_wayland EGL_MESA_platform_surfaceless EGL_EXT_device_base "                                      \
    "EGL_EXT_device_enumeration EGL_EXT_device_query EGL_EXT_platform_device"
    if (display == EGL_NO_DISPLAY && name == EGL_EXTENSIONS)
        return getenv("PODISH_KMSDRM") ? PG_CLIENT_EXTENSIONS " EGL_KHR_platform_gbm EGL_MESA_platform_gbm"
                                       : PG_CLIENT_EXTENSIONS;
#undef PG_CLIENT_EXTENSIONS
    WITH_LOCK;
    struct display* d = lookup(display, 1);
    if (!d)
        return NULL;
    switch (name) {
    case EGL_VENDOR:
        return "Podish (ANGLE)";
    case EGL_VERSION:
        return "1.4 Podish";
    case EGL_CLIENT_APIS:
        return "OpenGL_ES";
    case EGL_EXTENSIONS:
        if (d->managed_gbm)
            return "EGL_KHR_surfaceless_context EGL_KHR_no_config_context EGL_KHR_image_base "
                   "EGL_EXT_image_dma_buf_import EGL_EXT_image_dma_buf_import_modifiers";
        return "EGL_KHR_surfaceless_context";
    default:
        fail(EGL_BAD_PARAMETER);
        return NULL;
    }
}

static EGLint config_attribute(const struct display* display, EGLint attr) {
    switch (attr) {
    case EGL_CONFIG_ID:
        return 1;
    case EGL_RED_SIZE:
    case EGL_GREEN_SIZE:
    case EGL_BLUE_SIZE:
    case EGL_ALPHA_SIZE:
    case EGL_STENCIL_SIZE:
        return 8;
    case EGL_DEPTH_SIZE:
        return 24;
    case EGL_BUFFER_SIZE:
        return 32;
    case EGL_SURFACE_TYPE:
        return EGL_PBUFFER_BIT | (display->platform == PG_PLATFORM_SURFACELESS ? 0 : EGL_WINDOW_BIT);
    case EGL_RENDERABLE_TYPE:
    case EGL_CONFORMANT:
        return EGL_OPENGL_ES2_BIT;
    case EGL_NATIVE_VISUAL_ID:
        return display->platform == PG_PLATFORM_GBM ? PG_GBM_ARGB8888 : 0;
    case EGL_NATIVE_RENDERABLE:
        return EGL_FALSE;
    case EGL_CONFIG_CAVEAT:
    case EGL_TRANSPARENT_TYPE:
    case EGL_NATIVE_VISUAL_TYPE:
        return EGL_NONE;
    case EGL_COLOR_BUFFER_TYPE:
        return EGL_RGB_BUFFER;
    case EGL_LEVEL:
    case EGL_SAMPLE_BUFFERS:
    case EGL_SAMPLES:
    case EGL_LUMINANCE_SIZE:
    case EGL_ALPHA_MASK_SIZE:
    case EGL_TRANSPARENT_RED_VALUE:
    case EGL_TRANSPARENT_GREEN_VALUE:
    case EGL_TRANSPARENT_BLUE_VALUE:
        return 0;
    case EGL_BIND_TO_TEXTURE_RGB:
    case EGL_BIND_TO_TEXTURE_RGBA:
        return EGL_FALSE;
    case EGL_MIN_SWAP_INTERVAL:
        return 0;
    case EGL_MAX_SWAP_INTERVAL:
        return 1;
    case EGL_MAX_PBUFFER_WIDTH:
    case EGL_MAX_PBUFFER_HEIGHT:
        return PG_MAX_SIZE;
    case EGL_MAX_PBUFFER_PIXELS:
        return PG_MAX_SIZE * PG_MAX_SIZE;
    default:
        return -1;
    }
}

static EGLBoolean write_config(EGLConfig* configs, EGLint size, EGLint* count, int matches) {
    if (!count || size < 0)
        return fail(EGL_BAD_PARAMETER);
    if (!configs)
        *count = matches;
    else if (matches && size > 0) {
        configs[0] = PG_CONFIG;
        *count = 1;
    } else
        *count = 0;
    return EGL_TRUE;
}

EGLBoolean eglGetConfigs(EGLDisplay display, EGLConfig* configs, EGLint size, EGLint* count) {
    WITH_LOCK;
    return lookup(display, 1) && write_config(configs, size, count, 1);
}

EGLBoolean eglChooseConfig(EGLDisplay display, const EGLint* attribs, EGLConfig* configs, EGLint size, EGLint* count) {
    WITH_LOCK;
    struct display* d = lookup(display, 1);
    if (!d)
        return EGL_FALSE;
    int matches = 1;
    for (const EGLint* a = attribs; a && *a != EGL_NONE; a += 2) {
        const EGLint key = a[0], requested = a[1], actual = config_attribute(d, key);
        if (key == EGL_MATCH_NATIVE_PIXMAP) {
            if (requested != EGL_NONE)
                matches = 0;
            continue;
        }
        if (actual < 0)
            return fail(EGL_BAD_ATTRIBUTE);
        if (requested == EGL_DONT_CARE)
            continue;
        switch (key) {
        case EGL_BUFFER_SIZE:
        case EGL_RED_SIZE:
        case EGL_GREEN_SIZE:
        case EGL_BLUE_SIZE:
        case EGL_ALPHA_SIZE:
        case EGL_STENCIL_SIZE:
        case EGL_DEPTH_SIZE:
        case EGL_SAMPLES:
        case EGL_SAMPLE_BUFFERS:
        case EGL_LUMINANCE_SIZE:
        case EGL_ALPHA_MASK_SIZE:
            matches &= actual >= requested;
            break;
        case EGL_SURFACE_TYPE:
        case EGL_RENDERABLE_TYPE:
        case EGL_CONFORMANT:
            matches &= (actual & requested) == requested;
            break;
        default:
            matches &= actual == requested;
            break;
        }
    }
    return write_config(configs, size, count, matches);
}

EGLBoolean eglGetConfigAttrib(EGLDisplay display, EGLConfig config, EGLint attribute, EGLint* value) {
    WITH_LOCK;
    struct display* d = lookup(display, 1);
    if (!d)
        return EGL_FALSE;
    if (config != PG_CONFIG)
        return fail(EGL_BAD_CONFIG);
    if (!value)
        return fail(EGL_BAD_PARAMETER);
    EGLint actual = config_attribute(d, attribute);
    if (actual < 0)
        return fail(EGL_BAD_ATTRIBUTE);
    *value = actual;
    return EGL_TRUE;
}

EGLBoolean eglBindAPI(EGLenum api) {
    return api == EGL_OPENGL_ES_API ? EGL_TRUE : fail(EGL_BAD_PARAMETER);
}
EGLenum eglQueryAPI(void) {
    return EGL_OPENGL_ES_API;
}

EGLContext eglCreateContext(EGLDisplay display, EGLConfig config, EGLContext share, const EGLint* attribs) {
    WITH_LOCK;
    struct display* d = lookup(display, 1);
    if (!d)
        return EGL_NO_CONTEXT;
    int managed = config == NULL && d->managed_gbm;
    if (config != PG_CONFIG && !managed)
        return (fail(EGL_BAD_CONFIG), EGL_NO_CONTEXT);
    if (share && !lookup_context(d, share))
        return EGL_NO_CONTEXT;

    EGLint version = 1;
    for (const EGLint* a = attribs; a && *a != EGL_NONE; a += 2) {
        if (a[0] != EGL_CONTEXT_CLIENT_VERSION)
            return (fail(EGL_BAD_ATTRIBUTE), EGL_NO_CONTEXT);
        version = a[1];
    }
    if (version != 2)
        return (fail(EGL_BAD_MATCH), EGL_NO_CONTEXT);

    struct context* c = calloc(1, sizeof(*c));
    if (!c)
        return (fail(EGL_BAD_ALLOC), EGL_NO_CONTEXT);
    int id = pg_gl(PG_CREATE_CONTEXT, pg_ptr(share), managed);
    if (!called(id)) {
        free(c);
        return EGL_NO_CONTEXT;
    }
    c->id = (uint32_t)id;
    c->next = d->contexts;
    d->contexts = c;
    return (EGLContext)(uintptr_t)c->id;
}

EGLBoolean eglDestroyContext(EGLDisplay display, EGLContext context) {
    WITH_LOCK;
    struct display* d = lookup(display, 1);
    if (!d)
        return EGL_FALSE;
    struct context** link = lookup_context(d, context);
    if (!link || !called(pg_gl(PG_DESTROY_CONTEXT, pg_ptr(context))))
        return EGL_FALSE;
    struct context* c = *link;
    *link = c->next;
    free(c);
    return EGL_TRUE;
}

EGLBoolean eglQueryContext(EGLDisplay display, EGLContext context, EGLint attribute, EGLint* value) {
    WITH_LOCK;
    struct display* d = lookup(display, 1);
    if (!d || !lookup_context(d, context))
        return EGL_FALSE;
    if (!value)
        return fail(EGL_BAD_PARAMETER);
    switch (attribute) {
    case EGL_CONTEXT_CLIENT_VERSION:
        *value = 2;
        break;
    case EGL_CONTEXT_CLIENT_TYPE:
        *value = EGL_OPENGL_ES_API;
        break;
    case EGL_CONFIG_ID:
        *value = 1;
        break;
    case EGL_RENDER_BUFFER:
        *value = pg_ptr(context) == current_context && current_draw ? EGL_BACK_BUFFER : EGL_NONE;
        break;
    default:
        return fail(EGL_BAD_ATTRIBUTE);
    }
    return EGL_TRUE;
}

static int fb_size(int* width, int* height) {
    int size[2];
    int result = pg_gl(PG_FB_SIZE, pg_ptr(size));
    if (result < 0)
        return result;
    *width = size[0];
    *height = size[1];
    return 0;
}

static EGLSurface make_surface(struct display* d, enum surface_kind kind, int width, int height,
                               struct pg_wayland* window) {
    struct surface* s = calloc(1, sizeof(*s));
    if (!s) {
        pg_wayland_destroy(window);
        return (fail(EGL_BAD_ALLOC), EGL_NO_SURFACE);
    }
    int id = pg_gl(PG_CREATE_SURFACE, (uint32_t)width, (uint32_t)height, (uint32_t)kind);
    if (!called(id)) {
        pg_wayland_destroy(window);
        free(s);
        return EGL_NO_SURFACE;
    }
    *s = (struct surface){
        .id = (uint32_t)id, .kind = kind, .width = width, .height = height, .window = window, .next = d->surfaces};
    d->surfaces = s;
    return (EGLSurface)(uintptr_t)s->id;
}

static EGLSurface make_gbm_surface(struct display* display, void* native) {
    struct pg_gbm_geometry size;
    if (!gbm.begin(display->native, native, &size))
        return (fail(EGL_BAD_NATIVE_WINDOW), EGL_NO_SURFACE);
    struct surface* surface = calloc(1, sizeof(*surface));
    if (!surface) {
        gbm.end(native);
        return (fail(EGL_BAD_ALLOC), EGL_NO_SURFACE);
    }
    uint32_t handles[PG_GBM_SLOTS];
    int id = pg_gl(PG_CREATE_GBM_SURFACE, size.width, size.height, (uint32_t)size.fd, pg_ptr(handles));
    if (!called(id)) {
        gbm.end(native);
        free(surface);
        return EGL_NO_SURFACE;
    }
    gbm.buffers(native, handles);
    *surface = (struct surface){.id = (uint32_t)id,
                                .kind = GBM,
                                .width = (int)size.width,
                                .height = (int)size.height,
                                .gbm_window = native,
                                .next = display->surfaces};
    display->surfaces = surface;
    return (EGLSurface)(uintptr_t)id;
}

EGLSurface eglCreateWindowSurface(EGLDisplay display, EGLConfig config, EGLNativeWindowType native,
                                  const EGLint* attribs) {
    WITH_LOCK;
    struct display* d = lookup(display, 1);
    if (!d)
        return EGL_NO_SURFACE;
    if (config != PG_CONFIG)
        return (fail(EGL_BAD_CONFIG), EGL_NO_SURFACE);
    for (const EGLint* a = attribs; a && *a != EGL_NONE; a += 2)
        if (a[0] != EGL_RENDER_BUFFER || a[1] != EGL_BACK_BUFFER)
            return (fail(EGL_BAD_ATTRIBUTE), EGL_NO_SURFACE);

    if (d->platform == PG_PLATFORM_GBM)
        return make_gbm_surface(d, (void*)(uintptr_t)native);
    int width, height;
    if (d->platform == PG_PLATFORM_WAYLAND) {
        struct pg_wayland* window = pg_wayland_create(d->native, (struct wl_egl_window*)(uintptr_t)native);
        if (!window)
            return (fail(EGL_BAD_NATIVE_WINDOW), EGL_NO_SURFACE);
        if (pg_wayland_size(window, &width, &height) < 0) {
            pg_wayland_destroy(window);
            return (fail(EGL_BAD_NATIVE_WINDOW), EGL_NO_SURFACE);
        }
        return make_surface(d, WAYLAND, width, height, window);
    }
    if (d->platform == PG_PLATFORM_SURFACELESS)
        return (fail(EGL_BAD_MATCH), EGL_NO_SURFACE);
    if (!called(fb_size(&width, &height)))
        return EGL_NO_SURFACE;
    return make_surface(d, FBDEV, width, height, NULL);
}

EGLSurface eglCreatePlatformWindowSurface(EGLDisplay d, EGLConfig c, void* native, const EGLAttrib* a) {
    return eglCreateWindowSurface(d, c, (EGLNativeWindowType)(uintptr_t)native, a);
}

PODISH_EXPORT EGLSurface eglCreatePlatformWindowSurfaceEXT(EGLDisplay d, EGLConfig c, void* native, const EGLint* a) {
    return eglCreateWindowSurface(d, c, (EGLNativeWindowType)(uintptr_t)native, a);
}

EGLSurface eglCreatePbufferSurface(EGLDisplay display, EGLConfig config, const EGLint* attribs) {
    WITH_LOCK;
    struct display* d = lookup(display, 1);
    if (!d)
        return EGL_NO_SURFACE;
    if (config != PG_CONFIG)
        return (fail(EGL_BAD_CONFIG), EGL_NO_SURFACE);
    int width = 1, height = 1;
    for (const EGLint* a = attribs; a && *a != EGL_NONE; a += 2) {
        switch (a[0]) {
        case EGL_WIDTH:
            width = a[1];
            break;
        case EGL_HEIGHT:
            height = a[1];
            break;
        case EGL_LARGEST_PBUFFER:
            break;
        case EGL_TEXTURE_FORMAT:
        case EGL_TEXTURE_TARGET:
            if (a[1] == EGL_NO_TEXTURE)
                break;
            return (fail(EGL_BAD_MATCH), EGL_NO_SURFACE);
        default:
            return (fail(EGL_BAD_ATTRIBUTE), EGL_NO_SURFACE);
        }
    }
    enum surface_kind kind = PBUFFER;
    const char* present = getenv("PODISH_EGL_FBDEV_PBUFFER");
    if (d->platform != PG_PLATFORM_WAYLAND && present && strcmp(present, "1") == 0)
        kind = FBDEV_PBUFFER;
    return make_surface(d, kind, width, height, NULL);
}

EGLBoolean eglDestroySurface(EGLDisplay display, EGLSurface handle) {
    WITH_LOCK;
    struct display* d = lookup(display, 1);
    if (!d)
        return EGL_FALSE;
    struct surface* s = lookup_surface(d, handle);
    if (!s || !called(pg_gl(PG_DESTROY_SURFACE, s->id)))
        return EGL_FALSE;
    s->deleted = 1;
    collect_surface(d, s);
    return EGL_TRUE;
}

static int refresh_size(struct surface* s) {
    int width, height;
    if (s->kind == PBUFFER || s->kind == FBDEV_PBUFFER || s->kind == GBM)
        return EGL_TRUE;
    if (s->kind == WAYLAND) {
        if (pg_wayland_size(s->window, &width, &height) < 0)
            return fail(EGL_BAD_NATIVE_WINDOW);
    } else if (!called(fb_size(&width, &height)))
        return EGL_FALSE;
    if (width == s->width && height == s->height)
        return EGL_TRUE;
    if (!called(pg_gl(PG_RESIZE_SURFACE, s->id, (uint32_t)width, (uint32_t)height)))
        return EGL_FALSE;
    s->width = width;
    s->height = height;
    return EGL_TRUE;
}

EGLBoolean eglQuerySurface(EGLDisplay display, EGLSurface handle, EGLint attribute, EGLint* value) {
    WITH_LOCK;
    struct display* d = lookup(display, 1);
    if (!d)
        return EGL_FALSE;
    struct surface* s = lookup_surface(d, handle);
    if (!s)
        return EGL_FALSE;
    if (!value)
        return fail(EGL_BAD_PARAMETER);
    switch (attribute) {
    case EGL_WIDTH:
    case EGL_HEIGHT:
        if (!refresh_size(s))
            return EGL_FALSE;
        *value = attribute == EGL_WIDTH ? s->width : s->height;
        break;
    case EGL_CONFIG_ID:
        *value = 1;
        break;
    case EGL_LARGEST_PBUFFER:
    case EGL_MIPMAP_TEXTURE:
    case EGL_MIPMAP_LEVEL:
        *value = EGL_FALSE;
        break;
    case EGL_TEXTURE_FORMAT:
    case EGL_TEXTURE_TARGET:
        *value = EGL_NO_TEXTURE;
        break;
    case EGL_RENDER_BUFFER:
        *value = EGL_BACK_BUFFER;
        break;
    case EGL_SWAP_BEHAVIOR:
        *value = EGL_BUFFER_DESTROYED;
        break;
    case EGL_HORIZONTAL_RESOLUTION:
    case EGL_VERTICAL_RESOLUTION:
    case EGL_PIXEL_ASPECT_RATIO:
        *value = EGL_UNKNOWN;
        break;
    default:
        return fail(EGL_BAD_ATTRIBUTE);
    }
    return EGL_TRUE;
}

EGLBoolean eglMakeCurrent(EGLDisplay display, EGLSurface draw_handle, EGLSurface read_handle, EGLContext context) {
    WITH_LOCK;
    struct display* d = lookup(display, context != EGL_NO_CONTEXT);
    if (!d)
        return EGL_FALSE;
    if (!context && (draw_handle || read_handle))
        return fail(EGL_BAD_MATCH);
    if (!!draw_handle != !!read_handle)
        return fail(EGL_BAD_MATCH);
    if (context && !(d == current && pg_ptr(context) == current_context) && !lookup_context(d, context))
        return EGL_FALSE;
    struct surface *draw = NULL, *read = NULL;
    if (draw_handle) {
        draw = lookup_surface(d, draw_handle);
        if (!draw)
            return EGL_FALSE;
    }
    if (read_handle) {
        read = draw_handle == read_handle ? draw : lookup_surface(d, read_handle);
        if (!read)
            return EGL_FALSE;
    }
    if (!called(pg_gl(PG_MAKE_CURRENT, pg_ptr(context), pg_ptr(draw_handle), pg_ptr(read_handle))))
        return EGL_FALSE;
    replace_current(d, pg_ptr(context), draw, read);
    return EGL_TRUE;
}

EGLContext eglGetCurrentContext(void) {
    return (EGLContext)(uintptr_t)current_context;
}
EGLDisplay eglGetCurrentDisplay(void) {
    return current;
}

EGLSurface eglGetCurrentSurface(EGLint which) {
    struct surface* s;
    if (which == EGL_DRAW)
        s = current_draw;
    else if (which == EGL_READ)
        s = current_read;
    else
        return (fail(EGL_BAD_PARAMETER), EGL_NO_SURFACE);
    return s ? (EGLSurface)(uintptr_t)s->id : EGL_NO_SURFACE;
}

EGLBoolean eglSwapBuffers(EGLDisplay display, EGLSurface handle) {
    WITH_LOCK;
    struct display* d = lookup(display, 1);
    if (!d)
        return EGL_FALSE;
    struct surface* s = lookup_surface(d, handle);
    if (!s)
        return EGL_FALSE;
    if (s != current_draw || d != current)
        return fail(EGL_BAD_SURFACE);
    if (!refresh_size(s))
        return EGL_FALSE;
    if (s->kind == GBM) {
        int handle = pg_gl(PG_SWAP, s->id);
        if (!called(handle))
            return EGL_FALSE;
        gbm.publish(s->gbm_window, (uint32_t)handle);
        return EGL_TRUE;
    }
    int result = s->kind == WAYLAND ? pg_wayland_present(s->window, s->id, s->width, s->height)
                                    : pg_gl(s->kind == PBUFFER ? PG_SWAP : PG_FRAME, s->id);
    return called(result);
}

EGLBoolean eglSwapInterval(EGLDisplay display, EGLint interval) {
    WITH_LOCK;
    (void)interval;
    if (!lookup(display, 1))
        return EGL_FALSE;
    return current == display && current_draw ? EGL_TRUE : fail(EGL_BAD_SURFACE);
}

EGLBoolean eglReleaseThread(void) {
    WITH_LOCK;
    if (current && !called(pg_gl(PG_RELEASE_THREAD)))
        return EGL_FALSE;
    replace_current(NULL, 0, NULL, NULL);
    error = EGL_SUCCESS;
    return EGL_TRUE;
}

EGLBoolean eglWaitClient(void) {
    if (current)
        glFinish();
    return EGL_TRUE;
}
EGLBoolean eglWaitGL(void) {
    return eglWaitClient();
}
EGLBoolean eglWaitNative(EGLint engine) {
    return engine == EGL_CORE_NATIVE_ENGINE ? EGL_TRUE : fail(EGL_BAD_PARAMETER);
}

EGLBoolean eglSurfaceAttrib(EGLDisplay display, EGLSurface surface, EGLint attribute, EGLint value) {
    WITH_LOCK;
    struct display* d = lookup(display, 1);
    if (!d || !lookup_surface(d, surface))
        return EGL_FALSE;
    if (attribute != EGL_SWAP_BEHAVIOR)
        return fail(EGL_BAD_ATTRIBUTE);
    return value == EGL_BUFFER_DESTROYED ? EGL_TRUE : fail(EGL_BAD_MATCH);
}

EGLSurface eglCreatePixmapSurface(EGLDisplay d, EGLConfig c, EGLNativePixmapType pixmap, const EGLint* a) {
    (void)d;
    (void)c;
    (void)pixmap;
    (void)a;
    return (fail(EGL_BAD_NATIVE_PIXMAP), EGL_NO_SURFACE);
}
EGLBoolean eglCopyBuffers(EGLDisplay d, EGLSurface s, EGLNativePixmapType target) {
    (void)d;
    (void)s;
    (void)target;
    return fail(EGL_BAD_NATIVE_PIXMAP);
}
EGLBoolean eglBindTexImage(EGLDisplay d, EGLSurface s, EGLint buffer) {
    (void)d;
    (void)s;
    (void)buffer;
    return fail(EGL_BAD_MATCH);
}
EGLBoolean eglReleaseTexImage(EGLDisplay d, EGLSurface s, EGLint buffer) {
    (void)d;
    (void)s;
    (void)buffer;
    return fail(EGL_BAD_MATCH);
}
EGLSurface eglCreatePbufferFromClientBuffer(EGLDisplay d, EGLenum type, EGLClientBuffer b, EGLConfig c,
                                            const EGLint* a) {
    (void)d;
    (void)type;
    (void)b;
    (void)c;
    (void)a;
    return (fail(EGL_BAD_PARAMETER), EGL_NO_SURFACE);
}

PODISH_EXPORT EGLImage eglCreateImageKHR(EGLDisplay display, EGLContext ctx, EGLenum target, EGLClientBuffer buffer,
                                         const EGLint* attrs) {
    WITH_LOCK;
    struct display* d = lookup(display, 1);
    if (!d)
        return EGL_NO_IMAGE;
    if (ctx || buffer || !d->managed_gbm || target != PG_LINUX_DMA_BUF)
        return (fail(EGL_BAD_PARAMETER), EGL_NO_IMAGE);

    int width = 0, height = 0, pitch = 0, format = 0, offset = -1, fd = -1;
    unsigned i = 0;
    if (!attrs)
        return (fail(EGL_BAD_PARAMETER), EGL_NO_IMAGE);
    for (; i < 16 && attrs[i * 2] != EGL_NONE; i++) {
        const EGLint value = attrs[i * 2 + 1];
        switch (attrs[i * 2]) {
        case EGL_WIDTH:
            width = value;
            break;
        case EGL_HEIGHT:
            height = value;
            break;
        case PG_DMA_FOURCC:
            format = value;
            break;
        case PG_DMA_PITCH:
            pitch = value;
            break;
        case PG_DMA_OFFSET:
            offset = value;
            break;
        case PG_DMA_FD:
            fd = value;
            break;
        case PG_IMAGE_PRESERVED:
            if (value == EGL_TRUE || value == EGL_FALSE)
                break;
            return (fail(EGL_BAD_ATTRIBUTE), EGL_NO_IMAGE);
        default:
            return (fail(EGL_BAD_ATTRIBUTE), EGL_NO_IMAGE);
        }
    }
    if (i == 16 || fd < 0 || offset != 0 || pitch <= 0 || width <= 0 || height <= 0 ||
        (format != PG_GBM_XRGB8888 && format != PG_GBM_ARGB8888))
        return (fail(EGL_BAD_PARAMETER), EGL_NO_IMAGE);

    struct image* image = malloc(sizeof(*image));
    if (!image)
        return (fail(EGL_BAD_ALLOC), EGL_NO_IMAGE);
    int id = pg_gl(PG_IMPORT_IMAGE, fd, width, height, pitch, format);
    if (!called(id)) {
        free(image);
        return EGL_NO_IMAGE;
    }
    *image = (struct image){(uint32_t)id, d->images};
    d->images = image;
    return (EGLImage)(uintptr_t)id;
}

PODISH_EXPORT EGLBoolean eglDestroyImageKHR(EGLDisplay display, EGLImage image) {
    WITH_LOCK;
    struct display* d = lookup(display, 1);
    if (!d)
        return EGL_FALSE;
    struct image** link = &d->images;
    while (*link && (*link)->id != pg_ptr(image))
        link = &(*link)->next;
    if (!*link)
        return fail(EGL_BAD_PARAMETER);
    if (!called(pg_gl(PG_DESTROY_IMAGE, pg_ptr(image))))
        return EGL_FALSE;
    struct image* found = *link;
    *link = found->next;
    free(found);
    return EGL_TRUE;
}

PODISH_EXPORT EGLBoolean eglQueryDmaBufFormatsEXT(EGLDisplay display, EGLint max, EGLint* formats, EGLint* count) {
    WITH_LOCK;
    struct display* d = lookup(display, 1);
    if (!d)
        return EGL_FALSE;
    if (!d->managed_gbm || !count || max < 0 || (max && !formats))
        return fail(EGL_BAD_PARAMETER);
    if (max > 0)
        formats[0] = PG_GBM_XRGB8888;
    if (max > 1)
        formats[1] = PG_GBM_ARGB8888;
    *count = max == 1 ? 1 : 2;
    return EGL_TRUE;
}

PODISH_EXPORT EGLBoolean eglQueryDmaBufModifiersEXT(EGLDisplay display, EGLint format, EGLint max, uint64_t* modifiers,
                                                    EGLBoolean* external, EGLint* count) {
    WITH_LOCK;
    struct display* d = lookup(display, 1);
    if (!d)
        return EGL_FALSE;
    if (!d->managed_gbm || !count || max < 0 || (max && !modifiers) ||
        (format != PG_GBM_XRGB8888 && format != PG_GBM_ARGB8888))
        return fail(EGL_BAD_PARAMETER);
    // The GPU layout is opaque to the guest and uses implicit DRM modifiers.
    if (max > 0) {
        modifiers[0] = 0x00ffffffffffffffull;
        if (external)
            external[0] = EGL_FALSE;
    }
    *count = 1;
    return EGL_TRUE;
}

EGLImage eglCreateImage(EGLDisplay display, EGLContext ctx, EGLenum target, EGLClientBuffer buffer,
                        const EGLAttrib* attrs) {
    _Static_assert(sizeof(EGLAttrib) == sizeof(EGLint), "IA-32 EGL image attributes");
    return eglCreateImageKHR(display, ctx, target, buffer, (const EGLint*)attrs);
}

EGLBoolean eglDestroyImage(EGLDisplay display, EGLImage image) {
    return eglDestroyImageKHR(display, image);
}

__eglMustCastToProperFunctionPointerType eglGetProcAddress(const char* name) {
    if (!name)
        return NULL;
#define PROC(f)                                                                                                        \
    if (strcmp(name, #f) == 0)                                                                                         \
    return (__eglMustCastToProperFunctionPointerType)f
    PROC(eglGetPlatformDisplayEXT);
    PROC(eglCreatePlatformWindowSurfaceEXT);
    PROC(eglQueryDevicesEXT);
    PROC(eglQueryDeviceStringEXT);
    PROC(eglQueryDeviceAttribEXT);
    PROC(eglQueryDisplayAttribEXT);
    PROC(eglCreateImageKHR);
    PROC(eglDestroyImageKHR);
    PROC(eglCreateImage);
    PROC(eglDestroyImage);
    PROC(eglQueryDmaBufFormatsEXT);
    PROC(eglQueryDmaBufModifiersEXT);
    PROC(glEGLImageTargetRenderbufferStorageOES);
    PROC(glEGLImageTargetTexture2DOES);
#undef PROC
    if (strncmp(name, "gl", 2) == 0)
        return (__eglMustCastToProperFunctionPointerType)pg_get_gl_proc(name);
    return NULL;
}
