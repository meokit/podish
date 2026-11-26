using System.Buffers.Binary;
using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;

namespace Fiberish.Syscalls;

public partial class SyscallManager
{
    // Linux arm64 uses the generic 64-bit syscall table. Dispatch to the same
    // operations as i386 after adapting register widths and argument layout.
    // The size mask marks arguments that must fit in the shared handlers,
    // with register X0 at the low bit.
    private static (uint Operation, uint Sizes) DecodeAArch64Syscall(uint number) => number switch
    {
        5 => (X86SyscallNumbers.setxattr, 0b001000),
        6 => (X86SyscallNumbers.lsetxattr, 0b001000),
        7 => (X86SyscallNumbers.fsetxattr, 0b001000),
        8 => (X86SyscallNumbers.getxattr, 0b001000),
        9 => (X86SyscallNumbers.lgetxattr, 0b001000),
        10 => (X86SyscallNumbers.fgetxattr, 0b001000),
        11 => (X86SyscallNumbers.listxattr, 0b000100),
        12 => (X86SyscallNumbers.llistxattr, 0b000100),
        13 => (X86SyscallNumbers.flistxattr, 0b000100),
        14 => (X86SyscallNumbers.removexattr, 0),
        15 => (X86SyscallNumbers.lremovexattr, 0),
        16 => (X86SyscallNumbers.fremovexattr, 0),
        17 => (X86SyscallNumbers.getcwd, 0),
        19 => (X86SyscallNumbers.eventfd2, 0),
        20 => (X86SyscallNumbers.epoll_create1, 0),
        21 => (X86SyscallNumbers.epoll_ctl, 0),
        22 => (X86SyscallNumbers.epoll_pwait, 0b100000),
        23 => (X86SyscallNumbers.dup, 0),
        24 => (X86SyscallNumbers.dup3, 0),
        25 => (X86SyscallNumbers.fcntl, 0),
        26 => (X86SyscallNumbers.inotify_init1, 0),
        27 => (X86SyscallNumbers.inotify_add_watch, 0),
        28 => (X86SyscallNumbers.inotify_rm_watch, 0),
        29 => (X86SyscallNumbers.ioctl, 0),
        32 => (X86SyscallNumbers.flock, 0),
        33 => (X86SyscallNumbers.mknodat, 0),
        34 => (X86SyscallNumbers.mkdirat, 0),
        35 => (X86SyscallNumbers.unlinkat, 0),
        36 => (X86SyscallNumbers.symlinkat, 0),
        37 => (X86SyscallNumbers.linkat, 0),
        38 => (X86SyscallNumbers.renameat, 0),
        39 => (X86SyscallNumbers.umount2, 0),
        40 => (X86SyscallNumbers.mount, 0),
        43 => (X86SyscallNumbers.statfs, 0),
        44 => (X86SyscallNumbers.fstatfs, 0),
        45 => (X86SyscallNumbers.truncate64, 0),
        46 => (X86SyscallNumbers.ftruncate64, 0),
        47 => (X86SyscallNumbers.fallocate, 0),
        48 => (X86SyscallNumbers.faccessat, 0),
        49 => (X86SyscallNumbers.chdir, 0),
        50 => (X86SyscallNumbers.fchdir, 0),
        51 => (X86SyscallNumbers.chroot, 0),
        52 => (X86SyscallNumbers.fchmod, 0),
        53 => (X86SyscallNumbers.fchmodat, 0),
        54 => (X86SyscallNumbers.fchownat, 0),
        55 => (X86SyscallNumbers.fchown32, 0),
        56 => (X86SyscallNumbers.openat, 0),
        57 => (X86SyscallNumbers.close, 0),
        59 => (X86SyscallNumbers.pipe2, 0),
        61 => (X86SyscallNumbers.getdents64, 0),
        62 => (X86SyscallNumbers.lseek, 0),
        63 => (X86SyscallNumbers.read, 0b000100),
        64 => (X86SyscallNumbers.write, 0b000100),
        65 => (X86SyscallNumbers.readv, 0b000100),
        66 => (X86SyscallNumbers.writev, 0b000100),
        67 => (X86SyscallNumbers.pread, 0b000100),
        68 => (X86SyscallNumbers.pwrite, 0b000100),
        69 => (X86SyscallNumbers.preadv, 0b000100),
        70 => (X86SyscallNumbers.pwritev, 0b000100),
        71 => (X86SyscallNumbers.sendfile64, 0b001000),
        72 => (X86SyscallNumbers.pselect6_time64, 0),
        73 => (X86SyscallNumbers.ppoll_time64, 0b010000),
        74 => (X86SyscallNumbers.signalfd4, 0b000100),
        75 => (X86SyscallNumbers.vmsplice, 0b000100),
        76 => (X86SyscallNumbers.splice, 0b010000),
        77 => (X86SyscallNumbers.tee, 0b000100),
        78 => (X86SyscallNumbers.readlinkat, 0),
        79 => (X86SyscallNumbers.fstatat64, 0),
        80 => (X86SyscallNumbers.fstat64, 0),
        81 => (X86SyscallNumbers.sync, 0),
        82 => (X86SyscallNumbers.fsync, 0),
        83 => (X86SyscallNumbers.fdatasync, 0),
        85 => (X86SyscallNumbers.timerfd_create, 0),
        86 => (X86SyscallNumbers.timerfd_settime64, 0),
        87 => (X86SyscallNumbers.timerfd_gettime64, 0),
        88 => (X86SyscallNumbers.utimensat_time64, 0),
        90 => (X86SyscallNumbers.capget, 0),
        91 => (X86SyscallNumbers.capset, 0),
        92 => (X86SyscallNumbers.personality, 0),
        93 => (X86SyscallNumbers.exit, 0),
        94 => (X86SyscallNumbers.exit_group, 0),
        95 => (X86SyscallNumbers.waitid, 0),
        96 => (X86SyscallNumbers.set_tid_address, 0),
        98 => (X86SyscallNumbers.futex_time64, 0),
        99 => (X86SyscallNumbers.set_robust_list, 0),
        100 => (X86SyscallNumbers.get_robust_list, 0),
        101 => (X86SyscallNumbers.nanosleep, 0),
        102 => (X86SyscallNumbers.getitimer, 0),
        103 => (X86SyscallNumbers.setitimer, 0),
        107 => (X86SyscallNumbers.timer_create, 0),
        108 => (X86SyscallNumbers.timer_gettime, 0),
        109 => (X86SyscallNumbers.timer_getoverrun, 0),
        110 => (X86SyscallNumbers.timer_settime64, 0),
        111 => (X86SyscallNumbers.timer_delete, 0),
        112 => (X86SyscallNumbers.clock_settime64, 0),
        113 => (X86SyscallNumbers.clock_gettime64, 0),
        114 => (X86SyscallNumbers.clock_getres_time64, 0),
        115 => (X86SyscallNumbers.clock_nanosleep_time64, 0),
        120 => (X86SyscallNumbers.sched_getscheduler, 0),
        121 => (X86SyscallNumbers.sched_getparam, 0),
        123 => (X86SyscallNumbers.sched_getaffinity, 0),
        124 => (X86SyscallNumbers.sched_yield, 0),
        129 => (X86SyscallNumbers.kill, 0),
        130 => (X86SyscallNumbers.tkill, 0),
        131 => (X86SyscallNumbers.tgkill, 0),
        132 => (X86SyscallNumbers.sigaltstack, 0),
        133 => (X86SyscallNumbers.rt_sigsuspend, 0b000010),
        134 => (X86SyscallNumbers.rt_sigaction, 0),
        135 => (X86SyscallNumbers.rt_sigprocmask, 0b001000),
        136 => (X86SyscallNumbers.rt_sigpending, 0b000010),
        137 => (X86SyscallNumbers.rt_sigtimedwait_time64, 0b001000),
        138 => (X86SyscallNumbers.rt_sigqueueinfo, 0),
        139 => (X86SyscallNumbers.rt_sigreturn, 0),
        140 => (X86SyscallNumbers.setpriority, 0),
        141 => (X86SyscallNumbers.getpriority, 0),
        143 => (X86SyscallNumbers.setregid32, 0),
        144 => (X86SyscallNumbers.setgid32, 0),
        145 => (X86SyscallNumbers.setreuid32, 0),
        146 => (X86SyscallNumbers.setuid32, 0),
        147 => (X86SyscallNumbers.setresuid32, 0),
        148 => (X86SyscallNumbers.getresuid32, 0),
        149 => (X86SyscallNumbers.setresgid32, 0),
        150 => (X86SyscallNumbers.getresgid32, 0),
        151 => (X86SyscallNumbers.setfsuid32, 0),
        152 => (X86SyscallNumbers.setfsgid32, 0),
        153 => (X86SyscallNumbers.times, 0),
        154 => (X86SyscallNumbers.setpgid, 0),
        155 => (X86SyscallNumbers.getpgid, 0),
        156 => (X86SyscallNumbers.getsid, 0),
        157 => (X86SyscallNumbers.setsid, 0),
        158 => (X86SyscallNumbers.getgroups32, 0),
        159 => (X86SyscallNumbers.setgroups32, 0),
        160 => (X86SyscallNumbers.uname, 0),
        161 => (X86SyscallNumbers.sethostname, 0),
        162 => (X86SyscallNumbers.setdomainname, 0),
        163 => (X86SyscallNumbers.ugetrlimit, 0),
        164 => (X86SyscallNumbers.setrlimit, 0),
        165 => (X86SyscallNumbers.getrusage, 0),
        166 => (X86SyscallNumbers.umask, 0),
        167 => (X86SyscallNumbers.prctl, 0),
        168 => (X86SyscallNumbers.getcpu, 0),
        169 => (X86SyscallNumbers.gettimeofday, 0),
        172 => (X86SyscallNumbers.getpid, 0),
        173 => (X86SyscallNumbers.getppid, 0),
        174 => (X86SyscallNumbers.getuid32, 0),
        175 => (X86SyscallNumbers.geteuid32, 0),
        176 => (X86SyscallNumbers.getgid32, 0),
        177 => (X86SyscallNumbers.getegid32, 0),
        178 => (X86SyscallNumbers.gettid, 0),
        179 => (X86SyscallNumbers.sysinfo, 0),
        190 => (X86SyscallNumbers.semget, 0),
        191 => (X86SyscallNumbers.semctl, 0),
        192 => (X86SyscallNumbers.semtimedop_time64, 0),
        193 => (X86SyscallNumbers.semtimedop_time64, 0),
        194 => (X86SyscallNumbers.shmget, 0),
        195 => (X86SyscallNumbers.shmctl, 0),
        196 => (X86SyscallNumbers.shmat, 0),
        197 => (X86SyscallNumbers.shmdt, 0),
        198 => (X86SyscallNumbers.socket, 0),
        199 => (X86SyscallNumbers.socketpair, 0),
        200 => (X86SyscallNumbers.bind, 0),
        201 => (X86SyscallNumbers.listen, 0),
        202 => (X86SyscallNumbers.accept4, 0),
        203 => (X86SyscallNumbers.connect, 0),
        204 => (X86SyscallNumbers.getsockname, 0),
        205 => (X86SyscallNumbers.getpeername, 0),
        206 => (X86SyscallNumbers.sendto, 0b000100),
        207 => (X86SyscallNumbers.recvfrom, 0b000100),
        208 => (X86SyscallNumbers.setsockopt, 0),
        209 => (X86SyscallNumbers.getsockopt, 0),
        210 => (X86SyscallNumbers.shutdown, 0),
        211 => (X86SyscallNumbers.sendmsg, 0),
        212 => (X86SyscallNumbers.recvmsg, 0),
        213 => (X86SyscallNumbers.readahead, 0b000100),
        214 => (X86SyscallNumbers.brk, 0),
        215 => (X86SyscallNumbers.munmap, 0),
        216 => (X86SyscallNumbers.mremap, 0),
        220 => (X86SyscallNumbers.clone, 0),
        221 => (X86SyscallNumbers.execve, 0),
        222 => (X86SyscallNumbers.mmap2, 0),
        223 => (X86SyscallNumbers.fadvise64, 0),
        226 => (X86SyscallNumbers.mprotect, 0),
        227 => (X86SyscallNumbers.msync, 0),
        228 => (X86SyscallNumbers.mlock, 0),
        229 => (X86SyscallNumbers.munlock, 0),
        230 => (X86SyscallNumbers.mlockall, 0),
        231 => (X86SyscallNumbers.munlockall, 0),
        232 => (X86SyscallNumbers.mincore, 0),
        233 => (X86SyscallNumbers.madvise, 0),
        234 => (X86SyscallNumbers.remap_file_pages, 0),
        240 => (X86SyscallNumbers.rt_tgsigqueueinfo, 0),
        242 => (X86SyscallNumbers.accept4, 0),
        243 => (X86SyscallNumbers.recvmmsg, 0),
        260 => (X86SyscallNumbers.wait4, 0),
        261 => (X86SyscallNumbers.prlimit64, 0),
        266 => (X86SyscallNumbers.clock_adjtime64, 0),
        269 => (X86SyscallNumbers.sendmmsg, 0),
        276 => (X86SyscallNumbers.renameat2, 0),
        278 => (X86SyscallNumbers.getrandom, 0b000010),
        279 => (X86SyscallNumbers.memfd_create, 0),
        283 => (X86SyscallNumbers.membarrier, 0),
        284 => (X86SyscallNumbers.mlock2, 0),
        285 => (X86SyscallNumbers.copy_file_range, 0b010000),
        286 => (X86SyscallNumbers.preadv2, 0b000100),
        287 => (X86SyscallNumbers.pwritev2, 0b000100),
        291 => (X86SyscallNumbers.statx, 0),
        428 => (X86SyscallNumbers.open_tree, 0),
        429 => (X86SyscallNumbers.move_mount, 0),
        430 => (X86SyscallNumbers.fsopen, 0),
        431 => (X86SyscallNumbers.fsconfig, 0),
        432 => (X86SyscallNumbers.fsmount, 0),
        436 => (X86SyscallNumbers.close_range, 0),
        437 => (X86SyscallNumbers.openat2, 0b001000),
        439 => (X86SyscallNumbers.faccessat2, 0),
        441 => (X86SyscallNumbers.epoll_pwait2, 0b100000),
        442 => (X86SyscallNumbers.mount_setattr, 0b010000),
        452 => (X86SyscallNumbers.fchmodat2, 0),
        _ => (uint.MaxValue, 0)
    };

