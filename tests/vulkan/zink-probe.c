#include <EGL/egl.h>
#include <EGL/eglext.h>
#include <GLES2/gl2.h>
#include <stdbool.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <wayland-client.h>
#include <wayland-egl.h>

#include "xdg-shell-client.h"

struct window {
    struct wl_compositor* compositor;
    struct xdg_wm_base* shell;
    int width, height;
    bool configured, closed, frame_done;
};

struct gl_api {
    PFNGLGETSTRINGPROC get_string;
    PFNGLGETERRORPROC get_error;
    PFNGLDISABLEPROC disable;
    PFNGLENABLEPROC enable;
    PFNGLCLEARCOLORPROC clear_color;
    PFNGLCLEARPROC clear;
    PFNGLFINISHPROC finish;
    PFNGLSCISSORPROC scissor;
    PFNGLREADPIXELSPROC read_pixels;
};

static void require(bool ok, const char* message) {
    if (!ok) {
        fprintf(stderr, "%s (EGL error %#x)\n", message, eglGetError());
        exit(1);
    }
}

static void on_global(void* data, struct wl_registry* registry, uint32_t name, const char* interface,
                      uint32_t version) {
    struct window* window = data;
    (void)version;
    if (strcmp(interface, wl_compositor_interface.name) == 0)
        window->compositor = wl_registry_bind(registry, name, &wl_compositor_interface, 1);
    else if (strcmp(interface, xdg_wm_base_interface.name) == 0)
        window->shell = wl_registry_bind(registry, name, &xdg_wm_base_interface, 1);
}

static void on_global_remove(void* data, struct wl_registry* registry, uint32_t name) {
    (void)data;
    (void)registry;
    (void)name;
}

static void on_ping(void* data, struct xdg_wm_base* shell, uint32_t serial) {
    (void)data;
    xdg_wm_base_pong(shell, serial);
}

static void on_configure(void* data, struct xdg_surface* surface, uint32_t serial) {
    struct window* window = data;
    xdg_surface_ack_configure(surface, serial);
    window->configured = true;
}

static void on_toplevel_configure(void* data, struct xdg_toplevel* toplevel, int32_t width, int32_t height,
                                  struct wl_array* states) {
    struct window* window = data;
    (void)toplevel;
    (void)states;
    if (width > 0 && height > 1) {
        window->width = width;
        window->height = height;
    }
}

static void on_close(void* data, struct xdg_toplevel* toplevel) {
    struct window* window = data;
    (void)toplevel;
    window->closed = true;
}

static void on_frame(void* data, struct wl_callback* callback, uint32_t time) {
    struct window* window = data;
    (void)callback;
    (void)time;
    window->frame_done = true;
}

static struct gl_api resolve_gl(void) {
    struct gl_api gl = {
        .get_string = (PFNGLGETSTRINGPROC)eglGetProcAddress("glGetString"),
        .get_error = (PFNGLGETERRORPROC)eglGetProcAddress("glGetError"),
        .disable = (PFNGLDISABLEPROC)eglGetProcAddress("glDisable"),
        .enable = (PFNGLENABLEPROC)eglGetProcAddress("glEnable"),
        .clear_color = (PFNGLCLEARCOLORPROC)eglGetProcAddress("glClearColor"),
        .clear = (PFNGLCLEARPROC)eglGetProcAddress("glClear"),
        .finish = (PFNGLFINISHPROC)eglGetProcAddress("glFinish"),
        .scissor = (PFNGLSCISSORPROC)eglGetProcAddress("glScissor"),
        .read_pixels = (PFNGLREADPIXELSPROC)eglGetProcAddress("glReadPixels"),
    };
    require(gl.get_string && gl.get_error && gl.disable && gl.enable && gl.clear_color && gl.clear && gl.finish &&
                gl.scissor && gl.read_pixels,
            "EGL did not expose the requested GL entry points");
    return gl;
}

