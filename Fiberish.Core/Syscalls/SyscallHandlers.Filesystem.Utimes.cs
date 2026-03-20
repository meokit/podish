using System.Buffers.Binary;
using Fiberish.Core;
using Fiberish.Native;

namespace Fiberish.Syscalls;

public partial class SyscallManager
{
#pragma warning disable CS1998 // Async method lacks await operators
    private async ValueTask<int> SysUtimes(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var path = ReadString(a1);
        var timesPtr = a2;

        var loc = PathWalk(path);
        if (!loc.IsValid || loc.Dentry!.Inode == null) return -(int)Errno.ENOENT;

        // Check mount read-only
        if (loc.Mount != null && loc.Mount.IsReadOnly) return -(int)Errno.EROFS;

        if (timesPtr != 0)
        {
            var buf = new byte[8];
            if (!engine.CopyFromUser(timesPtr, buf)) return -(int)Errno.EFAULT;
            var asec = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(0, 4));
            var ausec = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(4, 4));

            if (!engine.CopyFromUser(timesPtr + 8, buf)) return -(int)Errno.EFAULT;
            var msec = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(0, 4));
            var musec = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(4, 4));

            loc.Dentry.Inode.ATime = DateTimeOffset.FromUnixTimeSeconds(asec).AddTicks(ausec * 10).DateTime;
            loc.Dentry.Inode.MTime = DateTimeOffset.FromUnixTimeSeconds(msec).AddTicks(musec * 10).DateTime;
        }
        else
        {
            loc.Dentry.Inode.ATime = DateTime.Now;
            loc.Dentry.Inode.MTime = DateTime.Now;
        }

        loc.Dentry.Inode.CTime = DateTime.Now;
        return 0;
    }
}