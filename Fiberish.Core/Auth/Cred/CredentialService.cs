using Fiberish.Core;
using Fiberish.Native;
using Fiberish.VFS;

namespace Fiberish.Auth.Cred;

public static class CredentialService
{
    public static Cred Get(Process process)
    {
        return new Cred
        {
            Ruid = process.UID,
            Euid = process.EUID,
            Suid = process.SUID,
            Fsuid = process.FSUID,
            Rgid = process.GID,
            Egid = process.EGID,
            Sgid = process.SGID,
            Fsgid = process.FSGID,
            Groups = new GroupInfo(process.SupplementaryGroups),
            Umask = process.Umask
        };
    }

    public static void Commit(Process process, Cred cred)
    {
        process.UID = cred.Ruid;
        process.EUID = cred.Euid;
        process.SUID = cred.Suid;
        process.FSUID = cred.Fsuid;

        process.GID = cred.Rgid;
        process.EGID = cred.Egid;
        process.SGID = cred.Sgid;
        process.FSGID = cred.Fsgid;

        process.SupplementaryGroups.Clear();
        process.SupplementaryGroups.AddRange(cred.Groups.Groups);
        process.Umask = cred.Umask;
    }

    public static int SetUid(Process process, int uid)
    {
        if (uid < 0) return -(int)Errno.EINVAL;

        var cred = Get(process);
        if (IsPrivileged(cred))
        {
            cred.Ruid = uid;
            cred.Euid = uid;
            cred.Suid = uid;
            cred.Fsuid = uid;
            Commit(process, cred);
            return 0;
        }

        if (uid == cred.Ruid || uid == cred.Suid)
        {
            cred.Euid = uid;
            cred.Fsuid = uid;
            Commit(process, cred);
            return 0;
        }

        return -(int)Errno.EPERM;
    }

    public static int SetGid(Process process, int gid)
    {
        if (gid < 0) return -(int)Errno.EINVAL;

        var cred = Get(process);
        if (IsPrivileged(cred))
        {
            cred.Rgid = gid;
            cred.Egid = gid;
            cred.Sgid = gid;
            cred.Fsgid = gid;
            Commit(process, cred);
            return 0;
        }

        if (gid == cred.Rgid || gid == cred.Sgid)
        {
            cred.Egid = gid;
            cred.Fsgid = gid;
            Commit(process, cred);
            return 0;
        }

        return -(int)Errno.EPERM;
    }

    public static int SetReUid(Process process, int ruid, int euid)
    {
        var cred = Get(process);
        var privileged = IsPrivileged(cred);
        var oldRuid = cred.Ruid;

        if (ruid != -1)
        {
            if (ruid < 0) return -(int)Errno.EINVAL;
            if (!privileged && !MatchesAnyUid(cred, ruid)) return -(int)Errno.EPERM;
            cred.Ruid = ruid;
        }

        if (euid != -1)
        {
            if (euid < 0) return -(int)Errno.EINVAL;
            if (!privileged && !MatchesAnyUid(cred, euid)) return -(int)Errno.EPERM;
            cred.Euid = euid;
            cred.Fsuid = euid;
        }

        if (privileged || (ruid != -1 && ruid != oldRuid) || (euid != -1 && euid != oldRuid))
            cred.Suid = cred.Euid;

        Commit(process, cred);
        return 0;
    }

    public static int SetReGid(Process process, int rgid, int egid)
    {
        var cred = Get(process);
        var privileged = IsPrivileged(cred);
        var oldRgid = cred.Rgid;

        if (rgid != -1)
        {
            if (rgid < 0) return -(int)Errno.EINVAL;
            if (!privileged && !MatchesAnyGid(cred, rgid)) return -(int)Errno.EPERM;
            cred.Rgid = rgid;
        }

        if (egid != -1)
        {
            if (egid < 0) return -(int)Errno.EINVAL;
            if (!privileged && !MatchesAnyGid(cred, egid)) return -(int)Errno.EPERM;
            cred.Egid = egid;
            cred.Fsgid = egid;
        }

        if (privileged || (rgid != -1 && rgid != oldRgid) || (egid != -1 && egid != oldRgid))
            cred.Sgid = cred.Egid;

        Commit(process, cred);
        return 0;
    }

