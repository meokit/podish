#include <signal.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/wait.h>
#include <unistd.h>

void handler(int sig) {
    printf("Signal %d handled\n", sig);
}

int main(int argc, char* argv[]) {
    printf("PID %d: Starting (Argc=%d)\n", getpid(), argc);

    // 1. Install signal handler
    struct sigaction sa;
    memset(&sa, 0, sizeof(sa));
    sa.sa_handler = handler;
    // We expect the kernel (emulator) to provide signal restoration via vDSO
    if (sigaction(SIGUSR1, &sa, NULL) < 0) {
        perror("sigaction");
        return 1;
    }

    // 2. Raise signal
    printf("PID %d: Raising SIGUSR1\n", getpid());
    kill(getpid(), SIGUSR1);
    printf("PID %d: Continued after signal\n", getpid());

    // 3. Execve self (once)
    if (argc == 1) {
        printf("PID %d: Executing self\n", getpid());
        char* args[] = {argv[0], "re-exec", NULL};
        execv(argv[0], args);
        perror("execv");
        return 1;
    } else {
        printf("PID %d: Re-executed successfully\n", getpid());
    }

    return 0;
}
