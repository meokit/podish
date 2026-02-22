/* tests/linux/syscall_test.c */
#define _LARGEFILE64_SOURCE
#include <dirent.h>
#include <errno.h>
#include <fcntl.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <sys/syscall.h>
#include <unistd.h>

#define ASSERT(cond, msg)                                                                                              \
    do {                                                                                                               \
        if (!(cond)) {                                                                                                 \
            printf("[FAIL] %s (errno=%d)\n", msg, errno);                                                              \
            _exit(1);                                                                                                  \
        }                                                                                                              \
        printf("[PASS] %s\n", msg);                                                                                    \
    } while (0)

void test_stat() {
    struct stat64 st;
    int res = stat64(".", &st);
    ASSERT(res == 0, "stat64 .");
    ASSERT(S_ISDIR(st.st_mode), ". is directory");

    // Create a file and stat it
    int fd = open("test.tmp", O_WRONLY | O_CREAT | O_TRUNC, 0644);
    ASSERT(fd >= 0, "open test.tmp");
    write(fd, "hello", 5);
    close(fd);

    res = stat64("test.tmp", &st);
    ASSERT(res == 0, "stat64 test.tmp");
    ASSERT(S_ISREG(st.st_mode), "test.tmp is regular file");
    ASSERT(st.st_size == 5, "test.tmp size is 5");

    unlink("test.tmp");
}

void test_getdents() {
    int fd = open(".", O_RDONLY | O_DIRECTORY);
    ASSERT(fd >= 0, "open .");

    char buf[1024];
    // SYS_getdents64 is 220
    int nread = syscall(SYS_getdents64, fd, buf, 1024);
    ASSERT(nread > 0, "getdents64 .");
    printf("getdents64 returned %d bytes\n", nread);

    close(fd);
}

int main() {
    printf("Starting Syscall Verification...\n");
    test_stat();
    test_getdents();
    printf("All Tests Passed!\n");
    return 0;
}
