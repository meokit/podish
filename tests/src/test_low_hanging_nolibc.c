#define SYS_exit 1
#define SYS_write 4
#define SYS_getpid 20
#define SYS_nice 34
#define SYS_getpriority 96
#define SYS_setpriority 97
#define SYS_personality 136
#define SYS_prctl 172
#define SYS_readahead 225
#define SYS_fadvise64 250
#define SYS_utimes 271
#define SYS_getcpu 318
#define SYS_prlimit64 340
#define SYS_get_thread_area 244

#define PR_SET_NAME 15
#define PR_GET_NAME 16

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

static inline int syscall4(int num, int arg1, int arg2, int arg3, int arg4) {
    int ret;
    asm volatile("int $0x80" : "=a"(ret) : "a"(num), "b"(arg1), "c"(arg2), "d"(arg3), "S"(arg4) : "memory");
    return ret;
}

static inline int syscall5(int num, int arg1, int arg2, int arg3, int arg4, int arg5) {
    int ret;
    asm volatile("int $0x80" : "=a"(ret) : "a"(num), "b"(arg1), "c"(arg2), "d"(arg3), "S"(arg4), "D"(arg5) : "memory");
    return ret;
}

void print(const char* s) {
    int len = 0;
    while (s[len])
        len++;
    syscall3(SYS_write, 1, (int)s, len);
}

void print_int(int n) {
    char buf[12];
    int i = 10;
    buf[11] = 0;
    if (n == 0) {
        buf[i--] = '0';
    } else {
        int neg = 0;
        if (n < 0) {
            neg = 1;
            n = -n;
        }
        while (n > 0) {
            buf[i--] = (n % 10) + '0';
            n /= 10;
        }
        if (neg)
            buf[i--] = '-';
    }
    print(&buf[i + 1]);
}

void _start() {
    print("Starting Batch 2 nolibc tests...\n");

    // 1. nice/priority
    print("Priority: ");
    print_int(syscall2(SYS_getpriority, 0, 0));
    print("\n");
    syscall1(SYS_nice, 5);
    print("nice(5) called\n");

    // 2. personality
    print("Personality: ");
    print_int(syscall1(SYS_personality, 0xffffffff));
    print("\n");

    // 3. getcpu
    unsigned int cpu = 99, node = 99;
    if (syscall3(SYS_getcpu, (int)&cpu, (int)&node, 0) == 0) {
        print("getcpu: CPU=");
        print_int(cpu);
        print(" Node=");
        print_int(node);
        print("\n");
    }

    // 4. prctl
    char name[16];
    for (int i = 0; i < 16; i++)
        name[i] = 0;
    syscall5(SYS_prctl, PR_SET_NAME, (int)"test-proc", 0, 0, 0);
    syscall5(SYS_prctl, PR_GET_NAME, (int)name, 0, 0, 0);
    print("Process name: ");
    print(name);
    print("\n");

    // 5. readahead/fadvise (no failure if stubbed correctly)
    print("Readahead/Fadvise called\n");
    syscall3(SYS_readahead, 0, 0, 0);
    syscall5(SYS_fadvise64, 0, 0, 0, 0, 0);

    // 6. get_thread_area
    unsigned int uinfo[4] = {0};
    if (syscall1(SYS_get_thread_area, (int)uinfo) == 0) {
        print("get_thread_area base: ");
        print_int(uinfo[1]);
        print("\n");
    }

    print("Batch 2 tests completed.\n");
    syscall1(SYS_exit, 0);
}
