#define _GNU_SOURCE
#include <errno.h>
#include <fcntl.h>
#include <stdio.h>
#include <stdlib.h>
#include <sys/mman.h>
#include <sys/syscall.h>
#include <sys/time.h>
#include <sys/wait.h>
#include <unistd.h>

#define SYS_futex 240
#define FUTEX_WAIT 0
#define FUTEX_WAKE 1

int futex_wait(int* uaddr, int val, const struct timespec* timeout) {
    return syscall(SYS_futex, uaddr, FUTEX_WAIT, val, timeout, NULL, 0);
}

int futex_wake(int* uaddr, int val) {
    return syscall(SYS_futex, uaddr, FUTEX_WAKE, val, NULL, NULL, 0);
}

int main() {
    printf("Starting shared file futex test\n");

    // Create a temporary file
    char filename[] = "/tmp/shared_file_futex_XXXXXX";
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

    int* shared_mem = mmap(NULL, 4096, PROT_READ | PROT_WRITE, MAP_SHARED, fd, 0);
    if (shared_mem == MAP_FAILED) {
        perror("mmap");
        return 1;
    }

    // Initialize futex word
    shared_mem[0] = 0;

    pid_t pid = fork();
    if (pid < 0) {
        perror("fork");
        return 1;
    }

    if (pid == 0) {
        // Child: Sleep a little to ensure parent is waiting, then write and wake
        usleep(50000); // 50ms

        printf("[Child] Writing 1 to shared memory and waking parent...\n");
        shared_mem[0] = 1;

        int woken = futex_wake(&shared_mem[0], 1);
        printf("[Child] Woke %d thread(s)\n", woken);

        exit(0);
    } else {
        // Parent: Wait for futex to become 1
        printf("[Parent] Waiting on futex (val=0)...\n");

        // As long as value is 0, wait
        while (shared_mem[0] == 0) {
            int ret = futex_wait(&shared_mem[0], 0, NULL);
            if (ret < 0 && errno != EAGAIN && errno != EINTR) {
                perror("futex_wait");
                exit(1);
            }
        }

        printf("[Parent] Futex wait finished, val=%d\n", shared_mem[0]);
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
