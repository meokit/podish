#define _GNU_SOURCE
#include <dlfcn.h>
#include <stddef.h>
#include <stdint.h>

/* SDL2 constants (stable ABI in libSDL2-2.0.so.0) */
#define SDL_WINDOW_OPENGL_FLAG 0x00000002u
#define SDL_GL_MULTISAMPLEBUFFERS_ATTR 13
#define SDL_GL_MULTISAMPLESAMPLES_ATTR 14
#define SDL_GL_CONTEXT_MAJOR_VERSION_ATTR 17
#define SDL_GL_CONTEXT_MINOR_VERSION_ATTR 18
#define SDL_GL_CONTEXT_PROFILE_MASK_ATTR 21
#define SDL_GL_CONTEXT_PROFILE_ES_VAL 0x0004

typedef int (*sdl_gl_set_attribute_fn)(int attr, int value);
typedef void* (*sdl_create_window_fn)(const char* title, int x, int y, int w, int h, uint32_t flags);

static sdl_gl_set_attribute_fn resolve_set_attr(void) {
    static sdl_gl_set_attribute_fn fn = NULL;
    if (!fn) {
        fn = (sdl_gl_set_attribute_fn)dlsym(RTLD_NEXT, "SDL_GL_SetAttribute");
    }
    return fn;
}

static void force_gles2_attributes(sdl_gl_set_attribute_fn fn) {
    if (!fn) {
        return;
    }
    fn(SDL_GL_CONTEXT_PROFILE_MASK_ATTR, SDL_GL_CONTEXT_PROFILE_ES_VAL);
    fn(SDL_GL_CONTEXT_MAJOR_VERSION_ATTR, 2);
    fn(SDL_GL_CONTEXT_MINOR_VERSION_ATTR, 0);
    fn(SDL_GL_MULTISAMPLEBUFFERS_ATTR, 0);
    fn(SDL_GL_MULTISAMPLESAMPLES_ATTR, 0);
}

int SDL_GL_SetAttribute(int attr, int value) {
    sdl_gl_set_attribute_fn real_fn = resolve_set_attr();
    if (!real_fn) {
        return -1;
    }
    switch (attr) {
    case SDL_GL_CONTEXT_PROFILE_MASK_ATTR:
        return real_fn(attr, SDL_GL_CONTEXT_PROFILE_ES_VAL);
    case SDL_GL_CONTEXT_MAJOR_VERSION_ATTR:
        return real_fn(attr, 2);
    case SDL_GL_CONTEXT_MINOR_VERSION_ATTR:
        return real_fn(attr, 0);
    case SDL_GL_MULTISAMPLEBUFFERS_ATTR:
    case SDL_GL_MULTISAMPLESAMPLES_ATTR:
        return real_fn(attr, 0);
    default:
        return real_fn(attr, value);
    }
}

void* SDL_CreateWindow(const char* title, int x, int y, int w, int h, uint32_t flags) {
    static sdl_create_window_fn real_create = NULL;
    if (!real_create) {
        real_create = (sdl_create_window_fn)dlsym(RTLD_NEXT, "SDL_CreateWindow");
    }
    if (!real_create) {
        return NULL;
    }
    if (flags & SDL_WINDOW_OPENGL_FLAG) {
        force_gles2_attributes(resolve_set_attr());
    }
    return real_create(title, x, y, w, h, flags);
}
