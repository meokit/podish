#include <stdint.h>
#include <stdio.h>

static inline uint64_t rdtsc() {
    uint32_t low, high;
    asm volatile("rdtsc" : "=a"(low), "=d"(high));
    return ((uint64_t)high << 32) | low;
}

int main() {
    printf("RDTSC Test\n");
    uint64_t t1 = rdtsc();
    printf("T1: %llu\n", t1);

    // Busy wait
    for (volatile int i = 0; i < 1000000; i++)
        ;

    uint64_t t2 = rdtsc();
    printf("T2: %llu\n", t2);

    if (t2 > t1) {
        printf("SUCCESS: TSC increased by %llu\n", t2 - t1);
        return 0;
    } else {
        printf("FAILURE: TSC did not increase (T1=%llu, T2=%llu)\n", t1, t2);
        return 1;
    }
}
