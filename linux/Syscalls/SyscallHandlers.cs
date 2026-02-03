using System.Runtime.InteropServices;
using System.Buffers.Binary;
using System.Text;
using System.Linq;
using Bifrost.Core;
using Bifrost.Native;
using Bifrost.Memory;
using Bifrost.VFS;
using Task = Bifrost.Core.Task;

namespace Bifrost.Syscalls;

public unsafe partial class SyscallManager
{
    private void RegisterHandlers()
    {
        Register(X86SyscallNumbers.exit, SysExit);
        Register(X86SyscallNumbers.fork, SysFork);
        Register(X86SyscallNumbers.waitpid, SysWaitPid);
        Register(X86SyscallNumbers.read, SysRead);
        Register(X86SyscallNumbers.pwritev2, SysPWriteV);
        Register(X86SyscallNumbers.statx, SysStatx);
        Register(X86SyscallNumbers.openat2, SysOpenAt2);
        Register(X86SyscallNumbers.write, SysWrite);
        Register(X86SyscallNumbers.open, SysOpen);
        Register(X86SyscallNumbers.close, SysClose);
        Register(X86SyscallNumbers.unlink, SysUnlink);
        Register(X86SyscallNumbers.chmod, SysChmod);
        Register(X86SyscallNumbers.chown, SysChown);
        Register(X86SyscallNumbers.lseek, SysLseek);
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
        Register(X86SyscallNumbers.gettimeofday, SysGetTimeOfDay);

        Register(X86SyscallNumbers.gettid, SysGettid);
        Register(X86SyscallNumbers.getpgid, SysGetpgid);
        Register(X86SyscallNumbers.umask, SysUmask);
        Register(X86SyscallNumbers.sethostname, SysSethostname);
        Register(X86SyscallNumbers.setdomainname, SysSetdomainname);
        Register(X86SyscallNumbers.sched_yield, SysSchedYield);
        Register(X86SyscallNumbers.pause, SysPause);

        Register(X86SyscallNumbers.fsync, SysFsync);
        Register(X86SyscallNumbers.fdatasync, SysFdatasync);
        Register(X86SyscallNumbers.sync, SysSync);
        Register(X86SyscallNumbers.madvise, SysMadvise);
        Register(X86SyscallNumbers.msync, SysMsync);
    }

    private static int SysCreat(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        // creat(path, mode) is open(path, O_CREAT|O_WRONLY|O_TRUNC, mode)
        return SysOpen(state, a1, (uint)(FileFlags.O_CREAT | FileFlags.O_WRONLY | FileFlags.O_TRUNC), a2, a4, a5, a6);
    }

    private static int SysLink(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        
        string oldPath = sm.ReadString(a1);
        string newPath = sm.ReadString(a2);
        
        var oldDentry = sm.PathWalk(oldPath);
        if (oldDentry == null || oldDentry.Inode == null) return -(int)Errno.ENOENT;
        if (oldDentry.Inode.Type == InodeType.Directory) return -(int)Errno.EPERM;

        int lastSlash = newPath.LastIndexOf('/');
        string dirPath = lastSlash == -1 ? "." : newPath.Substring(0, lastSlash);
        if (dirPath == "") dirPath = "/";
        string name = lastSlash == -1 ? newPath : newPath.Substring(lastSlash + 1);
        
        var dirDentry = sm.PathWalk(dirPath);
        if (dirDentry == null || dirDentry.Inode == null) return -(int)Errno.ENOENT;
        if (dirDentry.Inode.Type != InodeType.Directory) return -(int)Errno.ENOTDIR;
        
        try
        {
            var newDentry = new Dentry(name, null, dirDentry, dirDentry.SuperBlock);
            dirDentry.Inode.Link(newDentry, oldDentry.Inode);
            return 0;
        }
        catch (Exception)
        {
            return -(int)Errno.EIO;
        }
    }

    private static int SysChdir(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        
        string path = sm.ReadString(a1);
        var dentry = sm.PathWalk(path);
        if (dentry == null || dentry.Inode == null) return -(int)Errno.ENOENT;
        if (dentry.Inode.Type != InodeType.Directory) return -(int)Errno.ENOTDIR;
        
        var oldCwd = sm.CurrentWorkingDirectory;
        sm.CurrentWorkingDirectory = dentry;
        dentry.Inode!.Get();
        oldCwd?.Inode?.Put();
        return 0;
    }

    private static int SysTime(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        
        long t = DateTimeOffset.Now.ToUnixTimeSeconds();
        if (a1 != 0)
        {
            sm.Engine.MemWrite(a1, BitConverter.GetBytes((uint)t));
        }
        return (int)t;
    }

    private static int ImplOpen(SyscallManager sm, string path, uint flags, uint mode, Dentry? startAt = null)
    {
        var dentry = sm.PathWalk(path, startAt);
        if (dentry == null)
        {
            if ((flags & (uint)FileFlags.O_CREAT) != 0)
            {
                int lastSlash = path.LastIndexOf('/');
                string parentPath = lastSlash == -1 ? "" : (lastSlash == 0 ? "/" : path.Substring(0, lastSlash));
                string name = lastSlash == -1 ? path : path.Substring(lastSlash + 1);
                
                var parentDentry = sm.PathWalk(parentPath == "" ? "." : parentPath, startAt);
                if (parentDentry == null || parentDentry.Inode == null) return -(int)Errno.ENOENT;
                
                var t = Scheduler.GetByEngine(sm.Engine.State);
                int uid = t?.Process.EUID ?? 0;
                int gid = t?.Process.EGID ?? 0;
                
                try {
                    dentry = new Dentry(name, null, parentDentry, parentDentry.SuperBlock);
                    int finalMode = (int)mode & ~(t?.Process.Umask ?? 0);
                    parentDentry.Inode.Create(dentry, finalMode, uid, gid);
                } catch {
                    return -(int)Errno.EACCES;
                }
            }
            else
            {
                return -(int)Errno.ENOENT;
            }
        }
        else
        {
            // File exists - check for O_EXCL
            if ((flags & (uint)FileFlags.O_CREAT) != 0 && (flags & (uint)FileFlags.O_EXCL) != 0)
            {
                return -(int)Errno.EEXIST;
            }
        }
        
        try {
            var f = new Bifrost.VFS.File(dentry, (FileFlags)flags);
            return sm.AllocFD(f);
        } catch {
            return -1;
        }
    }

    private static int SysOpen(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -1;
        return ImplOpen(sm, sm.ReadString(a1), a2, a3);
    }

    private static int SysOpenAt(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        
        int dirfd = (int)a1;
        string path = sm.ReadString(a2);
        uint flags = a3;
        uint mode = a4;

        Dentry? startAt = null;
        if (dirfd != -100 && !path.StartsWith("/"))
        {
            var fdir = sm.GetFD(dirfd);
            if (fdir == null) return -(int)Errno.EBADF;
            startAt = fdir.Dentry;
        }

        return ImplOpen(sm, path, flags, mode, startAt);
    }

    private static int SysOpenAt2(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        int dirfd = (int)a1;
        string path = sm.ReadString(a2);
        uint howPtr = a3;
        uint howSize = a4;

        if (howSize < 24) return -(int)Errno.EINVAL;

        byte[] howBuf = sm.Engine.MemRead(howPtr, 24);
        ulong flags = BinaryPrimitives.ReadUInt64LittleEndian(howBuf.AsSpan(0, 8));
        ulong mode = BinaryPrimitives.ReadUInt64LittleEndian(howBuf.AsSpan(8, 8));

        Dentry? startAt = null;
        if (dirfd != -100 && !path.StartsWith("/"))
        {
            var fdir = sm.GetFD(dirfd);
            if (fdir == null) return -(int)Errno.EBADF;
            startAt = fdir.Dentry;
        }

        return ImplOpen(sm, path, (uint)flags, (uint)mode, startAt);
    }

    private static int SysDup(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        
        int oldfd = (int)a1;
        var f = sm.GetFD(oldfd);
        if (f == null) return -(int)Errno.EBADF;
        
        return sm.AllocFD(f);
    }

    private static int SysDup2(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        
        int oldfd = (int)a1;
        int newfd = (int)a2;
        
        if (oldfd == newfd)
        {
            if (sm.GetFD(oldfd) == null) return -(int)Errno.EBADF;
            return newfd;
        }

        var f = sm.GetFD(oldfd);
        if (f == null) return -(int)Errno.EBADF;
        
        sm.FreeFD(newfd);
        sm.FDs[newfd] = f;
        f.Dentry.Inode?.Get();
        return newfd;
    }

