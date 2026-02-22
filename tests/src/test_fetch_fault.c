#include <signal.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

void handler(int sig) {
    const char* msg = "PASS: Received SIGSEGV at fetch\n";
    write(1, msg, strlen(msg));
    _exit(0);
}

int main() {
    signal(SIGSEGV, handler);

    // Jump to an unmapped address (instruction fetch fault)
    void (*ptr)() = (void (*)())0xDEADBEEF;
    ptr();

    return 0;
}
