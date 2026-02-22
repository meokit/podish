using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Fiberish.Native;
using Microsoft.Extensions.Logging;

namespace Fiberish.Syscalls;

public static class SyscallTracer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
        { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static void TraceEntry(ILogger logger, SyscallManager sys, int tid, uint nr, uint a1, uint a2, uint a3,
        uint a4, uint a5, uint a6)
    {
        var sb = new StringBuilder();
        sb.Append($"[{tid}] [Syscall] {GetSyscallName(nr)}(");

        switch (nr)
        {
            case X86SyscallNumbers.read:
                // read(fd, buf, count)
                sb.Append($"{a1}, 0x{a2:X}, {a3}");
                break;
            case X86SyscallNumbers.write:
                // write(fd, buf, count)
                sb.Append($"{a1}, {ReadStringOrBuffer(sys, a2, a3)}, {a3}");
                break;
            case X86SyscallNumbers.open:
                // open(path, flags, mode)
                sb.Append($"{ReadString(sys, a1)}, 0x{a2:X}, 0x{a3:X}");
                break;
            case X86SyscallNumbers.close:
                // close(fd)
                sb.Append($"{a1}");
                break;
            case X86SyscallNumbers.execve:
                // execve(filename, argv, envp)
                sb.Append($"{ReadString(sys, a1)}, [Argv], [Envp]");
                break;
            case X86SyscallNumbers.chdir:
                // chdir(path)
                sb.Append($"{ReadString(sys, a1)}");
                break;
            case X86SyscallNumbers.mkdir:
                // mkdir(path, mode)
                sb.Append($"{ReadString(sys, a1)}, 0x{a2:X}");
                break;
            case X86SyscallNumbers.rmdir:
                // rmdir(path)
                sb.Append($"{ReadString(sys, a1)}");
                break;
            case X86SyscallNumbers.unlink:
                // unlink(path)
                sb.Append($"{ReadString(sys, a1)}");
                break;
            case X86SyscallNumbers.stat:
            case X86SyscallNumbers.lstat:
                // stat(path, buf)
                sb.Append($"{ReadString(sys, a1)}, 0x{a2:X}");
                break;
            case X86SyscallNumbers.stat64:
            case X86SyscallNumbers.lstat64:
                // stat64(path, buf)
                sb.Append($"{ReadString(sys, a1)}, 0x{a2:X}");
                break;
            case X86SyscallNumbers.access:
                // access(path, mode)
                sb.Append($"{ReadString(sys, a1)}, 0x{a2:X}");
                break;
            case X86SyscallNumbers.openat:
                // openat(dirfd, path, flags, mode)
                // dirfd: -100 is AT_FDCWD
                var dirfd = (int)a1 == -100 ? "AT_FDCWD" : a1.ToString();
                sb.Append($"{dirfd}, {ReadString(sys, a2)}, 0x{a3:X}, 0x{a4:X}");
                break;
            default:
                sb.Append($"{a1:X}, {a2:X}, {a3:X}, {a4:X}, {a5:X}, {a6:X}");
                break;
        }

        sb.Append(")");
        logger.LogTrace(sb.ToString());
    }

    public static void TraceExit(ILogger logger, SyscallManager sys, int tid, uint nr, int ret, uint a1, uint a2,
        uint a3)
    {
        var sb = new StringBuilder();
        sb.Append($"[{tid}] = ");

        if (ret < 0 && ret > -4096)
        {
            sb.Append($"{ret} ({(Errno)(-ret)})");
        }
        else
        {
            sb.Append($"{ret}");

            // For read, show the data read
            if (nr == X86SyscallNumbers.read && ret > 0) sb.Append($" {ReadStringOrBuffer(sys, a2, (uint)ret)}");
        }

        logger.LogTrace(sb.ToString());
    }

    private static string GetSyscallName(uint nr)
    {
        return nr switch
        {
            X86SyscallNumbers.restart_syscall => "restart_syscall",
            X86SyscallNumbers.exit => "exit",
            X86SyscallNumbers.fork => "fork",
            X86SyscallNumbers.read => "read",
            X86SyscallNumbers.write => "write",
            X86SyscallNumbers.open => "open",
            X86SyscallNumbers.close => "close",
            X86SyscallNumbers.waitpid => "waitpid",
            X86SyscallNumbers.creat => "creat",
            X86SyscallNumbers.link => "link",
            X86SyscallNumbers.unlink => "unlink",
            X86SyscallNumbers.execve => "execve",
            X86SyscallNumbers.chdir => "chdir",
            X86SyscallNumbers.time => "time",
            X86SyscallNumbers.mknod => "mknod",
            X86SyscallNumbers.chmod => "chmod",
            X86SyscallNumbers.lchown => "lchown",
            X86SyscallNumbers.@break => "break",
            X86SyscallNumbers.oldstat => "oldstat",
            X86SyscallNumbers.lseek => "lseek",
            X86SyscallNumbers.getpid => "getpid",
            X86SyscallNumbers.mount => "mount",
            X86SyscallNumbers.umount => "umount",
            X86SyscallNumbers.setuid => "setuid",
            X86SyscallNumbers.getuid => "getuid",
            X86SyscallNumbers.stime => "stime",
            X86SyscallNumbers.ptrace => "ptrace",
            X86SyscallNumbers.alarm => "alarm",
            X86SyscallNumbers.oldfstat => "oldfstat",
            X86SyscallNumbers.pause => "pause",
            X86SyscallNumbers.utime => "utime",
            X86SyscallNumbers.stty => "stty",
            X86SyscallNumbers.gtty => "gtty",
            X86SyscallNumbers.access => "access",
            X86SyscallNumbers.nice => "nice",
            X86SyscallNumbers.ftime => "ftime",
            X86SyscallNumbers.sync => "sync",
            X86SyscallNumbers.kill => "kill",
            X86SyscallNumbers.rename => "rename",
            X86SyscallNumbers.mkdir => "mkdir",
            X86SyscallNumbers.rmdir => "rmdir",
            X86SyscallNumbers.dup => "dup",
            X86SyscallNumbers.pipe => "pipe",
            X86SyscallNumbers.times => "times",
            X86SyscallNumbers.prof => "prof",
            X86SyscallNumbers.brk => "brk",
            X86SyscallNumbers.setgid => "setgid",
            X86SyscallNumbers.getgid => "getgid",
            X86SyscallNumbers.signal => "signal",
            X86SyscallNumbers.geteuid => "geteuid",
            X86SyscallNumbers.getegid => "getegid",
            X86SyscallNumbers.acct => "acct",
            X86SyscallNumbers.umount2 => "umount2",
            X86SyscallNumbers.@lock => "lock",
            X86SyscallNumbers.ioctl => "ioctl",
            X86SyscallNumbers.fcntl => "fcntl",
            X86SyscallNumbers.mpx => "mpx",
            X86SyscallNumbers.setpgid => "setpgid",
            X86SyscallNumbers.ulimit => "ulimit",
            X86SyscallNumbers.oldolduname => "oldolduname",
            X86SyscallNumbers.umask => "umask",
            X86SyscallNumbers.chroot => "chroot",
            X86SyscallNumbers.ustat => "ustat",
            X86SyscallNumbers.dup2 => "dup2",
            X86SyscallNumbers.getppid => "getppid",
            X86SyscallNumbers.getpgrp => "getpgrp",
            X86SyscallNumbers.setsid => "setsid",
            X86SyscallNumbers.sigaction => "sigaction",
            X86SyscallNumbers.sgetmask => "sgetmask",
            X86SyscallNumbers.ssetmask => "ssetmask",
            X86SyscallNumbers.setreuid => "setreuid",
            X86SyscallNumbers.setregid => "setregid",
            X86SyscallNumbers.sigsuspend => "sigsuspend",
            X86SyscallNumbers.sigpending => "sigpending",
            X86SyscallNumbers.sethostname => "sethostname",
            X86SyscallNumbers.setrlimit => "setrlimit",
            X86SyscallNumbers.getrlimit => "getrlimit",
            X86SyscallNumbers.getrusage => "getrusage",
            X86SyscallNumbers.gettimeofday => "gettimeofday",
            X86SyscallNumbers.settimeofday => "settimeofday",
            X86SyscallNumbers.getgroups => "getgroups",
            X86SyscallNumbers.setgroups => "setgroups",
            X86SyscallNumbers.select => "select",
            X86SyscallNumbers.symlink => "symlink",
            X86SyscallNumbers.oldlstat => "oldlstat",
            X86SyscallNumbers.readlink => "readlink",
            X86SyscallNumbers.uselib => "uselib",
            X86SyscallNumbers.swapon => "swapon",
            X86SyscallNumbers.reboot => "reboot",
            X86SyscallNumbers.readdir => "readdir",
            X86SyscallNumbers.mmap => "mmap",
            X86SyscallNumbers.munmap => "munmap",
            X86SyscallNumbers.truncate => "truncate",
            X86SyscallNumbers.ftruncate => "ftruncate",
            X86SyscallNumbers.fchmod => "fchmod",
            X86SyscallNumbers.fchown => "fchown",
            X86SyscallNumbers.getpriority => "getpriority",
            X86SyscallNumbers.setpriority => "setpriority",
            X86SyscallNumbers.profil => "profil",
            X86SyscallNumbers.statfs => "statfs",
            X86SyscallNumbers.fstatfs => "fstatfs",
            X86SyscallNumbers.ioperm => "ioperm",
            X86SyscallNumbers.socketcall => "socketcall",
            X86SyscallNumbers.syslog => "syslog",
            X86SyscallNumbers.setitimer => "setitimer",
            X86SyscallNumbers.getitimer => "getitimer",
            X86SyscallNumbers.stat => "stat",
            X86SyscallNumbers.lstat => "lstat",
            X86SyscallNumbers.fstat => "fstat",
            X86SyscallNumbers.olduname => "olduname",
            X86SyscallNumbers.iopl => "iopl",
            X86SyscallNumbers.vhangup => "vhangup",
            X86SyscallNumbers.idle => "idle",
            X86SyscallNumbers.vm86old => "vm86old",
            X86SyscallNumbers.wait4 => "wait4",
            X86SyscallNumbers.swapoff => "swapoff",
            X86SyscallNumbers.sysinfo => "sysinfo",
            X86SyscallNumbers.ipc => "ipc",
            X86SyscallNumbers.fsync => "fsync",
            X86SyscallNumbers.sigreturn => "sigreturn",
            X86SyscallNumbers.clone => "clone",
            X86SyscallNumbers.setdomainname => "setdomainname",
            X86SyscallNumbers.uname => "uname",
            X86SyscallNumbers.modify_ldt => "modify_ldt",
            X86SyscallNumbers.adjtimex => "adjtimex",
            X86SyscallNumbers.mprotect => "mprotect",
            X86SyscallNumbers.sigprocmask => "sigprocmask",
            X86SyscallNumbers.create_module => "create_module",
            X86SyscallNumbers.init_module => "init_module",
            X86SyscallNumbers.delete_module => "delete_module",
            X86SyscallNumbers.get_kernel_syms => "get_kernel_syms",
            X86SyscallNumbers.quotactl => "quotactl",
            X86SyscallNumbers.getpgid => "getpgid",
            X86SyscallNumbers.fchdir => "fchdir",
            X86SyscallNumbers.bdflush => "bdflush",
            X86SyscallNumbers.sysfs => "sysfs",
            X86SyscallNumbers.personality => "personality",
            X86SyscallNumbers.afs_syscall => "afs_syscall",
            X86SyscallNumbers.setfsuid => "setfsuid",
            X86SyscallNumbers.setfsgid => "setfsgid",
            X86SyscallNumbers._llseek => "_llseek",
            X86SyscallNumbers.getdents => "getdents",
            X86SyscallNumbers._newselect => "_newselect",
            X86SyscallNumbers.flock => "flock",
            X86SyscallNumbers.msync => "msync",
            X86SyscallNumbers.readv => "readv",
            X86SyscallNumbers.writev => "writev",
            X86SyscallNumbers.getsid => "getsid",
            X86SyscallNumbers.fdatasync => "fdatasync",
            X86SyscallNumbers._sysctl => "_sysctl",
            X86SyscallNumbers.mlock => "mlock",
            X86SyscallNumbers.munlock => "munlock",
            X86SyscallNumbers.mlockall => "mlockall",
            X86SyscallNumbers.munlockall => "munlockall",
            X86SyscallNumbers.sched_setparam => "sched_setparam",
            X86SyscallNumbers.sched_getparam => "sched_getparam",
            X86SyscallNumbers.sched_setscheduler => "sched_setscheduler",
            X86SyscallNumbers.sched_getscheduler => "sched_getscheduler",
            X86SyscallNumbers.sched_yield => "sched_yield",
            X86SyscallNumbers.sched_get_priority_max => "sched_get_priority_max",
            X86SyscallNumbers.sched_get_priority_min => "sched_get_priority_min",
            X86SyscallNumbers.sched_rr_get_interval => "sched_rr_get_interval",
            X86SyscallNumbers.nanosleep => "nanosleep",
            X86SyscallNumbers.mremap => "mremap",
            X86SyscallNumbers.setresuid => "setresuid",
            X86SyscallNumbers.getresuid => "getresuid",
            X86SyscallNumbers.vm86 => "vm86",
            X86SyscallNumbers.query_module => "query_module",
            X86SyscallNumbers.poll => "poll",
            X86SyscallNumbers.nfsservctl => "nfsservctl",
            X86SyscallNumbers.setresgid => "setresgid",
            X86SyscallNumbers.getresgid => "getresgid",
            X86SyscallNumbers.prctl => "prctl",
            X86SyscallNumbers.rt_sigreturn => "rt_sigreturn",
            X86SyscallNumbers.rt_sigaction => "rt_sigaction",
            X86SyscallNumbers.rt_sigprocmask => "rt_sigprocmask",
            X86SyscallNumbers.rt_sigpending => "rt_sigpending",
            X86SyscallNumbers.rt_sigtimedwait => "rt_sigtimedwait",
            X86SyscallNumbers.rt_sigqueueinfo => "rt_sigqueueinfo",
            X86SyscallNumbers.rt_sigsuspend => "rt_sigsuspend",
            X86SyscallNumbers.pread64 => "pread64",
            X86SyscallNumbers.pwrite64 => "pwrite64",
            X86SyscallNumbers.chown => "chown",
            X86SyscallNumbers.getcwd => "getcwd",
            X86SyscallNumbers.capget => "capget",
            X86SyscallNumbers.capset => "capset",
            X86SyscallNumbers.sigaltstack => "sigaltstack",
            X86SyscallNumbers.sendfile => "sendfile",
            X86SyscallNumbers.getpmsg => "getpmsg",
            X86SyscallNumbers.putpmsg => "putpmsg",
            X86SyscallNumbers.vfork => "vfork",
            X86SyscallNumbers.ugetrlimit => "ugetrlimit",
            X86SyscallNumbers.mmap2 => "mmap2",
            X86SyscallNumbers.truncate64 => "truncate64",
            X86SyscallNumbers.ftruncate64 => "ftruncate64",
            X86SyscallNumbers.stat64 => "stat64",
            X86SyscallNumbers.lstat64 => "lstat64",
            X86SyscallNumbers.fstat64 => "fstat64",
            X86SyscallNumbers.lchown32 => "lchown32",
            X86SyscallNumbers.getuid32 => "getuid32",
            X86SyscallNumbers.getgid32 => "getgid32",
            X86SyscallNumbers.geteuid32 => "geteuid32",
            X86SyscallNumbers.getegid32 => "getegid32",
            X86SyscallNumbers.setreuid32 => "setreuid32",
            X86SyscallNumbers.setregid32 => "setregid32",
            X86SyscallNumbers.getgroups32 => "getgroups32",
            X86SyscallNumbers.setgroups32 => "setgroups32",
            X86SyscallNumbers.fchown32 => "fchown32",
            X86SyscallNumbers.setresuid32 => "setresuid32",
            X86SyscallNumbers.getresuid32 => "getresuid32",
            X86SyscallNumbers.setresgid32 => "setresgid32",
            X86SyscallNumbers.getresgid32 => "getresgid32",
            X86SyscallNumbers.chown32 => "chown32",
            X86SyscallNumbers.setuid32 => "setuid32",
            X86SyscallNumbers.setgid32 => "setgid32",
            X86SyscallNumbers.setfsuid32 => "setfsuid32",
            X86SyscallNumbers.setfsgid32 => "setfsgid32",
            X86SyscallNumbers.pivot_root => "pivot_root",
            X86SyscallNumbers.mincore => "mincore",
            X86SyscallNumbers.madvise => "madvise",
            X86SyscallNumbers.getdents64 => "getdents64",
            X86SyscallNumbers.fcntl64 => "fcntl64",
            X86SyscallNumbers.gettid => "gettid",
            X86SyscallNumbers.readahead => "readahead",
            X86SyscallNumbers.setxattr => "setxattr",
            X86SyscallNumbers.lsetxattr => "lsetxattr",
            X86SyscallNumbers.fsetxattr => "fsetxattr",
            X86SyscallNumbers.getxattr => "getxattr",
            X86SyscallNumbers.lgetxattr => "lgetxattr",
            X86SyscallNumbers.fgetxattr => "fgetxattr",
            X86SyscallNumbers.listxattr => "listxattr",
            X86SyscallNumbers.llistxattr => "llistxattr",
            X86SyscallNumbers.flistxattr => "flistxattr",
            X86SyscallNumbers.removexattr => "removexattr",
            X86SyscallNumbers.lremovexattr => "lremovexattr",
            X86SyscallNumbers.fremovexattr => "fremovexattr",
            X86SyscallNumbers.tkill => "tkill",
            X86SyscallNumbers.sendfile64 => "sendfile64",
            X86SyscallNumbers.futex => "futex",
            X86SyscallNumbers.sched_setaffinity => "sched_setaffinity",
            X86SyscallNumbers.sched_getaffinity => "sched_getaffinity",
            X86SyscallNumbers.set_thread_area => "set_thread_area",
            X86SyscallNumbers.get_thread_area => "get_thread_area",
            X86SyscallNumbers.io_setup => "io_setup",
            X86SyscallNumbers.io_destroy => "io_destroy",
            X86SyscallNumbers.io_getevents => "io_getevents",
            X86SyscallNumbers.io_submit => "io_submit",
            X86SyscallNumbers.io_cancel => "io_cancel",
            X86SyscallNumbers.fadvise64 => "fadvise64",
            X86SyscallNumbers.exit_group => "exit_group",
            X86SyscallNumbers.lookup_dcookie => "lookup_dcookie",
            X86SyscallNumbers.epoll_create => "epoll_create",
            X86SyscallNumbers.epoll_ctl => "epoll_ctl",
            X86SyscallNumbers.epoll_wait => "epoll_wait",
            X86SyscallNumbers.remap_file_pages => "remap_file_pages",
            X86SyscallNumbers.set_tid_address => "set_tid_address",
            X86SyscallNumbers.timer_create => "timer_create",
            X86SyscallNumbers.timer_settime => "timer_settime",
            X86SyscallNumbers.timer_gettime => "timer_gettime",
            X86SyscallNumbers.timer_getoverrun => "timer_getoverrun",
            X86SyscallNumbers.timer_delete => "timer_delete",
            X86SyscallNumbers.clock_settime => "clock_settime",
            X86SyscallNumbers.clock_gettime => "clock_gettime",
            X86SyscallNumbers.clock_getres => "clock_getres",
            X86SyscallNumbers.clock_nanosleep => "clock_nanosleep",
            X86SyscallNumbers.statfs64 => "statfs64",
            X86SyscallNumbers.fstatfs64 => "fstatfs64",
            X86SyscallNumbers.tgkill => "tgkill",
            X86SyscallNumbers.utimes => "utimes",
            X86SyscallNumbers.fadvise64_64 => "fadvise64_64",
            X86SyscallNumbers.vserver => "vserver",
            X86SyscallNumbers.mbind => "mbind",
            X86SyscallNumbers.get_mempolicy => "get_mempolicy",
            X86SyscallNumbers.set_mempolicy => "set_mempolicy",
            X86SyscallNumbers.mq_open => "mq_open",
            X86SyscallNumbers.mq_unlink => "mq_unlink",
            X86SyscallNumbers.mq_timedsend => "mq_timedsend",
            X86SyscallNumbers.mq_timedreceive => "mq_timedreceive",
            X86SyscallNumbers.mq_notify => "mq_notify",
            X86SyscallNumbers.mq_getsetattr => "mq_getsetattr",
            X86SyscallNumbers.kexec_load => "kexec_load",
            X86SyscallNumbers.waitid => "waitid",
            X86SyscallNumbers.add_key => "add_key",
            X86SyscallNumbers.request_key => "request_key",
            X86SyscallNumbers.keyctl => "keyctl",
            X86SyscallNumbers.ioprio_set => "ioprio_set",
            X86SyscallNumbers.ioprio_get => "ioprio_get",
            X86SyscallNumbers.inotify_init => "inotify_init",
            X86SyscallNumbers.inotify_add_watch => "inotify_add_watch",
            X86SyscallNumbers.inotify_rm_watch => "inotify_rm_watch",
            X86SyscallNumbers.migrate_pages => "migrate_pages",
            X86SyscallNumbers.openat => "openat",
            X86SyscallNumbers.mkdirat => "mkdirat",
            X86SyscallNumbers.mknodat => "mknodat",
            X86SyscallNumbers.fchownat => "fchownat",
            X86SyscallNumbers.futimesat => "futimesat",
            X86SyscallNumbers.fstatat64 => "fstatat64",
            X86SyscallNumbers.unlinkat => "unlinkat",
            X86SyscallNumbers.renameat => "renameat",
            X86SyscallNumbers.linkat => "linkat",
            X86SyscallNumbers.symlinkat => "symlinkat",
            X86SyscallNumbers.readlinkat => "readlinkat",
            X86SyscallNumbers.fchmodat => "fchmodat",
            X86SyscallNumbers.faccessat => "faccessat",
            X86SyscallNumbers.pselect6 => "pselect6",
            X86SyscallNumbers.ppoll => "ppoll",
            X86SyscallNumbers.unshare => "unshare",
            X86SyscallNumbers.set_robust_list => "set_robust_list",
            X86SyscallNumbers.get_robust_list => "get_robust_list",
            X86SyscallNumbers.splice => "splice",
            X86SyscallNumbers.sync_file_range => "sync_file_range",
            X86SyscallNumbers.tee => "tee",
            X86SyscallNumbers.vmsplice => "vmsplice",
            X86SyscallNumbers.move_pages => "move_pages",
            X86SyscallNumbers.getcpu => "getcpu",
            X86SyscallNumbers.epoll_pwait => "epoll_pwait",
            X86SyscallNumbers.utimensat => "utimensat",
            X86SyscallNumbers.signalfd => "signalfd",
            X86SyscallNumbers.timerfd_create => "timerfd_create",
            X86SyscallNumbers.eventfd => "eventfd",
            X86SyscallNumbers.fallocate => "fallocate",
            X86SyscallNumbers.timerfd_settime => "timerfd_settime",
            X86SyscallNumbers.timerfd_gettime => "timerfd_gettime",
            X86SyscallNumbers.signalfd4 => "signalfd4",
            X86SyscallNumbers.eventfd2 => "eventfd2",
            X86SyscallNumbers.epoll_create1 => "epoll_create1",
            X86SyscallNumbers.dup3 => "dup3",
            X86SyscallNumbers.pipe2 => "pipe2",
            X86SyscallNumbers.inotify_init1 => "inotify_init1",
            X86SyscallNumbers.preadv => "preadv",
            X86SyscallNumbers.pwritev => "pwritev",
            X86SyscallNumbers.rt_tgsigqueueinfo => "rt_tgsigqueueinfo",
            X86SyscallNumbers.perf_event_open => "perf_event_open",
            X86SyscallNumbers.recvmmsg => "recvmmsg",
            X86SyscallNumbers.fanotify_init => "fanotify_init",
            X86SyscallNumbers.fanotify_mark => "fanotify_mark",
            X86SyscallNumbers.prlimit64 => "prlimit64",
            X86SyscallNumbers.name_to_handle_at => "name_to_handle_at",
            X86SyscallNumbers.open_by_handle_at => "open_by_handle_at",
            X86SyscallNumbers.clock_adjtime => "clock_adjtime",
            X86SyscallNumbers.syncfs => "syncfs",
            X86SyscallNumbers.sendmmsg => "sendmmsg",
            X86SyscallNumbers.setns => "setns",
            X86SyscallNumbers.process_vm_readv => "process_vm_readv",
            X86SyscallNumbers.process_vm_writev => "process_vm_writev",
            X86SyscallNumbers.kcmp => "kcmp",
            X86SyscallNumbers.finit_module => "finit_module",
            X86SyscallNumbers.sched_setattr => "sched_setattr",
            X86SyscallNumbers.sched_getattr => "sched_getattr",
            X86SyscallNumbers.renameat2 => "renameat2",
            X86SyscallNumbers.seccomp => "seccomp",
            X86SyscallNumbers.getrandom => "getrandom",
            X86SyscallNumbers.memfd_create => "memfd_create",
            X86SyscallNumbers.bpf => "bpf",
            X86SyscallNumbers.execveat => "execveat",
            X86SyscallNumbers.socket => "socket",
            X86SyscallNumbers.socketpair => "socketpair",
            X86SyscallNumbers.bind => "bind",
            X86SyscallNumbers.connect => "connect",
            X86SyscallNumbers.listen => "listen",
            X86SyscallNumbers.accept4 => "accept4",
            X86SyscallNumbers.getsockopt => "getsockopt",
            X86SyscallNumbers.setsockopt => "setsockopt",
            X86SyscallNumbers.getsockname => "getsockname",
            X86SyscallNumbers.getpeername => "getpeername",
            X86SyscallNumbers.sendto => "sendto",
            X86SyscallNumbers.sendmsg => "sendmsg",
            X86SyscallNumbers.recvfrom => "recvfrom",
            X86SyscallNumbers.recvmsg => "recvmsg",
            X86SyscallNumbers.shutdown => "shutdown",
            X86SyscallNumbers.userfaultfd => "userfaultfd",
            X86SyscallNumbers.membarrier => "membarrier",
            X86SyscallNumbers.mlock2 => "mlock2",
            X86SyscallNumbers.copy_file_range => "copy_file_range",
            X86SyscallNumbers.preadv2 => "preadv2",
            X86SyscallNumbers.pwritev2 => "pwritev2",
            X86SyscallNumbers.pkey_mprotect => "pkey_mprotect",
            X86SyscallNumbers.pkey_alloc => "pkey_alloc",
            X86SyscallNumbers.pkey_free => "pkey_free",
            X86SyscallNumbers.statx => "statx",
            X86SyscallNumbers.arch_prctl => "arch_prctl",
            X86SyscallNumbers.io_pgetevents => "io_pgetevents",
            X86SyscallNumbers.rseq => "rseq",
            X86SyscallNumbers.semget => "semget",
            X86SyscallNumbers.semctl => "semctl",
            X86SyscallNumbers.shmget => "shmget",
            X86SyscallNumbers.shmctl => "shmctl",
            X86SyscallNumbers.shmat => "shmat",
            X86SyscallNumbers.shmdt => "shmdt",
            X86SyscallNumbers.msgget => "msgget",
            X86SyscallNumbers.msgsnd => "msgsnd",
            X86SyscallNumbers.msgrcv => "msgrcv",
            X86SyscallNumbers.msgctl => "msgctl",
            X86SyscallNumbers.clock_gettime64 => "clock_gettime64",
            X86SyscallNumbers.clock_settime64 => "clock_settime64",
            X86SyscallNumbers.clock_adjtime64 => "clock_adjtime64",
            X86SyscallNumbers.clock_getres_time64 => "clock_getres_time64",
            X86SyscallNumbers.clock_nanosleep_time64 => "clock_nanosleep_time64",
            X86SyscallNumbers.timer_gettime64 => "timer_gettime64",
            X86SyscallNumbers.timer_settime64 => "timer_settime64",
            X86SyscallNumbers.timerfd_gettime64 => "timerfd_gettime64",
            X86SyscallNumbers.timerfd_settime64 => "timerfd_settime64",
            X86SyscallNumbers.utimensat_time64 => "utimensat_time64",
            X86SyscallNumbers.pselect6_time64 => "pselect6_time64",
            X86SyscallNumbers.ppoll_time64 => "ppoll_time64",
            X86SyscallNumbers.io_pgetevents_time64 => "io_pgetevents_time64",
            X86SyscallNumbers.recvmmsg_time64 => "recvmmsg_time64",
            X86SyscallNumbers.mq_timedsend_time64 => "mq_timedsend_time64",
            X86SyscallNumbers.mq_timedreceive_time64 => "mq_timedreceive_time64",
            X86SyscallNumbers.semtimedop_time64 => "semtimedop_time64",
            X86SyscallNumbers.rt_sigtimedwait_time64 => "rt_sigtimedwait_time64",
            X86SyscallNumbers.futex_time64 => "futex_time64",
            X86SyscallNumbers.sched_rr_get_interval_time64 => "sched_rr_get_interval_time64",
            X86SyscallNumbers.pidfd_send_signal => "pidfd_send_signal",
            X86SyscallNumbers.io_uring_setup => "io_uring_setup",
            X86SyscallNumbers.io_uring_enter => "io_uring_enter",
            X86SyscallNumbers.io_uring_register => "io_uring_register",
            X86SyscallNumbers.open_tree => "open_tree",
            X86SyscallNumbers.move_mount => "move_mount",
            X86SyscallNumbers.fsopen => "fsopen",
            X86SyscallNumbers.fsconfig => "fsconfig",
            X86SyscallNumbers.fsmount => "fsmount",
            X86SyscallNumbers.fspick => "fspick",
            X86SyscallNumbers.pidfd_open => "pidfd_open",
            X86SyscallNumbers.clone3 => "clone3",
            X86SyscallNumbers.close_range => "close_range",
            X86SyscallNumbers.openat2 => "openat2",
            X86SyscallNumbers.pidfd_getfd => "pidfd_getfd",
            X86SyscallNumbers.faccessat2 => "faccessat2",
            X86SyscallNumbers.process_madvise => "process_madvise",
            X86SyscallNumbers.epoll_pwait2 => "epoll_pwait2",
            X86SyscallNumbers.mount_setattr => "mount_setattr",
            X86SyscallNumbers.quotactl_fd => "quotactl_fd",
            X86SyscallNumbers.landlock_create_ruleset => "landlock_create_ruleset",
            X86SyscallNumbers.landlock_add_rule => "landlock_add_rule",
            X86SyscallNumbers.landlock_restrict_self => "landlock_restrict_self",
            X86SyscallNumbers.memfd_secret => "memfd_secret",
            X86SyscallNumbers.process_mrelease => "process_mrelease",
            X86SyscallNumbers.futex_waitv => "futex_waitv",
            X86SyscallNumbers.set_mempolicy_home_node => "set_mempolicy_home_node",
            X86SyscallNumbers.cachestat => "cachestat",
            X86SyscallNumbers.fchmodat2 => "fchmodat2",
            X86SyscallNumbers.map_shadow_stack => "map_shadow_stack",
            X86SyscallNumbers.futex_wake => "futex_wake",
            X86SyscallNumbers.futex_wait => "futex_wait",
            X86SyscallNumbers.futex_requeue => "futex_requeue",
            X86SyscallNumbers.statmount => "statmount",
            X86SyscallNumbers.listmount => "listmount",
            X86SyscallNumbers.lsm_get_self_attr => "lsm_get_self_attr",
            X86SyscallNumbers.lsm_set_self_attr => "lsm_set_self_attr",
            X86SyscallNumbers.lsm_list_modules => "lsm_list_modules",
            X86SyscallNumbers.mseal => "mseal",
            X86SyscallNumbers.setxattrat => "setxattrat",
            X86SyscallNumbers.getxattrat => "getxattrat",
            X86SyscallNumbers.listxattrat => "listxattrat",
            X86SyscallNumbers.removexattrat => "removexattrat",
            X86SyscallNumbers.open_tree_attr => "open_tree_attr",
            X86SyscallNumbers.file_getattr => "file_getattr",
            X86SyscallNumbers.file_setattr => "file_setattr",
            X86SyscallNumbers.listns => "listns",
            _ => $"syscall_{nr}"
        };
    }

    private static string ReadString(SyscallManager sys, uint addr)
    {
        if (addr == 0) return "NULL";

        try
        {
            // Read string from memory, limit to 256 chars
            var str = sys.Engine.ReadStringSafe(addr, 256);
            return JsonSerializer.Serialize(str, JsonOptions);
        }
        catch
        {
            return $"0x{addr:X}";
        }
    }

    private static string ReadStringOrBuffer(SyscallManager sys, uint addr, uint len)
    {
        if (addr == 0) return "NULL";
        if (len == 0) return "\"\"";

        try
        {
            // Limit to 64 bytes for display
            var displayLen = Math.Min(len, 64);
            var buffer = new byte[displayLen];
            if (!sys.Engine.CopyFromUser(addr, buffer)) return $"0x{addr:X}";

            // Check if it looks like a string (printable chars)
            var isPrintable = true;
            for (var i = 0; i < displayLen; i++)
            {
                var b = buffer[i];
                if (b < 32 && b != '\n' && b != '\r' && b != '\t')
                {
                    isPrintable = false;
                    break;
                }
            }

            if (isPrintable)
            {
                var str = Encoding.UTF8.GetString(buffer);
                var json = JsonSerializer.Serialize(str, JsonOptions);
                if (len > displayLen) json = json.Substring(0, json.Length - 1) + "...\"\"";
                return json;
            }

            // Hex dump
            return $"0x{addr:X}";
        }
        catch
        {
            return $"0x{addr:X}";
        }
    }
}