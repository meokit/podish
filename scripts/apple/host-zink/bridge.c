#include "podish_host_zink_bridge.h"

#include <EGL/egl.h>
#include <EGL/eglext.h>
#include <stdlib.h>
#include <vulkan/vulkan.h>

struct pipe_resource;
void podish_mesa_zink_set_vk_get_instance_proc_addr(PFN_vkGetInstanceProcAddr get_proc);
void* podish_mesa_zink_export_resource(struct pipe_resource* resource);
struct pipe_resource* podish_mesa_current_texture_resource(uint32_t texture);

struct podish_zink_context {
    EGLContext context;
    EGLSurface surface;
};

static EGLDisplay display;
static EGLConfig config;
static struct podish_zink_context* primary;

int podish_zink_initialize(void* vk_get_instance_proc_addr) {
    if (!vk_get_instance_proc_addr)
        return -1;
    if (display != EGL_NO_DISPLAY)
        return 0;

    podish_mesa_zink_set_vk_get_instance_proc_addr((PFN_vkGetInstanceProcAddr)vk_get_instance_proc_addr);
    const EGLAttrib attrs[] = {EGL_NONE};
    EGLDisplay candidate = eglGetPlatformDisplay(EGL_PLATFORM_SURFACELESS_MESA, EGL_DEFAULT_DISPLAY, attrs);
    if (candidate == EGL_NO_DISPLAY || !eglInitialize(candidate, NULL, NULL))
        return -1;

    const EGLint visual[] = {EGL_SURFACE_TYPE,
                             EGL_PBUFFER_BIT,
                             EGL_RENDERABLE_TYPE,
                             EGL_OPENGL_BIT,
                             EGL_RED_SIZE,
                             8,
                             EGL_GREEN_SIZE,
                             8,
                             EGL_BLUE_SIZE,
                             8,
                             EGL_NONE};
    EGLint count = 0;
    if (!eglBindAPI(EGL_OPENGL_API) || !eglChooseConfig(candidate, visual, &config, 1, &count) || count != 1) {
        eglTerminate(candidate);
        return -1;
    }
    display = candidate;
    return 0;
}

void podish_zink_shutdown(void) {
    if (display == EGL_NO_DISPLAY)
        return;
    eglMakeCurrent(display, EGL_NO_SURFACE, EGL_NO_SURFACE, EGL_NO_CONTEXT);
    eglTerminate(display);
    display = EGL_NO_DISPLAY;
    config = NULL;
    primary = NULL;
    podish_mesa_zink_set_vk_get_instance_proc_addr(NULL);
}

void* podish_zink_create_context(bool shared, int major, int minor, bool compatibility) {
    if (display == EGL_NO_DISPLAY || !eglBindAPI(EGL_OPENGL_API))
        return NULL;
    EGLint attributes[] = {EGL_CONTEXT_MAJOR_VERSION,
                           major,
                           EGL_CONTEXT_MINOR_VERSION,
                           minor,
                           EGL_CONTEXT_OPENGL_PROFILE_MASK,
                           compatibility ? EGL_CONTEXT_OPENGL_COMPATIBILITY_PROFILE_BIT
                                         : EGL_CONTEXT_OPENGL_CORE_PROFILE_BIT,
                           EGL_NONE};
    if (major < 3 || (major == 3 && minor < 2))
        attributes[4] = EGL_NONE;

    struct podish_zink_context* context = calloc(1, sizeof(*context));
    if (!context)
        return NULL;
    EGLContext share = shared && primary ? primary->context : EGL_NO_CONTEXT;
    context->context = eglCreateContext(display, config, share, attributes);
    if (context->context != EGL_NO_CONTEXT) {
        EGLint pbuffer[] = {EGL_WIDTH, 1, EGL_HEIGHT, 1, EGL_NONE};
        context->surface = eglCreatePbufferSurface(display, config, pbuffer);
        if (context->surface != EGL_NO_SURFACE) {
            if (!primary)
                primary = context;
            return context;
        }
        eglDestroyContext(display, context->context);
    }
    free(context);
    return NULL;
}

void podish_zink_destroy_context(void* opaque) {
    struct podish_zink_context* context = opaque;
    if (!context)
        return;
    if (primary == context)
        primary = NULL;
    if (eglGetCurrentContext() == context->context)
        eglMakeCurrent(display, EGL_NO_SURFACE, EGL_NO_SURFACE, EGL_NO_CONTEXT);
    eglDestroySurface(display, context->surface);
    eglDestroyContext(display, context->context);
    free(context);
}

int podish_zink_make_current(void* opaque) {
    struct podish_zink_context* context = opaque;
    EGLContext egl_context = context ? context->context : EGL_NO_CONTEXT;
    EGLSurface surface = context ? context->surface : EGL_NO_SURFACE;
    return eglMakeCurrent(display, surface, surface, egl_context) ? 0 : -1;
}

void* podish_zink_get_proc_address(const char* name) {
    return name ? (void*)eglGetProcAddress(name) : NULL;
}

void* podish_zink_get_egl_display(void) {
    return display;
}

void* podish_zink_export_texture(uint32_t texture) {
    return podish_mesa_zink_export_resource(podish_mesa_current_texture_resource(texture));
}
