using System.Text;
using System.Text.Json;
using Fiberish.Native;
using Fiberish.X86.Native;
using Microsoft.Extensions.Logging;

namespace Fiberish.Syscalls;

public static class SyscallTracer
{
    private static readonly JsonSerializerOptions JsonOptions = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static void TraceEntry(ILogger logger, SyscallManager sys, int tid, uint nr, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
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

    public static void TraceExit(ILogger logger, SyscallManager sys, int tid, uint nr, int ret, uint a1, uint a2, uint a3)
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
            if (nr == X86SyscallNumbers.read && ret > 0)
            {
                sb.Append($" {ReadStringOrBuffer(sys, a2, (uint)ret)}");
            }
        }
        
        logger.LogTrace(sb.ToString());
    }

    private static string GetSyscallName(uint nr)
    {
        // Simple mapping for now, can be expanded or use reflection on X86SyscallNumbers
        return nr switch
        {
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
            X86SyscallNumbers.lseek => "lseek",
            X86SyscallNumbers.getpid => "getpid",
            X86SyscallNumbers.mount => "mount",
            X86SyscallNumbers.setuid => "setuid",
            X86SyscallNumbers.getuid => "getuid",
            X86SyscallNumbers.ptrace => "ptrace",
            X86SyscallNumbers.alarm => "alarm",
            X86SyscallNumbers.pause => "pause",
            X86SyscallNumbers.access => "access",
            X86SyscallNumbers.kill => "kill",
            X86SyscallNumbers.rename => "rename",
            X86SyscallNumbers.mkdir => "mkdir",
            X86SyscallNumbers.rmdir => "rmdir",
            X86SyscallNumbers.dup => "dup",
            X86SyscallNumbers.pipe => "pipe",
            X86SyscallNumbers.brk => "brk",
            X86SyscallNumbers.ioctl => "ioctl",
            X86SyscallNumbers.fcntl => "fcntl",
            X86SyscallNumbers.setpgid => "setpgid",
            X86SyscallNumbers.umask => "umask",
            X86SyscallNumbers.chroot => "chroot",
            X86SyscallNumbers.dup2 => "dup2",
            X86SyscallNumbers.getppid => "getppid",
            X86SyscallNumbers.setsid => "setsid",
            X86SyscallNumbers.sigaction => "sigaction",
            X86SyscallNumbers.sigsuspend => "sigsuspend",
            X86SyscallNumbers.sigpending => "sigpending",
            X86SyscallNumbers.sethostname => "sethostname",
            X86SyscallNumbers.setrlimit => "setrlimit",
            X86SyscallNumbers.getrlimit => "getrlimit",
            X86SyscallNumbers.gettimeofday => "gettimeofday",
            X86SyscallNumbers.select => "select",
            X86SyscallNumbers.symlink => "symlink",
            X86SyscallNumbers.readlink => "readlink",
            X86SyscallNumbers.readdir => "readdir",
            X86SyscallNumbers.mmap => "mmap",
            X86SyscallNumbers.munmap => "munmap",
            X86SyscallNumbers.truncate => "truncate",
            X86SyscallNumbers.ftruncate => "ftruncate",
            X86SyscallNumbers.fchmod => "fchmod",
            X86SyscallNumbers.fchown => "fchown",
            X86SyscallNumbers.statfs => "statfs",
            X86SyscallNumbers.fstatfs => "fstatfs",
            X86SyscallNumbers.socketcall => "socketcall",
            X86SyscallNumbers.syslog => "syslog",
            X86SyscallNumbers.stat => "stat",
            X86SyscallNumbers.lstat => "lstat",
            X86SyscallNumbers.fstat => "fstat",
            X86SyscallNumbers.uname => "uname",
            X86SyscallNumbers.mprotect => "mprotect",
            X86SyscallNumbers.sigprocmask => "sigprocmask",
            X86SyscallNumbers.getdents => "getdents",
            X86SyscallNumbers._newselect => "_newselect",
            X86SyscallNumbers.flock => "flock",
            X86SyscallNumbers.msync => "msync",
            X86SyscallNumbers.readv => "readv",
            X86SyscallNumbers.writev => "writev",
            X86SyscallNumbers.getsid => "getsid",
            X86SyscallNumbers.fdatasync => "fdatasync",
            X86SyscallNumbers.mlock => "mlock",
            X86SyscallNumbers.munlock => "munlock",
            X86SyscallNumbers.mlockall => "mlockall",
            X86SyscallNumbers.munlockall => "munlockall",
            X86SyscallNumbers.sched_yield => "sched_yield",
            X86SyscallNumbers.nanosleep => "nanosleep",
            X86SyscallNumbers.mremap => "mremap",
            X86SyscallNumbers.poll => "poll",
            X86SyscallNumbers.prctl => "prctl",
            X86SyscallNumbers.rt_sigreturn => "rt_sigreturn",
            X86SyscallNumbers.rt_sigaction => "rt_sigaction",
            X86SyscallNumbers.rt_sigprocmask => "rt_sigprocmask",
            X86SyscallNumbers.rt_sigpending => "rt_sigpending",
            X86SyscallNumbers.rt_sigtimedwait => "rt_sigtimedwait",
            X86SyscallNumbers.rt_sigqueueinfo => "rt_sigqueueinfo",
            X86SyscallNumbers.rt_sigsuspend => "rt_sigsuspend",
            X86SyscallNumbers.pread => "pread",
            X86SyscallNumbers.pwrite => "pwrite",
            X86SyscallNumbers.chown => "chown",
            X86SyscallNumbers.getcwd => "getcwd",
            X86SyscallNumbers.sigaltstack => "sigaltstack",
            X86SyscallNumbers.sendfile => "sendfile",
            X86SyscallNumbers.vfork => "vfork",
            X86SyscallNumbers.mmap2 => "mmap2",
            X86SyscallNumbers.truncate64 => "truncate64",
            X86SyscallNumbers.ftruncate64 => "ftruncate64",
            X86SyscallNumbers.stat64 => "stat64",
            X86SyscallNumbers.lstat64 => "lstat64",
            X86SyscallNumbers.fstat64 => "fstat64",
            X86SyscallNumbers.getdents64 => "getdents64",
            X86SyscallNumbers.fcntl64 => "fcntl64",
            X86SyscallNumbers.gettid => "gettid",
            X86SyscallNumbers.readahead => "readahead",
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
            X86SyscallNumbers.epoll_create => "epoll_create",
            X86SyscallNumbers.epoll_ctl => "epoll_ctl",
            X86SyscallNumbers.epoll_wait => "epoll_wait",
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
            X86SyscallNumbers.getrandom => "getrandom",
            X86SyscallNumbers.memfd_create => "memfd_create",
            X86SyscallNumbers.bpf => "bpf",
            X86SyscallNumbers.execveat => "execveat",
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
            X86SyscallNumbers.ipc => "ipc",
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
            bool isPrintable = true;
            for (int i = 0; i < displayLen; i++)
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
            else
            {
                // Hex dump
                return $"0x{addr:X}";
            }
        }
        catch
        {
            return $"0x{addr:X}";
        }
    }
}
