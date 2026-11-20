#define _GNU_SOURCE
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include <sys/mman.h>
#include <unistd.h>
#include <wayland-client.h>
#include "xdg-shell-client.h"
#ifdef PROBE_EGL
#include <EGL/egl.h>
#include <GLES2/gl2.h>
#include <wayland-egl.h>
#endif

static struct wl_compositor* compositor;
static struct wl_shm* shm;
static struct xdg_wm_base* shell;
static bool configured, frame_done;
#ifndef PROBE_EGL
static bool released;
#endif
enum { width = 160, height = 96, stride = width * 4, size = height * stride };

static void registry_global(void* data, struct wl_registry* registry, uint32_t name,
                            const char* interface, uint32_t version) {
    (void)data;
    (void)version;
    if (!strcmp(interface, wl_compositor_interface.name))
        compositor = wl_registry_bind(registry, name, &wl_compositor_interface, 1);
    else if (!strcmp(interface, wl_shm_interface.name))
        shm = wl_registry_bind(registry, name, &wl_shm_interface, 1);
    else if (!strcmp(interface, xdg_wm_base_interface.name))
        shell = wl_registry_bind(registry, name, &xdg_wm_base_interface, 1);
}

static void registry_remove(void* data, struct wl_registry* registry, uint32_t name) {
    (void)data;
    (void)registry;
    (void)name;
}

static void ping(void* data, struct xdg_wm_base* base, uint32_t serial) {
    (void)data;
    xdg_wm_base_pong(base, serial);
}

static void configure(void* data, struct xdg_surface* surface, uint32_t serial) {
    (void)data;
    xdg_surface_ack_configure(surface, serial);
    configured = true;
}

static void toplevel_configure(void* data, struct xdg_toplevel* top, int32_t width,
                               int32_t height, struct wl_array* states) {
    (void)data;
    (void)top;
    (void)width;
    (void)height;
    (void)states;
}

static void toplevel_close(void* data, struct xdg_toplevel* top) {
    (void)data;
    (void)top;
}

static void done(void* data, struct wl_callback* callback, uint32_t time) {
    (void)data;
    (void)time;
    wl_callback_destroy(callback);
    frame_done = true;
}

#ifndef PROBE_EGL
static void release(void* data, struct wl_buffer* buffer) {
    (void)data;
    (void)buffer;
    released = true;
}
#endif

static bool wait_frame(struct wl_display* display) {
    while (!frame_done
#ifndef PROBE_EGL
           || !released
#endif
    ) {
        if (wl_display_dispatch(display) < 0) {
            fprintf(stderr, "Wayland compositor disconnected before frame completion\n");
            return false;
        }
    }
    return true;
}

#ifdef PROBE_EGL
static bool draw_egl(struct wl_display* display, struct wl_surface* surface) {
    EGLDisplay egl = eglGetDisplay((EGLNativeDisplayType)display);
    EGLint config_attrs[] = { EGL_SURFACE_TYPE, EGL_WINDOW_BIT, EGL_RENDERABLE_TYPE, EGL_OPENGL_ES2_BIT, EGL_NONE };
    EGLint context_attrs[] = { EGL_CONTEXT_CLIENT_VERSION, 2, EGL_NONE };
    EGLConfig config;
    EGLint count;
    if (!eglInitialize(egl, NULL, NULL) || !eglChooseConfig(egl, config_attrs, &config, 1, &count) || count != 1)
        goto fail;
    struct wl_egl_window* window = wl_egl_window_create(surface, width, height);
    if (!window)
        goto fail;
    EGLSurface target = eglCreateWindowSurface(egl, config, (EGLNativeWindowType)window, NULL);
    EGLContext context = eglCreateContext(egl, config, EGL_NO_CONTEXT, context_attrs);
    if (target == EGL_NO_SURFACE || context == EGL_NO_CONTEXT || !eglMakeCurrent(egl, target, target, context))
        goto fail;
    glClearColor(0.0f, 1.0f, 0.0f, 1.0f);
    glClear(GL_COLOR_BUFFER_BIT);
    uint8_t pixel[4];
    glReadPixels(0, 0, 1, 1, GL_RGBA, GL_UNSIGNED_BYTE, pixel);
    if (glGetError() != GL_NO_ERROR || pixel[0] || pixel[1] != 255 || pixel[2] || pixel[3] != 255) {
        fprintf(stderr, "wrong Wayland EGL pixel\n");
        return false;
    }
    if (!eglSwapBuffers(egl, target))
        goto fail;
    if (!wait_frame(display))
        return false;
    usleep(250000);
    eglMakeCurrent(egl, EGL_NO_SURFACE, EGL_NO_SURFACE, EGL_NO_CONTEXT);
    eglDestroyContext(egl, context);
    eglDestroySurface(egl, target);
    eglTerminate(egl);
    wl_egl_window_destroy(window);
    return true;
fail:
    fprintf(stderr, "Wayland EGL failed (0x%x)\n", eglGetError());
    return false;
}
#endif

