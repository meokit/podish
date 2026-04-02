using Fiberish.Core;
using Fiberish.Native;

namespace Fiberish.Syscalls;

public partial class SyscallManager
{
    private ValueTask<int> DispatchSyscall(uint eax, Engine engine, uint ebx, uint ecx, uint edx, uint esi, uint edi,
        uint ebp, out bool handled)
    {
        switch (eax)
        {
            case X86SyscallNumbers.exit:
                handled = true;
                return SysExit(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.fork:
                handled = true;
                return SysFork(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.fcntl64:
                handled = true;
                return SysFcntl64(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.waitpid:
                handled = true;
                return SysWaitPid(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.read:
                handled = true;
                return SysRead(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.preadv2:
                handled = true;
                return SysPReadV2(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.pwritev2:
                handled = true;
                return SysPWriteV2(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.statx:
                handled = true;
                return SysStatx(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.openat2:
                handled = true;
                return SysOpenAt2(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.write:
                handled = true;
                return SysWrite(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.open:
                handled = true;
                return SysOpen(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.close:
                handled = true;
                return SysClose(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.unlink:
                handled = true;
                return SysUnlink(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.mknod:
                handled = true;
                return SysMknod(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.chmod:
                handled = true;
                return SysChmod(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.chown:
                handled = true;
                return SysChown(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.lchown:
                handled = true;
                return SysLchown(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.lseek:
                handled = true;
                return SysLseek(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers._llseek:
                handled = true;
                return SysLlseek(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.getpid:
                handled = true;
                return SysGetPid(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.mount:
                handled = true;
                return SysMount(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.umount:
                handled = true;
                return SysUmount(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.umount2:
                handled = true;
                return SysUmount2(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.setuid:
                handled = true;
                return SysSetUid(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.getuid:
                handled = true;
                return SysGetUid(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.access:
                handled = true;
                return SysAccess(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.brk:
                handled = true;
                return SysBrk(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.setgid:
                handled = true;
                return SysSetGid(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.getgid:
                handled = true;
                return SysGetGid(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.signal:
                handled = true;
                return SysSignal(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.geteuid:
                handled = true;
                return SysGetEUid(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.getegid:
                handled = true;
                return SysGetEGid(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.ioctl:
                handled = true;
                return SysIoctl(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.chroot:
                handled = true;
                return SysChroot(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.getppid:
                handled = true;
                return SysGetPPid(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.setreuid:
                handled = true;
                return SysSetReUid(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.setregid:
                handled = true;
                return SysSetReGid(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.munmap:
                handled = true;
                return SysMunmap(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.truncate:
                handled = true;
                return SysTruncate(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.ftruncate:
                handled = true;
                return SysFtruncate(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.truncate64:
                handled = true;
                return SysTruncate64(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.ftruncate64:
                handled = true;
                return SysFtruncate64(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.fchmod:
                handled = true;
                return SysFchmod(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.fchown:
                handled = true;
                return SysFchown(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.sigreturn:
                handled = true;
                return SysSigReturn(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.clone:
                handled = true;
                return SysClone(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.uname:
                handled = true;
                return SysUname(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.sysinfo:
                handled = true;
                return SysSysinfo(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.mprotect:
                handled = true;
                return SysMprotect(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.setfsuid:
                handled = true;
                return SysSetFsUid(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.setfsgid:
                handled = true;
                return SysSetFsGid(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.writev:
                handled = true;
                return SysWriteV(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.setresuid:
                handled = true;
                return SysSetResUid(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.getresuid:
                handled = true;
                return SysGetResUid16(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.setresgid:
                handled = true;
                return SysSetResGid(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.getresgid:
                handled = true;
                return SysGetResGid16(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.capget:
                handled = true;
                return SysCapget(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.capset:
                handled = true;
                return SysCapset(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.rt_sigaction:
                handled = true;
                return SysRtSigAction(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.rt_sigprocmask:
                handled = true;
                return SysRtSigProcMask(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.chown32:
                handled = true;
                return SysChown(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.getcwd:
                handled = true;
                return SysGetCwd(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.mmap2:
                handled = true;
                return SysMmap2(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.statfs:
                handled = true;
                return SysStatfs(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.fstatfs:
                handled = true;
                return SysFstatfs(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.stat64:
                handled = true;
                return SysStat64(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.lstat64:
                handled = true;
                return SysLstat64(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.fstat64:
                handled = true;
                return SysFstat64(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.lchown32:
                handled = true;
                return SysLchown(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.getgroups:
                handled = true;
                return SysGetGroups(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.setgroups:
                handled = true;
                return SysSetGroups(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.fchown32:
                handled = true;
                return SysFchown(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.getdents64:
                handled = true;
                return SysGetdents64(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.wait4:
                handled = true;
                return SysWait4(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.vfork:
                handled = true;
                return SysVfork(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.futex:
                handled = true;
                return SysFutex(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.sigpending:
                handled = true;
                return SysSigPending(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.set_thread_area:
                handled = true;
                return SysSetThreadArea(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.set_robust_list:
                handled = true;
                return SysSetRobustList(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.get_robust_list:
                handled = true;
                return SysGetRobustList(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.exit_group:
                handled = true;
                return SysExitGroup(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.set_tid_address:
                handled = true;
                return SysSetTidAddress(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.waitid:
                handled = true;
                return SysWaitId(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.fchownat:
                handled = true;
                return SysFchownAt(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.fchmodat:
                handled = true;
                return SysFchmodAt(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.faccessat:
                handled = true;
                return SysFaccessAt(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.statfs64:
                handled = true;
                return SysStatfs64(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.fstatfs64:
                handled = true;
                return SysFstatfs64(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.getrandom:
                handled = true;
                return SysGetRandom(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.creat:
                handled = true;
                return SysCreat(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.link:
                handled = true;
                return SysLink(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.linkat:
                handled = true;
                return SysLinkat(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.chdir:
                handled = true;
                return SysChdir(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.fchdir:
                handled = true;
                return SysFchdir(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.time:
                handled = true;
                return SysTime(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.times:
                handled = true;
                return SysTimes(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.nice:
                handled = true;
                return SysNice(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.getpriority:
                handled = true;
                return SysGetPriority(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.setpriority:
                handled = true;
                return SysSetPriority(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.personality:
                handled = true;
                return SysPersonality(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.prctl:
                handled = true;
                return SysPrctl(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.getcpu:
                handled = true;
                return SysGetCpu(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.prlimit64:
                handled = true;
                return SysPrlimit64(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.get_thread_area:
                handled = true;
                return SysGetThreadArea(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.openat:
                handled = true;
                return SysOpenAt(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.dup:
                handled = true;
                return SysDup(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.dup2:
                handled = true;
                return SysDup2(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.dup3:
                handled = true;
                return SysDup3(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.pread:
                handled = true;
                return SysPRead(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.pwrite:
                handled = true;
                return SysPWrite(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.readv:
                handled = true;
                return SysReadV(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.preadv:
                handled = true;
                return SysPReadV(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.pwritev:
                handled = true;
                return SysPWriteV(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.mkdir:
                handled = true;
                return SysMkdir(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.rmdir:
                handled = true;
                return SysRmdir(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.mkdirat:
                handled = true;
                return SysMkdirAt(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.mknodat:
                handled = true;
                return SysMknodat(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.unlinkat:
                handled = true;
                return SysUnlinkAt(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.utimes:
                handled = true;
                return SysUtimes(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.symlink:
                handled = true;
                return SysSymlink(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.symlinkat:
                handled = true;
                return SysSymlinkAt(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.readlink:
                handled = true;
                return SysReadlink(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.readlinkat:
                handled = true;
                return SysReadlinkAt(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.getdents:
                handled = true;
                return SysGetdents(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.setxattr:
                handled = true;
                return SysSetXAttr(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.lsetxattr:
                handled = true;
                return SysLSetXAttr(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.fsetxattr:
                handled = true;
                return SysFSetXAttr(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.getxattr:
                handled = true;
                return SysGetXAttr(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.lgetxattr:
                handled = true;
                return SysLGetXAttr(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.fgetxattr:
                handled = true;
                return SysFGetXAttr(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.listxattr:
                handled = true;
                return SysListXAttr(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.llistxattr:
                handled = true;
                return SysLListXAttr(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.flistxattr:
                handled = true;
                return SysFListXAttr(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.removexattr:
                handled = true;
                return SysRemoveXAttr(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.lremovexattr:
                handled = true;
                return SysLRemoveXAttr(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.fremovexattr:
                handled = true;
                return SysFRemoveXAttr(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.fstatat64:
                handled = true;
                return SysNewFstatAt(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.utimensat:
                handled = true;
                return SysUtimensAt(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.rename:
                handled = true;
                return SysRename(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.renameat:
                handled = true;
                return SysRenameAt(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.renameat2:
                handled = true;
                return SysRenameAt2(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.getuid32:
                handled = true;
                return SysGetUid32(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.getgid32:
                handled = true;
                return SysGetGid32(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.geteuid32:
                handled = true;
                return SysGetEUid32(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.getegid32:
                handled = true;
                return SysGetEGid32(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.setuid32:
                handled = true;
                return SysSetUid32(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.setgid32:
                handled = true;
                return SysSetGid32(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.setreuid32:
                handled = true;
                return SysSetReUid32(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.setregid32:
                handled = true;
                return SysSetReGid32(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.getgroups32:
                handled = true;
                return SysGetGroups32(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.setgroups32:
                handled = true;
                return SysSetGroups32(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.setresuid32:
                handled = true;
                return SysSetResUid32(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.getresuid32:
                handled = true;
                return SysGetResUid32(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.setresgid32:
                handled = true;
                return SysSetResGid32(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.getresgid32:
                handled = true;
                return SysGetResGid32(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.setfsuid32:
                handled = true;
                return SysSetFsUid32(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.setfsgid32:
                handled = true;
                return SysSetFsGid32(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.clock_gettime:
                handled = true;
                return SysClockGetTime(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.gettimeofday:
                handled = true;
                return SysGetTimeOfDay(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.nanosleep:
                handled = true;
                return SysNanosleep(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.rt_sigreturn:
                handled = true;
                return SysRtSigReturn(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.rt_sigpending:
                handled = true;
                return SysRtSigPending(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.rt_sigsuspend:
                handled = true;
                return SysRtSigSuspend(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.rt_sigtimedwait:
                handled = true;
                return SysRtSigTimedWait(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.rt_sigtimedwait_time64:
                handled = true;
                return SysRtSigTimedWaitTime64(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.gettid:
                handled = true;
                return SysGettid(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.sched_getaffinity:
                handled = true;
                return SysSchedGetAffinity(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.getpgid:
                handled = true;
                return SysGetPgid(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.setpgid:
                handled = true;
                return SysSetPgid(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.setsid:
                handled = true;
                return SysSetSid(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.getsid:
                handled = true;
                return SysGetSid(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.getpgrp:
                handled = true;
                return SysGetPgrp(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.umask:
                handled = true;
                return SysUmask(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.sethostname:
                handled = true;
                return SysSethostname(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.setdomainname:
                handled = true;
                return SysSetdomainname(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.sched_yield:
                handled = true;
                return SysSchedYield(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.pause:
                handled = true;
                return SysPause(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.socketcall:
                handled = true;
                return SysSocketCall(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.socket:
                handled = true;
                return SysSocket(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.bind:
                handled = true;
                return SysBind(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.connect:
                handled = true;
                return SysConnect(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.listen:
                handled = true;
                return SysListen(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.accept4:
                handled = true;
                return SysAccept4(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.getsockname:
                handled = true;
                return SysGetSockName(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.getpeername:
                handled = true;
                return SysGetPeerName(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.sendto:
                handled = true;
                return SysSendTo(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.recvfrom:
                handled = true;
                return SysRecvFrom(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.shutdown:
                handled = true;
                return SysShutdown(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.socketpair:
                handled = true;
                return SysSocketPair(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.sendmsg:
                handled = true;
                return SysSendMsg(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.recvmsg:
                handled = true;
                return SysRecvMsg(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.setsockopt:
                handled = true;
                return SysSetSockOpt(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.getsockopt:
                handled = true;
                return SysGetSockOpt(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.sendmmsg:
                handled = true;
                return SysSendMMsg(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.recvmmsg:
                handled = true;
                return SysRecvMMsg(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.alarm:
                handled = true;
                return SysAlarm(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.setitimer:
                handled = true;
                return SysSetitimer(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.getitimer:
                handled = true;
                return SysGetitimer(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.timer_create:
                handled = true;
                return SysTimerCreate(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.timer_settime:
                handled = true;
                return SysTimerSetTime32(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.timer_gettime:
                handled = true;
                return SysTimerGetTime32(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.timer_getoverrun:
                handled = true;
                return SysTimerGetOverrun(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.timer_delete:
                handled = true;
                return SysTimerDelete(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.clock_gettime64:
                handled = true;
                return SysClockGetTime64(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.clock_settime64:
                handled = true;
                return SysClockSetTime64(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.clock_adjtime64:
                handled = true;
                return SysClockAdjTime64(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.clock_getres_time64:
                handled = true;
                return SysClockGetResTime64(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.clock_nanosleep_time64:
                handled = true;
                return SysClockNanosleepTime64(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.timer_settime64:
                handled = true;
                return SysTimerSetTime64(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.timerfd_gettime64:
                handled = true;
                return SysTimerFdGetTime64(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.timerfd_settime64:
                handled = true;
                return SysTimerFdSetTime64(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.utimensat_time64:
                handled = true;
                return SysUtimensAtTime64(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.timerfd_create:
                handled = true;
                return SysTimerFdCreate(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.timerfd_settime:
                handled = true;
                return SysTimerFdSetTime(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.timerfd_gettime:
                handled = true;
                return SysTimerFdGetTime(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.eventfd:
                handled = true;
                return SysEventFd(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.eventfd2:
                handled = true;
                return SysEventFd2(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.signalfd:
                handled = true;
                return SysSignalFd(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.signalfd4:
                handled = true;
                return SysSignalFd4(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.fsync:
                handled = true;
                return SysFsync(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.fdatasync:
                handled = true;
                return SysFdatasync(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.sync:
                handled = true;
                return SysSync(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.madvise:
                handled = true;
                return SysMadvise(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.msync:
                handled = true;
                return SysMsync(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.mremap:
                handled = true;
                return SysMremap(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.membarrier:
                handled = true;
                return SysMembarrier(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.kill:
                handled = true;
                return SysKill(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.tkill:
                handled = true;
                return SysTkill(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.tgkill:
                handled = true;
                return SysTgkill(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.rt_sigqueueinfo:
                handled = true;
                return SysRtSigQueueInfo(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.rt_tgsigqueueinfo:
                handled = true;
                return SysRtTgSigQueueInfo(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.execve:
                handled = true;
                return SysExecve(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.select:
                handled = true;
                return SysSelect(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers._newselect:
                handled = true;
                return SysNewSelect(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.pselect6:
                handled = true;
                return SysPselect6(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.pselect6_time64:
                handled = true;
                return SysPselect6Time64(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.flock:
                handled = true;
                return SysFlock(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.poll:
                handled = true;
                return SysPoll(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.ppoll:
                handled = true;
                return SysPpoll(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.ppoll_time64:
                handled = true;
                return SysPpollTime64(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.pipe:
                handled = true;
                return SysPipe(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.pipe2:
                handled = true;
                return SysPipe2(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.sendfile:
                handled = true;
                return SysSendfile(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.sendfile64:
                handled = true;
                return SysSendfile64(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.splice:
                handled = true;
                return SysSplice(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.tee:
                handled = true;
                return SysTee(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.readahead:
                handled = true;
                return SysReadahead(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.fadvise64:
                handled = true;
                return SysFadvise64(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.fallocate:
                handled = true;
                return SysFallocate(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.memfd_create:
                handled = true;
                return SysMemfdCreate(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.epoll_create:
                handled = true;
                return SysEpollCreate(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.epoll_create1:
                handled = true;
                return SysEpollCreate1(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.epoll_ctl:
                handled = true;
                return SysEpollCtl(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.epoll_wait:
                handled = true;
                return SysEpollWait(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.epoll_pwait:
                handled = true;
                return SysEpollPwait(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.epoll_pwait2:
                handled = true;
                return SysEpollPwait2(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.faccessat2:
                handled = true;
                return SysFaccessAt2(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.fchmodat2:
                handled = true;
                return SysFchmodAt2(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.ipc:
                handled = true;
                return SysIpc(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.semget:
                handled = true;
                return SysSemGet(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.semctl:
                handled = true;
                return SysSemCtl(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.semop:
                handled = true;
                return SysSemOp(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.semtimedop_time64:
                handled = true;
                return SysSemTimedOpTime64(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.open_tree:
                handled = true;
                return SysOpenTree(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.move_mount:
                handled = true;
                return SysMoveMount(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.fsopen:
                handled = true;
                return SysFsopen(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.fsconfig:
                handled = true;
                return SysFsconfig(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.fsmount:
                handled = true;
                return SysFsmount(engine, ebx, ecx, edx, esi, edi, ebp);
            case X86SyscallNumbers.mount_setattr:
                handled = true;
                return SysMountSetattr(engine, ebx, ecx, edx, esi, edi, ebp);
            case 888:
                handled = true;
                return SysMagicDebug(engine, ebx, ecx, edx, esi, edi, ebp);
            default:
                handled = false;
                return new ValueTask<int>(0);
        }
    }
}