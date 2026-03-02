/*
 * Regression test for Bug #1: futex_wait on a freshly mmap'd shared anonymous
 * page used to return EFAULT because GetPhysicalAddressSafe was called before
 * CopyFromUser, so the page had not yet been faulted into the native MMU.
 *
 * Fix: CopyFromUser is now called first (which faults in the page), and only
 * then is the physical key resolved via GetPhysicalAddressSafe.
 */
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

static int futex_wait(int* uaddr, int val) {
    return (int)syscall(SYS_futex, uaddr, FUTEX_WAIT, val, NULL, NULL, 0);
}

static int futex_wake(int* uaddr, int count) {
    return (int)syscall(SYS_futex, uaddr, FUTEX_WAKE, count, NULL, NULL, 0);
}

int main(void) {
    printf("[test_futex_fresh_page] Starting\n");

    /*
     * Allocate a fresh MAP_SHARED anonymous page.
     * Crucially, we do NOT access the memory before calling futex_wait,
     * so the page is NOT yet faulted into the native MMU at that point.
     */
    int* futex_word = mmap(NULL, 4096, PROT_READ | PROT_WRITE, MAP_SHARED | MAP_ANONYMOUS, -1, 0);
    if (futex_word == MAP_FAILED) {
        perror("mmap");
        return 1;
    }

    /* Page is not accessed yet — *futex_word is virtual 0 from demand paging */

    pid_t pid = fork();
    if (pid < 0) {
        perror("fork");
        return 1;
    }

    if (pid == 0) {
        /* Child: wait on the fresh (un-faulted) page for value 0.
         * Before the fix this returned EFAULT because physKey was resolved
         * before CopyFromUser faulted in the page. */
        int ret = futex_wait(futex_word, 0);
        if (ret < 0 && errno == EFAULT) {
            fprintf(stderr, "[Child] ERR: got EFAULT — regression!\n");
            exit(2);
        }
        if (ret < 0 && errno != EAGAIN) {
            /* EAGAIN is fine: means parent already wrote before we waited */
            perror("[Child] futex_wait unexpected error");
            exit(1);
        }
        printf("[Child] futex_wait returned OK (ret=%d errno=%d)\n", ret, errno);
        exit(0);
    } else {
        /* Parent: give child time to call futex_wait, then wake it */
        usleep(80000); /* 80 ms */
        *futex_word = 1;
        int woken = futex_wake(futex_word, 1);
        printf("[Parent] futex_wake woke %d waiter(s)\n", woken);

        int status;
        waitpid(pid, &status, 0);
        int code = WEXITSTATUS(status);
        if (code == 2) {
            printf("FAILED: child got EFAULT on fresh-page futex_wait\n");
            return 1;
        }
        if (code != 0) {
            printf("FAILED: child exited with code %d\n", code);
            return 1;
        }
        printf("SUCCESS\n");
    }

    return 0;
}
