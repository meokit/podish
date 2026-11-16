/* SPDX-License-Identifier: GPL-2.0-or-later */
#ifndef PODISH_FIFO_FIXTURE_H
#define PODISH_FIFO_FIXTURE_H
#define _GNU_SOURCE
#include <errno.h>
#include <fcntl.h>
#include <poll.h>
#include <signal.h>
#include <stdint.h>
#include <string.h>
#include <sys/epoll.h>
#include <sys/sendfile.h>
#include <sys/ioctl.h>
#include <sys/mman.h>
#include <sys/stat.h>
#include <sys/syscall.h>
#include <sys/uio.h>
#include <sys/wait.h>
#include <unistd.h>
#include "tst_test.h"

#define FIFO "fifo"
static void check(int condition, const char *description)
{
    tst_res(condition ? TPASS : TFAIL, "%s", description);
}

static void fresh_fifo(void)
{
    if (unlink(FIFO) && errno != ENOENT)
        tst_brk(TBROK | TERRNO, "unlink old FIFO");
    SAFE_MKFIFO(FIFO, 0600);
}

static short ready(int fd, short events)
{
    struct pollfd p = {.fd = fd, .events = events};
    if (poll(&p, 1, 0) < 0)
        tst_brk(TBROK | TERRNO, "poll");
    return p.revents;
}

static int child_ok(pid_t pid)
{
    int status;
    SAFE_WAITPID(pid, &status, 0);
    return WIFEXITED(status) && WEXITSTATUS(status) == 0;
}
#endif
