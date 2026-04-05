namespace Fiberish.Native;

public enum Errno
{
    SUCCESS = 0,
    EPERM = 1, /* Operation not permitted */
    ENOENT = 2, /* No such file or directory */
    ESRCH = 3, /* No such process */
    EINTR = 4, /* Interrupted system call */
    EIO = 5, /* I/O error */
    ENXIO = 6, /* No such device or address */
    E2BIG = 7, /* Argument list too long */
    ENOEXEC = 8, /* Exec format error */
    EBADF = 9, /* Bad file number */
    ECHILD = 10, /* No child processes */
    EAGAIN = 11, /* Try again */
    ENOMEM = 12, /* Out of memory */
    EACCES = 13, /* Permission denied */
    EFAULT = 14, /* Bad address */
    ENOTBLK = 15, /* Block device required */
    EBUSY = 16, /* Device or resource busy */
    EEXIST = 17, /* File exists */
    EXDEV = 18, /* Cross-device link */
    ENODEV = 19, /* No such device */
    ENOTDIR = 20, /* Not a directory */
    EISDIR = 21, /* Is a directory */
    EINVAL = 22, /* Invalid argument */
    ENFILE = 23, /* File table overflow */
    EMFILE = 24, /* Too many open files */
    ENOTTY = 25, /* Not a typewriter */
    ENAMETOOLONG = 36, /* File name too long */
    ETXTBSY = 26, /* Text file busy */
    EFBIG = 27, /* File too large */
    ENOSPC = 28, /* No space left on device */
    ESPIPE = 29, /* Illegal seek */
    EROFS = 30, /* Read-only file system */
    EMLINK = 31, /* Too many links */
    EPIPE = 32, /* Broken pipe */
    EDEADLK = 35, /* Resource deadlock would occur */
    EDOM = 33, /* Math argument out of domain of func */
    ERANGE = 34, /* Math result not representable */
    ENOSYS = 38, /* Invalid system call number */
    ENOTEMPTY = 39, /* Directory not empty */
    ELOOP = 40, /* Too many levels of symbolic links */
    EIDRM = 43, /* Identifier removed */
    ENODATA = 61, /* No data available */
    ENOTSOCK = 88, /* Socket operation on non-socket */
    EDESTADDRREQ = 89, /* Destination address required */
    EMSGSIZE = 90, /* Message too long */
    EPROTOTYPE = 91, /* Protocol wrong type for socket */
    ENOPROTOOPT = 92, /* Protocol not available */
    EPROTONOSUPPORT = 93, /* Protocol not supported */
    ESOCKTNOSUPPORT = 94, /* Socket type not supported */
    EOPNOTSUPP = 95, /* Operation not supported on transport endpoint */
    EPFNOSUPPORT = 96, /* Protocol family not supported */
    EAFNOSUPPORT = 97, /* Address family not supported by protocol */
    EADDRINUSE = 98, /* Address already in use */
    EADDRNOTAVAIL = 99, /* Cannot assign requested address */
    ENETDOWN = 100, /* Network is down */
    ENETUNREACH = 101, /* Network is unreachable */
    ENETRESET = 102, /* Network dropped connection because of reset */
    ECONNABORTED = 103, /* Software caused connection abort */
    ECONNRESET = 104, /* Connection reset by peer */
    ENOBUFS = 105, /* No buffer space available */
    EISCONN = 106, /* Transport endpoint is already connected */
    ENOTCONN = 107, /* Transport endpoint is not connected */
    ESHUTDOWN = 108, /* Cannot send after transport endpoint shutdown */
    ETOOMANYREFS = 109, /* Too many references: cannot splice */
    ETIMEDOUT = 110, /* Connection timed out */
    ECONNREFUSED = 111, /* Connection refused */
    EHOSTDOWN = 112, /* Host is down */
    EHOSTUNREACH = 113, /* No route to host */
    EALREADY = 114, /* Operation already in progress */
    EINPROGRESS = 115, /* Operation now in progress */
    ERESTARTSYS = 512,
    ERESTARTNOINTR = 513,
    ERESTARTNOHAND = 514,
    ERESTART_RESTARTBLOCK = 516
}

