#include <fcntl.h>
#include <stdio.h>
#include <string.h>
#include <sys/stat.h>
#include <unistd.h>

int main() {
    printf("Starting fchdir and truncate tests...\n");

    const char* dirname = "testdir_fchdir";
    const char* filename = "test_trunc.txt";

    // 1. Create a directory and file
    mkdir(dirname, 0755);

    // 2. Test fchdir
    int dirfd = open(dirname, O_RDONLY | O_DIRECTORY);
    if (dirfd < 0) {
        perror("open dir");
        return 1;
    }

    if (fchdir(dirfd) < 0) {
        perror("fchdir");
        return 1;
    }
    close(dirfd);

    printf("fchdir OK, inside %s\n", dirname);

    // Write some initial data
    int fd = open(filename, O_CREAT | O_RDWR, 0644);
    if (fd < 0) {
        perror("open file");
        return 1;
    }
    write(fd, "hello world 123456789\n", 22);

    // 3. Test ftruncate
    if (ftruncate(fd, 5) < 0) {
        perror("ftruncate");
        return 1;
    }
    close(fd);

    struct stat st;
    if (stat(filename, &st) < 0) {
        perror("stat after ftruncate");
        return 1;
    }

    if (st.st_size != 5) {
        printf("ftruncate failed: size is %lld, expected 5\n", (long long)st.st_size);
        return 1;
    }
    printf("ftruncate OK, size is 5\n");

    // 4. Test truncate
    if (truncate(filename, 100) < 0) {
        perror("truncate");
        return 1;
    }

    if (stat(filename, &st) < 0) {
        perror("stat after truncate");
        return 1;
    }

    if (st.st_size != 100) {
        printf("truncate failed: size is %lld, expected 100\n", (long long)st.st_size);
        return 1;
    }

    printf("truncate OK, size is 100\n");

    // Cleanup
    unlink(filename);

    // We are currently INSIDE testdir_fchdir, so we need to step back out to remove it
    chdir("..");
    rmdir(dirname);

    printf("All checks PASSED!\n");
    return 0;
}
