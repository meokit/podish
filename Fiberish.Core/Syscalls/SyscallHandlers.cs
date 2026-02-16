using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Buffers.Binary;
using System.Text;
using System.Linq;
using Bifrost.Core;
using Bifrost.Native;
using Bifrost.Memory;
using Bifrost.VFS;
using Microsoft.Extensions.Logging;

namespace Bifrost.Syscalls;

public partial class SyscallManager
{
    private void RegisterHandlers()
    {
        Register(X86SyscallNumbers.exit, SysExit);
        Register(X86SyscallNumbers.fork, SysFork);
        Register(X86SyscallNumbers.fcntl64, SysFcntl64);
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
        Register(140, SysLlseek);
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
        Register(X86SyscallNumbers.clock_gettime64, SysClockGetTime64);
        Register(X86SyscallNumbers.gettimeofday, SysGetTimeOfDay);
        Register(X86SyscallNumbers.nanosleep, SysNanosleep);
        
        Register(X86SyscallNumbers.rt_sigreturn, SysRtSigReturn);

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
        
        Register(X86SyscallNumbers.kill, SysKill);
        Register(X86SyscallNumbers.tkill, SysTkill);
        Register(X86SyscallNumbers.tgkill, SysTgkill);
        Register(X86SyscallNumbers.execve, SysExecve);

        Register(X86SyscallNumbers.select, SysSelect);
        Register(X86SyscallNumbers._newselect, SysNewSelect);
        Register(X86SyscallNumbers.poll, SysPoll);
        Register(X86SyscallNumbers.pipe, SysPipe);
        Register(239, SysSendfile64);
    }

