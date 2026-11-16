/* SPDX-License-Identifier: GPL-2.0-or-later */
#include "fifo_fixture.h"

static void run(void)
{
    char c;
    fresh_fifo();
    int r = SAFE_OPEN(FIFO, O_RDONLY | O_NONBLOCK);
    check(ready(r, POLLIN) == 0, "new reader has neither data nor initial HUP");
    check(read(r, &c, 1) == 0, "initial read EOF does not itself establish HUP");
    int w = open(FIFO, O_WRONLY | O_NONBLOCK);
    check(w >= 0, "connect writer for readiness tests");
    if (w < 0) { SAFE_CLOSE(r); return; }
    int ep = epoll_create1(EPOLL_CLOEXEC);
    if (ep < 0) tst_brk(TBROK | TERRNO, "epoll_create1");
    struct epoll_event ev = {.events = EPOLLIN | EPOLLET, .data.u64 = 42}, out;
    if (epoll_ctl(ep, EPOLL_CTL_ADD, r, &ev)) tst_brk(TBROK | TERRNO, "epoll_ctl");
    check(epoll_wait(ep, &out, 1, 0) == 0, "connected empty FIFO is not readable");
    SAFE_WRITE(1, w, "x", 1);
    check(ready(r, POLLIN) == POLLIN, "buffered byte reports POLLIN");
    check(epoll_wait(ep, &out, 1, 1000) == 1 && out.data.u64 == 42 && (out.events & EPOLLIN), "epoll reports FIFO data edge");
    check(epoll_wait(ep, &out, 1, 0) == 0, "unconsumed data does not repeat edge");
    SAFE_CLOSE(w);
    check(ready(r, POLLIN) == (POLLIN | POLLHUP), "buffered data and final-writer HUP coexist");
    check(ready(r, 0) == POLLHUP, "HUP is reported without requested events");
    SAFE_READ(1, r, &c, 1);
    check(ready(r, POLLIN) == POLLHUP, "drained disconnected FIFO retains HUP, not POLLIN");
    int fresh = SAFE_OPEN(FIFO, O_RDONLY | O_NONBLOCK);
    check(ready(fresh, POLLIN) == 0, "a new reader starts with its own writer generation");
    w = SAFE_OPEN(FIFO, O_WRONLY | O_NONBLOCK);
    check(ready(r, POLLIN) == 0, "reconnect clears old reader HUP while writer is live");
    SAFE_CLOSE(w);
    check(ready(fresh, 0) == POLLHUP, "new reader sees later writer disconnect");
    SAFE_CLOSE(fresh);
    SAFE_CLOSE(r);
    SAFE_CLOSE(ep);
}
static struct tst_test test = {.test_all = run, .needs_tmpdir = 1};
