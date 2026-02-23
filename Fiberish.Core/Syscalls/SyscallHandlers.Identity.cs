using Fiberish.Auth.Cred;
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
        if (t == null) return new ValueTask<int>(-(int)Errno.EPERM);
        return new ValueTask<int>(CredentialService.SetUid(t.Process, (int)a1));
    }

    private static ValueTask<int> SysSetGid(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        var t = sm?.Engine.Owner as FiberTask;
        if (t == null) return new ValueTask<int>(-(int)Errno.EPERM);
        return new ValueTask<int>(CredentialService.SetGid(t.Process, (int)a1));
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

    private static ValueTask<int> SysSetReUid32(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        return SysSetReUid(state, a1, a2, a3, a4, a5, a6);
    }

    private static ValueTask<int> SysSetReGid32(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        return SysSetReGid(state, a1, a2, a3, a4, a5, a6);
    }

    private static ValueTask<int> SysGetGroups32(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        return SysGetGroups(state, a1, a2, a3, a4, a5, a6);
    }

    private static ValueTask<int> SysSetGroups32(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        return SysSetGroups(state, a1, a2, a3, a4, a5, a6);
    }

    private static ValueTask<int> SysSetReUid(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        var t = sm?.Engine.Owner as FiberTask;
        if (t == null) return new ValueTask<int>(-(int)Errno.EPERM);
        var ruid = ParseOptionalId(a1);
        var euid = ParseOptionalId(a2);
        if (ruid == int.MinValue || euid == int.MinValue) return new ValueTask<int>(-(int)Errno.EINVAL);
        return new ValueTask<int>(CredentialService.SetReUid(t.Process, ruid, euid));
    }

    private static ValueTask<int> SysSetReGid(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        var t = sm?.Engine.Owner as FiberTask;
        if (t == null) return new ValueTask<int>(-(int)Errno.EPERM);
        var rgid = ParseOptionalId(a1);
        var egid = ParseOptionalId(a2);
        if (rgid == int.MinValue || egid == int.MinValue) return new ValueTask<int>(-(int)Errno.EINVAL);
        return new ValueTask<int>(CredentialService.SetReGid(t.Process, rgid, egid));
    }

    private static ValueTask<int> SysSetResUid(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        var t = sm?.Engine.Owner as FiberTask;
        if (t == null) return new ValueTask<int>(-(int)Errno.EPERM);
        var ruid = ParseOptionalId(a1);
        var euid = ParseOptionalId(a2);
        var suid = ParseOptionalId(a3);
        if (ruid == int.MinValue || euid == int.MinValue || suid == int.MinValue)
            return new ValueTask<int>(-(int)Errno.EINVAL);
        return new ValueTask<int>(CredentialService.SetResUid(t.Process, ruid, euid, suid));
    }

    private static ValueTask<int> SysGetResUid(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return new ValueTask<int>(-(int)Errno.EPERM);
        var t = sm.Engine.Owner as FiberTask;
        if (t == null) return new ValueTask<int>(-(int)Errno.EPERM);

        if (!sm.Engine.CopyToUser(a1, BitConverter.GetBytes(t.Process.UID)))
            return new ValueTask<int>(-(int)Errno.EFAULT);
        if (!sm.Engine.CopyToUser(a2, BitConverter.GetBytes(t.Process.EUID)))
            return new ValueTask<int>(-(int)Errno.EFAULT);
        if (!sm.Engine.CopyToUser(a3, BitConverter.GetBytes(t.Process.SUID)))
            return new ValueTask<int>(-(int)Errno.EFAULT);

        return new ValueTask<int>(0);
    }

    private static ValueTask<int> SysGetResUid16(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return new ValueTask<int>(-(int)Errno.EPERM);
        var t = sm.Engine.Owner as FiberTask;
        if (t == null) return new ValueTask<int>(-(int)Errno.EPERM);

        if (a1 != 0 && !sm.Engine.CopyToUser(a1, BitConverter.GetBytes((ushort)t.Process.UID)))
            return new ValueTask<int>(-(int)Errno.EFAULT);
        if (a2 != 0 && !sm.Engine.CopyToUser(a2, BitConverter.GetBytes((ushort)t.Process.EUID)))
            return new ValueTask<int>(-(int)Errno.EFAULT);
        if (a3 != 0 && !sm.Engine.CopyToUser(a3, BitConverter.GetBytes((ushort)t.Process.SUID)))
            return new ValueTask<int>(-(int)Errno.EFAULT);

        return new ValueTask<int>(0);
    }

    private static ValueTask<int> SysSetResUid32(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        return SysSetResUid(state, a1, a2, a3, a4, a5, a6);
    }

    private static ValueTask<int> SysGetResUid32(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        return SysGetResUid(state, a1, a2, a3, a4, a5, a6);
    }

    private static ValueTask<int> SysSetResGid(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        var t = sm?.Engine.Owner as FiberTask;
        if (t == null) return new ValueTask<int>(-(int)Errno.EPERM);
        var rgid = ParseOptionalId(a1);
        var egid = ParseOptionalId(a2);
        var sgid = ParseOptionalId(a3);
        if (rgid == int.MinValue || egid == int.MinValue || sgid == int.MinValue)
            return new ValueTask<int>(-(int)Errno.EINVAL);
        return new ValueTask<int>(CredentialService.SetResGid(t.Process, rgid, egid, sgid));
    }

    private static ValueTask<int> SysGetResGid(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return new ValueTask<int>(-(int)Errno.EPERM);
        var t = sm.Engine.Owner as FiberTask;
        if (t == null) return new ValueTask<int>(-(int)Errno.EPERM);

        if (!sm.Engine.CopyToUser(a1, BitConverter.GetBytes(t.Process.GID)))
            return new ValueTask<int>(-(int)Errno.EFAULT);
        if (!sm.Engine.CopyToUser(a2, BitConverter.GetBytes(t.Process.EGID)))
            return new ValueTask<int>(-(int)Errno.EFAULT);
        if (!sm.Engine.CopyToUser(a3, BitConverter.GetBytes(t.Process.SGID)))
            return new ValueTask<int>(-(int)Errno.EFAULT);

        return new ValueTask<int>(0);
    }

    private static ValueTask<int> SysGetResGid16(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return new ValueTask<int>(-(int)Errno.EPERM);
        var t = sm.Engine.Owner as FiberTask;
        if (t == null) return new ValueTask<int>(-(int)Errno.EPERM);

        if (a1 != 0 && !sm.Engine.CopyToUser(a1, BitConverter.GetBytes((ushort)t.Process.GID)))
            return new ValueTask<int>(-(int)Errno.EFAULT);
        if (a2 != 0 && !sm.Engine.CopyToUser(a2, BitConverter.GetBytes((ushort)t.Process.EGID)))
            return new ValueTask<int>(-(int)Errno.EFAULT);
        if (a3 != 0 && !sm.Engine.CopyToUser(a3, BitConverter.GetBytes((ushort)t.Process.SGID)))
            return new ValueTask<int>(-(int)Errno.EFAULT);

        return new ValueTask<int>(0);
    }

    private static ValueTask<int> SysSetResGid32(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        return SysSetResGid(state, a1, a2, a3, a4, a5, a6);
    }

    private static ValueTask<int> SysGetResGid32(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        return SysGetResGid(state, a1, a2, a3, a4, a5, a6);
    }

    private static ValueTask<int> SysSetFsUid(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        var t = sm?.Engine.Owner as FiberTask;
        if (t == null) return new ValueTask<int>(0);
        return new ValueTask<int>(CredentialService.SetFsUid(t.Process, (int)a1));
    }

    private static ValueTask<int> SysSetFsGid(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        var t = sm?.Engine.Owner as FiberTask;
        if (t == null) return new ValueTask<int>(0);
        return new ValueTask<int>(CredentialService.SetFsGid(t.Process, (int)a1));
    }

    private static ValueTask<int> SysSetFsUid32(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        return SysSetFsUid(state, a1, a2, a3, a4, a5, a6);
    }

    private static ValueTask<int> SysSetFsGid32(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        return SysSetFsGid(state, a1, a2, a3, a4, a5, a6);
    }

    private static ValueTask<int> SysSetGroups(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        var t = sm?.Engine.Owner as FiberTask;
        if (sm == null || t == null) return new ValueTask<int>(-(int)Errno.EPERM);

        var size = (int)a1;
        var listPtr = a2;
        if (size < 0) return new ValueTask<int>(-(int)Errno.EINVAL);
        if (size > 1024) return new ValueTask<int>(-(int)Errno.EINVAL);

        var groups = new int[size];
        if (size > 0)
        {
            var bytes = new byte[size * 4];
            if (!sm.Engine.CopyFromUser(listPtr, bytes)) return new ValueTask<int>(-(int)Errno.EFAULT);
            for (var i = 0; i < size; i++) groups[i] = BitConverter.ToInt32(bytes, i * 4);
        }

        return new ValueTask<int>(CredentialService.SetGroups(t.Process, groups));
    }

    private static ValueTask<int> SysGetGroups(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        var t = sm?.Engine.Owner as FiberTask;
        if (sm == null || t == null) return new ValueTask<int>(-(int)Errno.EPERM);

        var size = (int)a1;
        var listPtr = a2;
        var groups = CredentialService.GetGroups(t.Process);

        if (size == 0) return new ValueTask<int>(groups.Count);
        if (size < groups.Count) return new ValueTask<int>(-(int)Errno.EINVAL);

        if (groups.Count > 0)
        {
            var bytes = new byte[groups.Count * 4];
            for (var i = 0; i < groups.Count; i++)
                Array.Copy(BitConverter.GetBytes(groups[i]), 0, bytes, i * 4, 4);
            if (!sm.Engine.CopyToUser(listPtr, bytes)) return new ValueTask<int>(-(int)Errno.EFAULT);
        }

        return new ValueTask<int>(groups.Count);
    }

    private static int ParseOptionalId(uint value)
    {
        if (value == 0xFFFFFFFF) return -1;
        if ((value & 0x80000000) != 0) return int.MinValue;
        return (int)value;
    }
}
