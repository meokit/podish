/* SPDX-License-Identifier: GPL-2.0-or-later */
#include "fifo_fixture.h"

static void multiwriter(void)
{
    enum { RECORD = 4096, RECORDS = 16 };
    char buffer[RECORD];
    int gate[2], counts[2] = {0}, valid = 1;
    pid_t children[2];
    fresh_fifo();
    int anchor = SAFE_OPEN(FIFO, O_RDWR);
    SAFE_PIPE(gate);
    for (int writer = 0; writer < 2; writer++) {
        children[writer] = SAFE_FORK();
        if (!children[writer]) {
            alarm(10);
            close(anchor);
            close(gate[1]);
            int fd = open(FIFO, O_WRONLY);
            char *pages = mmap(NULL, 12288, PROT_READ | PROT_WRITE,
                MAP_PRIVATE | MAP_ANONYMOUS, -1, 0);
            if (fd < 0 || pages == MAP_FAILED) _exit(1);
            char *record = pages + 4094;
            memset(record, 'A' + writer, RECORD);
            struct iovec v[] = {{record, 2}, {record + 2, 2047}, {record + 2049, 2047}};
            char start;
            if (read(gate[0], &start, 1) != 1) _exit(2);
            for (int n = 0; n < RECORDS; n++)
                if (writev(fd, v, 3) != RECORD) _exit(3);
            close(fd);
            _exit(0);
        }
    }
    SAFE_CLOSE(gate[0]);
    SAFE_WRITE(1, gate[1], "go", 2);
    SAFE_CLOSE(gate[1]);
    alarm(10);
    for (int n = 0; n < 2 * RECORDS; n++) {
        int done = 0;
        while (done < RECORD) {
            int rc = read(anchor, buffer + done, RECORD - done);
            if (rc <= 0) tst_brk(TBROK | TERRNO, "read multiwriter record");
            done += rc;
        }
        if (buffer[0] != 'A' && buffer[0] != 'B') valid = 0;
        else counts[buffer[0] - 'A']++;
        for (int i = 1; i < RECORD; i++)
            if (buffer[i] != buffer[0]) valid = 0;
    }
    alarm(0);
    check(valid && counts[0] == RECORDS && counts[1] == RECORDS,
        "two independent writers preserve whole PIPE_BUF cross-page vector records");
    check(child_ok(children[0]), "first atomic-record writer completes");
    check(child_ok(children[1]), "second atomic-record writer completes");
    SAFE_CLOSE(anchor);
}

static void run(void)
{
    char buffer[4096];
    fresh_fifo();
    int a = SAFE_OPEN(FIFO, O_RDWR | O_NONBLOCK);
    int b = SAFE_OPEN(FIFO, O_RDONLY | O_NONBLOCK);
    SAFE_WRITE(1, a, "abcdef", 6);
    check(read(b, buffer, 3) == 3 && !memcmp(buffer, "abc", 3), "independent reader consumes shared stream prefix");
    check(read(a, buffer, 3) == 3 && !memcmp(buffer, "def", 3), "second reader consumes suffix, not a private file offset");
    TST_EXP_FAIL(read(b, buffer, 1), EAGAIN, "empty stream with live writer");
    int cap = fcntl(a, F_GETPIPE_SZ);
    check(cap > 0, "FIFO has bounded pipe capacity");
    if (cap > 0) {
        int written = 0;
        memset(buffer, 'x', sizeof(buffer));
        while (written < cap) {
            int rc = write(a, buffer, sizeof(buffer));
            if (rc <= 0) break;
            written += rc;
        }
        check(written == cap, "writes fill reported capacity");
        TST_EXP_FAIL(write(a, buffer, 1), EAGAIN, "full FIFO applies backpressure");
        while (read(b, buffer, sizeof(buffer)) > 0) {}
        struct iovec v[] = {{"ab", 2}, {"cd", 2}};
        check(writev(a, v, 2) == 4 && read(b, buffer, 4) == 4 && !memcmp(buffer, "abcd", 4), "writev concatenates one stream record");
        check(fcntl(a, F_SETPIPE_SZ, 4096) == 4096, "resize empty FIFO for atomic-vector boundary");
        SAFE_WRITE(1, a, buffer, 4093);
        TST_EXP_FAIL(writev(a, v, 2), EAGAIN, "aggregate small writev is atomic when only its first iovec fits");
        int available = -1;
        check(ioctl(a, FIONREAD, &available) == 0 && available == 4093, "failed atomic writev leaves no prefix in FIFO");
        while (read(b, buffer, sizeof(buffer)) > 0) {}
        char *pages = mmap(NULL, 8192, PROT_READ | PROT_WRITE, MAP_PRIVATE | MAP_ANONYMOUS, -1, 0);
        if (pages == MAP_FAILED) tst_brk(TBROK | TERRNO, "map cross-page record");
        memcpy(pages + 4094, "WXYZ", 4);
        struct iovec cross = {pages + 4094, 4};
        SAFE_WRITE(1, a, buffer, 4093);
        TST_EXP_FAIL(writev(a, &cross, 1), EAGAIN, "single cross-page iovec is atomic when only its prefix fits");
        check(ioctl(a, FIONREAD, &available) == 0 && available == 4093, "failed cross-page write leaves no prefix");
        while (read(b, buffer, sizeof(buffer)) > 0) {}
        check(writev(a, &cross, 1) == 4 && read(b, buffer, 4) == 4 && !memcmp(buffer, "WXYZ", 4),
            "cross-page atomic record preserves payload");
        SAFE_MUNMAP(pages, 8192);
    }
    SAFE_CLOSE(a);
    check(read(b, buffer, 1) == 0, "last writer close exposes EOF");
    SAFE_CLOSE(b);
    a = SAFE_OPEN(FIFO, O_RDWR | O_NONBLOCK);
    int w = SAFE_OPEN(FIFO, O_WRONLY | O_NONBLOCK);
    SAFE_CLOSE(a);
    struct sigaction ignore = {.sa_handler = SIG_IGN};
    SAFE_SIGACTION(SIGPIPE, &ignore, NULL);
    TST_EXP_FAIL(write(w, "x", 1), EPIPE, "last reader close breaks writers");
    SAFE_CLOSE(w);
    multiwriter();
}
static struct tst_test test = {.test_all = run, .needs_tmpdir = 1, .forks_child = 1};
