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

void test_bind_mount_dir() {
    printf("--- Testing bind mount directory ---\n");
    mkdir("/tmp/src_dir", 0777);
    mkdir("/tmp/target_dir", 0777);

    // Create a file in src
    int fd = open("/tmp/src_dir/hello.txt", O_CREAT | O_WRONLY, 0666);
    CHECK(fd >= 0);
    write(fd, "hello", 5);
    close(fd);

    // Bind mount
    if (mount("/tmp/src_dir", "/tmp/target_dir", NULL, MS_BIND, NULL) != 0) {
        perror("mount MS_BIND");
        exit(1);
    }

    // Check if file is visible at target
    struct stat st;
    CHECK(stat("/tmp/target_dir/hello.txt", &st) == 0);
    CHECK(S_ISREG(st.st_mode));

    // Verify content
    char buf[10];
    fd = open("/tmp/target_dir/hello.txt", O_RDONLY);
    CHECK(fd >= 0);
    int n = read(fd, buf, sizeof(buf));
    CHECK(n == 5);
    CHECK(memcmp(buf, "hello", 5) == 0);
    close(fd);

    printf("PASS: bind mount directory\n");
}

void test_bind_mount_file() {
    printf("--- Testing bind mount file ---\n");
    int fd = open("/tmp/src_file", O_CREAT | O_WRONLY, 0666);
    CHECK(fd >= 0);
    write(fd, "world", 5);
    close(fd);

    fd = open("/tmp/target_file", O_CREAT | O_WRONLY, 0666);
    CHECK(fd >= 0);
    close(fd);

    // Bind mount file
    if (mount("/tmp/src_file", "/tmp/target_file", NULL, MS_BIND, NULL) != 0) {
        perror("mount MS_BIND file");
        exit(1);
    }

    // Verify content at target
    char buf[10];
    fd = open("/tmp/target_file", O_RDONLY);
    CHECK(fd >= 0);
    int n = read(fd, buf, sizeof(buf));
    CHECK(n == 5);
    CHECK(memcmp(buf, "world", 5) == 0);
    close(fd);

    printf("PASS: bind mount file\n");
}

void test_bind_mount_readonly() {
    printf("--- Testing readonly bind mount ---\n");
    mkdir("/tmp/ro_src", 0777);
    mkdir("/tmp/ro_target", 0777);

    // Initial bind
    CHECK(mount("/tmp/ro_src", "/tmp/ro_target", NULL, MS_BIND, NULL) == 0);

    // Remount as read-only
    if (mount(NULL, "/tmp/ro_target", NULL, MS_BIND | MS_REMOUNT | MS_RDONLY, NULL) != 0) {
        perror("mount MS_RDONLY");
        // Some systems/kernels might require different flags for this, but standard is MS_BIND|MS_REMOUNT|MS_RDONLY
        exit(1);
    }

    // Try to create a file - should fail
    int fd = open("/tmp/ro_target/fail.txt", O_CREAT | O_WRONLY, 0666);
    if (fd >= 0) {
        fprintf(stderr, "FAILED: open for write succeeded on RO mount\n");
        close(fd);
        exit(1);
    }
    CHECK(errno == EROFS);

    printf("PASS: readonly bind mount\n");
}

int main() {
    test_bind_mount_dir();
    test_bind_mount_file();
    test_bind_mount_readonly();
    return 0;
}
