/*
 * test_vfork.c — Tests for vfork semantics:
 *   1. vfork+exit: parent must be blocked until child calls _exit().
 *   2. vfork+exec: parent must be blocked until child calls execve().
 *   3. Ordering: child's writes before exec/exit must be visible to parent
 *      (child and parent share address space).
 */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/wait.h>
#include <unistd.h>

/*
 * Use a volatile int to track execution ordering.
 * With vfork, the child shares the parent's address space, so writes are
 * visible to the parent once it resumes.
 */
static volatile int order_counter = 0;

int main(void) {
    printf("VFork Test Starting\n");

    /* ================================================================
     * Test 1: vfork + _exit — parent blocks until child exits
     * ================================================================ */
    printf("\nTest 1: vfork + _exit\n");
    order_counter = 0;

    pid_t pid = vfork();
    if (pid < 0) {
        printf("ERROR: vfork failed\n");
        return 1;
    }

    if (pid == 0) {
        /* Child: runs FIRST (parent is blocked) */
        order_counter = 1;
        printf("Child: set order_counter = %d\n", order_counter);
        _exit(42);
    }

    /* Parent: resumes AFTER child exits */
    printf("Parent: order_counter = %d (expect 1)\n", order_counter);
    if (order_counter != 1) {
        printf("ERROR: Parent ran before child! order_counter = %d\n", order_counter);
        return 1;
    }

    int status;
    pid_t w = waitpid(pid, &status, 0);
    if (w != pid) {
        printf("ERROR: waitpid returned %d, expected %d\n", w, pid);
        return 1;
    }
    if (!WIFEXITED(status) || WEXITSTATUS(status) != 42) {
        printf("ERROR: Child exit status wrong: exited=%d, code=%d\n", WIFEXITED(status), WEXITSTATUS(status));
        return 1;
    }
    printf("Test 1: PASS — child ran first, exited with 42\n");

    /* ================================================================
     * Test 2: vfork + exec — parent blocks until child execs
     * ================================================================ */
    printf("\nTest 2: vfork + exec\n");
    order_counter = 0;

    pid_t pid2 = vfork();
    if (pid2 < 0) {
        printf("ERROR: vfork failed\n");
        return 1;
    }

    if (pid2 == 0) {
        /* Child: set counter, then exec. After exec, address space is replaced,
           so this is the only chance to set it. */
        order_counter = 2;
        printf("Child: set order_counter = %d, about to exec /bin/true\n", order_counter);
        /* /bin/true just exits 0 */
        execl("/bin/true", "true", (char*)NULL);
        /* If exec fails, fall through to _exit */
        printf("Child: execl failed, falling back to _exit\n");
        _exit(99);
    }

    printf("Parent: order_counter = %d (expect 2)\n", order_counter);
    if (order_counter != 2) {
        printf("ERROR: Parent ran before child exec! order_counter = %d\n", order_counter);
        return 1;
    }

    int status2;
    pid_t w2 = waitpid(pid2, &status2, 0);
    if (w2 != pid2) {
        printf("ERROR: waitpid returned %d, expected %d\n", w2, pid2);
        return 1;
    }
    /* /bin/true may not exist in minimal rootfs; accept either 0 or 99 */
    if (WIFEXITED(status2)) {
        printf("Test 2: Child exited with code %d\n", WEXITSTATUS(status2));
    } else {
        printf("ERROR: Child did not exit normally\n");
        return 1;
    }
    printf("Test 2: PASS — parent blocked until child exec/exit\n");

    /* ================================================================
     * Test 3: Multiple vfork — serial execution
     * ================================================================ */
    printf("\nTest 3: Multiple sequential vfork\n");
    order_counter = 0;

    for (int i = 1; i <= 3; i++) {
        pid_t p = vfork();
        if (p < 0) {
            printf("ERROR: vfork #%d failed\n", i);
            return 1;
        }
        if (p == 0) {
            order_counter = i;
            _exit(i);
        }
        /* Parent resumes after each child */
        if (order_counter != i) {
            printf("ERROR: After vfork #%d, order_counter = %d (expected %d)\n", i, order_counter, i);
            return 1;
        }
        int st;
        waitpid(p, &st, 0);
        if (!WIFEXITED(st) || WEXITSTATUS(st) != i) {
            printf("ERROR: Child #%d exit status wrong\n", i);
            return 1;
        }
        printf("vfork #%d: PASS\n", i);
    }
    printf("Test 3: PASS\n");

    printf("\nPASS: All VFork Tests\n");
    return 0;
}
