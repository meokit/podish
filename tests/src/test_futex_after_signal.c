/*
 * Regression test for Bug #2: futex waiter leaked in _sharedQueues when
 * the waiting thread was interrupted by a signal before being woken.
 *
 * Before the fix: CancelWaitShared was never called on interrupt, so the
 * Waiter object remained in the queue. A subsequent futex_wake would silently
 * consume the defunct waiter instead of the real next waiter, causing later
 * waits to never be woken.
 *
 * Fix: SysFutex now calls CancelWaitShared(physKey, waiter) before returning
 * -ERESTARTSYS, removing the defunct entry from the queue.
 *
 * Test structure:
 *   1. Parent and child share a futex (MAP_SHARED | MAP_ANONYMOUS)
 *   2. Child does futex_wait — parent sends SIGALRM to interrupt it
 *   3. Child catches the signal and loops back to futex_wait again
 *   4. Parent now does a real futex_wake
 *   5. Child must actually wake — not hang because the wake was consumed
 *      by the leaked defunct waiter from step 2
 */
#define _GNU_SOURCE
#include <errno.h>
#include <signal.h>
#include <stdio.h>
#include <stdlib.h>
#include <sys/mman.h>
#include <sys/syscall.h>
#include <sys/wait.h>
#include <unistd.h>

#define SYS_futex 240
#define FUTEX_WAIT 0
#define FUTEX_WAKE 1

static volatile int g_interrupted = 0;

static void sig_handler(int sig) {
    (void)sig;
    g_interrupted = 1;
}

static int futex_wait(int* uaddr, int val) {
    return (int)syscall(SYS_futex, uaddr, FUTEX_WAIT, val, NULL, NULL, 0);
}

static int futex_wake(int* uaddr, int count) {
    return (int)syscall(SYS_futex, uaddr, FUTEX_WAKE, count, NULL, NULL, 0);
}

int main(void) {
    printf("[test_futex_after_signal] Starting\n");

    /* Shared control block:
     *   [0] = futex word  (value child waits on)
     *   [1] = phase flag  (parent signals child to move to next phase)
     */
    int* shared = mmap(NULL, 4096, PROT_READ | PROT_WRITE, MAP_SHARED | MAP_ANONYMOUS, -1, 0);
    if (shared == MAP_FAILED) {
        perror("mmap");
        return 1;
    }

    int* futex_word = &shared[0];
    int* phase = &shared[1];
    *futex_word = 0;
    *phase = 0;

    pid_t pid = fork();
    if (pid < 0) {
        perror("fork");
        return 1;
    }

    if (pid == 0) {
        /* ── Child ────────────────────────────────────────────────────── */
        struct sigaction sa = {.sa_handler = sig_handler};
        sigemptyset(&sa.sa_mask);
        sigaction(SIGALRM, &sa, NULL);

        /* Phase 1: wait, expect to be interrupted by SIGALRM */
        printf("[Child] Phase 1: waiting on futex (expect EINTR)...\n");
        int ret = futex_wait(futex_word, 0);
        if (ret < 0 && errno == EINTR) {
            printf("[Child] Phase 1: interrupted by signal (correct)\n");
        } else if (ret == 0) {
            printf("[Child] Phase 1: woken without signal (also OK)\n");
        } else {
            perror("[Child] Phase 1: unexpected error");
            exit(1);
        }

        /* Signal parent we finished phase 1 */
        *phase = 1;

        /* Phase 2: real wait — must be woken by parent's futex_wake */
        printf("[Child] Phase 2: re-waiting on futex...\n");
        while (*futex_word == 0) {
            ret = futex_wait(futex_word, 0);
            if (ret < 0 && (errno == EAGAIN || errno == EINTR))
                continue;
            if (ret < 0) {
                perror("[Child] Phase 2: futex_wait");
                exit(1);
            }
        }
        printf("[Child] Phase 2: woken! futex_word=%d\n", *futex_word);
        if (*futex_word != 42) {
            printf("[Child] FAILED: expected 42, got %d\n", *futex_word);
            exit(1);
        }
        printf("[Child] SUCCESS\n");
        exit(0);
    } else {
        /* ── Parent ───────────────────────────────────────────────────── */

        /* Step 1: wait for child to enter Phase 1 wait, then interrupt it */
        usleep(80000); /* 80ms — child should be in futex_wait by now */
        printf("[Parent] Sending SIGALRM to interrupt child's wait\n");
        kill(pid, SIGALRM);

        /* Step 2: wait for child to complete phase 1 */
        while (*phase == 0)
            usleep(5000);
        printf("[Parent] Child is in Phase 2, doing real wake\n");

        usleep(50000); /* 50ms — let child enter futex_wait for phase 2 */

        /* This wake must reach the REAL phase-2 waiter, not a leaked one */
        *futex_word = 42;
        int woken = futex_wake(futex_word, 1);
        printf("[Parent] futex_wake returned %d\n", woken);

        /* Give child a moment in case it's polling */
        usleep(300000); /* 300ms */

        int status;
        int r = waitpid(pid, &status, WNOHANG);
        if (r == 0) {
            /* Child is still running — it never woke up → leaked waiter bug */
            kill(pid, SIGKILL);
            waitpid(pid, NULL, 0);
            printf("FAILED: child hung after signal-interrupted wait (waiter leak)\n");
            return 1;
        }

        int code = WEXITSTATUS(status);
        if (code != 0) {
            printf("FAILED: child exited with code %d\n", code);
            return 1;
        }
        printf("SUCCESS\n");
    }

    return 0;
}
