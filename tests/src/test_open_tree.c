#define _GNU_SOURCE
#include <errno.h>
#include <fcntl.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <sys/syscall.h>
#include <unistd.h>

/* x86 syscall numbers */
#ifndef __NR_open_tree
#define __NR_open_tree 428
#endif

#ifndef OPEN_TREE_CLONE
#define OPEN_TREE_CLONE 1
#endif

#ifndef AT_EMPTY_PATH
#define AT_EMPTY_PATH 0x1000
#endif

#define CHECK(x)                                                                                                       \
    if (!(x)) {                                                                                                        \
        fprintf(stderr, "FAILED: %s at %s:%d (errno=%d: %s)\n", #x, __FILE__, __LINE__, errno, strerror(errno));       \
        exit(1);                                                                                                       \
    }

int open_tree(int dfd, const char* filename, unsigned int flags) {
    return syscall(__NR_open_tree, dfd, filename, flags);
}

void test_open_tree_clone() {
    printf("--- Testing open_tree clone ---\n");
    mkdir("/tmp/tree_src", 0777);
    int fd = open("/tmp/tree_src/file.txt", O_CREAT | O_WRONLY, 0666);
    CHECK(fd >= 0);
    write(fd, "tree", 4);
    close(fd);

    // Create a detached clone of the tree
    int tree_fd = open_tree(AT_FDCWD, "/tmp/tree_src", OPEN_TREE_CLONE);
    if (tree_fd < 0) {
        perror("open_tree OPEN_TREE_CLONE");
        exit(1);
    }

    // Verify we can access files through the tree FD using openat
    int file_fd = openat(tree_fd, "file.txt", O_RDONLY);
    CHECK(file_fd >= 0);
    char buf[10];
    int n = read(file_fd, buf, sizeof(buf));
    CHECK(n == 4);
    CHECK(memcmp(buf, "tree", 4) == 0);
    close(file_fd);
    close(tree_fd);

    printf("PASS: open_tree clone\n");
}

void test_open_tree_invalid() {
    printf("--- Testing open_tree invalid flags ---\n");
    // Invalid flag combination: CLONE without source path
    int res = open_tree(AT_FDCWD, "/nonexistent", OPEN_TREE_CLONE);
    CHECK(res == -1);
    CHECK(errno == ENOENT);
    printf("PASS: open_tree invalid flags\n");
}

int main() {
    test_open_tree_clone();
    test_open_tree_invalid();
    return 0;
}