public enum Signal
{
    SIGHUP = 1,
    SIGINT = 2,
    SIGQUIT = 3,
    SIGILL = 4,
    SIGTRAP = 5,
    SIGABRT = 6,
    SIGIOT = 6,
    SIGBUS = 7,
    SIGFPE = 8,
    SIGKILL = 9,
    SIGUSR1 = 10,
    SIGSEGV = 11,
    SIGUSR2 = 12,
    SIGPIPE = 13,
    SIGALRM = 14,
    SIGTERM = 15,
    SIGSTKFLT = 16,
    SIGCHLD = 17,
    SIGCONT = 18,
    SIGSTOP = 19,
    SIGTSTP = 20,
    SIGTTIN = 21,
    SIGTTOU = 22,
    SIGURG = 23,
    SIGXCPU = 24,
    SIGXFSZ = 25,
    SIGVTALRM = 26,
    SIGPROF = 27,
    SIGWINCH = 28,
    SIGIO = 29,
    SIGPOLL = 29,
    SIGPWR = 30,
    SIGSYS = 31,
    SIGUNUSED = 31,
    SIGRTMIN = 32
}

public enum SigProcMaskAction
{
    SIG_BLOCK = 0,
    SIG_UNBLOCK = 1,
    SIG_SETMASK = 2
}

public struct Iovec
{
    public uint BaseAddr;
    public uint Len;
}

public static class LinuxConstants
{
    public const int PageSize = 4096;
    public const uint PageMask = 0xFFFFF000;
    public const uint PageOffsetMask = 0xFFF;
    public const int PathMax = 4096;
    public const int NameMax = 255;
    public const int MaxSymlinkDepth = 40;

    // sigevent notify types
    public const int SIGEV_SIGNAL = 0;
    public const int SIGEV_NONE = 1;
    public const int SIGEV_THREAD = 2;
    public const int SIGEV_THREAD_ID = 4;

    // i386 TCGETS/TCSETS use old kernel struct termios:
    // 4*tcflag_t + c_line(1) + c_cc[19] = 36 bytes.
    public const int TERMIOS_SIZE_I386 = 36;
    public const int WINSIZE_SIZE = 8;

    public const uint MinMmapAddr = 0x10000;

    // Simulate a 2G/2G split: user mappings stay in the low 2 GiB.
    public const uint TaskSize32 = 0x80000000;

    // Auxv types
    public const uint AT_NULL = 0;
    public const uint AT_IGNORE = 1;
    public const uint AT_EXECFD = 2;
    public const uint AT_PHDR = 3;
    public const uint AT_PHENT = 4;
    public const uint AT_PHNUM = 5;
    public const uint AT_PAGESZ = 6;
    public const uint AT_BASE = 7;
    public const uint AT_FLAGS = 8;
    public const uint AT_ENTRY = 9;
    public const uint AT_NOTELF = 10;
    public const uint AT_UID = 11;
    public const uint AT_EUID = 12;
    public const uint AT_GID = 13;
    public const uint AT_EGID = 14;
    public const uint AT_PLATFORM = 15;
    public const uint AT_HWCAP = 16;
    public const uint AT_CLKTCK = 17;
    public const uint AT_FPUCW = 18;
    public const uint AT_DCACHEBSIZE = 19;
    public const uint AT_ICACHEBSIZE = 20;
    public const uint AT_UCACHEBSIZE = 21;
    public const uint AT_IGNOREPPC = 22;
    public const uint AT_SECURE = 23;
    public const uint AT_BASE_PLATFORM = 24;
    public const uint AT_RANDOM = 25;
    public const uint AT_HWCAP2 = 26;
    public const uint AT_EXECFN = 31;

    // Clone flags
    public const uint CLONE_VM = 0x00000100;
    public const uint CLONE_FS = 0x00000200;
    public const uint CLONE_FILES = 0x00000400;
    public const uint CLONE_SIGHAND = 0x00000800;
    public const uint CLONE_PTRACE = 0x00002000;
    public const uint CLONE_VFORK = 0x00004000;
    public const uint CLONE_PARENT = 0x00008000;
    public const uint CLONE_THREAD = 0x00010000;
    public const uint CLONE_NEWNS = 0x00020000;
    public const uint CLONE_SYSVSEM = 0x00040000;
    public const uint CLONE_SETTLS = 0x00080000;
    public const uint CLONE_PARENT_SETTID = 0x00100000;
    public const uint CLONE_CHILD_CLEARTID = 0x00200000;
    public const uint CLONE_DETACHED = 0x00400000;
    public const uint CLONE_UNTRACED = 0x00800000;
    public const uint CLONE_CHILD_SETTID = 0x01000000;

