#define _GNU_SOURCE
#include <errno.h>
#include <fcntl.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <unistd.h>

void test_magic_fd_reopen() {
    printf("--- Testing /proc/self/fd magic link reopen ---\n");

    // Create a temporary file
    char tmp_path[] = "/tmp/test_magic_XXXXXX";
    int fd = mkstemp(tmp_path);
    if (fd < 0) {
        perror("mkstemp");
        exit(1);
    }

    // Write something to it
    const char* msg = "Magic Link Test Data";
    if (write(fd, msg, strlen(msg)) != strlen(msg)) {
        perror("write");
        exit(1);
    }

    // Unlink the file - it no longer exists on the filesystem path
    if (unlink(tmp_path) < 0) {
        perror("unlink");
        exit(1);
    }

    printf("Unlinked %s, but fd %d is still open\n", tmp_path, fd);

    // Try to open it via /proc/self/fd/N
    char proc_fd_path[64];
    snprintf(proc_fd_path, sizeof(proc_fd_path), "/proc/self/fd/%d", fd);

    int fd2 = open(proc_fd_path, O_RDONLY);
    if (fd2 < 0) {
        perror("open /proc/self/fd/N");
        printf("FAIL: Could not reopen unlinked file via magic link\n");
        exit(1);
    }

    printf("Successfully reopened via %s\n", proc_fd_path);

    // CRITICAL: Verify fd2 has its own file offset (not shared with fd)
    // open() via /proc/self/fd/N should create a NEW file description,
    // so fd2's offset should start at 0, NOT at the end where fd left off.
    off_t fd2_offset = lseek(fd2, 0, SEEK_CUR);
    if (fd2_offset != 0) {
        printf("FAIL: fd2 offset is %lld, expected 0 (file description not independent)\n", (long long)fd2_offset);
        exit(1);
    }
    printf("PASS: fd2 has independent offset (starts at 0)\n");

    // Read and verify data
    char buf[64];
    memset(buf, 0, sizeof(buf));
    int n = read(fd2, buf, sizeof(buf));
    if (n < 0) {
        perror("read fd2");
        exit(1);
    }
    printf("Read %d bytes from fd2\n", n);

    if (n == 0) {
        printf("FAIL: Read 0 bytes - file data lost or offset wrong\n");
        exit(1);
    }

    if (strcmp(buf, msg) == 0) {
        printf("PASS: Content verified: %s\n", buf);
    } else {
        printf("FAIL: Content mismatch! Expected '%s', got '%s'\n", msg, buf);
        exit(1);
    }

    close(fd);
    close(fd2);
}

int main() {
    test_magic_fd_reopen();
    printf("Proc FD Magic Link test PASSED!\n");
    return 0;
}