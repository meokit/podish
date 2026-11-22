#include <EGL/egl.h>
#include <GLES3/gl3.h>
#include <fcntl.h>
#include <gbm.h>
#include <poll.h>
#include <stdbool.h>
#include <stdio.h>
#include <stdlib.h>
#include <unistd.h>
#include <xf86drm.h>
#include <xf86drmMode.h>

static void require(bool condition, const char* what) {
    if (condition)
        return;
    fprintf(stderr, "%s (EGL error 0x%x)\n", what, eglGetError());
    exit(1);
}

static unsigned flips;

static void flipped(int fd, unsigned sequence, unsigned sec, unsigned usec, void* data) {
    (void)fd;
    (void)sequence;
    (void)sec;
    (void)usec;
    require((uintptr_t)data == flips + 1, "page flip cookie");
    ++flips;
}

static void require_active(int fd, uint32_t crtc, uint32_t expected) {
    drmModeCrtc* active = drmModeGetCrtc(fd, crtc);
    require(active && active->buffer_id == expected, "active framebuffer");
    drmModeFreeCrtc(active);
}

static void require_fresh_back_buffer(GLsizei width, GLsizei height) {
    size_t size = (size_t)width * height * 4;
    GLubyte* pixels = malloc(size);
    require(pixels != NULL, "allocate fresh GBM pixel check");
    glReadPixels(0, 0, width, height, GL_RGBA, GL_UNSIGNED_BYTE, pixels);
    require(glGetError() == GL_NO_ERROR, "read fresh GBM pbuffer");
    for (size_t i = 0; i < size; i += 4)
        require(!(pixels[i] | pixels[i + 1] | pixels[i + 2]), "fresh GBM pbuffer is black before rendering");
    free(pixels);
}

struct fb_entry {
    uint32_t handle, id;
};