    // epoll op codes
    public const int EPOLL_CTL_ADD = 1;
    public const int EPOLL_CTL_DEL = 2;
    public const int EPOLL_CTL_MOD = 3;

    // epoll events
    public const uint EPOLLIN = 0x00000001;
    public const uint EPOLLPRI = 0x00000002;
    public const uint EPOLLOUT = 0x00000004;
    public const uint EPOLLERR = 0x00000008;
    public const uint EPOLLHUP = 0x00000010;
    public const uint EPOLLRDNORM = 0x00000040;
    public const uint EPOLLRDBAND = 0x00000080;
    public const uint EPOLLWRNORM = 0x00000100;
    public const uint EPOLLWRBAND = 0x00000200;
    public const uint EPOLLMSG = 0x00000400;
    public const uint EPOLLRDHUP = 0x00002000;
    public const uint EPOLLEXCLUSIVE = 1u << 28;
    public const uint EPOLLWAKEUP = 1u << 29;
    public const uint EPOLLONESHOT = 1u << 30;
    public const uint EPOLLET = 1u << 31;

    // Futex ops
    public const int FUTEX_WAIT = 0;
    public const int FUTEX_WAKE = 1;
    public const int FUTEX_FD = 2;
    public const int FUTEX_REQUEUE = 3;
    public const int FUTEX_CMP_REQUEUE = 4;
    public const int FUTEX_WAKE_OP = 5;
    public const int FUTEX_LOCK_PI = 6;
    public const int FUTEX_UNLOCK_PI = 7;
    public const int FUTEX_TRYLOCK_PI = 8;
    public const int FUTEX_WAIT_BITSET = 9;
    public const int FUTEX_WAKE_BITSET = 10;
    public const int FUTEX_WAIT_REQUEUE_PI = 11;
    public const int FUTEX_CMP_REQUEUE_PI = 12;

    public const int FUTEX_PRIVATE_FLAG = 128;
    public const int FUTEX_CLOCK_REALTIME = 256;

    public const uint FUTEX_BITSET_MATCH_ANY = 0xffffffff;

    public const int FUTEX_OP_SET = 0;
    public const int FUTEX_OP_ADD = 1;
    public const int FUTEX_OP_OR = 2;
    public const int FUTEX_OP_ANDN = 3;
    public const int FUTEX_OP_XOR = 4;
    public const int FUTEX_OP_OPARG_SHIFT = 8;

    public const int FUTEX_OP_CMP_EQ = 0;
    public const int FUTEX_OP_CMP_NE = 1;
    public const int FUTEX_OP_CMP_LT = 2;
    public const int FUTEX_OP_CMP_LE = 3;
    public const int FUTEX_OP_CMP_GT = 4;
    public const int FUTEX_OP_CMP_GE = 5;

    public const uint FUTEX_OWNER_DIED = 0x40000000;
    public const uint FUTEX_WAITERS = 0x80000000;
    public const uint FUTEX_TID_MASK = 0x3fffffff;

    // Mount flags
    public const uint MS_RDONLY = 1;
    public const uint MS_NOSUID = 2;
    public const uint MS_NODEV = 4;
    public const uint MS_NOEXEC = 8;
    public const uint MS_SYNCHRONOUS = 16;
    public const uint MS_REMOUNT = 32;
    public const uint MS_MANDLOCK = 64;
    public const uint MS_DIRSYNC = 128;
    public const uint MS_NOSYMFOLLOW = 256;
    public const uint MS_NOATIME = 1024;
    public const uint MS_NODIRATIME = 2048;
    public const uint MS_BIND = 4096;
    public const uint MS_MOVE = 8192;
    public const uint MS_REC = 16384;
    public const uint MS_SILENT = 32768;

    // SigAction flags
    public const uint SA_NOCLDSTOP = 0x00000001;
    public const uint SA_NOCLDWAIT = 0x00000002;
    public const uint SA_SIGINFO = 0x00000004;
    public const uint SA_ONSTACK = 0x08000000;
    public const uint SA_RESTART = 0x10000000;
    public const uint SA_NODEFER = 0x40000000;
    public const uint SA_RESETHAND = 0x80000000;
    public const uint SA_RESTORER = 0x04000000;

