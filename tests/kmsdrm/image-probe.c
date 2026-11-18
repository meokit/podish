#include <EGL/egl.h>
#include <GLES2/gl2.h>
#include <fcntl.h>
#include <gbm.h>
#include <poll.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <sys/mman.h>
#include <unistd.h>
#include <xf86drm.h>
#include <xf86drmMode.h>

static void require(bool ok, const char* check) {
    if (!ok) {
        fprintf(stderr, "%s failed (EGL %#x, GL %#x)\n", check, eglGetError(), glGetError());
        exit(1);
    }
}

static unsigned flips;
static void flip_handler(int fd, unsigned sequence, unsigned sec, unsigned usec, void* data) {
    (void)fd;
    (void)sequence;
    (void)sec;
    (void)usec;
    require(data == &flips, "preserve KMS flip cookie");
    flips++;
}

int main(void) {
    int card = open("/dev/dri/card0", O_RDWR | O_CLOEXEC);
    int render = open("/dev/dri/renderD128", O_RDWR | O_CLOEXEC);
    require(card >= 0 && render >= 0, "open DRM nodes");
    drmModeRes* resources = drmModeGetResources(card);
    require(resources && resources->count_crtcs == 1 && resources->count_connectors == 1, "find KMS output");
    uint32_t crtc = resources->crtcs[0], connector = resources->connectors[0];
    drmModeConnector* output = drmModeGetConnector(card, connector);
    require(output && output->count_modes > 0, "find KMS mode");
    drmModeModeInfo mode = output->modes[0];

    struct gbm_device* device = gbm_create_device(render);
    require(device != NULL, "open managed GBM device");
    typedef EGLDisplay (*platform_proc)(EGLenum, void*, const EGLint*);
    typedef void (*bind_image_proc)(GLenum, void*);
    platform_proc platform = (platform_proc)eglGetProcAddress("eglGetPlatformDisplayEXT");
    bind_image_proc bind_image = (bind_image_proc)eglGetProcAddress("glEGLImageTargetRenderbufferStorageOES");
    require(platform && bind_image, "look up guest EGL entry points");
    EGLDisplay display = platform(0x31D7 /* EGL_PLATFORM_GBM_KHR */, device, NULL);
    require(eglInitialize(display, NULL, NULL), "initialize managed EGL");
    const EGLint attrs[] = {EGL_CONTEXT_CLIENT_VERSION, 2, EGL_NONE};
    EGLContext context = eglCreateContext(display, NULL, EGL_NO_CONTEXT, attrs);
    require(context && eglMakeCurrent(display, EGL_NO_SURFACE, EGL_NO_SURFACE, context), "bind surfaceless context");

    struct gbm_bo* bo = gbm_bo_create(device, mode.hdisplay, mode.vdisplay, GBM_FORMAT_XRGB8888,
                                      GBM_BO_USE_RENDERING | GBM_BO_USE_SCANOUT);
    require(bo && gbm_bo_get_modifier(bo) == UINT64_C(0x00ffffffffffffff), "allocate implicit GPU BO");
    int share = gbm_bo_get_fd_for_plane(bo, 0);
    require(share >= 0, "export GPU PRIME fd");
    require(mmap(NULL, 4096, PROT_READ, MAP_SHARED, share, 0) == MAP_FAILED, "GPU PRIME has opaque storage");
    uint32_t handles[4] = {0}, strides[4] = {gbm_bo_get_stride(bo)}, offsets[4] = {0};
    require(drmPrimeFDToHandle(card, share, handles) == 0, "import PRIME on KMS card");
    uint32_t fb;
    require(drmModeAddFB2(card, mode.hdisplay, mode.vdisplay, GBM_FORMAT_XRGB8888, handles, strides, offsets, &fb, 0) ==
                0,
            "register GPU BO for scanout");

    EGLAttrib plane[] = {
        EGL_WIDTH, mode.hdisplay, EGL_HEIGHT,     mode.vdisplay, 0x3271, GBM_FORMAT_XRGB8888, 0x3272, share, 0x3273,
        0,         0x3274,        strides[0] + 4, EGL_NONE};
    require(eglCreateImage(display, EGL_NO_CONTEXT, 0x3270, NULL, plane) == EGL_NO_IMAGE, "reject mismatched pitch");
    eglGetError();
    plane[11] = strides[0];
    EGLImage image = eglCreateImage(display, EGL_NO_CONTEXT, 0x3270, NULL, plane);
    require(image != EGL_NO_IMAGE, "import PRIME fd as guest EGLImage");
    close(share);
    require(drmCloseBufferHandle(card, handles[0]) == 0, "release KMS GEM handle");
    gbm_bo_destroy(bo);

    GLuint rb, fbo;
    glGenRenderbuffers(1, &rb);
    glBindRenderbuffer(GL_RENDERBUFFER, rb);
    bind_image(GL_RENDERBUFFER, image);
    glGenFramebuffers(1, &fbo);
    glBindFramebuffer(GL_FRAMEBUFFER, fbo);
    glFramebufferRenderbuffer(GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_RENDERBUFFER, rb);
    require(glCheckFramebufferStatus(GL_FRAMEBUFFER) == GL_FRAMEBUFFER_COMPLETE, "complete image-backed guest FBO");
    GLint type, name;
    glGetFramebufferAttachmentParameteriv(GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_FRAMEBUFFER_ATTACHMENT_OBJECT_TYPE,
                                          &type);
    glGetFramebufferAttachmentParameteriv(GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_FRAMEBUFFER_ATTACHMENT_OBJECT_NAME,
                                          &name);
    require(type == GL_RENDERBUFFER && (GLuint)name == rb, "preserve guest renderbuffer attachment identity");
    require(eglDestroyImage(display, image), "release EGLImage while FBO retains backing");

    for (unsigned i = 0; i < 3; i++) {
        glClearColor(i == 0, i == 1, i == 2, 1.0);
        glClear(GL_COLOR_BUFFER_BIT);
        glFlush();
        GLubyte pixel[4];
        glReadPixels(0, 0, 1, 1, GL_RGBA, GL_UNSIGNED_BYTE, pixel);
        require(glGetError() == GL_NO_ERROR && pixel[i] == 255 && pixel[(i + 1) % 3] == 0 && pixel[(i + 2) % 3] == 0 &&
                    pixel[3] == 255,
                "render into and validate guest FBO");
        if (i == 0)
            require(drmModeSetCrtc(card, crtc, fb, 0, 0, &connector, 1, &mode) == 0, "modeset GPU texture");
        else {
            require(drmModePageFlip(card, crtc, fb, DRM_MODE_PAGE_FLIP_EVENT, &flips) == 0, "queue GPU flip");
            struct pollfd pollfd = {.fd = card, .events = POLLIN};
            drmEventContext event = {.version = 2, .page_flip_handler = flip_handler};
            require(poll(&pollfd, 1, 1000) == 1 && drmHandleEvent(card, &event) == 0 && flips == i,
                    "receive GPU flip event");
        }
    }

    glDeleteFramebuffers(1, &fbo);
    glDeleteRenderbuffers(1, &rb);
    require(drmModeSetCrtc(card, crtc, 0, 0, 0, NULL, 0, NULL) == 0 && drmModeRmFB(card, fb) == 0,
            "remove scanout references");
    require(eglMakeCurrent(display, EGL_NO_SURFACE, EGL_NO_SURFACE, EGL_NO_CONTEXT) &&
                eglDestroyContext(display, context) && eglTerminate(display),
            "clean up guest EGL");
    gbm_device_destroy(device);
    drmModeFreeConnector(output);
    drmModeFreeResources(resources);
    close(render);
    close(card);
    puts("Managed EGLImage GPU BO smoke passed (3 colors and 2 page flips)");
    return 0;
}
