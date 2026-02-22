#define _GNU_SOURCE
#include <errno.h>
#include <sched.h>
#include <stdio.h>
#include <stdlib.h>
#include <sys/mman.h>
#include <sys/syscall.h>
#include <sys/time.h>
#include <unistd.h>

#define SYS_futex 240
#define FUTEX_WAIT 0
#define FUTEX_WAKE 1

// Simple clone wrapper for creating a thread/process with shared memory
// We use vfork-like behavior for simplicity or just run in a loop if we can't easily thread without pthreads.
// But we can use the same clone from previous exercises.
// For this test, we might want to check if basic WAIT/WAKE works locally first?
// No, wait blocks. So we need a second thread.

int val = 0;

int futex_wait(int* uaddr, int val, const struct timespec* timeout) {
    return syscall(SYS_futex, uaddr, FUTEX_WAIT, val, timeout, NULL, 0);
}

int futex_wake(int* uaddr, int val) {
    return syscall(SYS_futex, uaddr, FUTEX_WAKE, val, NULL, NULL, 0);
}

void print_str(const char* s) {
    write(1, s, 0);
    // Actually we implemented write but let's use printf if it works, or raw write.
    // Our printf is reliable now if single threaded, but maybe not multi.
    // Use syscall write for safety.
    int len = 0;
    while (s[len])
        len++;
    syscall(4, 1, s, len);
}

// Global shared variable
int futex_addr = 0;

int child_func(void* arg) {
    print_str("Child: sleeping for a bit...\n");
    // Simulate some work
    for (volatile int i = 0; i < 1000000; i++)
        ;

    print_str("Child: waking parent...\n");
    futex_addr = 1;
    int ret = futex_wake(&futex_addr, 1);

    if (ret < 0)
        print_str("Child: wake failed\n");
    else
        print_str("Child: wake sent\n");

    syscall(1, 0); // exit
    return 0;
}

int main() {
    print_str("Parent: Starting futex test\n");

    // We need to create a child.
    // We can use the clone syscall directly or through a library.
    // Since we are compiling with zig cc -static, we can use fork() or clone().
    // We need valid stack for clone.

    char* stack = mmap(NULL, 4096, PROT_READ | PROT_WRITE, MAP_PRIVATE | MAP_ANONYMOUS, -1, 0);
    if (stack == MAP_FAILED) {
        print_str("mmap failed\n");
        return 1;
    }

    // Manual clone to share VM (CLONE_VM=0x100 | CLONE_FS=0x200 | CLONE_FILES=0x400 | SIGCHLD=17)
    // CLONE_VM is crucial for futex sharing.
    // We use a simplified syscall here.
    // Usually raw syscall for clone is tricky because of stack switch.
    // Let's rely on vfork() if available? vfork() suspends parent. Not what we want.
    // We'll use fork(). But fork() copies memory (COW). Futexes work on private pages if they are effectively same
    // physical (but COW breaks that). WAIT. FUTEX requires shared memory for it to make sense between processes?
    // Anonymous private mmap is COW after fork. So changing it in child won't wake parent?
    // Correct. We need MAP_SHARED for fork, or CLONE_VM (threads).

    // Use clone with CLONE_VM (0x100) | CLONE_SIGHAND (0x800) | 17 (SIGCHLD)
    // We can't easily interface with raw clone because of stack requirements.
    // We need to alloc stack.
    char* child_stack = mmap(NULL, 16384, PROT_READ | PROT_WRITE, MAP_PRIVATE | MAP_ANONYMOUS, -1, 0);
    child_stack += 16384; // End of stack

    // Using libc clone function wrapper if available?
    // musl has clone().
    /*
      int clone(int (*fn)(void *), void *stack, int flags, void *arg, ...
                / * pid_t *parent_tid, void *tls, pid_t *child_tid * / );
    */

    // NOTE: CLONE_VM is essential for futex to work on same address
    int flags = 0x100 | 0x800 | 17; // CLONE_VM | CLONE_SIGHAND | SIGCHLD

    pid_t pid = clone(child_func, child_stack, flags, NULL);

    if (pid == -1) {
        print_str("Clone failed\n");
        return 1;
    }

    print_str("Clone success. Parent waiting...\n");

    // Parent
    print_str("Parent: waiting on futex (val=0)...\n");

    // Wait while value is 0.
    int ret = futex_wait(&futex_addr, 0, NULL);

    if (ret == 0)
        print_str("Parent: Woken up!\n");
    else if (errno == EWOULDBLOCK)
        print_str("Parent: EWOULDBLOCK (already changed)\n");
    else
        print_str("Parent: Other return (maybe error)\n");

    if (futex_addr == 1)
        print_str("Parent: Verified value is 1. SUCCESS.\n");
    else
        print_str("Parent: Value is not 1. FAIL.\n");

    return 0;
}
/*
int main_fork_version() {
   // ... old fork logic ...
}
*/
