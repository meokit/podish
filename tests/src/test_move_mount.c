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
#ifndef __NR_move_mount
#define __NR_move_mount 429
#endif

#ifndef OPEN_TREE_CLONE
#define OPEN_TREE_CLONE 1
#endif

#ifndef AT_EMPTY_PATH
#define AT_EMPTY_PATH 0x1000
#endif

#ifndef MOVE_MOUNT_F_EMPTY_PATH
#define MOVE_MOUNT_F_EMPTY_PATH 0x00000004
#endif

#define CHECK(x)                                                                                                       \
    if (!(x)) {                                                                                                        \
        fprintf(stderr, "FAILED: %s at %s:%d (errno=%d: %s)\n", #x, __FILE__, __LINE__, errno, strerror(errno));       \
        exit(1);                                                                                                       \
    }

int open_tree(int dfd, const char* filename, unsigned int flags) {
    return syscall(__NR_open_tree, dfd, filename, flags);
}

int move_mount(int from_dfd, const char* from_pathname, int to_dfd, const char* to_pathname, unsigned int flags) {
    return syscall(__NR_move_mount, from_dfd, from_pathname, to_dfd, to_pathname, flags);
}

void test_move_mount_basic() {
    printf("--- Testing move_mount basic ---\n");

    // 1. Setup source tree
    mkdir("/tmp/move_src", 0777);
    int fd = open("/tmp/move_src/data.txt", O_CREAT | O_WRONLY, 0666);
    CHECK(fd >= 0);
    write(fd, "moved", 5);
    close(fd);

    // 2. Clone it to get a detached mount
    int tree_fd = open_tree(AT_FDCWD, "/tmp/move_src", OPEN_TREE_CLONE);
    CHECK(tree_fd >= 0);

    // 3. Move it to a new location
    mkdir("/tmp/move_target", 0777);
    if (move_mount(tree_fd, "", AT_FDCWD, "/tmp/move_target", MOVE_MOUNT_F_EMPTY_PATH) != 0) {
        perror("move_mount");
        exit(1);
    }

    // 4. Verify visibility
    struct stat st;
    CHECK(stat("/tmp/move_target/data.txt", &st) == 0);

    char buf[10];
    fd = open("/tmp/move_target/data.txt", O_RDONLY);
    CHECK(fd >= 0);
    int n = read(fd, buf, sizeof(buf));
    CHECK(n == 5);
    CHECK(memcmp(buf, "moved", 5) == 0);
    close(fd);

    close(tree_fd);
    printf("PASS: move_mount basic\n");
}

int main() {
    test_move_mount_basic();
    return 0;
}
