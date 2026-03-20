using Fiberish.Auth.Cred;
using Fiberish.Core;
using Fiberish.Native;

namespace Fiberish.Syscalls;

public partial class SyscallManager
{
    private ValueTask<int> SysGetUid(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var t = engine.Owner as FiberTask;
        return new ValueTask<int>(t?.Process.UID ?? 0);
    }

    private ValueTask<int> SysGetEUid(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var t = engine.Owner as FiberTask;
        return new ValueTask<int>(t?.Process.EUID ?? 0);
    }

    private ValueTask<int> SysGetGid(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var t = engine.Owner as FiberTask;
        return new ValueTask<int>(t?.Process.GID ?? 0);
    }

    private ValueTask<int> SysGetEGid(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var t = engine.Owner as FiberTask;
        return new ValueTask<int>(t?.Process.EGID ?? 0);
    }

    private ValueTask<int> SysSetUid(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var t = engine.Owner as FiberTask;
        if (t == null) return new ValueTask<int>(-(int)Errno.EPERM);
        return new ValueTask<int>(CredentialService.SetUid(t.Process, (int)a1));
    }

    private ValueTask<int> SysSetGid(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var t = engine.Owner as FiberTask;
        if (t == null) return new ValueTask<int>(-(int)Errno.EPERM);
        return new ValueTask<int>(CredentialService.SetGid(t.Process, (int)a1));
    }

    private ValueTask<int> SysGetUid32(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        return SysGetUid(engine, a1, a2, a3, a4, a5, a6);
    }

    private ValueTask<int> SysGetGid32(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        return SysGetGid(engine, a1, a2, a3, a4, a5, a6);
    }

    private ValueTask<int> SysGetEUid32(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        return SysGetEUid(engine, a1, a2, a3, a4, a5, a6);
    }

    private ValueTask<int> SysGetEGid32(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        return SysGetEGid(engine, a1, a2, a3, a4, a5, a6);
    }

    private ValueTask<int> SysSetUid32(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        return SysSetUid(engine, a1, a2, a3, a4, a5, a6);
    }

    private ValueTask<int> SysSetGid32(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        return SysSetGid(engine, a1, a2, a3, a4, a5, a6);
    }

    private ValueTask<int> SysSetReUid32(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        return SysSetReUid(engine, a1, a2, a3, a4, a5, a6);
    }

    private ValueTask<int> SysSetReGid32(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        return SysSetReGid(engine, a1, a2, a3, a4, a5, a6);
    }

    private ValueTask<int> SysGetGroups32(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        return SysGetGroups(engine, a1, a2, a3, a4, a5, a6);
    }

    private ValueTask<int> SysSetGroups32(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        return SysSetGroups(engine, a1, a2, a3, a4, a5, a6);
    }

    private ValueTask<int> SysSetReUid(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var t = engine.Owner as FiberTask;
        if (t == null) return new ValueTask<int>(-(int)Errno.EPERM);
        var ruid = ParseOptionalId(a1);
        var euid = ParseOptionalId(a2);
        if (ruid == int.MinValue || euid == int.MinValue) return new ValueTask<int>(-(int)Errno.EINVAL);
        return new ValueTask<int>(CredentialService.SetReUid(t.Process, ruid, euid));
    }

    private ValueTask<int> SysSetReGid(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var t = engine.Owner as FiberTask;
        if (t == null) return new ValueTask<int>(-(int)Errno.EPERM);
        var rgid = ParseOptionalId(a1);
        var egid = ParseOptionalId(a2);
        if (rgid == int.MinValue || egid == int.MinValue) return new ValueTask<int>(-(int)Errno.EINVAL);
        return new ValueTask<int>(CredentialService.SetReGid(t.Process, rgid, egid));
    }

    private ValueTask<int> SysSetResUid(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var t = engine.Owner as FiberTask;
        if (t == null) return new ValueTask<int>(-(int)Errno.EPERM);
        var ruid = ParseOptionalId(a1);
        var euid = ParseOptionalId(a2);
        var suid = ParseOptionalId(a3);
        if (ruid == int.MinValue || euid == int.MinValue || suid == int.MinValue)
            return new ValueTask<int>(-(int)Errno.EINVAL);
        return new ValueTask<int>(CredentialService.SetResUid(t.Process, ruid, euid, suid));
    }

    private ValueTask<int> SysGetResUid(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var t = engine.Owner as FiberTask;
        if (t == null) return new ValueTask<int>(-(int)Errno.EPERM);

        if (!engine.CopyToUser(a1, BitConverter.GetBytes(t.Process.UID)))
            return new ValueTask<int>(-(int)Errno.EFAULT);
        if (!engine.CopyToUser(a2, BitConverter.GetBytes(t.Process.EUID)))
            return new ValueTask<int>(-(int)Errno.EFAULT);
        if (!engine.CopyToUser(a3, BitConverter.GetBytes(t.Process.SUID)))
            return new ValueTask<int>(-(int)Errno.EFAULT);

        return new ValueTask<int>(0);
    }