    private ValueTask<int> DispatchAArch64Syscall(uint number, uint operation, Engine engine,
        ReadOnlySpan<ulong> args, uint sizes, out bool handled, out long? wideResult,
        out ValueTask<long>? wideTask)
    {
        wideResult = null;
        wideTask = null;
        if (operation == uint.MaxValue)
        {
            handled = false;
            return ValueTask.FromResult(-(int)Errno.ENOSYS);
        }

        handled = true;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] <= uint.MaxValue) continue;
            var bit = 1u << i;
            if ((sizes & bit) != 0)
                return ValueTask.FromResult(-(int)Errno.EINVAL);
        }

        var a1 = (uint)args[0];
        var a2 = (uint)args[1];
        var a3 = (uint)args[2];
        var a4 = (uint)args[3];
        var a5 = (uint)args[4];
        var a6 = (uint)args[5];
        switch (operation)
        {
            case X86SyscallNumbers.brk:
                wideResult = Brk(engine, args[0]);
                return default;
            case X86SyscallNumbers.mmap2:
                if ((args[5] & LinuxConstants.PageOffsetMask) != 0)
                    return ValueTask.FromResult(-(int)Errno.EINVAL);
                wideTask = Mmap(engine, args[0], args[1], (int)args[2], (int)args[3], (int)args[4],
                    unchecked((long)args[5]));
                return default;
            case X86SyscallNumbers.munmap:
                return SysMunmap(engine, args[0], args[1], 0, 0, 0, 0);
            case X86SyscallNumbers.mremap:
                wideResult = Mremap(engine, args[0], args[1], args[2], (int)args[3], args[4]);
                return default;
            case X86SyscallNumbers.madvise:
                return SysMadvise(engine, args[0], args[1], a3, 0, 0, 0);
            case X86SyscallNumbers.msync:
                return SysMsync(engine, args[0], args[1], a3, 0, 0, 0);
            case X86SyscallNumbers.mincore:
                return SysMincore(engine, args[0], args[1], args[2], 0, 0, 0);
            case X86SyscallNumbers.mlock:
                return SysMlock(engine, args[0], args[1], 0, 0, 0, 0);
            case X86SyscallNumbers.mlock2:
                return SysMlock2(engine, args[0], args[1], a3, 0, 0, 0);
            case X86SyscallNumbers.munlock:
                return SysMunlock(engine, args[0], args[1], 0, 0, 0, 0);
            case X86SyscallNumbers.mprotect:
                return SysMprotect(engine, args[0], args[1], a3, 0, 0, 0);
            case X86SyscallNumbers.rt_sigaction:
                if (args[3] != 8) return ValueTask.FromResult(-(int)Errno.EINVAL);
                return SysRtSigAction(engine, a1, args[1], args[2], 8, 0, 0);
            case X86SyscallNumbers.sigaltstack:
                return SysSigAltStack(engine, args[0], args[1], 0, 0, 0, 0);
            case X86SyscallNumbers.execve:
                return SysExecve(engine, args[0], args[1], args[2], 0, 0, 0);
            case X86SyscallNumbers.clone:
                return CloneTask(engine, a1, args[1], args[2], args[3], args[4]);
            case X86SyscallNumbers.set_tid_address:
                return SysSetTidAddress(engine, args[0], 0, 0, 0, 0, 0);
            case X86SyscallNumbers.set_robust_list:
                return SysSetRobustList(engine, args[0], args[1], 0, 0, 0, 0);
            case X86SyscallNumbers.get_robust_list:
                return SysGetRobustList(engine, a1, args[1], args[2], 0, 0, 0);
            case X86SyscallNumbers.futex_time64:
                return SysFutexTime64(engine, args[0], a2, a3, args[3], args[4], a6);
            case X86SyscallNumbers.getcwd:
                return SysGetCwd(engine, args[0], args[1], 0, 0, 0, 0);
            case X86SyscallNumbers.mknodat:
                return SysMknodat(engine, a1, args[1], a3, a4, 0, 0);
            case X86SyscallNumbers.mkdirat:
                return SysMkdirAt(engine, a1, args[1], a3, 0, 0, 0);
            case X86SyscallNumbers.unlinkat:
                return SysUnlinkAt(engine, a1, args[1], a3, 0, 0, 0);
            case X86SyscallNumbers.symlinkat:
                return SysSymlinkAt(engine, args[0], a2, args[2], 0, 0, 0);
            case X86SyscallNumbers.linkat:
                return SysLinkat(engine, a1, args[1], a3, args[3], a5, 0);
            case X86SyscallNumbers.renameat:
                return SysRenameAt(engine, a1, args[1], a3, args[3], 0, 0);
            case X86SyscallNumbers.renameat2:
                return SysRenameAt2(engine, a1, args[1], a3, args[3], a5, 0);
            case X86SyscallNumbers.statfs:
                return SysStatfs(engine, args[0], args[1], 0, 0, 0, 0);
            case X86SyscallNumbers.fstatfs:
                return SysFstatfs(engine, a1, args[1], 0, 0, 0, 0);
            case X86SyscallNumbers.faccessat:
                return SysFaccessAt(engine, a1, args[1], a3, 0, 0, 0);
            case X86SyscallNumbers.faccessat2:
                return SysFaccessAt2(engine, a1, args[1], a3, a4, 0, 0);
            case X86SyscallNumbers.chdir:
                return SysChdir(engine, args[0], 0, 0, 0, 0, 0);
            case X86SyscallNumbers.chroot:
                return SysChroot(engine, args[0], 0, 0, 0, 0, 0);
            case X86SyscallNumbers.fchmodat:
                return SysFchmodAt(engine, a1, args[1], a3, 0, 0, 0);
            case X86SyscallNumbers.fchmodat2:
                return SysFchmodAt2(engine, a1, args[1], a3, a4, 0, 0);
            case X86SyscallNumbers.fchownat:
                return SysFchownAt(engine, a1, args[1], a3, a4, a5, 0);
            case X86SyscallNumbers.getdents64:
                return SysGetdents64(engine, a1, args[1], a3, 0, 0, 0);
            case X86SyscallNumbers.readlinkat:
                return SysReadlinkAt(engine, a1, args[1], args[2], a4, 0, 0);
            case X86SyscallNumbers.fstatat64:
                return SysNewFstatAt(engine, a1, args[1], args[2], a4, 0, 0);
            case X86SyscallNumbers.fstat64:
                return SysFstat64(engine, a1, args[1], 0, 0, 0, 0);
            case X86SyscallNumbers.statx:
                return SysStatx(engine, a1, args[1], a3, a4, args[4], 0);
            case X86SyscallNumbers.utimensat_time64:
                return SysUtimensAtTime64(engine, a1, args[1], args[2], a4, 0, 0);
            case X86SyscallNumbers.truncate64:
                return SysTruncate64(engine, args[0], a2, (uint)(args[1] >> 32), 0, 0, 0);
            case X86SyscallNumbers.openat:
                return SysOpenAt(engine, a1, args[1], a3, a4, 0, 0);
            case X86SyscallNumbers.openat2:
                return SysOpenAt2(engine, a1, args[1], args[2], a4, 0, 0);
            case X86SyscallNumbers.clock_gettime64:
                return SysClockGetTime64(engine, a1, args[1], 0, 0, 0, 0);
            case X86SyscallNumbers.gettimeofday:
                return SysGetTimeOfDay(engine, args[0], args[1], 0, 0, 0, 0);
            case X86SyscallNumbers.sysinfo:
                return SysSysinfo(engine, args[0], 0, 0, 0, 0, 0);
            case X86SyscallNumbers.uname:
                return SysUname(engine, args[0], 0, 0, 0, 0, 0);
            case X86SyscallNumbers.getrandom:
                return SysGetRandom(engine, args[0], a2, a3, 0, 0, 0);
            case X86SyscallNumbers.semctl:
                if ((args[2] & LinuxConstants.IPC_64) != 0)
                    return ValueTask.FromResult(-(int)Errno.EINVAL);
                return SysSemCtl(engine, a1, a2, a3, args[3], 0, 0);
            case X86SyscallNumbers.semtimedop_time64:
                return DoSemTimedOp(engine, (int)args[0], args[1], a3, number == 192 ? args[3] : 0,
                    time64: true);
            case X86SyscallNumbers.shmget:
                return SysShmGet(engine, a1, args[1], a3);
            case X86SyscallNumbers.shmctl:
                return SysShmCtl(engine, a1, a2, args[2]);
            case X86SyscallNumbers.shmat:
                wideResult = AttachSharedMemory(engine, a1, args[1], a3);
                return default;
            case X86SyscallNumbers.shmdt:
                return SysShmDt(engine, args[0]);
            case X86SyscallNumbers.read:
            case X86SyscallNumbers.write:
            case X86SyscallNumbers.pread:
            case X86SyscallNumbers.pwrite:
            case X86SyscallNumbers.readv:
            case X86SyscallNumbers.writev:
            case X86SyscallNumbers.preadv:
            case X86SyscallNumbers.pwritev:
            case X86SyscallNumbers.preadv2:
            case X86SyscallNumbers.pwritev2:
                var fd = a1;
                var count = a3;
                var offsetLow = a4;
                var offsetHigh = (uint)(args[3] >> 32);
                var flags = a6;
                return operation switch
                {
                    X86SyscallNumbers.read => SysRead(engine, fd, args[1], count, 0, 0, 0),
                    X86SyscallNumbers.write => SysWrite(engine, fd, args[1], count, 0, 0, 0),
                    X86SyscallNumbers.pread => SysPRead(engine, fd, args[1], count, offsetLow, offsetHigh, flags),
                    X86SyscallNumbers.pwrite => SysPWrite(engine, fd, args[1], count, offsetLow, offsetHigh, flags),
                    X86SyscallNumbers.readv => SysReadV(engine, fd, args[1], count, 0, 0, 0),
                    X86SyscallNumbers.writev => SysWriteV(engine, fd, args[1], count, 0, 0, 0),
                    X86SyscallNumbers.preadv => SysPReadV(engine, fd, args[1], count, offsetLow, offsetHigh, flags),
                    X86SyscallNumbers.pwritev => SysPWriteV(engine, fd, args[1], count, offsetLow, offsetHigh, flags),
                    X86SyscallNumbers.preadv2 => SysPReadV2(engine, fd, args[1], count, offsetLow, offsetHigh, flags),
                    X86SyscallNumbers.pwritev2 => SysPWriteV2(engine, fd, args[1], count, offsetLow, offsetHigh, flags),
                    _ => throw new InvalidOperationException()
                };
            case X86SyscallNumbers.lseek:
                wideResult = SeekFile((int)a1, (long)args[1], (int)a3);
                return default;
            case X86SyscallNumbers.setxattr:
                return SysSetXAttr(engine, args[0], args[1], args[2], a4, a5, a6);
            case X86SyscallNumbers.lsetxattr:
                return SysLSetXAttr(engine, args[0], args[1], args[2], a4, a5, a6);
            case X86SyscallNumbers.fsetxattr:
                return SysFSetXAttr(engine, a1, args[1], args[2], a4, a5, a6);
            case X86SyscallNumbers.getxattr:
                return SysGetXAttr(engine, args[0], args[1], args[2], a4, a5, a6);
            case X86SyscallNumbers.lgetxattr:
                return SysLGetXAttr(engine, args[0], args[1], args[2], a4, a5, a6);
            case X86SyscallNumbers.fgetxattr:
                return SysFGetXAttr(engine, a1, args[1], args[2], a4, a5, a6);
            case X86SyscallNumbers.listxattr:
                return SysListXAttr(engine, args[0], args[1], a3, a4, a5, a6);
            case X86SyscallNumbers.llistxattr:
                return SysLListXAttr(engine, args[0], args[1], a3, a4, a5, a6);
            case X86SyscallNumbers.flistxattr:
                return SysFListXAttr(engine, a1, args[1], a3, a4, a5, a6);
            case X86SyscallNumbers.removexattr:
                return SysRemoveXAttr(engine, args[0], args[1], a3, a4, a5, a6);
            case X86SyscallNumbers.lremovexattr:
                return SysLRemoveXAttr(engine, args[0], args[1], a3, a4, a5, a6);
            case X86SyscallNumbers.fremovexattr:
                return SysFRemoveXAttr(engine, a1, args[1], a3, a4, a5, a6);
            case X86SyscallNumbers.epoll_pwait:
                return SysEpollPwait(engine, a1, args[1], a3, a4, args[4], a6);
            case X86SyscallNumbers.inotify_add_watch:
                return SysInotifyAddWatch(engine, a1, args[1], a3, a4, a5, a6);
            case X86SyscallNumbers.umount2:
                return SysUmount2(engine, args[0], a2, a3, a4, a5, a6);
            case X86SyscallNumbers.mount:
                return SysMount(engine, args[0], args[1], args[2], a4, args[4], a6);
            case X86SyscallNumbers.pipe2:
                return SysPipe2(engine, args[0], a2, a3, a4, a5, a6);
            case X86SyscallNumbers.sendfile64:
                return SysSendfile64(engine, a1, a2, args[2], a4, a5, a6);
            case X86SyscallNumbers.pselect6_time64:
                return SysPselect6Time64(engine, a1, args[1], args[2], args[3], args[4], args[5]);
            case X86SyscallNumbers.ppoll_time64:
                return SysPpollTime64(engine, args[0], a2, args[2], args[3], a5, a6);
            case X86SyscallNumbers.signalfd4:
                return SysSignalFd4(engine, a1, args[1], a3, a4, a5, a6);
            case X86SyscallNumbers.vmsplice:
                return SysVmsplice(engine, a1, args[1], a3, a4, a5, a6);
            case X86SyscallNumbers.splice:
                return SysSplice(engine, a1, args[1], a3, args[3], a5, a6);
            case X86SyscallNumbers.timerfd_settime64:
                return SysTimerFdSetTime64(engine, a1, a2, args[2], args[3], a5, a6);
            case X86SyscallNumbers.timerfd_gettime64:
                return SysTimerFdGetTime64(engine, a1, args[1], a3, a4, a5, a6);
            case X86SyscallNumbers.capget:
                return SysCapget(engine, args[0], args[1], a3, a4, a5, a6);
            case X86SyscallNumbers.capset:
                return SysCapset(engine, args[0], args[1], a3, a4, a5, a6);
            case X86SyscallNumbers.waitid:
                return SysWaitId(engine, a1, a2, args[2], a4, args[4], a6);
            case X86SyscallNumbers.nanosleep:
                return SysNanosleep(engine, args[0], args[1], a3, a4, a5, a6);
            case X86SyscallNumbers.getitimer:
                return SysGetitimer(engine, a1, args[1], a3, a4, a5, a6);
            case X86SyscallNumbers.setitimer:
                return SysSetitimer(engine, a1, args[1], args[2], a4, a5, a6);
            case X86SyscallNumbers.timer_create:
                return SysTimerCreate(engine, a1, args[1], args[2], a4, a5, a6);
            case X86SyscallNumbers.timer_gettime:
                return SysTimerGetTime64(engine, a1, args[1], a3, a4, a5, a6);
            case X86SyscallNumbers.timer_settime64:
                return SysTimerSetTime64(engine, a1, a2, args[2], args[3], a5, a6);
            case X86SyscallNumbers.clock_settime64:
                return SysClockSetTime64(engine, a1, args[1], a3, a4, a5, a6);
            case X86SyscallNumbers.clock_getres_time64:
                return SysClockGetResTime64(engine, a1, args[1], a3, a4, a5, a6);
            case X86SyscallNumbers.clock_nanosleep_time64:
                return SysClockNanosleepTime64(engine, a1, a2, args[2], args[3], a5, a6);
            case X86SyscallNumbers.sched_getparam:
                return SysSchedGetParam(engine, a1, args[1], a3, a4, a5, a6);
            case X86SyscallNumbers.sched_getaffinity:
                return SysSchedGetAffinity(engine, a1, a2, args[2], a4, a5, a6);
            case X86SyscallNumbers.rt_sigsuspend:
                return SysRtSigSuspend(engine, args[0], a2, a3, a4, a5, a6);
            case X86SyscallNumbers.rt_sigprocmask:
                return SysRtSigProcMask(engine, a1, args[1], args[2], a4, a5, a6);
            case X86SyscallNumbers.rt_sigpending:
                return SysRtSigPending(engine, args[0], a2, a3, a4, a5, a6);
            case X86SyscallNumbers.rt_sigtimedwait_time64:
                return SysRtSigTimedWaitTime64(engine, args[0], args[1], args[2], a4, a5, a6);
            case X86SyscallNumbers.rt_sigqueueinfo:
                return SysRtSigQueueInfo(engine, a1, a2, args[2], a4, a5, a6);
            case X86SyscallNumbers.getresuid32:
                return SysGetResUid32(engine, args[0], args[1], args[2], a4, a5, a6);
            case X86SyscallNumbers.getresgid32:
                return SysGetResGid32(engine, args[0], args[1], args[2], a4, a5, a6);
            case X86SyscallNumbers.times:
                return SysTimes(engine, args[0], a2, a3, a4, a5, a6);
            case X86SyscallNumbers.getgroups32:
                return SysGetGroups32(engine, a1, args[1], a3, a4, a5, a6);
            case X86SyscallNumbers.setgroups32:
                return SysSetGroups32(engine, a1, args[1], a3, a4, a5, a6);
            case X86SyscallNumbers.sethostname:
                return SysSethostname(engine, args[0], a2, a3, a4, a5, a6);
            case X86SyscallNumbers.setdomainname:
                return SysSetdomainname(engine, args[0], a2, a3, a4, a5, a6);
            case X86SyscallNumbers.ugetrlimit:
                return SysUGetRlimit(engine, a1, args[1], a3, a4, a5, a6);
            case X86SyscallNumbers.setrlimit:
                return SysSetRlimit(engine, a1, args[1], a3, a4, a5, a6);
            case X86SyscallNumbers.getrusage:
                return SysGetRusage(engine, a1, args[1], a3, a4, a5, a6);
            case X86SyscallNumbers.getcpu:
                return SysGetCpu(engine, args[0], args[1], args[2], a4, a5, a6);
            case X86SyscallNumbers.socketpair:
                return SysSocketPair(engine, a1, a2, a3, args[3], a5, a6);
            case X86SyscallNumbers.bind:
                return SysBind(engine, a1, args[1], a3, a4, a5, a6);
            case X86SyscallNumbers.accept4:
                return SysAccept4(engine, a1, args[1], args[2], number == 202 ? 0 : a4, a5, a6);
            case X86SyscallNumbers.connect:
                return SysConnect(engine, a1, args[1], a3, a4, a5, a6);
            case X86SyscallNumbers.getsockname:
                return SysGetSockName(engine, a1, args[1], args[2], a4, a5, a6);
            case X86SyscallNumbers.getpeername:
                return SysGetPeerName(engine, a1, args[1], args[2], a4, a5, a6);
            case X86SyscallNumbers.sendto:
                return SysSendTo(engine, a1, args[1], a3, a4, args[4], a6);
            case X86SyscallNumbers.recvfrom:
                return SysRecvFrom(engine, a1, args[1], a3, a4, args[4], args[5]);
            case X86SyscallNumbers.setsockopt:
                return SysSetSockOpt(engine, a1, a2, a3, args[3], a5, a6);
            case X86SyscallNumbers.getsockopt:
                return SysGetSockOpt(engine, a1, a2, a3, args[3], args[4], a6);
            case X86SyscallNumbers.sendmsg:
                return SysSendMsg(engine, a1, args[1], a3, a4, a5, a6);
            case X86SyscallNumbers.recvmsg:
                return SysRecvMsg(engine, a1, args[1], a3, a4, a5, a6);
            case X86SyscallNumbers.remap_file_pages:
                return SysRemapFilePages(engine, args[0], args[1], a3, args[3], a5, a6);
            case X86SyscallNumbers.rt_tgsigqueueinfo:
                return SysRtTgSigQueueInfo(engine, a1, a2, a3, args[3], a5, a6);
            case X86SyscallNumbers.recvmmsg:
                return SysRecvMMsg(engine, a1, args[1], a3, a4, args[4], a6);
            case X86SyscallNumbers.wait4:
                return SysWait4(engine, a1, args[1], a3, args[3], a5, a6);
            case X86SyscallNumbers.prlimit64:
                return SysPrlimit64(engine, a1, a2, args[2], args[3], a5, a6);
            case X86SyscallNumbers.clock_adjtime64:
                return SysClockAdjTime64(engine, a1, args[1], a3, a4, a5, a6);
            case X86SyscallNumbers.sendmmsg:
                return SysSendMMsg(engine, a1, args[1], a3, a4, a5, a6);
            case X86SyscallNumbers.memfd_create:
                return SysMemfdCreate(engine, args[0], a2, a3, a4, a5, a6);
            case X86SyscallNumbers.copy_file_range:
                return SysCopyFileRange(engine, a1, args[1], a3, args[3], a5, a6);
            case X86SyscallNumbers.open_tree:
                return SysOpenTree(engine, a1, args[1], a3, a4, a5, a6);
            case X86SyscallNumbers.move_mount:
                return SysMoveMount(engine, a1, args[1], a3, args[3], a5, a6);
            case X86SyscallNumbers.fsopen:
                return SysFsopen(engine, args[0], a2, a3, a4, a5, a6);
            case X86SyscallNumbers.fsconfig:
                return SysFsconfig(engine, a1, a2, args[2], args[3], a5, a6);
            case X86SyscallNumbers.epoll_pwait2:
                return SysEpollPwait2(engine, a1, args[1], a3, args[3], args[4], a6);
            case X86SyscallNumbers.mount_setattr:
                return SysMountSetattr(engine, a1, args[1], a3, args[3], a5, a6);
            case X86SyscallNumbers.ioctl:
                return SysIoctl(engine, a1, a2, args[2], a4, a5, a6);
            case X86SyscallNumbers.fcntl:
                if (a2 is 12 or 13 or 14) // These commands are specific to 32-bit processes.
                    return ValueTask.FromResult(-(int)Errno.EINVAL);
                return SysFcntl64(engine, a1, a2, args[2], a4, a5, a6);
            case X86SyscallNumbers.epoll_ctl:
                return SysEpollCtl(engine, a1, a2, a3, args[3], a5, a6);
            case X86SyscallNumbers.prctl:
                if (a1 is LinuxConstants.PR_GET_TSC or LinuxConstants.PR_SET_TSC)
                    return ValueTask.FromResult(-(int)Errno.EINVAL);
                if (a1 == LinuxConstants.PR_GET_TIMERSLACK && engine.Owner is FiberTask task)
                {
                    wideResult = task.Process.TimerSlackNs;
                    return default;
                }
                if (a1 == LinuxConstants.PR_SET_TIMERSLACK && engine.Owner is FiberTask settingTask)
                {
                    var slack = unchecked((long)args[1]);
                    settingTask.Process.TimerSlackNs = slack <= 0 ? settingTask.Process.DefaultTimerSlackNs : slack;
                    return default;
                }
                return SysPrctl(engine, a1, args[1], a3, a4, a5, a6);
            case X86SyscallNumbers.ftruncate64:
                a3 = (uint)(args[1] >> 32);
                break;
            case X86SyscallNumbers.fallocate:
                a4 = (uint)(args[2] >> 32);
                a5 = (uint)args[3];
                a6 = (uint)(args[3] >> 32);
                break;
        }

        return DispatchSyscall(operation, engine, a1, a2, a3, a4, a5, a6, out handled);
    }

    private static ulong ReadGuestWord(ReadOnlySpan<byte> data, int wordSize) => wordSize == 8
        ? BinaryPrimitives.ReadUInt64LittleEndian(data)
        : BinaryPrimitives.ReadUInt32LittleEndian(data);

    private static void WriteGuestWord(Span<byte> data, int wordSize, ulong value)
    {
        if (wordSize == 8)
            BinaryPrimitives.WriteUInt64LittleEndian(data, value);
        else
            BinaryPrimitives.WriteUInt32LittleEndian(data, (uint)value);
    }

    private static int GuestWordSize(Engine engine) => engine.IsAArch64 ? 8 : 4;

    // arm64 places O_DIRECTORY/O_NOFOLLOW before O_DIRECT/O_LARGEFILE.
    // The same permutation converts from either ABI to the other.
    private static uint TranslateAArch64OpenFlags(uint flags)
        => (flags & ~0x3C000u) | ((flags & 0xC000u) << 2) | ((flags & 0x30000u) >> 2);
}
