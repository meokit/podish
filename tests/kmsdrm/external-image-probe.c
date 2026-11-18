#define _GNU_SOURCE
#include <EGL/egl.h>
#include <GLES2/gl2.h>
#include <fcntl.h>
#include <gbm.h>
#include <stdbool.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

#define GL_TEXTURE_EXTERNAL_OES 0x8D65
#define GL_SAMPLER_EXTERNAL_OES 0x8D66
#define GL_TEXTURE_BINDING_EXTERNAL_OES 0x8D67

static void require(bool ok, const char* message) {
    if (!ok) {
        fprintf(stderr, "%s (EGL %#x, GL %#x)\n", message, eglGetError(), glGetError());
        exit(1);
    }
}

static void expect_gl(GLenum expected, const char* message) {
    GLenum actual = glGetError();
    if (actual != expected) {
        fprintf(stderr, "%s (expected GL %#x, got %#x)\n", message, expected, actual);
        exit(1);
    }
}

static GLuint compile(GLenum type, int count, const char* const* sources) {
    GLuint shader = glCreateShader(type);
    GLint lengths[3];
    for (int i = 0; i < count; i++)
        lengths[i] = (GLint)strlen(sources[i]);
    glShaderSource(shader, count, sources, lengths);
    char original[1024];
    GLsizei returned = 0;
    glGetShaderSource(shader, sizeof(original), &returned, original);
    GLint offset = 0;
    for (int i = 0; i < count; i++) {
        require(offset + lengths[i] <= returned && memcmp(original + offset, sources[i], (size_t)lengths[i]) == 0,
                "return the original shader source");
        offset += lengths[i];
    }
    require(offset == returned && original[returned] == '\0', "preserve original shader length");
    glCompileShader(shader);
    GLint success;
    glGetShaderiv(shader, GL_COMPILE_STATUS, &success);
    if (!success) {
        char log[1024];
        glGetShaderInfoLog(shader, sizeof(log), NULL, log);
        fprintf(stderr, "%s\n", log);
    }
    require(success, type == GL_FRAGMENT_SHADER ? "compile image sampler fragment shader" : "compile vertex shader");
    return shader;
}

static GLuint sampling_program(bool external) {
    const char* vs[] = {"attribute vec2 point; void main() { gl_Position = vec4(point, 0.0, 1.0); }"};
    const char* fs[] = {
        external ? "#extension GL_OES_EGL_image_ex" : "",
        external ? "ternal : require /* external texture input */\n"
                   "precision mediump float; uniform samplerExternalOES frame_source;\n"
                 : "precision mediump float; uniform sampler2D frame_source;\n",
        "uniform sampler2D tint;\n"
        "void main() {\n"
        "    vec4 samplerExternalOES_color = texture2D(frame_source, vec2(0.25));\n"
        "    vec4 color = texture2D(tint, vec2(0.75));\n"
        "    gl_FragColor = vec4(samplerExternalOES_color.r, color.g, samplerExternalOES_color.b, "
        "samplerExternalOES_color.a);\n"
        "}\n",
    };
    GLuint vertex = compile(GL_VERTEX_SHADER, 1, vs);
    GLuint fragment = compile(GL_FRAGMENT_SHADER, 3, fs);
    GLuint program = glCreateProgram();
    glAttachShader(program, vertex);
    glAttachShader(program, fragment);
    glBindAttribLocation(program, 0, "point");
    glLinkProgram(program);
    GLint success;
    glGetProgramiv(program, GL_LINK_STATUS, &success);
    if (!success) {
        char log[1024];
        glGetProgramInfoLog(program, sizeof(log), NULL, log);
        fprintf(stderr, "%s\n", log);
    }
    require(success, "link mixed-sampler program");
    glDeleteShader(vertex);
    glDeleteShader(fragment);
    return program;
}

static void check_uniforms(GLuint program, GLenum frame_type) {
    GLint count;
    glGetProgramiv(program, GL_ACTIVE_UNIFORMS, &count);
    bool frame = false, tint = false;
    for (GLint i = 0; i < count; i++) {
        GLchar name[32];
        GLint size;
        GLenum type;
        glGetActiveUniform(program, (GLuint)i, sizeof(name), NULL, &size, &type, name);
        if (strcmp(name, "frame_source") == 0)
            frame = size == 1 && type == frame_type;
        if (strcmp(name, "tint") == 0)
            tint = size == 1 && type == GL_SAMPLER_2D;
    }
    require(frame && tint, "reflect both original sampler types");
}