    private static int SysDup3(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        // For now ignore flags like O_CLOEXEC
        return SysDup2(state, a1, a2, a3, a4, a5, a6);
    }

    private static int SysPRead(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        int fd = (int)a1;
        uint bufAddr = a2;
        uint count = a3;
        long offset = (long)a4 | ((long)a5 << 32);

        var f = sm.GetFD(fd);
        if (f == null) return -(int)Errno.EBADF;
        
        try {
            byte[] buf = new byte[count];
            int n = f.Dentry.Inode!.Read(f, buf.AsSpan(), offset);
            if (n > 0) sm.Engine.MemWrite(bufAddr, buf.AsSpan(0, n));
            return n;
        } catch { return -(int)Errno.EIO; }
    }

    private static int SysPWrite(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        int fd = (int)a1;
        uint bufAddr = a2;
        uint count = a3;
        long offset = (long)a4 | ((long)a5 << 32);

        var data = sm.Engine.MemRead(bufAddr, count);
        var f = sm.GetFD(fd);
        if (f == null) return -(int)Errno.EBADF;
        
        try {
            int n = f.Dentry.Inode!.Write(f, data, offset);
            return n;
        } catch { return -(int)Errno.EIO; }
    }

    private static int SysReadV(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        int fd = (int)a1;
        uint iovAddr = a2;
        int iovCnt = (int)a3;
        
        var f = sm.GetFD(fd);
        if (f == null) return -(int)Errno.EBADF;

        int totalRead = 0;
        for(int i=0; i<iovCnt; i++)
        {
            var baseBytes = sm.Engine.MemRead(iovAddr + (uint)i*8, 4);
            var lenBytes = sm.Engine.MemRead(iovAddr + (uint)i*8 + 4, 4);
            uint baseAddr = BinaryPrimitives.ReadUInt32LittleEndian(baseBytes);
            uint len = BinaryPrimitives.ReadUInt32LittleEndian(lenBytes);
            
            if (len > 0)
            {
                byte[] buf = new byte[len];
                int n = f.Read(buf);
                if (n > 0)
                {
                    sm.Engine.MemWrite(baseAddr, buf.AsSpan(0, n));
                    totalRead += n;
                    if (n < (int)len) break; // EOF or short read
                }
                else break;
            }
        }
        return totalRead;
    }

    private static int SysPReadV(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        int fd = (int)a1;
        uint iovAddr = a2;
        int iovCnt = (int)a3;
        long offset = (long)a4 | ((long)a5 << 32); // Modified line
        
        var f = sm.GetFD(fd);
        if (f == null) return -(int)Errno.EBADF;

        int totalRead = 0;
        for(int i=0; i<iovCnt; i++)
        {
            var baseBytes = sm.Engine.MemRead(iovAddr + (uint)i*8, 4);
            var lenBytes = sm.Engine.MemRead(iovAddr + (uint)i*8 + 4, 4);
            uint baseAddr = BinaryPrimitives.ReadUInt32LittleEndian(baseBytes);
            uint len = BinaryPrimitives.ReadUInt32LittleEndian(lenBytes);
            
            if (len > 0)
            {
                byte[] buf = new byte[len];
                int n = f.Dentry.Inode!.Read(f, buf, offset + totalRead);
                if (n > 0)
                {
                    sm.Engine.MemWrite(baseAddr, buf.AsSpan(0, n));
                    totalRead += n;
                    if (n < (int)len) break;
                }
                else break;
            }
        }
        return totalRead;
    }

    private static int SysPWriteV(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        int fd = (int)a1;
        uint iovAddr = a2;
        int iovCnt = (int)a3;
        long offset = (long)a4 | ((long)a5 << 32); // Modified line

        var f = sm.GetFD(fd);
        if (f == null) return -(int)Errno.EBADF;

        int totalWritten = 0;
        for(int i=0; i<iovCnt; i++)
        {
            var baseBytes = sm.Engine.MemRead(iovAddr + (uint)i*8, 4);
            var lenBytes = sm.Engine.MemRead(iovAddr + (uint)i*8 + 4, 4);
            uint baseAddr = BinaryPrimitives.ReadUInt32LittleEndian(baseBytes);
            uint len = BinaryPrimitives.ReadUInt32LittleEndian(lenBytes);
            
            if (len > 0)
            {
                var data = sm.Engine.MemRead(baseAddr, len);
                int n = f.Dentry.Inode!.Write(f, data, offset + totalWritten);
                if (n > 0)
                {
                    totalWritten += n;
                    if (n < (int)len) break;
                }
                else break;
            }
        }
        return totalWritten;
    }

    private static int SysMkdir(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        string path = sm.ReadString(a1);
        uint mode = a2;

        int lastSlash = path.LastIndexOf('/');
        string parentPath = lastSlash == -1 ? "." : (lastSlash == 0 ? "/" : path.Substring(0, lastSlash));
        string name = lastSlash == -1 ? path : path.Substring(lastSlash + 1);

        var parentDentry = sm.PathWalk(parentPath);
        if (parentDentry == null || parentDentry.Inode == null) return -(int)Errno.ENOENT;

        var t = Scheduler.GetByEngine(sm.Engine.State);
        int uid = t?.Process.EUID ?? 0;
        int gid = t?.Process.EGID ?? 0;

        try {
            var dentry = new Dentry(name, null, parentDentry, parentDentry.SuperBlock);
            parentDentry.Inode.Mkdir(dentry, (int)mode, uid, gid);
            return 0;
        } catch { return -(int)Errno.EACCES; }
    }

    private static int SysRmdir(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        string path = sm.ReadString(a1);

        int lastSlash = path.LastIndexOf('/');
        string parentPath = lastSlash == -1 ? "." : (lastSlash == 0 ? "/" : path.Substring(0, lastSlash));
        string name = lastSlash == -1 ? path : path.Substring(lastSlash + 1);

        var parentDentry = sm.PathWalk(parentPath);
        if (parentDentry == null || parentDentry.Inode == null) return -(int)Errno.ENOENT;
        
        // Check if directory exists and is empty
        var targetDentry = sm.PathWalk(path);
        if (targetDentry == null || targetDentry.Inode == null) return -(int)Errno.ENOENT;
        if (targetDentry.Inode.Type != InodeType.Directory) return -(int)Errno.ENOTDIR;
        
        // Check if empty (only . and .. entries)
        var entries = targetDentry.Inode.GetEntries();
        if (entries.Count > 2) return -(int)Errno.ENOTEMPTY;  // Has more than . and ..

        try {
            parentDentry.Inode!.Rmdir(name);
            parentDentry.Children.Remove(name);
            return 0;
        } catch { return -(int)Errno.EACCES; }
    }

    private static int SysMkdirAt(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        int dirfd = (int)a1;
        string path = sm.ReadString(a2);
        uint mode = a3;

        Dentry? startAt = null;
        if (dirfd != -100 && !path.StartsWith("/"))
        {
            var fdir = sm.GetFD(dirfd);
            if (fdir == null) return -(int)Errno.EBADF;
            startAt = fdir.Dentry;
        }

        int lastSlash = path.LastIndexOf('/');
        string parentPath = lastSlash == -1 ? "" : path.Substring(0, lastSlash);
        string name = lastSlash == -1 ? path : path.Substring(lastSlash + 1);

        var parentDentry = sm.PathWalk(parentPath == "" ? "." : parentPath, startAt);
        if (parentDentry == null || parentDentry.Inode == null) return -(int)Errno.ENOENT;

        var t = Scheduler.GetByEngine(sm.Engine.State);
        int uid = t?.Process.EUID ?? 0;
        int gid = t?.Process.EGID ?? 0;

        try {
            var dentry = new Dentry(name, null, parentDentry, parentDentry.SuperBlock);
            parentDentry.Inode.Mkdir(dentry, (int)mode, uid, gid);
            return 0;
        } catch { return -(int)Errno.EACCES; }
    }

    private static int SysUnlinkAt(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        int dirfd = (int)a1;
        string path = sm.ReadString(a2);
        uint flags = a3;

        Dentry? startAt = null;
        if (dirfd != -100 && !path.StartsWith("/"))
        {
            var fdir = sm.GetFD(dirfd);
            if (fdir == null) return -(int)Errno.EBADF;
            startAt = fdir.Dentry;
        }

        int lastSlash = path.LastIndexOf('/');
        string parentPath = lastSlash == -1 ? "" : path.Substring(0, lastSlash);
        string name = lastSlash == -1 ? path : path.Substring(lastSlash + 1);

        var parentDentry = sm.PathWalk(parentPath == "" ? "." : parentPath, startAt);
        if (parentDentry == null || parentDentry.Inode == null) return -(int)Errno.ENOENT;

        if ((flags & 0x200) != 0) // AT_REMOVEDIR
        {
            try {
                parentDentry.Inode.Rmdir(name);
                parentDentry.Children.Remove(name);
                return 0;
            } catch { return -(int)Errno.EACCES; }
        }
        else
        {
            try {
                parentDentry.Inode.Unlink(name);
                parentDentry.Children.Remove(name);
                return 0;
            } catch { return -(int)Errno.ENOENT; }
        }
    }


