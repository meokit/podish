namespace Bifrost.Native;

public enum Errno : int
{
    SUCCESS = 0,
    EPERM = 1,      /* Operation not permitted */
    ENOENT = 2,     /* No such file or directory */
    ESRCH = 3,      /* No such process */
    EINTR = 4,      /* Interrupted system call */
    EIO = 5,        /* I/O error */
    ENXIO = 6,      /* No such device or address */
    E2BIG = 7,      /* Argument list too long */
    ENOEXEC = 8,    /* Exec format error */
    EBADF = 9,      /* Bad file number */
    ECHILD = 10,    /* No child processes */
    EAGAIN = 11,    /* Try again */
    ENOMEM = 12,    /* Out of memory */
    EACCES = 13,    /* Permission denied */
    EFAULT = 14,    /* Bad address */
    ENOTBLK = 15,   /* Block device required */
    EBUSY = 16,     /* Device or resource busy */
    EEXIST = 17,    /* File exists */
    EXDEV = 18,     /* Cross-device link */
    ENODEV = 19,    /* No such device */
    ENOTDIR = 20,   /* Not a directory */
    EISDIR = 21,    /* Is a directory */
    EINVAL = 22,    /* Invalid argument */
    ENFILE = 23,    /* File table overflow */
    EMFILE = 24,    /* Too many open files */
    ENOTTY = 25,    /* Not a typewriter */
    ETXTBSY = 26,   /* Text file busy */
    EFBIG = 27,     /* File too large */
    ENOSPC = 28,    /* No space left on device */
    ESPIPE = 29,    /* Illegal seek */
    EROFS = 30,     /* Read-only file system */
    EMLINK = 31,    /* Too many links */
    EPIPE = 32,     /* Broken pipe */
    EDOM = 33,      /* Math argument out of domain of func */
    ERANGE = 34,    /* Math result not representable */
    ENOSYS = 38,    /* Invalid system call number */
    ELOOP = 40,     /* Too many levels of symbolic links */
    ENOTEMPTY = 39, /* Directory not empty */
    ERESTARTSYS = 512,
}

public enum Signal : int
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
}

public enum SigProcMaskAction : int
{
    SIG_BLOCK = 0,
    SIG_UNBLOCK = 1,
    SIG_SETMASK = 2,
}

public static class LinuxConstants
{
    public const int PageSize = 4096;
    public const uint PageMask = 0xFFFFF000;
    public const uint PageOffsetMask = 0xFFF;

    public const uint MinMmapAddr = 0x10000;
    public const uint TaskSize32 = 0xC0000000;

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
    public const uint AT_NO_AUTOMOUNT = 0x800;
    public const uint AT_EMPTY_PATH = 0x1000;
    public const uint AT_STATX_SYNC_TYPE = 0x6000;
    public const uint AT_STATX_SYNC_AS_STAT = 0x0000;
    public const uint AT_STATX_FORCE_SYNC = 0x2000;
    public const uint AT_STATX_DONT_SYNC = 0x4000;

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
}
