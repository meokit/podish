using Fiberish.Core;
using Fiberish.Native;
using Fiberish.VFS;

namespace Fiberish.Auth.Cred;

public static class CredentialService
{
    public readonly record struct ExecIdentity(int Uid, int Euid, int Gid, int Egid, bool Secure);

    public static ExecIdentity ComputeExecIdentity(Process process, Inode executable,
        bool allowPrivilegeGain = true)
    {
        var newEuid = process.EUID;
        var newEgid = process.EGID;
        var secure = false;

        // PR_SET_NO_NEW_PRIVS: execve must not grant privileges, so setuid/setgid bits
        // (and, in Linux, file capabilities) are ignored for the transition.
        if (allowPrivilegeGain && !process.NoNewPrivs && executable.Type == InodeType.File)
        {
            if ((executable.Mode & 0x800) != 0)
                newEuid = executable.Uid; // S_ISUID
            if ((executable.Mode & 0x400) != 0)
                newEgid = executable.Gid; // S_ISGID

            secure = newEuid != process.EUID || newEgid != process.EGID;
        }

        return new ExecIdentity(process.UID, newEuid, process.GID, newEgid, secure);
    }
    public static void InitializeCredentials(Process process, int uid, int gid, IReadOnlyList<int>? supplementaryGroups = null)
    {
        var cred = new Cred
        {
            Ruid = uid,
            Euid = uid,
            Suid = uid,
            Fsuid = uid,
            Rgid = gid,
            Egid = gid,
            Sgid = gid,
            Fsgid = gid,
            Groups = new GroupInfo(supplementaryGroups),
            Umask = process.Umask
        };
        Commit(process, cred);
    }

    public static void CopyCredentials(Process source, Process destination)
    {
        destination.UID = source.UID;
        destination.EUID = source.EUID;
        destination.SUID = source.SUID;
        destination.FSUID = source.FSUID;

        destination.GID = source.GID;
        destination.EGID = source.EGID;
        destination.SGID = source.SGID;
        destination.FSGID = source.FSGID;

        destination.SupplementaryGroups.Clear();
        destination.SupplementaryGroups.AddRange(source.SupplementaryGroups);
        destination.Umask = source.Umask;

        Array.Copy(source.CapEffective, destination.CapEffective, destination.CapEffective.Length);
        Array.Copy(source.CapPermitted, destination.CapPermitted, destination.CapPermitted.Length);
        Array.Copy(source.CapInheritable, destination.CapInheritable, destination.CapInheritable.Length);
        Array.Copy(source.CapBounding, destination.CapBounding, destination.CapBounding.Length);
        // Do not normalize here: a root process that deliberately dropped its
        // capabilities must not silently regain CAP_SYS_ADMIN in a forked child.
    }

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
        NormalizeCapabilityState(process);
    }

    public static void NormalizeCapabilityState(Process process)
    {
        Array.Clear(process.CapEffective, 0, process.CapEffective.Length);
        Array.Clear(process.CapPermitted, 0, process.CapPermitted.Length);
        Array.Clear(process.CapInheritable, 0, process.CapInheritable.Length);

        if (process.EUID == 0 && process.HasBoundingCapability(Process.CapabilitySysAdmin))
            process.SetCapability(Process.CapabilitySysAdmin, true, true, false);
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

        if (ruid < -1 || euid < -1) return -(int)Errno.EINVAL;
        // Validate both IDs against the original credential before mutating
        // either one: setreuid(other, old_ruid) is a legal unprivileged swap.
        if (!privileged)
        {
            if (ruid != -1 && !MatchesAnyUid(cred, ruid)) return -(int)Errno.EPERM;
            if (euid != -1 && !MatchesAnyUid(cred, euid)) return -(int)Errno.EPERM;
        }

        if (ruid != -1) cred.Ruid = ruid;
        if (euid != -1)
        {
            cred.Euid = euid;
            cred.Fsuid = euid;
        }

        // setreuid(2): the saved UID follows the new effective UID only when
        // the real UID changes or the effective UID changes away from the old
        // real UID. Privilege alone must not overwrite it.
        if ((ruid != -1 && ruid != oldRuid) || (euid != -1 && euid != oldRuid))
            cred.Suid = cred.Euid;

        Commit(process, cred);
        return 0;
    }

    public static int SetReGid(Process process, int rgid, int egid)
    {
        var cred = Get(process);
        var privileged = IsPrivileged(cred);
        var oldRgid = cred.Rgid;

        if (rgid < -1 || egid < -1) return -(int)Errno.EINVAL;
        if (!privileged)
        {
            if (rgid != -1 && !MatchesAnyGid(cred, rgid)) return -(int)Errno.EPERM;
            if (egid != -1 && !MatchesAnyGid(cred, egid)) return -(int)Errno.EPERM;
        }

        if (rgid != -1) cred.Rgid = rgid;
        if (egid != -1)
        {
            cred.Egid = egid;
            cred.Fsgid = egid;
        }

        if ((rgid != -1 && rgid != oldRgid) || (egid != -1 && egid != oldRgid))
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
        var identity = ComputeExecIdentity(process, executable, allowPrivilegeGain);

        process.EUID = identity.Euid;
        process.EGID = identity.Egid;

        // Linux exec semantics: saved IDs become effective IDs after exec transitions.
        process.SUID = identity.Euid;
        process.SGID = identity.Egid;
        process.FSUID = identity.Euid;
        process.FSGID = identity.Egid;

        // A plain exec must preserve the capability sets. Only a setid transition
        // recomputes them from the new credentials; NoNewPrivs never gains anything.
        if (identity.Secure)
            NormalizeCapabilityState(process);
    }
}