int main(void) {
    int fd = open("/dev/dri/card0", O_RDWR | O_CLOEXEC);
    require(fd >= 0, "DRM primary node");
    drmModeRes* resources = drmModeGetResources(fd);
    require(resources && resources->count_crtcs == 1 && resources->count_connectors == 1, "DRM output");
    uint32_t crtc = resources->crtcs[0], connector = resources->connectors[0];
    drmModeConnector* output = drmModeGetConnector(fd, connector);
    require(output && output->count_modes, "DRM mode");
    drmModeModeInfo mode = output->modes[0];
    struct gbm_device* device = gbm_create_device(fd);
    const uint32_t usage = GBM_BO_USE_RENDERING | GBM_BO_USE_SCANOUT;
    require(device && gbm_device_is_format_supported(device, GBM_FORMAT_ARGB8888, usage), "GPU BO support");

    struct gbm_surface* gbm = gbm_surface_create(device, mode.hdisplay, mode.vdisplay, GBM_FORMAT_ARGB8888, usage);
    require(gbm != NULL, "GBM surface");
    EGLDisplay display = eglGetPlatformDisplay(0x31D7 /* EGL_PLATFORM_GBM_KHR */, device, NULL);
    require(eglInitialize(display, NULL, NULL), "EGL GBM display");
    EGLConfig config;
    EGLint count;
    bool es3 = getenv("PODISH_TEST_GLES3") != NULL;
    const EGLint attrs[] = {EGL_SURFACE_TYPE, EGL_WINDOW_BIT, EGL_RENDERABLE_TYPE,
                            es3 ? 0x0040 /* EGL_OPENGL_ES3_BIT_KHR */ : EGL_OPENGL_ES2_BIT, EGL_NONE};
    require(eglChooseConfig(display, attrs, &config, 1, &count) && count == 1, "EGL GBM config");
    const EGLint version[] = {EGL_CONTEXT_CLIENT_VERSION, es3 ? 3 : 2, EGL_NONE};
    EGLContext context = eglCreateContext(display, config, EGL_NO_CONTEXT, version);
    EGLSurface surface = eglCreatePlatformWindowSurface(display, config, gbm, NULL);
    require(eglMakeCurrent(display, surface, surface, context), "make GBM window current");
    if (es3) {
        GLint major = 0, minor = -1;
        glGetIntegerv(GL_MAJOR_VERSION, &major);
        glGetIntegerv(GL_MINOR_VERSION, &minor);
        require(major == 3 && minor == 0, "GLES 3.0 GBM context");
    }
    require(gbm_surface_lock_front_buffer(gbm) == NULL, "front BO only becomes available after EGL swap");

    struct fb_entry entries[3] = {0};
    uint32_t active = 0;
    for (unsigned frame = 0; frame < 9; ++frame) {
        const GLfloat color[] = {(frame & 1) != 0, (frame & 2) != 0, (frame & 4) != 0, 1};
        if (es3) {
            glClearBufferfv(GL_COLOR, 0, color);
        } else {
            glClearColor(color[0], color[1], color[2], color[3]);
            glClear(GL_COLOR_BUFFER_BIT);
        }
        require(glGetError() == GL_NO_ERROR && eglSwapBuffers(display, surface), "render and queue GBM BO");
        require_active(fd, crtc, active);

        struct gbm_bo* front = gbm_surface_lock_front_buffer(gbm);
        require(front != NULL, "lock front BO");
        uint32_t handle = gbm_bo_get_handle(front).u32;
        unsigned i = 0;
        while (i < 3 && entries[i].handle && entries[i].handle != handle)
            ++i;
        require(i < 3, "reuse three-slot GBM pool");
        struct fb_entry* fb = &entries[i];
        if (!fb->handle) {
            require(drmModeAddFB(fd, mode.hdisplay, mode.vdisplay, 24, 32, gbm_bo_get_stride(front), handle, &fb->id) ==
                        0,
                    "register GPU framebuffer");
            fb->handle = handle;
        }
        require(fb->id != active, "a BO held by scanout is not available to EGL");
        if (frame == 0)
            require(drmModeSetCrtc(fd, crtc, fb->id, 0, 0, &connector, 1, &mode) == 0, "initial GPU modeset");
        else {
            require(drmModePageFlip(fd, crtc, fb->id, DRM_MODE_PAGE_FLIP_EVENT, (void*)(uintptr_t)frame) == 0,
                    "submit GPU page flip");
            struct pollfd pollfd = {.fd = fd, .events = POLLIN};
            drmEventContext ev = {.version = 2, .page_flip_handler = flipped};
            require(poll(&pollfd, 1, 1500) == 1 && drmHandleEvent(fd, &ev) == 0 && flips == frame,
                    "GPU page-flip completion event");
        }
        active = fb->id;
        require_active(fd, crtc, active);
        // The BO remains pinned by active scanout even when the GBM lock is released.
        gbm_surface_release_buffer(gbm, front);
    }

    require(drmModeSetCrtc(fd, crtc, 0, 0, 0, NULL, 0, NULL) == 0, "disable scanout");
    require(eglMakeCurrent(display, EGL_NO_SURFACE, EGL_NO_SURFACE, EGL_NO_CONTEXT) &&
                eglDestroySurface(display, surface),
            "release GBM EGL surface");
    for (unsigned i = 0; i < 3; ++i)
        if (entries[i].id)
            require(drmModeRmFB(fd, entries[i].id) == 0, "release framebuffer reference");
    gbm_surface_destroy(gbm);

    // SDL KMSDRM can recreate its surface in the middle of a swap. The guest
    // has not drawn to the replacement yet, and it must not expose old pixels.
    gbm = gbm_surface_create(device, mode.hdisplay, mode.vdisplay, GBM_FORMAT_ARGB8888, usage);
    require(gbm != NULL, "recreate GBM surface");
    surface = eglCreatePlatformWindowSurface(display, config, gbm, NULL);
    require(surface != EGL_NO_SURFACE && eglMakeCurrent(display, surface, surface, context),
            "bind replacement GBM surface");
    require_fresh_back_buffer(mode.hdisplay, mode.vdisplay);
    require(eglMakeCurrent(display, EGL_NO_SURFACE, EGL_NO_SURFACE, EGL_NO_CONTEXT) &&
                eglDestroySurface(display, surface),
            "release replacement GBM surface");
    require(eglDestroyContext(display, context) && eglTerminate(display), "release EGL context");
    gbm_surface_destroy(gbm);
    gbm_device_destroy(device);
    drmModeFreeConnector(output);
    drmModeFreeResources(resources);
    close(fd);
    printf("KMS GBM EGL smoke passed (modeset, %u page flips, pool reuse and teardown)\n", flips);
    return 0;
}
