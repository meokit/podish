using Fiberish.Auth.Cred;
using Fiberish.Core;
using Fiberish.Native;
using Fiberish.VFS;
using Xunit;

namespace Fiberish.Tests.Auth;

public class CredentialServiceTests
{
    [Fact]
    public void SetUid_Root_Success()
    {
        var p = new TestProcess(0, 0);
        Assert.Equal(0, CredentialService.SetUid(p, 1000));
        Assert.Equal(1000, p.UID);
        Assert.Equal(1000, p.EUID);
        Assert.Equal(1000, p.SUID);
        Assert.Equal(1000, p.FSUID);
    }

    [Fact]
    public void SetUid_NonRoot_Success_IfSelf()
    {
        var p = new TestProcess(1000, 1000);
        // Can set EUID to RUID (1000)
        Assert.Equal(0, CredentialService.SetUid(p, 1000));
        Assert.Equal(1000, p.EUID);
    }

    [Fact]
    public void SetUid_NonRoot_Fail_IfOther()
    {
        var p = new TestProcess(1000, 1000);
        Assert.Equal(-(int)Errno.EPERM, CredentialService.SetUid(p, 2000));
    }

    [Fact]
    public void SetReUid_Root_Success()
    {
        var p = new TestProcess(0, 0);
        Assert.Equal(0, CredentialService.SetReUid(p, 1000, 2000));
        Assert.Equal(1000, p.UID);
        Assert.Equal(2000, p.EUID);
        // SUID is set to new EUID because RUID changed or privileged
        Assert.Equal(2000, p.SUID);
    }

    [Fact]
    public void SetReUid_NonRoot_Swap()
    {
        var p = new TestProcess(1000, 1000);
        p.EUID = 2000; // Simulate previous setreuid

        // Swap RUID and EUID
        Assert.Equal(0, CredentialService.SetReUid(p, 2000, 1000));
        Assert.Equal(2000, p.UID);
        Assert.Equal(1000, p.EUID);
    }

    [Fact]
    public void SetResUid_Root_Success()
    {
        var p = new TestProcess(0, 0);
        Assert.Equal(0, CredentialService.SetResUid(p, 1000, 2000, 3000));
        Assert.Equal(1000, p.UID);
        Assert.Equal(2000, p.EUID);
        Assert.Equal(3000, p.SUID);
        Assert.Equal(2000, p.FSUID);
    }

    [Fact]
    public void SetGroups_Root_Success()
    {
        var p = new TestProcess(0, 0);
        int[] groups = { 10, 20, 30 };
        Assert.Equal(0, CredentialService.SetGroups(p, groups));
        Assert.Equal(groups, p.SupplementaryGroups);
    }

    [Fact]
    public void SetGroups_NonRoot_Fail()
    {
        var p = new TestProcess(1000, 1000);
        int[] groups = { 10, 20 };
        Assert.Equal(-(int)Errno.EPERM, CredentialService.SetGroups(p, groups));
    }

    [Fact]
    public void ApplyExecSetId_SetUidBit()
    {
        var p = new TestProcess(1000, 1000);
        // Mode 04755 (S_ISUID | rwxr-xr-x) owned by root (0)
        var inode = new TestInode(0, 0, 0x9ED);

        CredentialService.ApplyExecSetIdOnExec(p, inode);

        Assert.Equal(1000, p.UID); // RUID unchanged
        Assert.Equal(0, p.EUID); // EUID becomes owner (0)
        Assert.Equal(0, p.SUID); // SUID becomes new EUID
        Assert.Equal(0, p.FSUID);
    }

    [Fact]
    public void ApplyExecSetId_NoSetUidBit()
    {
        var p = new TestProcess(1000, 1000);
        // Mode 0755
        var inode = new TestInode(0, 0, 0x1ED);

        CredentialService.ApplyExecSetIdOnExec(p, inode);

        Assert.Equal(1000, p.EUID);
    }

    private class TestProcess : Process
    {
        public TestProcess(int uid, int gid) : base(1, null!, null!)
        {
            UID = uid;
            GID = gid;
            EUID = uid;
            EGID = gid;
            SUID = uid;
            SGID = gid;
            FSUID = uid;
            FSGID = gid;
        }
    }

    private class TestInode : Inode
    {
        public TestInode(int uid, int gid, int mode)
        {
            Uid = uid;
            Gid = gid;
            Mode = mode;
            Type = InodeType.File;
        }

        public override Dentry? Lookup(string name)
        {
            return null;
        }
    }
}