static bool advertises_external(void) {
    const char* extension = "GL_OES_EGL_image_external";
    const size_t length = strlen(extension);
    const char* extensions = (const char*)glGetString(GL_EXTENSIONS);
    if (!extensions)
        return false;
    for (const char* match = strstr(extensions, extension); match; match = strstr(match + length, extension))
        if ((match == extensions || match[-1] == ' ') && (match[length] == ' ' || match[length] == '\0'))
            return true;
    return false;
}

static void check_shader(void) {
    GLuint program = sampling_program(true);
    check_uniforms(program, GL_SAMPLER_EXTERNAL_OES);
    require(advertises_external(), "advertise GL_OES_EGL_image_external");
    glDeleteProgram(program);
}

static void check_binding(GLenum query, GLuint expected) {
    GLint bound;
    glGetIntegerv(query, &bound);
    expect_gl(GL_NO_ERROR, "query texture binding");
    require((GLuint)bound == expected, "keep independent texture bindings by target and unit");
}

static void check_state(void) {
    GLuint textures[2];
    glGenTextures(2, textures);
    glBindTexture(GL_TEXTURE_EXTERNAL_OES, textures[0]);
    expect_gl(GL_NO_ERROR, "bind GL_TEXTURE_EXTERNAL_OES");
    glBindTexture(GL_TEXTURE_2D, textures[1]);
    check_binding(GL_TEXTURE_BINDING_EXTERNAL_OES, textures[0]);
    check_binding(GL_TEXTURE_BINDING_2D, textures[1]);

    GLint value;
    glGetTexParameteriv(GL_TEXTURE_EXTERNAL_OES, GL_TEXTURE_MIN_FILTER, &value);
    require(value == GL_LINEAR, "default external minification is linear");
    glGetTexParameteriv(GL_TEXTURE_EXTERNAL_OES, GL_TEXTURE_WRAP_S, &value);
    require(value == GL_CLAMP_TO_EDGE, "default external wrap is clamp-to-edge");
    expect_gl(GL_NO_ERROR, "query external texture parameters");
    glTexParameteri(GL_TEXTURE_EXTERNAL_OES, GL_TEXTURE_MIN_FILTER, GL_NEAREST);
    expect_gl(GL_NO_ERROR, "allow nearest external minification");
    glTexParameteri(GL_TEXTURE_EXTERNAL_OES, GL_TEXTURE_MIN_FILTER, GL_NEAREST_MIPMAP_NEAREST);
    expect_gl(GL_INVALID_ENUM, "reject mipmap filters for external textures");
    glTexParameteri(GL_TEXTURE_EXTERNAL_OES, GL_TEXTURE_WRAP_S, GL_REPEAT);
    expect_gl(GL_INVALID_ENUM, "reject repeat wrap for external textures");
    glTexImage2D(GL_TEXTURE_EXTERNAL_OES, 0, GL_RGBA, 1, 1, 0, GL_RGBA, GL_UNSIGNED_BYTE, NULL);
    expect_gl(GL_INVALID_ENUM, "reject TexImage2D for external textures");
    glGenerateMipmap(GL_TEXTURE_EXTERNAL_OES);
    expect_gl(GL_INVALID_ENUM, "reject GenerateMipmap for external textures");

    glActiveTexture(GL_TEXTURE1);
    check_binding(GL_TEXTURE_BINDING_EXTERNAL_OES, 0);
    glBindTexture(GL_TEXTURE_EXTERNAL_OES, textures[0]);
    glDeleteTextures(1, textures);
    check_binding(GL_TEXTURE_BINDING_EXTERNAL_OES, 0);
    glActiveTexture(GL_TEXTURE0);
    check_binding(GL_TEXTURE_BINDING_EXTERNAL_OES, 0);
    check_binding(GL_TEXTURE_BINDING_2D, textures[1]);
    glDeleteTextures(1, textures + 1);
}

