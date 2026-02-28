#define _GNU_SOURCE
#include <linux/futex.h>
#include <stddef.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <sys/mman.h>
#include <sys/syscall.h>
#include <sys/wait.h>
#include <unistd.h>

#define FUTEX_OWNER_DIED 0x40000000
#define FUTEX_WAITERS 0x80000000
#define FUTEX_TID_MASK 0x3fffffff

struct robust_list_32 {
    uint32_t next;
};

struct robust_list_head_32 {
    struct robust_list_32 list;
    int32_t futex_offset;
    uint32_t list_op_pending;
};

struct lock_node {
    struct robust_list_32 list; // The list node
    uint32_t futex_word;        // The actual lock word
};

int main() {
    struct lock_node* node =
        mmap(NULL, sizeof(struct lock_node), PROT_READ | PROT_WRITE, MAP_SHARED | MAP_ANONYMOUS, -1, 0);
    if (node == MAP_FAILED) {
        perror("mmap");
        return 1;
    }
    node->futex_word = 0;
    node->list.next = 0;

    pid_t child = fork();
    if (child == -1) {
        perror("fork");
        return 1;
    }

    if (child == 0) {
        // Child process
        pid_t tid = syscall(SYS_gettid);

        // Setup robust list
        struct robust_list_head_32* head = malloc(sizeof(struct robust_list_head_32));

        head->list.next = (uint32_t)(uintptr_t)&head->list;
        head->futex_offset = (int32_t)offsetof(struct lock_node, futex_word);
        head->list_op_pending = 0;

        printf("[Child] Registering robust list head at %p, offset=%d\n", head, head->futex_offset);
        if (syscall(SYS_set_robust_list, head, sizeof(*head)) != 0) {
            perror("set_robust_list");
            exit(1);
        }

        // Simulate acquiring a robust mutex
        // 1. Mark operation as pending
        head->list_op_pending = (uint32_t)(uintptr_t)&node->list;

        // 2. Add to list
        node->list.next = head->list.next;
        head->list.next = (uint32_t)(uintptr_t)&node->list;

        // 3. Clear pending
        head->list_op_pending = 0;

        // 4. Set owner
        printf("[Child] Acquiring futex, tid=%d\n", tid);
        node->futex_word = tid;

        // Exit without unlocking
        printf("[Child] Exiting without unlocking futex.\n");
        exit(0);
    }

    // Parent
    int status;
    waitpid(child, &status, 0);

    printf("[Parent] Child exited with status 0x%x\n", status);

    // Check futex word
    uint32_t val = node->futex_word;
    printf("[Parent] Futex word is now 0x%08x\n", val);

    if ((val & FUTEX_OWNER_DIED) != 0) {
        printf("[Parent] SUCCESS: FUTEX_OWNER_DIED bit is set!\n");
        return 0;
    } else {
        printf("[Parent] FAILED: FUTEX_OWNER_DIED bit is NOT set.\n");
        return 1;
    }
}
