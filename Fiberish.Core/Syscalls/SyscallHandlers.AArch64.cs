using System.Buffers.Binary;
using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;

namespace Fiberish.Syscalls;

public partial class SyscallManager
{
    // Linux arm64 uses the generic 64-bit syscall table. Dispatch to the same
    // operations as i386 after adapting register widths and argument layout.
    // Each mask marks the argument slots that contain pointers or sizes,
    // with register X0 at the low bit.
    private static (uint Operation, uint Pointers, uint Sizes) DecodeAArch64Syscall(uint number) => number switch
    {
        5 => (X86SyscallNumbers.setxattr, 0b000111, 0b001000),
        6 => (X86SyscallNumbers.lsetxattr, 0b000111, 0b001000),
        7 => (X86SyscallNumbers.fsetxattr, 0b000110, 0b001000),
        8 => (X86SyscallNumbers.getxattr, 0b000111, 0b001000),
        9 => (X86SyscallNumbers.lgetxattr, 0b000111, 0b001000),
        10 => (X86SyscallNumbers.fgetxattr, 0b000110, 0b001000),
        11 => (X86SyscallNumbers.listxattr, 0b000011, 0b000100),
        12 => (X86SyscallNumbers.llistxattr, 0b000011, 0b000100),
        13 => (X86SyscallNumbers.flistxattr, 0b000010, 0b000100),
        14 => (X86SyscallNumbers.removexattr, 0b000011, 0b000000),
        15 => (X86SyscallNumbers.lremovexattr, 0b000011, 0b000000),
        16 => (X86SyscallNumbers.fremovexattr, 0b000010, 0b000000),
        17 => (X86SyscallNumbers.getcwd, 0b000001, 0b000010),
        19 => (X86SyscallNumbers.eventfd2, 0b000000, 0b000000),
        20 => (X86SyscallNumbers.epoll_create1, 0b000000, 0b000000),
        21 => (X86SyscallNumbers.epoll_ctl, 0b000000, 0b000000),
        22 => (X86SyscallNumbers.epoll_pwait, 0b010010, 0b100000),
        23 => (X86SyscallNumbers.dup, 0b000000, 0b000000),
        24 => (X86SyscallNumbers.dup3, 0b000000, 0b000000),
        25 => (X86SyscallNumbers.fcntl, 0b000000, 0b000000),
        26 => (X86SyscallNumbers.inotify_init1, 0b000000, 0b000000),
        27 => (X86SyscallNumbers.inotify_add_watch, 0b000010, 0b000000),
        28 => (X86SyscallNumbers.inotify_rm_watch, 0b000000, 0b000000),
        29 => (X86SyscallNumbers.ioctl, 0b000000, 0b000000),
        32 => (X86SyscallNumbers.flock, 0b000000, 0b000000),
        33 => (X86SyscallNumbers.mknodat, 0b000010, 0b000000),
        34 => (X86SyscallNumbers.mkdirat, 0b000010, 0b000000),
        35 => (X86SyscallNumbers.unlinkat, 0b000010, 0b000000),
        36 => (X86SyscallNumbers.symlinkat, 0b000101, 0b000000),
        37 => (X86SyscallNumbers.linkat, 0b001010, 0b000000),
        38 => (X86SyscallNumbers.renameat, 0b001010, 0b000000),
        39 => (X86SyscallNumbers.umount2, 0b000001, 0b000000),
        40 => (X86SyscallNumbers.mount, 0b010111, 0b000000),
        43 => (X86SyscallNumbers.statfs, 0b000011, 0b000000),
        44 => (X86SyscallNumbers.fstatfs, 0b000010, 0b000000),
        45 => (X86SyscallNumbers.truncate64, 0b000001, 0b000000),
        46 => (X86SyscallNumbers.ftruncate64, 0b000000, 0b000000),
        47 => (X86SyscallNumbers.fallocate, 0b000000, 0b000000),
        48 => (X86SyscallNumbers.faccessat, 0b000010, 0b000000),
        49 => (X86SyscallNumbers.chdir, 0b000001, 0b000000),
        50 => (X86SyscallNumbers.fchdir, 0b000000, 0b000000),
        51 => (X86SyscallNumbers.chroot, 0b000001, 0b000000),
        52 => (X86SyscallNumbers.fchmod, 0b000000, 0b000000),
        53 => (X86SyscallNumbers.fchmodat, 0b000010, 0b000000),
        54 => (X86SyscallNumbers.fchownat, 0b000010, 0b000000),
        55 => (X86SyscallNumbers.fchown32, 0b000000, 0b000000),
        56 => (X86SyscallNumbers.openat, 0b000010, 0b000000),
        57 => (X86SyscallNumbers.close, 0b000000, 0b000000),
        59 => (X86SyscallNumbers.pipe2, 0b000001, 0b000000),
        61 => (X86SyscallNumbers.getdents64, 0b000010, 0b000000),
        62 => (X86SyscallNumbers.lseek, 0b000000, 0b000000),
        63 => (X86SyscallNumbers.read, 0b000010, 0b000100),
        64 => (X86SyscallNumbers.write, 0b000010, 0b000100),
        65 => (X86SyscallNumbers.readv, 0b000010, 0b000100),
        66 => (X86SyscallNumbers.writev, 0b000010, 0b000100),
        67 => (X86SyscallNumbers.pread, 0b000010, 0b000100),
        68 => (X86SyscallNumbers.pwrite, 0b000010, 0b000100),
        69 => (X86SyscallNumbers.preadv, 0b000010, 0b000100),
        70 => (X86SyscallNumbers.pwritev, 0b000010, 0b000100),
        71 => (X86SyscallNumbers.sendfile64, 0b000100, 0b001000),
        72 => (X86SyscallNumbers.pselect6_time64, 0b111110, 0b000000),
        73 => (X86SyscallNumbers.ppoll_time64, 0b001101, 0b010000),
        74 => (X86SyscallNumbers.signalfd4, 0b000010, 0b000100),
        75 => (X86SyscallNumbers.vmsplice, 0b000010, 0b000100),
        76 => (X86SyscallNumbers.splice, 0b001010, 0b010000),
        77 => (X86SyscallNumbers.tee, 0b000000, 0b000100),
        78 => (X86SyscallNumbers.readlinkat, 0b000110, 0b000000),
        79 => (X86SyscallNumbers.fstatat64, 0b000110, 0b000000),
        80 => (X86SyscallNumbers.fstat64, 0b000010, 0b000000),
        81 => (X86SyscallNumbers.sync, 0b000000, 0b000000),
        82 => (X86SyscallNumbers.fsync, 0b000000, 0b000000),
        83 => (X86SyscallNumbers.fdatasync, 0b000000, 0b000000),
        85 => (X86SyscallNumbers.timerfd_create, 0b000000, 0b000000),
        86 => (X86SyscallNumbers.timerfd_settime64, 0b001100, 0b000000),
        87 => (X86SyscallNumbers.timerfd_gettime64, 0b000010, 0b000000),
        88 => (X86SyscallNumbers.utimensat_time64, 0b000110, 0b000000),
        90 => (X86SyscallNumbers.capget, 0b000011, 0b000000),
        91 => (X86SyscallNumbers.capset, 0b000011, 0b000000),
        92 => (X86SyscallNumbers.personality, 0b000000, 0b000000),
        93 => (X86SyscallNumbers.exit, 0b000000, 0b000000),
        94 => (X86SyscallNumbers.exit_group, 0b000000, 0b000000),
        95 => (X86SyscallNumbers.waitid, 0b010100, 0b000000),
        96 => (X86SyscallNumbers.set_tid_address, 0b000001, 0b000000),
        98 => (X86SyscallNumbers.futex_time64, 0b010001, 0b000000),
        99 => (X86SyscallNumbers.set_robust_list, 0b000001, 0b000010),
        100 => (X86SyscallNumbers.get_robust_list, 0b000110, 0b000000),
        101 => (X86SyscallNumbers.nanosleep, 0b000011, 0b000000),
        102 => (X86SyscallNumbers.getitimer, 0b000010, 0b000000),
        103 => (X86SyscallNumbers.setitimer, 0b000110, 0b000000),
        107 => (X86SyscallNumbers.timer_create, 0b000110, 0b000000),
        108 => (X86SyscallNumbers.timer_gettime, 0b000010, 0b000000),
        109 => (X86SyscallNumbers.timer_getoverrun, 0b000000, 0b000000),
        110 => (X86SyscallNumbers.timer_settime64, 0b001100, 0b000000),
        111 => (X86SyscallNumbers.timer_delete, 0b000000, 0b000000),
        112 => (X86SyscallNumbers.clock_settime64, 0b000010, 0b000000),
        113 => (X86SyscallNumbers.clock_gettime64, 0b000010, 0b000000),
        114 => (X86SyscallNumbers.clock_getres_time64, 0b000010, 0b000000),
        115 => (X86SyscallNumbers.clock_nanosleep_time64, 0b001100, 0b000000),
        120 => (X86SyscallNumbers.sched_getscheduler, 0b000000, 0b000000),
        121 => (X86SyscallNumbers.sched_getparam, 0b000010, 0b000000),
        123 => (X86SyscallNumbers.sched_getaffinity, 0b000100, 0b000000),
        124 => (X86SyscallNumbers.sched_yield, 0b000000, 0b000000),
        129 => (X86SyscallNumbers.kill, 0b000000, 0b000000),
        130 => (X86SyscallNumbers.tkill, 0b000000, 0b000000),
        131 => (X86SyscallNumbers.tgkill, 0b000000, 0b000000),
        132 => (X86SyscallNumbers.sigaltstack, 0b000011, 0b000000),
        133 => (X86SyscallNumbers.rt_sigsuspend, 0b000001, 0b000010),
        134 => (X86SyscallNumbers.rt_sigaction, 0b000110, 0b001000),
        135 => (X86SyscallNumbers.rt_sigprocmask, 0b000110, 0b001000),
        136 => (X86SyscallNumbers.rt_sigpending, 0b000001, 0b000010),
        137 => (X86SyscallNumbers.rt_sigtimedwait_time64, 0b000111, 0b001000),
        138 => (X86SyscallNumbers.rt_sigqueueinfo, 0b000100, 0b000000),
        139 => (X86SyscallNumbers.rt_sigreturn, 0b000000, 0b000000),
        140 => (X86SyscallNumbers.setpriority, 0b000000, 0b000000),
        141 => (X86SyscallNumbers.getpriority, 0b000000, 0b000000),
        143 => (X86SyscallNumbers.setregid32, 0b000000, 0b000000),
        144 => (X86SyscallNumbers.setgid32, 0b000000, 0b000000),
        145 => (X86SyscallNumbers.setreuid32, 0b000000, 0b000000),
        146 => (X86SyscallNumbers.setuid32, 0b000000, 0b000000),
        147 => (X86SyscallNumbers.setresuid32, 0b000000, 0b000000),
        148 => (X86SyscallNumbers.getresuid32, 0b000111, 0b000000),
        149 => (X86SyscallNumbers.setresgid32, 0b000000, 0b000000),
        150 => (X86SyscallNumbers.getresgid32, 0b000111, 0b000000),
        151 => (X86SyscallNumbers.setfsuid32, 0b000000, 0b000000),
        152 => (X86SyscallNumbers.setfsgid32, 0b000000, 0b000000),
        153 => (X86SyscallNumbers.times, 0b000001, 0b000000),
        154 => (X86SyscallNumbers.setpgid, 0b000000, 0b000000),
        155 => (X86SyscallNumbers.getpgid, 0b000000, 0b000000),
        156 => (X86SyscallNumbers.getsid, 0b000000, 0b000000),
        157 => (X86SyscallNumbers.setsid, 0b000000, 0b000000),
        158 => (X86SyscallNumbers.getgroups32, 0b000010, 0b000000),
        159 => (X86SyscallNumbers.setgroups32, 0b000010, 0b000000),
        160 => (X86SyscallNumbers.uname, 0b000001, 0b000000),
        161 => (X86SyscallNumbers.sethostname, 0b000001, 0b000000),
        162 => (X86SyscallNumbers.setdomainname, 0b000001, 0b000000),
        163 => (X86SyscallNumbers.ugetrlimit, 0b000010, 0b000000),
        164 => (X86SyscallNumbers.setrlimit, 0b000010, 0b000000),
        165 => (X86SyscallNumbers.getrusage, 0b000010, 0b000000),
        166 => (X86SyscallNumbers.umask, 0b000000, 0b000000),
        167 => (X86SyscallNumbers.prctl, 0b000000, 0b000000),
        168 => (X86SyscallNumbers.getcpu, 0b000111, 0b000000),
        169 => (X86SyscallNumbers.gettimeofday, 0b000011, 0b000000),
        172 => (X86SyscallNumbers.getpid, 0b000000, 0b000000),
        173 => (X86SyscallNumbers.getppid, 0b000000, 0b000000),
        174 => (X86SyscallNumbers.getuid32, 0b000000, 0b000000),
        175 => (X86SyscallNumbers.geteuid32, 0b000000, 0b000000),
        176 => (X86SyscallNumbers.getgid32, 0b000000, 0b000000),
        177 => (X86SyscallNumbers.getegid32, 0b000000, 0b000000),
        178 => (X86SyscallNumbers.gettid, 0b000000, 0b000000),
        179 => (X86SyscallNumbers.sysinfo, 0b000001, 0b000000),
        190 => (X86SyscallNumbers.semget, 0b000000, 0b000000),
        191 => (X86SyscallNumbers.semctl, 0b000000, 0b000000),
        192 => (X86SyscallNumbers.semtimedop_time64, 0b001010, 0b000000),
        193 => (X86SyscallNumbers.semtimedop_time64, 0b000010, 0b000000),
        194 => (X86SyscallNumbers.shmget, 0b000000, 0b000010),
        195 => (X86SyscallNumbers.shmctl, 0b000000, 0b000000),
        196 => (X86SyscallNumbers.shmat, 0b000010, 0b000000),
        197 => (X86SyscallNumbers.shmdt, 0b000001, 0b000000),
        198 => (X86SyscallNumbers.socket, 0b000000, 0b000000),
        199 => (X86SyscallNumbers.socketpair, 0b001000, 0b000000),
        200 => (X86SyscallNumbers.bind, 0b000010, 0b000000),
        201 => (X86SyscallNumbers.listen, 0b000000, 0b000000),
        202 => (X86SyscallNumbers.accept4, 0b000110, 0b000000),
        203 => (X86SyscallNumbers.connect, 0b000010, 0b000000),
        204 => (X86SyscallNumbers.getsockname, 0b000110, 0b000000),
        205 => (X86SyscallNumbers.getpeername, 0b000110, 0b000000),
        206 => (X86SyscallNumbers.sendto, 0b010010, 0b000100),
        207 => (X86SyscallNumbers.recvfrom, 0b110010, 0b000100),
        208 => (X86SyscallNumbers.setsockopt, 0b001000, 0b000000),
        209 => (X86SyscallNumbers.getsockopt, 0b011000, 0b000000),
        210 => (X86SyscallNumbers.shutdown, 0b000000, 0b000000),
        211 => (X86SyscallNumbers.sendmsg, 0b000010, 0b000000),
        212 => (X86SyscallNumbers.recvmsg, 0b000010, 0b000000),
        213 => (X86SyscallNumbers.readahead, 0b000000, 0b000100),
        214 => (X86SyscallNumbers.brk, 0b000000, 0b000000),
        215 => (X86SyscallNumbers.munmap, 0b000001, 0b000010),
        216 => (X86SyscallNumbers.mremap, 0b000001, 0b000110),
        220 => (X86SyscallNumbers.clone, 0b000010, 0b000000),
        221 => (X86SyscallNumbers.execve, 0b000111, 0b000000),
        222 => (X86SyscallNumbers.mmap2, 0b000000, 0b000010),
        223 => (X86SyscallNumbers.fadvise64, 0b000000, 0b000000),
        226 => (X86SyscallNumbers.mprotect, 0b000001, 0b000010),
        227 => (X86SyscallNumbers.msync, 0b000001, 0b000010),
        228 => (X86SyscallNumbers.mlock, 0b000001, 0b000010),
        229 => (X86SyscallNumbers.munlock, 0b000001, 0b000010),
        230 => (X86SyscallNumbers.mlockall, 0b000000, 0b000000),
        231 => (X86SyscallNumbers.munlockall, 0b000000, 0b000000),
        232 => (X86SyscallNumbers.mincore, 0b000101, 0b000010),
        233 => (X86SyscallNumbers.madvise, 0b000001, 0b000010),
        234 => (X86SyscallNumbers.remap_file_pages, 0b000001, 0b001010),
        240 => (X86SyscallNumbers.rt_tgsigqueueinfo, 0b001000, 0b000000),
        242 => (X86SyscallNumbers.accept4, 0b000110, 0b000000),
        243 => (X86SyscallNumbers.recvmmsg, 0b010010, 0b000000),
        260 => (X86SyscallNumbers.wait4, 0b001010, 0b000000),
        261 => (X86SyscallNumbers.prlimit64, 0b001100, 0b000000),
        266 => (X86SyscallNumbers.clock_adjtime64, 0b000010, 0b000000),
        269 => (X86SyscallNumbers.sendmmsg, 0b000010, 0b000000),
        276 => (X86SyscallNumbers.renameat2, 0b001010, 0b000000),
        278 => (X86SyscallNumbers.getrandom, 0b000001, 0b000010),
        279 => (X86SyscallNumbers.memfd_create, 0b000001, 0b000000),
        283 => (X86SyscallNumbers.membarrier, 0b000000, 0b000000),
        284 => (X86SyscallNumbers.mlock2, 0b000001, 0b000010),
        285 => (X86SyscallNumbers.copy_file_range, 0b001010, 0b010000),
        286 => (X86SyscallNumbers.preadv2, 0b000010, 0b000100),
        287 => (X86SyscallNumbers.pwritev2, 0b000010, 0b000100),
        291 => (X86SyscallNumbers.statx, 0b010010, 0b000000),
        428 => (X86SyscallNumbers.open_tree, 0b000010, 0b000000),
        429 => (X86SyscallNumbers.move_mount, 0b001010, 0b000000),
        430 => (X86SyscallNumbers.fsopen, 0b000001, 0b000000),
        431 => (X86SyscallNumbers.fsconfig, 0b001100, 0b000000),
        432 => (X86SyscallNumbers.fsmount, 0b000000, 0b000000),
        436 => (X86SyscallNumbers.close_range, 0b000000, 0b000000),
        437 => (X86SyscallNumbers.openat2, 0b000110, 0b001000),
        439 => (X86SyscallNumbers.faccessat2, 0b000010, 0b000000),
        441 => (X86SyscallNumbers.epoll_pwait2, 0b011010, 0b100000),
        442 => (X86SyscallNumbers.mount_setattr, 0b001010, 0b010000),
        452 => (X86SyscallNumbers.fchmodat2, 0b000010, 0b000000),
        _ => (uint.MaxValue, 0, 0)
    };

