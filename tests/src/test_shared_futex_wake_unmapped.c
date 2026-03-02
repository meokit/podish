#define _GNU_SOURCE
#include <errno.h>
#include <stdio.h>
#include <stdlib.h>
#include <sys/mman.h>
#include <sys/syscall.h>
#include <sys/wait.h>
#include <unistd.h>

#define SYS_futex 240
#define FUTEX_WAIT 0
#define FUTEX_WAKE 1

int futex_wait(int* uaddr, int val) {
    return syscall(SYS_futex, uaddr, FUTEX_WAIT, val, NULL, NULL, 0);
}

int futex_wake(int* uaddr, int val) {
    return syscall(SYS_futex, uaddr, FUTEX_WAKE, val, NULL, NULL, 0);
}

int main() {
    printf("Starting shared futex wake unmapped test\n");

    // Create shared memory
    int* shared_mem = mmap(NULL, 4096, PROT_READ | PROT_WRITE, MAP_SHARED | MAP_ANONYMOUS, -1, 0);
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
        // Child
        // Wait for parent to definitely block
        usleep(100000); // 100ms

        printf("[Child] Calling futex_wake on shared memory WITHOUT touching it...\n");

        // This is the CRITICAL part: do not read or write to shared_mem[0] in C.
        // We directly pass the address to the kernel.
        // If the kernel fails to fault it in, it will do a private wake and return 0.
        int woken = futex_wake(&shared_mem[0], 1);
        printf("[Child] Woke %d thread(s)\n", woken);

        exit(woken > 0 ? 0 : 77); // return a special code if nobody was woken up
    } else {
        // Parent: Wait for futex to become 1, but we know child won't change the value!
        // So we wait for the value to be 0. We expect the child to wake us up regardless of the value (since wait just
        // checks initially). Wait, if child doesn't change the value, the parent might wake up but value is still 0. To
        // be safe against spurious wakeups, let's just do a single wait with a generous timeout, or just wait and see
        // if we return 0 (woken up normally). If we timeout, the test fails. But we don't pass a timeout here, so if it
        // hangs, the test framework will kill it. Or better, we fork a grandchild to kill us if we hang.

        printf("[Parent] Waiting on futex (val=0)...\n");

        // We just wait once. If child wakes us up, wait returns 0.
        int ret = futex_wait(&shared_mem[0], 0);
        printf("[Parent] futex_wait finished with %d, errno=%d\n", ret, errno);

        int status;
        waitpid(pid, &status, 0);
        printf("[Parent] Child exited with status %d\n", WEXITSTATUS(status));

        if (WEXITSTATUS(status) == 77) {
            printf("[Parent] ERROR: Child returned 77, meaning it didn't wake any threads (kernel bug).\n");
            return 1;
        }

        if (ret == 0) {
            printf("SUCCESS\n");
            return 0;
        } else {
            printf("ERROR: futex_wait returned error\n");
            return 1;
        }
    }

    return 0;
}