int main(void) {
    static const struct wl_registry_listener registry_listener = { registry_global, registry_remove };
    static const struct xdg_wm_base_listener shell_listener = { ping };
    static const struct xdg_surface_listener surface_listener = { configure };
    static const struct xdg_toplevel_listener top_listener = {
        .configure = toplevel_configure, .close = toplevel_close
    };
    static const struct wl_callback_listener frame_listener = { done };

    struct wl_display* display = wl_display_connect(NULL);
    if (!display) {
        perror("connect to guest Wayland compositor");
        return 1;
    }
    struct wl_registry* registry = wl_display_get_registry(display);
    wl_registry_add_listener(registry, &registry_listener, NULL);
    if (wl_display_roundtrip(display) < 0 || !compositor || !shm || !shell) {
        fprintf(stderr, "missing Wayland compositor, shm or xdg-shell\n");
        return 1;
    }

    xdg_wm_base_add_listener(shell, &shell_listener, NULL);
    struct wl_surface* surface = wl_compositor_create_surface(compositor);
    struct xdg_surface* xdg_surface = xdg_wm_base_get_xdg_surface(shell, surface);
    xdg_surface_add_listener(xdg_surface, &surface_listener, NULL);
    struct xdg_toplevel* top = xdg_surface_get_toplevel(xdg_surface);
    xdg_toplevel_add_listener(top, &top_listener, NULL);
    xdg_toplevel_set_title(top, "Podish Wayland smoke");
    wl_surface_commit(surface);
    while (!configured)
        if (wl_display_dispatch(display) < 0)
            return 1;

    wl_callback_add_listener(wl_surface_frame(surface), &frame_listener, NULL);
#ifdef PROBE_EGL
    if (!draw_egl(display, surface))
        return 1;
#else
    static const struct wl_buffer_listener buffer_listener = { release };
    int fd = memfd_create("podish-media-smoke", MFD_CLOEXEC);
    if (fd < 0 || ftruncate(fd, size) < 0) {
        perror("allocate Wayland shm");
        return 1;
    }
    uint32_t* pixels = mmap(NULL, size, PROT_READ | PROT_WRITE, MAP_SHARED, fd, 0);
    if (pixels == MAP_FAILED) {
        perror("map Wayland shm");
        return 1;
    }
    for (int y = 0; y < height; ++y)
        for (int x = 0; x < width; ++x)
            pixels[y * width + x] = x < width / 2 ? 0xc0248c30 : 0xc0b37318;
    struct wl_shm_pool* pool = wl_shm_create_pool(shm, fd, size);
    close(fd);
    struct wl_buffer* buffer = wl_shm_pool_create_buffer(pool, 0, width, height, stride, WL_SHM_FORMAT_ARGB8888);
    wl_shm_pool_destroy(pool);
    wl_buffer_add_listener(buffer, &buffer_listener, NULL);
    wl_surface_attach(surface, buffer, 0, 0);
    wl_surface_damage(surface, 0, 0, width, height);
    wl_surface_commit(surface);
    if (!wait_frame(display))
        return 1;
    usleep(250000);
    wl_buffer_destroy(buffer);
    munmap(pixels, size);
#endif
    xdg_toplevel_destroy(top);
    xdg_surface_destroy(xdg_surface);
    wl_surface_destroy(surface);
    xdg_wm_base_destroy(shell);
    wl_shm_destroy(shm);
    wl_compositor_destroy(compositor);
    wl_registry_destroy(registry);
    wl_display_flush(display);
    wl_display_disconnect(display);
#ifdef PROBE_EGL
    puts("Wayland EGL smoke passed");
#else
    puts("Wayland shm smoke passed");
#endif
    return 0;
}
