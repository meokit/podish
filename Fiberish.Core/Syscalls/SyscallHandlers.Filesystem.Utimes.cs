using System.Buffers.Binary;
using Fiberish.Core;
using Fiberish.Native;

namespace Fiberish.Syscalls;

public partial class SyscallManager
{
    private const int UtimeNow = 0x3fffffff;
    private const int UtimeOmit = 0x3ffffffe;

    private static DateTime UnixSecondsToUtcDateTime(long seconds, int nanoseconds = 0)
    {
        return DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime.AddTicks(nanoseconds / 100);
    }

    private static bool TryValidateTimespecNsec(int nanoseconds)
    {
        return nanoseconds is >= 0 and < 1_000_000_000 || nanoseconds == UtimeNow || nanoseconds == UtimeOmit;
    }

    private static (DateTime? Atime, DateTime? Mtime, DateTime Ctime, int? Error) ResolveRequestedTimes(
        DateTime currentAtime,
        DateTime currentMtime,
        bool useCurrentTime,
        long atimeSeconds = 0,
        int atimeNanoseconds = 0,
        long mtimeSeconds = 0,
        int mtimeNanoseconds = 0)
    {
        var now = DateTime.UtcNow;
        if (useCurrentTime)
            return (now, now, now, null);

        if (!TryValidateTimespecNsec(atimeNanoseconds) || !TryValidateTimespecNsec(mtimeNanoseconds))
            return (null, null, default, -(int)Errno.EINVAL);

        DateTime? resolvedAtime = atimeNanoseconds switch
        {
            UtimeNow => now,
            UtimeOmit => null,
            _ => UnixSecondsToUtcDateTime(atimeSeconds, atimeNanoseconds)
        };
        DateTime? resolvedMtime = mtimeNanoseconds switch
        {
            UtimeNow => now,
            UtimeOmit => null,
            _ => UnixSecondsToUtcDateTime(mtimeSeconds, mtimeNanoseconds)
        };

        if (resolvedAtime == null && atimeNanoseconds == UtimeOmit)
            resolvedAtime = currentAtime;
        if (resolvedMtime == null && mtimeNanoseconds == UtimeOmit)
            resolvedMtime = currentMtime;

        return (
            atimeNanoseconds == UtimeOmit ? null : resolvedAtime,
            mtimeNanoseconds == UtimeOmit ? null : resolvedMtime,
            now,
            null);
    }

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
            if (ausec is < 0 or >= 1_000_000 || musec is < 0 or >= 1_000_000)
                return -(int)Errno.EINVAL;

            var resolved = ResolveRequestedTimes(
                loc.Dentry.Inode.ATime,
                loc.Dentry.Inode.MTime,
                false,
                asec,
                ausec * 1000,
                msec,
                musec * 1000);
            if (resolved.Error.HasValue) return resolved.Error.Value;
            return loc.Dentry.Inode.UpdateTimes(resolved.Atime, resolved.Mtime, resolved.Ctime);
        }

        var nowResolved = ResolveRequestedTimes(loc.Dentry.Inode.ATime, loc.Dentry.Inode.MTime, true);
        return loc.Dentry.Inode.UpdateTimes(nowResolved.Atime, nowResolved.Mtime, nowResolved.Ctime);
    }
}