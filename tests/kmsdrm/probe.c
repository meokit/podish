#include <errno.h>
#include <fcntl.h>
#include <gbm.h>
#include <poll.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/epoll.h>
#include <sys/mman.h>
#include <unistd.h>
#include <xf86drm.h>
#include <xf86drmMode.h>

static void require(bool ok, const char* operation) {
    if (!ok) {
        fprintf(stderr, "%s: %s\n", operation, strerror(errno));
        exit(1);
    }
}

static uintptr_t cookie;
static uint32_t event_crtc, last_sequence;
static unsigned flips;

static void flipped(int fd, unsigned sequence, unsigned sec, unsigned usec, unsigned crtc, void* opaque) {
    (void)fd;
    require(sec > 0 && usec < 1000000, "validate monotonic flip timestamp");
    require(flips == 0 || sequence > last_sequence, "validate flip sequence");
    last_sequence = sequence;
    event_crtc = crtc;
    cookie = (uintptr_t)opaque;
    ++flips;
}

static uint32_t register_fb(int fd, struct gbm_bo* bo, uint32_t height, bool legacy) {
    uint32_t fb;
    uint32_t width = gbm_bo_get_width(bo);
    uint32_t handles[4] = {gbm_bo_get_handle(bo).u32};
    uint32_t pitches[4] = {gbm_bo_get_stride(bo)};
    uint32_t offsets[4] = {0};
    if (legacy)
        require(drmModeAddFB(fd, width, height, 24, 32, pitches[0], handles[0], &fb) == 0, "drmModeAddFB");
    else
        require(drmModeAddFB2(fd, width, height, GBM_FORMAT_XRGB8888, handles, pitches, offsets, &fb, 0) == 0,
                "drmModeAddFB2");
    return fb;
}

static void wait_flip(int fd, int epoll, unsigned expected, uint32_t crtc, uintptr_t data) {
    struct pollfd pfd = {.fd = fd, .events = POLLIN};
    struct epoll_event event;
    if (expected == 1)
        require(poll(&pfd, 1, 1000) == 1 && (pfd.revents & POLLIN), "poll for page flip");
    else
        require(epoll_wait(epoll, &event, 1, 1000) == 1 && event.data.fd == fd && (event.events & EPOLLIN),
                "epoll for page flip");

    drmEventContext event_context = {.version = 3, .page_flip_handler2 = flipped};
    require(drmHandleEvent(fd, &event_context) == 0, "read flip using drmHandleEvent");
    require(flips == expected && event_crtc == crtc && cookie == data, "verify flip event fields");
    require(poll(&pfd, 1, 0) == 0, "event queue becomes empty");
}

static void check_prime(int primary, struct gbm_bo* bo, uint32_t value) {
    char* path = drmGetRenderDeviceNameFromFd(primary);
    require(path && !strcmp(path, "/dev/dri/renderD128"), "discover render node through sysfs");
    int render = open(path, O_RDWR | O_CLOEXEC);
    free(path);
    require(render >= 0 && drmGetNodeTypeFromFd(render) == DRM_NODE_RENDER, "open render node");
    require(drmModeGetResources(render) == NULL && drmSetMaster(render) < 0, "render node cannot modeset");

    int readonly;
    require(drmPrimeHandleToFD(primary, gbm_bo_get_handle(bo).u32, DRM_CLOEXEC, &readonly) == 0,
            "export a read-only PRIME fd");
    require(mmap(NULL, 4096, PROT_READ | PROT_WRITE, MAP_SHARED, readonly, 0) == MAP_FAILED && errno == EACCES,
            "reject writable mappings of a read-only PRIME fd");
    close(readonly);

    int share = gbm_bo_get_fd_for_plane(bo, 0);
    require(share >= 0 && (fcntl(share, F_GETFD) & FD_CLOEXEC), "export GBM buffer as a PRIME fd");
    uint32_t handle, repeat;
    require(drmPrimeFDToHandle(render, share, &handle) == 0 && drmPrimeFDToHandle(render, share, &repeat) == 0 &&
                handle == repeat,
            "PRIME imports reuse the handle within one DRM file");

    struct gbm_device* device = gbm_create_device(render);
    require(device != NULL, "create GBM render device");
    struct gbm_import_fd_data info = {.fd = share,
                                      .width = gbm_bo_get_width(bo),
                                      .height = gbm_bo_get_height(bo),
                                      .stride = gbm_bo_get_stride(bo),
                                      .format = gbm_bo_get_format(bo)};
    struct gbm_bo* first = gbm_bo_import(device, GBM_BO_IMPORT_FD, &info, GBM_BO_USE_LINEAR);
    struct gbm_bo* second = gbm_bo_import(device, GBM_BO_IMPORT_FD, &info, GBM_BO_USE_LINEAR);
    require(first && second, "import PRIME buffer into two GBM objects");
    close(share);
    gbm_bo_destroy(first);
    require(drmCloseBufferHandle(render, handle) == 0, "release the libdrm PRIME handle");
    uint32_t stride;
    void* map;
    uint32_t* pixel = gbm_bo_map(second, 3, 4, 1, 1, GBM_BO_TRANSFER_READ, &stride, &map);
    require(pixel && *pixel == value, "GBM import shares data and owns its own handle");
    gbm_bo_unmap(second, map);
    gbm_bo_destroy(second);
    gbm_device_destroy(device);
    close(render);
}

