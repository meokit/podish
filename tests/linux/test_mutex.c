#include <pthread.h>
#include <stdio.h>
#include <stdlib.h>
#include <unistd.h>
#include <sys/syscall.h>

// Simple write-based print
void print_str(const char* s) {
    int len = 0;
    while(s[len]) len++;
    syscall(4, 1, s, len);
}

pthread_mutex_t lock;
int counter = 0;

void* thread_func(void* arg) {
    for (int i = 0; i < 1000; i++) {
        pthread_mutex_lock(&lock);
        counter++;
        pthread_mutex_unlock(&lock);
    }
    return NULL;
}

int main() {
    pthread_mutex_init(&lock, NULL);
    pthread_t t1, t2;

    print_str("Starting mutex test...\n");

    pthread_create(&t1, NULL, thread_func, NULL);
    pthread_create(&t2, NULL, thread_func, NULL);

    pthread_join(t1, NULL);
    pthread_join(t2, NULL);

    pthread_mutex_destroy(&lock);

    if (counter == 2000) {
        print_str("SUCCESS: Counter is 2000\n");
        return 0;
    } else {
        print_str("FAIL: Counter mismatch\n");
        return 1;
    }
}
