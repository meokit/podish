#ifndef PODISH_GBM_EGL_H
#define PODISH_GBM_EGL_H
#include <stdint.h>

#define PG_GBM_SLOTS 3
struct pg_gbm_geometry {
    int fd;
    uint32_t width, height;
};

#endif
