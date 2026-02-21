#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <time.h>
#include <unistd.h>

long long get_time_ns() {
    struct timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return (long long)ts.tv_sec * 1000000000LL + ts.tv_nsec;
}

int main() {
    printf("Starting nanosleep test...\n");

    struct timespec req;
    struct timespec rem;

    req.tv_sec = 0;
    req.tv_nsec = 50000000; // 50ms

    long long start = get_time_ns();

    if (nanosleep(&req, &rem) == -1) {
        perror("nanosleep");
        return 1;
    }

    long long end = get_time_ns();
    long long elapsed = end - start;

    printf("Elapsed time: %lld ns\n", elapsed);

    // Check if the elapsed time is at least 50ms (50000000 ns)
    if (elapsed >= 50000000LL) {
        printf("Nanosleep test PASSED!\n");
        return 0;
    } else {
        printf("Nanosleep test FAILED! Elapsed time too short.\n");
        return 1;
    }
}