    private ValueTask<int> SysGetResUid16(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var t = engine.Owner as FiberTask;
        if (t == null) return new ValueTask<int>(-(int)Errno.EPERM);

        if (a1 != 0 && !engine.CopyToUser(a1, BitConverter.GetBytes((ushort)t.Process.UID)))
            return new ValueTask<int>(-(int)Errno.EFAULT);
        if (a2 != 0 && !engine.CopyToUser(a2, BitConverter.GetBytes((ushort)t.Process.EUID)))
            return new ValueTask<int>(-(int)Errno.EFAULT);
        if (a3 != 0 && !engine.CopyToUser(a3, BitConverter.GetBytes((ushort)t.Process.SUID)))
            return new ValueTask<int>(-(int)Errno.EFAULT);

        return new ValueTask<int>(0);
    }

    private ValueTask<int> SysSetResUid32(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        return SysSetResUid(engine, a1, a2, a3, a4, a5, a6);
    }

    private ValueTask<int> SysGetResUid32(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        return SysGetResUid(engine, a1, a2, a3, a4, a5, a6);
    }

    private ValueTask<int> SysSetResGid(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var t = engine.Owner as FiberTask;
        if (t == null) return new ValueTask<int>(-(int)Errno.EPERM);
        var rgid = ParseOptionalId(a1);
        var egid = ParseOptionalId(a2);
        var sgid = ParseOptionalId(a3);
        if (rgid == int.MinValue || egid == int.MinValue || sgid == int.MinValue)
            return new ValueTask<int>(-(int)Errno.EINVAL);
        return new ValueTask<int>(CredentialService.SetResGid(t.Process, rgid, egid, sgid));
    }

    private ValueTask<int> SysGetResGid(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var t = engine.Owner as FiberTask;
        if (t == null) return new ValueTask<int>(-(int)Errno.EPERM);

        if (!engine.CopyToUser(a1, BitConverter.GetBytes(t.Process.GID)))
            return new ValueTask<int>(-(int)Errno.EFAULT);
        if (!engine.CopyToUser(a2, BitConverter.GetBytes(t.Process.EGID)))
            return new ValueTask<int>(-(int)Errno.EFAULT);
        if (!engine.CopyToUser(a3, BitConverter.GetBytes(t.Process.SGID)))
            return new ValueTask<int>(-(int)Errno.EFAULT);

        return new ValueTask<int>(0);
    }

    private ValueTask<int> SysGetResGid16(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var t = engine.Owner as FiberTask;
        if (t == null) return new ValueTask<int>(-(int)Errno.EPERM);

        if (a1 != 0 && !engine.CopyToUser(a1, BitConverter.GetBytes((ushort)t.Process.GID)))
            return new ValueTask<int>(-(int)Errno.EFAULT);
        if (a2 != 0 && !engine.CopyToUser(a2, BitConverter.GetBytes((ushort)t.Process.EGID)))
            return new ValueTask<int>(-(int)Errno.EFAULT);
        if (a3 != 0 && !engine.CopyToUser(a3, BitConverter.GetBytes((ushort)t.Process.SGID)))
            return new ValueTask<int>(-(int)Errno.EFAULT);

        return new ValueTask<int>(0);
    }

    private ValueTask<int> SysSetResGid32(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        return SysSetResGid(engine, a1, a2, a3, a4, a5, a6);
    }

    private ValueTask<int> SysGetResGid32(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        return SysGetResGid(engine, a1, a2, a3, a4, a5, a6);
    }

    private ValueTask<int> SysSetFsUid(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var t = engine.Owner as FiberTask;
        if (t == null) return new ValueTask<int>(0);
        return new ValueTask<int>(CredentialService.SetFsUid(t.Process, (int)a1));
    }

    private ValueTask<int> SysSetFsGid(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var t = engine.Owner as FiberTask;
        if (t == null) return new ValueTask<int>(0);
        return new ValueTask<int>(CredentialService.SetFsGid(t.Process, (int)a1));
    }

    private ValueTask<int> SysSetFsUid32(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        return SysSetFsUid(engine, a1, a2, a3, a4, a5, a6);
    }

    private ValueTask<int> SysSetFsGid32(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        return SysSetFsGid(engine, a1, a2, a3, a4, a5, a6);
    }

    private ValueTask<int> SysSetGroups(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var t = engine.Owner as FiberTask;
        if (t == null) return new ValueTask<int>(-(int)Errno.EPERM);

        var size = (int)a1;
        var listPtr = a2;
        if (size < 0) return new ValueTask<int>(-(int)Errno.EINVAL);
        if (size > 1024) return new ValueTask<int>(-(int)Errno.EINVAL);

        var groups = new int[size];
        if (size > 0)
        {
            var bytes = new byte[size * 4];
            if (!engine.CopyFromUser(listPtr, bytes)) return new ValueTask<int>(-(int)Errno.EFAULT);
            for (var i = 0; i < size; i++) groups[i] = BitConverter.ToInt32(bytes, i * 4);
        }

        return new ValueTask<int>(CredentialService.SetGroups(t.Process, groups));
    }

    private ValueTask<int> SysGetGroups(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var t = engine.Owner as FiberTask;
        if (t == null) return new ValueTask<int>(-(int)Errno.EPERM);

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
            if (!engine.CopyToUser(listPtr, bytes)) return new ValueTask<int>(-(int)Errno.EFAULT);
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