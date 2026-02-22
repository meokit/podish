#include <signal.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

void handler(int sig) {
    const char* msg = "PASS: Received SIGSEGV\n";
    write(1, msg, strlen(msg));
    _exit(0);
}

int main() {
    signal(SIGSEGV, handler);
    // Access unmapped memory
    volatile int* p = (int*)0xDEADBEEF;
    int val = *p;
    return 0;
}
