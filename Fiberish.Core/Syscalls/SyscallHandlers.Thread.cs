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
}