    private static int SysGetdents(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        int fd = (int)a1;
        uint bufAddr = a2;
        int count = (int)a3;

        var f = sm.GetFD(fd);
        if (f == null || f.Dentry.Inode == null) return -(int)Errno.EBADF;
        if (f.Dentry.Inode.Type != InodeType.Directory) return -(int)Errno.ENOTDIR;

        try {
            var entries = f.Dentry.Inode.GetEntries();
            int startIdx = (int)f.Position;
            if (startIdx >= entries.Count) return 0;

            int writeOffset = 0;
            for (int i = startIdx; i < entries.Count; i++)
            {
                var entry = entries[i];
                byte[] nameBytes = Encoding.UTF8.GetBytes(entry.Name);
                int nameLen = nameBytes.Length + 1;
                // reclen: ino(4) + off(4) + reclen(2) + name + pad + type(1)
                // We align to 4 bytes for 32-bit
                int reclen = (4 + 4 + 2 + nameLen + 1 + 3) & ~3;

                if (writeOffset + reclen > count) break;

                uint baseAddr = bufAddr + (uint)writeOffset;
                byte[] buf = new byte[reclen];
                
                BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0), (uint)entry.Ino);
                BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4), (uint)(i + 1)); // d_off
                BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(8), (ushort)reclen);
                
                Array.Copy(nameBytes, 0, buf, 10, nameBytes.Length);
                buf[10 + nameBytes.Length] = 0; // null terminator
                
                byte dType = entry.Type switch {
                    InodeType.Directory => (byte)4,
                    InodeType.File => (byte)8,
                    InodeType.Symlink => (byte)10,
                    InodeType.CharDev => (byte)2,
                    InodeType.BlockDev => (byte)6,
                    InodeType.Fifo => (byte)1,
                    InodeType.Socket => (byte)12,
                    _ => (byte)0
                };
                buf[reclen - 1] = dType;

                sm.Engine.MemWrite(baseAddr, buf);
                writeOffset += reclen;
                f.Position = i + 1;
            }
            return writeOffset;
        } catch { return -(int)Errno.EIO; }
    }

    private static int SysNewFstatAt(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        int dirfd = (int)a1;
        string path = sm.ReadString(a2);
        uint statAddr = a3;
        uint flags = a4;

        if (path == "" && (flags & 0x1000) != 0) // AT_EMPTY_PATH
        {
            return SysFstat64(state, a1, a3, 0, 0, 0, 0);
        }

        Dentry? startAt = null;
        if (dirfd != -100 && !path.StartsWith("/"))
        {
            var fdir = sm.GetFD(dirfd);
            if (fdir == null) return -(int)Errno.EBADF;
            startAt = fdir.Dentry;
        }

        var dentry = sm.PathWalk(path, startAt);
        if (dentry == null || dentry.Inode == null) return -(int)Errno.ENOENT;
        
        WriteStat64(sm, statAddr, dentry.Inode);
        return 0;
    }

    private static int SysUtimensAt(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        int dirfd = (int)a1;
        string path = sm.ReadString(a2);
        uint timesAddr = a3;
        uint flags = a4;

        var dentry = sm.PathWalk(path);
        if (dentry == null || dentry.Inode == null) return -(int)Errno.ENOENT;

        if (timesAddr == 0)
        {
            dentry.Inode.ATime = dentry.Inode.MTime = DateTime.Now;
        }
        else
        {
            // TODO: Read timespec from memory
            dentry.Inode.ATime = dentry.Inode.MTime = DateTime.Now;
        }
        return 0;
    }

    private static int SysFchownAt(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        int dirfd = (int)a1;
        string path = sm.ReadString(a2);
        int uid = (int)a3;
        int gid = (int)a4;
        uint flags = a5;

        Dentry? startAt = null;
        if (dirfd != -100 && !path.StartsWith("/"))
        {
            var fdir = sm.GetFD(dirfd);
            if (fdir == null) return -(int)Errno.EBADF;
            startAt = fdir.Dentry;
        }

        var dentry = sm.PathWalk(path, startAt);
        if (dentry == null || dentry.Inode == null) return -(int)Errno.ENOENT;

        // Redirect to SysChown logic or similar
        return SysChown(state, a2, a3, a4, 0, 0, 0); // Simplified: should use dentry directly
    }

    private static int SysFchmodAt(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        int dirfd = (int)a1;
        string path = sm.ReadString(a2);
        uint mode = a3;

        Dentry? startAt = null;
        if (dirfd != -100 && !path.StartsWith("/"))
        {
            var fdir = sm.GetFD(dirfd);
            if (fdir == null) return -(int)Errno.EBADF;
            startAt = fdir.Dentry;
        }

        var dentry = sm.PathWalk(path, startAt);
        if (dentry == null || dentry.Inode == null) return -(int)Errno.ENOENT;

        return SysChmod(state, a2, a3, 0, 0, 0, 0);
    }

    private static int SysFaccessAt(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        int dirfd = (int)a1;
        string path = sm.ReadString(a2);
        uint mode = a3;

        Dentry? startAt = null;
        if (dirfd != -100 && !path.StartsWith("/"))
        {
            var fdir = sm.GetFD(dirfd);
            if (fdir == null) return -(int)Errno.EBADF;
            startAt = fdir.Dentry;
        }

        var dentry = sm.PathWalk(path, startAt);
        if (dentry == null || dentry.Inode == null) return -(int)Errno.ENOENT;

        return SysAccess(state, a2, a3, 0, 0, 0, 0);
    }

    private static int SysRename(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        string oldPath = sm.ReadString(a1);
        string newPath = sm.ReadString(a2);

        return ImplRename(sm, -100, oldPath, -100, newPath, 0);
    }

    private static int SysRenameAt(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        return ImplRename(sm, (int)a1, sm.ReadString(a2), (int)a3, sm.ReadString(a4), 0);
    }

    private static int SysRenameAt2(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        return ImplRename(sm, (int)a1, sm.ReadString(a2), (int)a3, sm.ReadString(a4), a5);
    }

    private static int ImplRename(SyscallManager sm, int oldDirFd, string oldPath, int newDirFd, string newPath, uint flags)
    {
        Dentry? oldStart = null;
        if (oldDirFd != -100 && !oldPath.StartsWith("/"))
        {
            var f = sm.GetFD(oldDirFd);
            if (f == null) return -(int)Errno.EBADF;
            oldStart = f.Dentry;
        }

        Dentry? newStart = null;
        if (newDirFd != -100 && !newPath.StartsWith("/"))
        {
            var f = sm.GetFD(newDirFd);
            if (f == null) return -(int)Errno.EBADF;
            newStart = f.Dentry;
        }

        int lastSlashOld = oldPath.LastIndexOf('/');
        string oldParentPath = lastSlashOld == -1 ? "" : oldPath.Substring(0, lastSlashOld);
        string oldName = lastSlashOld == -1 ? oldPath : oldPath.Substring(lastSlashOld + 1);

        int lastSlashNew = newPath.LastIndexOf('/');
        string newParentPath = lastSlashNew == -1 ? "" : newPath.Substring(0, lastSlashNew);
        string newName = lastSlashNew == -1 ? newPath : newPath.Substring(lastSlashNew + 1);

        var oldParentDentry = sm.PathWalk(oldParentPath == "" ? "." : oldParentPath, oldStart);
        var newParentDentry = sm.PathWalk(newParentPath == "" ? "." : newParentPath, newStart);

        if (oldParentDentry == null || newParentDentry == null) return -(int)Errno.ENOENT;

        try {
            oldParentDentry.Inode!.Rename(oldName, newParentDentry.Inode!, newName);
            return 0;
        } catch { return -(int)Errno.EACCES; }
    }

    private static int SysStat(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        string path = sm.ReadString(a1);
        var dentry = sm.PathWalk(path);
        if (dentry == null || dentry.Inode == null) return -(int)Errno.ENOENT;
        
        WriteStat(sm, a2, dentry.Inode);
        return 0;
    }

    private static int SysLstat(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        string path = sm.ReadString(a1);
        var dentry = sm.PathWalk(path, followLink: false);
        if (dentry == null || dentry.Inode == null) return -(int)Errno.ENOENT;
        
        WriteStat(sm, a2, dentry.Inode);
        return 0;
    }

    private static int SysFstat(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        int fd = (int)a1;
        var f = sm.GetFD(fd);
        if (f == null || f.Dentry.Inode == null) return -(int)Errno.EBADF;
        
        WriteStat(sm, a2, f.Dentry.Inode);
        return 0;
    }

    private static int SysStatx(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        
        int dirfd = (int)a1;
        string path = sm.ReadString(a2);
        uint flags = a3;
        uint mask = a4;
        uint statxAddr = a5;

        if (path == "" && (flags & LinuxConstants.AT_EMPTY_PATH) != 0)
        {
            var f = sm.GetFD(dirfd);
            if (f == null || f.Dentry.Inode == null) return -(int)Errno.EBADF;
            WriteStatx(sm, statxAddr, f.Dentry.Inode, mask);
            return 0;
        }

        Dentry? startAt = null;
        if (dirfd != unchecked((int)LinuxConstants.AT_FDCWD) && !path.StartsWith("/"))
        {
            var fdir = sm.GetFD(dirfd);
            if (fdir == null) return -(int)Errno.EBADF;
            startAt = fdir.Dentry;
        }

        bool followLink = (flags & LinuxConstants.AT_SYMLINK_NOFOLLOW) == 0;
        var dentry = sm.PathWalk(path, startAt, followLink);
        if (dentry == null || dentry.Inode == null) return -(int)Errno.ENOENT;
        
        WriteStatx(sm, statxAddr, dentry.Inode, mask);
        return 0;
    }

    private static int SysExit(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        
        var task = Scheduler.GetByEngine(state);
        if (task != null)
        {
            
            task.ExitCode = (int)a1;
            task.Exited = true;
            
            // CRITICAL: Set process to zombie state BEFORE signaling events
            // to avoid race condition where parent wakes up before state is set
            lock (task.Process)
            {
                task.Process.State = ProcessState.Zombie;
                task.Process.ExitStatus = (int)a1;
                
                // Signal zombie event for waitpid
                task.Process.ZombieEvent.Set();
            }
            
            // Wake vfork parent if exists
            if (task.VforkParent != null)
            {
                // Vfork parent is waiting, signal it to continue
                task.VforkParent = null;
            }
            
            // Signal after state is set
            task.WaitEvent.Set();
        }
        
        int code = (int)a1;
        sm.ExitHandler?.Invoke(sm.Engine, code, false);
        sm.Engine.Stop();
        return 0;
    }

    private static int SysExitGroup(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        int code = (int)a1;
        sm.ExitHandler?.Invoke(sm.Engine, code, true);
        sm.Engine.Stop();
        return 0;
    }

    private static int SysRead(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -1;
        int fd = (int)a1;
        uint bufAddr = a2;
        uint count = a3;

        try {
            var f = sm.GetFD(fd);
            if (f != null) {
                byte[] buf = new byte[count];
                int n = f.Read(buf.AsSpan(0, (int)count));
                if (n > 0) sm.Engine.MemWrite(bufAddr, buf.AsSpan(0, n));
                return n;
            }
        } catch { return -(int)Errno.EIO; }

        return -(int)Errno.EBADF;
    }

    private static int SysWrite(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -1;
        int fd = (int)a1;
        uint bufAddr = a2;
        uint count = a3;

        var data = sm.Engine.MemRead(bufAddr, count);

        var f = sm.GetFD(fd);
        if (f != null) {
            try {
                int n = f.Write(data);
                return n;
            } catch { return -(int)Errno.EIO; }
        }

        return -(int)Errno.EBADF;
    }



    private static int SysLseek(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -1;
        int fd = (int)a1;
        long offset = (long)(int)a2;  // signed offset
        int whence = (int)a3;
        
        var f = sm.GetFD(fd);
        if (f == null) return -(int)Errno.EBADF;
        
        long newPos = whence switch
        {
            0 => offset, // SEEK_SET
            1 => f.Position + offset, // SEEK_CUR
            2 => (long)f.Dentry.Inode!.Size + offset, // SEEK_END
            _ => -1
        };
        
        if (newPos < 0) return -(int)Errno.EINVAL;
        f.Position = newPos;
        return (int)newPos;
    }

    private static int SysClose(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -1;
        sm.FreeFD((int)a1);
        return 0;
    }

    private static int SysBrk(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -1;
        uint newBrk = a1;
        if (newBrk == 0) return (int)sm.BrkAddr;

        if (newBrk > sm.BrkAddr) {
            uint start = (sm.BrkAddr + 0xFFF) & ~0xFFFu;
            uint end = (newBrk + 0xFFF) & ~0xFFFu;
            if (end > start)
            {
                // Map anonymous
                sm.Mem.Mmap(start, end - start, Protection.Read | Protection.Write, MapFlags.Private | MapFlags.Anonymous, null, 0, 0, "HEAP", sm.Engine);
            }
            sm.BrkAddr = newBrk;
        }
        return (int)sm.BrkAddr;
    }
    
    private static int SysClone(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.ENOSYS;
        
        if (sm.CloneHandler == null) return -(int)Errno.ENOSYS;
        
        var (tid, err) = sm.CloneHandler((int)a1, a2, a3, a4, a5);
        if (err != null) return -(int)Errno.EAGAIN;
        
        if (((int)a1 & (int)LinuxConstants.CLONE_VFORK) != 0)
        {
            // Parent should be suspended until child exits
            var child = Scheduler.Get(tid);
            var task = Scheduler.GetByEngine(state);
            if (child != null && task != null)
            {
                // Suspend parent using BlockingTask pattern
                var tcs = new TaskCompletionSource<int>();
                task.BlockingTask = tcs.Task;
                
                // Spawn task to wait for child and wake parent
                _ = System.Threading.Tasks.Task.Run(() =>
                {
                    child.WaitEvent.Wait();
                    tcs.SetResult(0);
                });
                
                sm.Engine.Yield();
            }
        }
        
        return tid;
    }
    
    private static int SysFork(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        // fork = clone(0, 0, NULL, NULL, NULL) - no flags, copy everything
        return SysClone(state, 0, 0, 0, 0, 0, 0);
    }
    
    private static int SysVfork(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        // vfork = clone(CLONE_VM | CLONE_VFORK, 0, NULL, NULL, NULL)
        return SysClone(state, LinuxConstants.CLONE_VM | LinuxConstants.CLONE_VFORK, 0, 0, 0, 0, 0);
    }
    
    private static int SysFutex(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.ENOSYS;
        
        uint uaddr = a1;
        int op = (int)a2;
        uint val = a3;
        
        int opCode = op & 0x7F;
        
        if (opCode == 0) // WAIT
        {
            var buf = sm.Engine.MemRead(uaddr, 4);
            uint currentVal = BinaryPrimitives.ReadUInt32LittleEndian(buf);
            if (currentVal != val) return -(int)Errno.EAGAIN; // EWOULDBLOCK
            
            var waiter = sm.Futex.PrepareWait(uaddr);
            
            // Non-blocking: set the Task to await and yield the engine
            var task = Scheduler.GetByEngine(state);
            if (task != null) 
            {
                // Use wrapper to avoid CS4004 (await in unsafe context)
                task.BlockingTask = SyscallAsyncWrappers.WaitFutexAsync(waiter.Tcs.Task);
            }
            sm.Engine.Yield();
            
            return 0;
        }
        else if (opCode == 1) // WAKE
        {
            int count = (int)val;
            return sm.Futex.Wake(uaddr, count);
        }
        
        return -(int)Errno.ENOSYS;
    }
    
    private static int SysSetThreadArea(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        
        uint uInfoAddr = a1;
        var buf = sm.Engine.MemRead(uInfoAddr, 16); 
        
        uint entry = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(0, 4));
        uint baseAddr = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(4, 4));
        
        sm.Engine.SetSegBase(Seg.GS, baseAddr);
        
        if (entry == 0xFFFFFFFF) 
        {
             BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0, 4), 12);
             sm.Engine.MemWrite(uInfoAddr, buf.AsSpan(0, 4));
        }
        
        return 0;
    }
    
    private static int SysSetTidAddress(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        if (sm.GetTID != null) return sm.GetTID(sm.Engine);
        return 1;
    }
    
    private static int SysUname(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var t = Scheduler.GetByEngine(state);
        if (t == null) return -(int)Errno.EPERM;

        var uts = t.Process.UTS;
        
        void WriteUnameString(uint addr, string s)
        {
            byte[] buf = new byte[65];
            var bytes = System.Text.Encoding.ASCII.GetBytes(s);
            Array.Copy(bytes, buf, Math.Min(bytes.Length, 64));
            sm.Engine.MemWrite(addr, buf);
        }

        WriteUnameString(a1, uts.SysName);
        WriteUnameString(a1 + 65, uts.NodeName);
        WriteUnameString(a1 + 130, uts.Release);
        WriteUnameString(a1 + 195, uts.Version);
        WriteUnameString(a1 + 260, uts.Machine);
        WriteUnameString(a1 + 325, uts.DomainName);
        
        return 0;
    }

    private static int SysSignal(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        return 0;
    }

    private static int SysGetUid(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var t = Scheduler.GetByEngine(state);
        return t?.Process.UID ?? 0;
    }

    private static int SysGetEUid(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var t = Scheduler.GetByEngine(state);
        return t?.Process.EUID ?? 0;
    }

    private static int SysGetGid(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var t = Scheduler.GetByEngine(state);
        return t?.Process.GID ?? 0;
    }

    private static int SysGetEGid(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var t = Scheduler.GetByEngine(state);
        return t?.Process.EGID ?? 0;
    }

    private static int SysSetUid(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var t = Scheduler.GetByEngine(state);
        if (t != null)
        {
            t.Process.UID = t.Process.EUID = t.Process.SUID = t.Process.FSUID = (int)a1;
        }
        return 0;
    }

    private static int SysSetGid(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var t = Scheduler.GetByEngine(state);
        if (t != null)
        {
            t.Process.GID = t.Process.EGID = t.Process.SGID = t.Process.FSGID = (int)a1;
        }
        return 0;
    }

    private static int SysGetUid32(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6) => SysGetUid(state, a1, a2, a3, a4, a5, a6);
    private static int SysGetGid32(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6) => SysGetGid(state, a1, a2, a3, a4, a5, a6);
    private static int SysGetEUid32(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6) => SysGetEUid(state, a1, a2, a3, a4, a5, a6);
    private static int SysGetEGid32(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6) => SysGetEGid(state, a1, a2, a3, a4, a5, a6);
    private static int SysSetUid32(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6) => SysSetUid(state, a1, a2, a3, a4, a5, a6);
    private static int SysSetGid32(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6) => SysSetGid(state, a1, a2, a3, a4, a5, a6);

    private static int SysSetReUid(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var t = Scheduler.GetByEngine(state);
        if (t != null)
        {
            if (a1 != 0xFFFFFFFF) t.Process.UID = (int)a1;
            if (a2 != 0xFFFFFFFF) t.Process.EUID = (int)a2;
            t.Process.SUID = t.Process.EUID;
            t.Process.FSUID = t.Process.EUID;
        }
        return 0;
    }

    private static int SysSetReGid(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var t = Scheduler.GetByEngine(state);
        if (t != null)
        {
            if (a1 != 0xFFFFFFFF) t.Process.GID = (int)a1;
            if (a2 != 0xFFFFFFFF) t.Process.EGID = (int)a2;
            t.Process.SGID = t.Process.EGID;
            t.Process.FSGID = t.Process.EGID;
        }
        return 0;
    }

    private static int SysSetResUid(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var t = Scheduler.GetByEngine(state);
        if (t != null)
        {
            if (a1 != 0xFFFFFFFF) t.Process.UID = (int)a1;
            if (a2 != 0xFFFFFFFF) t.Process.EUID = (int)a2;
            if (a3 != 0xFFFFFFFF) t.Process.SUID = (int)a3;
            t.Process.FSUID = t.Process.EUID;
        }
        return 0;
    }

    private static int SysGetResUid(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        var t = Scheduler.GetByEngine(state);
        if (t != null && sm != null)
        {
            sm.Engine.MemWrite(a1, BitConverter.GetBytes(t.Process.UID));
            sm.Engine.MemWrite(a2, BitConverter.GetBytes(t.Process.EUID));
            sm.Engine.MemWrite(a3, BitConverter.GetBytes(t.Process.SUID));
        }
        return 0;
    }

    private static int SysSetResGid(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var t = Scheduler.GetByEngine(state);
        if (t != null)
        {
            if (a1 != 0xFFFFFFFF) t.Process.GID = (int)a1;
            if (a2 != 0xFFFFFFFF) t.Process.EGID = (int)a2;
            if (a3 != 0xFFFFFFFF) t.Process.SGID = (int)a3;
            t.Process.FSGID = t.Process.EGID;
        }
        return 0;
    }

    private static int SysGetResGid(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        var t = Scheduler.GetByEngine(state);
        if (t != null && sm != null)
        {
            sm.Engine.MemWrite(a1, BitConverter.GetBytes(t.Process.GID));
            sm.Engine.MemWrite(a2, BitConverter.GetBytes(t.Process.EGID));
            sm.Engine.MemWrite(a3, BitConverter.GetBytes(t.Process.SGID));
        }
        return 0;
    }

    private static int SysSetFsUid(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var t = Scheduler.GetByEngine(state);
        if (t != null)
        {
            int old = t.Process.FSUID;
            t.Process.FSUID = (int)a1;
            return old;
        }
        return 0;
    }

    private static int SysSetFsGid(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var t = Scheduler.GetByEngine(state);
        if (t != null)
        {
            int old = t.Process.FSGID;
            t.Process.FSGID = (int)a1;
            return old;
        }
        return 0;
    }

    private static int SysChmod(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        
        string path = sm.ReadString(a1);
        uint mode = a2;
        
        var dentry = sm.PathWalk(path);
        if (dentry == null || dentry.Inode == null) return -2; // ENOENT
        
        // Permission check: only owner or root can chmod
        var t = Scheduler.GetByEngine(sm.Engine.State);
        if (t != null && t.Process.EUID != 0 && t.Process.EUID != dentry.Inode.Uid)
            return -(int)Errno.EPERM;
        
        // Only modify permission bits (lower 12 bits: rwx for user/group/other + special bits)
        dentry.Inode.Mode = (int)(mode & 0xFFF);
        dentry.Inode.CTime = DateTime.Now;
        return 0;
    }
    private static int SysFchmod(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        
        int fd = (int)a1;
        uint mode = a2;
        
        var f = sm.GetFD(fd);
        if (f == null || f.Dentry.Inode == null) return -9; // EBADF
        
        // Permission check
        var t = Scheduler.GetByEngine(sm.Engine.State);
        if (t != null && t.Process.EUID != 0 && t.Process.EUID != f.Dentry.Inode.Uid)
            return -(int)Errno.EPERM;
        
        f.Dentry.Inode.Mode = (int)(mode & 0xFFF);
        f.Dentry.Inode.CTime = DateTime.Now;
        return 0;
    }
    private static int SysChown(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        
        string path = sm.ReadString(a1);
        int uid = (int)a2;
        int gid = (int)a3;
        
        var dentry = sm.PathWalk(path);
        if (dentry == null || dentry.Inode == null) return -2; // ENOENT
        
        // Permission check: only root can chown
        var t = Scheduler.GetByEngine(sm.Engine.State);
        if (t != null && t.Process.EUID != 0)
            return -1; // EPERM
        
        // -1 means "don't change"
        if (uid != -1) dentry.Inode.Uid = uid;
        if (gid != -1) dentry.Inode.Gid = gid;
        dentry.Inode.CTime = DateTime.Now;
        return 0;
    }
    private static int SysFchown(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        
        int fd = (int)a1;
        int uid = (int)a2;
        int gid = (int)a3;
        
        var f = sm.GetFD(fd);
        if (f == null || f.Dentry.Inode == null) return -9; // EBADF
        
        // Permission check: only root can chown
        var t = Scheduler.GetByEngine(sm.Engine.State);
        if (t != null && t.Process.EUID != 0)
            return -1; // EPERM
        
        if (uid != -1) f.Dentry.Inode.Uid = uid;
        if (gid != -1) f.Dentry.Inode.Gid = gid;
        f.Dentry.Inode.CTime = DateTime.Now;
        return 0;
    }
    private static int SysLchown(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        // Since we don't have symlinks yet, behave like chown
        return SysChown(state, a1, a2, a3, a4, a5, a6);
    }
    private static int SysSetGroups(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6) => 0;
    private static int SysGetGroups(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6) => 0;

    
    private static int SysGetCwd(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        uint bufAddr = a1;
        uint size = a2;
        
        var parts = new List<string>();
        var current = sm.CurrentWorkingDirectory;
        while (current != sm.ProcessRoot && current != sm.Root)
        {
            parts.Insert(0, current.Name);
            var next = current.Parent;
            if (current == current.SuperBlock.Root && current.MountedAt != null)
            {
                 next = current.MountedAt.Parent;
            }
            if (next == null || next == current) break;
            current = next;
        }
        
        string cwd = "/" + string.Join("/", parts);
        if (cwd.Length + 1 > size) return -(int)Errno.ERANGE;
        
        sm.Engine.MemWrite(bufAddr, System.Text.Encoding.ASCII.GetBytes(cwd + "\0"));
        return cwd.Length + 1;
    }
    
    private static int SysWriteV(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        int fd = (int)a1;
        uint iovAddr = a2;
        int iovCnt = (int)a3;
        
        var f = sm.GetFD(fd);
        if (f == null) return -(int)Errno.EBADF;

        int total = 0;
        for(int i=0; i<iovCnt; i++)
        {
            var baseBytes = sm.Engine.MemRead(iovAddr + (uint)i*8, 4);
            var lenBytes = sm.Engine.MemRead(iovAddr + (uint)i*8 + 4, 4);
            uint baseAddr = BinaryPrimitives.ReadUInt32LittleEndian(baseBytes);
            uint len = BinaryPrimitives.ReadUInt32LittleEndian(lenBytes);
            
            if (len > 0)
            {
                var data = sm.Engine.MemRead(baseAddr, len);
                f.Write(data);
                total += (int)len;
            }
        }
        return total;
    }
    
    private static int SysMmap2(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        
        uint addr = a1;
        uint len = a2;
        int prot = (int)a3;
        int flags = (int)a4;
        int fd = (int)a5;
        long offset = (long)a6 * 4096;
        
        prot |= (int)(Protection.Read | Protection.Write);
        
        Bifrost.VFS.File? f = null;
        bool isAnon = (flags & (int)MapFlags.Anonymous) != 0;
        
        if (!isAnon && fd != -1)
        {
            f = sm.GetFD(fd);
            if (f == null) return -(int)Errno.EBADF;
        }
        
        try
        {
            uint res = sm.Mem.Mmap(addr, len, (Protection)prot, (MapFlags)flags, f, offset, len, "MMAP2", sm.Engine);
            return (int)res;
        }
        catch
        {
            return -(int)Errno.ENOMEM;
        }
    }
    
    private static int SysMunmap(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        sm.Mem.Munmap(a1, a2, sm.Engine);
        return 0;
    }

    private static int SysMprotect(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        return 0;
    }
    
    private static int SysGetPid(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm?.GetTGID != null) return sm.GetTGID(sm.Engine);
        return 1000;
    }
    
    private static int SysGetPPid(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        
        var task = Scheduler.GetByEngine(state);
        if (task == null) return -(int)Errno.EPERM;
        
        return task.Process.PPID;
    }

    private static int SysGettid(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var task = Scheduler.GetByEngine(state);
        return task?.TID ?? -1;
    }

    private static int SysGetpgid(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var task = Scheduler.GetByEngine(state);
        // Simple PGID = TGID for now
        return task?.Process.TGID ?? -1;
    }

    private static int SysUmask(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var task = Scheduler.GetByEngine(state);
        if (task == null) return 0;
        int old = task.Process.Umask;
        task.Process.Umask = (int)(a1 & 0x1FF);
        return old;
    }

    private static int SysSethostname(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -1;
        var task = Scheduler.GetByEngine(state);
        if (task == null || task.Process.EUID != 0) return -(int)Errno.EPERM;

        string name = sm.ReadString(a1);
        task.Process.UTS.NodeName = name;
        return 0;
    }

    private static int SysSetdomainname(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -1;
        var task = Scheduler.GetByEngine(state);
        if (task == null || task.Process.EUID != 0) return -(int)Errno.EPERM;

        string name = sm.ReadString(a1);
        task.Process.UTS.DomainName = name;
        return 0;
    }

    private static int SysSchedYield(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        sm?.Engine.Yield();
        return 0;
    }

    private static int SysPause(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        sm?.Engine.Yield();
        return 0;
    }

    private static int SysSync(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -1;
        foreach (var file in sm.FDs.Values)
        {
            file?.Sync();
        }
        return 0;
    }

    private static int SysFsync(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -1;
        var file = sm.GetFD((int)a1);
        if (file == null) return -(int)Errno.EBADF;
        file.Sync();
        return 0;
    }

    private static int SysFdatasync(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        return SysFsync(state, a1, a2, a3, a4, a5, a6);
    }

    private static int SysMadvise(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        return 0; // No-op
    }

    private static int SysMsync(IntPtr state, uint addr, uint len, uint flags, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -1;
        uint end = addr + len;
        foreach (var vma in sm.Mem.FindVMAsInRange(addr, end))
        {
            sm.Mem.SyncVMA(vma, sm.Engine);
        }
        return 0;
    }
    
    private static int SysWait4(IntPtr state, uint pid, uint statusPtr, uint options, uint rusagePtr, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        
        IdType idtype;
        int id;
        
        if ((int)pid < -1)
        {
            // Wait for process group
            return -(int)Errno.ENOSYS;
        }
        else if ((int)pid == -1)
        {
            idtype = IdType.P_ALL;
            id = 0;
        }
        else if (pid == 0)
        {
            // Wait for same process group
            return -(int)Errno.ENOSYS;
        }
        else
        {
            idtype = IdType.P_PID;
            id = (int)pid;
        }
        
        var task = Scheduler.GetByEngine(state);
        if (task == null) return -(int)Errno.ECHILD;

        var infop = new SigInfo();
        var (result, tcs) = WaitHelpers.KernelWaitId(task, idtype, id, infop, (int)options);
        
        if (tcs != null)
        {
            // Need to block - set BlockingTask and yield
            task.BlockingTask = SyscallAsyncWrappers.SysWait4Async(sm, task, idtype, id, statusPtr, (int)options, tcs.Task);
            sm.Engine.Yield();
            return 0;
        }
        
        if (result > 0 && statusPtr != 0)
        {
            // Write exit status (WEXITSTATUS macro format)
            int status = (infop.si_status & 0xFF) << 8;
            byte[] statusBuf = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(statusBuf, status);
            sm.Engine.MemWrite(statusPtr, statusBuf);
        }
        
        // rusagePtr ignored for now
        
        return result;
    }
    
    private static int SysWaitPid(IntPtr state, uint pid, uint statusPtr, uint options, uint a4, uint a5, uint a6)
    {
        // waitpid(pid, status, options) = wait4(pid, status, options, NULL)
        return SysWait4(state, pid, statusPtr, options, 0, 0, 0);
    }
    
    private static int SysWaitId(IntPtr state, uint idtype, uint id, uint infop, uint options, uint rusagePtr, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        
        var task = Scheduler.GetByEngine(state);
        if (task == null) return -(int)Errno.ECHILD;

        var info = new SigInfo();
        var (result, tcs) = WaitHelpers.KernelWaitId(task, (IdType)idtype, (int)id, info, (int)options);
        
        if (tcs != null)
        {
            // Need to block - yield and child will run
            task.BlockingTask = SyscallAsyncWrappers.SysWaitIdAsync(sm, task, (IdType)idtype, (int)id, infop, (int)options, tcs.Task);
            sm.Engine.Yield();
            return 0;
        }
        
        if (result >= 0 && infop != 0)
        {
            // Write siginfo_t structure
            WriteSigInfo(sm, infop, info);
        }
        
        return result >= 0 ? 0 : result;
    }
    
    private static void WriteSigInfo(SyscallManager sm, uint addr, SigInfo info)
    {
        var buf = new byte[128];
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(0, 4), info.si_signo);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(4, 4), info.si_errno);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(8, 4), info.si_code);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(12, 4), info.si_pid);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(16, 4), info.si_uid);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(20, 4), info.si_status);
        sm.Engine.MemWrite(addr, buf);
    }
    
    private static int SysUnlink(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        string path = sm.ReadString(a1);
        
        int lastSlash = path.LastIndexOf('/');
        string parentPath = lastSlash == -1 ? "." : (lastSlash == 0 ? "/" : path.Substring(0, lastSlash));
        string name = lastSlash == -1 ? path : path.Substring(lastSlash + 1);
        
        var parentDentry = sm.PathWalk(parentPath);
        if (parentDentry == null) return -(int)Errno.ENOENT;
        
        try {
            parentDentry.Inode!.Unlink(name);
            parentDentry.Children.Remove(name);
            return 0;
        } catch { return -(int)Errno.ENOENT; }
    }
    
    private static int SysAccess(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -1;
        string path = sm.ReadString(a1);
        var dentry = sm.PathWalk(path);
        if (dentry != null && dentry.Inode != null) return 0;
        return -(int)Errno.ENOENT;
    }
    
    private static int SysIoctl(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        return 0;
    }
    
    private static int SysGetdents64(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        int fd = (int)a1;
        uint bufAddr = a2;
        int count = (int)a3;

        var f = sm.GetFD(fd);
        if (f == null || f.Dentry.Inode == null) return -(int)Errno.EBADF;

        try {
            var entries = f.Dentry.Inode.GetEntries();
            
            // Simplified logic: uses f.Position as index in entries list
            int startIdx = (int)f.Position;
            if (startIdx >= entries.Count) return 0;

            int writeOffset = 0;
            for (int i = startIdx; i < entries.Count; i++)
            {
                var entry = entries[i];
                byte[] nameBytes = Encoding.UTF8.GetBytes(entry.Name);
                int nameLen = nameBytes.Length + 1;
                int recLen = (8 + 8 + 2 + 1 + nameLen + 7) & ~7;

                if (writeOffset + recLen > count) break;

                uint baseAddr = bufAddr + (uint)writeOffset;
                
                byte[] buf = new byte[recLen];
                BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(0), entry.Ino);
                BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(8), (long)(writeOffset + recLen));
                BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(16), (ushort)recLen);
                
                byte dType = 8; // DT_REG
                if (entry.Type == InodeType.Directory) dType = 4;
                buf[18] = dType;

                Array.Copy(nameBytes, 0, buf, 19, nameBytes.Length);
                buf[19 + nameBytes.Length] = 0;

                sm.Engine.MemWrite(baseAddr, buf);
                writeOffset += recLen;
                f.Position = i + 1;
            }
            
            return writeOffset;
        } catch { return -(int)Errno.EPERM; }
    }

    private static void WriteStat64(SyscallManager sm, uint addr, Inode inode)
    {
        byte[] buf = new byte[96];
        
        uint mode = (uint)inode.Mode | (uint)inode.Type;
        long size = (long)inode.Size;
        uint uid = (uint)inode.Uid;
        uint gid = (uint)inode.Gid;

        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(0), 0x800);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(12), (uint)inode.Ino); // __st_ino
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(16), mode);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(20), 1); // nlink
        
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(24), uid);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(28), gid);

        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(44), (ulong)size);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(52), 4096);
        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(56), (ulong)((size+511)/512));
        
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(64), (uint)new DateTimeOffset(inode.ATime).ToUnixTimeSeconds());
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(72), (uint)new DateTimeOffset(inode.MTime).ToUnixTimeSeconds());
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(80), (uint)new DateTimeOffset(inode.CTime).ToUnixTimeSeconds());
        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(88), inode.Ino);
        
        sm.Engine.MemWrite(addr, buf);
    }

    private static void WriteStat(SyscallManager sm, uint addr, Inode inode)
    {
        byte[] buf = new byte[64];
        
        uint mode = (uint)inode.Mode | (uint)inode.Type;
        long size = (long)inode.Size;
        uint uid = (uint)inode.Uid;
        uint gid = (uint)inode.Gid;

        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0), 0x800); // st_dev
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4), (uint)inode.Ino);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(8), (ushort)mode);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(10), 1); // nlink
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(12), (ushort)uid);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(14), (ushort)gid);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(16), 0); // st_rdev
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(20), (uint)size);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(24), 4096); // blksize
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(28), (uint)((size+511)/512));
        
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(32), (uint)new DateTimeOffset(inode.ATime).ToUnixTimeSeconds());
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(40), (uint)new DateTimeOffset(inode.MTime).ToUnixTimeSeconds());
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(48), (uint)new DateTimeOffset(inode.CTime).ToUnixTimeSeconds());
        
        sm.Engine.MemWrite(addr, buf);
    }

    private static void WriteStatx(SyscallManager sm, uint addr, Inode inode, uint mask)
    {
        byte[] buf = new byte[256];
        
        uint actualMask = mask & LinuxConstants.STATX_BASIC_STATS;
        
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0x00), actualMask);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0x04), 4096); // blksize
        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(0x08), 0); // attributes
        
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0x10), (uint)Math.Max(1, inode.Dentries.Count));
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0x14), (uint)inode.Uid);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0x18), (uint)inode.Gid);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0x1C), (ushort)((uint)inode.Mode | (uint)inode.Type));
        
        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(0x20), inode.Ino);
        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(0x28), inode.Size);
        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(0x30), (inode.Size + 511) / 512);
        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(0x38), 0); // attributes_mask
        
        Action<int, DateTime> writeTime = (offset, dt) => {
            var dto = new DateTimeOffset(dt);
            BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(offset), dto.ToUnixTimeSeconds());
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(offset + 8), (uint)(dto.Millisecond * 1000000));
        };
        
        writeTime(0x40, inode.ATime);
        writeTime(0x50, DateTime.UnixEpoch); // btime (creation) - not supported yet
        writeTime(0x60, inode.CTime);
        writeTime(0x70, inode.MTime);
        
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0x80), 0); // rdev_major
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0x84), 0); // rdev_minor
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0x88), 0x8); // dev_major (faked)
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0x8C), 0x0); // dev_minor (faked)
        
        sm.Engine.MemWrite(addr, buf);
    }

    private static int SysStat64(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        return ImplStat64(state, a1, a2);
    }
    
    private static int SysLstat64(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        return ImplStat64(state, a1, a2);
    }
    
    private static int ImplStat64(IntPtr state, uint ptrPath, uint ptrStat)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        string path = sm.ReadString(ptrPath);
        var dentry = sm.PathWalk(path);
        if (dentry == null || dentry.Inode == null) return -(int)Errno.ENOENT;
        
        WriteStat64(sm, ptrStat, dentry.Inode);
        return 0;
    }

    private static int SysGetTimeOfDay(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        
        uint tvPtr = a1;
        uint tzPtr = a2;
        
        var dto = DateTimeOffset.UtcNow;
        long secs = dto.ToUnixTimeSeconds();
        int usecs = dto.Millisecond * 1000;
        
        if (tvPtr != 0)
        {
            byte[] buf = new byte[8];
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(0, 4), (int)secs);
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(4, 4), usecs);
            sm.Engine.MemWrite(tvPtr, buf);
        }
        
        return 0;
    }

    private static int SysClockGetTime(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        
        int clockId = (int)a1;
        uint tsPtr = a2;
        
        DateTime now;
        if (clockId == LinuxConstants.CLOCK_REALTIME)
        {
            now = DateTime.UtcNow;
        }
        else
        {
            // Faking monotonic with UtcNow for now
            now = DateTime.UtcNow;
        }
        
        var dto = new DateTimeOffset(now);
        long secs = dto.ToUnixTimeSeconds();
        int nsecs = (int)(dto.Millisecond * 1000000);
        
        byte[] buf = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(0, 4), (int)secs);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(4, 4), nsecs);
        sm.Engine.MemWrite(tsPtr, buf);
        
        return 0;
    }

    private static int SysClockGetTime64(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        
        int clockId = (int)a1;
        uint tsPtr = a2;
        
        DateTime now;
        if (clockId == LinuxConstants.CLOCK_REALTIME)
        {
            now = DateTime.UtcNow;
        }
        else
        {
            now = DateTime.UtcNow;
        }
        
        var dto = new DateTimeOffset(now);
        long secs = dto.ToUnixTimeSeconds();
        int nsecs = (int)(dto.Millisecond * 1000000);
        
        byte[] buf = new byte[12];
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(0, 8), secs);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(8, 4), nsecs);
        sm.Engine.MemWrite(tsPtr, buf);
        
        return 0;
    }

    private static int SysFstat64(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        int fd = (int)a1;
        var f = sm.GetFD(fd);
        if (f == null || f.Dentry.Inode == null) return -(int)Errno.EBADF;
        
        WriteStat64(sm, a2, f.Dentry.Inode);
        return 0;
    }
    
    private static int SysSymlink(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        string target = sm.ReadString(a1);
        string linkpath = sm.ReadString(a2);

        int lastSlash = linkpath.LastIndexOf('/');
        string parentPath = lastSlash == -1 ? "" : linkpath.Substring(0, lastSlash);
        string name = lastSlash == -1 ? linkpath : linkpath.Substring(lastSlash + 1);

        var parentDentry = sm.PathWalk(parentPath == "" ? "." : parentPath);
        if (parentDentry == null || parentDentry.Inode == null) return -(int)Errno.ENOENT;

        var t = Scheduler.GetByEngine(sm.Engine.State);
        int uid = t?.Process.EUID ?? 0;
        int gid = t?.Process.EGID ?? 0;

        try {
            var dentry = new Dentry(name, null, parentDentry, parentDentry.SuperBlock);
            parentDentry.Inode.Symlink(dentry, target, uid, gid);
            return 0;
        } catch { return -(int)Errno.EACCES; }
    }

    private static int SysReadlink(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        string path = sm.ReadString(a1);
        uint bufAddr = a2;
        int bufSize = (int)a3;

        var dentry = sm.PathWalk(path, followLink: false);
        if (dentry == null || dentry.Inode == null) return -(int)Errno.ENOENT;
        if (dentry.Inode.Type != InodeType.Symlink) return -(int)Errno.EINVAL;

        string target = dentry.Inode.Readlink();
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(target);
        int len = Math.Min(bytes.Length, bufSize);
        sm.Engine.MemWrite(bufAddr, bytes.AsSpan(0, len));
        return len;
    }

    private static int SysReadlinkAt(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        int dirfd = (int)a1;
        string path = sm.ReadString(a2);
        uint bufAddr = a3;
        int bufSize = (int)a4;

        Dentry? startAt = null;
        if (dirfd != -100 && !path.StartsWith("/"))
        {
            var fdir = sm.GetFD(dirfd);
            if (fdir == null) return -(int)Errno.EBADF;
            startAt = fdir.Dentry;
        }

        var dentry = sm.PathWalk(path, startAt, followLink: false);
        if (dentry == null || dentry.Inode == null) return -(int)Errno.ENOENT;
        if (dentry.Inode.Type != InodeType.Symlink) return -(int)Errno.EINVAL;

        string target = dentry.Inode.Readlink();
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(target);
        int len = Math.Min(bytes.Length, bufSize);
        sm.Engine.MemWrite(bufAddr, bytes.AsSpan(0, len));
        return len;
    }

    private static int SysSymlinkAt(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        string target = sm.ReadString(a1);
        int dirfd = (int)a2;
        string linkpath = sm.ReadString(a3);

        Dentry? startAt = null;
        if (dirfd != -100 && !linkpath.StartsWith("/"))
        {
            var fdir = sm.GetFD(dirfd);
            if (fdir == null) return -(int)Errno.EBADF;
            startAt = fdir.Dentry;
        }

        int lastSlash = linkpath.LastIndexOf('/');
        string parentPath = lastSlash == -1 ? "" : linkpath.Substring(0, lastSlash);
        string name = lastSlash == -1 ? linkpath : linkpath.Substring(lastSlash + 1);

        var parentDentry = sm.PathWalk(parentPath == "" ? "." : parentPath, startAt);
        if (parentDentry == null || parentDentry.Inode == null) return -(int)Errno.ENOENT;

        var t = Scheduler.GetByEngine(sm.Engine.State);
        int uid = t?.Process.EUID ?? 0;
        int gid = t?.Process.EGID ?? 0;

        try {
            var dentry = new Dentry(name, null, parentDentry, parentDentry.SuperBlock);
            parentDentry.Inode.Symlink(dentry, target, uid, gid);
            return 0;
        } catch { return -(int)Errno.EACCES; }
    }
    
    private static int SysRtSigAction(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        return 0;
    }

    private static int SysRtSigProcMask(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        return 0;
    }
    
    private static int SysSigReturn(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        return 0;
    }

    private static int SysMount(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        
        string source = a1 == 0 ? "" : sm.ReadString(a1);
        string target = sm.ReadString(a2);
        string fstype = sm.ReadString(a3);
        uint flags = a4;
        
        var targetDentry = sm.PathWalk(target);
        if (targetDentry == null)
        {
            return -(int)Errno.ENOENT;
        }
        
        SuperBlock? newSb = null;
        if (fstype == "tmpfs" || fstype == "devtmpfs")
        {
            var fsType = new FileSystemType { Name = fstype, FileSystem = new Tmpfs() };
            newSb = fsType.FileSystem.ReadSuper(fsType, (int)flags, source, null);
        }
        else if (fstype == "hostfs" || ((flags & (uint)LinuxConstants.MS_BIND) != 0)) // MS_BIND
        {
             string hostRoot = source;
             if ((flags & (uint)LinuxConstants.MS_BIND) != 0)
             {
                 var srcDentry = sm.PathWalk(source);
                 if (srcDentry != null && srcDentry.Inode is HostInode hi) hostRoot = hi.HostPath;
             }
             
             var fsType = new FileSystemType { Name = "hostfs", FileSystem = new Hostfs() };
             try {
                newSb = fsType.FileSystem.ReadSuper(fsType, (int)flags, hostRoot, null);
             } catch {
                // Failed to create hostfs
                return -(int)Errno.ENOENT;
             }
        }

        if (newSb != null)
        {
            targetDentry.IsMounted = true;
            targetDentry.MountRoot = newSb.Root;
            newSb.Root.MountedAt = targetDentry;
            return 0;
        }

        return -22; // EINVAL
    }

    private static int SysUmount(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        
        string target = sm.ReadString(a1);
        var targetDentry = sm.PathWalk(target);
        
        // PathWalk follows mounts, so we need to check MountedAt to find the actual mount point
        if (targetDentry != null && targetDentry.MountedAt != null)
        {
            // This dentry is a mount root, find the mount point
            var mountPoint = targetDentry.MountedAt;
            var mountRoot = mountPoint.MountRoot;
            
            if (mountRoot == null) return -(int)Errno.EINVAL;
            
            // Check if filesystem is busy (has active inodes)
            if (mountRoot.SuperBlock.HasActiveInodes())
            {
                return -16; // EBUSY
            }
            
            // Detach mount
            mountPoint.IsMounted = false;
            mountPoint.MountRoot = null;
            targetDentry.MountedAt = null;
            
            // Release SuperBlock reference
            mountRoot.SuperBlock.Put();
            return 0;
        }
        else if (targetDentry != null && targetDentry.IsMounted)
        {
            // This is the mount point itself
            var mountRoot = targetDentry.MountRoot;
            
            if (mountRoot != null && mountRoot.SuperBlock.HasActiveInodes())
            {
                return -16; // EBUSY
            }
            
            targetDentry.IsMounted = false;
            if (targetDentry.MountRoot != null)
            {
                targetDentry.MountRoot.MountedAt = null;
                targetDentry.MountRoot.SuperBlock.Put();
            }
            targetDentry.MountRoot = null;
            return 0;
        }
        return -22; // EINVAL
    }

    private static int SysUmount2(IntPtr state, uint a1, uint flags, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        
        string target = sm.ReadString(a1);
        var targetDentry = sm.PathWalk(target);
        
        const uint MNT_FORCE = 1;
        const uint MNT_DETACH = 2;
        
        Dentry? mountPoint = null;
        Dentry? mountRoot = null;
        
        if (targetDentry != null && targetDentry.MountedAt != null)
        {
            mountPoint = targetDentry.MountedAt;
            mountRoot = mountPoint.MountRoot;
        }
        else if (targetDentry != null && targetDentry.IsMounted)
        {
            mountPoint = targetDentry;
            mountRoot = targetDentry.MountRoot;
        }
        else
        {
            return -22; // EINVAL
        }
        
        if (mountRoot == null) return -22; // EINVAL
        
        if ((flags & MNT_DETACH) != 0)
        {
            // Lazy unmount: detach immediately but allow active references to continue
            mountPoint.IsMounted = false;
            mountPoint.MountRoot = null;
            if (targetDentry?.MountedAt != null) targetDentry.MountedAt = null;
            else if (mountRoot.MountedAt != null) mountRoot.MountedAt = null;
            // Don't call sb.Put() - let reference counting naturally decrease when files close
            return 0;
        }
        
        // Normal umount with optional force
        if (mountRoot.SuperBlock.HasActiveInodes() && (flags & MNT_FORCE) == 0)
        {
            return -16; // EBUSY
        }
        
        // Force unmount or no active inodes
        mountPoint.IsMounted = false;
        mountPoint.MountRoot = null;
        if (targetDentry?.MountedAt != null) targetDentry.MountedAt = null;
        else if (mountRoot.MountedAt != null) mountRoot.MountedAt = null;
        mountRoot.SuperBlock.Put();
        
        return 0;
    }

    private static int SysChroot(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        
        string path = sm.ReadString(a1);
        var dentry = sm.PathWalk(path);
        
        if (dentry == null || dentry.Inode == null) return -(int)Errno.ENOENT;
        if (dentry.Inode.Type != InodeType.Directory) return -(int)Errno.ENOTDIR; // ENOTDIR
        
        sm.ProcessRoot = dentry;
        return 0;
    }
}