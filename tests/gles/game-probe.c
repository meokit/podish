#define _GNU_SOURCE
#include <dlfcn.h>
#include <fcntl.h>
#include <linux/fb.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/ioctl.h>
#include <unistd.h>

/* Minimal SDL2 ABI for an LD_PRELOAD probe usable with distribution binaries. */
typedef struct SDL_Renderer SDL_Renderer;
struct renderer_info {
    const char* name;
    uint32_t flags, num_texture_formats, texture_formats[16];
    int max_texture_width, max_texture_height;
};

/* SDL 2.26 Wayland creates its EGL surface before SDL_CreateRenderer can select ES. */
__attribute__((visibility("default"))) void* SDL_CreateWindow(const char* title, int x, int y, int w, int h,
                                                              uint32_t flags) {
    void* (*create)(const char*, int, int, int, int, uint32_t) = dlsym(RTLD_NEXT, "SDL_CreateWindow");
    int (*attribute)(int, int) = dlsym(RTLD_NEXT, "SDL_GL_SetAttribute");
    const char* (*driver)(void) = dlsym(RTLD_NEXT, "SDL_GetCurrentVideoDriver");
    if (!create || !attribute || !driver)
        _exit(1);
    if (driver() && !strcmp(driver(), "wayland")) {
        attribute(21, 4); /* SDL_GL_CONTEXT_PROFILE_MASK, SDL_GL_CONTEXT_PROFILE_ES */
        attribute(17, 2); /* SDL_GL_CONTEXT_MAJOR_VERSION */
        attribute(18, 0); /* SDL_GL_CONTEXT_MINOR_VERSION */
    }
    return create(title, x, y, w, h, flags);
}

/* Select one backend explicitly so SDL's software fallback cannot hide startup errors. */
__attribute__((visibility("default"))) SDL_Renderer* SDL_CreateRenderer(void* window, int index, uint32_t flags) {
    SDL_Renderer* (*create)(void*, int, uint32_t) = dlsym(RTLD_NEXT, "SDL_CreateRenderer");
    int (*num)(void) = dlsym(RTLD_NEXT, "SDL_GetNumRenderDrivers");
    int (*info)(int, struct renderer_info*) = dlsym(RTLD_NEXT, "SDL_GetRenderDriverInfo");
    const char* (*error)(void) = dlsym(RTLD_NEXT, "SDL_GetError");
    if (!create || !num || !info || !error)
        _exit(1);
    struct renderer_info ri;
    for (int i = 0; i < num(); ++i) {
        if (info(i, &ri) != 0 || strcmp(ri.name, "opengles2") != 0)
            continue;
        SDL_Renderer* renderer = create(window, i, flags);
        if (renderer)
            return renderer;
        fprintf(stderr, "SDL2 opengles2 renderer creation failed: %s\n", error());
        _exit(1);
    }
    (void)index;
    fprintf(stderr, "SDL2 lacks the opengles2 renderer\n");
    _exit(1);
}

static int colors_vary(const uint32_t* pixels, size_t count) {
    for (size_t i = 1; i < count; ++i)
        if (((pixels[i] ^ pixels[0]) & 0xffffffu) != 0)
            return 1;
    return 0;
}

static int framebuffer_has_image(void) {
    int fd = open("/dev/fb0", O_RDONLY);
    if (fd < 0)
        return 0;
    struct fb_fix_screeninfo fixed;
    struct fb_var_screeninfo mode;
    if (ioctl(fd, FBIOGET_FSCREENINFO, &fixed) != 0 || ioctl(fd, FBIOGET_VSCREENINFO, &mode) != 0 ||
        mode.bits_per_pixel != 32 || mode.xres == 0 || mode.xres > 4096) {
        close(fd);
        return 0;
    }
    uint32_t row[4096];
    int varies = 0;
    for (uint32_t y = 0; y < mode.yres && !varies; ++y) {
        size_t len = mode.xres * sizeof(uint32_t);
        off_t offset = (off_t)(y + mode.yoffset) * fixed.line_length + mode.xoffset * sizeof(uint32_t);
        if (pread(fd, row, len, offset) == (ssize_t)len)
            varies = colors_vary(row, mode.xres);
    }
    close(fd);
    return varies;
}

__attribute__((visibility("default"))) void SDL_RenderPresent(SDL_Renderer* renderer) {
    static void (*present)(SDL_Renderer*);
    static int (*info)(SDL_Renderer*, struct renderer_info*);
    static int (*size)(SDL_Renderer*, int*, int*);
    static int (*read_pixels)(SDL_Renderer*, const void*, uint32_t, void*, int);
    static const char* (*video_driver)(void);
    static unsigned frames;

    if (!present) {
        present = dlsym(RTLD_NEXT, "SDL_RenderPresent");
        info = dlsym(RTLD_NEXT, "SDL_GetRendererInfo");
        size = dlsym(RTLD_NEXT, "SDL_GetRendererOutputSize");
        read_pixels = dlsym(RTLD_NEXT, "SDL_RenderReadPixels");
        video_driver = dlsym(RTLD_NEXT, "SDL_GetCurrentVideoDriver");
        if (!present || !info || !size || !read_pixels || !video_driver)
            _exit(1);
    }
    if (++frames < 12) {
        present(renderer);
        return;
    }

    struct renderer_info ri;
    int width, height;
    if (info(renderer, &ri) != 0 || strcmp(ri.name, "opengles2") != 0) {
        fprintf(stderr, "Game did not select the SDL2 opengles2 renderer\n");
        _exit(1);
    }
    if (size(renderer, &width, &height) != 0 || width < 1 || height < 1 || width > 4096 || height > 4096)
        _exit(1);
    size_t count = (size_t)width * height;
    uint32_t* pixels = malloc(count * sizeof(*pixels));
    if (!pixels)
        _exit(1);
    int image = read_pixels(renderer, NULL, 0x16362004u /* SDL_PIXELFORMAT_ARGB8888 */, pixels, width * 4) == 0 &&
                colors_vary(pixels, count);
    free(pixels);
    present(renderer);

    const char* driver = video_driver();
    if (image && (!strcmp(driver, "wayland") || framebuffer_has_image())) {
        printf("SDL2 GLES game smoke passed (%s, %dx%d, frame %u)\n", driver, width, height, frames);
        fflush(stdout);
        _exit(0);
    }
    if (frames == 240) {
        fprintf(stderr, "No nonuniform GLES game frame reached the display\n");
        _exit(1);
    }
}
