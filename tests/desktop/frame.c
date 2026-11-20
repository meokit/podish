#include <fcntl.h>
#include <linux/fb.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <sys/ioctl.h>
#include <unistd.h>

int main(void) {
    int fd = open("/dev/fb0", O_RDONLY);
    struct fb_fix_screeninfo fixed;
    struct fb_var_screeninfo mode;
    if (fd < 0 || ioctl(fd, FBIOGET_FSCREENINFO, &fixed) || ioctl(fd, FBIOGET_VSCREENINFO, &mode)) {
        perror("read framebuffer geometry");
        return 1;
    }
    if (mode.bits_per_pixel != 32 || mode.red.length != 8 || mode.green.length != 8 || mode.blue.length != 8 ||
        mode.red.offset > 24 || mode.green.offset > 24 || mode.blue.offset > 24 || !mode.xres || !mode.yres ||
        mode.xres > 4096 || mode.yres > 4096 || ((uint64_t)mode.xres + mode.xoffset) * 4 > fixed.line_length) {
        fprintf(stderr, "Unsupported framebuffer geometry\n");
        return 1;
    }

    uint32_t* row = malloc(mode.xres * sizeof(*row));
    uint8_t* rgb = malloc(mode.xres * 3);
    if (!row || !rgb)
        return 1;
    printf("P6\n%u %u\n255\n", mode.xres, mode.yres);
    for (unsigned y = 0; y < mode.yres; y++) {
        off_t offset = (off_t)(y + mode.yoffset) * fixed.line_length + mode.xoffset * 4;
        if (pread(fd, row, mode.xres * sizeof(*row), offset) != (ssize_t)(mode.xres * sizeof(*row))) {
            perror("read framebuffer row");
            return 1;
        }
        for (unsigned x = 0; x < mode.xres; x++) {
            rgb[3 * x] = row[x] >> mode.red.offset;
            rgb[3 * x + 1] = row[x] >> mode.green.offset;
            rgb[3 * x + 2] = row[x] >> mode.blue.offset;
        }
        if (fwrite(rgb, 3, mode.xres, stdout) != mode.xres)
            return 1;
    }
    close(fd);
    free(rgb);
    free(row);
    return fflush(stdout) == 0 ? 0 : 1;
}
