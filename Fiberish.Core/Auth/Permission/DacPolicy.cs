using Fiberish.Auth.Cred;
using Fiberish.Core;
using Fiberish.Native;
using Fiberish.VFS;

namespace Fiberish.Auth.Permission;

public static class DacPolicy
{
    public readonly record struct CreationMetadata(int Mode, int Uid, int Gid);

    public static int ApplyUmask(int requestedMode, int umask)
    {
        return requestedMode & 0xFFF & ~umask;
    }

    public static CreationMetadata ComputeCreationMetadata(Process? process, Inode parentDirectory, int requestedMode,
        bool creatingDirectory)
    {
        const int S_ISGID = 0x400;

        var uid = process?.FSUID ?? 0;
        var gid = process?.FSGID ?? 0;
        var mode = ApplyUmask(requestedMode, process?.Umask ?? 0);

        if ((parentDirectory.Mode & S_ISGID) != 0)
        {
            gid = parentDirectory.Gid;
            if (creatingDirectory)
                mode |= S_ISGID;
        }

        return new CreationMetadata(mode, uid, gid);
    }

    public static int CheckPathAccess(Process process, Inode inode, AccessMode mode, bool useEffectiveIds)
    {
        if (mode == AccessMode.None) return 0;

        var uid = useEffectiveIds ? process.FSUID : process.UID;
        var gid = useEffectiveIds ? process.FSGID : process.GID;

        if (uid == 0)
        {
            // Root bypasses read/write; for execute require at least one execute bit if regular file.
            if ((mode & AccessMode.MayExec) != 0 && inode.Type == InodeType.File && (inode.Mode & 0x49) == 0)
                return -(int)Errno.EACCES;
            return 0;
        }

        var permBits = ResolvePermissionClass(process, inode, uid, gid, useEffectiveIds);

        if ((mode & AccessMode.MayRead) != 0 && (permBits & 0x4) == 0) return -(int)Errno.EACCES;
        if ((mode & AccessMode.MayWrite) != 0 && (permBits & 0x2) == 0) return -(int)Errno.EACCES;
        if ((mode & AccessMode.MayExec) != 0 && (permBits & 0x1) == 0) return -(int)Errno.EACCES;

        return 0;
    }

    public static int CanChmod(Process process, Inode inode)
    {
        if (process.FSUID == 0 || process.FSUID == inode.Uid) return 0;
        return -(int)Errno.EPERM;
    }

    public static int CanChown(Process process, Inode inode, int uid, int gid)
    {
        if (process.EUID == 0) return 0;

        // Linux allows an unprivileged no-op ownership change even when the caller
        // does not own the file, because neither uid nor gid changes.
        if (uid == -1 && gid == -1) return 0;

        // Unprivileged users may only retain ownership and optionally change group to one they are in.
        if (uid != -1 && uid != inode.Uid) return -(int)Errno.EPERM;
        if (process.FSUID != inode.Uid) return -(int)Errno.EPERM;
        if (gid != -1 && !CredentialService.IsInGroup(process, gid)) return -(int)Errno.EPERM;

        return 0;
    }

    public static int CanChroot(Process process)
    {
        return process.EUID == 0 ? 0 : -(int)Errno.EPERM;
    }

    public static int NormalizeChmodMode(Process process, Inode inode, int requestedMode)
    {
        var mode = requestedMode & 0xFFF;
        if (process.FSUID == 0) return mode;

        // Linux behavior: without CAP_FSETID, SGID bit is cleared when caller is not in file's group.
        if ((mode & 0x400) != 0 && !CredentialService.IsInGroup(process, inode.Gid))
            mode &= ~0x400;

        return mode;
    }

    public static int ApplySetIdClearOnChown(Inode inode, int oldUid, int oldGid, int newUid, int newGid)
    {
        if (oldUid == newUid && oldGid == newGid) return inode.Mode;
        if (inode.Type == InodeType.Symlink) return inode.Mode;

        // Linux clears S_ISUID/S_ISGID on ownership changes for non-symlink
        // objects that can carry mode bits in pjdfstest coverage.
        return inode.Mode & ~0xC00;
    }

    public static int ApplySetIdClearOnWrite(Process process, Inode inode)
    {
        if (inode.Type != InodeType.File) return inode.Mode;
        if (process.FSUID == 0 || process.FSUID == inode.Uid) return inode.Mode;

        // Linux clears S_ISUID/S_ISGID on regular-file writes by non-owner writers.
        return inode.Mode & ~0xC00;
    }

    public static int CanRemoveOrRenameEntry(Process process, Inode parentDirectory, Inode victim)
    {
        const int S_ISVTX = 0x200;

        if (process.FSUID == 0) return 0;
        if ((parentDirectory.Mode & S_ISVTX) == 0) return 0;
        if (process.FSUID == parentDirectory.Uid) return 0;
        if (process.FSUID == victim.Uid) return 0;

        return -(int)Errno.EPERM;
    }

    private static int ResolvePermissionClass(Process process, Inode inode, int uid, int gid, bool useEffectiveIds)
    {
        if (uid == inode.Uid) return (inode.Mode >> 6) & 0x7;

        var inGroup = inode.Gid == gid;
        if (!inGroup)
        {
            if (useEffectiveIds)
                inGroup = CredentialService.IsInGroup(process, inode.Gid);
            else
                inGroup = inode.Gid == process.GID || inode.Gid == process.EGID || inode.Gid == process.SGID ||
                          process.SupplementaryGroups.Contains(inode.Gid);
        }

        return inGroup ? (inode.Mode >> 3) & 0x7 : inode.Mode & 0x7;
    }
}
