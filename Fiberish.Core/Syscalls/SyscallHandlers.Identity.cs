using Fiberish.Core;
using Fiberish.Native;

namespace Fiberish.Syscalls;

public partial class SyscallManager
{
    private static ValueTask<int> SysGetUid(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        var t = sm?.Engine.Owner as FiberTask;
        return new ValueTask<int>(t?.Process.UID ?? 0);
    }

    private static ValueTask<int> SysGetEUid(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        var t = sm?.Engine.Owner as FiberTask;
        return new ValueTask<int>(t?.Process.EUID ?? 0);
    }

    private static ValueTask<int> SysGetGid(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        var t = sm?.Engine.Owner as FiberTask;
        return new ValueTask<int>(t?.Process.GID ?? 0);
    }

    private static ValueTask<int> SysGetEGid(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        var t = sm?.Engine.Owner as FiberTask;
        return new ValueTask<int>(t?.Process.EGID ?? 0);
    }

    private static ValueTask<int> SysSetUid(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        var t = sm?.Engine.Owner as FiberTask;
        if (t != null) t.Process.UID = t.Process.EUID = t.Process.SUID = t.Process.FSUID = (int)a1;
        return new ValueTask<int>(0);
    }

    private static ValueTask<int> SysSetGid(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        var t = sm?.Engine.Owner as FiberTask;
        if (t != null) t.Process.GID = t.Process.EGID = t.Process.SGID = t.Process.FSGID = (int)a1;
        return new ValueTask<int>(0);
    }

    private static ValueTask<int> SysGetUid32(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        return SysGetUid(state, a1, a2, a3, a4, a5, a6);
    }

    private static ValueTask<int> SysGetGid32(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        return SysGetGid(state, a1, a2, a3, a4, a5, a6);
    }

    private static ValueTask<int> SysGetEUid32(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        return SysGetEUid(state, a1, a2, a3, a4, a5, a6);
    }

    private static ValueTask<int> SysGetEGid32(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        return SysGetEGid(state, a1, a2, a3, a4, a5, a6);
    }

    private static ValueTask<int> SysSetUid32(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        return SysSetUid(state, a1, a2, a3, a4, a5, a6);
    }

    private static ValueTask<int> SysSetGid32(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        return SysSetGid(state, a1, a2, a3, a4, a5, a6);
    }

    private static ValueTask<int> SysSetReUid(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        var t = sm?.Engine.Owner as FiberTask;
        if (t != null)
        {
            if (a1 != 0xFFFFFFFF) t.Process.UID = (int)a1;
            if (a2 != 0xFFFFFFFF) t.Process.EUID = (int)a2;
            t.Process.SUID = t.Process.EUID;
            t.Process.FSUID = t.Process.EUID;
        }

        return new ValueTask<int>(0);
    }

    private static ValueTask<int> SysSetReGid(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        var t = sm?.Engine.Owner as FiberTask;
        if (t != null)
        {
            if (a1 != 0xFFFFFFFF) t.Process.GID = (int)a1;
            if (a2 != 0xFFFFFFFF) t.Process.EGID = (int)a2;
            t.Process.SGID = t.Process.EGID;
            t.Process.FSGID = t.Process.EGID;
        }

        return new ValueTask<int>(0);
    }

    private static ValueTask<int> SysSetResUid(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        var t = sm?.Engine.Owner as FiberTask;
        if (t != null)
        {
            if (a1 != 0xFFFFFFFF) t.Process.UID = (int)a1;
            if (a2 != 0xFFFFFFFF) t.Process.EUID = (int)a2;
            if (a3 != 0xFFFFFFFF) t.Process.SUID = (int)a3;
            t.Process.FSUID = t.Process.EUID;
        }

        return new ValueTask<int>(0);
    }

    private static ValueTask<int> SysGetResUid(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return new ValueTask<int>(0);
        var t = sm.Engine.Owner as FiberTask;
        if (t != null)
        {
            if (!sm.Engine.CopyToUser(a1, BitConverter.GetBytes(t.Process.UID)))
                return new ValueTask<int>(-(int)Errno.EFAULT);
            if (!sm.Engine.CopyToUser(a2, BitConverter.GetBytes(t.Process.EUID)))
                return new ValueTask<int>(-(int)Errno.EFAULT);
            if (!sm.Engine.CopyToUser(a3, BitConverter.GetBytes(t.Process.SUID)))
                return new ValueTask<int>(-(int)Errno.EFAULT);
        }

        return new ValueTask<int>(0);
    }

    private static ValueTask<int> SysSetResGid(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        var t = sm?.Engine.Owner as FiberTask;
        if (t != null)
        {
            if (a1 != 0xFFFFFFFF) t.Process.GID = (int)a1;
            if (a2 != 0xFFFFFFFF) t.Process.EGID = (int)a2;
            if (a3 != 0xFFFFFFFF) t.Process.SGID = (int)a3;
            t.Process.FSGID = t.Process.EGID;
        }

        return new ValueTask<int>(0);
    }

    private static ValueTask<int> SysGetResGid(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return new ValueTask<int>(0);
        var t = sm.Engine.Owner as FiberTask;
        if (t != null)
        {
            if (!sm.Engine.CopyToUser(a1, BitConverter.GetBytes(t.Process.GID)))
                return new ValueTask<int>(-(int)Errno.EFAULT);
            if (!sm.Engine.CopyToUser(a2, BitConverter.GetBytes(t.Process.EGID)))
                return new ValueTask<int>(-(int)Errno.EFAULT);
            if (!sm.Engine.CopyToUser(a3, BitConverter.GetBytes(t.Process.SGID)))
                return new ValueTask<int>(-(int)Errno.EFAULT);
        }

        return new ValueTask<int>(0);
    }

    private static ValueTask<int> SysSetFsUid(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        var t = sm?.Engine.Owner as FiberTask;
        if (t != null)
        {
            var old = t.Process.FSUID;
            t.Process.FSUID = (int)a1;
            return new ValueTask<int>(old);
        }

        return new ValueTask<int>(0);
    }

    private static ValueTask<int> SysSetFsGid(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        var t = sm?.Engine.Owner as FiberTask;
        if (t != null)
        {
            var old = t.Process.FSGID;
            t.Process.FSGID = (int)a1;
            return new ValueTask<int>(old);
        }

        return new ValueTask<int>(0);
    }

    private static ValueTask<int> SysSetGroups(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        return new ValueTask<int>(0);
    }

    private static ValueTask<int> SysGetGroups(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        return new ValueTask<int>(0);
    }
}