int main(void) {
    int fd = open("/dev/dri/card0", O_RDWR | O_CLOEXEC | O_NONBLOCK);
    require(fd >= 0, "open DRM card");
    drmVersionPtr version = drmGetVersion(fd);
    require(version && !strcmp(version->name, "podish"), "query DRM version");
    drmFreeVersion(version);
    int other = open("/dev/dri/card0", O_RDWR | O_CLOEXEC);
    require(other >= 0, "open second DRM file");
    require(drmSetMaster(fd) == 0 && drmSetMaster(other) < 0, "exclusive DRM master");

    uint64_t cap;
    require(drmGetCap(fd, DRM_CAP_DUMB_BUFFER, &cap) == 0 && cap == 1, "query dumb buffer capability");
    drmModeRes* resources = drmModeGetResources(fd);
    require(resources && resources->count_crtcs == 1 && resources->count_connectors == 1 &&
                resources->count_encoders == 1,
            "enumerate one KMS output");
    uint32_t crtc = resources->crtcs[0];
    uint32_t connector = resources->connectors[0];
    drmModeConnector* output = drmModeGetConnector(fd, connector);
    require(output && output->connection == DRM_MODE_CONNECTED && output->count_modes == 1 &&
                output->count_encoders == 1,
            "query connector");
    drmModeEncoder* encoder = drmModeGetEncoder(fd, output->encoders[0]);
    require(encoder && (encoder->possible_crtcs & 1), "query encoder");
    drmModeFreeEncoder(encoder);
    drmModeModeInfo mode = output->modes[0];
    require(mode.hdisplay == 640 && mode.vdisplay == 480 && mode.vrefresh == 60, "query advertised mode");

    struct gbm_device* device = gbm_create_device(fd);
    require(device && gbm_device_get_fd(device) == fd, "open GBM device");
    uint32_t usage = GBM_BO_USE_SCANOUT | GBM_BO_USE_LINEAR | GBM_BO_USE_WRITE;
    require(gbm_device_is_format_supported(device, GBM_FORMAT_XRGB8888, usage), "linear GBM capability");
    require(!gbm_device_is_format_supported(device, GBM_FORMAT_XRGB8888, GBM_BO_USE_RENDERING),
            "GPU capability negotiation");
    struct gbm_bo* first = gbm_bo_create(device, mode.hdisplay, mode.vdisplay, GBM_FORMAT_XRGB8888, usage);
    const uint64_t linear = 0;
    struct gbm_bo* second =
        gbm_bo_create_with_modifiers(device, mode.hdisplay, mode.vdisplay + 32, GBM_FORMAT_XRGB8888, &linear, 1);
    require(first && second, "allocate two GBM BOs through DRM");
    require(gbm_bo_get_plane_count(first) == 1 && gbm_bo_get_modifier(first) == 0, "query GBM plane metadata");

    uint32_t stride, alias_stride;
    void *token, *alias_token;
    uint32_t* pixel = gbm_bo_map(first, 3, 4, 1, 1, GBM_BO_TRANSFER_WRITE, &stride, &token);
    uint32_t* alias = gbm_bo_map(first, 3, 4, 1, 1, GBM_BO_TRANSFER_READ, &alias_stride, &alias_token);
    require(pixel && alias && stride == alias_stride, "map shared GBM BO");
    require(*alias == 0, "new BO is zero initialized");
    *pixel = 0x00a05533;
    require(*alias == 0x00a05533, "mappings share linear buffer pages");
    check_prime(fd, first, *alias);
    gbm_bo_unmap(first, alias_token);

    uint32_t initial = 0x01234567;
    require(gbm_bo_write(second, &initial, sizeof(initial)) == 0, "write a linear BO");
    uint32_t* second_pixel = gbm_bo_map(second, 0, 0, 1, 1, GBM_BO_TRANSFER_READ, &alias_stride, &alias_token);
    require(second_pixel && *second_pixel == initial, "GBM write is visible through mmap");
    gbm_bo_unmap(second, alias_token);
    alias = gbm_bo_map(first, 3, 4, 1, 1, GBM_BO_TRANSFER_READ, &alias_stride, &alias_token);
    require(alias && *alias == 0x00a05533, "different BOs have independent storage");
    gbm_bo_unmap(first, alias_token);

    uint32_t first_fb = register_fb(fd, first, mode.vdisplay, true);
    uint32_t second_fb = register_fb(fd, second, mode.vdisplay, false);
    uint32_t invalid;
    require(drmModeAddFB(other, mode.hdisplay, mode.vdisplay, 24, 32, stride, gbm_bo_get_handle(first).u32, &invalid) <
                0,
            "GEM handle belongs to its open file");
    close(other);

    require(drmModeSetCrtc(fd, crtc, first_fb, 0, 0, &connector, 1, &mode) == 0, "initial modeset");
    drmModeCrtc* state = drmModeGetCrtc(fd, crtc);
    require(state && state->mode_valid && state->buffer_id == first_fb, "first framebuffer is active");
    drmModeFreeCrtc(state);

    int epoll = epoll_create1(EPOLL_CLOEXEC);
    struct epoll_event event = {.events = EPOLLIN, .data.fd = fd};
    require(epoll >= 0 && epoll_ctl(epoll, EPOLL_CTL_ADD, fd, &event) == 0, "register DRM file with epoll");
    require(drmModePageFlip(fd, crtc, second_fb, DRM_MODE_PAGE_FLIP_EVENT, (void*)0x11223344) == 0,
            "queue first page flip");
    wait_flip(fd, epoll, 1, crtc, 0x11223344);
    state = drmModeGetCrtc(fd, crtc);
    require(state && state->buffer_id == second_fb, "flip updates active framebuffer");
    drmModeFreeCrtc(state);
    require(drmModePageFlip(fd, crtc, first_fb, DRM_MODE_PAGE_FLIP_EVENT, (void*)0x55667788) == 0,
            "queue second page flip");
    wait_flip(fd, epoll, 2, crtc, 0x55667788);
    close(epoll);
    gbm_bo_unmap(first, token);

    gbm_bo_destroy(first);
    state = drmModeGetCrtc(fd, crtc);
    require(state && state->buffer_id == first_fb, "KMS framebuffer retains BO after guest handle closes");
    drmModeFreeCrtc(state);
    /* The standalone probe also runs without a display. With --fbdev, verify
       that the active BO reaches the preview and allow time for an SDL upload. */
    int preview = open("/dev/fb0", O_RDONLY | O_CLOEXEC);
    if (preview >= 0) {
        uint32_t scanout;
        off_t offset = (4 * mode.hdisplay + 3) * sizeof(scanout);
        require(pread(preview, &scanout, sizeof(scanout), offset) == sizeof(scanout) &&
                    (scanout & 0xffffff) == 0xa05533,
                "verify KMS scanout in the framebuffer preview");
        close(preview);
        usleep(250000);
        puts("DRM framebuffer preview passed");
    } else {
        require(errno == ENOENT, "open optional framebuffer preview");
    }
    require(drmModeRmFB(fd, first_fb) == 0 && drmModeRmFB(fd, second_fb) == 0, "remove KMS framebuffers");
    gbm_bo_destroy(second);
    gbm_device_destroy(device);
    drmModeFreeConnector(output);
    drmModeFreeResources(resources);
    close(fd);
    printf("DRM/GBM smoke passed (%s, %u page flips)\n", mode.name, flips);
    return 0;
}
