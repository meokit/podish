#include <fcntl.h>
#include <signal.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include <sys/wait.h>
#include <unistd.h>

/* Run as Cage's client with --kmsdrm --fbdev. fb0 mirrors the committed KMS output. */
int main(int argc, char** argv) {
    int allow_static = argc > 1 && strcmp(argv[1], "--static") == 0;
    int client = allow_static ? 2 : 1;
    if (argc <= client) {
        fprintf(stderr, "Usage: %s [--static] WAYLAND_CLIENT [ARGS...]\n", argv[0]);
        return 2;
    }
    int framebuffer = open("/dev/fb0", O_RDONLY | O_CLOEXEC);
    if (framebuffer < 0) {
        perror("open KMS preview");
        return 1;
    }

    pid_t child = fork();
    if (child < 0) {
        perror("fork Wayland client");
        close(framebuffer);
        return 1;
    }
    if (child == 0) {
        execv(argv[client], argv + client);
        perror("exec Wayland client");
        _exit(1);
    }

    uint32_t pixels[640];
    uint32_t previous = 0;
    unsigned frames = 0;
    int status = 0;
    for (unsigned trial = 0; trial < 100 && frames < 8; trial++) {
        if (waitpid(child, &status, WNOHANG) != 0) {
            fprintf(stderr, "Wayland client exited before scanout completed\n");
            close(framebuffer);
            return 1;
        }
        uint32_t fingerprint = 2166136261u;
        unsigned different = 0;
        const unsigned rows[] = {60, 150, 240, 330, 420};
        for (unsigned i = 0; i < sizeof(rows) / sizeof(rows[0]); i++) {
            if (pread(framebuffer, pixels, sizeof(pixels), sizeof(pixels) * rows[i]) != sizeof(pixels)) {
                perror("read KMS preview");
                goto finish;
            }
            for (unsigned x = 0; x < 640; x++) {
                different += (pixels[x] & 0xffffffu) != (pixels[0] & 0xffffffu);
                fingerprint = (fingerprint ^ (pixels[x] & 0xffffffu)) * 16777619u;
            }
        }
        if (different > 32 && (allow_static || fingerprint != previous)) {
            previous = fingerprint;
            frames++;
        }
        usleep(40000);
    }

finish:
    close(framebuffer);
    kill(child, SIGINT);
    int stopped = waitpid(child, &status, 0) == child && ((WIFEXITED(status) && WEXITSTATUS(status) == 0) ||
                                                          (WIFSIGNALED(status) && WTERMSIG(status) == SIGINT));
    printf("KMS scanout observed %u %s frames\n", frames, allow_static ? "colored" : "changing");
    return frames == 8 && stopped ? 0 : 1;
}
