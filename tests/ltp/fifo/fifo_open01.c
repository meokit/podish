/* SPDX-License-Identifier: GPL-2.0-or-later */
#include "fifo_fixture.h"

static void pairing(int child_mode)
{
    int control[2], fd;
    char c;
    struct pollfd p;
    SAFE_PIPE(control);
    pid_t pid = SAFE_FORK();
    if (!pid) {
        alarm(5);
        close(control[0]);
        if (write(control[1], "S", 1) != 1) _exit(1);
        fd = open(FIFO, child_mode);
        if (fd < 0) _exit(2);
        if (write(control[1], "D", 1) != 1) _exit(3);
        /* Hold the endpoint until the parent acknowledges successful pairing. */
        if (child_mode == O_RDONLY && read(fd, &c, 1) != 1) _exit(4);
        if (child_mode == O_WRONLY && write(fd, "x", 1) != 1) _exit(5);
        close(fd);
        _exit(0);
    }
    close(control[1]);
    SAFE_READ(1, control[0], &c, 1);
    p = (struct pollfd){.fd = control[0], .events = POLLIN};
    check(poll(&p, 1, 50) == 0, child_mode == O_RDONLY ?
        "reader open waits for a writer" : "writer open waits for a reader");
    fd = SAFE_OPEN(FIFO, child_mode == O_RDONLY ? O_WRONLY : O_RDONLY);
    SAFE_READ(1, control[0], &c, 1);
    if (child_mode == O_RDONLY) SAFE_WRITE(1, fd, "x", 1);
    else SAFE_READ(1, fd, &c, 1);
    check(child_ok(pid), "blocking opens pair and exchange data");
    SAFE_CLOSE(fd);
    SAFE_CLOSE(control[0]);
}

static void run(unsigned int n)
{
    int r, w, path;
    char c;
    fresh_fifo();
    if (n < 2) {
        pairing(n ? O_WRONLY : O_RDONLY);
        return;
    }
    path = SAFE_OPEN(FIFO, O_PATH);
    TST_EXP_FAIL(open(FIFO, O_WRONLY | O_NONBLOCK), ENXIO,
        "O_PATH is not an active FIFO reader");
    r = SAFE_OPEN(FIFO, O_RDONLY | O_NONBLOCK);
    check(read(r, &c, 1) == 0, "new nonblocking reader without writer observes EOF");
    w = openat(AT_FDCWD, FIFO, O_WRONLY | O_NONBLOCK);
    check(w >= 0, "nonblocking writer succeeds with a reader (openat)");
    if (w >= 0) SAFE_CLOSE(w);
    SAFE_CLOSE(r);
    SAFE_CLOSE(path);
    r = SAFE_OPEN(FIFO, O_RDWR | O_NONBLOCK);
    check(read(r, &c, 1) == -1 && errno == EAGAIN, "O_RDWR supplies its own live writer");
    SAFE_CLOSE(r);
}
static struct tst_test test = {
    .test = run, .tcnt = 3, .needs_tmpdir = 1, .forks_child = 1,
};
