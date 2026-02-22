#define SYS_exit 1
#define SYS_fork 2
#define SYS_read 3
#define SYS_write 4
#define SYS_waitpid 7
#define SYS_getpid 20
#define SYS_setpgid 57
#define SYS_ioctl 54
#define TIOCSPGRP 0x5410

// x86 Linux Int 80h macros
static inline int syscall0(int num) {
    int ret;
    asm volatile("int $0x80" : "=a"(ret) : "a"(num) : "memory");
    return ret;
}

static inline int syscall1(int num, int arg1) {
    int ret;
    asm volatile("int $0x80" : "=a"(ret) : "a"(num), "b"(arg1) : "memory");
    return ret;
}

static inline int syscall2(int num, int arg1, int arg2) {
    int ret;
    asm volatile("int $0x80" : "=a"(ret) : "a"(num), "b"(arg1), "c"(arg2) : "memory");
    return ret;
}

static inline int syscall3(int num, int arg1, int arg2, int arg3) {
    int ret;
    asm volatile("int $0x80" : "=a"(ret) : "a"(num), "b"(arg1), "c"(arg2), "d"(arg3) : "memory");
    return ret;
}

static inline int tcsetpgrp(int fd, int pgrp) {
    return syscall3(SYS_ioctl, fd, TIOCSPGRP, (int)&pgrp);
}

void _start() {
    int parent_pid = syscall0(SYS_getpid);
    syscall2(SYS_setpgid, parent_pid, parent_pid);
    tcsetpgrp(0, parent_pid);

    int pid = syscall0(SYS_fork);
    if (pid == 0) {
        // child
        syscall2(SYS_setpgid, 0, 0);
        char buf[10];
        // this read should trigger SIGTTIN and stop the process by default
        syscall3(SYS_read, 0, (int)buf, sizeof(buf));

        char msg[] = "Child continued, exiting\n";
        syscall3(SYS_write, 1, (int)msg, sizeof(msg) - 1);
        syscall1(SYS_exit, 1);
        __builtin_unreachable();
    } else {
        // parent
        int status;
        // WUNTRACED is 2
        int wpid = syscall3(SYS_waitpid, pid, (int)&status, 2);

        // Check if stopped (WIFSTOPPED is (status & 0xff) == 0x7f)
        if (wpid == pid && (status & 0xff) == 0x7f) {
            char msg[] = "Received SIGTTIN (child stopped)\n";
            syscall3(SYS_write, 1, (int)msg, sizeof(msg) - 1);
        } else {
            char msg[] = "Child exited normally instead of stopping\n";
            syscall3(SYS_write, 1, (int)msg, sizeof(msg) - 1);
        }

        // Terminate child just in case
        syscall2(37, pid, 9); // SYS_kill = 37, SIGKILL = 9

        char msg2[] = "Parent exit\n";
        syscall3(SYS_write, 1, (int)msg2, sizeof(msg2) - 1);
        syscall1(SYS_exit, 0);
        __builtin_unreachable();
    }
}
