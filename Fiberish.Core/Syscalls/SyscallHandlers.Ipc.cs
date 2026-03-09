using Fiberish.Core;
using Fiberish.Native;

namespace Fiberish.Syscalls;

public partial class SyscallManager
{
#pragma warning disable CS1998 // Async method lacks await operators - syscall handlers require async signature
    /// <summary>
    /// sys_ipc multiplexer for i386 Linux.
    /// This syscall multiplexes various IPC operations including:
    /// - SHMGET (23): Create/get shared memory segment
    /// - SHMAT (21): Attach shared memory segment
    /// - SHMDT (22): Detach shared memory segment
    /// - SHMCTL (24): Control shared memory segment
    /// 
    /// On i386, the call parameter encodes:
    /// - Low 16 bits: the IPC operation (op)
    /// - High 16 bits: version/flags (usually 0 or IPC_64)
    /// </summary>
    private static async ValueTask<int> SysIpc(
        IntPtr state,
        uint call, // IPC subcommand with version (low 16 bits = op, high 16 bits = version)
        uint first, // First argument (varies by call)
        uint second, // Second argument (varies by call)
        uint third, // Third argument (varies by call)
        uint ptr, // Pointer argument (varies by call)
        uint fifth) // Fifth argument (varies by call)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        var t = sm.Engine.Owner as FiberTask;
        // Use TGID (process ID) not TID (thread ID) for IPC - shared memory is per-process
        var pid = t?.Process.TGID ?? 0;
        var uid = t?.Process.EUID ?? 0;
        var gid = t?.Process.EGID ?? 0;

        // Decode call: low 16 bits are the operation, high bits may contain version flags
        var op = (int)(call & 0xFFFF);
        var version = (int)(call >> 16);

        int ret = op switch
        {
            LinuxConstants.SHMGET => DoShmGet(sm, (int)first, second, (int)third, uid, gid, pid),
            LinuxConstants.SHMAT => DoShmAt(sm, (int)first, ptr, (int)second, third, pid),
            LinuxConstants.SHMDT => DoShmDt(sm, ptr, pid),
            LinuxConstants.SHMCTL => DoShmCtl(sm, (int)first, (int)second, ptr, uid, gid, pid, version),

            LinuxConstants.SEMGET => sm.SysVSem.SemGet((int)first, (int)second, (int)third, uid, gid),
            LinuxConstants.SEMCTL => DoSemCtlFromIpc(sm, (int)first, (int)second, third, ptr, uid, gid),

            _ => -(int)Errno.ENOSYS
        };

        if (ret == -(int)Errno.ENOSYS)
        {
            if (op == LinuxConstants.SEMOP)
                ret = await sm.SysVSem.SemOp((int)first, ptr, second, sm.Engine);
            else if (op == LinuxConstants.SEMTIMEDOP)
                ret = await sm.SysVSem.SemOp((int)first, ptr, second, sm.Engine); // Ignore timeout for now
        }

