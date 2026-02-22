#define _GNU_SOURCE
#include <errno.h>
#include <fcntl.h>
#include <sched.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/personality.h>
#include <sys/prctl.h>
#include <sys/resource.h>
#include <sys/stat.h>
#include <sys/syscall.h>
#include <sys/time.h>
#include <sys/vfs.h>
#include <unistd.h>

#ifndef SYS_getcpu
#define SYS_getcpu 318
#endif

#ifndef SYS_get_thread_area
#define SYS_get_thread_area 244
#endif

struct user_desc {
    unsigned int entry_number;
    unsigned int base_addr;
    unsigned int limit;
    unsigned int seg_32bit : 1;
    unsigned int contents : 2;
    unsigned int read_exec_only : 1;
    unsigned int limit_in_pages : 1;
    unsigned int seg_not_present : 1;
    unsigned int useable : 1;
};

// Fallback for getcpu if not in headers
static int my_getcpu(unsigned int* cpu, unsigned int* node) {
    return syscall(SYS_getcpu, cpu, node, NULL);
}

int main() {
    printf("Starting Batch 2 low-hanging fruit tests...\n");

    // 1. nice, getpriority, setpriority
    errno = 0;
    int prio = getpriority(PRIO_PROCESS, 0);
    printf("Current priority: %d (errno=%d)\n", prio, errno);

    if (nice(5) == -1 && errno != 0) {
        perror("nice failed");
    } else {
        printf("nice(5) called successfully\n");
    }

    if (setpriority(PRIO_PROCESS, 0, 10) == -1) {
        perror("setpriority failed");
    } else {
        printf("setpriority(10) called successfully\n");
    }

    // 2. personality
    int pers = personality(0xffffffff);
    printf("Current personality: %d\n", pers);
    if (personality(PER_LINUX) == -1) {
        perror("personality(PER_LINUX) failed");
    } else {
        printf("personality(PER_LINUX) set successfully\n");
    }

    // 3. getcpu
    unsigned int cpu, node;
    if (my_getcpu(&cpu, &node) == -1) {
        perror("getcpu failed");
    } else {
        printf("getcpu: CPU=%u, Node=%u\n", cpu, node);
    }

    // 4. prctl (PR_SET_NAME, PR_GET_NAME)
    char name[16];
    if (prctl(PR_SET_NAME, "test-proc", 0, 0, 0) == -1) {
        perror("prctl(PR_SET_NAME) failed");
    } else {
        printf("prctl(PR_SET_NAME) to 'test-proc' successful\n");
    }

    if (prctl(PR_GET_NAME, name, 0, 0, 0) == -1) {
        perror("prctl(PR_GET_NAME) failed");
    } else {
        printf("prctl(PR_GET_NAME) returned: '%s'\n", name);
    }

    // 5. readahead, fadvise64 (no-ops)
    int fd = open("test_hints.txt", O_CREAT | O_RDWR | O_TRUNC, 0644);
    if (fd != -1) {
        write(fd, "hello", 5);
        if (readahead(fd, 0, 5) == -1) {
            perror("readahead failed");
        } else {
            printf("readahead called successfully\n");
        }
        if (posix_fadvise(fd, 0, 5, POSIX_FADV_NORMAL) == -1) {
            perror("posix_fadvise failed");
        } else {
            printf("posix_fadvise called successfully\n");
        }
        close(fd);
    }

    // 6. utimes
    struct timeval tv[2];
    tv[0].tv_sec = 1234567890;
    tv[0].tv_usec = 0;
    tv[1].tv_sec = 1234567890;
    tv[1].tv_usec = 0;
    if (utimes("test_hints.txt", tv) == -1) {
        perror("utimes failed");
    } else {
        printf("utimes called successfully\n");
        struct stat st;
        if (stat("test_hints.txt", &st) == 0) {
            printf("Modified time: %lld\n", (long long)st.st_mtime);
        }
    }

    // 7. prlimit64
    struct rlimit rlim;
    if (getrlimit(RLIMIT_NOFILE, &rlim) == -1) {
        perror("getrlimit failed");
    } else {
        printf("RLIMIT_NOFILE: soft=%ld, hard=%ld\n", (long)rlim.rlim_cur, (long)rlim.rlim_max);
    }

    // 8. statfs
    struct statfs sfs;
    if (statfs(".", &sfs) == -1) {
        perror("statfs failed");
    } else {
        printf("statfs: f_type=0x%lx, f_bsize=%ld\n", (long)sfs.f_type, (long)sfs.f_bsize);
    }

    // 9. get_thread_area
    struct user_desc u_info;
    memset(&u_info, 0, sizeof(u_info));
    u_info.entry_number = 0;
    if (syscall(SYS_get_thread_area, &u_info) == -1) {
        perror("get_thread_area failed");
    } else {
        printf("get_thread_area: base_addr=0x%x\n", u_info.base_addr);
    }

    printf("Batch 2 tests completed.\n");
    return 0;
}
