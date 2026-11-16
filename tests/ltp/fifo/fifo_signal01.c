/* SPDX-License-Identifier: GPL-2.0-or-later */
#include "fifo_fixture.h"

static volatile sig_atomic_t caught;
static void handler(int sig) { (void)sig; caught = 1; }
static int notification_fd;
static void restart_handler(int sig)
{
    (void)sig;
    if (caught) _exit(90); /* Second alarm is a bounded watchdog. */
    caught = 1;
    if (write(notification_fd, "S", 1) != 1) _exit(91);
    alarm(5);
}

static void restart_case(unsigned int operation)
{
    int notification[2], fd;
    char c, buffer[4096];
    SAFE_PIPE(notification);
    pid_t pid = SAFE_FORK();
    if (!pid) {
        close(notification[0]);
        notification_fd = notification[1];
        struct sigaction sa = {.sa_handler = restart_handler, .sa_flags = SA_RESTART};
        sigemptyset(&sa.sa_mask);
        if (sigaction(SIGALRM, &sa, NULL)) _exit(1);
        fd = -1;
        if (operation >= 2) fd = open(FIFO, O_RDWR | O_NONBLOCK);
        if (operation == 3) {
            memset(buffer, 'f', sizeof(buffer));
            if (fcntl(fd, F_SETPIPE_SZ, 4096) != 4096 || write(fd, buffer, 4096) != 4096)
                _exit(2);
        }
        if (operation >= 2 && fcntl(fd, F_SETFL, 0)) _exit(3);
        caught = 0;
        alarm(1);
        int rc = operation < 2 ? open(FIFO, operation ? O_WRONLY : O_RDONLY) :
            operation == 2 ? read(fd, &c, 1) : write(fd, "x", 1);
        alarm(0);
        if (write(notification[1], "D", 1) != 1) _exit(4);
        if (rc >= 0 && operation < 2) close(rc);
        if (fd >= 0) close(fd);
        _exit(caught && (operation < 2 ? rc >= 0 : rc == 1) ? 0 : 5);
    }
    SAFE_CLOSE(notification[1]);
    SAFE_READ(1, notification[0], &c, 1); /* Handler proves signal reached the blocked child. */
    struct pollfd p = {.fd = notification[0], .events = POLLIN};
    check(c == 'S' && poll(&p, 1, 50) == 0, "SA_RESTART keeps operation blocked after handler");
    fd = SAFE_OPEN(FIFO, operation == 0 ? O_WRONLY | O_NONBLOCK :
        operation == 1 ? O_RDONLY | O_NONBLOCK : O_RDWR | O_NONBLOCK);
    if (operation == 2) SAFE_WRITE(1, fd, "x", 1);
    if (operation == 3) SAFE_READ(1, fd, buffer, sizeof(buffer));
    check(child_ok(pid), operation == 0 ? "SA_RESTART resumes reader open after writer arrives" :
        operation == 1 ? "SA_RESTART resumes writer open after reader arrives" :
        operation == 2 ? "SA_RESTART resumes read after data arrives" :
        "SA_RESTART resumes write after capacity becomes available");
    SAFE_CLOSE(fd);
    SAFE_CLOSE(notification[0]);
}

static void run(unsigned int n)
{
    fresh_fifo();
    if (n >= 4) {
        restart_case(n - 4);
        TST_EXP_FAIL(open(FIFO, O_WRONLY | O_NONBLOCK), ENXIO, "restarted operation leaves no phantom reader");
        return;
    }
    pid_t pid = SAFE_FORK();
    if (!pid) {
        struct sigaction sa = {.sa_handler = handler};
        char c;
        sigemptyset(&sa.sa_mask);
        if (sigaction(SIGALRM, &sa, NULL)) _exit(1);
        int fd = -1;
        if (n >= 2) fd = open(FIFO, O_RDWR | O_NONBLOCK);
        if (n == 3) {
            char buffer[4096] = {0};
            if (fcntl(fd, F_SETPIPE_SZ, 4096) != 4096 || write(fd, buffer, 4096) != 4096) _exit(3);
        }
        if (n >= 2 && fcntl(fd, F_SETFL, 0)) _exit(4);
        caught = 0;
        alarm(1); /* Signal delivery is the tested event, not sleep synchronization. */
        int rc = n < 2 ? open(FIFO, n ? O_WRONLY : O_RDONLY) :
            n == 2 ? read(fd, &c, 1) : write(fd, "x", 1);
        int error = errno;
        alarm(0);
        if (rc >= 0 && n < 2) close(rc);
        if (fd >= 0) close(fd);
        _exit(rc == -1 && error == EINTR && caught ? 0 : 2);
    }
    check(child_ok(pid), n == 0 ? "blocked reader open is interrupted with EINTR" :
        n == 1 ? "blocked writer open is interrupted with EINTR" :
        n == 2 ? "blocked read is interrupted with EINTR" : "blocked write is interrupted with EINTR");
    TST_EXP_FAIL(open(FIFO, O_WRONLY | O_NONBLOCK), ENXIO, "interrupted operation leaves no phantom reader");
}
static struct tst_test test = {.test = run, .tcnt = 8, .needs_tmpdir = 1, .forks_child = 1};