    // AT_* flags for path resolution
    public const uint AT_FDCWD = 0xFFFFFF9C; // -100
    public const uint AT_SYMLINK_NOFOLLOW = 0x100;
    public const uint AT_REMOVEDIR = 0x200;
    public const uint AT_SYMLINK_FOLLOW = 0x400;

    // renameat2 flags
    public const uint RENAME_NOREPLACE = 1;
    public const uint RENAME_EXCHANGE = 2;
    public const uint RENAME_WHITEOUT = 4;
    public const uint AT_NO_AUTOMOUNT = 0x800;
    public const uint AT_EMPTY_PATH = 0x1000;
    public const uint AT_STATX_SYNC_TYPE = 0x6000;
    public const uint AT_STATX_SYNC_AS_STAT = 0x0000;
    public const uint AT_STATX_FORCE_SYNC = 0x2000;
    public const uint AT_STATX_DONT_SYNC = 0x4000;

    // prctl
    public const int PR_SET_NAME = 15;
    public const int PR_GET_NAME = 16;

    // membarrier commands (linux/membarrier.h)
    public const int MEMBARRIER_CMD_QUERY = 0;
    public const int MEMBARRIER_CMD_GLOBAL = 1 << 0;
    public const int MEMBARRIER_CMD_GLOBAL_EXPEDITED = 1 << 1;
    public const int MEMBARRIER_CMD_REGISTER_GLOBAL_EXPEDITED = 1 << 2;
    public const int MEMBARRIER_CMD_PRIVATE_EXPEDITED = 1 << 3;
    public const int MEMBARRIER_CMD_REGISTER_PRIVATE_EXPEDITED = 1 << 4;
    public const int MEMBARRIER_CMD_PRIVATE_EXPEDITED_SYNC_CORE = 1 << 5;
    public const int MEMBARRIER_CMD_REGISTER_PRIVATE_EXPEDITED_SYNC_CORE = 1 << 6;
    public const int MEMBARRIER_CMD_PRIVATE_EXPEDITED_RSEQ = 1 << 7;
    public const int MEMBARRIER_CMD_REGISTER_PRIVATE_EXPEDITED_RSEQ = 1 << 8;

    // personality
    public const uint PER_LINUX = 0x0000;

    // Statx mask bits
    public const uint STATX_TYPE = 0x00000001;
    public const uint STATX_MODE = 0x00000002;
    public const uint STATX_NLINK = 0x00000004;
    public const uint STATX_UID = 0x00000008;
    public const uint STATX_GID = 0x00000010;
    public const uint STATX_ATIME = 0x00000020;
    public const uint STATX_MTIME = 0x00000040;
    public const uint STATX_CTIME = 0x00000080;
    public const uint STATX_INO = 0x00000100;
    public const uint STATX_SIZE = 0x00000200;
    public const uint STATX_BLOCKS = 0x00000400;
    public const uint STATX_BASIC_STATS = 0x000007ff;
    public const uint STATX_BTIME = 0x00000800;
    public const uint STATX_MNT_ID = 0x00001000;
    public const uint STATX_DIOALIGN = 0x00002000;
    public const uint STATX_MNT_ID_UNIQUE = 0x00004000;

    // Clock IDs
    public const int CLOCK_REALTIME = 0;
    public const int CLOCK_MONOTONIC = 1;
    public const int CLOCK_PROCESS_CPUTIME_ID = 2;
    public const int CLOCK_THREAD_CPUTIME_ID = 3;
    public const int CLOCK_MONOTONIC_RAW = 4;
    public const int CLOCK_REALTIME_COARSE = 5;
    public const int CLOCK_MONOTONIC_COARSE = 6;
    public const int CLOCK_BOOTTIME = 7;

    // TTY IOCTLs
    public const uint TCGETS = 0x5401;
    public const uint TCSETS = 0x5402;
    public const uint TCSETSW = 0x5403;
    public const uint TCSETSF = 0x5404;
    public const uint TIOCGWINSZ = 0x5413;
    public const uint TIOCSWINSZ = 0x5414;
    public const uint TIOCGPGRP = 0x540F;
    public const uint TIOCSPGRP = 0x5410;
    public const uint TIOCSCTTY = 0x540E;
    public const uint FIONREAD = 0x541B;
    public const uint FIONBIO = 0x5421;

