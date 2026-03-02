#define _GNU_SOURCE
#include <errno.h>
#include <fcntl.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/mman.h>
#include <sys/wait.h>
#include <unistd.h>

int main() {
    printf("Starting COW private mmap test\n");

    // Create a temporary file
    char filename[] = "/tmp/cow_private_mmap_XXXXXX";
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

    // Write initial data to the file
    char initial_data[4096];
    memset(initial_data, 'A', sizeof(initial_data));
    if (write(fd, initial_data, sizeof(initial_data)) != sizeof(initial_data)) {
        perror("write");
        return 1;
    }

    // Map the file as MAP_PRIVATE
    volatile char* private_mem = mmap(NULL, 4096, PROT_READ | PROT_WRITE, MAP_PRIVATE, fd, 0);
    if (private_mem == MAP_FAILED) {
        perror("mmap");
        return 1;
    }

    // Verify initial data
    if (private_mem[0] != 'A') {
        printf("ERROR: Expected 'A', got '%c'\n", private_mem[0]);
        return 1;
    }

    pid_t pid = fork();
    if (pid < 0) {
        perror("fork");
        return 1;
    }

    if (pid == 0) {
        // Child: write to the private mapping, triggering COW
        printf("[Child] Writing 'B' to private mapping...\n");
        private_mem[0] = 'B';

        // Verify child sees its own write
        if (private_mem[0] != 'B') {
            printf("[Child] ERROR: Expected 'B', got '%c'\n", private_mem[0]);
            exit(1);
        }

        printf("[Child] Read back 'B' successfully. Exiting.\n");
        exit(0);
    } else {
        // Parent: wait for child to finish its write
        int status;
        waitpid(pid, &status, 0);
        printf("[Parent] Child exited with status %d\n", WEXITSTATUS(status));

        // Verify parent still sees 'A'
        printf("[Parent] Checking private mapping...\n");
        if (private_mem[0] != 'A') {
            printf("[Parent] ERROR: Expected 'A', got '%c' (COW failed!)\n", private_mem[0]);
            exit(1);
        }

        printf("[Parent] Value is still 'A'. COW works!\n");
        printf("SUCCESS\n");
    }

    return 0;
}
