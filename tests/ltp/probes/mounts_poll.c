/* Native Linux semantic probe. Independent of unmodified upstream LTP. */
#define _GNU_SOURCE
#include <assert.h>
#include <fcntl.h>
#include <poll.h>
#include <sched.h>
#include <stdio.h>
#include <stdlib.h>
#include <sys/mount.h>
#include <unistd.h>

static short sample(int fd, short events)
{
    struct pollfd p = {.fd = fd, .events = events};
    assert(poll(&p, 1, 0) >= 0);
    return p.revents;
}

int main(void)
{
    char directory[] = "/tmp/podish-mounts-poll-XXXXXX";
    char buf[256];
    /* All mount mutations are confined to this probe's own Linux namespace. */
    assert(unshare(CLONE_NEWNS) == 0);
    assert(mount(NULL, "/", NULL, MS_REC | MS_PRIVATE, NULL) == 0);
    assert(mkdtemp(directory));
    int fd = open("/proc/self/mounts", O_RDONLY);
    assert(fd >= 0);
    assert(sample(fd, POLLPRI) == 0);
    assert(mount("none", directory, "tmpfs", 0, NULL) == 0);
    assert(lseek(fd, 0, SEEK_SET) == 0);
    ssize_t n;
    while ((n = read(fd, buf, sizeof(buf))) > 0) {}
    assert(n == 0);
    short first = sample(fd, POLLPRI);
    short second = sample(fd, POLLPRI);
    assert(first == (POLLPRI | POLLERR));
    assert(second == 0);
    assert(umount(directory) == 0);
    short error_only = sample(fd, 0);
    assert(error_only == POLLERR);
    assert(sample(fd, 0) == 0);
    assert(close(fd) == 0);
    assert(rmdir(directory) == 0);
    printf("mounts_poll: read/seek do not consume; poll consumes; PRI|ERR=%#x; events=0 -> ERR=%#x\n",
           first, error_only);
    return 0;
}
