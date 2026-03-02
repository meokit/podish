#define _GNU_SOURCE
#include <stdio.h>
#include <stdlib.h>
#include <sys/mman.h>
#include <sys/wait.h>
#include <unistd.h>

int main() {
    printf("Starting anonymous COW test\n");

    // Map an anonymous private page
    int* shared_val = mmap(NULL, 4096, PROT_READ | PROT_WRITE, MAP_PRIVATE | MAP_ANONYMOUS, -1, 0);
    if (shared_val == MAP_FAILED) {
        perror("mmap");
        return 1;
    }

    // Initialize value
    *shared_val = 42;

    // Evict the page from the C++ MMU page tables using mprotect.
    // This forces the emulator to drop the eager native memory copy during fork.
    mprotect(shared_val, 4096, PROT_NONE);

    pid_t p = fork();
    if (p < 0) {
        perror("fork");
        return 1;
    }

    if (p == 0) {
        // Child process
        // Fault the page back in
        mprotect(shared_val, 4096, PROT_READ | PROT_WRITE);

        printf("Child: Writing 100 to anonymous private page\n");
        *shared_val = 100;

        // Verify locally
        if (*shared_val != 100) {
            printf("Child: ERROR: Read %d, expected 100\n", *shared_val);
            exit(1);
        }

        exit(0);
    } else {
        // Parent process
        int status;
        waitpid(p, &status, 0);

        // Fault the page back in for the parent
        mprotect(shared_val, 4096, PROT_READ | PROT_WRITE);

        printf("Parent: Child exited with status %d\n", WEXITSTATUS(status));

        // Value should still be 42. If it's 100, COW mapping is broken (shared physical page)
        printf("Parent: Value is %d\n", *shared_val);

        if (*shared_val != 42) {
            printf("ERROR: COW is broken! Parent sees child's write to MAP_PRIVATE mapping.\n");
            return 1;
        }

        printf("SUCCESS\n");
        return 0;
    }
}
