/* SPDX-License-Identifier: GPL-2.0-or-later */
#include "fifo_fixture.h"

static void run(void)
{
    char c;
    int count = -1;
    fresh_fifo();
    int fd = SAFE_OPEN(FIFO, O_RDWR | O_NONBLOCK);
    int cap = fcntl(fd, F_GETPIPE_SZ);
    check(cap > 0, "F_GETPIPE_SZ accepts a named FIFO");
    check(fcntl(fd, F_SETPIPE_SZ, 4096) == 4096, "F_SETPIPE_SZ resizes a named FIFO");
    SAFE_WRITE(1, fd, "abc", 3);
    check(ioctl(fd, FIONREAD, &count) == 0 && count == 3, "FIONREAD reports queued bytes");
    TST_EXP_FAIL(lseek(fd, 0, SEEK_SET), ESPIPE);
    TST_EXP_FAIL(pread(fd, &c, 1, 0), ESPIPE);
    TST_EXP_FAIL(pwrite(fd, &c, 1, 0), ESPIPE);
    TST_EXP_FAIL(fsync(fd), EINVAL);
    TST_EXP_FAIL(ftruncate(fd, 0), EINVAL);
    void *p = mmap(NULL, 4096, PROT_READ, MAP_PRIVATE, fd, 0);
    check(p == MAP_FAILED && errno == ENODEV, "FIFO mmap is rejected with ENODEV");
    if (p != MAP_FAILED) munmap(p, 4096);
    int pipefd[2];
    SAFE_PIPE(pipefd);
    int rc = splice(fd, NULL, pipefd[1], NULL, 3, SPLICE_F_NONBLOCK);
    check(rc == 3, "splice consumes FIFO into an anonymous pipe");
    if (rc == 3) {
        char buf[3];
        check(read(pipefd[0], buf, 3) == 3 && !memcmp(buf, "abc", 3), "splice preserves FIFO payload");
    }
    SAFE_CLOSE(pipefd[0]);
    SAFE_CLOSE(pipefd[1]);
    SAFE_CLOSE(fd);

    fresh_fifo();
    fd = SAFE_OPEN(FIFO, O_RDWR | O_NONBLOCK);
    if (fcntl(fd, F_SETPIPE_SZ, 4096) != 4096) tst_brk(TBROK | TERRNO, "resize API FIFO");
    char buffer[4096] = {0};
    SAFE_PIPE(pipefd);
    SAFE_WRITE(1, pipefd[1], "xyz", 3);
    SAFE_WRITE(1, fd, buffer, sizeof(buffer));
    TST_EXP_FAIL(splice(pipefd[0], NULL, fd, NULL, 3, SPLICE_F_NONBLOCK), EAGAIN,
        "splice into full FIFO returns EAGAIN");
    check(ioctl(pipefd[0], FIONREAD, &count) == 0 && count == 3,
        "failed splice leaves source pipe bytes intact");
    SAFE_READ(1, fd, buffer, sizeof(buffer));
    rc = splice(pipefd[0], NULL, fd, NULL, 3, SPLICE_F_NONBLOCK);
    check(rc == 3 && read(fd, buffer, 3) == 3 && !memcmp(buffer, "xyz", 3),
        "splice retries intact source after FIFO capacity returns");
    SAFE_WRITE(1, pipefd[1], "tee", 3);
    rc = tee(pipefd[0], fd, 3, SPLICE_F_NONBLOCK);
    check(rc == 3 && read(fd, buffer, 3) == 3 && !memcmp(buffer, "tee", 3) &&
        read(pipefd[0], buffer, 3) == 3 && !memcmp(buffer, "tee", 3),
        "tee into FIFO preserves source and copied payload");
    struct iovec vector[] = {{"vm", 2}, {"io", 2}};
    /* vmsplice retains separate page slots for vectors, not merged byte capacity. */
    if (fcntl(fd, F_SETPIPE_SZ, 8192) != 8192) tst_brk(TBROK | TERRNO, "reserve two vmsplice slots");
    check(vmsplice(fd, vector, 2, SPLICE_F_NONBLOCK) == 4 && read(fd, buffer, 4) == 4 &&
        !memcmp(buffer, "vmio", 4), "vmsplice writes concatenated user vectors into FIFO");
    if (fcntl(fd, F_SETPIPE_SZ, 4096) != 4096) tst_brk(TBROK | TERRNO, "restore sendfile capacity");
    SAFE_CLOSE(pipefd[0]);
    SAFE_CLOSE(pipefd[1]);

    int regular = SAFE_OPEN("sendfile-input", O_CREAT | O_TRUNC | O_RDWR, 0600);
    SAFE_WRITE(1, regular, "abcdef", 6);
    SAFE_LSEEK(regular, 0, SEEK_SET);
    off_t offset = 1;
    rc = sendfile(fd, regular, &offset, 3);
    check(rc == 3 && offset == 4 && lseek(regular, 0, SEEK_CUR) == 0 &&
        read(fd, buffer, 3) == 3 && !memcmp(buffer, "bcd", 3),
        "sendfile into FIFO honors explicit offset without changing input position");
    SAFE_WRITE(1, fd, buffer, sizeof(buffer));
    TST_EXP_FAIL(sendfile(fd, regular, NULL, 3), EAGAIN, "sendfile into full FIFO returns EAGAIN");
    check(lseek(regular, 0, SEEK_CUR) == 0, "failed sendfile does not advance input position");
    SAFE_READ(1, fd, buffer, sizeof(buffer));
    rc = sendfile(fd, regular, NULL, 3);
    check(rc == 3 && lseek(regular, 0, SEEK_CUR) == 3 && read(fd, buffer, 3) == 3 &&
        !memcmp(buffer, "abc", 3), "sendfile retries from unchanged input position");
    SAFE_CLOSE(regular);
    SAFE_CLOSE(fd);
}
static struct tst_test test = {.test_all = run, .needs_tmpdir = 1};