    public static int SetResUid(Process process, int ruid, int euid, int suid)
    {
        var cred = Get(process);
        var privileged = IsPrivileged(cred);

        if (ruid != -1)
        {
            if (ruid < 0) return -(int)Errno.EINVAL;
            if (!privileged && !MatchesAnyUid(cred, ruid)) return -(int)Errno.EPERM;
            cred.Ruid = ruid;
        }

        if (euid != -1)
        {
            if (euid < 0) return -(int)Errno.EINVAL;
            if (!privileged && !MatchesAnyUid(cred, euid)) return -(int)Errno.EPERM;
            cred.Euid = euid;
            cred.Fsuid = euid;
        }

        if (suid != -1)
        {
            if (suid < 0) return -(int)Errno.EINVAL;
            if (!privileged && !MatchesAnyUid(cred, suid)) return -(int)Errno.EPERM;
            cred.Suid = suid;
        }

        Commit(process, cred);
        return 0;
    }

    public static int SetResGid(Process process, int rgid, int egid, int sgid)
    {
        var cred = Get(process);
        var privileged = IsPrivileged(cred);

        if (rgid != -1)
        {
            if (rgid < 0) return -(int)Errno.EINVAL;
            if (!privileged && !MatchesAnyGid(cred, rgid)) return -(int)Errno.EPERM;
            cred.Rgid = rgid;
        }

        if (egid != -1)
        {
            if (egid < 0) return -(int)Errno.EINVAL;
            if (!privileged && !MatchesAnyGid(cred, egid)) return -(int)Errno.EPERM;
            cred.Egid = egid;
            cred.Fsgid = egid;
        }

        if (sgid != -1)
        {
            if (sgid < 0) return -(int)Errno.EINVAL;
            if (!privileged && !MatchesAnyGid(cred, sgid)) return -(int)Errno.EPERM;
            cred.Sgid = sgid;
        }

        Commit(process, cred);
        return 0;
    }

    public static int SetFsUid(Process process, int uid)
    {
        var cred = Get(process);
        var old = cred.Fsuid;

        if (uid < 0) return old;
        if (IsPrivileged(cred) || uid == cred.Ruid || uid == cred.Euid || uid == cred.Suid || uid == cred.Fsuid)
        {
            cred.Fsuid = uid;
            Commit(process, cred);
        }

        return old;
    }

    public static int SetFsGid(Process process, int gid)
    {
        var cred = Get(process);
        var old = cred.Fsgid;

        if (gid < 0) return old;
        if (IsPrivileged(cred) || gid == cred.Rgid || gid == cred.Egid || gid == cred.Sgid || gid == cred.Fsgid)
        {
            cred.Fsgid = gid;
            Commit(process, cred);
        }

        return old;
    }

    public static int SetGroups(Process process, IReadOnlyList<int> groups)
    {
        var cred = Get(process);
        if (!IsPrivileged(cred)) return -(int)Errno.EPERM;
        if (groups.Any(g => g < 0)) return -(int)Errno.EINVAL;

        cred.Groups = new GroupInfo(groups);
        Commit(process, cred);
        return 0;
    }

    public static IReadOnlyList<int> GetGroups(Process process)
    {
        return process.SupplementaryGroups;
    }

    public static int SetUmask(Process process, int newUmask)
    {
        var old = process.Umask;
        process.Umask = newUmask & 0x1FF;
        return old;
    }

    public static bool IsPrivileged(Cred cred)
    {
        return cred.Euid == 0;
    }

    public static bool IsPrivileged(Process process)
    {
        return process.EUID == 0;
    }

    public static bool IsInGroup(Process process, int gid)
    {
        if (gid == process.FSGID || gid == process.EGID || gid == process.GID || gid == process.SGID) return true;
        return process.SupplementaryGroups.Contains(gid);
    }

    private static bool MatchesAnyUid(Cred cred, int uid)
    {
        return uid == cred.Ruid || uid == cred.Euid || uid == cred.Suid;
    }

    private static bool MatchesAnyGid(Cred cred, int gid)
    {
        return gid == cred.Rgid || gid == cred.Egid || gid == cred.Sgid;
    }

    public static void ApplyExecSetIdOnExec(Process process, Inode executable, bool allowPrivilegeGain = true)
    {
        var newEuid = process.EUID;
        var newEgid = process.EGID;

        if (allowPrivilegeGain && executable.Type == InodeType.File)
        {
            if ((executable.Mode & 0x800) != 0) newEuid = executable.Uid; // S_ISUID
            if ((executable.Mode & 0x400) != 0) newEgid = executable.Gid; // S_ISGID
        }

        process.EUID = newEuid;
        process.EGID = newEgid;

        // Linux exec semantics: saved IDs become effective IDs after exec transitions.
        process.SUID = newEuid;
        process.SGID = newEgid;
        process.FSUID = newEuid;
        process.FSGID = newEgid;
    }
}
