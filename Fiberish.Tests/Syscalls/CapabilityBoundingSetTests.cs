using System.Buffers.Binary;
using Fiberish.Auth.Cred;
using Fiberish.Core;
using Fiberish.Native;
using Fiberish.VFS;
using Xunit;

namespace Fiberish.Tests.Syscalls;

public class CapabilityBoundingSetTests
{
    [Fact]
    public void Drop_PreservesCurrentSets_ButPreventsSetIdExecRegainingCapability()
    {
        using var env = new SyscallTestEnv();
        env.Process.SetCapability(Process.CapabilitySetPcap, true, true, false);
        Assert.Equal(1, env.Invoke("SysPrctl", LinuxConstants.PR_CAPBSET_READ, Process.CapabilitySysAdmin));
        Assert.Equal(0, env.Invoke("SysPrctl", LinuxConstants.PR_CAPBSET_DROP, Process.CapabilitySysAdmin));
        Assert.Equal(0, env.Invoke("SysPrctl", LinuxConstants.PR_CAPBSET_READ, Process.CapabilitySysAdmin));
        Assert.True(env.Process.HasEffectiveCapability(Process.CapabilitySysAdmin));
        Assert.Equal(0, SetCaps(env, 0, 0, 0));

        var executable = env.Sb.AllocInode();
        executable.Type = InodeType.File;
        executable.Mode = 0x9ED;
        executable.Uid = 0;
        CredentialService.ApplyExecSetIdOnExec(env.Process, executable);

        Assert.False(env.Process.HasEffectiveCapability(Process.CapabilitySysAdmin));
        Assert.All(env.Process.CapPermitted, word => Assert.Equal(0U, word));
        Assert.Equal(0, env.Invoke("SysPrctl", LinuxConstants.PR_CAPBSET_READ, Process.CapabilitySysAdmin));
    }

    [Fact]
    public void Fork_InheritsIndependentBoundingSet()
    {
        using var env = new SyscallTestEnv();
        env.Process.SetCapability(Process.CapabilitySetPcap, true, true, false);
        Assert.Equal(0, env.Invoke("SysPrctl", LinuxConstants.PR_CAPBSET_DROP, Process.CapabilitySysAdmin));
        var pid = env.Invoke("SysFork");
        Assert.True(pid > 0);
        var child = env.Scheduler.GetProcess(pid)!;
        Assert.NotNull(child);
        Assert.False(child.HasBoundingCapability(Process.CapabilitySysAdmin));
        child.DropBoundingCapability(Process.CapabilityKill);
        Assert.True(env.Process.HasBoundingCapability(Process.CapabilityKill));
        env.Scheduler.CleanupDeadProcess(child);
        child.Syscalls.Close();
    }

    [Fact]
    public void Drop_RequiresCapability_RejectsInvalidNumbers_AndIsIdempotent()
    {
        using var env = new SyscallTestEnv();
        Assert.Equal(-(int)Errno.EPERM,
            env.Invoke("SysPrctl", LinuxConstants.PR_CAPBSET_DROP, Process.CapabilitySysAdmin));
        Assert.Equal(1, env.Invoke("SysPrctl", LinuxConstants.PR_CAPBSET_READ, Process.CapabilitySysAdmin));
        env.Process.SetCapability(Process.CapabilitySetPcap, true, true, false);
        foreach (var cap in new uint[] { Process.CapabilityLast + 1, 63, uint.MaxValue })
        {
            Assert.Equal(-(int)Errno.EINVAL, env.Invoke("SysPrctl", LinuxConstants.PR_CAPBSET_READ, cap));
            Assert.Equal(-(int)Errno.EINVAL, env.Invoke("SysPrctl", LinuxConstants.PR_CAPBSET_DROP, cap));
        }
        for (var i = 0; i < 2; i++)
            Assert.Equal(0, env.Invoke("SysPrctl", LinuxConstants.PR_CAPBSET_DROP, Process.CapabilityLast));
        Assert.Equal(0, env.Invoke("SysPrctl", LinuxConstants.PR_CAPBSET_READ, Process.CapabilityLast));
    }

    [Fact]
    public void Capset_CannotRestoreDroppedPermittedBits_OrBypassBoundingSetViaInheritable()
    {
        using var env = new SyscallTestEnv();
        const uint admin = 1U << Process.CapabilitySysAdmin;
        const uint setPcap = 1U << Process.CapabilitySetPcap;
        env.Process.SetCapability(Process.CapabilitySetPcap, true, true, false);
        Assert.Equal(0, env.Invoke("SysPrctl", LinuxConstants.PR_CAPBSET_DROP, Process.CapabilitySysAdmin));
        Assert.Equal(-(int)Errno.EPERM, SetCaps(env, setPcap, setPcap, admin));
        Assert.True(env.Process.HasEffectiveCapability(Process.CapabilitySysAdmin)); // rejected atomically
        Assert.Equal(0, SetCaps(env, setPcap, setPcap, 0));
        Assert.Equal(-(int)Errno.EPERM, SetCaps(env, admin | setPcap, admin | setPcap, 0));
        Assert.False(env.Process.HasEffectiveCapability(Process.CapabilitySysAdmin));
        Assert.Equal(-(int)Errno.EPERM, SetCaps(env, admin | setPcap, setPcap, 0));
    }

    private static int SetCaps(SyscallTestEnv env, uint effective, uint permitted, uint inheritable)
    {
        Span<byte> header = stackalloc byte[8];
        header.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(header, 0x20080522);
        Span<byte> data = stackalloc byte[24];
        data.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(data, effective);
        BinaryPrimitives.WriteUInt32LittleEndian(data[4..], permitted);
        BinaryPrimitives.WriteUInt32LittleEndian(data[8..], inheritable);
        Assert.True(env.Engine.CopyToUser(0x10000, header));
        Assert.True(env.Engine.CopyToUser(0x11000, data));
        return env.Invoke("SysCapset", 0x10000, 0x11000);
    }
}