static void check_pixel(GLubyte red, GLubyte blue, const char* message) {
    GLubyte pixel[4];
    glDrawArrays(GL_TRIANGLES, 0, 3);
    glReadPixels(0, 0, 1, 1, GL_RGBA, GL_UNSIGNED_BYTE, pixel);
    expect_gl(GL_NO_ERROR, message);
    if (pixel[0] != red || pixel[1] != 255 || pixel[2] != blue || pixel[3] != 255) {
        fprintf(stderr, "%s (observed %u %u %u %u)\n", message, pixel[0], pixel[1], pixel[2], pixel[3]);
        exit(1);
    }
}

static void check_sampling(EGLDisplay display, struct gbm_device* device, bool external) {
    typedef void (*image_target_proc)(GLenum, void*);
    image_target_proc renderbuffer_image =
        (image_target_proc)eglGetProcAddress("glEGLImageTargetRenderbufferStorageOES");
    image_target_proc texture_image = (image_target_proc)eglGetProcAddress("glEGLImageTargetTexture2DOES");
    require(renderbuffer_image && texture_image, "resolve EGLImage targets");
    struct gbm_bo* bo = gbm_bo_create(device, 16, 8, GBM_FORMAT_XRGB8888, GBM_BO_USE_RENDERING);
    require(bo != NULL, "allocate managed RGB BO");
    int fd = gbm_bo_get_fd(bo);
    require(fd >= 0, "export managed BO as PRIME");
    EGLAttrib attrs[] = {EGL_WIDTH, 16, EGL_HEIGHT, 8, 0x3271, GBM_FORMAT_XRGB8888,
                         0x3272,    fd, 0x3273,     0, 0x3274, gbm_bo_get_stride(bo),
                         EGL_NONE};
    EGLImage image = eglCreateImage(display, EGL_NO_CONTEXT, 0x3270 /* EGL_LINUX_DMA_BUF_EXT */, NULL, attrs);
    require(image != EGL_NO_IMAGE, "import RGB EGLImage");
    close(fd);
    gbm_bo_destroy(bo);

    GLuint renderbuffer, producer, output, textures[3];
    glGenRenderbuffers(1, &renderbuffer);
    glBindRenderbuffer(GL_RENDERBUFFER, renderbuffer);
    renderbuffer_image(GL_RENDERBUFFER, image);
    glGenFramebuffers(1, &producer);
    glBindFramebuffer(GL_FRAMEBUFFER, producer);
    glFramebufferRenderbuffer(GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_RENDERBUFFER, renderbuffer);
    require(glCheckFramebufferStatus(GL_FRAMEBUFFER) == GL_FRAMEBUFFER_COMPLETE, "attach producer EGLImage");
    glClearColor(1, 0, 0, 1);
    glClear(GL_COLOR_BUFFER_BIT);
    glFlush();

    const GLenum target = external ? GL_TEXTURE_EXTERNAL_OES : GL_TEXTURE_2D;
    glGenTextures(3, textures);
    glBindTexture(target, textures[0]);
    expect_gl(GL_NO_ERROR, "bind image sampling texture");
    texture_image(target, image);
    expect_gl(GL_NO_ERROR, "attach EGLImage to sampling texture");
    glTexParameteri(target, GL_TEXTURE_MIN_FILTER, GL_NEAREST);
    glTexParameteri(target, GL_TEXTURE_MAG_FILTER, GL_NEAREST);
    require(eglDestroyImage(display, image), "destroy EGLImage while its siblings remain");

    glActiveTexture(GL_TEXTURE1);
    glBindTexture(GL_TEXTURE_2D, textures[1]);
    const GLubyte green[] = {0, 255, 0, 255};
    glTexImage2D(GL_TEXTURE_2D, 0, GL_RGBA, 1, 1, 0, GL_RGBA, GL_UNSIGNED_BYTE, green);
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_NEAREST);
    if (external) {
        glActiveTexture(GL_TEXTURE0);
        glBindTexture(GL_TEXTURE_2D, textures[1]);
    }
    glActiveTexture(GL_TEXTURE2);
    glBindTexture(GL_TEXTURE_2D, textures[2]);
    glTexImage2D(GL_TEXTURE_2D, 0, GL_RGBA, 1, 1, 0, GL_RGBA, GL_UNSIGNED_BYTE, NULL);
    glGenFramebuffers(1, &output);
    glBindFramebuffer(GL_FRAMEBUFFER, output);
    glFramebufferTexture2D(GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_TEXTURE_2D, textures[2], 0);
    require(glCheckFramebufferStatus(GL_FRAMEBUFFER) == GL_FRAMEBUFFER_COMPLETE, "attach readback target");

    GLuint program = sampling_program(external);
    check_uniforms(program, external ? GL_SAMPLER_EXTERNAL_OES : GL_SAMPLER_2D);
    glUseProgram(program);
    GLint frame_sampler = glGetUniformLocation(program, "frame_source");
    GLint tint_sampler = glGetUniformLocation(program, "tint");
    glUniform1i(frame_sampler, 0);
    glUniform1i(tint_sampler, 1);
    GLuint vertices;
    const GLfloat triangle[] = {-1, -1, 3, -1, -1, 3};
    glGenBuffers(1, &vertices);
    glBindBuffer(GL_ARRAY_BUFFER, vertices);
    glBufferData(GL_ARRAY_BUFFER, sizeof(triangle), triangle, GL_STATIC_DRAW);
    glVertexAttribPointer(0, 2, GL_FLOAT, GL_FALSE, 0, NULL);
    glEnableVertexAttribArray(0);
    glViewport(0, 0, 1, 1);
    check_pixel(255, 0, "sample the original RGB image and the ordinary texture");

    if (external) {
        glUniform1i(tint_sampler, 0);
        glDrawArrays(GL_TRIANGLES, 0, 3);
        expect_gl(GL_INVALID_OPERATION, "reject active samplers of different types on the same unit");
    }
    glActiveTexture(GL_TEXTURE0);
    glBindTexture(GL_TEXTURE_2D, textures[1]);
    glActiveTexture(GL_TEXTURE1);
    glBindTexture(target, textures[0]);
    glUniform1i(frame_sampler, 1);
    glUniform1i(tint_sampler, 0);

    glBindFramebuffer(GL_FRAMEBUFFER, producer);
    glClearColor(0, 0, 1, 1);
    glClear(GL_COLOR_BUFFER_BIT);
    glFlush();
    glDeleteFramebuffers(1, &producer);
    glDeleteRenderbuffers(1, &renderbuffer);
    glBindFramebuffer(GL_FRAMEBUFFER, output);
    check_pixel(0, 255, "sample the updated shared image after rebinding and deleting its producer");

    glDeleteProgram(program);
    glDeleteBuffers(1, &vertices);
    glDeleteFramebuffers(1, &output);
    glDeleteTextures(3, textures);
}

