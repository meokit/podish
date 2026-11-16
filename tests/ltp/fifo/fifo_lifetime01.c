/* SPDX-License-Identifier: GPL-2.0-or-later */
#include "fifo_fixture.h"

static void run(void)
{
    char buf[8];
    struct stat path, fd;
    fresh_fifo();
    int a = SAFE_OPEN(FIFO, O_RDWR | O_NONBLOCK);
    SAFE_LINK(FIFO, "alias");
    SAFE_RENAME("alias", "renamed");
    int b = SAFE_OPEN("renamed", O_RDWR | O_NONBLOCK);
    SAFE_WRITE(1, a, "old", 3);
    check(read(b, buf, 3) == 3 && !memcmp(buf, "old", 3), "hard-link/rename aliases share transport");
    SAFE_STAT(FIFO, &path);
    SAFE_FSTAT(a, &fd);
    check(path.st_ino == fd.st_ino && path.st_dev == fd.st_dev && S_ISFIFO(fd.st_mode), "fstat preserves namespace FIFO identity");
    SAFE_WRITE(1, a, "x", 1);
    SAFE_FSTAT(a, &fd);
    check(fd.st_size == 0, "buffered payload does not become file size");
    SAFE_UNLINK(FIFO);
    SAFE_MKFIFO(FIFO, 0600);
    int replacement = SAFE_OPEN(FIFO, O_RDWR | O_NONBLOCK);
    TST_EXP_FAIL(read(replacement, buf, 1), EAGAIN, "recreated pathname has isolated transport");
    check(read(b, buf, 1) == 1 && buf[0] == 'x', "unlinked original endpoint retains data");
    SAFE_WRITE(1, replacement, "discard", 7);
    int dupfd = SAFE_DUP(replacement);
    SAFE_CLOSE(replacement);
    check(read(dupfd, buf, 7) == 7 && !memcmp(buf, "discard", 7), "dup retains the open description and transport");
    SAFE_WRITE(1, dupfd, "discard", 7);
    SAFE_CLOSE(dupfd);
    replacement = SAFE_OPEN(FIFO, O_RDWR | O_NONBLOCK);
    TST_EXP_FAIL(read(replacement, buf, 1), EAGAIN, "last close discards payload before reopen");
    SAFE_CLOSE(replacement);
    SAFE_CLOSE(a);
    SAFE_CLOSE(b);
    SAFE_UNLINK("renamed");
}
static struct tst_test test = {.test_all = run, .needs_tmpdir = 1};
