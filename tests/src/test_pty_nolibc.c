#define SYS_exit 1
#define SYS_read 3
#define SYS_write 4
#define SYS_open 5
#define SYS_close 6
#define SYS_ioctl 54
#define SYS_stat64 195
#define SYS_fstat64 197

#define O_RDWR 2
#define O_NOCTTY 256
#define TIOCGPTN 0x80045430
#define TIOCSPTLCK 0x40045431

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

void print(const char* str) {
    int len = 0;
    while (str[len])
        len++;
    syscall3(SYS_write, 1, (int)str, len);
}

void print_num(int num) {
    char buf[16];
    int i = 14;
    buf[15] = 0;
    if (num == 0) {
        buf[i] = '0';
        i--;
    }
    while (num > 0 && i >= 0) {
        buf[i] = '0' + (num % 10);
        num /= 10;
        i--;
    }
    print(&buf[i + 1]);
}

void print_hex(unsigned int num) {
    char buf[16];
    int i = 14;
    buf[15] = 0;
    const char* hex = "0123456789ABCDEF";
    if (num == 0) {
        buf[i] = '0';
        i--;
    }
    while (num > 0 && i >= 0) {
        buf[i] = hex[num % 16];
        num /= 16;
        i--;
    }
    print("0x");
    print(&buf[i + 1]);
}

void _start() {
    print("Opening /dev/ptmx\n");
    int ptmx_fd = syscall3(SYS_open, (int)"/dev/ptmx", O_RDWR | O_NOCTTY, 0);

    if (ptmx_fd < 0) {
        print("Failed to open /dev/ptmx\n");
        syscall1(SYS_exit, 1);
    }

    // Unlock PTY
    int unlock = 0;
    int res = syscall3(SYS_ioctl, ptmx_fd, TIOCSPTLCK, (int)&unlock);
    if (res < 0) {
        print("Failed to unlock format\n");
        syscall1(SYS_exit, 1);
    }

    // Get PTY Number
    int pty_num = -1;
    res = syscall3(SYS_ioctl, ptmx_fd, TIOCGPTN, (int)&pty_num);
    if (res < 0 || pty_num < 0) {
        print("Failed to get PTY number\n");
        syscall1(SYS_exit, 1);
    }

    print("Got PTY number: ");
    print_num(pty_num);
    print("\n");

    // Construct slave path /dev/pts/N
    char pts_path[20] = "/dev/pts/";
    {
        int idx = 9; // start writing after "/dev/pts/"
        int temp = pty_num;
        if (temp == 0) {
            pts_path[idx++] = '0';
        } else {
            // Convert number to string (reverse, then fix)
            int start = idx;
            while (temp > 0) {
                pts_path[idx++] = '0' + (temp % 10);
                temp /= 10;
            }
            // Reverse the digits
            for (int l = start, r = idx - 1; l < r; l++, r--) {
                char c = pts_path[l];
                pts_path[l] = pts_path[r];
                pts_path[r] = c;
            }
        }
        pts_path[idx] = '\0';
    }

    print("Opening slave: ");
    print(pts_path);
    print("\n");

    int pts_fd = syscall3(SYS_open, (int)pts_path, O_RDWR | O_NOCTTY, 0);
    if (pts_fd < 0) {
        print("Failed to open slave\n");
        syscall1(SYS_exit, 1);
    }
    print("Successfully opened slave!\n");

    // Write to slave, read from master
    char msg[] = "ping";
    syscall3(SYS_write, pts_fd, (int)msg, 4);

    char buf[10];
    int n = syscall3(SYS_read, ptmx_fd, (int)buf, sizeof(buf));
    if (n > 0) {
        print("Master read: ");
        syscall3(SYS_write, 1, (int)buf, n);
        print("\n");
    }

    // Check Rdev through fstat64
    char statbuf[96];
    syscall2(SYS_fstat64, ptmx_fd, (int)statbuf);
    unsigned int ptmx_rdev = *(unsigned int*)(&statbuf[32]); // st_rdev offset in Linux stat64 is 32 (or similar
                                                             // depending on abi, we encoded it at 0 initially)
    // Actually in our WriteStat64, we don't have rdev offset completely right. I'll read from index 32 for now

    syscall1(SYS_exit, 0);
    __builtin_unreachable();
}