int main(int argc, char** argv) {
    if (argc != 2 || (strcmp(argv[1], "control") && strcmp(argv[1], "shader") && strcmp(argv[1], "state") &&
                      strcmp(argv[1], "sampling"))) {
        fprintf(stderr, "Usage: %s {control|shader|state|sampling}\n", argv[0]);
        return 2;
    }
    int fd = open("/dev/dri/renderD128", O_RDWR | O_CLOEXEC);
    require(fd >= 0, "open Podish DRM render node");
    struct gbm_device* device = gbm_create_device(fd);
    require(device != NULL, "create GBM render device");
    typedef EGLDisplay (*platform_display_proc)(EGLenum, void*, const EGLint*);
    platform_display_proc platform = (platform_display_proc)eglGetProcAddress("eglGetPlatformDisplayEXT");
    require(platform != NULL, "resolve EGL platform display");
    EGLDisplay display = platform(0x31D7 /* EGL_PLATFORM_GBM_KHR */, device, NULL);
    require(eglInitialize(display, NULL, NULL), "initialize managed GBM EGL");
    const EGLint attrs[] = {EGL_CONTEXT_CLIENT_VERSION, 2, EGL_NONE};
    EGLContext context = eglCreateContext(display, NULL, EGL_NO_CONTEXT, attrs);
    require(context && eglMakeCurrent(display, EGL_NO_SURFACE, EGL_NO_SURFACE, context), "bind managed EGL context");

    if (strcmp(argv[1], "shader") == 0)
        check_shader();
    else if (strcmp(argv[1], "state") == 0)
        check_state();
    else
        check_sampling(display, device, strcmp(argv[1], "sampling") == 0);

    require(eglMakeCurrent(display, EGL_NO_SURFACE, EGL_NO_SURFACE, EGL_NO_CONTEXT) &&
                eglDestroyContext(display, context) && eglTerminate(display),
            "release EGL context");
    gbm_device_destroy(device);
    close(fd);
    printf("External image probe passed (%s)\n", argv[1]);
    return 0;
}
