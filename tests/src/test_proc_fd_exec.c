#define _GNU_SOURCE
#include <errno.h>
#include <fcntl.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/syscall.h>
#include <unistd.h>

extern char** environ;

static int create_script_memfd(void) {
    int fd = syscall(SYS_memfd_create, "proc-fd-exec-script", 0);
    if (fd < 0) {
        perror("memfd_create");
        return -1;
    }

    const char* script = "#!/bin/sh\n"
                         "echo PROC_FD_EXEC_OK\n"
                         "exit 0\n";

    ssize_t n = write(fd, script, strlen(script));
    if (n != (ssize_t)strlen(script)) {
        perror("write script");
        close(fd);
        return -1;
    }

    if (lseek(fd, 0, SEEK_SET) < 0) {
        perror("lseek");
        close(fd);
        return -1;
    }

    return fd;
}

int main(void) {
    printf("--- Testing execve via /proc/self/fd/7 ---\n");

    int fd = create_script_memfd();
    if (fd < 0)
        return 1;

    if (fd != 7) {
        if (dup2(fd, 7) < 0) {
            perror("dup2(fd, 7)");
            close(fd);
            return 1;
        }
        close(fd);
    }

    char path[] = "/proc/self/fd/7";
    char* argv[] = {path, NULL};

    execve(path, argv, environ);
    fprintf(stderr, "FAIL: execve(%s) failed: %s\n", path, strerror(errno));
    return 1;
}
