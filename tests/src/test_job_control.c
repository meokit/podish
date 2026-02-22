#include <signal.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/types.h>
#include <sys/wait.h>
#include <termios.h>
#include <unistd.h>

void handler(int sig) {
    if (sig == SIGTTIN) {
        // Safe printing in signal handler
        const char* msg = "Received SIGTTIN\n";
        write(STDOUT_FILENO, msg, strlen(msg));
    } else if (sig == SIGTTOU) {
        const char* msg = "Received SIGTTOU\n";
        write(STDOUT_FILENO, msg, strlen(msg));
        // Exit to avoid infinite loop
        _exit(0);
    }
}

int main() {
    struct sigaction sa;
    sa.sa_handler = handler;
    sigemptyset(&sa.sa_mask);
    sa.sa_flags = 0; // Specifically NO SA_RESTART
    sigaction(SIGTTIN, &sa, NULL);
    sigaction(SIGTTOU, &sa, NULL);

    // The emulator static runner doesn't set up the TTY foreground PGID automatically
    // because it isn't a shell. We need to do it manually.
    pid_t parent_pid = getpid();
    setpgid(parent_pid, parent_pid);
    tcsetpgrp(STDIN_FILENO, parent_pid);

    pid_t pid = fork();
    if (pid == 0) {
        // Child becomes a new process group (background)
        setpgid(0, 0);

        // Try to read from TTY, this should trigger SIGTTIN
        char buf[10];
        ssize_t n = read(STDIN_FILENO, buf, sizeof(buf));

        if (n < 0) {
            const char* msg = "read interrupted\n";
            write(STDOUT_FILENO, msg, strlen(msg));
        }

        // Try to write to TTY with TOSTOP set
        struct termios t;
        tcgetattr(STDOUT_FILENO, &t);
        t.c_lflag |= TOSTOP;
        tcsetattr(STDOUT_FILENO, TCSANOW, &t);

        const char* msg2 = "Child attempting to write (should trigger SIGTTOU if TOSTOP is honored)\n";
        write(STDOUT_FILENO, msg2, strlen(msg2));

        // Wait around just in case
        while (1)
            sleep(1);
    } else {
        // Wait for child
        int status;
        waitpid(pid, &status, 0);

        printf("Parent exit\n");
    }

    return 0;
}
