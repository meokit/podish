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
    }

    private static int SysExit(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        
        var task = Scheduler.GetByEngine(state);
        if (task != null)
        {
            Console.WriteLine($"[SysExit] TID={task.TID}, TGID={task.Process.TGID}, ExitCode={a1}");
            Console.WriteLine($"[SysExit] Task.Process hash={task.Process.GetHashCode()}, TID==TGID? {task.TID == task.Process.TGID}");
            
            task.ExitCode = (int)a1;
            task.Exited = true;
            
            // CRITICAL: Set process to zombie state BEFORE signaling events
            // to avoid race condition where parent wakes up before state is set
            lock (task.Process)
            {
                task.Process.State = ProcessState.Zombie;
                task.Process.ExitStatus = (int)a1;
                Console.WriteLine($"[SysExit] Set Process {task.Process.TGID} to Zombie, state={task.Process.State}, hash={task.Process.GetHashCode()}");
                
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
            Console.WriteLine($"[SysExit] WaitEvent.Set() called");
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

    private static int SysOpen(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -1;
        string path = sm.ReadString(a1);
        uint flags = a2;
        uint mode = a3;
        
        var dentry = sm.PathWalk(path);
        if (dentry == null)
        {
            if ((flags & (uint)FileFlags.O_CREAT) != 0)
            {
                int lastSlash = path.LastIndexOf('/');
                string parentPath = lastSlash == -1 ? "." : (lastSlash == 0 ? "/" : path.Substring(0, lastSlash));
                string name = lastSlash == -1 ? path : path.Substring(lastSlash + 1);
                
                var parentDentry = sm.PathWalk(parentPath);
                if (parentDentry == null || parentDentry.Inode == null) return -(int)Errno.ENOENT;
                
                // Get current process credentials
                var t = Scheduler.GetByEngine(sm.Engine.State);
                int uid = t?.Process.EUID ?? 0;
                int gid = t?.Process.EGID ?? 0;
                
                try {
                    var newInode = parentDentry.Inode.Create(name, (int)mode, uid, gid);
                    dentry = new Dentry(name, newInode, parentDentry, parentDentry.SuperBlock);
                    parentDentry.Children[name] = dentry;
                    if (newInode is TmpfsInode ti) ti.SetPrimaryDentry(dentry);
                } catch {
                    // Failed to create file
                    return -(int)Errno.EACCES; // or EEXIST
                }
            }
            else
            {
                return -(int)Errno.ENOENT;
            }
        }
        
        try {
            var f = new Bifrost.VFS.File(dentry, (FileFlags)flags);
            return sm.AllocFD(f);
        } catch {
            // Failed to create file object
            return -1;
        }
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
            2 => (long)f.Dentry.Inode.Size + offset, // SEEK_END
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
        sm.Engine.MemWrite(a1, System.Text.Encoding.ASCII.GetBytes("Linux\0"));
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
    private static int SysFchownAt(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6) => 0;
    private static int SysFchmodAt(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6) => 0;
    private static int SysFaccessAt(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6) => 0;
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
            parentDentry.Inode.Unlink(name);
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
        if (f == null) return -(int)Errno.EBADF;

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
        
        uint mode = (uint)inode.Mode;
        if (inode.Type == InodeType.Directory) mode |= 0x4000;
        else if (inode.Type == InodeType.File) mode |= 0x8000;
        else if (inode.Type == InodeType.CharDev) mode |= 0x2000;
        
        long size = (long)inode.Size;
        
        // Return the actual file ownership, not process credentials
        uint uid = (uint)inode.Uid;
        uint gid = (uint)inode.Gid;

        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(0), 0x800);
        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(8), inode.Ino);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(16), mode);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(20), 1);
        
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(24), uid);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(28), gid);

        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(44), (ulong)size);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(52), 4096);
        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(56), (ulong)((size+511)/512));
        
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
                Console.WriteLine($"SysUmount: filesystem busy (active inodes), cannot unmount");
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
                Console.WriteLine($"SysUmount: filesystem busy (active inodes), cannot unmount");
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
            Console.WriteLine($"SysUmount2: lazy unmount (MNT_DETACH)");
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