#include <fcntl.h>
#include <linux/fb.h>
#include <stdint.h>
#include <stdio.h>
#include <sys/ioctl.h>
#include <sys/mman.h>
#include <unistd.h>

int main(void) {
    struct fb_fix_screeninfo fix;
    struct fb_var_screeninfo var;
    int fd = open("/dev/fb0", O_RDWR);
    if (fd < 0 || ioctl(fd, FBIOGET_FSCREENINFO, &fix) || ioctl(fd, FBIOGET_VSCREENINFO, &var)) {
        perror("query /dev/fb0");
        return 1;
    }
    if (var.xres != 96 || var.yres != 64 || var.bits_per_pixel != 32 ||
        fix.line_length < var.xres * 4 || fix.smem_len < fix.line_length * var.yres) {
        fprintf(stderr, "unexpected framebuffer geometry\n");
        return 1;
    }
    uint32_t* pixels = mmap(NULL, fix.smem_len, PROT_READ | PROT_WRITE, MAP_SHARED, fd, 0);
    if (pixels == MAP_FAILED) {
        perror("mmap framebuffer");
        return 1;
    }
    for (unsigned y = 0; y < var.yres; ++y)
        for (unsigned x = 0; x < var.xres; ++x)
            pixels[y * (fix.line_length / 4) + x] = x < var.xres / 2 ? 0x00ff4040 : 0x004080ff;
    uint32_t pixel;
    if (pread(fd, &pixel, sizeof(pixel), 0) != sizeof(pixel) || pixel != 0x00ff4040) {
        fprintf(stderr, "framebuffer mapping does not share data with read\n");
        return 1;
    }
    usleep(250000); /* Give the SDL presenter time to upload the changed frame. */
    munmap(pixels, fix.smem_len);
    close(fd);
    puts("fbdev smoke passed");
    return 0;
}
