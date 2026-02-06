#include <unistd.h>
#include <sys/syscall.h>
#include <pthread.h>
#include <string.h>

void print_str(const char* s) {
    write(1, s, strlen(s));
}

void* thread_func(void* arg) {
    int id = *(int*)arg;
    if (id == 1) print_str("Hello from thread 1!\n");
    else if (id == 2) print_str("Hello from thread 2!\n");
    return NULL;
}

int main() {
    pthread_t t1, t2;
    int id1 = 1, id2 = 2;

    print_str("Main thread starting...\n");

    if (pthread_create(&t1, NULL, thread_func, &id1) != 0) {
        print_str("pthread_create 1 failed\n");
        return 1;
    }
    
    if (pthread_create(&t2, NULL, thread_func, &id2) != 0) {
        print_str("pthread_create 2 failed\n");
        return 1;
    }

    print_str("Threads created, waiting...\n");

    pthread_join(t1, NULL);
    pthread_join(t2, NULL);

    print_str("All threads joined. Exiting.\n");
    print_str("PASS: Pthread Basic\n");
    return 0;
}