    private ValueTask<int> DispatchAArch64Syscall(uint number, uint operation, Engine engine,
        ReadOnlySpan<ulong> args, uint pointers, uint sizes, out bool handled, out long? wideResult,
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
                return SysMadvise(engine, args[0], args[1], (uint)args[2], 0, 0, 0);
            case X86SyscallNumbers.msync:
                return SysMsync(engine, args[0], args[1], (uint)args[2], 0, 0, 0);
            case X86SyscallNumbers.mincore:
                return SysMincore(engine, args[0], args[1], args[2], 0, 0, 0);
            case X86SyscallNumbers.mlock:
                return SysMlock(engine, args[0], args[1], 0, 0, 0, 0);
            case X86SyscallNumbers.mlock2:
                return SysMlock2(engine, args[0], args[1], (uint)args[2], 0, 0, 0);
            case X86SyscallNumbers.munlock:
                return SysMunlock(engine, args[0], args[1], 0, 0, 0, 0);
            case X86SyscallNumbers.mprotect:
                return SysMprotect(engine, args[0], args[1], (uint)args[2], 0, 0, 0);
            case X86SyscallNumbers.rt_sigaction:
                if (args[3] != 8) return ValueTask.FromResult(-(int)Errno.EINVAL);
                return SysRtSigAction(engine, (uint)args[0], args[1], args[2], 8, 0, 0);
            case X86SyscallNumbers.sigaltstack:
                return SysSigAltStack(engine, args[0], args[1], 0, 0, 0, 0);
            case X86SyscallNumbers.execve:
                return SysExecve(engine, args[0], args[1], args[2], 0, 0, 0);
            case X86SyscallNumbers.clone:
                return CloneTask(engine, (uint)args[0], args[1], args[2], args[3], args[4]);
            case X86SyscallNumbers.set_tid_address:
                return SysSetTidAddress(engine, args[0], 0, 0, 0, 0, 0);
            case X86SyscallNumbers.set_robust_list:
                return SysSetRobustList(engine, args[0], args[1], 0, 0, 0, 0);
            case X86SyscallNumbers.get_robust_list:
                return SysGetRobustList(engine, (uint)args[0], args[1], args[2], 0, 0, 0);
            case X86SyscallNumbers.futex_time64:
                return SysFutexTime64(engine, args[0], (uint)args[1], (uint)args[2], args[3], args[4], (uint)args[5]);
            case X86SyscallNumbers.getcwd:
                return SysGetCwd(engine, args[0], args[1], 0, 0, 0, 0);
            case X86SyscallNumbers.mknodat:
                return SysMknodat(engine, (uint)args[0], args[1], (uint)args[2], (uint)args[3], 0, 0);
            case X86SyscallNumbers.mkdirat:
                return SysMkdirAt(engine, (uint)args[0], args[1], (uint)args[2], 0, 0, 0);
            case X86SyscallNumbers.unlinkat:
                return SysUnlinkAt(engine, (uint)args[0], args[1], (uint)args[2], 0, 0, 0);
            case X86SyscallNumbers.symlinkat:
                return SysSymlinkAt(engine, args[0], (uint)args[1], args[2], 0, 0, 0);
            case X86SyscallNumbers.linkat:
                return SysLinkat(engine, (uint)args[0], args[1], (uint)args[2], args[3], (uint)args[4], 0);
            case X86SyscallNumbers.renameat:
                return SysRenameAt(engine, (uint)args[0], args[1], (uint)args[2], args[3], 0, 0);
            case X86SyscallNumbers.renameat2:
                return SysRenameAt2(engine, (uint)args[0], args[1], (uint)args[2], args[3], (uint)args[4], 0);
            case X86SyscallNumbers.statfs:
                return SysStatfs(engine, args[0], args[1], 0, 0, 0, 0);
            case X86SyscallNumbers.fstatfs:
                return SysFstatfs(engine, (uint)args[0], args[1], 0, 0, 0, 0);
            case X86SyscallNumbers.faccessat:
                return SysFaccessAt(engine, (uint)args[0], args[1], (uint)args[2], 0, 0, 0);
            case X86SyscallNumbers.faccessat2:
                return SysFaccessAt2(engine, (uint)args[0], args[1], (uint)args[2], (uint)args[3], 0, 0);
            case X86SyscallNumbers.chdir:
                return SysChdir(engine, args[0], 0, 0, 0, 0, 0);
            case X86SyscallNumbers.chroot:
                return SysChroot(engine, args[0], 0, 0, 0, 0, 0);
            case X86SyscallNumbers.fchmodat:
                return SysFchmodAt(engine, (uint)args[0], args[1], (uint)args[2], 0, 0, 0);
            case X86SyscallNumbers.fchmodat2:
                return SysFchmodAt2(engine, (uint)args[0], args[1], (uint)args[2], (uint)args[3], 0, 0);
            case X86SyscallNumbers.fchownat:
                return SysFchownAt(engine, (uint)args[0], args[1], (uint)args[2], (uint)args[3], (uint)args[4], 0);
            case X86SyscallNumbers.getdents64:
                return SysGetdents64(engine, (uint)args[0], args[1], (uint)args[2], 0, 0, 0);
            case X86SyscallNumbers.readlinkat:
                return SysReadlinkAt(engine, (uint)args[0], args[1], args[2], (uint)args[3], 0, 0);
            case X86SyscallNumbers.fstatat64:
                return SysNewFstatAt(engine, (uint)args[0], args[1], args[2], (uint)args[3], 0, 0);
            case X86SyscallNumbers.fstat64:
                return SysFstat64(engine, (uint)args[0], args[1], 0, 0, 0, 0);
            case X86SyscallNumbers.statx:
                return SysStatx(engine, (uint)args[0], args[1], (uint)args[2], (uint)args[3], args[4], 0);
            case X86SyscallNumbers.utimensat_time64:
                return SysUtimensAtTime64(engine, (uint)args[0], args[1], args[2], (uint)args[3], 0, 0);
            case X86SyscallNumbers.truncate64:
                return SysTruncate64(engine, args[0], (uint)args[1], (uint)(args[1] >> 32), 0, 0, 0);
            case X86SyscallNumbers.openat:
                return SysOpenAt(engine, (uint)args[0], args[1], (uint)args[2], (uint)args[3], 0, 0);
            case X86SyscallNumbers.openat2:
                if (args[3] > uint.MaxValue) return ValueTask.FromResult(-(int)Errno.EINVAL);
                return SysOpenAt2(engine, (uint)args[0], args[1], args[2], (uint)args[3], 0, 0);
            case X86SyscallNumbers.clock_gettime64:
                return SysClockGetTime64(engine, (uint)args[0], args[1], 0, 0, 0, 0);
            case X86SyscallNumbers.gettimeofday:
                return SysGetTimeOfDay(engine, args[0], args[1], 0, 0, 0, 0);
            case X86SyscallNumbers.sysinfo:
                return SysSysinfo(engine, args[0], 0, 0, 0, 0, 0);
            case X86SyscallNumbers.uname:
                return SysUname(engine, args[0], 0, 0, 0, 0, 0);
            case X86SyscallNumbers.getrandom:
                if (args[1] > uint.MaxValue) return ValueTask.FromResult(-(int)Errno.EINVAL);
                return SysGetRandom(engine, args[0], (uint)args[1], (uint)args[2], 0, 0, 0);
            case X86SyscallNumbers.semctl:
                if ((args[2] & LinuxConstants.IPC_64) != 0)
                    return ValueTask.FromResult(-(int)Errno.EINVAL);
                return SysSemCtl(engine, (uint)args[0], (uint)args[1], (uint)args[2], args[3], 0, 0);
            case X86SyscallNumbers.semtimedop_time64:
                return DoSemTimedOp(engine, (int)args[0], args[1], (uint)args[2], number == 192 ? args[3] : 0,
                    time64: true);
            case X86SyscallNumbers.shmget:
                return SysShmGet(engine, (uint)args[0], args[1], (uint)args[2]);
            case X86SyscallNumbers.shmctl:
                return SysShmCtl(engine, (uint)args[0], (uint)args[1], args[2]);
            case X86SyscallNumbers.shmat:
                wideResult = AttachSharedMemory(engine, (uint)args[0], args[1], (uint)args[2]);
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
                if (args[2] > uint.MaxValue) return ValueTask.FromResult(-(int)Errno.EINVAL);
                var fd = (uint)args[0];
                var count = (uint)args[2];
                var offsetLow = (uint)args[3];
                var offsetHigh = (uint)(args[3] >> 32);
                var flags = (uint)args[5];
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
        }

        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] <= uint.MaxValue) continue;
            var bit = 1u << i;
            if ((pointers & bit) != 0) return ValueTask.FromResult(-(int)Errno.EFAULT);
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
            case X86SyscallNumbers.lseek:
                wideResult = SeekFile((int)a1, (long)args[1], (int)a3);
                return default;
            case X86SyscallNumbers.accept4 when number == 202:
                a4 = 0; // accept() has no flags argument.
                break;
            case X86SyscallNumbers.ioctl:
                if (args[2] > uint.MaxValue) return ValueTask.FromResult(-(int)Errno.EFAULT);
                break;
            case X86SyscallNumbers.fcntl:
                if (a2 is 5 or 6 or 7 or 15 or 16 or 36 or 37 or 38 && args[2] > uint.MaxValue)
                    return ValueTask.FromResult(-(int)Errno.EFAULT);
                if (a2 is 12 or 13 or 14) // These commands are specific to 32-bit processes.
                    return ValueTask.FromResult(-(int)Errno.EINVAL);
                break;
            case X86SyscallNumbers.epoll_ctl:
                if (a2 != LinuxConstants.EPOLL_CTL_DEL && args[3] > uint.MaxValue)
                    return ValueTask.FromResult(-(int)Errno.EFAULT);
                break;
            case X86SyscallNumbers.prctl:
                if (a1 is LinuxConstants.PR_GET_NAME or LinuxConstants.PR_SET_NAME
                    or LinuxConstants.PR_GET_PDEATHSIG or LinuxConstants.PR_GET_CHILD_SUBREAPER)
                {
                    if (args[1] > uint.MaxValue) return ValueTask.FromResult(-(int)Errno.EFAULT);
                }
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
                break;
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