    internal static async ValueTask<int> SysSendfile64(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        // ssize_t sendfile64(int out_fd, int in_fd, off64_t *offset, size_t count);
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        int outFd = (int)a1;
        int inFd = (int)a2;
        uint offsetPtr = a3;
        int count = (int)a4;

        if (!sm.FDs.ContainsKey(inFd) || !sm.FDs.ContainsKey(outFd)) return -(int)Errno.EBADF;
        var inFile = sm.FDs[inFd];
        var outFile = sm.FDs[outFd];

        if (inFile == null || outFile == null) return -(int)Errno.EBADF;

        // Verify modes
        const int O_ACCMODE = 3;
        if (((int)inFile.Flags & O_ACCMODE) == (int)FileFlags.O_WRONLY) return -(int)Errno.EBADF;
        if (((int)outFile.Flags & O_ACCMODE) == (int)FileFlags.O_RDONLY) return -(int)Errno.EBADF;

        // Use a buffer
        byte[] buffer = new byte[Math.Min(count, 32768)];
        int totalWritten = 0;

        try 
        {
            long initialOffset = -1;
            if (offsetPtr != 0)
            {
                byte[] offsetBytes = new byte[8];
                if (!sm.Engine.CopyFromUser(offsetPtr, offsetBytes)) return -(int)Errno.EFAULT;
                initialOffset = BitConverter.ToInt64(offsetBytes);
            }

            int remaining = count;
            while (remaining > 0)
            {
                int toRead = Math.Min(remaining, buffer.Length);
                int bytesRead = 0;
                
                if (offsetPtr != 0)
                {
                    // Read from specific offset directly from inode
                    bytesRead = inFile.Dentry.Inode!.Read(inFile, buffer.AsSpan(0, toRead), initialOffset + totalWritten);
                }
                else
                {
                    // Read from current position via File object
                    bytesRead = inFile.Read(buffer.AsSpan(0, toRead));
                }

                if (bytesRead <= 0) 
                {
                    if (bytesRead == 0) // EOF
                    {
                        break; 
                    } 
                }

                // Write to out_fd
                int bytesWritten = outFile.Write(buffer.AsSpan(0, bytesRead));
                
                if (bytesWritten < 0) 
                {
                   if (totalWritten > 0) break; 
                   return bytesWritten;
                }
                
                totalWritten += bytesWritten;
                remaining -= bytesWritten;
                
                if (bytesWritten < bytesRead) break;
            }

            if (offsetPtr != 0)
            {
                if (!sm.Engine.CopyToUser(offsetPtr, BitConverter.GetBytes(initialOffset + totalWritten)))
                    return -(int)Errno.EFAULT;
            }

            return totalWritten;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "SysSendfile64 failed");
            return -(int)Errno.EIO;
        }
    }

    private static async ValueTask<int> SysPipe(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {

        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        uint fdsAddr = a1;

        try
        {
            var pipe = new PipeInode();
            
            // Reader
            var rDentry = new Dentry("pipe:[read]", pipe, sm.Root, sm.Root.SuperBlock);
            var rFile = new Bifrost.VFS.File(rDentry, FileFlags.O_RDONLY);
            int rFd = sm.AllocFD(rFile);
            // pipe.AddReader(); // Handled by File ctor -> Inode.Open

            // Writer
            var wDentry = new Dentry("pipe:[write]", pipe, sm.Root, sm.Root.SuperBlock);
            var wFile = new Bifrost.VFS.File(wDentry, FileFlags.O_WRONLY);
            int wFd = sm.AllocFD(wFile);
            // pipe.AddWriter(); // Handled by File ctor -> Inode.Open

            // Write FDs to user memory
            // Write FDs to user memory
            var fds = new int[] { rFd, wFd };
            if (!sm.Engine.CopyToUser(fdsAddr, MemoryMarshal.AsBytes(fds.AsSpan())))
                return -(int)Errno.EFAULT;

            return 0;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "SysPipe failed");
            return -(int)Errno.ENFILE;
        }
    }

    private static async ValueTask<int> SysCreat(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        // creat(path, mode) is open(path, O_CREAT|O_WRONLY|O_TRUNC, mode)
        return await SysOpen(state, a1, (uint)(FileFlags.O_CREAT | FileFlags.O_WRONLY | FileFlags.O_TRUNC), a2, a4, a5, a6);
    }

    private static async ValueTask<int> SysLink(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
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

    private static async ValueTask<int> SysChdir(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
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

    private static async ValueTask<int> SysTime(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        long t = DateTimeOffset.Now.ToUnixTimeSeconds();
        if (a1 != 0)
        {
            if (!sm.Engine.CopyToUser(a1, BitConverter.GetBytes((uint)t))) return -(int)Errno.EFAULT;
        }
        return (int)t;
    }

    private static int ImplOpen(SyscallManager sm, string path, uint flags, uint mode, Dentry? startAt = null)
    {
        Logger.LogInformation($"[Open] Path='{path}' Flags={flags} Mode={mode}");
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

                var t = (sm.Engine.Owner as FiberTask);
                int uid = t?.Process.EUID ?? 0;
                int gid = t?.Process.EGID ?? 0;

                try
                {
                    dentry = new Dentry(name, null, parentDentry, parentDentry.SuperBlock);
                    int finalMode = (int)mode & ~(t?.Process.Umask ?? 0);
                    parentDentry.Inode.Create(dentry, finalMode, uid, gid);
                }
                catch
                {
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

        try
        {
            var f = new Bifrost.VFS.File(dentry, (FileFlags)flags);
            return sm.AllocFD(f);
        }
        catch
        {
            return -1;
        }
    }

    private static async ValueTask<int> SysOpen(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -1;
        
        string? path = sm.Engine.ReadStringSafe(a1);
        if (path == null) return -(int)Errno.EFAULT;

        return ImplOpen(sm, path, a2, a3);
    }

    private static async ValueTask<int> SysOpenAt(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        int dirfd = (int)a1;
        string? path = sm.Engine.ReadStringSafe(a2);
        if (path == null) return -(int)Errno.EFAULT;

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

    private static async ValueTask<int> SysOpenAt2(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        int dirfd = (int)a1;
        string? path = sm.Engine.ReadStringSafe(a2);
        if (path == null) return -(int)Errno.EFAULT;

        uint howPtr = a3;
        uint howSize = a4;

        if (howSize < 24) return -(int)Errno.EINVAL;

        byte[] howBuf = new byte[24];
        if (!sm.Engine.CopyFromUser(howPtr, howBuf)) return -(int)Errno.EFAULT;

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

    private static async ValueTask<int> SysDup(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        int oldfd = (int)a1;
        var f = sm.GetFD(oldfd);
        if (f == null) return -(int)Errno.EBADF;

        return sm.AllocFD(f);
    }

    private static async ValueTask<int> SysDup2(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
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

    private static async ValueTask<int> SysDup3(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        // For now ignore flags like O_CLOEXEC
        return await SysDup2(state, a1, a2, a3, a4, a5, a6);
    }

    private static async ValueTask<int> SysPRead(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        int fd = (int)a1;
        uint bufAddr = a2;
        uint count = a3;
        long offset = (long)a4 | ((long)a5 << 32);

        var f = sm.GetFD(fd);
        if (f == null) return -(int)Errno.EBADF;

        try
        {
            byte[] buf = new byte[count];
            int n = f.Dentry.Inode!.Read(f, buf.AsSpan(), offset);
            if (n > 0) 
            {
                if (!sm.Engine.CopyToUser(bufAddr, buf.AsSpan(0, n)))
                    return -(int)Errno.EFAULT;
            }
            return n;
        }
        catch { return -(int)Errno.EIO; }
    }

    private static async ValueTask<int> SysPWrite(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        int fd = (int)a1;
        uint bufAddr = a2;
        uint count = a3;
        long offset = (long)a4 | ((long)a5 << 32);

        byte[] data = new byte[count];
        if (!sm.Engine.CopyFromUser(bufAddr, data)) return -(int)Errno.EFAULT;

        var f = sm.GetFD(fd);
        if (f == null) return -(int)Errno.EBADF;

        try
        {
            int n = f.Dentry.Inode!.Write(f, data, offset);
            return n;
        }
        catch { return -(int)Errno.EIO; }
    }

    private static async ValueTask<int> SysReadV(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        int fd = (int)a1;
        uint iovAddr = a2;
        int iovCnt = (int)a3;

        var f = sm.GetFD(fd);
        if (f == null) return -(int)Errno.EBADF;

        int totalRead = 0;
        for (int i = 0; i < iovCnt; i++)
        {
            byte[] iovBuf = new byte[8];
            if (!sm.Engine.CopyFromUser(iovAddr + (uint)i * 8, iovBuf)) return -(int)Errno.EFAULT;
            
            uint baseAddr = BinaryPrimitives.ReadUInt32LittleEndian(iovBuf);
            uint len = BinaryPrimitives.ReadUInt32LittleEndian(iovBuf.AsSpan(4));

            if (len > 0)
            {
                byte[] buf = new byte[len];
                int n = f.Read(buf);
                if (n > 0)
                {
                if (!sm.Engine.CopyToUser(baseAddr, buf.AsSpan(0, n))) return -(int)Errno.EFAULT;
                totalRead += n;
                    if (n < (int)len) break; // EOF or short read
                }
                else break;
            }
        }
        return totalRead;
    }

    private static async ValueTask<int> SysPReadV(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
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
        for (int i = 0; i < iovCnt; i++)
        {
            byte[] iovBuf = new byte[8];
            if (!sm.Engine.CopyFromUser(iovAddr + (uint)i * 8, iovBuf)) return -(int)Errno.EFAULT;
            uint baseAddr = BinaryPrimitives.ReadUInt32LittleEndian(iovBuf);
            uint len = BinaryPrimitives.ReadUInt32LittleEndian(iovBuf.AsSpan(4));

            if (len > 0)
            {
                byte[] buf = new byte[len];
                int n = f.Dentry.Inode!.Read(f, buf, offset + totalRead);
                if (n > 0)
                {
                if (!sm.Engine.CopyToUser(baseAddr, buf.AsSpan(0, n))) return -(int)Errno.EFAULT;
                totalRead += n;
                    if (n < (int)len) break;
                }
                else break;
            }
        }
        return totalRead;
    }

    private static async ValueTask<int> SysPWriteV(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
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
        for (int i = 0; i < iovCnt; i++)
        {
            byte[] iovBuf = new byte[8];
            if (!sm.Engine.CopyFromUser(iovAddr + (uint)i * 8, iovBuf)) return -(int)Errno.EFAULT;
            
            uint baseAddr = BinaryPrimitives.ReadUInt32LittleEndian(iovBuf);
            uint len = BinaryPrimitives.ReadUInt32LittleEndian(iovBuf.AsSpan(4));

            if (len > 0)
            {
                byte[] data = new byte[len];
                if (!sm.Engine.CopyFromUser(baseAddr, data)) return -(int)Errno.EFAULT;
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

    private static async ValueTask<int> SysMkdir(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
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

        var t = (sm.Engine.Owner as FiberTask);
        int uid = t?.Process.EUID ?? 0;
        int gid = t?.Process.EGID ?? 0;

        try
        {
            var dentry = new Dentry(name, null, parentDentry, parentDentry.SuperBlock);
            parentDentry.Inode.Mkdir(dentry, (int)mode, uid, gid);
            return 0;
        }
        catch { return -(int)Errno.EACCES; }
    }

    private static async ValueTask<int> SysRmdir(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
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

        try
        {
            parentDentry.Inode!.Rmdir(name);
            parentDentry.Children.Remove(name);
            return 0;
        }
        catch { return -(int)Errno.EACCES; }
    }

    private static async ValueTask<int> SysMkdirAt(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
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

        var t = (sm.Engine.Owner as FiberTask);
        int uid = t?.Process.EUID ?? 0;
        int gid = t?.Process.EGID ?? 0;

        try
        {
            var dentry = new Dentry(name, null, parentDentry, parentDentry.SuperBlock);
            parentDentry.Inode.Mkdir(dentry, (int)mode, uid, gid);
            return 0;
        }
        catch { return -(int)Errno.EACCES; }
    }

    private static async ValueTask<int> SysUnlinkAt(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
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
            try
            {
                parentDentry.Inode.Rmdir(name);
                parentDentry.Children.Remove(name);
                return 0;
            }
            catch { return -(int)Errno.EACCES; }
        }
        else
        {
            try
            {
                parentDentry.Inode.Unlink(name);
                parentDentry.Children.Remove(name);
                return 0;
            }
            catch { return -(int)Errno.ENOENT; }
        }
    }


    private static async ValueTask<int> SysGetdents(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        int fd = (int)a1;
        uint bufAddr = a2;
        int count = (int)a3;

        var f = sm.GetFD(fd);
        if (f == null || f.Dentry.Inode == null) return -(int)Errno.EBADF;
        if (f.Dentry.Inode.Type != InodeType.Directory) return -(int)Errno.ENOTDIR;

        try
        {
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

                byte dType = entry.Type switch
                {
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

                if (!sm.Engine.CopyToUser(baseAddr, buf))
                    return -(int)Errno.EFAULT;
                writeOffset += reclen;
                f.Position = i + 1;
            }
            return writeOffset;
        }
        catch { return -(int)Errno.EIO; }
    }

    private static async ValueTask<int> SysNewFstatAt(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        int dirfd = (int)a1;
        string path = sm.ReadString(a2);
        uint statAddr = a3;
        uint flags = a4;

        if (path == "" && (flags & 0x1000) != 0) // AT_EMPTY_PATH
        {
            return await SysFstat64(state, a1, a3, 0, 0, 0, 0);
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

    private static async ValueTask<int> SysUtimensAt(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
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

    private static async ValueTask<int> SysFchownAt(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
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
        return await SysChown(state, a2, a3, a4, 0, 0, 0); // Simplified: should use dentry directly
    }

    private static async ValueTask<int> SysFchmodAt(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
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

        return await SysChmod(state, a2, a3, 0, 0, 0, 0);
    }

    private static async ValueTask<int> SysFaccessAt(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
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

        return await SysAccess(state, a2, a3, 0, 0, 0, 0);
    }

    private static async ValueTask<int> SysRename(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        string oldPath = sm.ReadString(a1);
        string newPath = sm.ReadString(a2);

        return ImplRename(sm, -100, oldPath, -100, newPath, 0);
    }

    private static async ValueTask<int> SysRenameAt(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        return ImplRename(sm, (int)a1, sm.ReadString(a2), (int)a3, sm.ReadString(a4), 0);
    }

    private static async ValueTask<int> SysRenameAt2(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
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

        try
        {
            oldParentDentry.Inode!.Rename(oldName, newParentDentry.Inode!, newName);
            return 0;
        }
        catch { return -(int)Errno.EACCES; }
    }

    private static async ValueTask<int> SysStat(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        string path = sm.ReadString(a1);
        var dentry = sm.PathWalk(path);
        if (dentry == null || dentry.Inode == null) return -(int)Errno.ENOENT;

        WriteStat(sm, a2, dentry.Inode);
        return 0;
    }

    private static async ValueTask<int> SysLstat(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        string path = sm.ReadString(a1);
        var dentry = sm.PathWalk(path, followLink: false);
        if (dentry == null || dentry.Inode == null) return -(int)Errno.ENOENT;

        WriteStat(sm, a2, dentry.Inode);
        return 0;
    }

    private static async ValueTask<int> SysFstat(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        int fd = (int)a1;
        var f = sm.GetFD(fd);
        if (f == null || f.Dentry.Inode == null) return -(int)Errno.EBADF;

        WriteStat(sm, a2, f.Dentry.Inode);
        return 0;
    }

    private static async ValueTask<int> SysStatx(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
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

    private static async ValueTask<int> SysExit(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        int exitCode = (int)a1;
        var fiberTask = sm.Engine.Owner as FiberTask;
        if (fiberTask != null)
        {
            fiberTask.Exited = true;
            fiberTask.ExitStatus = exitCode;
            
            // Notify Parent
            var ppid = fiberTask.Process.PPID;
            if (ppid > 0)
            {
                // We still need KernelScheduler to find OTHER tasks (parent).
                // KernelScheduler SHOULD be available via FiberTask reference?
                // Yes, FiberTask.CommonKernel property.
                var parentTask = fiberTask.CommonKernel.GetTask(ppid);
                if (parentTask != null)
                {
                    // Signaling child exit usually means handling SIGCHLD.
                    parentTask.HandleSignal(17); // SIGCHLD = 17
                }
            }
            return 0;
        }

        var task = (sm.Engine.Owner as FiberTask);
        if (task != null)
        {
            // Remove from /proc
            // Only if leader? Threads exit individually.
            // Linux removes /proc/pid only when all threads are gone (ThreadGroup empty).
            // Simplified: If TID == TGID (main thread) and we are exiting properly? 
            // Actually SysExit terminates the thread. SysExitGroup terminates the process.
            // If SysExit is called by main thread, the process becomes zombie but /proc/pid remains until Waitpid?
            // "ps" should show Zombies.
            // So we should NOT remove /proc/pid here immediately if we want to see Zombies.
            // But we don't support full Zombie state inspection in /proc yet.
            // If we remove it, "ps" won't show it.
            // Let's remove it for cleanup for now, or on Waitpid reaping.
            
            // Current simple impl: Remove on exit.
            if (task.TID == task.Process.TGID)
                ProcFsManager.OnProcessExit(sm, task.Process.TGID);

            task.ExitStatus = (int)a1;
            task.Exited = true;

            task.Process.State = ProcessState.Zombie;
            task.Process.ExitStatus = (int)a1;

            // Signal zombie event for waitpid
            task.Process.ZombieEvent.Set();
        }

        int code = (int)a1;
        sm.ExitHandler?.Invoke(sm.Engine, code, false);
        sm.Engine.Stop();
        return 0;
    }

    private static async ValueTask<int> SysExitGroup(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        
        var task = (sm.Engine.Owner as FiberTask);
        if (task != null)
        {
             ProcFsManager.OnProcessExit(sm, task.Process.TGID);
        }

        int code = (int)a1;
        sm.ExitHandler?.Invoke(sm.Engine, code, true);
        sm.Engine.Stop();
        return 0;
    } 

    // ... SysRead ... (omitted)

    private static async ValueTask<int> SysClone(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        
        var current = sm.Engine.Owner as FiberTask;
        if (current == null) return -(int)Errno.EPERM;

        uint flags = a1;
        uint stackPtr = a2;
        uint ptidPtr = a3;
        uint tlsPtr = a4;
        uint ctidPtr = a5;

        // Clone
        var child = await current.Clone((int)flags, stackPtr, ptidPtr, tlsPtr, ctidPtr);
        
        // TODO: Re-verify ProcFsManager compatibility
        // ProcFsManager.OnProcessStart(sm, child.TID);

        return child.TID; 
    }

    internal static async ValueTask<int> SysRead(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        int fd = (int)a1;
        uint bufAddr = a2;
        int count = (int)a3;

        var f = sm.GetFD(fd);
        if (f == null) return -(int)Errno.EBADF;
        
        // TODO: Access checks

        byte[] buf = new byte[count];
        
        while (true)
        {
            try
            {
                int n = f.Read(buf.AsSpan(0, count));
                
                if (n == -(int)Errno.EAGAIN)
                {
                    if ((f.Flags & FileFlags.O_NONBLOCK) != 0) return -(int)Errno.EAGAIN;
                    
                    // Blocking Read
                    var currentTask = sm.Engine.Owner as FiberTask;
                    if (currentTask == null) return -(int)Errno.EAGAIN; // Should not happen

                    // Wait for read or interrupt
                    var tcs = new TaskCompletionSource<bool>();
                    currentTask.RegisterBlockingSyscall(() => tcs.TrySetResult(false)); // False = Interrupted
                    
                    try
                    {
                        var waitTask = f.WaitForRead().AsTask();
                        var interruptTask = tcs.Task;
                        
                        var finishedTask = await System.Threading.Tasks.Task.WhenAny(waitTask, interruptTask);
                        
                        if (finishedTask == interruptTask)
                        {
                            return -(int)Errno.EINTR;
                        }
                        
                        // File is ready, retry read
                        continue;
                    }
                    finally
                    {
                        currentTask.ClearInterrupt();
                    }
                }
                
                if (n >= 0)
                {
                    if (n > 0) 
                    {
                        if (!sm.Engine.CopyToUser(bufAddr, buf.AsSpan(0, n)))
                            return -(int)Errno.EFAULT;
                    }
                    return n;
                }
                
                return n; // Other error
            }
            catch (Exception ex) 
            { 
                Console.WriteLine($"SysRead Exception: {ex}");
                return -(int)Errno.EIO; 
            }
        }
    }

    internal static async ValueTask<int> SysWrite(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        int fd = (int)a1;
        uint bufAddr = a2;
        int count = (int)a3;

        var f = sm.GetFD(fd);
        if (f == null) return -(int)Errno.EBADF;

        // Verify read access to buffer before writing
        var data = new byte[count];
        if (!sm.Engine.CopyFromUser(bufAddr, data))
            return -(int)Errno.EFAULT;

        while (true)
        {
            try
            {
                int n = f.Write(data);
                
                if (n == -(int)Errno.EAGAIN)
                {
                    if ((f.Flags & FileFlags.O_NONBLOCK) != 0) return -(int)Errno.EAGAIN;
                    
                    // Blocking Write
                    var currentTask = sm.Engine.Owner as FiberTask;
                    if (currentTask == null) return -(int)Errno.EAGAIN; 

                    // Wait for write or interrupt
                    var tcs = new TaskCompletionSource<bool>();
                    currentTask.RegisterBlockingSyscall(() => tcs.TrySetResult(false)); 
                    
                    try
                    {
                        var waitTask = f.WaitForWrite().AsTask(); // Allocation! Optimize later
                        var interruptTask = tcs.Task;
                        
                        var finishedTask = await System.Threading.Tasks.Task.WhenAny(waitTask, interruptTask);
                        
                        if (finishedTask == interruptTask)
                        {
                            return -(int)Errno.EINTR;
                        }
                        
                        // File is ready, retry write
                        continue;
                    }
                    finally
                    {
                        currentTask.ClearInterrupt();
                    }
                }

                return n;
            }
            catch { return -(int)Errno.EIO; }
        }
    }



    private static async ValueTask<int> SysLseek(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
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

    private static async ValueTask<int> SysLlseek(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -1;
        int fd = (int)a1;
        long offset = ((long)a2 << 32) | a3;
        uint resultPtr = a4;
        int whence = (int)a5;

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
        
        if (!sm.Engine.CopyToUser(resultPtr, BitConverter.GetBytes(newPos)))
            return -(int)Errno.EFAULT;

        return 0;
    }

    private static async ValueTask<int> SysClose(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -1;
        sm.FreeFD((int)a1);
        return 0;
    }

    private static async ValueTask<int> SysBrk(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -1;
        uint newBrk = a1;
        if (newBrk == 0) return (int)sm.BrkAddr;

        if (newBrk > sm.BrkAddr)
        {
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



    private static async ValueTask<int> SysFork(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        // fork = clone(0, 0, NULL, NULL, NULL) - no flags, copy everything
        return await SysClone(state, 0, 0, 0, 0, 0, 0);
    }

    private static async ValueTask<int> SysVfork(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        // vfork = clone(CLONE_VM | CLONE_VFORK, 0, NULL, NULL, NULL)
        return await SysClone(state, LinuxConstants.CLONE_VM | LinuxConstants.CLONE_VFORK, 0, 0, 0, 0, 0);
    }

    private static async ValueTask<int> SysFutex(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.ENOSYS;

        uint uaddr = a1;
        int op = (int)a2;
        uint val = a3;

        int opCode = op & 0x7F;

        if (opCode == 0) // WAIT
        {
            byte[] tidBuf = new byte[4];
            if (!sm.Engine.CopyFromUser(uaddr, tidBuf)) return -(int)Errno.EFAULT;
            uint currentVal = BinaryPrimitives.ReadUInt32LittleEndian(tidBuf);
            if (currentVal != val) return -(int)Errno.EAGAIN; // EWOULDBLOCK

            var waiter = sm.Futex.PrepareWait(uaddr);

            var task = (sm.Engine.Owner as FiberTask);
            if (task != null)
            {
                task.RegisterBlockingSyscall(() => waiter.Tcs.TrySetResult(false));
                try
                {
                    if (!await waiter.Tcs.Task) return -(int)Errno.EINTR;
                }
                finally { task.ClearInterrupt(); }
            }
            return 0;
        }
        else if (opCode == 1) // WAKE
        {
            int count = (int)val;
            return sm.Futex.Wake(uaddr, count);
        }

        return -(int)Errno.ENOSYS;
    }

    private static async ValueTask<int> SysSetThreadArea(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        uint uInfoAddr = a1;
        byte[] buf = new byte[16];
        if (!sm.Engine.CopyFromUser(uInfoAddr, buf)) return -(int)Errno.EFAULT;

        uint entry = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(0, 4));
        uint baseAddr = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(4, 4));

        Logger.LogInformation($"[SysSetThreadArea] Entry={entry} Base={baseAddr:X}");

        sm.Engine.SetSegBase(Seg.GS, baseAddr);

        if (entry == 0xFFFFFFFF)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0, 4), 12);
            if (!sm.Engine.CopyToUser(uInfoAddr, buf.AsSpan(0, 4))) return -(int)Errno.EFAULT;
        }

        return 0;
    }

    private static async ValueTask<int> SysSetTidAddress(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        if (sm.GetTID != null) return sm.GetTID(sm.Engine);
        return 1;
    }

    private static async ValueTask<int> SysUname(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var t = (sm.Engine.Owner as FiberTask);
        if (t == null) return -(int)Errno.EPERM;

        var uts = t.Process.UTS;

        void WriteUnameString(uint addr, string s)
        {
            byte[] buf = new byte[65];
            var bytes = System.Text.Encoding.ASCII.GetBytes(s);
            Array.Copy(bytes, buf, Math.Min(bytes.Length, 64));
            if (!sm.Engine.CopyToUser(addr, buf)) return;
        }

        WriteUnameString(a1, uts.SysName);
        WriteUnameString(a1 + 65, uts.NodeName);
        WriteUnameString(a1 + 130, uts.Release);
        WriteUnameString(a1 + 195, uts.Version);
        WriteUnameString(a1 + 260, uts.Machine);
        WriteUnameString(a1 + 325, uts.DomainName);

        return 0;
    }

    private static async ValueTask<int> SysSysinfo(IntPtr state, uint sysinfoAddr, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        
        var t = (sm.Engine.Owner as FiberTask);
        if (t == null) return -(int)Errno.EPERM;

        var info = new SysInfo();
        info.Uptime = (int)((DateTime.UtcNow - System.Diagnostics.Process.GetCurrentProcess().StartTime).TotalSeconds);
        info.Loads = new int[] { 65536, 65536, 65536 };
        info.TotalRam = 256 * 1024 * 1024;
        info.FreeRam = 128 * 1024 * 1024;
        info.SharedRam = 0;
        info.BufferRam = 0;
        info.TotalSwap = 0;
        info.FreeSwap = 0;
        info.Procs = 1; // Simplified for now: current processes count is not easily accessible via public API without listing.
        info.TotalHigh = 0;
        info.FreeHigh = 0;
        info.MemUnit = 1;
        info.Padding = new byte[8];

        if (sysinfoAddr != 0)
        {
            int size = Marshal.SizeOf<SysInfo>();
            byte[] buffer = new byte[size];
            IntPtr ptr = Marshal.AllocHGlobal(size);
            try {
                Marshal.StructureToPtr(info, ptr, false);
                Marshal.Copy(ptr, buffer, 0, size);
            } finally {
                Marshal.FreeHGlobal(ptr);
            }
            if (!sm.Engine.CopyToUser(sysinfoAddr, buffer)) return -(int)Errno.EFAULT;
        }
        
        return 0;
    }

    private static async ValueTask<int> SysSignal(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        return 0;
    }

    private static async ValueTask<int> SysGetUid(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        var t = (sm?.Engine.Owner as FiberTask);
        return t?.Process.UID ?? 0;
    }

    private static async ValueTask<int> SysGetEUid(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        var t = (sm?.Engine.Owner as FiberTask);
        return t?.Process.EUID ?? 0;
    }

    private static async ValueTask<int> SysGetGid(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        var t = (sm?.Engine.Owner as FiberTask);
        return t?.Process.GID ?? 0;
    }

    private static async ValueTask<int> SysGetEGid(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        var t = (sm?.Engine.Owner as FiberTask);
        return t?.Process.EGID ?? 0;
    }

    private static async ValueTask<int> SysSetUid(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        var t = (sm?.Engine.Owner as FiberTask);
        if (t != null)
        {
            t.Process.UID = t.Process.EUID = t.Process.SUID = t.Process.FSUID = (int)a1;
        }
        return 0;
    }

    private static async ValueTask<int> SysSetGid(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        var t = (sm?.Engine.Owner as FiberTask);
        if (t != null)
        {
            t.Process.GID = t.Process.EGID = t.Process.SGID = t.Process.FSGID = (int)a1;
        }
        return 0;
    }

    private static async ValueTask<int> SysGetUid32(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6) => await SysGetUid(state, a1, a2, a3, a4, a5, a6);
    private static async ValueTask<int> SysGetGid32(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6) => await SysGetGid(state, a1, a2, a3, a4, a5, a6);
    private static async ValueTask<int> SysGetEUid32(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6) => await SysGetEUid(state, a1, a2, a3, a4, a5, a6);
    private static async ValueTask<int> SysGetEGid32(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6) => await SysGetEGid(state, a1, a2, a3, a4, a5, a6);
    private static async ValueTask<int> SysSetUid32(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6) => await SysSetUid(state, a1, a2, a3, a4, a5, a6);
    private static async ValueTask<int> SysSetGid32(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6) => await SysSetGid(state, a1, a2, a3, a4, a5, a6);

    private static async ValueTask<int> SysSetReUid(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        var t = (sm?.Engine.Owner as FiberTask);
        if (t != null)
        {
            if (a1 != 0xFFFFFFFF) t.Process.UID = (int)a1;
            if (a2 != 0xFFFFFFFF) t.Process.EUID = (int)a2;
            t.Process.SUID = t.Process.EUID;
            t.Process.FSUID = t.Process.EUID;
        }
        return 0;
    }

    private static async ValueTask<int> SysSetReGid(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        var t = (sm?.Engine.Owner as FiberTask);
        if (t != null)
        {
            if (a1 != 0xFFFFFFFF) t.Process.GID = (int)a1;
            if (a2 != 0xFFFFFFFF) t.Process.EGID = (int)a2;
            t.Process.SGID = t.Process.EGID;
            t.Process.FSGID = t.Process.EGID;
        }
        return 0;
    }

    private static async ValueTask<int> SysSetResUid(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        var t = (sm?.Engine.Owner as FiberTask);
        if (t != null)
        {
            if (a1 != 0xFFFFFFFF) t.Process.UID = (int)a1;
            if (a2 != 0xFFFFFFFF) t.Process.EUID = (int)a2;
            if (a3 != 0xFFFFFFFF) t.Process.SUID = (int)a3;
            t.Process.FSUID = t.Process.EUID;
        }
        return 0;
    }

    private static async ValueTask<int> SysGetResUid(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        var t = (sm.Engine.Owner as FiberTask);
        if (t != null && sm != null)
        {
            if (!sm.Engine.CopyToUser(a1, BitConverter.GetBytes(t.Process.UID))) return -(int)Errno.EFAULT;
            if (!sm.Engine.CopyToUser(a2, BitConverter.GetBytes(t.Process.EUID))) return -(int)Errno.EFAULT;
            if (!sm.Engine.CopyToUser(a3, BitConverter.GetBytes(t.Process.SUID))) return -(int)Errno.EFAULT;
        }
        return 0;
    }

    private static async ValueTask<int> SysSetResGid(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        var t = (sm?.Engine.Owner as FiberTask);
        if (t != null)
        {
            if (a1 != 0xFFFFFFFF) t.Process.GID = (int)a1;
            if (a2 != 0xFFFFFFFF) t.Process.EGID = (int)a2;
            if (a3 != 0xFFFFFFFF) t.Process.SGID = (int)a3;
            t.Process.FSGID = t.Process.EGID;
        }
        return 0;
    }

    private static async ValueTask<int> SysGetResGid(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        var t = (sm.Engine.Owner as FiberTask);
        if (t != null && sm != null)
        {
            if (!sm.Engine.CopyToUser(a1, BitConverter.GetBytes(t.Process.GID))) return -(int)Errno.EFAULT;
            if (!sm.Engine.CopyToUser(a2, BitConverter.GetBytes(t.Process.EGID))) return -(int)Errno.EFAULT;
            if (!sm.Engine.CopyToUser(a3, BitConverter.GetBytes(t.Process.SGID))) return -(int)Errno.EFAULT;
        }
        return 0;
    }

    private static async ValueTask<int> SysSetFsUid(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        var t = (sm?.Engine.Owner as FiberTask);
        if (t != null)
        {
            int old = t.Process.FSUID;
            t.Process.FSUID = (int)a1;
            return old;
        }
        return 0;
    }

    private static async ValueTask<int> SysSetFsGid(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        var t = (sm?.Engine.Owner as FiberTask);
        if (t != null)
        {
            int old = t.Process.FSGID;
            t.Process.FSGID = (int)a1;
            return old;
        }
        return 0;
    }

    private static async ValueTask<int> SysChmod(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        string path = sm.ReadString(a1);
        uint mode = a2;

        var dentry = sm.PathWalk(path);
        if (dentry == null || dentry.Inode == null) return -2; // ENOENT

        // Permission check: only owner or root can chmod
        var t = (sm.Engine.Owner as FiberTask);
        if (t != null && t.Process.EUID != 0 && t.Process.EUID != dentry.Inode.Uid)
            return -(int)Errno.EPERM;

        // Only modify permission bits (lower 12 bits: rwx for user/group/other + special bits)
        dentry.Inode.Mode = (int)(mode & 0xFFF);
        dentry.Inode.CTime = DateTime.Now;
        return 0;
    }
    private static async ValueTask<int> SysFchmod(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        int fd = (int)a1;
        uint mode = a2;

        var f = sm.GetFD(fd);
        if (f == null || f.Dentry.Inode == null) return -9; // EBADF

        // Permission check
        var t = (sm.Engine.Owner as FiberTask);
        if (t != null && t.Process.EUID != 0 && t.Process.EUID != f.Dentry.Inode.Uid)
            return -(int)Errno.EPERM;

        f.Dentry.Inode.Mode = (int)(mode & 0xFFF);
        f.Dentry.Inode.CTime = DateTime.Now;
        return 0;
    }
    private static async ValueTask<int> SysChown(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        string path = sm.ReadString(a1);
        int uid = (int)a2;
        int gid = (int)a3;

        var dentry = sm.PathWalk(path);
        if (dentry == null || dentry.Inode == null) return -2; // ENOENT

        // Permission check: only root can chown
        var t = (sm.Engine.Owner as FiberTask);
        if (t != null && t.Process.EUID != 0)
            return -1; // EPERM

        // -1 means "don't change"
        if (uid != -1) dentry.Inode.Uid = uid;
        if (gid != -1) dentry.Inode.Gid = gid;
        dentry.Inode.CTime = DateTime.Now;
        return 0;
    }
    private static async ValueTask<int> SysFchown(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        int fd = (int)a1;
        int uid = (int)a2;
        int gid = (int)a3;

        var f = sm.GetFD(fd);
        if (f == null || f.Dentry.Inode == null) return -9; // EBADF

        // Permission check: only root can chown
        var t = (sm.Engine.Owner as FiberTask);
        if (t != null && t.Process.EUID != 0)
            return -1; // EPERM

        if (uid != -1) f.Dentry.Inode.Uid = uid;
        if (gid != -1) f.Dentry.Inode.Gid = gid;
        f.Dentry.Inode.CTime = DateTime.Now;
        return 0;
    }
    private static async ValueTask<int> SysLchown(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        // Since we don't have symlinks yet, behave like chown
        return await SysChown(state, a1, a2, a3, a4, a5, a6);
    }
    private static async ValueTask<int> SysSetGroups(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6) => 0;
    private static async ValueTask<int> SysGetGroups(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6) => 0;


    private static async ValueTask<int> SysGetCwd(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
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

        if (!sm.Engine.CopyToUser(bufAddr, System.Text.Encoding.ASCII.GetBytes(cwd + "\0"))) return -(int)Errno.EFAULT;
        return cwd.Length + 1;
    }

    private static async ValueTask<int> SysWriteV(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        int fd = (int)a1;
        uint iovAddr = a2;
        int iovCnt = (int)a3;

        var f = sm.GetFD(fd);
        if (f == null) return -(int)Errno.EBADF;

        int total = 0;
        for (int i = 0; i < iovCnt; i++)
        {
            byte[] iovBuf = new byte[8];
            if (!sm.Engine.CopyFromUser(iovAddr + (uint)i * 8, iovBuf)) return -(int)Errno.EFAULT;
            uint baseAddr = BinaryPrimitives.ReadUInt32LittleEndian(iovBuf);
            uint len = BinaryPrimitives.ReadUInt32LittleEndian(iovBuf.AsSpan(4));

            if (len > 0)
            {
                byte[] data = new byte[len];
                if (!sm.Engine.CopyFromUser(baseAddr, data)) return -(int)Errno.EFAULT;
                f.Write(data);
                total += (int)len;
            }
        }
        return total;
    }

    private static async ValueTask<int> SysMmap2(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
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

    private static async ValueTask<int> SysMunmap(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        sm.Engine.InvalidateRange(a1, a2);
        sm.Mem.Munmap(a1, a2, sm.Engine);
        return 0;
    }

    private static async ValueTask<int> SysMprotect(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        uint addr = a1;
        uint len = a2;
        Protection prot = (Protection)a3;

        // Invalidate cache since permissions are changing (e.g. RW -> RX)
        sm.Engine.InvalidateRange(addr, len);

        var vmas = sm.Mem.FindVMAsInRange(addr, addr + len);
        foreach (var vma in vmas)
        {
            // Update permissions in VMA manager
            vma.Perms = prot;

            // Update permissions in native MMU for already mapped pages
            for (uint p = Math.Max(vma.Start, addr); p < Math.Min(vma.End, addr + len); p += 4096)
            {
                if (sm.Engine.IsDirty(p)) // Check if mapped/present using a proxy
                {
                    // Actually we need a way to update native perms without re-mapping?
                    // For now, MemMap will update perms in native MMU
                    sm.Engine.MemMap(p, 4096, (byte)prot);
                }
            }
        }

        return 0;
    }

    private static async ValueTask<int> SysGetPid(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm?.GetTGID != null) return sm.GetTGID(sm.Engine);
        return 1000;
    }

    private static async ValueTask<int> SysGetPPid(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        var task = (sm.Engine.Owner as FiberTask);
        if (task == null) return -(int)Errno.EPERM;

        return task.Process.PPID;
    }

    private static async ValueTask<int> SysGettid(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        var task = (sm?.Engine.Owner as FiberTask);
        return task?.TID ?? -1;
    }

    private static async ValueTask<int> SysGetpgid(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        var task = (sm?.Engine.Owner as FiberTask);
        // Simple PGID = TGID for now
        return task?.Process.TGID ?? -1;
    }

    private static async ValueTask<int> SysUmask(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        var task = (sm?.Engine.Owner as FiberTask);
        if (task == null) return 0;
        int old = task.Process.Umask;
        task.Process.Umask = (int)(a1 & 0x1FF);
        return old;
    }

    private static async ValueTask<int> SysSethostname(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -1;
        var task = (sm.Engine.Owner as FiberTask);
        if (task == null || task.Process.EUID != 0) return -(int)Errno.EPERM;

        string name = sm.ReadString(a1);
        task.Process.UTS.NodeName = name;
        return 0;
    }

    private static async ValueTask<int> SysSetdomainname(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -1;
        var task = (sm.Engine.Owner as FiberTask);
        if (task == null || task.Process.EUID != 0) return -(int)Errno.EPERM;

        string name = sm.ReadString(a1);
        task.Process.UTS.DomainName = name;
        return 0;
    }

    private static async ValueTask<int> SysSchedYield(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        sm?.Engine.Yield();
        return 0;
    }

    private static async ValueTask<int> SysPause(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        sm?.Engine.Yield();
        return 0;
    }

    private static async ValueTask<int> SysSync(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -1;
        foreach (var file in sm.FDs.Values)
        {
            file?.Sync();
        }
        return 0;
    }

    private static async ValueTask<int> SysFsync(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -1;
        var file = sm.GetFD((int)a1);
        if (file == null) return -(int)Errno.EBADF;
        file.Sync();
        return 0;
    }

    private static async ValueTask<int> SysFdatasync(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        return await SysFsync(state, a1, a2, a3, a4, a5, a6);
    }

    private static async ValueTask<int> SysMadvise(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        return 0; // No-op
    }

    private static async ValueTask<int> SysMsync(IntPtr state, uint addr, uint len, uint flags, uint a4, uint a5, uint a6)
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

    private static async ValueTask<int> SysWait4(IntPtr state, uint pid, uint statusPtr, uint options, uint rusagePtr, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        
        int pidVal = (int)pid;
        int optVal = (int)options;
        bool hang = (optVal & 1) != 0; // WNOHANG
        
        var fiberTask = sm.Engine.Owner as FiberTask;
        if (fiberTask == null) return -(int)Errno.ECHILD;
        
        var currentProc = fiberTask.Process;
        var kernel = KernelScheduler.Instance;
        
        // Loop for retrying wait
        while (true)
        {
            // Scan children
            bool hasChildren = false;
            
            List<Process> candidates = new();
            
            // Iterate over copy to allow modification if needed? No, removing from dict is what matters.
            // currentProc.Children is List<int>.
            // We can iterate it directly.
            
            foreach (var childPid in currentProc.Children)
            {
                var childProc = kernel.GetProcess(childPid);
                if (childProc == null) continue; 
                
                bool match = false;
                if (pidVal == -1) match = true;
                else if (pidVal > 0) match = childPid == pidVal;
                else if (pidVal == 0) match = childProc.PGID == currentProc.PGID;
                else match = childProc.PGID == -pidVal;
                
                if (match)
                {
                    hasChildren = true;
                    if (childProc.State == ProcessState.Zombie)
                    {
                        // Found REAPABLE child
                        if (statusPtr != 0)
                        {
                            byte[] stBuf = new byte[4];
                            BinaryPrimitives.WriteInt32LittleEndian(stBuf, childProc.ExitStatus);
                            if (!sm.Engine.CopyToUser(statusPtr, stBuf)) return -(int)Errno.EFAULT;
                        }
                        
                        // Reap
                        currentProc.Children.Remove(childPid);
                        // Also remove from global table? Or let it be garbage collected if no other refs?
                        // If we remove from global table, PID can be reused properly (if allocator reuses).
                        // Current allocator is monotonic increment.
                        
                        return childPid;
                    }
                    candidates.Add(childProc);
                }
            }
            
            if (!hasChildren) return -(int)Errno.ECHILD;
            
            if (hang) return 0;
            
            // Block until ONE of candidates becomes zombie
            // Use TaskCompletionSource and register on all candidates
            var tcs = new TaskCompletionSource<bool>();
            
            Action continuation = () => tcs.TrySetResult(true);
            
            foreach (var c in candidates)
            {
                c.ZombieEvent.Register(continuation);
            }
            
            fiberTask.RegisterBlockingSyscall(() => {
                tcs.TrySetResult(false); // Interrupted
            });
            
            try
            {
                bool success = await tcs.Task;
                if (!success) return -(int)Errno.EINTR;
            }
            finally
            {
                fiberTask.ClearInterrupt();
            }
        }
    }




    private static async ValueTask<int> SysWaitPid(IntPtr state, uint pid, uint statusPtr, uint options, uint a4, uint a5, uint a6)
    {
        // waitpid(pid, status, options) = wait4(pid, status, options, NULL)
        return await SysWait4(state, pid, statusPtr, options, 0, 0, 0);
    }

    private static async ValueTask<int> SysWaitId(IntPtr state, uint idtype, uint id, uint infop, uint options, uint rusagePtr, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        var fiberTask = sm.Engine.Owner as FiberTask;
        if (fiberTask == null) return -(int)Errno.ECHILD;

        // Logic similar to SysWait4 but with idtype
        var currentProc = fiberTask.Process;
        var kernel = KernelScheduler.Instance;
        
        bool wnohang = ((int)options & 1) != 0;
        bool wexited = ((int)options & 4) != 0;
        // Other flags ignored for now
        if (options == 0) wexited = true; // Default?

        while (true)
        {
            List<Process> candidates = new();
            bool hasChildren = false;

            foreach (var childPid in currentProc.Children)
            {
                var childProc = kernel.GetProcess(childPid);
                if (childProc == null) continue;
                
                bool match = false;
                if ((Bifrost.Core.IdType)idtype == Bifrost.Core.IdType.P_ALL) match = true;
                else if ((Bifrost.Core.IdType)idtype == Bifrost.Core.IdType.P_PID && childPid == (int)id) match = true;
                else if ((Bifrost.Core.IdType)idtype == Bifrost.Core.IdType.P_PGID && childProc.PGID == (int)id) match = true;
                
                if (match)
                {
                    hasChildren = true;
                    if (wexited && childProc.State == ProcessState.Zombie)
                    {
                        // Found
                         if (infop != 0)
                        {
                            var info = new SigInfo();
                            info.si_signo = 17; // SIGCHLD
                            info.si_pid = childProc.TGID;
                            info.si_status = childProc.ExitStatus;
                            info.si_code = 1; // CLD_EXITED
                            
                            if (!WriteSigInfo(sm, infop, info)) return -(int)Errno.EFAULT;
                        }
                        
                        // Waitid keeps child unless WNOWAIT? 
                        // "waitid()... If WNOWAIT is set... leave the child in a waitable state"
                        bool wnowait = ((int)options & 0x01000000) != 0;
                        if (!wnowait)
                        {
                            lock(currentProc.Children) currentProc.Children.Remove(childPid);
                        }
                        
                        return 0; // Success
                    }
                    candidates.Add(childProc);
                }
            }
            
            if (!hasChildren) return -(int)Errno.ECHILD;
            if (wnohang) return 0;
            
            // Block
            var tcs = new TaskCompletionSource<bool>();
            Action continuation = () => tcs.TrySetResult(true);
            
            foreach (var c in candidates)
            {
                c.ZombieEvent.Register(continuation);
            }
            
            fiberTask.RegisterBlockingSyscall(() => {
                tcs.TrySetResult(false);
            });
            
            try
            {
                bool success = await tcs.Task;
                if (!success) return -(int)Errno.EINTR;
            }
            finally
            {
                fiberTask.ClearInterrupt();
            }
        }
    }

    private static bool WriteSigInfo(SyscallManager sm, uint addr, SigInfo info)
    {
        var buf = new byte[128];
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(0, 4), info.si_signo);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(4, 4), info.si_errno);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(8, 4), info.si_code);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(12, 4), info.si_pid);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(16, 4), info.si_uid);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(20, 4), info.si_status);
        return sm.Engine.CopyToUser(addr, buf);
    }

    private static async ValueTask<int> SysUnlink(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        string path = sm.ReadString(a1);

        int lastSlash = path.LastIndexOf('/');
        string parentPath = lastSlash == -1 ? "." : (lastSlash == 0 ? "/" : path.Substring(0, lastSlash));
        string name = lastSlash == -1 ? path : path.Substring(lastSlash + 1);

        var parentDentry = sm.PathWalk(parentPath);
        if (parentDentry == null) return -(int)Errno.ENOENT;

        try
        {
            parentDentry.Inode!.Unlink(name);
            parentDentry.Children.Remove(name);
            return 0;
        }
        catch { return -(int)Errno.ENOENT; }
    }

    private static async ValueTask<int> SysAccess(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -1;
        string path = sm.ReadString(a1);
        var dentry = sm.PathWalk(path);
        if (dentry != null && dentry.Inode != null) return 0;
        return -(int)Errno.ENOENT;
    }

    private static async ValueTask<int> SysIoctl(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        return 0;
    }

    private static async ValueTask<int> SysGetdents64(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        int fd = (int)a1;
        uint bufAddr = a2;
        int count = (int)a3;

        var f = sm.GetFD(fd);
        if (f == null || f.Dentry.Inode == null) return -(int)Errno.EBADF;

        try
        {
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

                if (!sm.Engine.CopyToUser(baseAddr, buf)) return -(int)Errno.EFAULT;
                writeOffset += recLen;
                f.Position = i + 1;
            }

            return writeOffset;
        }
        catch { return -(int)Errno.EPERM; }
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
        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(56), (ulong)((size + 511) / 512));

        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(64), (uint)new DateTimeOffset(inode.ATime).ToUnixTimeSeconds());
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(72), (uint)new DateTimeOffset(inode.MTime).ToUnixTimeSeconds());
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(80), (uint)new DateTimeOffset(inode.CTime).ToUnixTimeSeconds());
        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(88), inode.Ino);

        if (!sm.Engine.CopyToUser(addr, buf)) return;
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
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(28), (uint)((size + 511) / 512));

        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(32), (uint)new DateTimeOffset(inode.ATime).ToUnixTimeSeconds());
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(40), (uint)new DateTimeOffset(inode.MTime).ToUnixTimeSeconds());
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(48), (uint)new DateTimeOffset(inode.CTime).ToUnixTimeSeconds());

        if (!sm.Engine.CopyToUser(addr, buf)) return;
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

        Action<int, DateTime> writeTime = (offset, dt) =>
        {
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

        if (!sm.Engine.CopyToUser(addr, buf)) return;
    }

    private static async ValueTask<int> SysStat64(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        return ImplStat64(state, a1, a2);
    }

    private static async ValueTask<int> SysLstat64(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        return ImplStat64(state, a1, a2);
    }

    private static int ImplStat64(IntPtr state, uint ptrPath, uint ptrStat)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        string? path = sm.Engine.ReadStringSafe(ptrPath);
        if (path == null) return -(int)Errno.EFAULT;

        Logger.LogInformation($"[Stat64] Path='{path}'");
        var dentry = sm.PathWalk(path);
        if (dentry == null || dentry.Inode == null) 
        {
            Logger.LogWarning($"[Stat64] PathWalk failed for '{path}'");
            return -(int)Errno.ENOENT;
        }

        WriteStat64(sm, ptrStat, dentry.Inode);
        return 0;
    }

    private static async ValueTask<int> SysGetTimeOfDay(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        uint tvPtr = a1;
        uint tzPtr = a2;

        // Use UtcNow for REALTIME (gettimeofday is strictly REALTIME)
        long ticks = DateTime.UtcNow.Ticks - DateTime.UnixEpoch.Ticks;
        long secs = ticks / TimeSpan.TicksPerSecond;
        long usecs = (ticks % TimeSpan.TicksPerSecond) / 10; // 100ns -> 1us

        if (tvPtr != 0)
        {
            byte[] buf = new byte[8];
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(0, 4), (int)secs);
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(4, 4), (int)usecs);
            if (!sm.Engine.CopyToUser(tvPtr, buf)) return -(int)Errno.EFAULT;
        }

        return 0;
    }

    private static async ValueTask<int> SysClockGetTime(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        int clockId = (int)a1;
        uint tsPtr = a2;

        long secs;
        long nsecs;

        if (clockId == LinuxConstants.CLOCK_REALTIME)
        {
             long ticks = DateTime.UtcNow.Ticks - DateTime.UnixEpoch.Ticks;
             secs = ticks / TimeSpan.TicksPerSecond;
             nsecs = (ticks % TimeSpan.TicksPerSecond) * 100;
        }
        else
        {
            // CLOCK_MONOTONIC and others
            // Use Stopwatch for high precision
            long freq = System.Diagnostics.Stopwatch.Frequency;
            long ticks = System.Diagnostics.Stopwatch.GetTimestamp();
            
            secs = ticks / freq;
            nsecs = (ticks % freq) * 1000000000 / freq;
        }

        byte[] buf = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(0, 4), (int)secs);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(4, 4), (int)nsecs);
        if (!sm.Engine.CopyToUser(tsPtr, buf)) return -(int)Errno.EFAULT;

        return 0;
    }

    private static async ValueTask<int> SysClockGetTime64(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        int clockId = (int)a1;
        uint tsPtr = a2;

        long secs;
        long nsecs;

        if (clockId == LinuxConstants.CLOCK_REALTIME)
        {
             long ticks = DateTime.UtcNow.Ticks - DateTime.UnixEpoch.Ticks;
             secs = ticks / TimeSpan.TicksPerSecond;
             nsecs = (ticks % TimeSpan.TicksPerSecond) * 100;
        }
        else
        {
            // CLOCK_MONOTONIC and others
            long freq = System.Diagnostics.Stopwatch.Frequency;
            long ticks = System.Diagnostics.Stopwatch.GetTimestamp();
            
            secs = ticks / freq;
            nsecs = (ticks % freq) * 1000000000 / freq;
        }

        byte[] buf = new byte[12];
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(0, 8), secs);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(8, 4), (int)nsecs);
        if (!sm.Engine.CopyToUser(tsPtr, buf)) return -(int)Errno.EFAULT;

        return 0;
    }

    private static async ValueTask<int> SysFstat64(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        int fd = (int)a1;
        var f = sm.GetFD(fd);
        if (f == null || f.Dentry.Inode == null) return -(int)Errno.EBADF;

        WriteStat64(sm, a2, f.Dentry.Inode);
        return 0;
    }

    private static async ValueTask<int> SysSymlink(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
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

        var t = (sm.Engine.Owner as FiberTask);
        int uid = t?.Process.EUID ?? 0;
        int gid = t?.Process.EGID ?? 0;

        try
        {
            var dentry = new Dentry(name, null, parentDentry, parentDentry.SuperBlock);
            parentDentry.Inode.Symlink(dentry, target, uid, gid);
            return 0;
        }
        catch { return -(int)Errno.EACCES; }
    }

    private static async ValueTask<int> SysReadlink(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
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
        if (!sm.Engine.CopyToUser(bufAddr, bytes.AsSpan(0, len))) return -(int)Errno.EFAULT;
        return len;
    }

    private static async ValueTask<int> SysReadlinkAt(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
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
        if (!sm.Engine.CopyToUser(bufAddr, bytes.AsSpan(0, len))) return -(int)Errno.EFAULT;
        return len;
    }

    private static async ValueTask<int> SysSymlinkAt(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
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

        var t = (sm.Engine.Owner as FiberTask);
        int uid = t?.Process.EUID ?? 0;
        int gid = t?.Process.EGID ?? 0;

        try
        {
            var dentry = new Dentry(name, null, parentDentry, parentDentry.SuperBlock);
            parentDentry.Inode.Symlink(dentry, target, uid, gid);
            return 0;
        }
        catch { return -(int)Errno.EACCES; }
    }

    private static async ValueTask<int> SysRtSigAction(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        // a1: sig, a2: new_sa, a3: old_sa, a4: sigsetsize
        int sig = (int)a1;
        uint newSaPtr = a2;
        uint oldSaPtr = a3;
        uint sigsetsize = a4;

        if (sigsetsize != 8) return -(int)Errno.EINVAL;

        var sm = Get(state);
        var task = (sm.Engine.Owner as FiberTask);
        if (sm == null || task == null) return -(int)Errno.EPERM;

        if (sig < 1 || sig > 64) return -(int)Errno.EINVAL;

        // Save old action
        if (oldSaPtr != 0)
        {
            if (task.Process.SignalActions.TryGetValue(sig, out var oldSa))
            {
                byte[] buf = new byte[20];
                BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0, 4), oldSa.Handler);
                BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4, 4), oldSa.Flags);
                BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(8, 4), oldSa.Restorer);
                BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(12, 8), oldSa.Mask);
                if (!sm.Engine.CopyToUser(oldSaPtr, buf)) return -(int)Errno.EFAULT;
            }
            else
            {
                if (!sm.Engine.CopyToUser(oldSaPtr, new byte[20])) return -(int)Errno.EFAULT;
            }
        }

        if (newSaPtr != 0)
        {
            if (sig == 9 || sig == 19) return -(int)Errno.EINVAL; // Cannot catch SIGKILL or SIGSTOP

            byte[] buf = new byte[20];
            if (!sm.Engine.CopyFromUser(newSaPtr, buf)) return -(int)Errno.EFAULT;
            var sa = new SigAction
            {
                Handler = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(0, 4)),
                Flags = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(4, 4)),
                Restorer = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(8, 4)),
                Mask = BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(12, 8))
            };
            task.Process.SignalActions[sig] = sa;
        }

        return 0;
    }

    private static async ValueTask<int> SysRtSigProcMask(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        // a1: how, a2: set, a3: oldset, a4: sigsetsize
        int how = (int)a1;
        uint setPtr = a2;
        uint oldSetPtr = a3;
        uint sigsetsize = a4;

        if (sigsetsize != 8) return -(int)Errno.EINVAL;

        var task = (sm.Engine.Owner as FiberTask);
        if (task == null) return -(int)Errno.EPERM;

        if (oldSetPtr != 0)
        {
            byte[] buf = new byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(buf, task.SignalMask);
            if (!task.CPU.CopyToUser(oldSetPtr, buf)) return -(int)Errno.EFAULT;
        }

        if (setPtr != 0)
        {
            byte[] setBuf = new byte[8];
            if (!task.CPU.CopyFromUser(setPtr, setBuf)) return -(int)Errno.EFAULT;
            ulong set = BinaryPrimitives.ReadUInt64LittleEndian(setBuf);

            // SIGKILL and SIGSTOP cannot be blocked
            set &= ~(1UL << 8); // SIGKILL (9) - 1 bit shift
            set &= ~(1UL << 18); // SIGSTOP (19)

            switch (how)
            {
                case (int)SigProcMaskAction.SIG_BLOCK:
                    task.SignalMask |= set;
                    break;
                case (int)SigProcMaskAction.SIG_UNBLOCK:
                    task.SignalMask &= ~set;
                    break;
                case (int)SigProcMaskAction.SIG_SETMASK:
                    task.SignalMask = set;
                    break;
                default:
                    return -(int)Errno.EINVAL;
            }
        }

        return 0;
    }

    private static async ValueTask<int> SysKill(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var task = (sm.Engine.Owner as FiberTask);
        if (task == null) return -(int)Errno.EPERM;

        int pid = (int)a1;
        int sig = (int)a2;

        if (sig < 0 || sig > 64) return -(int)Errno.EINVAL;

        // Simplified: only support current process/group for now or direct PID match
        // Kill 0: current process group. Kill -1: all processes. Kill < -1: process group -pid.
        
        List<FiberTask> targets = new();

        if (pid > 0)
        {
            var proc = KernelScheduler.Instance.GetProcess(pid);
            if (proc != null)
            {
                lock(proc.Threads)
                {
                    if (proc.Threads.Count > 0)
                        targets.Add(proc.Threads[0]); // Target main/first thread
                }
            }
            else
            {
                // Might be a TID?
                var t = KernelScheduler.Instance.GetTask(pid);
                if (t != null) targets.Add(t);
            }
        }
        else if (pid == 0) // Current process group
        {
             // Simplified: Signal current process
             lock(task.Process.Threads)
             {
                 if (task.Process.Threads.Count > 0) targets.Add(task.Process.Threads[0]);
             }
        }
        else if (pid == -1)
        {
            // All processes. Dangerous!
            return -(int)Errno.EPERM; 
        }
        else // pid < -1: PGRP = -pid
        {
             // Not implemented PGRP signaling yet.
             return -(int)Errno.ESRCH;
        }

        if (targets.Count == 0) return -(int)Errno.ESRCH;

        foreach (var t in targets)
        {
            t.HandleSignal(sig);
        }

        return 0;      
    }



    private static async ValueTask<int> SysTkill(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var task = sm.Engine.Owner as FiberTask;
        if (task == null) return -(int)Errno.EPERM;

        int tid = (int)a1;
        int sig = (int)a2;

        if (sig < 0 || sig > 64) return -(int)Errno.EINVAL;

        var target = KernelScheduler.Instance.GetTask(tid);
        if (target == null) return -(int)Errno.ESRCH;

        if (sig != 0) target.HandleSignal(sig);

        return 0;
    }

    private static async ValueTask<int> SysTgkill(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        // int tgid = (int)a1; // Not used yet?
        // int tid = (int)a2;
        // int sig = (int)a3;
        
        int tgid = (int)a1;
        int tid = (int)a2;
        int sig = (int)a3;
        
        var target = KernelScheduler.Instance.GetTask(tid);
        if (target == null) return -(int)Errno.ESRCH;
        if (target.Process.TGID != tgid && tgid != -1) return -(int)Errno.ESRCH;

         if (sig != 0) target.HandleSignal(sig);
        return 0;
    }

    private static async ValueTask<int> SysExecve(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        var task = (sm.Engine.Owner as FiberTask);
        if (sm == null || task == null) return -(int)Errno.EPERM;
        
        Logger.LogDebug("[SysExecve] sm.Engine==task.CPU? {Same}, sm.Engine.State=0x{EngState:x}, task.CPU.State=0x{CpuState:x}", 
            object.ReferenceEquals(sm.Engine, task.CPU), sm.Engine.State, task.CPU.State);

        string filename = sm.ReadString(a1);
        if (string.IsNullOrEmpty(filename)) return -(int)Errno.EFAULT;
        // Resolve path via VFS
        var dentry = sm.PathWalk(filename);
        string? hostPath = null;

        if (dentry?.Inode is HostInode hi)
        {
            hostPath = hi.HostPath;
        }
        else if (dentry?.Inode is OverlayInode oi && oi.UpperInode == null && oi.LowerInode is HostInode lhi)
        {
            hostPath = lhi.HostPath;
        }

        if (hostPath == null)
        {
            Logger.LogDebug("[SysExecve] Could not resolve '{Filename}' to a host-backed file in VFS", filename);
            return -(int)Errno.ENOENT;
        }

        if (!System.IO.File.Exists(hostPath))
        {
            Logger.LogWarning("[SysExecve] VFS resolved '{Filename}' to '{HostPath}', but file does not exist on host", filename, hostPath);
            return -(int)Errno.ENOENT;
        }

        // We use the host path for loading
        string absPath = hostPath;

        // Read Args (must be done BEFORE clearing memory)
        List<string> args = new();
        if (a2 != 0)
        {
            uint curr = a2;
            byte[] ptrBuf = new byte[4];
            while (true)
            {
                 if (!sm.Engine.CopyFromUser(curr, ptrBuf)) break;
                 uint strPtr = BinaryPrimitives.ReadUInt32LittleEndian(ptrBuf);
                 if (strPtr == 0) break;
                 args.Add(sm.ReadString(strPtr));
                 curr += 4;
            }
        }
        
        // Read Envs (must be done BEFORE clearing memory)
        List<string> envs = new();
        if (a3 != 0)
        {
            uint curr = a3;
            byte[] ptrBuf = new byte[4];
            while (true)
            {
                 if (!sm.Engine.CopyFromUser(curr, ptrBuf)) break;
                 uint strPtr = BinaryPrimitives.ReadUInt32LittleEndian(ptrBuf);
                 if (strPtr == 0) break;
                 envs.Add(sm.ReadString(strPtr));
                 curr += 4;
            }
        }

        // Clear Memory
        sm.Mem.Clear(sm.Engine);
        
        // We also need to reset BRK
        sm.BrkAddr = 0; // ElfLoader will set it

        // Close O_CLOEXEC files
        var toClose = sm.FDs.Where(f => (f.Value.Flags & FileFlags.O_CLOEXEC) != 0).Select(f => f.Key).ToList();
        foreach(var fd in toClose) sm.FreeFD(fd);
        
        // Reset Signals
        task.Process.SignalActions.Clear();
        task.SignalMask = 0;
        task.PendingSignals = 0;
        task.AltStackSp = 0;
        task.AltStackSize = 0;
        task.AltStackFlags = 0;

        // Load new ELF
        Logger.LogInformation("[SysExecve] Loading {Path} with {ArgCount} args, {EnvCount} envs", absPath, args.Count, envs.Count);
        foreach (var arg in args) Logger.LogDebug("  arg: {Arg}", arg);
        try 
        {
            // Note: ElfLoader.Load usually expects us to map the file.
            // In the current codebase usage (e.g. Program.cs), `ElfLoader.Load` takes a real file path?
            var res = Bifrost.Loader.ElfLoader.Load(absPath, sm, args.ToArray(), envs.ToArray());
            
            // Set CPU State
            sm.Engine.Eip = res.Entry;
            sm.Engine.RegWrite(Reg.ESP, res.SP);
            sm.Engine.Eflags = 0x202; // Reset EFLAGS like Program.cs
            
            // Reset segment bases (TLS will be re-setup by new process)
            sm.Engine.SetSegBase(Seg.GS, 0);
            sm.Engine.SetSegBase(Seg.FS, 0);
            
            // Reset other registers to clean state
            sm.Engine.RegWrite(Reg.EAX, 0);
            sm.Engine.RegWrite(Reg.EBX, 0);
            sm.Engine.RegWrite(Reg.ECX, 0);
            sm.Engine.RegWrite(Reg.EDX, 0);
            sm.Engine.RegWrite(Reg.ESI, 0);
            sm.Engine.RegWrite(Reg.EDI, 0);
            sm.Engine.RegWrite(Reg.EBP, 0);
            
            // Initial stack content must be written to memory
            // Use CopyToUser instead of MemWrite to avoid recursive fault handler
            bool stackWritten = sm.Engine.CopyToUser(res.SP, res.InitialStack);
            Logger.LogDebug("[SysExecve] Stack write to 0x{SP:x} len={Len} success={Success}", res.SP, res.InitialStack.Length, stackWritten);
            if (!stackWritten)
            {
                Logger.LogError("[SysExecve] Failed to write initial stack!");
                return -(int)Errno.EFAULT;
            }
            
            sm.BrkAddr = res.BrkAddr; // Set BRK address from ElfLoader result

            return 0; // Success
        }
        catch (FileNotFoundException)
        {
            return -(int)Errno.ENOENT;
        }
        catch (Exception ex) // Catch other exceptions during execve
        {
            Logger.LogWarning("Execve failed: {Message}", ex.Message);
            return -(int)Errno.ENOENT;
        }

        // Unreachable
        // return 0; 
    }

    private static async ValueTask<int> SysSigReturn(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        var task = (sm?.Engine.Owner as FiberTask);
        if (task == null) return -(int)Errno.EPERM;
        
        uint sp = task.CPU.RegRead(Reg.ESP);
        
        // On i386 sigreturn, ESP points to the saved sigcontext
        // (after popl %eax which was done in __restore)
        task.RestoreSigContext(sp); 
        
        return (int)task.CPU.RegRead(Reg.EAX);
    }

    private static async ValueTask<int> SysRtSigReturn(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        var task = (sm?.Engine.Owner as FiberTask);
        if (task == null) return -(int)Errno.EPERM;
        
        uint sp = task.CPU.RegRead(Reg.ESP);
        
        // Heuristic to detect if Arg1 (sig) was popped by handler (e.g. legacy handler doing 'pop' or ret 4?)
        // Layout 1 (Standard): [ESP]=Sig, [ESP+4]=SigInfo*, [ESP+8]=UContext*
        // Layout 2 (Shifted):  [ESP]=SigInfo*, [ESP+4]=UContext*
        
        byte[] spBuf = new byte[4];
        if (!task.CPU.CopyFromUser(sp, spBuf)) 
        {
            return -(int)Errno.EFAULT;
        }
        uint val0 = BinaryPrimitives.ReadUInt32LittleEndian(spBuf);
        uint ucontextAddr;
        
        if (val0 > 0x1000) // Likely a pointer (SigInfo*) -> Shifted stack
        {
            // ESP points to Arg2
            // Arg3 (UContext*) is at ESP+4
            byte[] ptrBuf = new byte[4];
            if (!task.CPU.CopyFromUser(sp + 4, ptrBuf)) 
            {
                return -(int)Errno.EFAULT;
            }
            ucontextAddr = BinaryPrimitives.ReadUInt32LittleEndian(ptrBuf);
        }
        else // Likely a small int (Sig) -> Standard stack
        {
            // ESP points to Arg1
            // Arg3 (UContext*) is at ESP+8
            byte[] ptrBuf = new byte[4];
            if (!task.CPU.CopyFromUser(sp + 8, ptrBuf)) 
            {
                return -(int)Errno.EFAULT;
            }
            ucontextAddr = BinaryPrimitives.ReadUInt32LittleEndian(ptrBuf);
        }
        
        // Restore
        // ucontext.mcontext is at offset 20
        task.RestoreSigContext(ucontextAddr + 20);
        
        return (int)task.CPU.RegRead(Reg.EAX); 
    }

    private static async ValueTask<int> SysNanosleep(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        
        byte[] reqBuf = new byte[8];
        if (!sm.Engine.CopyFromUser(a1, reqBuf)) return -(int)Errno.EFAULT;
        int sec = BinaryPrimitives.ReadInt32LittleEndian(reqBuf.AsSpan(0, 4));
        int nsec = BinaryPrimitives.ReadInt32LittleEndian(reqBuf.AsSpan(4, 4));
        // 1 tick = 1 microsecond?
        long totalTicks = sec * 1000000L + nsec / 1000L;
        if (totalTicks < 0) return 0;
        
        var fiberTask = sm.Engine.Owner as FiberTask;
        if (fiberTask != null)
        {
             // Register Interruption Logic
             // If interrupted, return EINTR and remaining time?
             // For now simpler: Just return EINTR if interrupted, or 0 if success.
             
             // We can't easily use 'await' if we want to handle cancellation via callback logic in FiberTask.
             // But WaitHandle/TimerAwaiter supports normal await.
             // If signal interrupts, FiberTask.TryInterrupt() calls the callback.
             // The callback should cancel the Timer.
             
             // My Timer implementation has 'Cancel()'.
             // TimerAwaiter uses 'ScheduleTimer'. ScheduleTimer returns Timer object.
             // But TimerAwaiter.OnCompleted doesn't expose Timer object easily to the caller of await.
             // We need a more manual usage or improved Awaiter.
             
             // Manual usage for interruptibility:
             var tcs = new TaskCompletionSource<int>();
             var timer = KernelScheduler.Instance.ScheduleTimer(totalTicks + KernelScheduler.Instance.CurrentTick, () => tcs.TrySetResult(0));
             
             fiberTask.RegisterBlockingSyscall(() => {
                 timer.Cancel();
                 tcs.TrySetResult(-(int)Errno.EINTR);
             });
             
             try
             {
                 int ret = await tcs.Task;
                 // If success (0), we are done.
                 return ret;
             }
             finally
             {
                 fiberTask.ClearInterrupt();
             }
        }
        return 0;
    }


    private static async ValueTask<int> SysMount(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        string source = a1 == 0 ? "" : sm.ReadString(a1);
        string target = sm.ReadString(a2);
        string fstype = a3 == 0 ? "" : sm.ReadString(a3);
        uint flags = a4;
        uint dataAddr = a5; 

        var targetDentry = sm.PathWalk(target);
        if (targetDentry == null)
        {
            return -(int)Errno.ENOENT;
        }

        SuperBlock? newSb = null;

        // Handle MS_BIND (Bind Mount)
        if ((flags & (uint)LinuxConstants.MS_BIND) != 0)
        {
            string hostRoot = source;
            var srcDentry = sm.PathWalk(source);
            
            // If binding a host path, we might need a Hostfs SB?
            // Bind mount usually just attaching the existing dentry tree to a new location.
            // But our VFS 'mount' logic expects a SuperBlock root.
            // If we bind a directory, we effectively mount the SB of that directory?
            // Or we treat it as a new "BindFS"?
            // Existing logic created a new Hostfs SB rooted at source.
            // We'll preserve that behavior for HostFS compatibility.
            
            if (srcDentry != null && srcDentry.Inode is HostInode hi) 
            {
                hostRoot = hi.HostPath;
                var fsType = FileSystemRegistry.Get("hostfs");
                if (fsType != null)
                {
                    try { newSb = fsType.FileSystem.ReadSuper(fsType, (int)flags, hostRoot, null); } catch { }
                }
            }
            // For internal bind mounts (Tmpfs -> Tmpfs), we might need a different approach (BindDentry?), 
            // strictly following VFS struct, MountRoot points to a SB Root. 
            // We can't easily "bind" a subdirectory as a Root of a new SB without a "BindFileSystem" wrapper.
            // Fallback: only support Hostfs bind for now as before.
        }
        else
        {
            var fsType = FileSystemRegistry.Get(fstype);
            if (fsType != null)
            {
                 // Special handling for Overlay Options parsing could go here if we support dynamic overlay mounts
                 object? dataObj = null;
                 
                 // If passing string data options
                 if (dataAddr != 0)
                 {
                     // We could read string and pass it?
                     // Tmpfs ignores it. Hostfs uses it as root path? No, Hostfs uses 'source'.
                 }
                 
                 try 
                 {
                    newSb = fsType.FileSystem.ReadSuper(fsType, (int)flags, source, dataObj);
                 }
                 catch (Exception e)
                 {
                     Logger.LogWarning($"Mount failed for {fstype}: {e.Message}");
                     return -(int)Errno.EINVAL;
                 }
            }
            else
            {
                return -(int)Errno.ENODEV;
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

    private static async ValueTask<int> SysUmount(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
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

    private static async ValueTask<int> SysUmount2(IntPtr state, uint a1, uint flags, uint a3, uint a4, uint a5, uint a6)
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

    private static async ValueTask<int> SysChroot(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
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
    private static async ValueTask<int> SysFcntl64(IntPtr state, uint fd, uint cmd, uint arg, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        
        // Console.WriteLine($"[DEBUG] fcntl64({fd}, {cmd}, {arg})");
        
        if (!sm.FDs.ContainsKey((int)fd)) return -(int)Errno.EBADF;

        // Basic implementation for startup
        switch (cmd)
        {
            case 0: // F_DUPFD
                 return sm.AllocFD(sm.FDs[(int)fd], (int)arg);
            case 1: // F_GETFD
                return 0; // No flags
            case 2: // F_SETFD
                // Ignore FD_CLOEXEC for now
                return 0;
            case 3: // F_GETFL
                return (int)sm.FDs[(int)fd].Flags;
            case 4: // F_SETFL
                // Update flags (O_APPEND, O_NONBLOCK, etc)
                // Filter read-only flags
                // sm.FDs[(int)fd].Flags = (int)arg; 
                return 0;
            default:
                // Unimplemented fcntl64 cmd (suppress unless verbose)
                return -(int)Errno.EINVAL;
        }
    }
}
