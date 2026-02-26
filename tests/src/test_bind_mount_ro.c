#define _GNU_SOURCE
#include <errno.h>
#include <fcntl.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/mount.h>
#include <sys/stat.h>
#include <unistd.h>

#define CHECK(x)                                                                                                       \
    if (!(x)) {                                                                                                        \
        fprintf(stderr, "FAILED: %s at %s:%d (errno=%d: %s)\n", #x, __FILE__, __LINE__, errno, strerror(errno));       \
        exit(1);                                                                                                       \
    }

void test_bind_mount_different_permissions() {
    printf("--- Testing bind mount with different permissions ---\n");

    // 1. Setup source directory and file
    mkdir("/tmp/src_perm", 0777);
    int fd = open("/tmp/src_perm/file.txt", O_CREAT | O_WRONLY, 0666);
    CHECK(fd >= 0);
    write(fd, "test", 4);
    close(fd);

    // 2. Create target directory
    mkdir("/tmp/target_perm", 0777);

    // 3. Bind mount
    if (mount("/tmp/src_perm", "/tmp/target_perm", NULL, MS_BIND, NULL) != 0) {
        perror("mount MS_BIND");
        exit(1);
    }

    // 4. Remount target as read-only
    if (mount(NULL, "/tmp/target_perm", NULL, MS_BIND | MS_REMOUNT | MS_RDONLY, NULL) != 0) {
        perror("mount MS_REMOUNT");
        exit(1);
    }

    // 5. Verify source is still writable
    fd = open("/tmp/src_perm/file.txt", O_WRONLY | O_APPEND);
    CHECK(fd >= 0);
    write(fd, "x", 1);
    close(fd);
    printf("Source is writable: PASS\n");

    // 6. Verify target is read-only
    fd = open("/tmp/target_perm/file.txt", O_WRONLY | O_APPEND);
    if (fd >= 0) {
        fprintf(stderr, "FAILED: target should be read-only but opened for write\n");
        close(fd);
        exit(1);
    }
    CHECK(errno == EROFS);
    printf("Target is read-only: PASS\n");

    // 7. Try to create new file in target (should fail)
    fd = open("/tmp/target_perm/newfile.txt", O_CREAT | O_WRONLY, 0666);
    if (fd >= 0) {
        fprintf(stderr, "FAILED: should not create file in read-only mount\n");
        close(fd);
        exit(1);
    }
    CHECK(errno == EROFS);
    printf("Cannot create in read-only mount: PASS\n");

    // 8. Try to create new file in source (should succeed)
    fd = open("/tmp/src_perm/newfile.txt", O_CREAT | O_WRONLY, 0666);
    CHECK(fd >= 0);
    write(fd, "new", 3);
    close(fd);
    printf("Can create in source mount: PASS\n");

    printf("PASS: bind mount with different permissions\n");
}

int main() {
    test_bind_mount_different_permissions();
    return 0;
}