        return ret;
    }

    /// <summary>
    /// SHMGET: Create or get a shared memory segment.
    /// args: key, size, shmflg
    /// returns: shmid or negative errno
    /// </summary>
    private static int DoShmGet(SyscallManager sm, int key, uint size, int shmflg, int uid, int gid, int pid)
    {
        return sm.SysVShm.ShmGet(key, size, shmflg, uid, gid, pid);
    }

    /// <summary>
    /// SHMAT: Attach shared memory segment to process address space.
    /// args: shmid, shmaddr, shmflg
    /// returns: 0 on success (address written to *ptr), negative errno on failure
    /// Note: The actual return address is written to the pointer in 'ptr' on i386.
    /// </summary>
    private static int DoShmAt(SyscallManager sm, int shmid, uint shmaddr, int shmflg, uint ptr, int pid)
    {
        var process = (sm.Engine.Owner as FiberTask)?.Process;
        var result = sm.SysVShm.ShmAt(shmid, shmaddr, shmflg, pid, sm.Mem, sm.Engine, process);

        if (result < 0)
            return (int)result;

        // On i386 Linux, shmat returns the address via a pointer in the fifth argument
        // The syscall return value is 0 on success, and the address is written to *ptr
        if (ptr != 0)
        {
            var addrBytes = BitConverter.GetBytes((uint)result);
            if (!sm.Engine.CopyToUser(ptr, addrBytes))
            {
                // [P2] Rollback the mapping on EFAULT - detaching cleans up the VMA
                sm.SysVShm.ShmDt((uint)result, pid, sm.Mem, sm.Engine, process);
                return -(int)Errno.EFAULT;
            }

            return 0;
        }

        // If ptr is NULL, return the address directly (non-standard but some programs expect this)
        return (int)result;
    }

    /// <summary>
    /// SHMDT: Detach shared memory segment from process address space.
    /// args: shmaddr
    /// returns: 0 on success, negative errno on failure
    /// </summary>
    private static int DoShmDt(SyscallManager sm, uint shmaddr, int pid)
    {
        var process = (sm.Engine.Owner as FiberTask)?.Process;
        return sm.SysVShm.ShmDt(shmaddr, pid, sm.Mem, sm.Engine, process);
    }

    /// <summary>
    /// SHMCTL: Control operations on shared memory segment.
    /// args: shmid, cmd, buf
    /// returns: 0 on success, negative errno on failure
    /// Note: cmd may have IPC_64 flag set (0x0100) on i386 glibc.
    /// </summary>
    private static int DoShmCtl(SyscallManager sm, int shmid, int cmd, uint buf, int uid, int gid, int pid, int version)
    {
        // Handle IPC_64 flag - strip it from cmd and pass to ShmCtl
        // Modern glibc on i386 always sets IPC_64 in the version field
        var actualCmd = cmd;
        if ((version & (LinuxConstants.IPC_64 >> 8)) != 0 || (cmd & LinuxConstants.IPC_64) != 0)
        {
            actualCmd = cmd & ~LinuxConstants.IPC_64;
        }

        return sm.SysVShm.ShmCtl(shmid, actualCmd, buf, sm.Engine, uid, gid, pid);
    }

    private static int DoSemCtlFromIpc(SyscallManager sm, int first, int second, uint third, uint ptr, int uid, int gid)
    {
        // In sys_ipc on i386, the 4th arg (ptr) is a pointer to the union semun.
        // We must dereference it to get the actual value for SETVAL/IPC_STAT.
        uint argVal = 0;
        if (ptr != 0)
        {
            Span<byte> buf = stackalloc byte[4];
            if (sm.Engine.CopyFromUser(ptr, buf))
            {
                argVal = BitConverter.ToUInt32(buf);
            }
        }

        return sm.SysVSem.SemCtl(first, second, (int)third, argVal, sm.Engine, uid, gid);
    }

    private static ValueTask<int> SysSemGet(IntPtr state, uint key, uint nsems, uint semflg, uint _4, uint _5, uint _6)
    {
        var sm = Get(state);
        if (sm == null) return new ValueTask<int>(-(int)Errno.EPERM);
        var t = sm.Engine.Owner as FiberTask;
        var uid = t?.Process.EUID ?? 0;
        var gid = t?.Process.EGID ?? 0;
        return new ValueTask<int>(sm.SysVSem.SemGet((int)key, (int)nsems, (int)semflg, uid, gid));
    }

    private static async ValueTask<int> SysSemOp(IntPtr state, uint semid, uint sops, uint nsops, uint _4, uint _5,
        uint _6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        return await sm.SysVSem.SemOp((int)semid, sops, nsops, sm.Engine);
    }

    private static ValueTask<int> SysSemCtl(IntPtr state, uint semid, uint semnum, uint cmd, uint arg, uint _5, uint _6)
    {
        var sm = Get(state);
        if (sm == null) return new ValueTask<int>(-(int)Errno.EPERM);
        var t = sm.Engine.Owner as FiberTask;
        var uid = t?.Process.EUID ?? 0;
        var gid = t?.Process.EGID ?? 0;

        int actualCmd = (int)cmd;
        if ((cmd & LinuxConstants.IPC_64) != 0)
            actualCmd = (int)(cmd & ~LinuxConstants.IPC_64);

        return new ValueTask<int>(sm.SysVSem.SemCtl((int)semid, (int)semnum, actualCmd, arg, sm.Engine, uid, gid));
    }
#pragma warning restore CS1998
}