static void render_frame(const struct gl_api* gl, int width, int height, unsigned frame) {
    static const GLubyte colors[][4] = {{255, 0, 0, 255}, {0, 255, 0, 255}, {0, 0, 255, 255}, {255, 255, 0, 255}};
    const GLubyte* lower = colors[frame % 4];
    const GLubyte* upper = colors[(frame + 1) % 4];
    gl->disable(GL_SCISSOR_TEST);
    gl->clear_color(lower[0] / 255.0f, lower[1] / 255.0f, lower[2] / 255.0f, 1);
    gl->clear(GL_COLOR_BUFFER_BIT);
    gl->enable(GL_SCISSOR_TEST);
    gl->scissor(0, height / 2, width, height - height / 2);
    gl->clear_color(upper[0] / 255.0f, upper[1] / 255.0f, upper[2] / 255.0f, 1);
    gl->clear(GL_COLOR_BUFFER_BIT);

    gl->finish();
    GLubyte bottom[4], top[4];
    gl->read_pixels(width / 2, 0, 1, 1, GL_RGBA, GL_UNSIGNED_BYTE, bottom);
    gl->read_pixels(width / 2, height - 1, 1, 1, GL_RGBA, GL_UNSIGNED_BYTE, top);
    require(gl->get_error() == GL_NO_ERROR, "GL draw or readback failed");
    if (memcmp(bottom, lower, 3) != 0 || memcmp(top, upper, 3) != 0) {
        fprintf(stderr, "Frame %u (%dx%d) read back bottom RGB %u,%u,%u and top RGB %u,%u,%u\n", frame, width, height,
                bottom[0], bottom[1], bottom[2], top[0], top[1], top[2]);
        require(false, "GL readback color mismatch");
    }
}

