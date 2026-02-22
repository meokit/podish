#include <signal.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

void handler(int sig) {
    const char* msg = "PASS: Received SIGFPE\n";
    write(1, msg, strlen(msg));
    _exit(0);
}

int main() {
    signal(SIGFPE, handler);
    volatile int a = 1;
    volatile int b = 0;
    // We don't use printf here because we want to see if the write in the handler works.
    // The emulator should deliver SIGFPE on the division.
    volatile int c = a / b;
    return 0;
}