    // Signal constants
    public const int SIGALRM = 14;

    // virtual FDs definitions (eventfd, timerfd, signalfd)
    public const int EFD_SEMAPHORE = 1;
    public const int EFD_NONBLOCK = 2048; // 04000 octal
    public const int EFD_CLOEXEC = 524288; // 02000000 octal
    public const int TFD_NONBLOCK = 2048;
    public const int TFD_CLOEXEC = 524288;
    public const int SFD_NONBLOCK = 2048;
    public const int SFD_CLOEXEC = 524288;

    // flock commands
    public const int LOCK_SH = 1;
    public const int LOCK_EX = 2;
    public const int LOCK_NB = 4;
    public const int LOCK_UN = 8;

    // Poll events
    public const int POLLIN = 0x0001;
    public const int POLLOUT = 0x0004;

    // PTY ioctls (from linux/tty.h)
    public const uint TIOCGPTN = 0x80045430; // Get PTY number
    public const uint TIOCSPTLCK = 0x40045431; // Lock/unlock PTY
    public const uint TIOCGPTLCK = 0x80045432; // Get PTY lock status

    // IPC commands for sys_ipc multiplexer (from linux/ipc.h)
    public const int SEMOP = 1;
    public const int SEMGET = 2;
    public const int SEMCTL = 3;
    public const int SEMTIMEDOP = 4;
    public const int SHMAT = 21;
    public const int SHMDT = 22;
    public const int SHMGET = 23;
    public const int SHMCTL = 24;

    // semctl commands (from linux/sem.h)
    public const int GETPID = 11;
    public const int GETVAL = 12;
    public const int GETALL = 13;
    public const int GETNCNT = 14;
    public const int GETZCNT = 15;
    public const int SETVAL = 16;
    public const int SETALL = 17;

    // IPC flags (from linux/ipc.h) - these are octal values in Linux
    public const int IPC_CREAT = 0x200; // 01000 octal = create if key is nonexistent
    public const int IPC_EXCL = 0x400; // 02000 octal = fail if key exists
    public const int IPC_NOWAIT = 0x800; // 04000 octal = return error on wait
    public const int IPC_PRIVATE = 0; // private key

    // shmctl commands (from linux/ipc.h)
    public const int IPC_RMID = 0; // remove resource
    public const int IPC_SET = 1; // set ipc_perm options
    public const int IPC_STAT = 2; // get ipc_perm options
    public const int IPC_INFO = 3; // see ipcs

    // IPC_64 flag for shmctl (from linux/ipc.h)
    // On i386, glibc uses IPC_64 | cmd to indicate 64-bit ipc_perm layout
    public const int IPC_64 = 0x0100;

    // shmctl commands (from linux/shm.h)
    public const int SHM_LOCK = 11;
    public const int SHM_UNLOCK = 12;
    public const int SHM_STAT = 13;
    public const int SHM_INFO = 14;
    public const int SHM_STAT_ANY = 15;

    // shmat flags (from linux/shm.h)
    public const int SHM_RDONLY = 010000; // read-only access
    public const int SHM_RND = 020000; // round attach address to SHMLBA
    public const int SHM_REMAP = 040000; // take-over region on attach
    public const int SHM_EXEC = 0100000; // execution access

    // shmget flags (from linux/shm.h)
    public const int SHM_R = 0400; // or S_IRUGO
    public const int SHM_W = 0200; // or S_IWUGO
    public const int SHM_HUGETLB = 04000; // segment will use huge TLB pages
    public const int SHM_NORESERVE = 010000; // don't check for reservations

    // IPC version flags
    public const int IPC_OLD = 0;

    // Socket message flags (from linux/socket.h)
    public const int MSG_OOB = 0x01;
    public const int MSG_PEEK = 0x02;
    public const int MSG_DONTROUTE = 0x04;
    public const int MSG_CTRUNC = 0x08;
    public const int MSG_PROXY = 0x10;
    public const int MSG_TRUNC = 0x20;
    public const int MSG_DONTWAIT = 0x40;
    public const int MSG_EOR = 0x80;
    public const int MSG_WAITALL = 0x100;
    public const int MSG_FIN = 0x200;
    public const int MSG_SYN = 0x400;
    public const int MSG_CONFIRM = 0x800;
    public const int MSG_RST = 0x1000;
    public const int MSG_ERRQUEUE = 0x2000;
    public const int MSG_NOSIGNAL = 0x4000;

