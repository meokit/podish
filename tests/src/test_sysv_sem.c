#include <errno.h>
#include <stdio.h>
#include <stdlib.h>
#include <sys/ipc.h>
#include <sys/sem.h>
#include <sys/types.h>
#include <sys/wait.h>
#include <unistd.h>

union semun {
    int val;
    struct semid_ds* buf;
    unsigned short* array;
};

void fail(const char* msg) {
    perror(msg);
    exit(1);
}

int main() {
    printf("Starting SysV Semaphores Test\n");

    // 1. Create a semaphore set with 1 semaphore
    int semid = semget(IPC_PRIVATE, 1, IPC_CREAT | 0666);
    if (semid == -1)
        fail("semget IPC_PRIVATE");

    // 2. Initialize semaphore 0 to value 0
    union semun arg;
    arg.val = 0;
    if (semctl(semid, 0, SETVAL, arg) == -1)
        fail("semctl SETVAL");

    printf("Semaphore created and initialized to 0\n");

    // 3. Fork a child process
    pid_t pid = fork();
    if (pid == -1)
        fail("fork");

    if (pid == 0) {
        // Child Process
        printf("Child: Sleeping for 1 second...\n");
        sleep(1);

        // Increment the semaphore to unblock parent
        struct sembuf sop;
        sop.sem_num = 0;
        sop.sem_op = 1;
        sop.sem_flg = 0;

        printf("Child: Incrementing semaphore\n");
        if (semop(semid, &sop, 1) == -1)
            fail("child semop increment");

        printf("Child: Exiting\n");
        exit(0);
    } else {
        // Parent Process
        // Block until child increments the semaphore
        struct sembuf sop;
        sop.sem_num = 0;
        sop.sem_op = -1;
        sop.sem_flg = 0;

        printf("Parent: Waiting for semaphore to be 1...\n");
        if (semop(semid, &sop, 1) == -1)
            fail("parent semop wait");

        printf("Parent: Semaphore acquired!\n");

        int status;
        waitpid(pid, &status, 0);

        // Try IPC_NOWAIT
        sop.sem_num = 0;
        sop.sem_op = -1;
        sop.sem_flg = IPC_NOWAIT;

        printf("Parent: Trying IPC_NOWAIT decrement...\n");
        if (semop(semid, &sop, 1) == -1) {
            if (errno == EAGAIN) {
                printf("Parent: Received EAGAIN as expected\n");
            } else {
                fail("parent semop NOWAIT unexpected error");
            }
        } else {
            printf("Parent: semop NOWAIT succeeded unexpectedly!\n");
            exit(1); // Should fail
        }

        // Clean up
        if (semctl(semid, 0, IPC_RMID) == -1)
            fail("semctl IPC_RMID");
        printf("Parent: Semaphore removed\n");

        printf("Test Passed\n");
    }

    return 0;
}
