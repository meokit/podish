#include <signal.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

void handler(int sig) {
    const char* msg = "PASS: Received SIGILL\n";
    write(1, msg, strlen(msg));
    _exit(0);
}

int main() {
    signal(SIGILL, handler);
    // Trigger #UD
    __asm__("ud2");
    return 0;
}
