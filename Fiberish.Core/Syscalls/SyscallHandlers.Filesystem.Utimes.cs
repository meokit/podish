using System.Buffers.Binary;
using Fiberish.Core;
using Fiberish.Native;
using Microsoft.Extensions.Logging;

namespace Fiberish.Syscalls;

public partial class SyscallManager
{
    private static async ValueTask<int> SysUtimes(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        var path = sm.ReadString(a1);
        var timesPtr = a2;

        var dentry = sm.PathWalk(path);
        if (dentry?.Inode == null) return -(int)Errno.ENOENT;

        if (timesPtr != 0)
        {
            var buf = new byte[8];
            if (!sm.Engine.CopyFromUser(timesPtr, buf)) return -(int)Errno.EFAULT;
            var asec = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(0, 4));
            var ausec = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(4, 4));

            if (!sm.Engine.CopyFromUser(timesPtr + 8, buf)) return -(int)Errno.EFAULT;
            var msec = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(0, 4));
            var musec = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(4, 4));

            dentry.Inode.ATime = DateTimeOffset.FromUnixTimeSeconds(asec).AddTicks(ausec * 10).DateTime;
            dentry.Inode.MTime = DateTimeOffset.FromUnixTimeSeconds(msec).AddTicks(musec * 10).DateTime;
        }
        else
        {
            dentry.Inode.ATime = DateTime.Now;
            dentry.Inode.MTime = DateTime.Now;
        }

        dentry.Inode.CTime = DateTime.Now;
        return 0;
    }
}
