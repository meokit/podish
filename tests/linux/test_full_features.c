#include <stdio.h>
#include <stdlib.h>
#include <unistd.h>
#include <time.h>
#include <signal.h>
#include <errno.h>
#include <string.h>
#include <sys/wait.h>
#include <sys/types.h>
#include <dirent.h>

void test_time_precision() {
    printf("=== Testing Time Precision ===\n");
    struct timespec ts1, ts2;
    
    // Test CLOCK_REALTIME
    clock_gettime(CLOCK_REALTIME, &ts1);
    // Busy wait a bit
    for(volatile int i=0; i<1000000; i++);
    clock_gettime(CLOCK_REALTIME, &ts2);
    
    long diff_ns = (ts2.tv_sec - ts1.tv_sec) * 1000000000L + (ts2.tv_nsec - ts1.tv_nsec);
    printf("CLOCK_REALTIME delta: %ld ns\n", diff_ns);
    
    if (diff_ns <= 0) {
        printf("FAIL: Time did not advance or went backwards!\n");
        exit(1);
    }

    // Test CLOCK_MONOTONIC
    clock_gettime(CLOCK_MONOTONIC, &ts1);
    for(volatile int i=0; i<1000000; i++);
    clock_gettime(CLOCK_MONOTONIC, &ts2);
    
    diff_ns = (ts2.tv_sec - ts1.tv_sec) * 1000000000L + (ts2.tv_nsec - ts1.tv_nsec);
    printf("CLOCK_MONOTONIC delta: %ld ns\n", diff_ns);

    if (diff_ns <= 0) {
        printf("FAIL: Monotonic time did not advance!\n");
        exit(1);
    }
    printf("PASS: Time Precision\n");
}

// Flag for signal tests
volatile int sigusr1_handled = 0;
volatile int siginfo_handled = 0;

void sigusr1_handler(int sig) {
    if (sig == SIGUSR1) {
        sigusr1_handled = 1;
        printf("Signal Handler: Caught SIGUSR1\n");
    }
}

void siginfo_handler(int sig, siginfo_t *info, void *ucontext) {
    if (sig == SIGUSR2) {
        siginfo_handled = 1;
        printf("  [SigInfo] signo=%d, code=%d\n", info->si_signo, info->si_code);
    }
}

void test_signals() {
    printf("\n=== Testing Signals ===\n");
    
    struct sigaction sa;
    memset(&sa, 0, sizeof(sa));
    sa.sa_handler = sigusr1_handler;
    sigemptyset(&sa.sa_mask);
    
    if (sigaction(SIGUSR1, &sa, NULL) != 0) {
        perror("sigaction");
        exit(1);
    }
    
    printf("Raising SIGUSR1...\n");
    kill(getpid(), SIGUSR1);
    
    // Wait a bit for delivery (if async)
    // In our emulator, it might be synchronous or check at quantum end
    for(int i=0; i<10; i++) {
        if (sigusr1_handled) break;
        usleep(1000); 
    }
    
    if (sigusr1_handled) {
        printf("PASS: Signal Delivered\n");
    } else {
        printf("FAIL: Signal NOT Delivered\n");
        exit(1); // Now we expect it to work
    }
    
    // Reset
    sigusr1_handled = 0;
    
    // Test Blocking
    sigset_t set, oldset;
    sigemptyset(&set);
    sigaddset(&set, SIGUSR1);
    sigprocmask(SIG_BLOCK, &set, &oldset);
    
    printf("Raising blocked SIGUSR1...\n");
    kill(getpid(), SIGUSR1);
    
    // Should NOT be handled yet
    usleep(10000);
    if (sigusr1_handled) {
        printf("FAIL: Signal handled while blocked!\n");
        exit(1);
    } else {
        printf("PASS: Signal correctly blocked\n");
    }
    
    // Unblock
    printf("Unblocking signal...\n");
    // Unblock and check
    sigprocmask(SIG_UNBLOCK, &set, NULL);
    // Should be delivered immediately if implementation is correct (pending check in hot loop)
    // Small sleep to yield
    struct timespec ts = {0, 10000000}; // 10ms
    nanosleep(&ts, NULL);
    
    if (sigusr1_handled) {
        printf("Signal delivered after unblock: PASS\n");
    } else {
        printf("Signal NOT delivered after unblock: FAIL\n");
        exit(1);
    }

    // NEW: Test SA_SIGINFO with SIGUSR2
    printf("Testing SA_SIGINFO...\n");
    struct sigaction sa_info;
    memset(&sa_info, 0, sizeof(sa_info));
    sa_info.sa_sigaction = siginfo_handler;
    sa_info.sa_flags = SA_SIGINFO;
    
    if (sigaction(SIGUSR2, &sa_info, NULL) < 0) {
        perror("sigaction SA_SIGINFO");
        exit(1);
    }
    
    kill(getpid(), SIGUSR2);
    nanosleep(&ts, NULL); // Yield
    
    if (siginfo_handled) {
        printf("SA_SIGINFO handler executed: PASS\n");
    } else {
        printf("SA_SIGINFO handler NOT executed: FAIL\n");
        exit(1);
    }
}

void test_execve() {
    printf("\n=== Testing Execve ===\n");
    
    pid_t pid = fork();
    if (pid == -1) {
        perror("fork");
        exit(1);
    }
    
    if (pid == 0) {
        // Child
        char *args[] = { "hello_static", NULL };
        char *env[] = { "TEST_ENV=1", NULL };
        
        char buf[256];
        if (getcwd(buf, sizeof(buf)) != NULL) {
            printf("[Child] CWD: %s\n", buf);
        } else {
             perror("[Child] getcwd failed");
        }
        fflush(stdout);



        // Note: we assume hello_static exists in tests/linux/assets since we run from repo root
        // Use ABSOLUTE path from chroot root
        char *path = "/tests/linux/assets/hello_static";
        printf("[Child] Checking access for %s\n", path);
        fflush(stdout);
        
        if (access(path, F_OK) != 0) {
             printf("[Child] Access failed for %s: %s\n", path, strerror(errno));
             fflush(stdout);
             // Fallback to relative
             path = "./hello_static"; 
        }
        execve(path, args, env);
        
        // If we get here, execve failed
        perror("[Child] execve failed");
        exit(1);
    } else {
        // Parent
        int status;
        waitpid(pid, &status, 0);
        if (WIFEXITED(status)) {
            printf("[Parent] Child exited with status %d\n", WEXITSTATUS(status));
            if (WEXITSTATUS(status) == 0) {
                printf("PASS: Execve success\n");
            } else {
                 printf("FAIL: Child returned non-zero\n");
            }
        } else {
            printf("FAIL: Child did not exit normally\n");
        }
    }
}

int main(int argc, char *argv[]) {
    printf("Starting Feature Tests...\n");
    
    test_time_precision();
    test_signals();
    test_execve();
    
    printf("All Tests Completed.\n");
    return 0;
}
