#undef NDEBUG
#include <assert.h>
#include <errno.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/epoll.h>
#include <sys/socket.h>
#include <unistd.h>

void test_epoll_socketpair() {
    printf("--- Testing epoll and socketpair ---\n");
    int sv[2];
    int ret = socketpair(AF_UNIX, SOCK_STREAM | SOCK_NONBLOCK, 0, sv);
    if (ret != 0) {
        perror("socketpair");
        exit(1);
    }
    printf("socketpair created: fds %d and %d\n", sv[0], sv[1]);

    int epfd = epoll_create1(0);
    if (epfd < 0) {
        perror("epoll_create1");
        exit(1);
    }
    printf("epoll fd created: %d\n", epfd);

    struct epoll_event ev;
    memset(&ev, 0, sizeof(ev));
    ev.events = EPOLLIN;
    ev.data.fd = sv[1];

    ret = epoll_ctl(epfd, EPOLL_CTL_ADD, sv[1], &ev);
    if (ret != 0) {
        perror("epoll_ctl");
        exit(1);
    }

    // Ensure non-blocking read returns EAGAIN
    char buf[128] = {0};
    int n = read(sv[1], buf, sizeof(buf));
    assert(n == -1);
    assert(errno == EAGAIN || errno == EWOULDBLOCK);
    printf("Verified EAGAIN on empty read\n");

    const char* msg = "hello epoll";
    n = write(sv[0], msg, strlen(msg));
    assert(n == strlen(msg));
    printf("Wrote message to sv[0]\n");

    struct epoll_event events[2];
    int nfds = epoll_wait(epfd, events, 2, 1000);
    assert(nfds == 1);
    assert(events[0].data.fd == sv[1]);
    assert(events[0].events & EPOLLIN);
    printf("epoll_wait returned: 1 event\n");

    memset(buf, 0, sizeof(buf));
    n = read(sv[1], buf, sizeof(buf));
    assert(n == strlen(msg));
    assert(strcmp(buf, msg) == 0);
    printf("Message read successfully: %s\n", buf);

    // Epoll should not trigger again
    nfds = epoll_wait(epfd, events, 2, 0); // 0 timeout
    assert(nfds == 0);
    printf("Verified empty epoll_wait\n");

    close(epfd);
    close(sv[0]);
    close(sv[1]);
    printf("Network test PASSED!\n");
}

int main() {
    test_epoll_socketpair();
    return 0;
}
