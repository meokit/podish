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

static GLuint sampling_shader(GLenum type, const GLchar* source) {
    GLuint shader = glCreateShader(type);
    glShaderSource(shader, 1, &source, NULL);
    glCompileShader(shader);
    GLint compiled = 0;
    glGetShaderiv(shader, GL_COMPILE_STATUS, &compiled);
    require(compiled, "compile image sampling shader");
    return shader;
}

static void sample_and_check(unsigned red, unsigned blue, const char* check) {
    glDrawArrays(GL_TRIANGLES, 0, 3);
    GLubyte pixel[4];
    glReadPixels(0, 0, 1, 1, GL_RGBA, GL_UNSIGNED_BYTE, pixel);
    require(glGetError() == GL_NO_ERROR && pixel[0] == red && abs((int)pixel[1] - (int)blue) <= 1 && pixel[2] == 0 &&
                pixel[3] == 255,
            check);
}

static void check_sampling(EGLDisplay display, EGLImage image, GLuint source_fbo, unsigned width, unsigned height) {
    typedef void (*bind_image_proc)(GLenum, void*);
    bind_image_proc bind_image = (bind_image_proc)eglGetProcAddress("glEGLImageTargetTexture2DOES");
    require(bind_image != NULL, "look up EGLImage texture target");

    GLuint images[2], target, destination, alias_fbo;
    glGenTextures(2, images);
    glGenFramebuffers(1, &alias_fbo);
    for (unsigned unit = 0; unit < 2; unit++) {
        glActiveTexture(GL_TEXTURE0 + unit);
        glBindTexture(GL_TEXTURE_2D, images[unit]);
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_NEAREST);
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_NEAREST);
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE);
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE);
        if (unit == 0) {
            glBindFramebuffer(GL_FRAMEBUFFER, alias_fbo);
            glFramebufferTexture2D(GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_TEXTURE_2D, images[0], 0);
        }
        bind_image(GL_TEXTURE_2D, image);
    }
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_LINEAR);
    const GLubyte pattern[] = {255, 0, 0, 0, 0, 0, 255, 0};
    glTexSubImage2D(GL_TEXTURE_2D, 0, 0, 0, 2, 1, GL_RGBA, GL_UNSIGNED_BYTE, pattern);
    require(glGetError() == GL_NO_ERROR, "update shared image through a texture sibling");
    require(eglDestroyImage(display, image), "destroy EGLImage while texture and framebuffer siblings live");

    glBindFramebuffer(GL_FRAMEBUFFER, alias_fbo);
    require(glCheckFramebufferStatus(GL_FRAMEBUFFER) == GL_FRAMEBUFFER_COMPLETE,
            "update the framebuffer when its texture becomes image-backed");
    GLint name;
    glGetFramebufferAttachmentParameteriv(GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_FRAMEBUFFER_ATTACHMENT_OBJECT_NAME,
                                          &name);
    require((GLuint)name == images[0], "keep the guest texture attachment name");

    glActiveTexture(GL_TEXTURE2);
    glGenTextures(1, &target);
    glBindTexture(GL_TEXTURE_2D, target);
    glTexImage2D(GL_TEXTURE_2D, 0, GL_RGBA, 1, 1, 0, GL_RGBA, GL_UNSIGNED_BYTE, NULL);
    glGenFramebuffers(1, &destination);
    glBindFramebuffer(GL_FRAMEBUFFER, destination);
    glFramebufferTexture2D(GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_TEXTURE_2D, target, 0);
    require(glCheckFramebufferStatus(GL_FRAMEBUFFER) == GL_FRAMEBUFFER_COMPLETE, "create sampling destination");

    GLuint vert = sampling_shader(GL_VERTEX_SHADER, "attribute vec2 pos; void main(){gl_Position=vec4(pos,0.,1.);}");
    GLuint frag = sampling_shader(
        GL_FRAGMENT_SHADER,
        "precision highp float; uniform sampler2D a,b; uniform vec2 uv;"
        "void main(){vec4 x=texture2D(a,uv); vec4 y=texture2D(b,uv);gl_FragColor=vec4(x.r,y.b,0.,x.a*y.a);}");
    GLuint program = glCreateProgram();
    glAttachShader(program, vert);
    glAttachShader(program, frag);
    glBindAttribLocation(program, 0, "pos");
    glLinkProgram(program);
    GLint linked = 0;
    glGetProgramiv(program, GL_LINK_STATUS, &linked);
    require(linked, "link image sampling shader");
    glUseProgram(program);
    glUniform1i(glGetUniformLocation(program, "a"), 0);
    glUniform1i(glGetUniformLocation(program, "b"), 1);
    glUniform2f(glGetUniformLocation(program, "uv"), 0.75f / width, 0.5f / height);
    const GLfloat vertices[] = {-1, -1, 3, -1, -1, 3};
    GLuint vbo;
    glGenBuffers(1, &vbo);
    glBindBuffer(GL_ARRAY_BUFFER, vbo);
    glBufferData(GL_ARRAY_BUFFER, sizeof(vertices), vertices, GL_STATIC_DRAW);
    glVertexAttribPointer(0, 2, GL_FLOAT, GL_FALSE, 0, NULL);
    glEnableVertexAttribArray(0);
    glViewport(0, 0, 1, 1);
    sample_and_check(255, 64, "sample one BO with nearest and linear filtering in the same draw");

    GLint unit;
    glGetIntegerv(GL_ACTIVE_TEXTURE, &unit);
    glGetIntegerv(GL_TEXTURE_BINDING_2D, &name);
    require(unit == GL_TEXTURE2 && (GLuint)name == target,
            "restore the active texture and native binding after sampling");
    glActiveTexture(GL_TEXTURE0);
    glGetIntegerv(GL_TEXTURE_BINDING_2D, &name);
    require((GLuint)name == images[0] && glIsTexture(images[0]), "keep the guest texture identity");

    const GLubyte green[4] = {0, 255, 0, 255};
    glTexImage2D(GL_TEXTURE_2D, 0, GL_RGBA, 1, 1, 0, GL_RGBA, GL_UNSIGNED_BYTE, green);
    glBindFramebuffer(GL_FRAMEBUFFER, alias_fbo);
    GLubyte pixel[4];
    glReadPixels(0, 0, 1, 1, GL_RGBA, GL_UNSIGNED_BYTE, pixel);
    require(glGetError() == GL_NO_ERROR && pixel[0] == 0 && pixel[1] == 255 && pixel[2] == 0,
            "redefine one sibling and update its framebuffer attachment");
    glBindFramebuffer(GL_FRAMEBUFFER, destination);
    sample_and_check(0, 64, "redefined texture has independent storage while its peer still samples the image");
    glActiveTexture(GL_TEXTURE1);
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_NEAREST);
    sample_and_check(0, 0, "apply sampler changes after binding the image");
    glGenerateMipmap(GL_TEXTURE_2D);
    require(glGetError() == GL_NO_ERROR, "generate mipmaps in independent storage");
    glUniform2f(glGetUniformLocation(program, "uv"), 1.25f / width, 0.5f / height);
    glBindFramebuffer(GL_FRAMEBUFFER, source_fbo);
    glClearColor(1, 0, 0, 1);
    glClear(GL_COLOR_BUFFER_BIT);
    glBindFramebuffer(GL_FRAMEBUFFER, destination);
    sample_and_check(0, 255, "preserve the detached base level while updating the original image");

    glBindFramebuffer(GL_FRAMEBUFFER, source_fbo);
    glDisableVertexAttribArray(0);
    glUseProgram(0);
    glDeleteBuffers(1, &vbo);
    glDeleteProgram(program);
    glDeleteShader(vert);
    glDeleteShader(frag);
    glDeleteFramebuffers(1, &alias_fbo);
    glDeleteFramebuffers(1, &destination);
    glDeleteTextures(1, &target);
    glDeleteTextures(2, images);
    glActiveTexture(GL_TEXTURE0);
    puts("Managed EGLImage texture sampling passed (shared storage, independent filters and texture redefinition)");
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
    check_sampling(display, image, fbo, mode.hdisplay, mode.vdisplay);

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
