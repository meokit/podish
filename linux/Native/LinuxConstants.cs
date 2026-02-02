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
    ENOSYS = 38     /* Invalid system call number */
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
}
