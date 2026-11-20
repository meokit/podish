#include <EGL/egl.h>
#include <GLES3/gl3.h>
#include <fcntl.h>
#include <linux/fb.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/ioctl.h>
#include <unistd.h>

#define EGL_OPENGL_ES3_BIT_KHR 0x0040
#define EGL_CONTEXT_MINOR_VERSION_KHR 0x30FB

#define CHECK(expr)                                                                                                    \
    do {                                                                                                               \
        if (!(expr)) {                                                                                                 \
            fprintf(stderr, "line %d: %s (EGL 0x%x, GL 0x%x)\n", __LINE__, #expr, eglGetError(), glGetError());        \
            exit(1);                                                                                                   \
        }                                                                                                              \
    } while (0)

static GLuint shader(GLenum type, const char* source) {
    GLuint id = glCreateShader(type);
    glShaderSource(id, 1, &source, NULL);
    glCompileShader(id);
    GLint ok = 0;
    glGetShaderiv(id, GL_COMPILE_STATUS, &ok);
    if (!ok) {
        char log[1024] = {0};
        glGetShaderInfoLog(id, sizeof(log), NULL, log);
        fprintf(stderr, "%s\n", log);
    }
    CHECK(ok);
    return id;
}

static GLuint renderer(void) {
    GLuint vs = shader(GL_VERTEX_SHADER, "#version 300 es\n"
                                         "layout(location=0) in ivec2 xy;\n"
                                         "void main(){gl_Position=vec4(vec2(xy),0.,1.);}\n");
    GLuint fs = shader(GL_FRAGMENT_SHADER, "#version 300 es\n"
                                           "precision mediump float; precision mediump sampler3D;\n"
                                           "uniform sampler3D image; uniform mat2x3 weights; out vec4 color;\n"
                                           "void main(){color=texelFetch(image,ivec3(1,1,1),0)*weights[1].y;}\n");
    GLuint program = glCreateProgram();
    glAttachShader(program, vs);
    glAttachShader(program, fs);
    glLinkProgram(program);
    GLint linked = 0;
    glGetProgramiv(program, GL_LINK_STATUS, &linked);
    CHECK(linked);
    glDeleteShader(vs);
    glDeleteShader(fs);
    glUseProgram(program);
    GLfloat matrix[] = {2, 3, 4, 5, 1, 6}, copy[6] = {0};
    GLint location = glGetUniformLocation(program, "weights");
    glUniformMatrix2x3fv(location, 1, GL_FALSE, matrix);
    glGetUniformfv(program, location, copy);
    CHECK(memcmp(matrix, copy, sizeof(copy)) == 0);
    const char* names[] = {"image", "weights"};
    GLuint indices[2] = {GL_INVALID_INDEX, GL_INVALID_INDEX};
    GLint types[2] = {0};
    glGetUniformIndices(program, 2, names, indices);
    CHECK(indices[0] != GL_INVALID_INDEX && indices[1] != GL_INVALID_INDEX);
    glGetActiveUniformsiv(program, 2, indices, GL_UNIFORM_TYPE, types);
    CHECK(types[0] == GL_SAMPLER_3D && types[1] == GL_FLOAT_MAT2x3);
    return program;
}

static void draw(void) {
    GLuint vao, buffers[2], texture;
    glGenVertexArrays(1, &vao);
    glBindVertexArray(vao);
    CHECK(glIsVertexArray(vao));
    glGenBuffers(2, buffers);
    glBindBuffer(GL_ARRAY_BUFFER, buffers[0]);
    static const GLint vertices[] = {99, -1, -1, 3, -1, -1, 3};
    glBufferData(GL_ARRAY_BUFFER, sizeof(vertices), vertices, GL_STATIC_DRAW);
    glVertexAttribIPointer(0, 2, GL_INT, 0, (void*)sizeof(GLint));
    glEnableVertexAttribArray(0);
    void* pointer = NULL;
    glGetVertexAttribPointerv(0, GL_VERTEX_ATTRIB_ARRAY_POINTER, &pointer);
    CHECK(pointer == (void*)sizeof(GLint));
    glBindBuffer(GL_ELEMENT_ARRAY_BUFFER, buffers[1]);
    static const GLuint indices[] = {99, 0, 1, 2};
    glBufferData(GL_ELEMENT_ARRAY_BUFFER, sizeof(indices), indices, GL_STATIC_DRAW);

    glGenTextures(1, &texture);
    glBindTexture(GL_TEXTURE_3D, texture);
    glTexStorage3D(GL_TEXTURE_3D, 1, GL_RGBA8, 2, 2, 2);
    glTexParameteri(GL_TEXTURE_3D, GL_TEXTURE_MIN_FILTER, GL_NEAREST);
    unsigned char pixels[4 * 4 * 4 * 3] = {0};
    size_t last = (2 * 4 * 4 + 2 * 4 + 2) * 4;
    pixels[last + 1] = pixels[last + 3] = 255;
    glPixelStorei(GL_UNPACK_ROW_LENGTH, 4);
    glPixelStorei(GL_UNPACK_IMAGE_HEIGHT, 4);
    glPixelStorei(GL_UNPACK_SKIP_IMAGES, 1);
    glPixelStorei(GL_UNPACK_SKIP_ROWS, 1);
    glPixelStorei(GL_UNPACK_SKIP_PIXELS, 1);
    glTexSubImage3D(GL_TEXTURE_3D, 0, 0, 0, 0, 2, 2, 2, GL_RGBA, GL_UNSIGNED_BYTE, pixels);
    CHECK(glGetError() == GL_NO_ERROR);

    GLuint program = renderer();
    glViewport(0, 0, 8, 8);
    glDrawElementsInstanced(GL_TRIANGLES, 3, GL_UNSIGNED_INT, (void*)sizeof(GLuint), 2);
    unsigned char pixel[4] = {0};
    glReadPixels(1, 1, 1, 1, GL_RGBA, GL_UNSIGNED_BYTE, pixel);
    CHECK(pixel[0] == 0 && pixel[1] == 255 && pixel[2] == 0 && pixel[3] == 255);

    glBindVertexArray(0);
    glBindBuffer(GL_ARRAY_BUFFER, 0);
    glVertexAttribIPointer(0, 2, GL_INT, 0, vertices + 1);
    glEnableVertexAttribArray(0);
    glClearColor(0, 0, 0, 0);
    glClear(GL_COLOR_BUFFER_BIT);
    glDrawRangeElements(GL_TRIANGLES, 0, 2, 3, GL_UNSIGNED_INT, indices + 1);
    glReadPixels(1, 1, 1, 1, GL_RGBA, GL_UNSIGNED_BYTE, pixel);
    CHECK(glGetError() == GL_NO_ERROR && pixel[1] == 255);
    glDisableVertexAttribArray(0);
    glDeleteProgram(program);
    glDeleteTextures(1, &texture);
    glDeleteBuffers(2, buffers);
    glDeleteVertexArrays(1, &vao);
}

static void pixel_buffer_and_mapping(void) {
    GLuint buffers[2];
    glGenBuffers(2, buffers);
    glBindBuffer(GL_PIXEL_PACK_BUFFER, buffers[0]);
    unsigned char initial[64];
    memset(initial, 0x55, sizeof(initial));
    glBufferData(GL_PIXEL_PACK_BUFFER, sizeof(initial), initial, GL_STREAM_READ);
    glPixelStorei(GL_PACK_ROW_LENGTH, 4);
    glPixelStorei(GL_PACK_SKIP_ROWS, 1);
    glPixelStorei(GL_PACK_SKIP_PIXELS, 1);
    glReadPixels(1, 1, 1, 1, GL_RGBA, GL_UNSIGNED_BYTE, (void*)4);
    GLint64 bytes = 0;
    glGetBufferParameteri64v(GL_PIXEL_PACK_BUFFER, GL_BUFFER_SIZE, &bytes);
    CHECK(bytes == sizeof(initial));
    unsigned char* map = glMapBufferRange(GL_PIXEL_PACK_BUFFER, 0, sizeof(initial), GL_MAP_READ_BIT);
    CHECK(map && map[23] == 0x55 && map[24] == 0 && map[25] == 255 && map[26] == 0 && map[27] == 255 &&
          map[28] == 0x55);
    void* pointer = NULL;
    glGetBufferPointerv(GL_PIXEL_PACK_BUFFER, GL_BUFFER_MAP_POINTER, &pointer);
    CHECK(pointer == map);
    CHECK(glUnmapBuffer(GL_PIXEL_PACK_BUFFER));
    glGetBufferPointerv(GL_PIXEL_PACK_BUFFER, GL_BUFFER_MAP_POINTER, &pointer);
    CHECK(pointer == NULL);

    glBindBuffer(GL_COPY_WRITE_BUFFER, buffers[1]);
    glBufferData(GL_COPY_WRITE_BUFFER, sizeof(initial), initial, GL_DYNAMIC_COPY);
    map = glMapBufferRange(GL_COPY_WRITE_BUFFER, 8, 16, GL_MAP_WRITE_BIT | GL_MAP_FLUSH_EXPLICIT_BIT);
    CHECK(map);
    map[0] = 0x11;
    map[5] = 0x22;
    glFlushMappedBufferRange(GL_COPY_WRITE_BUFFER, 4, 4);
    CHECK(glUnmapBuffer(GL_COPY_WRITE_BUFFER));
    map = glMapBufferRange(GL_COPY_WRITE_BUFFER, 8, 16, GL_MAP_READ_BIT);
    CHECK(map && map[0] == 0x55 && map[4] == 0x55 && map[5] == 0x22 && map[6] == 0x55);
    glBufferData(GL_COPY_WRITE_BUFFER, sizeof(initial), initial, GL_DYNAMIC_COPY);
    glGetBufferPointerv(GL_COPY_WRITE_BUFFER, GL_BUFFER_MAP_POINTER, &pointer);
    CHECK(pointer == NULL);
    glDeleteBuffers(1, &buffers[1]);
    // Leave a pack buffer and non-default pixel-store state bound across swap.
    CHECK(glGetError() == GL_NO_ERROR);
}

static void sync_objects(void) {
    GLsync fence = glFenceSync(GL_SYNC_GPU_COMMANDS_COMPLETE, 0);
    CHECK(fence && glIsSync(fence));
    GLint size = 0, type = 0;
    glGetSynciv(fence, GL_SYNC_CONDITION, 1, &size, &type);
    CHECK(size == 1 && type == GL_SYNC_GPU_COMMANDS_COMPLETE);
    glWaitSync(fence, 0, GL_TIMEOUT_IGNORED);
    GLenum status = glClientWaitSync(fence, GL_SYNC_FLUSH_COMMANDS_BIT, UINT64_C(5000000000));
    CHECK(status == GL_ALREADY_SIGNALED || status == GL_CONDITION_SATISFIED);
    glDeleteSync(fence);
    CHECK(!glIsSync(fence) && glGetError() == GL_NO_ERROR);
}

static void share_objects(EGLDisplay display, EGLConfig config, EGLSurface surface, EGLContext context,
                          const EGLint* attributes) {
    EGLContext shared = eglCreateContext(display, config, context, attributes);
    CHECK(shared != EGL_NO_CONTEXT);
    GLuint buffer;
    glGenBuffers(1, &buffer);
    glBindBuffer(GL_ARRAY_BUFFER, buffer);
    unsigned char initial[4] = {1, 2, 3, 4};
    glBufferData(GL_ARRAY_BUFFER, sizeof(initial), initial, GL_DYNAMIC_DRAW);
    unsigned char* map = glMapBufferRange(GL_ARRAY_BUFFER, 0, sizeof(initial), GL_MAP_WRITE_BIT);
    CHECK(map);
    map[0] = 42;
    GLsync fence = glFenceSync(GL_SYNC_GPU_COMMANDS_COMPLETE, 0);
    CHECK(fence && eglMakeCurrent(display, surface, surface, shared));
    CHECK(glIsSync(fence));
    glDeleteSync(fence);
    glBindBuffer(GL_ARRAY_BUFFER, buffer);
    void* pointer = NULL;
    glGetBufferPointerv(GL_ARRAY_BUFFER, GL_BUFFER_MAP_POINTER, &pointer);
    CHECK(pointer == map && glUnmapBuffer(GL_ARRAY_BUFFER));
    map = glMapBufferRange(GL_ARRAY_BUFFER, 0, sizeof(initial), GL_MAP_READ_BIT);
    CHECK(map && map[0] == 42 && memcmp(map + 1, initial + 1, sizeof(initial) - 1) == 0);
    glDeleteBuffers(1, &buffer);
    CHECK(glGetError() == GL_NO_ERROR && eglMakeCurrent(display, surface, surface, context));
    CHECK(!glIsSync(fence) && eglDestroyContext(display, shared));
}

static void check_framebuffer(void) {
    int fd = open("/dev/fb0", O_RDONLY);
    struct fb_fix_screeninfo fixed;
    struct fb_var_screeninfo variable;
    CHECK(fd >= 0 && ioctl(fd, FBIOGET_FSCREENINFO, &fixed) == 0 && ioctl(fd, FBIOGET_VSCREENINFO, &variable) == 0 &&
          variable.bits_per_pixel == 32);
    uint32_t pixel;
    off_t offset = (variable.yres - 2) * fixed.line_length + 4;
    CHECK(lseek(fd, offset, SEEK_SET) == offset && read(fd, &pixel, sizeof(pixel)) == sizeof(pixel));
    CHECK((pixel & 0x00ffffff) == 0x0000ff00);
    close(fd);
}

int main(void) {
    EGLDisplay display = eglGetDisplay(EGL_DEFAULT_DISPLAY);
    CHECK(display != EGL_NO_DISPLAY && eglInitialize(display, NULL, NULL));
    EGLint surface_type = getenv("PODISH_TEST_FBDEV") ? EGL_WINDOW_BIT : EGL_PBUFFER_BIT;
    const EGLint config_attrs[] = {EGL_SURFACE_TYPE, surface_type, EGL_RENDERABLE_TYPE,
                                   EGL_OPENGL_ES2_BIT | EGL_OPENGL_ES3_BIT_KHR, EGL_NONE};
    EGLConfig config;
    EGLint count = 0, version = 0;
    CHECK(eglChooseConfig(display, config_attrs, &config, 1, &count) && count == 1);
    const EGLint attributes[] = {EGL_CONTEXT_CLIENT_VERSION, 3, EGL_CONTEXT_MINOR_VERSION_KHR, 0, EGL_NONE};
    EGLContext context = eglCreateContext(display, config, EGL_NO_CONTEXT, attributes);
    CHECK(context != EGL_NO_CONTEXT);
    CHECK(eglQueryContext(display, context, EGL_CONTEXT_CLIENT_VERSION, &version) && version == 3);
    const EGLint surface_attrs[] = {EGL_WIDTH, 8, EGL_HEIGHT, 8, EGL_NONE};
    EGLSurface surface = surface_type == EGL_WINDOW_BIT ? eglCreateWindowSurface(display, config, 0, NULL)
                                                        : eglCreatePbufferSurface(display, config, surface_attrs);
    CHECK(surface != EGL_NO_SURFACE && eglMakeCurrent(display, surface, surface, context));

    const char* gl_version = (const char*)glGetString(GL_VERSION);
    CHECK(gl_version && strstr(gl_version, "OpenGL ES 3.0"));
    GLint major = 0, minor = -1, extensions = -1;
    glGetIntegerv(GL_MAJOR_VERSION, &major);
    glGetIntegerv(GL_MINOR_VERSION, &minor);
    glGetIntegerv(GL_NUM_EXTENSIONS, &extensions);
    CHECK(major == 3 && minor == 0 && extensions == 0);
    CHECK(eglGetProcAddress("glTexSubImage3D") && eglGetProcAddress("glFenceSync"));
    CHECK(glGetStringi(GL_EXTENSIONS, 0) == NULL && glGetError() == GL_INVALID_VALUE);

    draw();
    pixel_buffer_and_mapping();
    sync_objects();
    share_objects(display, config, surface, context, attributes);
    GLuint framebuffers[2];
    glGenFramebuffers(2, framebuffers);
    glBindFramebuffer(GL_READ_FRAMEBUFFER, framebuffers[0]);
    glBindFramebuffer(GL_DRAW_FRAMEBUFFER, framebuffers[1]);
    GLint pack_buffer = 0, row_length = 0, skip_rows = 0, skip_pixels = 0, read_fbo = 0, draw_fbo = 0;
    CHECK(eglSwapBuffers(display, surface));
    glGetIntegerv(GL_PIXEL_PACK_BUFFER_BINDING, &pack_buffer);
    glGetIntegerv(GL_PACK_ROW_LENGTH, &row_length);
    glGetIntegerv(GL_PACK_SKIP_ROWS, &skip_rows);
    glGetIntegerv(GL_PACK_SKIP_PIXELS, &skip_pixels);
    glGetIntegerv(GL_READ_FRAMEBUFFER_BINDING, &read_fbo);
    glGetIntegerv(GL_DRAW_FRAMEBUFFER_BINDING, &draw_fbo);
    CHECK(pack_buffer != 0 && row_length == 4 && skip_rows == 1 && skip_pixels == 1);
    CHECK(read_fbo == (GLint)framebuffers[0] && draw_fbo == (GLint)framebuffers[1]);
    CHECK(glGetError() == GL_NO_ERROR);
    if (surface_type == EGL_WINDOW_BIT)
        check_framebuffer();
    CHECK(eglMakeCurrent(display, EGL_NO_SURFACE, EGL_NO_SURFACE, EGL_NO_CONTEXT));
    CHECK(eglDestroyContext(display, context) && eglDestroySurface(display, surface) && eglTerminate(display));
    puts("GLES 3.0 probe passed");
    return 0;
}
