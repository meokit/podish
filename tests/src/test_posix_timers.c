#include <signal.h>
#include <stdio.h>
#include <stdlib.h>
#include <sys/time.h>
#include <time.h>
#include <unistd.h>

volatile int timer_fired = 0;

void timer_handler(int sig, siginfo_t* si, void* uc) {
    if (sig == SIGALRM) {
        printf("Timer fired! sig=%d value=%d\n", sig, si->si_value.sival_int);
        timer_fired++;
    }
}

int main() {
    printf("Starting POSIX Timers Test\n");

    struct sigaction sa;
    sa.sa_flags = SA_SIGINFO;
    sa.sa_sigaction = timer_handler;
    sigemptyset(&sa.sa_mask);
    if (sigaction(SIGALRM, &sa, NULL) == -1) {
        perror("sigaction");
        return 1;
    }

    timer_t timerid;
    struct sigevent sev;
    struct itimerspec its;

    sev.sigev_notify = SIGEV_SIGNAL;
    sev.sigev_signo = SIGALRM;
    sev.sigev_value.sival_int = 42;

    if (timer_create(CLOCK_REALTIME, &sev, &timerid) == -1) {
        perror("timer_create");
        return 1;
    }
    printf("Timer created successfully\n");

    // Configure timer: expire in 500 ms, then every 500 ms
    its.it_value.tv_sec = 0;
    its.it_value.tv_nsec = 500000000;
    its.it_interval.tv_sec = 0;
    its.it_interval.tv_nsec = 500000000;

    if (timer_settime(timerid, 0, &its, NULL) == -1) {
        perror("timer_settime");
        return 1;
    }
    printf("Timer set. Waiting for ticks...\n");

    // Wait for the timer to fire at least twice
    while (timer_fired < 2) {
        pause(); // Sleep until signal
    }

    printf("Timer fired %d times. Deleting timer...\n", timer_fired);

    if (timer_delete(timerid) == -1) {
        perror("timer_delete");
        return 1;
    }

    printf("Test Passed\n");
    return 0;
}
