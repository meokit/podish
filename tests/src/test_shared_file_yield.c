#define _GNU_SOURCE
#include <errno.h>
#include <fcntl.h>
#include <sched.h>
#include <stdio.h>
#include <stdlib.h>
#include <sys/mman.h>
#include <sys/wait.h>
#include <unistd.h>

int main() {
    printf("Starting shared file yield (spin wait) test\n");

    // Create a temporary file
    char filename[] = "/tmp/shared_file_yield_XXXXXX";
    int fd = mkstemp(filename);
    if (fd < 0) {
        perror("mkstemp");
        return 1;
    }

    // Unlink so it's cleaned up automatically
    unlink(filename);

    if (ftruncate(fd, 4096) != 0) {
        perror("ftruncate");
        return 1;
    }

    volatile int* shared_mem = mmap(NULL, 4096, PROT_READ | PROT_WRITE, MAP_SHARED, fd, 0);
    if (shared_mem == MAP_FAILED) {
        perror("mmap");
        return 1;
    }

    // Initialize shared variable
    shared_mem[0] = 0;

    pid_t pid = fork();
    if (pid < 0) {
        perror("fork");
        return 1;
    }

    if (pid == 0) {
        // Child: yield a bit to let parent spin, then write
        for (int i = 0; i < 50; i++) {
            sched_yield();
        }

        printf("[Child] Writing 1 to shared memory...\n");
        shared_mem[0] = 1;

        exit(0);
    } else {
        // Parent: Spin wait for value to become 1
        printf("[Parent] Spinning on shared variable (val=0)...");

        int loops = 0;
        while (shared_mem[0] == 0) {
            sched_yield();
            loops++;

            // Safety timeout
            if (loops > 1000000) {
                printf("[Parent] ERROR: Timed out waiting for child to write\n");
                kill(pid, 9);
                exit(1);
            }
        }

        printf("[Parent] Spin wait finished after %d yields, val=%d\n", loops, shared_mem[0]);
        if (shared_mem[0] != 1) {
            printf("[Parent] ERROR: Expected 1, got %d\n", shared_mem[0]);
            exit(1);
        }

        int status;
        waitpid(pid, &status, 0);
        printf("[Parent] Child exited with status %d\n", WEXITSTATUS(status));
        printf("SUCCESS\n");
    }

    return 0;
}