    // Socket domains/types/protocols
    public const int AF_UNIX = 1;
    public const int AF_INET = 2;
    public const int AF_NETLINK = 16;
    public const int AF_INET6 = 10;

    public const int SOCK_STREAM = 1;
    public const int SOCK_DGRAM = 2;
    public const int SOCK_RAW = 3;
    public const int SOCK_SEQPACKET = 5;
    public const int SOCK_NONBLOCK = 0x800;
    public const int SOCK_CLOEXEC = 0x80000;

    public const int IPPROTO_ICMP = 1;
    public const int IPPROTO_IP = 0;
    public const int IPPROTO_TCP = 6;
    public const int IPPROTO_UDP = 17;
    public const int IPPROTO_IPV6 = 41;
    public const int IPPROTO_ICMPV6 = 58;
    public const int ICMPV6_FILTER = 1;
    public const int IP_STRIPHDR = 23;

    public const int SOL_SOCKET = 1;
    public const int SCM_RIGHTS = 1;
    public const int SCM_CREDENTIALS = 2;
    public const int SCM_MAX_FD = 253;
    public const int SO_TYPE = 3;
    public const int SO_ERROR = 4;
    public const int SO_REUSEADDR = 2;
    public const int SO_KEEPALIVE = 6;
    public const int SO_OOBINLINE = 7;
    public const int SO_SNDBUF = 8;
    public const int SO_RCVBUF = 9;
    public const int SO_LINGER = 13;
    public const int SO_PASSCRED = 16;
    public const int SO_PEERCRED = 17;
    public const int SO_REUSEPORT = 15;
    public const int SO_RCVTIMEO = 20;
    public const int SO_SNDTIMEO = 21;

    public const int TCP_NODELAY = 1;
    public const int TCP_KEEPIDLE = 8;
    public const int TCP_KEEPINTVL = 9;
    public const int TCP_KEEPCNT = 10;

    public const int IPV6_V6ONLY = 26;
    public const int MSG_MORE = 0x8000;
    public const int MSG_WAITFORONE = 0x10000;
    public const int MSG_BATCH = 0x40000;
    public const int MSG_ZEROCOPY = 0x4000000;
    public const int MSG_FASTOPEN = 0x20000000;
    public const int MSG_CMSG_CLOEXEC = 0x40000000;

    // netlink / rtnetlink
    public const int NETLINK_ROUTE = 0;
    public const ushort RTM_NEWLINK = 16;
    public const ushort RTM_GETLINK = 18;
    public const ushort RTM_NEWADDR = 20;
    public const ushort RTM_GETADDR = 22;
    public const ushort NLMSG_DONE = 3;
    public const ushort NLM_F_REQUEST = 0x0001;
    public const ushort NLM_F_MULTI = 0x0002;
    public const ushort NLM_F_ROOT = 0x0100;
    public const ushort NLM_F_MATCH = 0x0200;
    public const ushort NLM_F_DUMP = NLM_F_ROOT | NLM_F_MATCH;
    public const uint IFF_UP = 0x1;
    public const uint IFF_RUNNING = 0x40;
    public const uint IFF_LOOPBACK = 0x8;
    public const ushort ARPHRD_ETHER = 1;
    public const ushort ARPHRD_LOOPBACK = 772;
    public const ushort IFLA_ADDRESS = 1;
    public const ushort IFLA_IFNAME = 3;
    public const ushort IFLA_MTU = 4;
    public const ushort IFA_ADDRESS = 1;
    public const ushort IFA_LOCAL = 2;
    public const ushort IFA_LABEL = 3;
    public const byte RT_SCOPE_UNIVERSE = 0;
    public const byte RT_SCOPE_HOST = 254;

    // network ioctl (i386)
    public const uint SIOCGIFCONF = 0x8912;
    public const uint SIOCGIFFLAGS = 0x8913;
    public const uint SIOCGIFADDR = 0x8915;
    public const uint SIOCGIFNETMASK = 0x891B;
    public const uint SIOCGIFMTU = 0x8921;
    public const uint SIOCGIFTXQLEN = 0x8942;
    public const int IFNAMSIZ = 16;
}