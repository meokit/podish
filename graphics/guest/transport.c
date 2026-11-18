#include "transport.h"
#include <errno.h>
#include <fcntl.h>
#include <pthread.h>
#include <sys/ioctl.h>
#include <unistd.h>

static pthread_mutex_t device_lock = PTHREAD_MUTEX_INITIALIZER;
static int device = -1;
static pid_t owner;

int pg_call(uint32_t operation, const uint32_t args[PG_WORDS]) {
    struct pg_request request = {.operation = operation};
    memcpy(request.args, args, sizeof(request.args));

    pthread_mutex_lock(&device_lock);
    if (device >= 0 && owner != getpid()) {
        close(device);
        device = -1;
    }
    if (device < 0) {
        device = open("/dev/podish-gles", O_RDWR | O_CLOEXEC);
        owner = getpid();
    }
    int fd = device;
    pthread_mutex_unlock(&device_lock);
    if (fd < 0)
        return -errno;
    if (ioctl(fd, PG_IOCTL, &request) < 0)
        return -errno;
    return (int)request.args[0];
}

const GLubyte* pg_string(uint32_t operation, GLenum name) {
    static _Thread_local GLubyte strings[5][512];
    unsigned slot;
    switch (name) {
    case GL_VENDOR:
        slot = 0;
        break;
    case GL_RENDERER:
        slot = 1;
        break;
    case GL_VERSION:
        slot = 2;
        break;
    case GL_EXTENSIONS:
        slot = 3;
        break;
    case GL_SHADING_LANGUAGE_VERSION:
        slot = 4;
        break;
    default:
        slot = 4;
        break;
    }
    if (pg_gl(operation, name, pg_ptr(strings[slot]), sizeof(strings[slot])) < 0)
        return NULL;
    return strings[slot];
}
