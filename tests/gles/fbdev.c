#include <EGL/egl.h>
#include <GLES2/gl2.h>
#include <dlfcn.h>
#include <errno.h>
#include <fcntl.h>
#include <linux/fb.h>
#include <stdio.h>
#include <stdlib.h>
#include <sys/ioctl.h>
#include <unistd.h>

static void require(int condition, const char* what) {
    if (condition)
        return;
    fprintf(stderr, "%s: EGL=0x%x GLES=0x%x\n", what, eglGetError(), glGetError());
    exit(1);
}

static GLuint shader(GLenum type, const char* text) {
    GLuint id = glCreateShader(type);
    glShaderSource(id, 1, &text, NULL);
    glCompileShader(id);
    GLint compiled;
    glGetShaderiv(id, GL_COMPILE_STATUS, &compiled);
    require(compiled, "compile shader");
    return id;
}

int main(void) {
    require(open("/run/podish-gles/libEGL.so.1", O_RDWR) < 0 && errno == EROFS, "read-only library bind mount");
    void* alias = dlopen("libEGL.so", RTLD_NOW);
    require(alias && dlsym(alias, "eglGetDisplay") == (void*)eglGetDisplay, "EGL library alias");
    dlclose(alias);
    int fd = open("/dev/fb0", O_RDWR);
    struct fb_var_screeninfo mode;
    struct fb_fix_screeninfo layout;
    require(fd >= 0 && ioctl(fd, FBIOGET_VSCREENINFO, &mode) == 0, "open framebuffer");
    if (getenv("PODISH_TEST_RGB565")) {
        mode.bits_per_pixel = 16;
        require(ioctl(fd, FBIOPUT_VSCREENINFO, &mode) == 0, "switch to RGB565");
    }
    require(ioctl(fd, FBIOGET_FSCREENINFO, &layout) == 0, "get framebuffer layout");

    EGLDisplay display = eglGetDisplay(EGL_DEFAULT_DISPLAY);
    require(eglInitialize(display, NULL, NULL), "initialize");
    EGLConfig config;
    EGLint n;
    const EGLint attrs[] = {EGL_SURFACE_TYPE, EGL_WINDOW_BIT, EGL_RENDERABLE_TYPE, EGL_OPENGL_ES2_BIT, EGL_NONE};
    require(eglChooseConfig(display, attrs, &config, 1, &n) && n == 1, "choose config");
    const EGLint version[] = {EGL_CONTEXT_CLIENT_VERSION, 2, EGL_NONE};
    EGLContext context = eglCreateContext(display, config, EGL_NO_CONTEXT, version);
    EGLSurface surface = eglCreateWindowSurface(display, config, 0, NULL);
    require(eglMakeCurrent(display, surface, surface, context), "bind window surface");
    require(eglGetProcAddress("glDrawElements") != NULL, "resolve GLES function");
    printf("GLES renderer: %s\n", glGetString(GL_RENDERER));

    GLuint program = glCreateProgram();
    GLuint vert = shader(GL_VERTEX_SHADER, "attribute vec2 xy; void main(){gl_Position=vec4(xy,0.,1.);}");
    GLuint frag =
        shader(GL_FRAGMENT_SHADER, "precision mediump float; uniform vec4 tint; void main(){gl_FragColor=tint;}");
    glAttachShader(program, vert);
    glAttachShader(program, frag);
    glBindAttribLocation(program, 0, "xy");
    glLinkProgram(program);
    GLint linked;
    glGetProgramiv(program, GL_LINK_STATUS, &linked);
    require(linked, "link shaders");
    glUseProgram(program);
    glViewport(0, 0, (GLsizei)mode.xres, (GLsizei)mode.yres);
    glClearColor(0, 0, 1, 1);
    glClear(GL_COLOR_BUFFER_BIT);
    static const GLfloat vertices[] = {-1, -1, 3, -1, -1, 3};
    static const GLubyte indices[] = {0, 1, 2};
    glVertexAttribPointer(0, 2, GL_FLOAT, GL_FALSE, 0, vertices);
    glEnableVertexAttribArray(0);
    GLint tint = glGetUniformLocation(program, "tint");
    const GLfloat red[] = {1, 0, 0, 1};
    glUniform4fv(tint, 1, red);
    GLfloat queried[5] = {0, 0, 0, 0, 42};
    glGetUniformfv(program, tint, queried);
    require(queried[0] == 1 && queried[3] == 1 && queried[4] == 42, "uniform query");
    glScissor(0, 0, (GLsizei)mode.xres, (GLsizei)mode.yres / 2);
    glEnable(GL_SCISSOR_TEST);
    glDrawElements(GL_TRIANGLES, 3, GL_UNSIGNED_BYTE, indices);
    require(glGetError() == GL_NO_ERROR, "draw client arrays and client indices");
    GLuint index_buffer;
    glGenBuffers(1, &index_buffer);
    glBindBuffer(GL_ELEMENT_ARRAY_BUFFER, index_buffer);
    glBufferData(GL_ELEMENT_ARRAY_BUFFER, sizeof(indices), indices, GL_STATIC_DRAW);
    glDrawElements(GL_TRIANGLES, 3, GL_UNSIGNED_BYTE, NULL);
    require(glGetError() == GL_NO_ERROR, "draw client arrays and index buffer");
    glDeleteBuffers(1, &index_buffer);
    GLubyte pixel[4];
    glReadPixels(0, 0, 1, 1, GL_RGBA, GL_UNSIGNED_BYTE, pixel);
    require(glGetError() == GL_NO_ERROR && pixel[0] == 255 && pixel[2] == 0, "draw and read pixels");
    require(eglSwapBuffers(display, surface), "present");
    unsigned bytes = mode.bits_per_pixel / 8;
    unsigned char top[4], bottom[4];
    require(pread(fd, top, bytes, 0) == (int)bytes &&
                pread(fd, bottom, bytes, (off_t)(mode.yres - 1) * layout.line_length) == (int)bytes,
            "read framebuffer");
    if (bytes == 2) {
        require(top[0] == 0x1f && top[1] == 0 && bottom[0] == 0 && bottom[1] == 0xf8, "RGB565 scanout and row order");
    } else {
        require(bytes == 4 && top[0] == 255 && top[2] == 0 && bottom[0] == 0 && bottom[2] == 255,
                "XRGB8888 scanout and row order");
    }

    require(eglDestroyContext(display, context) && eglGetCurrentContext() == context, "destroy current context");
    require(eglDestroySurface(display, surface) && eglGetCurrentSurface(EGL_DRAW) == surface,
            "destroy current surface");
    require(eglMakeCurrent(display, EGL_NO_SURFACE, EGL_NO_SURFACE, EGL_NO_CONTEXT) &&
                eglGetCurrentContext() == EGL_NO_CONTEXT,
            "release destroyed objects");
    require(eglTerminate(display), "terminate");
    close(fd);
    puts("fbdev EGL/GLES smoke passed");
    return 0;
}