int main(int argc, char** argv) {
    const bool gles = argc == 2 && strcmp(argv[1], "--gles") == 0;
    const bool virgl = argc == 2 && strcmp(argv[1], "--virgl") == 0;
    if (argc != 1 && !gles && !virgl) {
        fprintf(stderr, "Usage: %s [--gles|--virgl]\n", argv[0]);
        return 2;
    }

    struct window window = {.width = 192, .height = 128};
    struct wl_display* display = wl_display_connect(NULL);
    require(display != NULL, "could not connect to the Wayland compositor");

    static const struct wl_registry_listener registry_listener = {on_global, on_global_remove};
    struct wl_registry* registry = wl_display_get_registry(display);
    wl_registry_add_listener(registry, &registry_listener, &window);
    require(wl_display_roundtrip(display) >= 0 && window.compositor && window.shell,
            "Wayland compositor has no xdg-shell");

    static const struct xdg_wm_base_listener shell_listener = {.ping = on_ping};
    static const struct xdg_surface_listener surface_listener = {.configure = on_configure};
    static const struct xdg_toplevel_listener toplevel_listener = {
        .configure = on_toplevel_configure,
        .close = on_close,
    };
    xdg_wm_base_add_listener(window.shell, &shell_listener, &window);
    struct wl_surface* surface = wl_compositor_create_surface(window.compositor);
    struct xdg_surface* xdg_surface = xdg_wm_base_get_xdg_surface(window.shell, surface);
    xdg_surface_add_listener(xdg_surface, &surface_listener, &window);
    struct xdg_toplevel* toplevel = xdg_surface_get_toplevel(xdg_surface);
    xdg_toplevel_add_listener(toplevel, &toplevel_listener, &window);
    xdg_toplevel_set_title(toplevel, "Podish OpenGL probe");
    wl_surface_commit(surface);
    while (!window.configured && !window.closed)
        require(wl_display_dispatch(display) >= 0, "xdg-shell configure failed");
    require(!window.closed, "Wayland compositor closed the probe window");

    struct wl_egl_window* native_window = wl_egl_window_create(surface, window.width, window.height);
    require(native_window != NULL, "could not create a Wayland EGL window");
    EGLDisplay egl = eglGetPlatformDisplay(EGL_PLATFORM_WAYLAND_KHR, display, NULL);
    require(egl != EGL_NO_DISPLAY && eglInitialize(egl, NULL, NULL), "could not initialize Wayland EGL");
    require(eglBindAPI(gles ? EGL_OPENGL_ES_API : EGL_OPENGL_API), "EGL could not bind the requested GL API");
    const EGLint attribs[] = {EGL_SURFACE_TYPE,
                              EGL_WINDOW_BIT,
                              EGL_RENDERABLE_TYPE,
                              gles ? EGL_OPENGL_ES2_BIT : EGL_OPENGL_BIT,
                              EGL_RED_SIZE,
                              8,
                              EGL_GREEN_SIZE,
                              8,
                              EGL_BLUE_SIZE,
                              8,
                              EGL_NONE};
    const EGLint es_context[] = {EGL_CONTEXT_CLIENT_VERSION, 2, EGL_NONE};
    EGLint count;
    EGLConfig config;
    require(eglChooseConfig(egl, attribs, &config, 1, &count) && count == 1,
            "no window config for the requested GL API");
    EGLContext context = eglCreateContext(egl, config, EGL_NO_CONTEXT, gles ? es_context : NULL);
    require(context != EGL_NO_CONTEXT, "could not create a GL context");
    EGLSurface egl_surface = eglCreatePlatformWindowSurface(egl, config, native_window, NULL);
    require(egl_surface != EGL_NO_SURFACE && eglMakeCurrent(egl, egl_surface, egl_surface, context),
            "could not bind the GL window");
    const struct gl_api gl = resolve_gl();
    const char* renderer = (const char*)gl.get_string(GL_RENDERER);
    const char* version = (const char*)gl.get_string(GL_VERSION);
    const char* api = gles ? "OpenGL ES" : "OpenGL";
    printf("%s renderer %s / version %s\n", api, renderer ? renderer : "unknown", version ? version : "unknown");
    const char* vulkan = getenv("PODISH_VULKAN");
    bool expect_zink = !gles || (vulkan && strcmp(vulkan, "1") == 0);
    if (virgl) {
        PFNEGLGETDISPLAYDRIVERNAMEPROC get_driver_name =
            (PFNEGLGETDISPLAYDRIVERNAMEPROC)eglGetProcAddress("eglGetDisplayDriverName");
        const char* driver = get_driver_name ? get_driver_name(egl) : NULL;
        printf("EGL driver %s\n", driver ? driver : "unknown");
        require(driver && strcmp(driver, "virtio_gpu") == 0 && renderer && strstr(renderer, "zink") &&
                    !strstr(renderer, "Venus") && !strstr(renderer, "llvmpipe"),
                "GL is not using VirGL through host Zink");
    } else if (expect_zink) {
        require(renderer && strstr(renderer, "zink") && strstr(renderer, "Venus") && !strstr(renderer, "llvmpipe"),
                "GL is not using Zink over a Venus GPU");
    } else {
        require(renderer && strstr(renderer, "ANGLE") && strstr(renderer, "Metal"),
                "OpenGL ES is not using ANGLE Metal");
    }

    const char es_prefix[] = "OpenGL ES ";
    if (gles) {
        require(version && strncmp(version, es_prefix, sizeof(es_prefix) - 1) == 0, "expected an OpenGL ES context");
        version += sizeof(es_prefix) - 1;
    }
    int major, minor;
    require(version && sscanf(version, "%d.%d", &major, &minor) == 2 &&
                (major > 2 || (major == 2 && minor >= (gles ? 0 : 1))),
            "GL context version is too old");

    static const struct wl_callback_listener frame_listener = {on_frame};
    const unsigned frame_count = 12;
    for (unsigned frame = 0; frame < frame_count; frame++) {
        wl_egl_window_resize(native_window, window.width, window.height, 0, 0);
        render_frame(&gl, window.width, window.height, frame);
        window.frame_done = false;
        struct wl_callback* callback = wl_surface_frame(surface);
        wl_callback_add_listener(callback, &frame_listener, &window);
        require(eglSwapBuffers(egl, egl_surface), "could not present the GL window");
        while (!window.frame_done && !window.closed)
            require(wl_display_dispatch(display) >= 0, "Wayland frame callback failed");
        wl_callback_destroy(callback);
        require(!window.closed, "Wayland compositor closed the probe window");
    }
    printf("%s rendered and presented %u changing %s frames over Wayland\n",
           virgl         ? "VirGL over host Zink"
           : expect_zink ? "Zink"
                         : "ANGLE Metal",
           frame_count, api);

    eglMakeCurrent(egl, EGL_NO_SURFACE, EGL_NO_SURFACE, EGL_NO_CONTEXT);
    eglDestroySurface(egl, egl_surface);
    eglDestroyContext(egl, context);
    eglTerminate(egl);
    wl_egl_window_destroy(native_window);
    xdg_toplevel_destroy(toplevel);
    xdg_surface_destroy(xdg_surface);
    wl_surface_destroy(surface);
    xdg_wm_base_destroy(window.shell);
    wl_compositor_destroy(window.compositor);
    wl_registry_destroy(registry);
    wl_display_disconnect(display);
    return 0;
}
