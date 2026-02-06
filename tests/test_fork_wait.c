#include <stdio.h>
#include <stdlib.h>
#include <unistd.h>
#include <sys/wait.h>

int main() {
    printf("Fork/Wait Test Starting\n");
    
    // Test 1: Basic fork
    pid_t pid = fork();
    
    if (pid < 0) {
        printf("ERROR: fork failed\n");
        return 1;
    }
    
    if (pid == 0) {
        // Child process
        printf("Child: PID=%d, PPID=%d\n", getpid(), getppid());
        printf("Child: Exiting with code 42\n");
        exit(42);
    } else {
        // Parent process
        printf("Parent: PID=%d, child PID=%d\n", getpid(), pid);
        
        int status;
        pid_t waited = waitpid(pid, &status, 0);
        
        if (waited != pid) {
            printf("ERROR: waitpid returned %d, expected %d\n", waited, pid);
            return 1;
        }
        
        if (WIFEXITED(status)) {
            int exit_code = WEXITSTATUS(status);
            printf("Parent: Child exited with code %d\n", exit_code);
            
            if (exit_code != 42) {
                printf("ERROR: Expected exit code 42, got %d\n", exit_code);
                return 1;
            }
        } else {
            printf("ERROR: Child did not exit normally\n");
            return 1;
        }
    }
    
    // Test 2: Multiple children
    printf("\nTest 2: Multiple children\n");
    
    pid_t child1 = fork();
    if (child1 == 0) {
        printf("Child1: Exiting with code 1\n");
        exit(1);
    }
    
    pid_t child2 = fork();
    if (child2 == 0) {
        printf("Child2: Exiting with code 2\n");
        exit(2);
    }
    
    // Wait for both children
    int status1, status2;
    pid_t w1 = wait(&status1);
    pid_t w2 = wait(&status2);
    
    printf("Parent: Reaped PIDs %d and %d\n", w1, w2);
    
    if ((w1 == child1 || w1 == child2) && (w2 == child1 || w2 == child2) && w1 != w2) {
        printf("Parent: Both children reaped successfully\n");
    } else {
        printf("ERROR: Failed to reap both children correctly\n");
        return 1;
    }
    
    // Test 3: WNOHANG test
    printf("\nTest 3: WNOHANG test\n");
    
    pid_t live_child = fork();
    if (live_child == 0) {
        sleep(1);
        exit(0);
    }
    
    // Should return 0 immediately because child is still running
    pid_t res = waitpid(live_child, NULL, WNOHANG);
    if (res == 0) {
        printf("Parent: WNOHANG correctly returned 0 for live child\n");
    } else {
        printf("ERROR: WNOHANG returned %d for live child\n", res);
        return 1;
    }
    
    // Test 4: WNOWAIT test
    printf("\nTest 4: WNOWAIT test\n");
    // Wait for live_child to exit first
    waitpid(live_child, NULL, 0);
    
    pid_t wnowait_child = fork();
    if (wnowait_child == 0) {
        exit(77);
    }
    
    // Wait for it to exit
    sleep(1); 
    
    int status_nowait = 0;
    // WNOWAIT is 0x01000000 on Linux
    #ifndef WNOWAIT
    #define WNOWAIT 0x01000000
    #endif
    
    pid_t res1 = waitpid(wnowait_child, &status_nowait, WNOWAIT);
    if (res1 == wnowait_child && WIFEXITED(status_nowait) && WEXITSTATUS(status_nowait) == 77) {
        printf("Parent: WNOWAIT correctly reported child status without reaping\n");
    } else {
        printf("ERROR: WNOWAIT failed, res=%d, status=%d\n", res1, status_nowait);
        return 1;
    }
    
    // Now reap it for real
    int status_reap = 0;
    pid_t res2 = waitpid(wnowait_child, &status_reap, 0);
    if (res2 == wnowait_child && WIFEXITED(status_reap) && WEXITSTATUS(status_reap) == 77) {
        printf("Parent: Successfully reaped child after WNOWAIT\n");
    } else {
        printf("ERROR: Reaping after WNOWAIT failed, res=%d\n", res2);
        return 1;
    }
    
    printf("PASS: Fork/Wait Tests\n");
    return 0;
}
