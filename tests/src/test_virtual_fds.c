#include <poll.h>
#include <signal.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <sys/eventfd.h>
#include <sys/signalfd.h>
#include <sys/timerfd.h>
#include <unistd.h>

int test_eventfd() {
    printf("--- Testing eventfd ---\n");
    int efd = eventfd(0, EFD_NONBLOCK);
    if (efd == -1) {
        perror("eventfd");
        return -1;
    }

    uint64_t val = 5;
    if (write(efd, &val, sizeof(uint64_t)) != sizeof(uint64_t)) {
        perror("write eventfd");
        return -1;
    }

    val = 15;
    if (write(efd, &val, sizeof(uint64_t)) != sizeof(uint64_t)) {
        perror("write eventfd");
        return -1;
    }

    uint64_t read_val;
    if (read(efd, &read_val, sizeof(uint64_t)) != sizeof(uint64_t)) {
        perror("read eventfd");
        return -1;
    }
    printf("eventfd read: %llu\n", (unsigned long long)read_val);
    if (read_val != 20) {
        return -1;
    }
    close(efd);
    return 0;
}

int test_timerfd() {
    printf("--- Testing timerfd ---\n");
    int tfd = timerfd_create(CLOCK_MONOTONIC, TFD_NONBLOCK);
    if (tfd == -1) {
        perror("timerfd_create");
        return -1;
    }

    struct itimerspec its;
    its.it_value.tv_sec = 0;
    its.it_value.tv_nsec = 50000000; // 50ms
    its.it_interval.tv_sec = 0;
    its.it_interval.tv_nsec = 50000000; // 50ms

    if (timerfd_settime(tfd, 0, &its, NULL) == -1) {
        perror("timerfd_settime");
        return -1;
    }

    struct pollfd pfd;
    pfd.fd = tfd;
    pfd.events = POLLIN;

    int expirations = 0;
    for (int i = 0; i < 2; i++) {
        if (poll(&pfd, 1, 1000) > 0) {
            uint64_t exp;
            if (read(tfd, &exp, sizeof(uint64_t)) == sizeof(uint64_t)) {
                printf("timerfd expired %llu times\n", (unsigned long long)exp);
                expirations += exp;
            }
        }
    }

    if (expirations < 2) {
        printf("timerfd failed: expected at least 2 expirations, got %d\n", expirations);
        return -1;
    }

    close(tfd);
    return 0;
}

int test_signalfd() {
    printf("--- Testing signalfd ---\n");
    sigset_t mask;
    sigemptyset(&mask);
    sigaddset(&mask, SIGUSR1);

    // Block the signal so it can be read by signalfd
    if (sigprocmask(SIG_BLOCK, &mask, NULL) == -1) {
        perror("sigprocmask");
        return -1;
    }

    int sfd = signalfd(-1, &mask, SFD_NONBLOCK);
    if (sfd == -1) {
        perror("signalfd");
        return -1;
    }

    printf("Sending SIGUSR1 to self...\n");
    kill(getpid(), SIGUSR1);

    struct signalfd_siginfo fdsi;
    int s = read(sfd, &fdsi, sizeof(struct signalfd_siginfo));
    if (s != sizeof(struct signalfd_siginfo)) {
        perror("read signalfd");
        return -1;
    }

    printf("signalfd read SIGUSR1\n");
    if (fdsi.ssi_signo != SIGUSR1) {
        return -1;
    }

    close(sfd);
    return 0;
}

int main() {
    if (test_eventfd() != 0)
        return 1;
    if (test_timerfd() != 0)
        return 1;
    if (test_signalfd() != 0)
        return 1;

    printf("Virtual FDs test PASSED!\n");
    return 0;
}
