#ifndef PODISH_GLES_TRANSPORT_H
#define PODISH_GLES_TRANSPORT_H
#include <GLES2/gl2.h>
#include <stdint.h>
#include <string.h>

#define PODISH_EXPORT __attribute__((visibility("default")))
#define PG_IOCTL 0xC0285047u
#define PG_ABI 1u
#define PG_WORDS 9
enum pg_operation {
    PG_INIT = 1,
    PG_CREATE_CONTEXT,
    PG_DESTROY_CONTEXT,
    PG_CREATE_SURFACE,
    PG_DESTROY_SURFACE,
    PG_MAKE_CURRENT,
    PG_FRAME,
    PG_RESIZE_SURFACE,
    PG_FB_SIZE,
    PG_RELEASE_THREAD,
    PG_SWAP,
    PG_CREATE_GBM_SURFACE,
    PG_IMPORT_IMAGE,
    PG_DESTROY_IMAGE,
    PG_IMAGE_RENDERBUFFER,
};

struct pg_request {
    uint32_t operation;
    uint32_t args[PG_WORDS];
};

_Static_assert(sizeof(struct pg_request) == 40, "Podish GLES ioctl ABI");
_Static_assert(sizeof(void*) == 4, "Podish GLES guest requires IA-32");

PODISH_EXPORT int pg_call(uint32_t operation, const uint32_t args[PG_WORDS]);
PODISH_EXPORT void* pg_get_gl_proc(const char* name);
PODISH_EXPORT void glEGLImageTargetRenderbufferStorageOES(GLenum target, void* image);
const GLubyte* pg_string(uint32_t operation, GLenum name);

#define pg_gl(op, ...) pg_call(op, (const uint32_t[PG_WORDS]){__VA_ARGS__})
#define pg_ptr(p) ((uint32_t)(uintptr_t)(p))

static inline uint32_t pg_float(float value) {
    uint32_t bits;
    memcpy(&bits, &value, sizeof(bits));
    return bits;
}
#endif
