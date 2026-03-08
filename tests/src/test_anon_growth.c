#include <errno.h>
#include <inttypes.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/types.h>
#include <sys/wait.h>
#include <unistd.h>

static long read_anon_pages_kb(void) {
    FILE* f = fopen("/proc/meminfo", "r");
    if (!f) {
        perror("fopen(/proc/meminfo)");
        return -1;
    }

    char line[256];
    long value = -1;
    while (fgets(line, sizeof(line), f)) {
        if (strncmp(line, "AnonPages:", 10) != 0) {
            continue;
        }
        long kb = -1;
        if (sscanf(line, "AnonPages: %ld kB", &kb) == 1) {
            value = kb;
            break;
        }
    }

    fclose(f);
    return value;
}

static int run_noop_loop(int n) {
    volatile int sink = 0;
    for (int i = 0; i < n; i++) {
        sink += i;
    }
    return sink == -1;
}

static int run_fork_exit_loop(int n) {
    for (int i = 0; i < n; i++) {
        pid_t pid = fork();
        if (pid < 0) {
            perror("fork");
            return -1;
        }
        if (pid == 0) {
            _exit(0);
        }
        int st = 0;
        if (waitpid(pid, &st, 0) < 0) {
            perror("waitpid");
            return -1;
        }
    }
    return 0;
}

static int run_fork_exec_loop(int n) {
    for (int i = 0; i < n; i++) {
        pid_t pid = fork();
        if (pid < 0) {
            perror("fork");
            return -1;
        }
        if (pid == 0) {
            execl("/bin/true", "true", (char*)NULL);
            perror("execl(/bin/true)");
            _exit(127);
        }
        int st = 0;
        if (waitpid(pid, &st, 0) < 0) {
            perror("waitpid");
            return -1;
        }
    }
    return 0;
}

static void print_delta(const char* label, long before, long after) {
    printf("%s before=%ld after=%ld delta=%ld\n", label, before, after, after - before);
}

static uintptr_t read_heap_size_bytes(void) {
    FILE* f = fopen("/proc/self/maps", "r");
    if (!f)
        return 0;

    char line[512];
    uintptr_t size = 0;
    while (fgets(line, sizeof(line), f)) {
        if (strstr(line, "[heap]") == NULL)
            continue;
        unsigned long start = 0, end = 0;
        if (sscanf(line, "%lx-%lx", &start, &end) == 2 && end > start) {
            size += (uintptr_t)(end - start);
        }
    }
    fclose(f);
    return size;
}

int main(int argc, char** argv) {
    int loops = 20;
    if (argc > 1) {
        loops = atoi(argv[1]);
        if (loops <= 0) {
            loops = 20;
        }
    }

    void* brk0 = sbrk(0);
    uintptr_t heap0 = read_heap_size_bytes();

    long b0 = read_anon_pages_kb();
    if (b0 < 0) {
        fprintf(stderr, "failed to read AnonPages baseline\n");
        return 1;
    }

    if (run_noop_loop(loops * 1000) < 0) {
        return 2;
    }
    long a0 = read_anon_pages_kb();
    if (a0 < 0)
        return 1;
    print_delta("NOOP", b0, a0);

    long b1 = read_anon_pages_kb();
    if (b1 < 0)
        return 1;
    if (run_fork_exit_loop(loops) < 0) {
        return 3;
    }
    long a1 = read_anon_pages_kb();
    if (a1 < 0)
        return 1;
    print_delta("FORK_EXIT", b1, a1);

    long b2 = read_anon_pages_kb();
    if (b2 < 0)
        return 1;
    if (run_fork_exec_loop(loops) < 0) {
        return 4;
    }
    long a2 = read_anon_pages_kb();
    if (a2 < 0)
        return 1;
    print_delta("FORK_EXEC_TRUE", b2, a2);

    void* brk1 = sbrk(0);
    uintptr_t heap1 = read_heap_size_bytes();
    printf("HEAP brk_before=%p brk_after=%p brk_delta=%" PRIuPTR " heapmap_before=%" PRIuPTR " heapmap_after=%" PRIuPTR
           " heapmap_delta=%" PRIuPTR "\n",
           brk0, brk1, (uintptr_t)((char*)brk1 - (char*)brk0), heap0, heap1, heap1 - heap0);

    return 0;
}
