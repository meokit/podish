using Fiberish.Native;

namespace Fiberish.Syscalls;

public partial class SyscallManager
{
    private void RegisterHandlers()
    {
        Register(X86SyscallNumbers.exit, SysExit);
        Register(X86SyscallNumbers.fork, SysFork);
        Register(X86SyscallNumbers.fcntl64, SysFcntl64);
        Register(X86SyscallNumbers.waitpid, SysWaitPid);
        Register(X86SyscallNumbers.read, SysRead);
        Register(X86SyscallNumbers.preadv2, SysPReadV2);
        Register(X86SyscallNumbers.pwritev2, SysPWriteV2);
        Register(X86SyscallNumbers.statx, SysStatx);
        Register(X86SyscallNumbers.openat2, SysOpenAt2);
        Register(X86SyscallNumbers.write, SysWrite);
        Register(X86SyscallNumbers.open, SysOpen);
        Register(X86SyscallNumbers.close, SysClose);
        Register(X86SyscallNumbers.unlink, SysUnlink);
        Register(X86SyscallNumbers.chmod, SysChmod);
        Register(X86SyscallNumbers.chown, SysChown);
        Register(X86SyscallNumbers.lseek, SysLseek);
        Register(X86SyscallNumbers._llseek, SysLlseek);
        Register(X86SyscallNumbers.getpid, SysGetPid);
        Register(X86SyscallNumbers.mount, SysMount);
        Register(X86SyscallNumbers.umount, SysUmount);
        Register(X86SyscallNumbers.umount2, SysUmount2);
        Register(X86SyscallNumbers.setuid, SysSetUid);
        Register(X86SyscallNumbers.getuid, SysGetUid);
        Register(X86SyscallNumbers.access, SysAccess);
        Register(X86SyscallNumbers.brk, SysBrk);
        Register(X86SyscallNumbers.setgid, SysSetGid);
        Register(X86SyscallNumbers.getgid, SysGetGid);
        Register(X86SyscallNumbers.signal, SysSignal);
        Register(X86SyscallNumbers.geteuid, SysGetEUid);
        Register(X86SyscallNumbers.getegid, SysGetEGid);
        Register(X86SyscallNumbers.ioctl, SysIoctl);
        Register(X86SyscallNumbers.chroot, SysChroot);
        Register(X86SyscallNumbers.getppid, SysGetPPid);
        Register(X86SyscallNumbers.setreuid, SysSetReUid);
        Register(X86SyscallNumbers.setregid, SysSetReGid);
        Register(X86SyscallNumbers.munmap, SysMunmap);
        Register(X86SyscallNumbers.fchmod, SysFchmod);
        Register(X86SyscallNumbers.fchown, SysFchown);
        Register(X86SyscallNumbers.sigreturn, SysSigReturn);
        Register(X86SyscallNumbers.clone, SysClone);
        Register(X86SyscallNumbers.uname, SysUname);
        Register(X86SyscallNumbers.sysinfo, SysSysinfo);
        Register(X86SyscallNumbers.mprotect, SysMprotect);
        Register(X86SyscallNumbers.setfsuid, SysSetFsUid);
        Register(X86SyscallNumbers.setfsgid, SysSetFsGid);
        Register(X86SyscallNumbers.writev, SysWriteV);
        Register(X86SyscallNumbers.setresuid, SysSetResUid);
        Register(X86SyscallNumbers.getresuid, SysGetResUid);
        Register(X86SyscallNumbers.setresgid, SysSetResGid);
        Register(X86SyscallNumbers.getresgid, SysGetResGid);
        Register(X86SyscallNumbers.rt_sigaction, SysRtSigAction);
        Register(X86SyscallNumbers.rt_sigprocmask, SysRtSigProcMask);
        Register(X86SyscallNumbers.chown32, SysChown);
        Register(X86SyscallNumbers.getcwd, SysGetCwd);
        Register(X86SyscallNumbers.mmap2, SysMmap2);
        Register(X86SyscallNumbers.statfs, SysStatfs);
        Register(X86SyscallNumbers.fstatfs, SysFstatfs);
        Register(X86SyscallNumbers.stat64, SysStat64);
        Register(X86SyscallNumbers.lstat64, SysLstat64);
        Register(X86SyscallNumbers.fstat64, SysFstat64);
        Register(X86SyscallNumbers.lchown32, SysLchown);
        Register(X86SyscallNumbers.getgroups, SysGetGroups);
        Register(X86SyscallNumbers.setgroups, SysSetGroups);
        Register(X86SyscallNumbers.fchown32, SysFchown);
        Register(X86SyscallNumbers.getdents64, SysGetdents64);
        Register(X86SyscallNumbers.wait4, SysWait4);
        Register(X86SyscallNumbers.vfork, SysVfork);
        Register(X86SyscallNumbers.futex, SysFutex);
        Register(X86SyscallNumbers.set_thread_area, SysSetThreadArea);
        Register(X86SyscallNumbers.exit_group, SysExitGroup);
        Register(X86SyscallNumbers.set_tid_address, SysSetTidAddress);
        Register(X86SyscallNumbers.waitid, SysWaitId);
        Register(X86SyscallNumbers.fchownat, SysFchownAt);
        Register(X86SyscallNumbers.fchmodat, SysFchmodAt);
        Register(X86SyscallNumbers.faccessat, SysFaccessAt);
        Register(X86SyscallNumbers.statfs64, SysStatfs64);
        Register(X86SyscallNumbers.fstatfs64, SysFstatfs64);

        Register(X86SyscallNumbers.creat, SysCreat);
        Register(X86SyscallNumbers.link, SysLink);
        Register(X86SyscallNumbers.chdir, SysChdir);
        Register(X86SyscallNumbers.time, SysTime);

        Register(X86SyscallNumbers.openat, SysOpenAt);
        Register(X86SyscallNumbers.dup, SysDup);
        Register(X86SyscallNumbers.dup2, SysDup2);
        Register(X86SyscallNumbers.dup3, SysDup3);

        Register(X86SyscallNumbers.pread, SysPRead);
        Register(X86SyscallNumbers.pwrite, SysPWrite);
        Register(X86SyscallNumbers.readv, SysReadV);
        Register(X86SyscallNumbers.preadv, SysPReadV);
        Register(X86SyscallNumbers.pwritev, SysPWriteV);

        Register(X86SyscallNumbers.mkdir, SysMkdir);
        Register(X86SyscallNumbers.rmdir, SysRmdir);
        Register(X86SyscallNumbers.mkdirat, SysMkdirAt);
        Register(X86SyscallNumbers.unlinkat, SysUnlinkAt);
        Register(X86SyscallNumbers.symlink, SysSymlink);
        Register(X86SyscallNumbers.readlink, SysReadlink);
        Register(X86SyscallNumbers.readlinkat, SysReadlinkAt);
        Register(X86SyscallNumbers.getdents, SysGetdents);

        Register(X86SyscallNumbers.fstatat64, SysNewFstatAt);
        Register(X86SyscallNumbers.utimensat, SysUtimensAt);

        Register(X86SyscallNumbers.rename, SysRename);
        Register(X86SyscallNumbers.renameat, SysRenameAt);
        Register(X86SyscallNumbers.renameat2, SysRenameAt2);

        Register(X86SyscallNumbers.getuid32, SysGetUid32);
        Register(X86SyscallNumbers.getgid32, SysGetGid32);
        Register(X86SyscallNumbers.geteuid32, SysGetEUid32);
        Register(X86SyscallNumbers.getegid32, SysGetEGid32);
        Register(X86SyscallNumbers.setuid32, SysSetUid32);
        Register(X86SyscallNumbers.setgid32, SysSetGid32);
        Register(X86SyscallNumbers.clock_gettime, SysClockGetTime);
        Register(X86SyscallNumbers.clock_gettime64, SysClockGetTime64);
        Register(X86SyscallNumbers.clock_gettime64, SysClockGetTime64);
        Register(X86SyscallNumbers.gettimeofday, SysGetTimeOfDay);
        Register(X86SyscallNumbers.nanosleep, SysNanosleep);

        Register(X86SyscallNumbers.rt_sigreturn, SysRtSigReturn);
        Register(X86SyscallNumbers.rt_sigsuspend, SysRtSigSuspend);

        Register(X86SyscallNumbers.gettid, SysGettid);
        Register(X86SyscallNumbers.getpgid, SysGetPgid);
        Register(X86SyscallNumbers.setpgid, SysSetPgid);
        Register(X86SyscallNumbers.setsid, SysSetSid);
        Register(X86SyscallNumbers.getsid, SysGetSid);
        Register(X86SyscallNumbers.getpgrp, SysGetPgrp);
        Register(X86SyscallNumbers.umask, SysUmask);
        Register(X86SyscallNumbers.sethostname, SysSethostname);
        Register(X86SyscallNumbers.setdomainname, SysSetdomainname);
        Register(X86SyscallNumbers.sched_yield, SysSchedYield);
        Register(X86SyscallNumbers.pause, SysPause);
        
        // Network Syscalls
        Register(X86SyscallNumbers.socketcall, SysSocketCall);
        Register(X86SyscallNumbers.socket, SysSocket);
        Register(X86SyscallNumbers.bind, SysBind);
        Register(X86SyscallNumbers.connect, SysConnect);
        Register(X86SyscallNumbers.listen, SysListen);
        Register(X86SyscallNumbers.accept4, SysAccept4);
        Register(X86SyscallNumbers.sendto, SysSendTo);
        Register(X86SyscallNumbers.recvfrom, SysRecvFrom);

        // Alarm
        Register(27, SysAlarm); // alarm

        // POSIX Timers 32-bit (Using 64-bit implementations because timespec padding allows identical parsing if careful, or we just direct them to the 64-bit handlers that adapt for missing padding if applicable. For now, we point to the implementations in Time64).
        Register(259, SysTimerCreate);
        Register(260, SysTimerSetTime32);
        Register(261, SysTimerGetTime32);
        Register(262, SysTimerGetOverrun);
        Register(263, SysTimerDelete);

        // 64-bit time syscalls (i386)
        Register(403, SysClockGetTime64);
        Register(404, SysClockSetTime64);
        Register(405, SysClockAdjTime64);
        Register(406, SysClockGetResTime64);
        Register(407, SysClockNanosleepTime64);
        Register(408, SysTimerGetTime64);
        Register(409, SysTimerSetTime64);
        Register(410, SysTimerFdGetTime64);
        Register(411, SysTimerFdSetTime64);
        
        // FDs / Virtual
        Register(322, SysTimerFdCreate); // timerfd_create
        Register(325, SysTimerFdSetTime); // timerfd_settime
        Register(326, SysTimerFdGetTime); // timerfd_gettime
        
        Register(323, SysEventFd); // eventfd
        Register(328, SysEventFd2); // eventfd2
        
        Register(321, SysSignalFd); // signalfd
        Register(327, SysSignalFd4); // signalfd4

        Register(X86SyscallNumbers.fsync, SysFsync);
        Register(X86SyscallNumbers.fdatasync, SysFdatasync);
        Register(X86SyscallNumbers.sync, SysSync);
        Register(X86SyscallNumbers.madvise, SysMadvise);
        Register(X86SyscallNumbers.msync, SysMsync);

        Register(X86SyscallNumbers.kill, SysKill);
        Register(X86SyscallNumbers.tkill, SysTkill);
        Register(X86SyscallNumbers.tgkill, SysTgkill);
        Register(X86SyscallNumbers.rt_sigqueueinfo, SysRtSigQueueInfo);
        Register(X86SyscallNumbers.rt_tgsigqueueinfo, SysRtTgSigQueueInfo);
        Register(X86SyscallNumbers.execve, SysExecve);

        Register(X86SyscallNumbers.select, SysSelect);
        Register(X86SyscallNumbers._newselect, SysNewSelect);
        Register(X86SyscallNumbers.poll, SysPoll);
        Register(X86SyscallNumbers.pipe, SysPipe);
        Register(X86SyscallNumbers.sendfile64, SysSendfile64);
        Register(X86SyscallNumbers.memfd_create, SysMemfdCreate);
        Register(X86SyscallNumbers.epoll_create, SysEpollCreate);
        Register(X86SyscallNumbers.epoll_create1, SysEpollCreate1);
        Register(X86SyscallNumbers.epoll_ctl, SysEpollCtl);
        Register(X86SyscallNumbers.epoll_wait, SysEpollWait);
        Register(X86SyscallNumbers.epoll_pwait, SysEpollPwait);

        // System V IPC
        Register(X86SyscallNumbers.ipc, SysIpc);
        Register(X86SyscallNumbers.semget, SysSemGet);
        Register(X86SyscallNumbers.semctl, SysSemCtl);
        Register(X86SyscallNumbers.semop, SysSemOp);
    }
}
