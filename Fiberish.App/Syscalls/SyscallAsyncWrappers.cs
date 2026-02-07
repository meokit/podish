using System.Buffers.Binary;
using Bifrost.Core;
using Bifrost.Native;
using Task = Bifrost.Core.Task;

namespace Bifrost.Syscalls;

public static class SyscallAsyncWrappers
{
    public static async Task<int> WaitFutexAsync(System.Threading.Tasks.Task futexTask)
    {
        await futexTask;
        return 0;
    }

    public static async System.Threading.Tasks.Task<int> SysWait4Async(SyscallManager sm, Bifrost.Core.Task parentTask, IdType idtype, int id, uint statusPtr, int options, System.Threading.Tasks.Task<int> tcsTask)
    {
        int waitResult = await tcsTask;
        if (waitResult < 0) return waitResult;

        await Bifrost.Core.Task.GIL.WaitAsync();
        try
        {
            var info = new SigInfo();
            // Call KernelWaitId with WNOHANG to actually do the reaping using ORIGINAL criteria
            var (pid, _) = WaitHelpers.KernelWaitId(parentTask, idtype, id, info, options | WaitHelpers.WNOHANG);

            if (pid > 0 && statusPtr != 0)
            {
                int status = (info.si_status & 0xFF) << 8;
                byte[] statusBuf = new byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(statusBuf, status);
                if (!sm.Engine.CopyToUser(statusPtr, statusBuf))
                {
                    return -(int)Errno.EFAULT;
                }
            }
            return pid;
        }
        finally
        {
            Bifrost.Core.Task.GIL.Release();
        }
    }

    public static async System.Threading.Tasks.Task<int> SysWaitIdAsync(SyscallManager sm, Bifrost.Core.Task parentTask, IdType idtype, int id, uint infop, int options, System.Threading.Tasks.Task<int> tcsTask)
    {
        int waitResult = await tcsTask;
        if (waitResult < 0) return waitResult;

        await Bifrost.Core.Task.GIL.WaitAsync();
        try
        {
            var info = new SigInfo();
            var (pid, _) = WaitHelpers.KernelWaitId(parentTask, idtype, id, info, options | WaitHelpers.WNOHANG);

            if (pid >= 0 && infop != 0)
            {
                if (!WriteSigInfo(sm, infop, info)) return -(int)Errno.EFAULT;
            }
            return pid >= 0 ? 0 : pid;
        }
        finally
        {
            Bifrost.Core.Task.GIL.Release();
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
}
