#ifndef PODISH_GLES_WAYLAND_H
#define PODISH_GLES_WAYLAND_H

#include <stdint.h>

struct wl_egl_window;
struct pg_wayland;
struct pg_wayland* pg_wayland_create(void* display, struct wl_egl_window* window);
void pg_wayland_destroy(struct pg_wayland* window);
int pg_wayland_size(struct pg_wayland* window, int* width, int* height);
int pg_wayland_present(struct pg_wayland* window, uint32_t surface, int width, int height);

#endif
