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
    private static async ValueTask<int> SysSetGroups(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6) => 0;
    private static async ValueTask<int> SysGetGroups(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6) => 0;
}
