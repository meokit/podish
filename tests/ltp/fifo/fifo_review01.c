/* SPDX-License-Identifier: GPL-2.0-or-later */
#include "fifo_fixture.h"
#include <pthread.h>

static int reader, control[2];
static int read_result;

static void packet_splice(void)
{
    int input[2], output[2], count = -1;
    char bytes[8];
    if (pipe2(input, O_DIRECT | O_NONBLOCK) || pipe2(output, O_NONBLOCK))
        tst_brk(TBROK | TERRNO, "packet splice pipes");
    SAFE_WRITE(1, input[1], "ABCDE", 5);
    check(splice(input[0], NULL, output[1], NULL, 1, SPLICE_F_NONBLOCK) == 1,
        "short splice transfers one packet byte");
    check(ioctl(input[0], FIONREAD, &count) == 0 && count == 4,
        "short splice retains the packet tail");
    check(read(input[0], bytes, sizeof(bytes)) == 4 && !memcmp(bytes, "BCDE", 4),
        "retained packet tail has the correct payload");
    check(read(output[0], bytes, sizeof(bytes)) == 1 && bytes[0] == 'A',
        "short splice destination contains only the accepted byte");
    for (int i = 0; i < 2; i++) { SAFE_CLOSE(input[i]); SAFE_CLOSE(output[i]); }
}

static void atomic_fault(void)
{
    fresh_fifo();
    int fd = SAFE_OPEN(FIFO, O_RDWR | O_NONBLOCK), count = -1;
    struct iovec vectors[] = {{"AB", 2}, {(void *)1, 2}};
    TST_EXP_FAIL(writev(fd, vectors, 2), EFAULT, "small writev rejects an invalid suffix");
    check(ioctl(fd, FIONREAD, &count) == 0 && count == 0,
        "invalid small writev does not publish its valid prefix");
    char *pages = mmap(NULL, 8192, PROT_READ | PROT_WRITE, MAP_PRIVATE | MAP_ANONYMOUS, -1, 0);
    if (pages == MAP_FAILED) tst_brk(TBROK | TERRNO, "cross-page fault fixture");
    memcpy(pages + 4094, "AB", 2);
    SAFE_MUNMAP(pages + 4096, 4096);
    struct iovec cross = {pages + 4094, 4};
    TST_EXP_FAIL(writev(fd, &cross, 1), EFAULT, "small cross-page writev rejects an unmapped suffix");
    check(ioctl(fd, FIONREAD, &count) == 0 && count == 0,
        "cross-page fault does not publish a partial record");
    SAFE_MUNMAP(pages, 4096);
    SAFE_CLOSE(fd);
}

static void sendfile_validation(void)
{
    fresh_fifo();
    int fd = SAFE_OPEN(FIFO, O_RDWR | O_NONBLOCK), count = -1;
    SAFE_WRITE(1, fd, "ABCDE", 5);
    TST_EXP_FAIL(sendfile(fd, fd, NULL, 1), EINVAL, "sendfile rejects the same FIFO as source and destination");
    check(ioctl(fd, FIONREAD, &count) == 0 && count == 5,
        "rejected self-sendfile leaves queued bytes intact");
    int out = SAFE_OPEN("sendfile-output", O_CREAT | O_TRUNC | O_RDWR, 0600);
    TST_EXP_FAIL(sendfile(out, fd, NULL, 1), EINVAL, "sendfile rejects FIFO input for regular-file output");
    check(ioctl(fd, FIONREAD, &count) == 0 && count == 5 && lseek(out, 0, SEEK_END) == 0,
        "rejected sendfile neither consumes FIFO nor changes output");
    SAFE_CLOSE(out);
    SAFE_CLOSE(fd);
}

static void *read_thread(void *unused)
{
    char c;
    (void)unused;
    if (write(control[1], "S", 1) != 1) return NULL;
    read_result = read(reader, &c, 1);
    if (write(control[1], "D", 1) != 1) return NULL;
    return NULL;
}

static void inflight_close(void)
{
    fresh_fifo();
    reader = SAFE_OPEN(FIFO, O_RDONLY | O_NONBLOCK);
    int writer = SAFE_OPEN(FIFO, O_WRONLY | O_NONBLOCK);
    if (fcntl(reader, F_SETFL, 0)) tst_brk(TBROK | TERRNO, "set blocking read");
    SAFE_PIPE(control);
    read_result = -99;
    pthread_t thread;
    int rc = pthread_create(&thread, NULL, read_thread, NULL);
    if (rc) tst_brk(TBROK, "pthread_create: %s", strerror(rc));
    char c;
    SAFE_READ(1, control[0], &c, 1);
    struct pollfd p = {.fd = control[0], .events = POLLIN};
    check(poll(&p, 1, 100) == 0, "FIFO read is blocked before its descriptor is closed");
    SAFE_CLOSE(reader);
    check(write(writer, "x", 1) == 1, "in-flight read retains an active reader after close(fd)");
    SAFE_CLOSE(writer);
    if (poll(&p, 1, 2000) != 1) tst_brk(TBROK, "in-flight reader failed to complete");
    rc = pthread_join(thread, NULL);
    if (rc) tst_brk(TBROK, "pthread_join: %s", strerror(rc));
    check(read_result == 1, "in-flight read receives the byte, not premature EOF");
    SAFE_CLOSE(control[0]);
    SAFE_CLOSE(control[1]);
}

static void run(void)
{
    struct sigaction ignore = {.sa_handler = SIG_IGN};
    SAFE_SIGACTION(SIGPIPE, &ignore, NULL);
    packet_splice();
    atomic_fault();
    sendfile_validation();
    inflight_close();
}

static struct tst_test test = {.test_all = run, .needs_tmpdir = 1};
